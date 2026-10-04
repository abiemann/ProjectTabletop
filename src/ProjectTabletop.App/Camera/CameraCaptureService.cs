using System.Diagnostics;
using ProjectTabletop.Interaction;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

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
    // About 30 copies per second on average, like the former 32 ms minimum gap.
    // Up to two copies of allowance carry over, so the on-time frame after a
    // late one still arrives from a 30 fps webcam.
    private const double MaximumCopiesPerSecond = 31.25, MaximumCopyAllowance = 2;
    private double _copyAllowance;
    private long _copyAllowanceTicks;
    private long _epoch;

    public event EventHandler<CameraFrame>? FrameReceived;
    public event EventHandler<string>? CaptureFailed;

    public CameraFrame? LatestFrame => Volatile.Read(ref _latestFrame);
    public string? ActiveDeviceId { get; private set; }
    public bool IsRunning => _running;
    public (int Width, int Height, double FramesPerSecond)? NegotiatedFormat { get; private set; }

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

    public async Task StartAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync();

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
            var source = sourceList
                .OrderByDescending(HasPreferredFormat)
                .ThenByDescending(value => (long)value.CurrentFormat.VideoFormat.Width * value.CurrentFormat.VideoFormat.Height)
                .FirstOrDefault();
            if (source is null)
                throw new InvalidOperationException("Windows did not expose a color frame source for this webcam.");

            if (hasExclusiveControl)
            {
                var supportedFormats = new List<MediaFrameFormat>();
                for (var i = 0; i < source.SupportedFormats.Count; i++)
                    supportedFormats.Add(source.SupportedFormats[i]);
                var preferred = supportedFormats
                    .Where(IsPreferredFormat)
                    .OrderByDescending(format => FramesPerSecond(format) >= 25)
                    .ThenByDescending(format => (long)format.VideoFormat.Width * format.VideoFormat.Height)
                    .ThenBy(format => Math.Abs(FramesPerSecond(format) - 30))
                    .FirstOrDefault();
                if (preferred is not null)
                {
                    try { await source.SetFormatAsync(preferred); }
                    catch { /* Keep the camera's current native format if its driver rejects a listed format. */ }
                }
            }
            var current = source.CurrentFormat;
            if (!IsUsableFormat(current))
                throw new InvalidOperationException($"The webcam supplied {current.VideoFormat.Width} × {current.VideoFormat.Height} at {FramesPerSecond(current):0.#} fps. Select a camera with at least 720p at 20 fps, or close another app holding its format.");
            NegotiatedFormat = ((int)current.VideoFormat.Width, (int)current.VideoFormat.Height,
                FramesPerSecond(current));

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader = reader;
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            _running = true;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"Windows could not start the selected webcam ({status}).");
            ActiveDeviceId = id;
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public CameraFrame? CaptureSnapshot()
    {
        if (!_running) return null;
        var frame = LatestFrame;
        return !_running || frame is null ? null : frame with { Bgra = (byte[])frame.Bgra.Clone() };
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
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
            // The average 30 Hz cap bounds allocations when a webcam advertises 60 Hz or more.
            // A fixed minimum gap instead dropped the on-time frame after each late one.
            var now = Stopwatch.GetTimestamp();
            _copyAllowance = _copyAllowanceTicks == 0 ? MaximumCopyAllowance : Math.Min(MaximumCopyAllowance,
                _copyAllowance + Stopwatch.GetElapsedTime(_copyAllowanceTicks, now).TotalSeconds * MaximumCopiesPerSecond);
            _copyAllowanceTicks = now;
            if (_copyAllowance < 1) return;

            using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? null
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var bgraBitmap = converted ?? bitmap;
            var width = bgraBitmap.PixelWidth;
            var height = bgraBitmap.PixelHeight;
            var stride = checked(width * 4);
            var bytes = new byte[checked(stride * height)];
            var buffer = new Windows.Storage.Streams.Buffer((uint)bytes.Length);
            bgraBitmap.CopyToBuffer(buffer);
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);

            var owned = new CameraFrame(width, height, stride, bytes, MonotonicClock.UtcNow);
            if (_running && epoch == Interlocked.Read(ref _epoch) && ReferenceEquals(sender, _reader))
            {
                Volatile.Write(ref _latestFrame, owned);
                _copyAllowance--;
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
        _copyAllowanceTicks = 0;
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

    private static bool HasPreferredFormat(MediaFrameSource source)
    {
        for (var i = 0; i < source.SupportedFormats.Count; i++)
            if (IsPreferredFormat(source.SupportedFormats[i])) return true;
        return false;
    }

    private static double FramesPerSecond(MediaFrameFormat format) =>
        format.FrameRate.Denominator == 0 ? 0 :
        (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

    private static bool IsPreferredFormat(MediaFrameFormat format) =>
        format.VideoFormat.Width is >= 1280 and <= 1920 &&
        format.VideoFormat.Height is >= 720 and <= 1080 &&
        FramesPerSecond(format) is >= 20 and <= 60;

    private static bool IsUsableFormat(MediaFrameFormat format) =>
        format.VideoFormat.Width >= 1280 && format.VideoFormat.Height >= 720 &&
        format.VideoFormat.Width <= 3840 && format.VideoFormat.Height <= 2160 &&
        FramesPerSecond(format) is >= 20 and <= 60;

    private void RaiseFailure(string message)
    {
        try { CaptureFailed?.Invoke(this, message); }
        catch { /* A UI notification failure must not escape a native camera callback. */ }
    }
}
