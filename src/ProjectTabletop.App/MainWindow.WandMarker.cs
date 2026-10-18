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
    // Water Garden and Paint share one double-bar marker profile per camera.
    // Football retains its own player profiles; learning this marker never changes them.
    private readonly Dictionary<string, BlackTipProfile> _wandMarkerProfiles = new(StringComparer.Ordinal);
    private readonly FootballMarkerTracker _wandMarkerTracker = new();
    private bool _wandMarkerLearning, _wandMarkerLearningBusy;
    private WandMarkerPreview? _wandMarkerPreview;
    private (int Width, int Height)? _wandMarkerFrameSize;
    private string? _wandMarkerSettingsError;
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
                throw new InvalidDataException("The saved double-bar marker settings are too large.");
            var saved = DoubleBarMarkerSettings.Parse(File.ReadAllText(WandMarkerSettingsPath));
            foreach (var pair in saved.Cameras) _wandMarkerProfiles.Add(pair.Key, pair.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _wandMarkerSettingsError = "Double-bar marker settings could not be loaded: " + ex.Message; }
    }

    private BlackTipProfile? LearnedWandMarkerProfileLocked() => _markerTrackingCameraId is { } id
        ? DoubleBarProfileSelection.Usable(_wandMarkerProfiles.GetValueOrDefault(id)) : null;

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

    private void SaveWandMarkerSettings()
    {
        try
        {
            DoubleBarMarkerSettings saved;
            lock (_markerTrackingGate) saved = new(1, new(_wandMarkerProfiles, StringComparer.Ordinal));
            Directory.CreateDirectory(_appDataDirectory);
            string temporary = WandMarkerSettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, WandMarkerSettingsPath, overwrite: true);
            _wandMarkerSettingsError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _wandMarkerSettingsError = "Double-bar marker settings could not be saved: " + ex.Message; }
    }

    private bool TryLearnWandMarkerFromPreview(CameraFrame frame, PixelPoint point)
    {
        lock (_markerTrackingGate)
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
        catch (Exception ex) { if (!_closing) SetStatus("Double-bar marker could not be learned: " + ex.Message); }
    }

    private async Task<object> LearnWandMarkerAsync(double x, double y, CameraFrame? selectedFrame = null)
    {
        using var frameLease = selectedFrame is null ? AcquireLatestCameraFrame() : selectedFrame.Retain();
        var frame = frameLease?.Frame;
        var cameraId = _camera.ActiveDeviceId;
        if (frame is null || !_cameraWanted || !_camera.IsRunning || _cameraHealthWarning ||
            cameraId != _selectedCameraId || cameraId is null || !MarkerFrameFresh(frame, MonotonicClock.UtcNow))
            throw new InvalidOperationException("Start the selected webcam and wait for a fresh camera frame.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new ArgumentException("Provide x and y inside the raw camera frame.");
        lock (_markerTrackingGate)
        {
            if (_wandMarkerLearningBusy) throw new InvalidOperationException("Wait for the current learning attempt to finish.");
            _wandMarkerLearningBusy = true;
        }
        CancelFootballLearning();
        SetStickTrackingEnabled(true);
        _frozenFrame = null;
        long generation;
        lock (_markerTrackingGate)
        {
            _wandMarkerLearning = true;
            _markerTrackingReason = "learning-double-bar-marker";
            generation = _markerTrackingGeneration;
            PublishStickTipLocked();
        }
        UpdateMarkerStatus();
        try
        {
            long started = Stopwatch.GetTimestamp();
            var learned = await Task.Run(async () =>
            {
                await _markerTrackingDetectorGate.WaitAsync();
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
                finally { _markerTrackingDetectorGate.Release(); }
            });
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_markerTrackingGate)
            {
                if (WandMarkerLearningSuperseded(generation, cameraId)) throw new OperationCanceledException();
                if (!MarkerFrameFresh(frame, MonotonicClock.UtcNow))
                    throw new InvalidOperationException("The learning frame expired. Click the marker again.");
                var chosen = NearestStickTipCandidate(learned.Detection.Candidates, x, y,
                    item => item.Center, item => item.RadiusPixels);
                if (chosen is null)
                    throw new InvalidOperationException("Click inside one clearly visible black bar with both bars facing the webcam.");
                _wandMarkerProfiles[cameraId] = learned.Profile;
                _markerTrackingGeneration++;
                _wandMarkerTracker.Acquire(chosen, frame.Timestamp);
                SetWandMarkerPreviewLocked(new(frame, learned.Detection, new(chosen, false, "confirming"), frame.Timestamp, elapsed));
                _wandMarkerLearning = false;
                _markerTrackingReason = "Confirming the black marker.";
                PublishStickTipLocked();
            }
            SaveWandMarkerSettings();
            return GetStickTipStatus();
        }
        catch (Exception) when (WandMarkerLearningSuperseded(generation, cameraId))
        { throw new OperationCanceledException("Double-bar marker learning was cancelled or superseded."); }
        finally
        {
            lock (_markerTrackingGate)
            {
                _wandMarkerLearningBusy = false;
                if (!_closing && generation == _markerTrackingGeneration) ResetMarkerTracking("double-bar-learning-failed");
            }
            if (!_closing) { UpdateMarkerStatus(); CameraCanvas.Invalidate(); }
        }
    }

    private bool WandMarkerLearningSuperseded(long generation, string cameraId)
    {
        lock (_markerTrackingGate) return _closing || generation != _markerTrackingGeneration ||
            cameraId != _markerTrackingCameraId || cameraId != _camera.ActiveDeviceId ||
            _scene.CurrentBoardScreen != _markerTrackingSceneBoard || CurrentWandMarkerInputRevision != _markerTrackingSceneRevision;
    }

    private bool WandMarkerFreshLocked(DateTimeOffset now) =>
        _wandMarkerPreview is { Track: { Confirmed: true, Observation: not null } } preview &&
        preview.ObservedAt <= now + TimeSpan.FromMilliseconds(30) && now - preview.ObservedAt <= MarkerObservationLifetime;

    private string WandMarkerStatusTextLocked()
    {
        string message = !_markerTrackingEnabled ? "Off. Use Marker setup to train the double-bar marker before tracking." :
            !_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _markerTrackingCameraId ? "Start the selected webcam to track the double-bar marker." :
            _wandMarkerLearning ? "Click inside either black bar in the live preview. Keep both bars facing the webcam, close to the board." :
            LearnedWandMarkerProfileLocked() is null ? "Use Marker setup to train this camera for the double-bar marker." :
            !WandMarkerBoardSelected ? "Double-bar marker saved. Tracking runs in Water Garden and Paint." :
            WandMarkerFreshLocked(MonotonicClock.UtcNow) ? _scene.CurrentBoardScreen == BoardScreen.Paint
                ? "Double-bar marker tracked. Move it across the board to leave an ink trail."
                : "Double-bar marker tracked. Move its centre over the water to make ripples." :
                _markerTrackingReason;
        return _wandMarkerSettingsError is null ? message : message + " " + _wandMarkerSettingsError;
    }

    private object GetWandMarkerStatusLocked()
    {
        var now = MonotonicClock.UtcNow;
        var preview = _wandMarkerPreview;
        var profile = LearnedWandMarkerProfileLocked();
        bool cameraReady = _cameraWanted && _camera.IsRunning && !_cameraHealthWarning && _camera.ActiveDeviceId == _markerTrackingCameraId;
        bool running = _markerTrackingEnabled && cameraReady && WandMarkerBoardSelected && profile is not null;
        bool confirmed = running && !_wandMarkerLearning && WandMarkerFreshLocked(now);
        bool previewFresh = preview is not null && MarkerFrameFresh(preview.Frame, now);
        return new
        {
            mode = "double-bar", enabled = _markerTrackingEnabled, learning = _wandMarkerLearning, learned = profile is not null,
            running, cameraDeviceId = _markerTrackingCameraId, profile, confirmed,
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
            reason = _markerTrackingReason, status = WandMarkerStatusTextLocked(),
            settingsPath = WandMarkerSettingsPath, settingsError = _wandMarkerSettingsError
        };
    }

    private void DrawWandMarkerPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        lock (_markerTrackingGate)
        {
            if (!_markerTrackingEnabled || _frozenFrame is not null || _wandMarkerPreview is not { } preview ||
                preview.Frame.Width != frame.Width || preview.Frame.Height != frame.Height ||
                !MarkerFrameFresh(preview.Frame, MonotonicClock.UtcNow)) return;
            DrawWandMarkerMarkers(ds, preview, rect, _wandMarkerLearning);
        }
    }

    private static void DrawWandMarkerMarkers(CanvasDrawingSession ds, WandMarkerPreview preview, Rect rect, bool candidates) =>
        DrawStickTipMarkers(ds, preview.Frame, rect,
            candidates ? preview.Detection.Candidates.Select(tip => (tip.Center, tip.RadiusPixels)) : [],
            preview.Track.Observation is { } tip && MonotonicClock.UtcNow - preview.ObservedAt <= MarkerObservationLifetime
                ? (tip.Center, tip.RadiusPixels) : null, preview.Track.Confirmed);
}
