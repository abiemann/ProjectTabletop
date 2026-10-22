using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    /// <summary>Immutable raw-camera-to-normalized-board mapping for one active input epoch.
    /// Camera workers must return this epoch with their result.</summary>
    public sealed record DuckHuntInputContext(long Epoch, IReadOnlyList<double> CameraToBoard,
        DateTimeOffset NotBefore);

    private readonly record struct DuckHuntInputKey(long Navigation, Homography? Camera,
        Homography? Surface, DateTimeOffset InputReset, bool Active);
    private readonly DuckHuntAimGate _duckHuntAimGate = new();
    private DuckHuntInputKey? _duckHuntInputKey;
    private DuckHuntInputContext? _duckHuntInputContext;
    private long _duckHuntInputEpoch;
    private DateTimeOffset _duckHuntDotFrameTime;
    private bool _duckHuntDotDetected;
    private bool _duckHuntAudioReady;
    private bool _duckHuntLearningActive;
    private string _duckHuntAudioStatus = "Starting microphone…";
    private string _duckHuntRenderedHint = string.Empty;
    private long _duckHuntInputVisualRevision;

    // Read while constructing the rendered-board cache key so microphone/learning
    // messages and the expiring dot hint refresh even while the duck is stationary.
    internal long DuckHuntInputVisualRevision
    {
        get
        {
            lock (_gate)
            {
                RefreshDuckHuntInputHint(MonotonicClock.UtcNow);
                return _duckHuntInputVisualRevision;
            }
        }
    }

    public DuckHuntInputContext? GetDuckHuntInputContext()
    {
        lock (_gate) return SyncDuckHuntInput(MonotonicClock.UtcNow);
    }

    public bool DuckHuntInputReady
    {
        get { lock (_gate) return SyncDuckHuntInput(MonotonicClock.UtcNow) is not null; }
    }

    // Choosing an input pauses shots, but does not unnecessarily reopen the
    // current microphone. Its lifetime follows the calibrated board itself.
    public bool DuckHuntBoardReady
    {
        get { lock (_gate) return _boardSession.Screen == BoardScreen.DuckHunt &&
            MappedBoardInputReady && !_cameraCheckRequired; }
    }

    public void SetDuckHuntLearningActive(bool active)
    {
        lock (_gate)
        {
            if (_duckHuntLearningActive == active) return;
            _duckHuntLearningActive = active;
            RefreshDuckHuntInputHint(MonotonicClock.UtcNow);
        }
    }

    public void ClearDuckHuntInput()
    {
        lock (_gate)
        {
            _duckHuntInputKey = null;
            ResetDuckHuntObservations(MonotonicClock.UtcNow);
        }
    }

    public void PublishDuckHuntDot(BoardAim? aim, DateTimeOffset frameTimestamp, long epoch)
    {
        lock (_gate)
        {
            var now = MonotonicClock.UtcNow;
            var context = SyncDuckHuntInput(now);
            if (context is null || context.Epoch != epoch || frameTimestamp <= context.NotBefore ||
                frameTimestamp <= _duckHuntDotFrameTime || frameTimestamp > now ||
                now - frameTimestamp > DuckHuntAimGate.Freshness) return;
            // Continuous observations are display feedback only. They never arm,
            // cancel or submit the independent click-triggered snapshot decision.
            _duckHuntDotFrameTime = frameTimestamp;
            _duckHuntDotDetected = aim is { } point && double.IsFinite(point.U) && double.IsFinite(point.V) &&
                point.U is >= 0 and <= 1 && point.V is >= 0 and <= 1;
            RefreshDuckHuntInputHint(now);
        }
    }

    public bool SubmitDuckHuntSnapshot(BoardAim? aim, DateTimeOffset frameTimestamp,
        DateTimeOffset clickOnset, DateTimeOffset confirmedAt, long epoch)
    {
        lock (_gate)
        {
            var now = MonotonicClock.UtcNow;
            var context = SyncDuckHuntInput(now);
            if (context is null || context.Epoch != epoch || !_duckHuntAudioReady) return false;
            // Each recognized sound checks one current frame. No earlier dot or
            // temporary detection gap can arm, cancel or supply this snapshot's aim.
            var shot = _duckHuntAimGate.EvaluateSnapshot(aim, frameTimestamp, clickOnset, confirmedAt, now);
            if (shot is not { } hit) return false;
            SubmitDuckHuntShot(hit.Aim, hit.ClickedAt);
            return true;
        }
    }

    public void SetDuckHuntAudioStatus(bool ready, string status)
    {
        lock (_gate)
        {
            var now = MonotonicClock.UtcNow;
            SyncDuckHuntInput(now);
            if (_duckHuntAudioReady && !ready) _duckHuntAimGate.Reset(now);
            _duckHuntAudioReady = ready;
            // Keep platform/driver exception details in the main app's status. The projected
            // line must stay readable at normal board scale instead of shrinking to fit them.
            _duckHuntAudioStatus = ready ? "Microphone ready" :
                status?.StartsWith("Starting", StringComparison.OrdinalIgnoreCase) == true ? "Starting microphone…" :
                status?.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase) == true ? "Waiting for camera and board setup" :
                "Microphone unavailable · choose MIC";
            RefreshDuckHuntInputHint(now);
        }
    }

    private string DuckHuntAudioHint
    {
        get
        {
            lock (_gate)
            {
                RefreshDuckHuntInputHint(MonotonicClock.UtcNow);
                return _duckHuntRenderedHint;
            }
        }
    }

    private void RefreshDuckHuntInputHint(DateTimeOffset now)
    {
        var context = SyncDuckHuntInput(now);
        string hint = _duckHuntLearningActive ? "Click learning · shots paused" :
            DuckHuntMicrophoneMenuOpen ? "Choose a microphone" :
            context is null ? "Waiting for camera and board calibration" :
            !_duckHuntAudioReady ? _duckHuntAudioStatus :
            _duckHuntDotDetected && _duckHuntDotFrameTime <= now && now - _duckHuntDotFrameTime <= DuckHuntAimGate.Freshness
                ? "Red dot detected · click to shoot" : "Show the red dot on the board";
        if (_duckHuntRenderedHint == hint) return;
        _duckHuntRenderedHint = hint;
        _duckHuntInputVisualRevision++;
    }

    private DuckHuntInputContext? SyncDuckHuntInput(DateTimeOffset now)
    {
        bool active = _boardSession.Screen == BoardScreen.DuckHunt && MappedBoardInputReady &&
            !_cameraCheckRequired && !DuckHuntMicrophoneMenuOpen && !_duckHuntLearningActive;
        var key = new DuckHuntInputKey(_boardSession.NavigationRevision, _boardCameraMap,
            _boardSurfaceMap, _handVisualResetThrough, active);
        if (_duckHuntInputKey == key) return _duckHuntInputContext;
        _duckHuntInputKey = key;
        ResetDuckHuntObservations(now);
        if (!active) return null;
        try
        {
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            var camera = unit.Select(point =>
                _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point))).ToArray();
            var map = Homography.FromFourPoints(camera, unit).ToMatrix();
            return _duckHuntInputContext = new(_duckHuntInputEpoch, Array.AsReadOnly(map), now);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private void ResetDuckHuntObservations(DateTimeOffset now)
    {
        _duckHuntInputEpoch++;
        _duckHuntInputContext = null;
        _duckHuntAimGate.Reset(now);
        _duckHuntDotFrameTime = now;
        _duckHuntDotDetected = false;
    }
}
