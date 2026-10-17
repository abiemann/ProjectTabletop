using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan EyeTipLifetime = TimeSpan.FromMilliseconds(250);
    private readonly object _eyeTipGate = new();
    private readonly SemaphoreSlim _eyeTipDetectorGate = new(1, 1);
    private readonly EyeTipTracker _eyeTipTracker = new();
    private readonly FrameWorker _eyeTipWorker = new();
    private readonly CoalescedDispatch _eyeTipUiRefresh = new();
    private readonly Dictionary<string, double> _eyeTipProfiles = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _eyeTipTimer;
    private EyeTipPreview? _eyeTipPreview;
    private bool _eyeTipEnabled, _eyeTipLearning;
    private string? _eyeTipCameraId, _eyeTipSettingsError;
    private string _eyeTipReason = "tracking-off";
    private string _eyeTipStatus = "Off. Use Marker setup to train the wand.";
    private long _eyeTipGeneration;
    private long _eyeTipSceneRevision = -1;
    private BoardScreen _eyeTipSceneBoard;
    private DateTimeOffset _eyeTipNotBefore, _eyeTipLastAssociated;

    private sealed record EyeTipSettings(int Version, Dictionary<string, double> Cameras);
    private sealed record EyeTipPreview(CameraFrame Frame, EyeTipDetectionResult Detection,
        EyeTipTrackResult Track, double InferenceMilliseconds)
    {
        public CameraFrameLease FrameLease { get; } = Frame.Retain();
    }

    // Caller holds the shared eye/wand tracking gate.
    private void SetEyeTipPreviewLocked(EyeTipPreview? preview)
    {
        var previous = _eyeTipPreview;
        _eyeTipPreview = preview;
        previous?.FrameLease.Dispose();
    }
    private string EyeTipSettingsPath => Path.Combine(_appDataDirectory, "stick-tip.json");
    private bool IsLearningEyeTip { get { lock (_eyeTipGate) return _eyeTipLearning || _wandMarkerLearning; } }

    private void InitializeEyeTipTracking()
    {
        try
        {
            if (File.Exists(EyeTipSettingsPath))
            {
                if (new FileInfo(EyeTipSettingsPath).Length > 65536)
                    throw new InvalidDataException("The saved eye-tip settings are too large.");
                var saved = JsonSerializer.Deserialize<EyeTipSettings>(File.ReadAllText(EyeTipSettingsPath));
                if (saved is not { Version: 1, Cameras: not null } || saved.Cameras.Count > 64 ||
                    saved.Cameras.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 4096 ||
                        !double.IsFinite(pair.Value) || pair.Value is < .0001 or > .1))
                    throw new InvalidDataException("The saved eye-tip settings are invalid.");
                foreach (var pair in saved.Cameras) _eyeTipProfiles.Add(pair.Key, pair.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _eyeTipSettingsError = "Eye-tip settings could not be loaded: " + ex.Message; }
        InitializeWandMarkerTracking();
        // Remember the measured scale, but require a deliberate opt-in each launch.
        _eyeTipTimer = DispatcherQueue.CreateTimer();
        _eyeTipTimer.Interval = TimeSpan.FromMilliseconds(100);
        _eyeTipTimer.Tick += (_, _) =>
        {
            bool expired = false;
            lock (_eyeTipGate)
            {
                if (_eyeTipPreview is { } preview && !EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow))
                {
                    SetEyeTipPreviewLocked(null);
                    _eyeTipReason = "waiting-for-fresh-camera-frame";
                    _eyeTipTracker.Reset();
                    _eyeTipLastAssociated = default;
                    PublishStickTipLocked();
                    expired = true;
                }
                if (_wandMarkerPreview is { } wandPreview && !EyeTipFrameFresh(wandPreview.Frame, MonotonicClock.UtcNow))
                {
                    SetWandMarkerPreviewLocked(null);
                    // Release stale pixels and stop wand input at 250 ms, but
                    // preserve the tracker's separate 750 ms identity window.
                    // Only a new measured pose can publish input again.
                    _eyeTipReason = "waiting-for-fresh-camera-frame";
                    PublishStickTipLocked();
                    expired = true;
                }
            }
            UpdateEyeTipStatus();
            if (expired && !_closing) CameraCanvas.Invalidate();
        };
        _eyeTipTimer.Start();
        UpdateEyeTipStatus();
    }

    private static bool EyeTipFrameFresh(CameraFrame frame, DateTimeOffset now) =>
        frame.Timestamp <= now + TimeSpan.FromMilliseconds(30) && now - frame.Timestamp <= EyeTipLifetime;

    private double? LearnedEyeTipRadiusLocked() => _eyeTipCameraId is { } id &&
        _eyeTipProfiles.TryGetValue(id, out var radius) ? radius : null;

    private void ResetEyeTipTracking(string reason, string? cameraId = null)
    {
        lock (_eyeTipGate)
        {
            _eyeTipGeneration++;
            _eyeTipSceneBoard = _scene.CurrentBoardScreen;
            _eyeTipSceneRevision = CurrentWandMarkerInputRevision;
            _eyeTipNotBefore = MonotonicClock.UtcNow;
            _eyeTipLastAssociated = default;
            SetEyeTipPreviewLocked(null);
            _eyeTipTracker.Reset();
            _eyeTipLearning = false;
            ResetWandMarkerTrackingLocked();
            if (cameraId is not null) _eyeTipCameraId = cameraId;
            _eyeTipReason = reason;
            PublishStickTipLocked();
        }
        QueueEyeTipStatusRefresh();
    }

    private void SetStickTrackingEnabled(bool enabled)
    {
        lock (_eyeTipGate) _eyeTipEnabled = enabled;
        ResetEyeTipTracking(enabled ? "searching-for-wand-marker" : "tracking-off");
        UpdateEyeTipStatus();
    }

    private bool TryLearnEyeTipFromPreview(CameraFrame frame, PixelPoint point)
    {
        if (TryLearnWandMarkerFromPreview(frame, point)) return true;
        lock (_eyeTipGate) if (!_eyeTipLearning) return false;
        _ = LearnEyeTipFromPreviewAsync(frame, point);
        return true;
    }

    private async Task LearnEyeTipFromPreviewAsync(CameraFrame frame, PixelPoint point)
    {
        try { await LearnEyeTipAsync(point.X, point.Y, frame); }
        catch (Exception ex) { SetStatus("Eye tip could not be learned: " + ex.Message); }
    }

    private async Task<object> LearnEyeTipAsync(double x, double y, CameraFrame? selectedFrame = null)
    {
        using var frameLease = selectedFrame is null ? AcquireLatestCameraFrame() : selectedFrame.Retain();
        var frame = frameLease?.Frame;
        var cameraId = _camera.ActiveDeviceId;
        if (frame is null || !_cameraWanted || !_camera.IsRunning || cameraId != _selectedCameraId ||
            cameraId is null || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
            throw new InvalidOperationException("Start the selected webcam and wait for a fresh camera frame.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new ArgumentException("Provide x and y inside the raw camera frame.");
        CancelFootballLearning();
        // Serialize learning with inference; neither operation queues a camera-frame backlog.
        SetStickTrackingEnabled(true);
        _frozenFrame = null;
        long generation;
        lock (_eyeTipGate)
        {
            _eyeTipLearning = true;
            _eyeTipReason = "learning-eye-marker";
            generation = _eyeTipGeneration;
        }
        var started = Stopwatch.GetTimestamp();
        var detection = await Task.Run(async () =>
        {
            await _eyeTipDetectorGate.WaitAsync();
            try
            {
                return EyeTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                    new(PreferredCenter: new(x, y), ProjectionFrames: StickTipProjectionReference(frame, learning: true),
                        FrameTime: frame.Timestamp));
            }
            finally { _eyeTipDetectorGate.Release(); }
        });
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        bool wandModeChanged;
        lock (_eyeTipGate)
        {
            if (_closing || generation != _eyeTipGeneration || cameraId != _eyeTipCameraId ||
                !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
                throw new InvalidOperationException("The camera changed or the learning frame expired. Click the pupil again.");
            if (NearestStickTipCandidate(detection.Candidates, x, y, item => item.Center, item => item.RadiusPixels)
                is not { } chosen)
            {
                SetEyeTipPreviewLocked(new(frame, detection, new(null, false, "click-the-black-pupil"), elapsed));
                _eyeTipReason = "click-the-black-pupil";
                throw new InvalidOperationException("No unambiguous black pupil with a white surround is near that point.");
            }
            double normalizedRadius = chosen.RadiusPixels / frame.Width;
            if (normalizedRadius is < .0001 or > .1 || !double.IsFinite(normalizedRadius))
                throw new InvalidOperationException("The eye marker is outside the supported camera scale.");
            _eyeTipProfiles[cameraId] = normalizedRadius;
            // Only a learned eye replaces this camera's bar mode; an
            // abandoned learning attempt leaves the saved wand marker active.
            wandModeChanged = SelectEyeTipModeLocked();
            // Invalidate inference that started while the user-selected identity was being learned.
            _eyeTipGeneration++;
            _eyeTipTracker.Acquire(chosen, frame.Timestamp);
            _eyeTipLastAssociated = frame.Timestamp;
            SetEyeTipPreviewLocked(new(frame, detection, new(chosen, false, "confirming-eye-marker"), elapsed));
            _eyeTipLearning = false;
            _eyeTipReason = "confirming-eye-marker";
            PublishStickTipLocked();
        }
        SaveEyeTipSettings();
        if (wandModeChanged) SaveWandMarkerSettings();
        UpdateEyeTipStatus();
        CameraCanvas.Invalidate();
        return GetStickTipStatus();
    }

    private void SaveEyeTipSettings()
    {
        try
        {
            EyeTipSettings settings;
            lock (_eyeTipGate) settings = new(1, new(_eyeTipProfiles, StringComparer.Ordinal));
            Directory.CreateDirectory(_appDataDirectory);
            string temporary = EyeTipSettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, EyeTipSettingsPath, overwrite: true);
            _eyeTipSettingsError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _eyeTipSettingsError = "Eye-tip settings could not be saved: " + ex.Message; }
    }

    private void QueueEyeTipDetection(CameraFrame frame)
    {
        lock (_eyeTipGate)
        {
            var board = _scene.CurrentBoardScreen;
            long sceneRevision = CurrentWandMarkerInputRevision;
            if (board != _eyeTipSceneBoard || sceneRevision != _eyeTipSceneRevision)
            {
                // Disabled tracking has no association to invalidate. Preserve
                // the queued board-entry opt-in generation until it can run.
                if (_eyeTipEnabled) ResetEyeTipTracking("board-registration-changed");
                else { _eyeTipSceneBoard = board; _eyeTipSceneRevision = sceneRevision; }
            }
            var wandProfile = ActiveWandMarkerProfileLocked();
            if (_closing || !_eyeTipEnabled || _eyeTipWorker.IsBusy || _wandMarkerLearning ||
                (wandProfile is not null && !WandMarkerBoardSelected) ||
                (board == BoardScreen.Paint && wandProfile is null) ||
                (!_eyeTipLearning && wandProfile is null && LearnedEyeTipRadiusLocked() is null) ||
                _eyeTipCameraId != _cameraWantedDeviceId || frame.Timestamp < _eyeTipNotBefore ||
                !EyeTipFrameFresh(frame, MonotonicClock.UtcNow) || _cameraHealthWarning) return;
            if (wandProfile is not null && _wandMarkerFrameSize is { } previousSize &&
                previousSize != (frame.Width, frame.Height))
            {
                // Preview expiry releases its frame lease; keep the dimension
                // guard independently so old pixel coordinates cannot survive
                // a capture-format change while that preview is absent.
                _wandMarkerTracker.Reset();
                SetWandMarkerPreviewLocked(null);
                PublishStickTipLocked();
            }
            // The single worker drops arrivals while busy; no second rate cap
            // should discard fresh 60/100 fps stick positions.
            long generation = _eyeTipGeneration;
            double? expectedRadius = LearnedEyeTipRadiusLocked() * frame.Width;
            PixelPoint? preferredCenter = _eyeTipPreview?.Track.Observation?.Center;
            var wandHint = _wandMarkerTracker.GetSearchHint(frame.Timestamp);
            _eyeTipWorker.TryRun(Stopwatch.GetTimestamp(), TimeSpan.Zero, frame, async _ =>
            {
                try
                {
                    await _eyeTipDetectorGate.WaitAsync();
                    if (wandProfile is not null)
                    {
                        MarkerTipDetectionResult wandDetection;
                        var wandStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            var area = _scene.GetWandMarkerCameraPolygon();
                            wandDetection = area is null ? new([], "waiting-for-projected-bat-reference") :
                                BlackTipDetector.DetectEach(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                                    [wandProfile], new(ProjectionFrames: StickTipProjectionReference(frame), FrameTime: frame.Timestamp),
                                    [wandHint], area)[0];
                        }
                        finally { _eyeTipDetectorGate.Release(); }
                        PublishWandMarkerDetection(frame, wandDetection, wandProfile, wandHint,
                            Stopwatch.GetElapsedTime(wandStarted).TotalMilliseconds, generation);
                        return;
                    }
                    EyeTipDetectionResult detected;
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        detected = EyeTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                            new(expectedRadius, preferredCenter, StickTipProjectionReference(frame), frame.Timestamp));
                    }
                    finally { _eyeTipDetectorGate.Release(); }
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    lock (_eyeTipGate)
                    {
                        if (_closing || generation != _eyeTipGeneration || !_eyeTipEnabled || !_cameraWanted ||
                            _cameraHealthWarning || _camera.ActiveDeviceId != _eyeTipCameraId) return;
                        var time = MonotonicClock.UtcNow;
                        if (!EyeTipFrameFresh(frame, time))
                        {
                            SetEyeTipPreviewLocked(null);
                            _eyeTipTracker.Reset();
                            _eyeTipLastAssociated = default;
                            _eyeTipReason = "waiting-for-fresh-camera-frame";
                            PublishStickTipLocked();
                            return;
                        }
                        EyeTipTrackResult track = new(null, false, "click-the-black-pupil");
                        if (!_eyeTipLearning && LearnedEyeTipRadiusLocked() is { } radius)
                        {
                            if (_eyeTipPreview is { } previous &&
                                (previous.Frame.Width != frame.Width || previous.Frame.Height != frame.Height))
                            {
                                _eyeTipTracker.Reset();
                                _eyeTipLastAssociated = default;
                            }
                            var candidates = detected.Candidates.Where(item => item.RadiusPixels >= radius * frame.Width * .60 &&
                                item.RadiusPixels <= radius * frame.Width * 1.65).ToArray();
                            bool acquiring = _eyeTipLastAssociated == default ||
                                frame.Timestamp - _eyeTipLastAssociated > TimeSpan.FromMilliseconds(450);
                            if (detected.Reason == "waiting-for-projected-eye-reference")
                            {
                                _eyeTipTracker.Reset();
                                _eyeTipLastAssociated = default;
                                track = new(null, false, detected.Reason);
                            }
                            else if (acquiring && candidates.Length > 1)
                            {
                                _eyeTipTracker.Reset();
                                track = new(null, false, "ambiguous-eye-marker");
                            }
                            else
                                track = _eyeTipTracker.Update(new(candidates, detected.Reason), frame.Timestamp, time);
                            if (track.Observation is not null) _eyeTipLastAssociated = frame.Timestamp;
                        }
                        SetEyeTipPreviewLocked(new(frame, detected, track, elapsed));
                        _eyeTipReason = track.Reason;
                        PublishStickTipLocked();
                    }
                }
                catch (Exception ex)
                {
                    lock (_eyeTipGate)
                    {
                        if (generation != _eyeTipGeneration) return;
                        SetEyeTipPreviewLocked(null);
                        _eyeTipTracker.Reset();
                        SetWandMarkerPreviewLocked(null);
                        _wandMarkerTracker.Reset();
                        _eyeTipLastAssociated = default;
                        _eyeTipReason = "Detection failed: " + ex.Message;
                        PublishStickTipLocked();
                    }
                }
                finally
                {
                    QueueEyeTipStatusRefresh();
                }
            });
        }
    }

    private void QueueEyeTipStatusRefresh()
    {
        if (_closing) return;
        _eyeTipUiRefresh.TryPost(_dispatchInputResult, () =>
        {
            if (_closing) return;
            // Read the latest state, including a reset or camera change
            // since this callback was queued. Scene input is published on
            // the worker immediately; only laptop updates are coalesced.
            UpdateEyeTipStatus();
            RequestCameraPreviewRedraw();
        });
    }

    private object GetStickTipStatus()
    {
        lock (_eyeTipGate)
        {
            if (WandMarkerModeActiveLocked()) return GetWandMarkerStatusLocked();
            var now = MonotonicClock.UtcNow;
            var preview = _eyeTipPreview;
            bool fresh = preview is not null && EyeTipFrameFresh(preview.Frame, now);
            bool cameraReady = _cameraWanted && _camera.IsRunning && !_cameraHealthWarning &&
                _camera.ActiveDeviceId == _eyeTipCameraId;
            bool confirmed = _eyeTipEnabled && cameraReady && fresh && preview!.Track.Confirmed;
            double? radius = LearnedEyeTipRadiusLocked();
            return new
            {
                mode = "eye",
                enabled = _eyeTipEnabled, learning = _eyeTipLearning, learned = radius is not null,
                running = _eyeTipEnabled && cameraReady && (radius is not null || _eyeTipLearning),
                cameraDeviceId = _eyeTipCameraId, normalizedRadius = radius,
                confirmed, tip = confirmed ? preview!.Track.Observation : null,
                cameraX = confirmed ? preview!.Track.Observation?.Center.X : null,
                cameraY = confirmed ? preview!.Track.Observation?.Center.Y : null,
                candidates = fresh ? preview!.Detection.Candidates : Array.Empty<EyeTipObservation>(),
                sourceFrameUtc = preview?.Frame.Timestamp, frameWidth = preview?.Frame.Width,
                frameHeight = preview?.Frame.Height, inferenceMilliseconds = preview?.InferenceMilliseconds,
                resultAgeMilliseconds = preview is null ? (double?)null : (now - preview.Frame.Timestamp).TotalMilliseconds,
                reason = _eyeTipReason, status = _eyeTipStatus,
                settingsPath = EyeTipSettingsPath, settingsError = _eyeTipSettingsError
            };
        }
    }

    private void UpdateEyeTipStatus()
    {
        lock (_eyeTipGate)
        {
            if (WandMarkerModeActiveLocked())
            {
                _eyeTipStatus = WandMarkerStatusTextLocked();
                return;
            }
            bool learned = LearnedEyeTipRadiusLocked() is not null;
            string message = !_eyeTipEnabled ? (learned ? "Off. Eye-tip size remembered for this camera." : "Off. Learn the eye sticker before tracking.") :
                !_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _eyeTipCameraId ? "Start the selected webcam to see the eye tip." :
                _eyeTipLearning ? "Click the black pupil in the live camera preview. Yellow circles show candidates." :
                !learned ? "Choose Learn eye tip, then click the black pupil in the camera preview." :
                _eyeTipPreview is { Track: { Confirmed: true, Observation: { } tip } } preview && EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow)
                    ? $"Eye tip tracked at ({tip.Center.X:F0}, {tip.Center.Y:F0}) camera pixels. Move it over Water Garden to disturb the surface." :
                _eyeTipReason == "ambiguous-eye-marker" ? "Several eyes match. Move the stick clear of other marks, or learn its pupil again." :
                _eyeTipReason == "confirming-eye-marker" ? "Confirming the eye tip across fresh camera frames…" :
                _eyeTipReason == "waiting-for-projected-eye-reference" ? "Waiting for the current board image before tracking the eye." :
                _eyeTipReason.StartsWith("Detection failed:", StringComparison.Ordinal) ? _eyeTipReason :
                    "Eye tip not visible. Keep the black pupil and white surround facing the camera.";
            _eyeTipStatus = _eyeTipSettingsError is null ? message : message + " " + _eyeTipSettingsError;
        }
    }

    private void DrawEyeTipPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        lock (_eyeTipGate)
            if (WandMarkerModeActiveLocked())
            { DrawWandMarkerPreview(ds, frame, rect); return; }
        EyeTipPreview? preview;
        bool learning;
        lock (_eyeTipGate)
        {
            preview = _eyeTipPreview;
            learning = _eyeTipLearning;
            if (!_eyeTipEnabled || _frozenFrame is not null || preview is null ||
                preview.Frame.Width != frame.Width || preview.Frame.Height != frame.Height ||
                !EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow)) return;
        }
        DrawEyeTipMarkers(ds, preview, rect, learning);
    }

    private static void DrawEyeTipMarkers(CanvasDrawingSession ds, EyeTipPreview preview, Rect rect, bool candidates) =>
        DrawStickTipMarkers(ds, preview.Frame, rect,
            candidates ? preview.Detection.Candidates.Select(eye => (eye.Center, eye.RadiusPixels)) : [],
            preview.Track.Observation is { } tip ? (tip.Center, tip.RadiusPixels) : null, preview.Track.Confirmed);

    // Shared by the eye and bar previews: gold candidates, then the
    // associated tip, lime once confirmed across fresh frames.
    private static void DrawStickTipMarkers(CanvasDrawingSession ds, CameraFrame frame, Rect rect,
        IEnumerable<(PixelPoint Center, double Radius)> candidates, (PixelPoint Center, double Radius)? tip, bool confirmed)
    {
        Vector2 Point(PixelPoint center) => new((float)(rect.X + center.X / frame.Width * rect.Width),
            (float)(rect.Y + center.Y / frame.Height * rect.Height));
        float Radius(double radius) => Math.Max(5, (float)(radius / frame.Width * rect.Width) + 3);
        foreach (var candidate in candidates)
            ds.DrawCircle(Point(candidate.Center), Radius(candidate.Radius), Colors.Gold, 1.5f);
        if (tip is { } current)
        {
            var center = Point(current.Center);
            float radius = Radius(current.Radius);
            var color = confirmed ? Colors.Lime : Colors.Gold;
            ds.DrawCircle(center, radius + 2, Colors.Black, 4);
            ds.DrawCircle(center, radius + 2, color, 2);
            ds.FillCircle(center, 1.5f, color);
        }
    }

    // No board clip means stand-alone detection. A board whose reference is
    // not ready yet returns an empty history, so the detector waits for it.
    private IReadOnlyList<EyeTipProjectionFrame>? StickTipProjectionReference(CameraFrame frame, bool learning = false)
    {
        if (!_scene.HasBoardMediaClip || _scene.CurrentBoardScreen == BoardScreen.Media) return null;
        var references = _scene.GetEyeTipProjectionFrames();
        long minimumAge = Volatile.Read(ref _projectionReferenceMinimumAgeTicks);
        // First-time learning may create the very first reference: retain its
        // conservative artwork veto. Continuous tracking builds a history and
        // can align that history with the camera's measured delivery delay.
        if (learning || minimumAge <= 0) return references;
        // The scene retains three references spaced at least 150 ms apart.
        // Bound this conservative correction so a slower camera cannot exclude
        // the entire rolling history forever once it has filled.
        minimumAge = Math.Min(minimumAge, TimeSpan.FromMilliseconds(250).Ticks);
        // Compare against artwork that could have reached this exposure. Newer
        // graphics can overlap real ink in the delayed image and falsely veto it.
        var latestVisible = (frame.ArrivedAt ?? frame.Timestamp).Subtract(TimeSpan.FromTicks(minimumAge));
        return references.Where(reference => reference.RenderedAt <= latestVisible).ToArray();
    }

    // The candidate a learning click identifies, or null when none is within
    // reach or a second candidate is nearly as close.
    private static T? NearestStickTipCandidate<T>(IEnumerable<T> candidates, double x, double y,
        Func<T, PixelPoint> center, Func<T, double> radius) where T : class
    {
        var near = candidates.Select(item => (Item: item,
                Distance: Math.Sqrt(Math.Pow(center(item).X - x, 2) + Math.Pow(center(item).Y - y, 2))))
            .Where(item => item.Distance <= Math.Max(12, radius(item.Item) * 2.5))
            .OrderBy(item => item.Distance).ToArray();
        return near.Length == 0 || (near.Length > 1 && near[1].Distance - near[0].Distance < 4) ? null : near[0].Item;
    }

    private async Task<object> CaptureStickTipAsync()
    {
        WandMarkerPreview? wandPreview;
        EyeTipPreview? preview;
        CameraFrameLease? selectedLease;
        lock (_eyeTipGate)
        {
            wandPreview = _eyeTipEnabled && ActiveWandMarkerProfileLocked() is not null &&
                _wandMarkerPreview is { } wandCurrent && EyeTipFrameFresh(wandCurrent.Frame, MonotonicClock.UtcNow) ? wandCurrent : null;
            preview = _eyeTipEnabled && _cameraWanted && _camera.IsRunning &&
                !_cameraHealthWarning && _camera.ActiveDeviceId == _eyeTipCameraId &&
                _eyeTipPreview is { } current && EyeTipFrameFresh(current.Frame, MonotonicClock.UtcNow) ? current : null;
            selectedLease = (wandPreview?.Frame ?? preview?.Frame)?.Retain();
        }
        using var frameLease = selectedLease ?? AcquireLatestCameraFrame() ??
            throw new InvalidOperationException("Start the webcam before capturing the eye tip.");
        var frame = frameLease.Frame;
        var status = GetStickTipStatus();
        string directory = Path.Combine(_appDataDirectory, "StickTipSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"stick-tip-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), frame.Width, frame.Height, 96);
        using var bitmap = CanvasBitmap.CreateFromBytes(target, frame.Bgra, frame.Width, frame.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
        string? rawPath = null;
        if (wandPreview is not null)
        {
            // Save the exact inferred frame for replay; a later raw-frame command
            // can already contain different projected texture on the marker.
            rawPath = Path.ChangeExtension(path, ".raw.png");
            await bitmap.SaveAsync(rawPath, CanvasBitmapFileFormat.Png);
        }
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(bitmap);
            if (wandPreview is not null) DrawWandMarkerMarkers(ds, wandPreview, new(0, 0, frame.Width, frame.Height), candidates: true);
            else if (preview is not null) DrawEyeTipMarkers(ds, preview, new(0, 0, frame.Width, frame.Height), candidates: true);
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            sourceFrameUtc = frame.Timestamp, capturedAtUtc = MonotonicClock.UtcNow,
            frame.Width, frame.Height, frame.Stride, rawPath, searchHint = wandPreview?.SearchHint,
            detection = wandPreview?.Detection,
            observation = wandPreview is not null ? (object)wandPreview.Track : preview?.Track, status
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new { path, rawPath, sourceFrameUtc = frame.Timestamp, status };
    }

    private async Task DisposeEyeTipTrackingAsync()
    {
        _eyeTipTimer?.Stop();
        ResetEyeTipTracking("tracking-off");
        lock (_eyeTipGate) _eyeTipEnabled = false;
        await _eyeTipWorker.WorkerTask;
    }
}
