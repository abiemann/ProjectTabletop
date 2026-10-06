using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Shares the eye tracker's lock, generation and inference gate. A camera has
    // one selected marker mode, so learning a colour never creates a second tip.
    private readonly Dictionary<string, ColorTipProfile> _colorTipProfiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _colorTipModes = new(StringComparer.Ordinal);
    private readonly ColorTipTracker _colorTipTracker = new();
    private bool _colorTipLearning;
    private ColorTipPreview? _colorTipPreview;
    private string? _colorTipSettingsError;
    private sealed record ColorTipSettings(int Version, Dictionary<string, ColorTipProfile> Cameras,
        Dictionary<string, string> ActiveModes);
    private sealed record ColorTipPreview(CameraFrame Frame, ColorTipDetectionResult Detection,
        ColorTipTrackResult Track, double InferenceMilliseconds);
    private string ColorTipSettingsPath => Path.Combine(_appDataDirectory, "stick-color-tip.json");

    private void InitializeColorTipTracking()
    {
        lock (_eyeTipGate)
        {
            try
            {
                if (!File.Exists(ColorTipSettingsPath)) return;
                if (new FileInfo(ColorTipSettingsPath).Length > 65536)
                    throw new InvalidDataException("The saved colour-tip settings are too large.");
                var saved = JsonSerializer.Deserialize<ColorTipSettings>(File.ReadAllText(ColorTipSettingsPath));
                if (saved is not { Version: 1, Cameras: not null, ActiveModes: not null } ||
                    saved.Cameras.Count > 64 || saved.ActiveModes.Count > 64 ||
                    saved.Cameras.Any(pair => !ValidCameraKey(pair.Key) || pair.Value is null || !pair.Value.IsValid) ||
                    saved.ActiveModes.Any(pair => !ValidCameraKey(pair.Key) ||
                        pair.Value is not ("color" or "eye") || !saved.Cameras.ContainsKey(pair.Key)))
                    throw new InvalidDataException("The saved colour-tip settings are invalid.");
                foreach (var pair in saved.Cameras) _colorTipProfiles.Add(pair.Key, pair.Value);
                foreach (var pair in saved.ActiveModes) _colorTipModes.Add(pair.Key, pair.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { _colorTipSettingsError = "Colour-tip settings could not be loaded: " + ex.Message; }
        }
        static bool ValidCameraKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 4096;
    }

    private ColorTipProfile? LearnedColorTipProfileLocked() => _eyeTipCameraId is { } id &&
        _colorTipModes.TryGetValue(id, out var mode) && mode == "color" &&
        _colorTipProfiles.TryGetValue(id, out var profile) ? profile : null;

    private void ResetColorTipTrackingLocked()
    {
        _colorTipLearning = false;
        _colorTipPreview = null;
        _colorTipTracker.Reset();
    }

    private void DeactivateColorTipForCameraLocked()
    {
        ResetColorTipTrackingLocked();
        if (_eyeTipCameraId is { } id && _colorTipProfiles.ContainsKey(id))
        {
            _colorTipModes[id] = "eye";
            SaveColorTipSettingsLocked();
        }
    }

    private void ForgetColorTipForCameraLocked()
    {
        ResetColorTipTrackingLocked();
        if (_eyeTipCameraId is { } id)
        {
            _colorTipProfiles.Remove(id);
            _colorTipModes.Remove(id);
            SaveColorTipSettingsLocked();
        }
    }

    private void SaveColorTipSettingsLocked()
    {
        try
        {
            var saved = new ColorTipSettings(1, new(_colorTipProfiles, StringComparer.Ordinal),
                new(_colorTipModes, StringComparer.Ordinal));
            Directory.CreateDirectory(_appDataDirectory);
            string temporary = ColorTipSettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ColorTipSettingsPath, overwrite: true);
            _colorTipSettingsError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _colorTipSettingsError = "Colour-tip settings could not be saved: " + ex.Message; }
    }

    private void LearnColorTip_Click(object sender, RoutedEventArgs e)
    {
        if (!_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _selectedCameraId)
        { SetStatus("Start the selected webcam before learning the coloured tip."); return; }
        SetStickTrackingEnabled(true);
        _frozenFrame = null;
        lock (_eyeTipGate)
        {
            _eyeTipLearning = false;
            _colorTipLearning = true;
            _eyeTipReason = "click-the-coloured-tip";
            PublishWaterStickTipLocked();
        }
        UpdateEyeTipStatus();
        CameraCanvas.Invalidate();
    }

    private bool TryLearnColorTipFromPreview(CameraFrame frame, PixelPoint point)
    {
        lock (_eyeTipGate) if (!_colorTipLearning) return false;
        _ = LearnColorTipFromPreviewAsync(frame, point);
        return true;
    }

    private async Task LearnColorTipFromPreviewAsync(CameraFrame frame, PixelPoint point)
    {
        try { await LearnColorTipAsync(point.X, point.Y, frame); }
        catch (Exception ex) { SetStatus("Coloured tip could not be learned: " + ex.Message); }
    }

    private async Task<object> LearnColorTipAsync(double x, double y, CameraFrame? selectedFrame = null)
    {
        var frame = selectedFrame ?? Volatile.Read(ref _latestCameraFrame);
        var cameraId = _camera.ActiveDeviceId;
        if (frame is null || !_cameraWanted || !_camera.IsRunning || cameraId != _selectedCameraId ||
            cameraId is null || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
            throw new InvalidOperationException("Start the selected webcam and wait for a fresh camera frame.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new ArgumentException("Provide x and y inside the raw camera frame.");
        SetStickTrackingEnabled(true);
        _frozenFrame = null;
        long generation;
        lock (_eyeTipGate)
        {
            _eyeTipLearning = false;
            _colorTipLearning = true;
            _eyeTipReason = "learning-colour-marker";
            generation = _eyeTipGeneration;
            PublishWaterStickTipLocked();
        }
        long started = Stopwatch.GetTimestamp();
        var learned = await Task.Run(async () =>
        {
            await _eyeTipDetectorGate.WaitAsync();
            try
            {
                var profile = ColorTipDetector.Learn(frame.Width, frame.Height, frame.Stride, frame.Bgra, new(x, y));
                var reference = _scene.HasBoardMediaClip && _scene.CurrentBoardScreen != BoardScreen.Media
                    ? _scene.GetEyeTipProjectionFrames() : null;
                var detection = ColorTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra, profile,
                    new(PreferredCenter: new(x, y), ProjectionFrames: reference, FrameTime: frame.Timestamp));
                return (Profile: profile, Detection: detection);
            }
            finally { _eyeTipDetectorGate.Release(); }
        });
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_eyeTipGate)
        {
            if (_closing || generation != _eyeTipGeneration || cameraId != _eyeTipCameraId ||
                !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
                throw new InvalidOperationException("The camera changed or the learning frame expired. Click the coloured tip again.");
            var near = learned.Detection.Candidates.Select(item => (Item: item,
                    Distance: Math.Sqrt(Math.Pow(item.Center.X - x, 2) + Math.Pow(item.Center.Y - y, 2))))
                .Where(item => item.Distance <= Math.Max(12, item.Item.RadiusPixels * 2.5))
                .OrderBy(item => item.Distance).ToArray();
            if (!learned.Profile.IsValid || near.Length == 0 ||
                (near.Length > 1 && near[1].Distance - near[0].Distance < 4))
            {
                _colorTipPreview = new(frame, learned.Detection, new(null, false, "click-the-coloured-tip"), elapsed);
                _eyeTipReason = "click-the-coloured-tip";
                throw new InvalidOperationException("Click inside one clearly coloured tip, away from similarly coloured objects.");
            }
            var chosen = near[0].Item;
            _colorTipProfiles[cameraId] = learned.Profile;
            _colorTipModes[cameraId] = "color";
            _eyeTipGeneration++;
            _eyeTipPreview = null;
            _eyeTipTracker.Reset();
            _colorTipTracker.Acquire(chosen, frame.Timestamp);
            _eyeTipLastAssociated = frame.Timestamp;
            _colorTipPreview = new(frame, learned.Detection, new(chosen, false, "confirming-colour-marker"), elapsed);
            _colorTipLearning = false;
            _eyeTipReason = "confirming-colour-marker";
            SaveColorTipSettingsLocked();
            PublishWaterStickTipLocked();
        }
        UpdateEyeTipStatus();
        CameraCanvas.Invalidate();
        return GetStickTipStatus();
    }

    private string ColorTipStatusTextLocked()
    {
        bool learned = LearnedColorTipProfileLocked() is not null;
        string message = !_eyeTipEnabled ? (learned ? "Off. Coloured tip remembered for this camera." : "Off. Learn a coloured tip before tracking.") :
            !_camera.IsRunning || !_cameraWanted || _camera.ActiveDeviceId != _eyeTipCameraId ? "Start the selected webcam to see the coloured tip." :
            _colorTipLearning ? "Click inside the coloured pin or marker in the live camera preview." :
            !learned ? "Choose Learn colour tip, then click inside the coloured marker." :
            _colorTipPreview is { Track: { Confirmed: true, Observation: { } tip } } preview && EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow)
                ? $"Coloured tip tracked at ({tip.Center.X:F0}, {tip.Center.Y:F0}) camera pixels. Move it over Water Garden to disturb the surface." :
            _eyeTipReason.Contains("ambiguous", StringComparison.Ordinal) ? "Several colours match. Move the pin clear of other matching objects, or learn it again." :
            _eyeTipReason.Contains("confirming", StringComparison.Ordinal) ? "Confirming the coloured tip across fresh camera frames…" :
            _eyeTipReason.StartsWith("Detection failed:", StringComparison.Ordinal) ? _eyeTipReason :
                "Coloured tip not visible. Keep its coloured surface facing the camera.";
        return _colorTipSettingsError is null ? message : message + " " + _colorTipSettingsError;
    }

    private object GetColorTipStatusLocked()
    {
        var now = MonotonicClock.UtcNow;
        var preview = _colorTipPreview;
        var profile = LearnedColorTipProfileLocked();
        bool fresh = preview is not null && EyeTipFrameFresh(preview.Frame, now);
        bool cameraReady = _cameraWanted && _camera.IsRunning && !_cameraHealthWarning && _camera.ActiveDeviceId == _eyeTipCameraId;
        bool confirmed = _eyeTipEnabled && !_colorTipLearning && cameraReady && fresh && preview!.Track.Confirmed;
        return new
        {
            mode = "color", enabled = _eyeTipEnabled, learning = _colorTipLearning, learned = profile is not null,
            running = _eyeTipEnabled && cameraReady && (profile is not null || _colorTipLearning),
            cameraDeviceId = _eyeTipCameraId, normalizedRadius = profile?.NormalizedRadius, profile,
            confirmed, tip = confirmed ? preview!.Track.Observation : null,
            cameraX = confirmed ? preview!.Track.Observation?.Center.X : null,
            cameraY = confirmed ? preview!.Track.Observation?.Center.Y : null,
            candidates = fresh ? preview!.Detection.Candidates : Array.Empty<ColorTipObservation>(),
            sourceFrameUtc = preview?.Frame.Timestamp, frameWidth = preview?.Frame.Width,
            frameHeight = preview?.Frame.Height, inferenceMilliseconds = preview?.InferenceMilliseconds,
            resultAgeMilliseconds = preview is null ? (double?)null : (now - preview.Frame.Timestamp).TotalMilliseconds,
            reason = _eyeTipReason, status = ColorTipStatusTextLocked(),
            settingsPath = ColorTipSettingsPath, settingsError = _colorTipSettingsError
        };
    }

    private void DrawColorTipPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        ColorTipPreview? preview;
        bool learning;
        lock (_eyeTipGate)
        {
            preview = _colorTipPreview;
            learning = _colorTipLearning;
            if (!_eyeTipEnabled || _frozenFrame is not null || preview is null ||
                preview.Frame.Width != frame.Width || preview.Frame.Height != frame.Height ||
                !EyeTipFrameFresh(preview.Frame, MonotonicClock.UtcNow)) return;
        }
        DrawColorTipMarkers(ds, preview, rect, learning);
    }

    private static void DrawColorTipMarkers(CanvasDrawingSession ds, ColorTipPreview preview, Rect rect, bool candidates)
    {
        Vector2 Point(ColorTipObservation tip) => new((float)(rect.X + tip.Center.X / preview.Frame.Width * rect.Width),
            (float)(rect.Y + tip.Center.Y / preview.Frame.Height * rect.Height));
        float Radius(ColorTipObservation tip) => Math.Max(5, (float)(tip.RadiusPixels / preview.Frame.Width * rect.Width) + 3);
        if (candidates) foreach (var candidate in preview.Detection.Candidates)
            ds.DrawCircle(Point(candidate), Radius(candidate), Colors.Gold, 1.5f);
        if (preview.Track.Observation is { } tip)
        {
            var center = Point(tip);
            float radius = Radius(tip);
            ds.DrawCircle(center, radius + 2, Colors.Black, 4);
            ds.DrawCircle(center, radius + 2, preview.Track.Confirmed ? Colors.Lime : Colors.Gold, 2);
            ds.FillCircle(center, 1.5f, preview.Track.Confirmed ? Colors.Lime : Colors.Gold);
        }
    }
}
