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

// Shared camera-side tracking for the double-bar marker used by Water Garden and Paint.
public sealed partial class MainWindow
{
    private static readonly TimeSpan MarkerObservationLifetime = TimeSpan.FromMilliseconds(250);
    private readonly object _markerTrackingGate = new();
    private readonly SemaphoreSlim _markerTrackingDetectorGate = new(1, 1);
    private readonly FrameWorker _markerTrackingWorker = new();
    private readonly CoalescedDispatch _markerTrackingUiRefresh = new();
    private DispatcherQueueTimer? _markerTrackingTimer;
    private bool _markerTrackingEnabled;
    private string? _markerTrackingCameraId;
    private string _markerTrackingReason = "tracking-off";
    private string _markerTrackingStatus = "Off. Use Marker setup to train the double-bar marker.";
    private long _markerTrackingGeneration;
    private long _markerTrackingSceneRevision = -1;
    private BoardScreen _markerTrackingSceneBoard;
    private DateTimeOffset _markerTrackingNotBefore;

    private bool IsLearningDoubleBarMarker { get { lock (_markerTrackingGate) return _wandMarkerLearning; } }

    private void InitializeMarkerTracking()
    {
        InitializeWandMarkerTracking();
        // Remember the measured marker, but require a deliberate opt-in each launch.
        _markerTrackingTimer = DispatcherQueue.CreateTimer();
        _markerTrackingTimer.Interval = TimeSpan.FromMilliseconds(100);
        _markerTrackingTimer.Tick += (_, _) =>
        {
            bool expired = false;
            lock (_markerTrackingGate)
            {
                if (_wandMarkerPreview is { } markerPreview && !MarkerFrameFresh(markerPreview.Frame, MonotonicClock.UtcNow))
                {
                    SetWandMarkerPreviewLocked(null);
                    // Release stale pixels and stop marker input at 250 ms, but
                    // preserve the tracker's separate 750 ms identity window.
                    // Only a new measured pose can publish input again.
                    _markerTrackingReason = "waiting-for-fresh-camera-frame";
                    PublishStickTipLocked();
                    expired = true;
                }
            }
            UpdateMarkerStatus();
            if (expired && !_closing) CameraCanvas.Invalidate();
        };
        _markerTrackingTimer.Start();
        UpdateMarkerStatus();
    }

    private static bool MarkerFrameFresh(CameraFrame frame, DateTimeOffset now) =>
        frame.Timestamp <= now + TimeSpan.FromMilliseconds(30) && now - frame.Timestamp <= MarkerObservationLifetime;

    private void ResetMarkerTracking(string reason, string? cameraId = null)
    {
        lock (_markerTrackingGate)
        {
            _markerTrackingGeneration++;
            _scene.ResetControlSwipeMarkerChecks();
            _markerTrackingSceneBoard = _scene.CurrentBoardScreen;
            _markerTrackingSceneRevision = CurrentWandMarkerInputRevision;
            _markerTrackingNotBefore = MonotonicClock.UtcNow;
            ResetWandMarkerTrackingLocked();
            if (cameraId is not null) _markerTrackingCameraId = cameraId;
            _markerTrackingReason = reason;
            PublishStickTipLocked();
        }
        QueueMarkerStatusRefresh();
    }

    private void SetStickTrackingEnabled(bool enabled)
    {
        lock (_markerTrackingGate) _markerTrackingEnabled = enabled;
        ResetMarkerTracking(enabled ? "searching-for-double-bar-marker" : "tracking-off");
        UpdateMarkerStatus();
    }

