using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using ProjectTabletop.Interaction;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace ProjectTabletop.App.Camera;

/// <summary>
/// Captures the selected webcam through MediaFrameReader. It keeps only the latest owned
/// frame, so slow image analysis cannot build a stale queue of camera frames.
/// StartAsync should be called from the WinUI thread for camera consent.
/// </summary>
public sealed class CameraCaptureService : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _frameGate = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private CameraFrame? _latestFrame;
    private volatile bool _running;
    private volatile bool _disposed;
    private CameraCopyRateBudget _copyBudget = new(31.25);
    private long _epoch;
    // A wedged driver or USB connection can fail each format slowly. Bound repeated
    // slow failures, but keep searching after quick mode rejections so a working
    // lower-resolution format or another color source still gets a chance.
    private const int MaximumFormatFailures = 4;
    private static readonly TimeSpan FormatFailureTimeBudget = TimeSpan.FromSeconds(5);
    private volatile bool _captureEveryFrame;

    // During the brief setup-dot measurement, do not add the normal copy-rate cap
    // to a faster webcam's measured response. The latest-frame-only policy remains.
    public bool CaptureEveryFrame { get => _captureEveryFrame; set => _captureEveryFrame = value; }

    public event EventHandler<CameraFrame>? FrameReceived;
    public event EventHandler<string>? CaptureFailed;

    public CameraFrame? LatestFrame => Volatile.Read(ref _latestFrame);
    public string? ActiveDeviceId { get; private set; }
    public bool IsRunning => _running;
    public (int Width, int Height, double FramesPerSecond)? NegotiatedFormat { get; private set; }
    public int RequestedFramesPerSecond { get; private set; } = 30;
    public IReadOnlyList<int> SupportedRequestedFramesPerSecond { get; private set; } = Array.Empty<int>();
    public bool HasExclusiveFormatControl { get; private set; }
    public double NormalCopyFramesPerSecond => _copyBudget.CopiesPerSecond;

    public static async Task<IReadOnlyList<CameraDeviceInfo>> ListDevicesAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var devices = new List<CameraDeviceInfo>();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (HasColorVideo(group)) devices.Add(new CameraDeviceInfo(group.Id, group.DisplayName));
        }
        devices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName, b.DisplayName));
        return devices;
    }

    public async Task StartAsync(string id, int requestedFramesPerSecond = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!CameraFrameRatePolicy.IsRequestedRate(requestedFramesPerSecond))
            throw new ArgumentOutOfRangeException(nameof(requestedFramesPerSecond), "Choose 30, 60 or 100 fps.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync();
            RequestedFramesPerSecond = requestedFramesPerSecond;

            var groups = await MediaFrameSourceGroup.FindAllAsync();
            MediaFrameSourceGroup? group = null;
            for (var i = 0; i < groups.Count; i++)
                if (groups[i].Id == id) { group = groups[i]; break; }
            if (group is null)
                throw new InvalidOperationException("The selected webcam is no longer connected. Refresh the camera list.");
            if (!HasColorVideo(group))
                throw new InvalidOperationException("The selected webcam does not provide color video frames.");

            var capture = new MediaCapture();
            var hasExclusiveControl = true;
            _capture = capture;
            capture.Failed += OnCaptureFailed;
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl
                });
            }
            catch
            {
                hasExclusiveControl = false;
                capture.Failed -= OnCaptureFailed;
                capture.Dispose();
                _capture = new MediaCapture();
                capture = _capture;
                capture.Failed += OnCaptureFailed;
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly
                });
            }

            var sourceList = new List<MediaFrameSource>();
            for (var i = 0; i < group.SourceInfos.Count; i++)
            {
                var info = group.SourceInfos[i];
                if (IsColorVideo(info) && capture.FrameSources.TryGetValue(info.Id, out var candidate))
                    sourceList.Add(candidate);
            }
            if (sourceList.Count == 0)
                throw new InvalidOperationException("Windows did not expose a color frame source for this webcam.");

            HasExclusiveFormatControl = hasExclusiveControl;
            var formats = new List<SourceFormat>();
            foreach (var candidate in sourceList)
                for (var i = 0; i < candidate.SupportedFormats.Count; i++)
                    formats.Add(new SourceFormat(candidate, candidate.SupportedFormats[i]));
            SupportedRequestedFramesPerSecond = CameraFrameRatePolicy.SupportedRequestedRates(
                formats.Select(candidate => VideoMode(candidate.Format))
                    .Concat(sourceList.Select(candidate => VideoMode(candidate.CurrentFormat))));

            Exception? lastFailure = null;
            var started = false;
            var failures = 0;
            var failedFormatTime = TimeSpan.Zero;
            // The format attempts below change each source's format; remember the originals.
            var nativeFormats = sourceList.Select(source => new SourceFormat(source, source.CurrentFormat)).ToList();
            if (hasExclusiveControl)
            {
                // Search every color source, including separate preview/record streams.
                // A rejected advertised mode must not hide another usable 60/100 fps mode.
                foreach (var candidate in CameraFrameRatePolicy.Order(formats,
                    candidate => VideoMode(candidate.Format), requestedFramesPerSecond))
                {
                    if (failures >= MaximumFormatFailures && failedFormatTime >= FormatFailureTimeBudget) break;
                    var attemptStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        await candidate.Source.SetFormatAsync(candidate.Format);
                        var actual = VideoMode(candidate.Source.CurrentFormat);
                        if (!CameraFrameRatePolicy.IsUsable(actual) ||
                            Math.Abs(actual.FramesPerSecond - FramesPerSecond(candidate.Format)) >
                                FramesPerSecond(candidate.Format) * .01)
                            continue;
                        await StartReaderAsync(capture, candidate.Source);
                        started = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ex;
                        failures++;
                        failedFormatTime += Stopwatch.GetElapsedTime(attemptStarted);
                    }
                }
            }

            if (!started)
            {
                // SharedReadOnly cannot change formats. Under exclusive control no
                // attempted format worked, so restore each source's original native
                // format, which the attempts above replaced, and start with that.
                foreach (var native in CameraFrameRatePolicy.Order(nativeFormats,
                    native => VideoMode(native.Format), requestedFramesPerSecond))
                {
                    try
                    {
                        if (hasExclusiveControl)
                        {
                            try { await native.Source.SetFormatAsync(native.Format); }
                            // Some drivers reject format changes but can still stream
                            // their current mode. StartReader validates and reports it.
                            catch (Exception ex) { lastFailure = ex; }
                        }
                        await StartReaderAsync(capture, native.Source);
                        started = true;
                        break;
                    }
                    catch (Exception ex) { lastFailure = ex; }
                }
            }
            if (!started)
                throw new InvalidOperationException("The webcam could not provide usable color video. " +
                    "Select a camera with 720p–4K video at 20–100 fps, or close another app holding its format.", lastFailure);
            ActiveDeviceId = id;
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartReaderAsync(MediaCapture capture, MediaFrameSource source)
    {
        MediaFrameReader? reader = null;
        try
        {
            var actual = VideoMode(source.CurrentFormat);
            if (!CameraFrameRatePolicy.IsUsable(actual))
                throw new InvalidOperationException("The webcam changed to an unsupported video format.");
            _copyBudget = new CameraCopyRateBudget(CameraFrameRatePolicy.CopyRateLimit(
                RequestedFramesPerSecond, actual.FramesPerSecond));
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader = reader;
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            _running = true;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"Windows could not start the selected webcam ({status}).");
            // Always report the native format, even when fallback or another app prevented
            // the requested rate. CPU copy throttling is not a negotiated camera rate.
            actual = VideoMode(source.CurrentFormat);
            if (!CameraFrameRatePolicy.IsUsable(actual))
                throw new InvalidOperationException("The webcam changed to an unsupported video format on startup.");
            lock (_frameGate)
                _copyBudget = new CameraCopyRateBudget(CameraFrameRatePolicy.CopyRateLimit(
                    RequestedFramesPerSecond, actual.FramesPerSecond));
            NegotiatedFormat = (actual.Width, actual.Height, actual.FramesPerSecond);
        }
        catch
        {
            _running = false;
            Interlocked.Increment(ref _epoch);
            _reader = null;
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                try { await reader.StopAsync(); } catch { }
                lock (_frameGate) reader.Dispose();
            }
            NegotiatedFormat = null;
            Volatile.Write(ref _latestFrame, null);
            throw;
        }
    }

    public CameraFrame? CaptureSnapshot()
    {
        if (!_running) return null;
        var frame = LatestFrame;
        return !_running || frame is null ? null : frame with { Bgra = (byte[])frame.Bgra.Clone() };
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var arrivedAt = MonotonicClock.UtcNow;
        if (!Monitor.TryEnter(_frameGate)) return;
        CameraFrame? delivered = null;
        string? error = null;
        long epoch = 0;
        try
        {
            if (!_running || !ReferenceEquals(sender, _reader)) return;
            epoch = Interlocked.Read(ref _epoch);
            using var frame = sender.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;

            // A camera frame is copied to CPU memory for analysis; projector video stays on GPU.
            // Follow the selected/negotiated rate, allowing minor delivery jitter without
            // retaining more than two frames of credit. Calibration bypasses this budget.
            if (!_copyBudget.TryTake(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, _captureEveryFrame)) return;

            using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? null
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var bgraBitmap = converted ?? bitmap;
            var width = bgraBitmap.PixelWidth;
            var height = bgraBitmap.PixelHeight;
            var stride = checked(width * 4);
            // Copy once, straight into the owned array; an intermediate WinRT buffer
            // doubled the large allocations and copies at 60/100 fps.
            var bytes = new byte[checked(stride * height)];
            bgraBitmap.CopyToBuffer(bytes.AsBuffer());

            var owned = new CameraFrame(width, height, stride, bytes, MonotonicClock.UtcNow) { ArrivedAt = arrivedAt };
            if (_running && epoch == Interlocked.Read(ref _epoch) && ReferenceEquals(sender, _reader))
            {
                Volatile.Write(ref _latestFrame, owned);
                delivered = owned;
            }
        }
        catch (Exception ex)
        {
            error = "Webcam frame capture failed: " + ex.Message;
            _running = false;
            Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _latestFrame, null);
        }
        finally { Monitor.Exit(_frameGate); }
        if (delivered is not null && _running && epoch == Interlocked.Read(ref _epoch))
        {
            try { FrameReceived?.Invoke(this, delivered); }
            catch (Exception ex) { RaiseFailure("Camera frame consumer failed: " + ex.Message); }
        }
        if (error is not null) RaiseFailure(error);
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        if (ReferenceEquals(sender, _capture))
        {
            _running = false;
            Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _latestFrame, null);
            RaiseFailure("Webcam stopped or disconnected: " + args.Message);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _running = false;
        Interlocked.Increment(ref _epoch);
        ActiveDeviceId = null;
        NegotiatedFormat = null;
        SupportedRequestedFramesPerSecond = Array.Empty<int>();
        HasExclusiveFormatControl = false;
        var reader = _reader;
        _reader = null;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await reader.StopAsync(); }
            catch { /* Dispose still releases a driver that stopped unexpectedly. */ }
            lock (_frameGate) reader.Dispose();
        }
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.Failed -= OnCaptureFailed;
            capture.Dispose();
        }
        Volatile.Write(ref _latestFrame, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    private static bool IsColorVideo(MediaFrameSourceInfo info) =>
        info.SourceKind == MediaFrameSourceKind.Color &&
        info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;

    private static bool HasColorVideo(MediaFrameSourceGroup group)
    {
        for (var i = 0; i < group.SourceInfos.Count; i++)
            if (IsColorVideo(group.SourceInfos[i])) return true;
        return false;
    }

    private static double FramesPerSecond(MediaFrameFormat format) =>
        format.FrameRate.Denominator == 0 ? 0 :
        (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

    private static CameraVideoMode VideoMode(MediaFrameFormat format) =>
        new((int)format.VideoFormat.Width, (int)format.VideoFormat.Height, FramesPerSecond(format));

    private sealed record SourceFormat(MediaFrameSource Source, MediaFrameFormat Format);

    private void RaiseFailure(string message)
    {
        try { CaptureFailed?.Invoke(this, message); }
        catch { /* A UI notification failure must not escape a native camera callback. */ }
    }
}
