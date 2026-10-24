using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    /// <summary>Immutable raw-camera-to-normalized-board mapping for one active input epoch.
    /// Camera workers must return this epoch with their result.</summary>
    public sealed record DuckHuntInputContext(long Epoch, IReadOnlyList<double> CameraToBoard,
        DateTimeOffset NotBefore);

    private readonly record struct DuckHuntInputKey(long Navigation, long Round, Homography? Camera,
        Homography? Surface, DateTimeOffset InputReset, bool Active);
    private readonly DuckHuntMeasurementTracker _duckHuntMeasurement = new();
    private DuckHuntInputKey? _duckHuntInputKey;
    private DuckHuntInputContext? _duckHuntInputContext;
    private long _duckHuntInputEpoch;
    private bool _duckHuntAudioReady;
    private bool _duckHuntLearningActive;
    private string _duckHuntAudioStatus = "Starting microphone…";
    private string _duckHuntRenderedHint = string.Empty;
    private BoardAim? _duckHuntRenderedLiveDot;
    private long _duckHuntInputVisualRevision;

    private void UpdateDuckHuntRound(DateTimeOffset now)
    {
        bool ready = SyncDuckHuntInput(MonotonicClock.UtcNow) is not null &&
            _duckHuntAudioReady && DuckHuntArtworkReady;
        _boardSession.SetDuckHuntReady(ready, now, PaintBoardAspect());
        _boardSession.TickDuckHunt(now);
    }

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

    public DuckHuntMeasurementSnapshot DuckHuntMeasurement
    {
        get
        {
            lock (_gate)
            {
                var now = MonotonicClock.UtcNow;
                SyncDuckHuntInput(now);
                return _duckHuntMeasurement.GetSnapshot(now);
            }
        }
    }

    public bool DuckHuntInputReady
    {
        get { lock (_gate) return SyncDuckHuntInput(MonotonicClock.UtcNow) is not null; }
    }

    // Both play modes listen for shots. Their microphone lifetime follows
    // calibration, independent of the click trainer's temporary shot pause.
    public bool DuckHuntBoardReady
    {
        get { lock (_gate) return _boardSession.Screen == BoardScreen.DuckHunt &&
            _boardSession.CurrentDuckHuntView is DuckHuntView.TargetPractice or DuckHuntView.Hunt &&
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
            if (context is null || context.Epoch != epoch || frameTimestamp <= context.NotBefore) return;
            if (!_duckHuntMeasurement.Observe(aim, frameTimestamp, now)) return;
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
            UpdateDuckHuntRound(_blackjackClock());
            if (_boardSession.CurrentDuckHuntView == DuckHuntView.TargetPractice &&
                (_boardSession.DuckHuntState.Phase == DuckHuntPhase.Ready ||
                 _boardSession.DuckHuntState.Paused || _boardSession.DuckHuntState.Phase == DuckHuntPhase.PlayerWon)) return false;
            if (_boardSession.CurrentDuckHuntView == DuckHuntView.Hunt &&
                (!DuckHuntArtworkReady || _boardSession.HuntFlightState.Paused)) return false;
            // The current photo decides the hit at its own timestamp, even if
            // rendering has already advanced. Each valid shot replaces the
            // previous Target Practice dot/circle; Hunt uses the same fresh-photo
            // gate for its START target and moving birds without drawing those marks.
            if (!_duckHuntMeasurement.Capture(aim, frameTimestamp, clickOnset, confirmedAt, now)) return false;
            _boardSession.TryDuckHuntShot(aim, frameTimestamp, PaintBoardAspect());
            _duckHuntInputVisualRevision++;
            return true;
        }
    }

    public void SetDuckHuntAudioStatus(bool ready, string status)
    {
        lock (_gate)
        {
            var now = MonotonicClock.UtcNow;
            SyncDuckHuntInput(now);
            if (_duckHuntAudioReady && !ready)
            {
                _duckHuntMeasurement.Reset(now);
                _duckHuntInputVisualRevision++;
            }
            _duckHuntAudioReady = ready;
            // Keep platform/driver exception details in the main app's status. The projected
            // line must stay readable at normal board scale instead of shrinking to fit them.
            _duckHuntAudioStatus = ready ? "Microphone ready" :
                status?.StartsWith("Starting", StringComparison.OrdinalIgnoreCase) == true ? "Starting microphone…" :
                status?.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase) == true ? "Waiting for camera and board setup" :
                "Microphone unavailable · choose one in the main app";
            RefreshDuckHuntInputHint(now);
        }
    }

    private void RefreshDuckHuntInputHint(DateTimeOffset now)
    {
        var context = SyncDuckHuntInput(now);
        var live = _duckHuntMeasurement.GetSnapshot(now).LiveDetectedXY;
        if (_duckHuntRenderedLiveDot != live)
        {
            _duckHuntRenderedLiveDot = live;
            _duckHuntInputVisualRevision++;
        }
        string hint = _duckHuntLearningActive ? "Click learning · round paused" :
            context is null ? "Waiting for camera and board calibration" :
            !_duckHuntAudioReady ? _duckHuntAudioStatus :
            live.HasValue
                ? "Red dot detected · click to shoot" : "Show the red dot on the board";
        if (_duckHuntRenderedHint == hint) return;
        _duckHuntRenderedHint = hint;
        _duckHuntInputVisualRevision++;
    }

    private DuckHuntInputContext? SyncDuckHuntInput(DateTimeOffset now)
    {
        bool active = _boardSession.Screen == BoardScreen.DuckHunt &&
            _boardSession.CurrentDuckHuntView is DuckHuntView.TargetPractice or DuckHuntView.Hunt && MappedBoardInputReady &&
            !_cameraCheckRequired && !_duckHuntLearningActive;
        var key = new DuckHuntInputKey(_boardSession.NavigationRevision, _boardSession.DuckHuntRoundRevision, _boardCameraMap,
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
        _duckHuntMeasurement.Reset(now);
        _duckHuntInputVisualRevision++;
    }
}