    private void QueueMarkerDetection(CameraFrame frame)
    {
        lock (_markerTrackingGate)
        {
            var board = _scene.CurrentBoardScreen;
            long sceneRevision = CurrentWandMarkerInputRevision;
            if (board != _markerTrackingSceneBoard || sceneRevision != _markerTrackingSceneRevision)
            {
                // Disabled tracking has no association to invalidate. Preserve
                // the queued board-entry opt-in generation until it can run.
                if (_markerTrackingEnabled) ResetMarkerTracking("board-registration-changed");
                else { _markerTrackingSceneBoard = board; _markerTrackingSceneRevision = sceneRevision; }
            }
            var profile = LearnedWandMarkerProfileLocked();
            if (_closing || !_markerTrackingEnabled || _markerTrackingWorker.IsBusy || _wandMarkerLearning ||
                profile is null || !WandMarkerBoardSelected ||
                _markerTrackingCameraId != _cameraWantedDeviceId || frame.Timestamp < _markerTrackingNotBefore ||
                !MarkerFrameFresh(frame, MonotonicClock.UtcNow) || _cameraHealthWarning) return;
            if (_wandMarkerFrameSize is { } previousSize && previousSize != (frame.Width, frame.Height))
            {
                // Preview expiry releases its frame lease; keep the dimension
                // guard independently so old pixel coordinates cannot survive
                // a capture-format change while that preview is absent.
                _wandMarkerTracker.Reset();
                SetWandMarkerPreviewLocked(null);
                PublishStickTipLocked();
            }
            // The single worker drops arrivals while busy; no second rate cap
            // should discard fresh 60/100 fps marker positions.
            long generation = _markerTrackingGeneration;
            var hint = _wandMarkerTracker.GetSearchHint(frame.Timestamp);
            _markerTrackingWorker.TryRun(Stopwatch.GetTimestamp(), TimeSpan.Zero, frame, async _ =>
            {
                try
                {
                    await _markerTrackingDetectorGate.WaitAsync();
                    MarkerTipDetectionResult detection;
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        var area = _scene.GetWandMarkerCameraPolygon();
                        detection = area is null ? new([], "waiting-for-projected-bat-reference") :
                            BlackTipDetector.DetectEach(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                                [profile], new(ProjectionFrames: StickTipProjectionReference(frame), FrameTime: frame.Timestamp),
                                [hint], area)[0];
                    }
                    finally { _markerTrackingDetectorGate.Release(); }
                    PublishWandMarkerDetection(frame, detection, profile, hint,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, generation);
                }
                catch (Exception ex)
                {
                    lock (_markerTrackingGate)
                    {
                        if (generation != _markerTrackingGeneration) return;
                        SetWandMarkerPreviewLocked(null);
                        _wandMarkerTracker.Reset();
                        _markerTrackingReason = "Detection failed: " + ex.Message;
                        PublishStickTipLocked();
                    }
                }
                finally
                {
                    QueueMarkerStatusRefresh();
                }
            });
        }
    }

    private void QueueMarkerStatusRefresh()
    {
        if (_closing) return;
        _markerTrackingUiRefresh.TryPost(_dispatchInputResult, () =>
        {
            if (_closing) return;
            // Read the latest state, including a reset or camera change
            // since this callback was queued. Scene input is published on
            // the worker immediately; only laptop updates are coalesced.
            UpdateMarkerStatus();
            RequestCameraPreviewRedraw();
        });
    }

    private object GetStickTipStatus()
    {
        lock (_markerTrackingGate) return GetWandMarkerStatusLocked();
    }

    private void UpdateMarkerStatus()
    {
        lock (_markerTrackingGate) _markerTrackingStatus = WandMarkerStatusTextLocked();
    }

    private void DrawMarkerPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect) =>
        DrawWandMarkerPreview(ds, frame, rect);

    // Gold candidates, then the associated marker, lime once confirmed across fresh frames.
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
    private IReadOnlyList<MarkerProjectionFrame>? StickTipProjectionReference(CameraFrame frame, bool learning = false)
    {
        if (!_scene.HasBoardMediaClip || _scene.CurrentBoardScreen == BoardScreen.Media) return null;
        var references = _scene.GetMarkerProjectionFrames();
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
        WandMarkerPreview? markerPreview;
        CameraFrameLease? selectedLease;
        lock (_markerTrackingGate)
        {
            markerPreview = _markerTrackingEnabled && LearnedWandMarkerProfileLocked() is not null &&
                _wandMarkerPreview is { } current && MarkerFrameFresh(current.Frame, MonotonicClock.UtcNow) ? current : null;
            selectedLease = markerPreview?.Frame.Retain();
        }
        using var frameLease = selectedLease ?? AcquireLatestCameraFrame() ??
            throw new InvalidOperationException("Start the webcam before capturing the double-bar marker.");
        var frame = frameLease.Frame;
        var status = GetStickTipStatus();
        string directory = Path.Combine(_appDataDirectory, "StickTipSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"stick-tip-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), frame.Width, frame.Height, 96);
        using var bitmap = CanvasBitmap.CreateFromBytes(target, frame.Bgra, frame.Width, frame.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
        string? rawPath = null;
        if (markerPreview is not null)
        {
            // Save the exact inferred frame for replay; a later raw-frame command
            // can already contain different projected texture on the marker.
            rawPath = Path.ChangeExtension(path, ".raw.png");
            await bitmap.SaveAsync(rawPath, CanvasBitmapFileFormat.Png);
        }
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(bitmap);
            if (markerPreview is not null) DrawWandMarkerMarkers(ds, markerPreview, new(0, 0, frame.Width, frame.Height), candidates: true);
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            sourceFrameUtc = frame.Timestamp, capturedAtUtc = MonotonicClock.UtcNow,
            frame.Width, frame.Height, frame.Stride, rawPath, searchHint = markerPreview?.SearchHint,
            detection = markerPreview?.Detection, observation = markerPreview?.Track, status
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new { path, rawPath, sourceFrameUtc = frame.Timestamp, status };
    }

    private async Task DisposeMarkerTrackingAsync()
    {
        _markerTrackingTimer?.Stop();
        ResetMarkerTracking("tracking-off");
        lock (_markerTrackingGate) _markerTrackingEnabled = false;
        await _markerTrackingWorker.WorkerTask;
    }
}
