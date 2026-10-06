using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan EyeTipInterval = TimeSpan.FromMilliseconds(66);
    private static readonly TimeSpan EyeTipLifetime = TimeSpan.FromMilliseconds(250);
    private readonly object _eyeTipGate = new();
    private readonly SemaphoreSlim _eyeTipDetectorGate = new(1, 1);
    private readonly EyeTipTracker _eyeTipTracker = new();
    private readonly Dictionary<string, double> _eyeTipProfiles = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _eyeTipTimer;
    private Task? _eyeTipTask;
    private EyeTipPreview? _eyeTipPreview;
    private bool _eyeTipEnabled, _eyeTipLearning, _eyeTipDetecting;
    private string? _eyeTipCameraId, _eyeTipSettingsError;
    private string _eyeTipReason = "tracking-off";
    private long _eyeTipGeneration, _eyeTipLastQueuedTick;
    private DateTimeOffset _eyeTipNotBefore, _eyeTipLastAssociated;

    private sealed record EyeTipSettings(int Version, Dictionary<string, double> Cameras);
    private sealed record EyeTipPreview(CameraFrame Frame, EyeTipDetectionResult Detection,
        EyeTipTrackResult Track, double InferenceMilliseconds);
    private string EyeTipSettingsPath => Path.Combine(_appDataDirectory, "stick-tip.json");
    private bool IsLearningEyeTip { get { lock (_eyeTipGate) return _eyeTipLearning; } }

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
                    _eyeTipPreview = null;
                    _eyeTipReason = "waiting-for-fresh-camera-frame";
                    _eyeTipTracker.Reset();
                    _eyeTipLastAssociated = default;
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
            _eyeTipNotBefore = MonotonicClock.UtcNow;
            _eyeTipLastQueuedTick = 0;
            _eyeTipLastAssociated = default;
            _eyeTipPreview = null;
            _eyeTipTracker.Reset();
            _eyeTipLearning = false;
            if (cameraId is not null) _eyeTipCameraId = cameraId;
            _eyeTipReason = reason;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            UpdateEyeTipStatus();
            CameraCanvas.Invalidate();
        });
    }

    private void StickTrackingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initialized) SetStickTrackingEnabled(StickTrackingToggle.IsOn);
    }

    private void SetStickTrackingEnabled(bool enabled)
    {
        lock (_eyeTipGate) _eyeTipEnabled = enabled;
        ResetEyeTipTracking(enabled ? "searching-for-eye-marker" : "tracking-off");
        if (StickTrackingToggle.IsOn != enabled) StickTrackingToggle.IsOn = enabled;
        UpdateEyeTipStatus();
    }

    private void LearnEyeTip_Click(object sender, RoutedEventArgs e)
    {
        if (!_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _selectedCameraId)
        { SetStatus("Start the selected webcam before learning the eye tip."); return; }
        SetStickTrackingEnabled(true);
        _frozenFrame = null;
        lock (_eyeTipGate)
        {
            _eyeTipLearning = true;
            _eyeTipReason = "click-the-black-pupil";
        }
        UpdateEyeTipStatus();
        CameraCanvas.Invalidate();
    }

    private void ForgetEyeTip_Click(object sender, RoutedEventArgs e)
    {
        lock (_eyeTipGate)
            if (_eyeTipCameraId is { } id) _eyeTipProfiles.Remove(id);
        ResetEyeTipTracking("eye-marker-not-learned");
        SaveEyeTipSettings();
        UpdateEyeTipStatus();
    }

    private bool TryLearnEyeTipFromPreview(CameraFrame frame, PixelPoint point)
    {
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
        var frame = selectedFrame ?? Volatile.Read(ref _latestCameraFrame);
        var cameraId = _camera.ActiveDeviceId;
        if (frame is null || !_cameraWanted || !_camera.IsRunning || cameraId != _selectedCameraId ||
            cameraId is null || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
            throw new InvalidOperationException("Start the selected webcam and wait for a fresh camera frame.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new ArgumentException("Provide x and y inside the raw camera frame.");
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
            try { return EyeTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra); }
            finally { _eyeTipDetectorGate.Release(); }
        });
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_eyeTipGate)
        {
            if (_closing || generation != _eyeTipGeneration || cameraId != _eyeTipCameraId ||
                !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
                throw new InvalidOperationException("The camera changed or the learning frame expired. Click the pupil again.");
            var near = detection.Candidates.Select(item => (Item: item,
                    Distance: Math.Sqrt(Math.Pow(item.Center.X - x, 2) + Math.Pow(item.Center.Y - y, 2))))
                .Where(item => item.Distance <= Math.Max(12, item.Item.RadiusPixels * 2.5))
                .OrderBy(item => item.Distance).ToArray();
            if (near.Length == 0 || (near.Length > 1 && near[1].Distance - near[0].Distance < 4))
            {
                _eyeTipPreview = new(frame, detection, new(null, false, "click-the-black-pupil"), elapsed);
                _eyeTipReason = "click-the-black-pupil";
                throw new InvalidOperationException("No unambiguous black pupil with a white surround is near that point.");
            }
            var chosen = near[0].Item;
            double normalizedRadius = chosen.RadiusPixels / frame.Width;
            if (normalizedRadius is < .0001 or > .1 || !double.IsFinite(normalizedRadius))
                throw new InvalidOperationException("The eye marker is outside the supported camera scale.");
            _eyeTipProfiles[cameraId] = normalizedRadius;
            // Invalidate inference that started while the user-selected identity was being learned.
            _eyeTipGeneration++;
            _eyeTipTracker.Acquire(chosen, frame.Timestamp);
            _eyeTipLastAssociated = frame.Timestamp;
            _eyeTipPreview = new(frame, detection, new(chosen, false, "confirming-eye-marker"), elapsed);
            _eyeTipLearning = false;
            _eyeTipReason = "confirming-eye-marker";
        }
        SaveEyeTipSettings();
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
            var now = Stopwatch.GetTimestamp();
            if (_closing || !_eyeTipEnabled || _eyeTipDetecting ||
                (!_eyeTipLearning && LearnedEyeTipRadiusLocked() is null) ||
                _eyeTipCameraId != _cameraWantedDeviceId || frame.Timestamp < _eyeTipNotBefore ||
                !EyeTipFrameFresh(frame, MonotonicClock.UtcNow) || _cameraHealthWarning ||
                (_eyeTipLastQueuedTick != 0 && Stopwatch.GetElapsedTime(_eyeTipLastQueuedTick, now) < EyeTipInterval)) return;
            _eyeTipDetecting = true;
            _eyeTipLastQueuedTick = now;
            long generation = _eyeTipGeneration;
            _eyeTipTask = Task.Run(async () =>
            {
                try
                {
                    await _eyeTipDetectorGate.WaitAsync();
                    EyeTipDetectionResult detected;
                    var started = Stopwatch.GetTimestamp();
                    try { detected = EyeTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra); }
                    finally { _eyeTipDetectorGate.Release(); }
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    lock (_eyeTipGate)
                    {
                        if (_closing || generation != _eyeTipGeneration || !_eyeTipEnabled || !_cameraWanted ||
                            _cameraHealthWarning || _camera.ActiveDeviceId != _eyeTipCameraId) return;
                        var time = MonotonicClock.UtcNow;
                        if (!EyeTipFrameFresh(frame, time))
                        {
                            _eyeTipPreview = null;
                            _eyeTipTracker.Reset();
                            _eyeTipLastAssociated = default;
                            _eyeTipReason = "waiting-for-fresh-camera-frame";
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
                            if (acquiring && candidates.Length > 1)
                            {
                                _eyeTipTracker.Reset();
                                track = new(null, false, "ambiguous-eye-marker");
                            }
                            else
                                track = _eyeTipTracker.Update(new(candidates, detected.Reason), frame.Timestamp, time);
                            if (track.Observation is not null) _eyeTipLastAssociated = frame.Timestamp;
                        }
                        _eyeTipPreview = new(frame, detected, track, elapsed);
                        _eyeTipReason = track.Reason;
                    }
                }
                catch (Exception ex)
                {
                    lock (_eyeTipGate)
                    {
                        if (generation != _eyeTipGeneration) return;
                        _eyeTipPreview = null;
                        _eyeTipTracker.Reset();
                        _eyeTipLastAssociated = default;
                        _eyeTipReason = "Detection failed: " + ex.Message;
                    }
                }
                finally
                {
                    lock (_eyeTipGate) _eyeTipDetecting = false;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closing || generation != _eyeTipGeneration) return;
                        UpdateEyeTipStatus();
                        CameraCanvas.Invalidate();
                    });
                }
            });
        }
    }

    private object GetStickTipStatus()
    {
        lock (_eyeTipGate)
        {
            var now = MonotonicClock.UtcNow;
            var preview = _eyeTipPreview;
            bool fresh = preview is not null && EyeTipFrameFresh(preview.Frame, now);
            bool cameraReady = _cameraWanted && _camera.IsRunning && !_cameraHealthWarning &&
                _camera.ActiveDeviceId == _eyeTipCameraId;
            bool confirmed = _eyeTipEnabled && cameraReady && fresh && preview!.Track.Confirmed;
            double? radius = LearnedEyeTipRadiusLocked();
            return new
            {
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
                reason = _eyeTipReason, status = EyeTipStatusText.Text,
                settingsPath = EyeTipSettingsPath, settingsError = _eyeTipSettingsError
            };
        }
    }

    private void UpdateEyeTipStatus()
    {
        lock (_eyeTipGate)
        {
            bool learned = LearnedEyeTipRadiusLocked() is not null;
            string message = !_eyeTipEnabled ? (learned ? "Off. Eye-tip size remembered for this camera." : "Off. Learn the eye sticker before tracking.") :
                !_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _eyeTipCameraId ? "Start the selected webcam to see the eye tip." :
                _eyeTipLearning ? "Click the black pupil in the live camera preview. Yellow circles show candidates." :
                !learned ? "Choose Learn eye tip, then click the black pupil in the camera preview." :
                _eyeTipPreview is { Track: { Confirmed: true, Observation: { } tip } } preview && EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow)
                    ? $"Eye tip tracked at ({tip.Center.X:F0}, {tip.Center.Y:F0}) camera pixels. Camera preview only." :
                _eyeTipReason == "ambiguous-eye-marker" ? "Several eyes match. Move the stick clear of other marks, or learn its pupil again." :
                _eyeTipReason == "confirming-eye-marker" ? "Confirming the eye tip across fresh camera frames…" :
                _eyeTipReason.StartsWith("Detection failed:", StringComparison.Ordinal) ? _eyeTipReason :
                    "Eye tip not visible. Keep the black pupil and white surround facing the camera.";
            EyeTipStatusText.Text = _eyeTipSettingsError is null ? message : message + " " + _eyeTipSettingsError;
        }
    }

    private void DrawEyeTipPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
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

    private static void DrawEyeTipMarkers(CanvasDrawingSession ds, EyeTipPreview preview, Rect rect, bool candidates)
    {
        Vector2 Point(EyeTipObservation eye) => new((float)(rect.X + eye.Center.X / preview.Frame.Width * rect.Width),
            (float)(rect.Y + eye.Center.Y / preview.Frame.Height * rect.Height));
        float Radius(EyeTipObservation eye) => Math.Max(5, (float)(eye.RadiusPixels / preview.Frame.Width * rect.Width) + 3);
        if (candidates) foreach (var eye in preview.Detection.Candidates)
            ds.DrawCircle(Point(eye), Radius(eye), Colors.Gold, 1.5f);
        if (preview.Track.Observation is { } tip)
        {
            var center = Point(tip);
            float radius = Radius(tip);
            ds.DrawCircle(center, radius + 2, Colors.Black, 4);
            ds.DrawCircle(center, radius + 2, preview.Track.Confirmed ? Colors.Lime : Colors.Gold, 2);
            ds.FillCircle(center, 1.5f, preview.Track.Confirmed ? Colors.Lime : Colors.Gold);
        }
    }

    private async Task<object> CaptureStickTipAsync()
    {
        EyeTipPreview? preview;
        lock (_eyeTipGate) preview = _eyeTipEnabled && _cameraWanted && _camera.IsRunning &&
            !_cameraHealthWarning && _camera.ActiveDeviceId == _eyeTipCameraId &&
            _eyeTipPreview is { } current && EyeTipFrameFresh(current.Frame, MonotonicClock.UtcNow) ? current : null;
        var frame = preview?.Frame ?? Volatile.Read(ref _latestCameraFrame) ??
            throw new InvalidOperationException("Start the webcam before capturing the eye tip.");
        var status = GetStickTipStatus();
        string directory = Path.Combine(_appDataDirectory, "StickTipSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"stick-tip-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), frame.Width, frame.Height, 96);
        using var bitmap = CanvasBitmap.CreateFromBytes(target, frame.Bgra, frame.Width, frame.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(bitmap);
            if (preview is not null) DrawEyeTipMarkers(ds, preview, new(0, 0, frame.Width, frame.Height), candidates: true);
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            sourceFrameUtc = frame.Timestamp, capturedAtUtc = MonotonicClock.UtcNow,
            frame.Width, frame.Height, observation = preview?.Track, status
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new { path, sourceFrameUtc = frame.Timestamp, status };
    }

    private async Task DisposeEyeTipTrackingAsync()
    {
        _eyeTipTimer?.Stop();
        ResetEyeTipTracking("tracking-off");
        Task? task;
        lock (_eyeTipGate) { _eyeTipEnabled = false; task = _eyeTipTask; }
        if (task is not null) await task;
    }
}
