using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // One worker and generation serve the selected wand mode. Football retains
    // its own player profiles; learning a wand never changes either player.
    private readonly Dictionary<string, BlackTipProfile> _wandMarkerProfiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wandMarkerModes = new(StringComparer.Ordinal);
    private readonly FootballMarkerTracker _wandMarkerTracker = new();
    private bool _wandMarkerLearning, _wandMarkerLearningBusy;
    private WandMarkerPreview? _wandMarkerPreview;
    private (int Width, int Height)? _wandMarkerFrameSize;
    private string? _wandMarkerSettingsError;
    private sealed record WandMarkerSettings(int Version, Dictionary<string, BlackTipProfile> Cameras,
        Dictionary<string, string> ActiveModes);
    private sealed record WandMarkerPreview(CameraFrame Frame, MarkerTipDetectionResult Detection,
        MarkerTipTrackResult Track, DateTimeOffset ObservedAt, double InferenceMilliseconds)
    {
        public CameraFrameLease FrameLease { get; } = Frame.Retain();
        public BlackTipSearchHint? SearchHint { get; init; }
    }

    private void SetWandMarkerPreviewLocked(WandMarkerPreview? preview)
    {
        var previous = _wandMarkerPreview;
        _wandMarkerPreview = preview;
        if (preview is not null) _wandMarkerFrameSize = (preview.Frame.Width, preview.Frame.Height);
        previous?.FrameLease.Dispose();
    }

    private string WandMarkerSettingsPath => Path.Combine(_appDataDirectory, "wand-marker.json");

    private void InitializeWandMarkerTracking()
    {
        try
        {
            if (!File.Exists(WandMarkerSettingsPath)) return;
            if (new FileInfo(WandMarkerSettingsPath).Length > 131072)
                throw new InvalidDataException("The saved wand-marker settings are too large.");
            var saved = JsonSerializer.Deserialize<WandMarkerSettings>(File.ReadAllText(WandMarkerSettingsPath));
            if (saved is not { Version: 1, Cameras: not null, ActiveModes: not null } ||
                saved.Cameras.Count > 64 || saved.ActiveModes.Count > 64 ||
                saved.Cameras.Any(pair => !ValidKey(pair.Key) || pair.Value is not { IsValid: true, BarCount: 2 }) ||
                saved.ActiveModes.Any(pair => !ValidKey(pair.Key) || pair.Value is not ("bar" or "eye") ||
                    !saved.Cameras.ContainsKey(pair.Key)))
                throw new InvalidDataException("The saved wand-marker settings are invalid.");
            foreach (var pair in saved.Cameras) _wandMarkerProfiles.Add(pair.Key, pair.Value);
            foreach (var pair in saved.ActiveModes) _wandMarkerModes.Add(pair.Key, pair.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _wandMarkerSettingsError = "Wand-marker settings could not be loaded: " + ex.Message; }
        static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 4096;
    }

    private BlackTipProfile? LearnedWandMarkerProfileLocked()
    {
        if (_eyeTipCameraId is not { } id) return null;
        var profile = _wandMarkerProfiles.GetValueOrDefault(id);
        // Paint uses the double bars even when Water Garden's optional eye
        // marker is selected. The saved camera profile is shared unchanged.
        return _scene.CurrentBoardScreen == BoardScreen.Paint
            ? WandMarkerProfileSelection.PaintBarProfile(profile)
            : WandMarkerProfileSelection.BarProfile(_wandMarkerModes.GetValueOrDefault(id), profile);
    }
    private BlackTipProfile? ActiveWandMarkerProfileLocked() => _eyeTipLearning ? null : LearnedWandMarkerProfileLocked();
    private bool WandMarkerModeActiveLocked() => _scene.CurrentBoardScreen == BoardScreen.Paint || _wandMarkerLearning || !_eyeTipLearning &&
        (LearnedWandMarkerProfileLocked() is not null || LearnedEyeTipRadiusLocked() is null);

    private bool WandMarkerBoardSelected => _scene.CurrentBoardScreen is BoardScreen.WaterGarden or BoardScreen.Paint;
    private long CurrentWandMarkerInputRevision => _scene.CurrentBoardScreen == BoardScreen.Paint
        ? _scene.PaintMarkerInputRevision : _scene.WaterMarkerInputRevision;

    private void ResetWandMarkerTrackingLocked()
    {
        _wandMarkerLearning = false;
        SetWandMarkerPreviewLocked(null);
        _wandMarkerTracker.Reset();
        _wandMarkerFrameSize = null;
    }

    private bool SelectEyeTipModeLocked()
    {
        ResetWandMarkerTrackingLocked();
        if (_eyeTipCameraId is not { } id || !_wandMarkerProfiles.ContainsKey(id) ||
            _wandMarkerModes.GetValueOrDefault(id) == "eye") return false;
        _wandMarkerModes[id] = "eye";
        return true;
    }

    private void ForgetWandMarkerForCameraLocked()
    {
        ResetWandMarkerTrackingLocked();
        if (_eyeTipCameraId is not { } id) return;
        _wandMarkerProfiles.Remove(id);
        _wandMarkerModes.Remove(id);
    }

    private void SaveWandMarkerSettings()
    {
        try
        {
            WandMarkerSettings saved;
            lock (_eyeTipGate) saved = new(1, new(_wandMarkerProfiles, StringComparer.Ordinal),
                new(_wandMarkerModes, StringComparer.Ordinal));
            Directory.CreateDirectory(_appDataDirectory);
            string temporary = WandMarkerSettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, WandMarkerSettingsPath, overwrite: true);
            _wandMarkerSettingsError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _wandMarkerSettingsError = "Wand-marker settings could not be saved: " + ex.Message; }
    }

    private bool TryLearnWandMarkerFromPreview(CameraFrame frame, PixelPoint point)
    {
        lock (_eyeTipGate)
        {
            if (!_wandMarkerLearning) return false;
            if (_wandMarkerLearningBusy) return true;
        }
        _ = LearnWandMarkerFromPreviewAsync(frame, point);
        return true;
    }

    private async Task LearnWandMarkerFromPreviewAsync(CameraFrame frame, PixelPoint point)
    {
        try { await LearnWandMarkerAsync(point.X, point.Y, frame); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) SetStatus("Wand marker could not be learned: " + ex.Message); }
    }

    private async Task<object> LearnWandMarkerAsync(double x, double y, CameraFrame? selectedFrame = null)
    {
        using var frameLease = selectedFrame is null ? AcquireLatestCameraFrame() : selectedFrame.Retain();
        var frame = frameLease?.Frame;
        var cameraId = _camera.ActiveDeviceId;
        if (frame is null || !_cameraWanted || !_camera.IsRunning || _cameraHealthWarning ||
            cameraId != _selectedCameraId || cameraId is null || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
            throw new InvalidOperationException("Start the selected webcam and wait for a fresh camera frame.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new ArgumentException("Provide x and y inside the raw camera frame.");
        lock (_eyeTipGate)
        {
            if (_wandMarkerLearningBusy) throw new InvalidOperationException("Wait for the current learning attempt to finish.");
            _wandMarkerLearningBusy = true;
        }
        CancelFootballLearning();
        SetStickTrackingEnabled(true);
        _frozenFrame = null;
        long generation;
        lock (_eyeTipGate)
        {
            _wandMarkerLearning = true;
            _eyeTipReason = "learning-wand-marker";
            generation = _eyeTipGeneration;
            PublishStickTipLocked();
        }
        UpdateEyeTipStatus();
        try
        {
            long started = Stopwatch.GetTimestamp();
            var learned = await Task.Run(async () =>
            {
                await _eyeTipDetectorGate.WaitAsync();
                try
                {
                    var references = StickTipProjectionReference(frame, learning: true);
                    var profile = BlackTipDetector.Learn(frame.Width, frame.Height, frame.Stride, frame.Bgra, new(x, y),
                        new(ProjectionFrames: references, FrameTime: frame.Timestamp));
                    if (profile.BarCount != 2)
                        throw new InvalidOperationException("Keep both parallel black bars visible, then click inside either bar.");
                    var detection = BlackTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra, profile,
                        new(PreferredCenter: new(x, y), ProjectionFrames: references, FrameTime: frame.Timestamp));
                    return (Profile: profile, Detection: detection);
                }
                finally { _eyeTipDetectorGate.Release(); }
            });
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_eyeTipGate)
            {
                if (WandMarkerLearningSuperseded(generation, cameraId)) throw new OperationCanceledException();
                if (!EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
                    throw new InvalidOperationException("The learning frame expired. Click the marker again.");
                var chosen = NearestStickTipCandidate(learned.Detection.Candidates, x, y,
                    item => item.Center, item => item.RadiusPixels);
                if (chosen is null)
                    throw new InvalidOperationException("Click inside one clearly visible black bar with both bars facing the webcam.");
                _wandMarkerProfiles[cameraId] = learned.Profile;
                _wandMarkerModes[cameraId] = "bar";
                _eyeTipGeneration++;
                SetEyeTipPreviewLocked(null);
                _eyeTipTracker.Reset();
                _wandMarkerTracker.Acquire(chosen, frame.Timestamp);
                SetWandMarkerPreviewLocked(new(frame, learned.Detection, new(chosen, false, "confirming"), frame.Timestamp, elapsed));
                _wandMarkerLearning = false;
                _eyeTipReason = "Confirming the black marker.";
                PublishStickTipLocked();
            }
            SaveWandMarkerSettings();
            return GetStickTipStatus();
        }
        catch (Exception) when (WandMarkerLearningSuperseded(generation, cameraId))
        { throw new OperationCanceledException("Wand learning was cancelled or superseded."); }
        finally
        {
            lock (_eyeTipGate)
            {
                _wandMarkerLearningBusy = false;
                if (!_closing && generation == _eyeTipGeneration) ResetEyeTipTracking("wand-learning-failed");
            }
            if (!_closing) { UpdateEyeTipStatus(); CameraCanvas.Invalidate(); }
        }
    }

    private bool WandMarkerLearningSuperseded(long generation, string cameraId)
    {
        lock (_eyeTipGate) return _closing || generation != _eyeTipGeneration ||
            cameraId != _eyeTipCameraId || cameraId != _camera.ActiveDeviceId ||
            _scene.CurrentBoardScreen != _eyeTipSceneBoard || CurrentWandMarkerInputRevision != _eyeTipSceneRevision;
    }

    private bool WandMarkerFreshLocked(DateTimeOffset now) =>
        _wandMarkerPreview is { Track: { Confirmed: true, Observation: not null } } preview &&
        preview.ObservedAt <= now + TimeSpan.FromMilliseconds(30) && now - preview.ObservedAt <= EyeTipLifetime;

    private string WandMarkerStatusTextLocked()
    {
        string message = !_eyeTipEnabled ? "Off. Learn the Football-style double-bar wand marker before tracking." :
            !_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _eyeTipCameraId ? "Start the selected webcam to track the wand." :
            _wandMarkerLearning ? "Click inside either black bar in the live preview. Keep both bars facing the webcam, close to the board." :
            LearnedWandMarkerProfileLocked() is null ? "Use Marker setup to train this camera for the wand." :
            !WandMarkerBoardSelected ? "Wand marker saved. Tracking runs in Water Garden and Paint." :
            WandMarkerFreshLocked(MonotonicClock.UtcNow) ? _scene.CurrentBoardScreen == BoardScreen.Paint
                ? "Double-bar wand tracked. Move it across the board to leave an ink trail."
                : "Double-bar wand tracked. Move its centre over the water to make ripples." :
                _eyeTipReason;
        return _wandMarkerSettingsError is null ? message : message + " " + _wandMarkerSettingsError;
    }

    private object GetWandMarkerStatusLocked()
    {
        var now = MonotonicClock.UtcNow;
        var preview = _wandMarkerPreview;
        var profile = LearnedWandMarkerProfileLocked();
        bool cameraReady = _cameraWanted && _camera.IsRunning && !_cameraHealthWarning && _camera.ActiveDeviceId == _eyeTipCameraId;
        bool running = _eyeTipEnabled && cameraReady && WandMarkerBoardSelected && profile is not null;
        bool confirmed = running && !_wandMarkerLearning && WandMarkerFreshLocked(now);
        bool previewFresh = preview is not null && EyeTipFrameFresh(preview.Frame, now);
        return new
        {
            mode = "bar", enabled = _eyeTipEnabled, learning = _wandMarkerLearning, learned = profile is not null,
            running, cameraDeviceId = _eyeTipCameraId, profile, confirmed,
            tip = confirmed ? preview!.Track.Observation : null,
            cameraX = confirmed ? preview!.Track.Observation?.Center.X : null,
            cameraY = confirmed ? preview!.Track.Observation?.Center.Y : null,
            candidates = previewFresh ? preview!.Detection.Candidates : [],
            detectionSource = previewFresh ? preview!.Detection.Source : null,
            supportingBarCount = previewFresh ? preview!.Detection.SupportingBars.Count : 0,
            ambiguousMarkerCount = previewFresh ? preview!.Detection.AmbiguousMarkerCenters.Count : 0,
            sourceFrameUtc = preview?.Frame.Timestamp,
            observedAtUtc = preview?.ObservedAt is { } measuredAt && measuredAt != default ? measuredAt : (DateTimeOffset?)null,
            frameWidth = preview?.Frame.Width, frameHeight = preview?.Frame.Height,
            inferenceMilliseconds = preview?.InferenceMilliseconds,
            resultAgeMilliseconds = preview is null || preview.ObservedAt == default
                ? (double?)null : (now - preview.ObservedAt).TotalMilliseconds,
            reason = _eyeTipReason, status = WandMarkerStatusTextLocked(),
            settingsPath = WandMarkerSettingsPath, settingsError = _wandMarkerSettingsError
        };
    }

    private void DrawWandMarkerPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        lock (_eyeTipGate)
        {
            if (!_eyeTipEnabled || _frozenFrame is not null || _wandMarkerPreview is not { } preview ||
                preview.Frame.Width != frame.Width || preview.Frame.Height != frame.Height ||
                !EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow)) return;
            DrawWandMarkerMarkers(ds, preview, rect, _wandMarkerLearning);
        }
    }

    private static void DrawWandMarkerMarkers(CanvasDrawingSession ds, WandMarkerPreview preview, Rect rect, bool candidates) =>
        DrawStickTipMarkers(ds, preview.Frame, rect,
            candidates ? preview.Detection.Candidates.Select(tip => (tip.Center, tip.RadiusPixels)) : [],
            preview.Track.Observation is { } tip && MonotonicClock.UtcNow - preview.ObservedAt <= EyeTipLifetime
                ? (tip.Center, tip.RadiusPixels) : null, preview.Track.Confirmed);
}
