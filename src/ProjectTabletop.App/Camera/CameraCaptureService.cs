using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
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
    private ExclusiveProcessLease? _deviceLease;
    private readonly CameraFrameSlot _latestFrame = new();
    private CameraFramePool? _framePool;
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
    // Lifetime diagnostics survive Stop so a stalled stream can be inspected after
    // its explicit shutdown. Only counters/timestamps change on the callback path.
    private enum CaptureStage { Idle, Acquire, Convert, Allocate, Copy, Publish, Dispose }
    private readonly long[] _lastStageTicks = new long[7], _maximumStageTicks = new long[7];
    private int _captureStage, _activeCallbacks, _activeConsumers, _maximumActiveConsumers;
    private long _stageStarted, _arrivals, _acquired, _copied, _published, _consumerStarted, _consumerCompleted;
    private long _busyDrops, _rateDrops, _emptyFrames, _obsoleteDrops, _captureErrors, _consumerErrors, _nativeFailures;
    private long _readerStarts, _readerStartedUtc, _lastArrivalUtc, _lastAcquiredUtc, _lastCopiedUtc,
        _lastPublishedUtc, _lastConsumerStartedUtc, _lastConsumerCompletedUtc, _lastConsumerStartedTick;
    private long _lastConsumerTicks, _maximumConsumerTicks;
    private string? _readerOutputSubtype;
    private int _sourceBitmapPixelFormat = -1;

    public sealed record CaptureStageTiming(string Stage, double LastMilliseconds, double MaximumMilliseconds);
    public sealed record CaptureDiagnostics(long Epoch, long ReaderStarts, DateTimeOffset? LastReaderStartedUtc,
        long Arrivals, long Acquired, long Copied, long Published, long ConsumerStarted, long ConsumerCompleted,
        long BusyDrops, long RateDrops, long EmptyFrames, long ObsoleteDrops, long CaptureErrors, long ConsumerErrors, long NativeFailures,
        DateTimeOffset? LastFrameArrivedUtc, DateTimeOffset? LastAcquiredUtc, DateTimeOffset? LastCopiedUtc,
        DateTimeOffset? LastPublishedUtc, DateTimeOffset? LastConsumerStartedUtc, DateTimeOffset? LastConsumerCompletedUtc,
        DateTimeOffset? LatestFrameUtc, int ActiveCallbacks, int ActiveConsumers, int MaximumActiveConsumers,
        string NativeStage, double NativeStageElapsedMilliseconds, double LatestConsumerElapsedMilliseconds,
        double LastConsumerMilliseconds, double MaximumConsumerMilliseconds, IReadOnlyList<CaptureStageTiming> NativeTimings,
        string? ReaderOutputSubtype, string? SourceBitmapPixelFormat)
    {
        public CameraFramePoolDiagnostics? FramePool { get; init; }
    }

    /// <summary>Nonblocking approximate snapshot; never queries the driver or takes the frame/lifecycle gates.</summary>
    public CaptureDiagnostics GetDiagnostics()
    {
        long tick = Stopwatch.GetTimestamp(), stageStarted = Interlocked.Read(ref _stageStarted);
        int stage = Volatile.Read(ref _captureStage), consumers = Volatile.Read(ref _activeConsumers);
        int sourcePixelFormat = Volatile.Read(ref _sourceBitmapPixelFormat);
        long consumerStarted = Interlocked.Read(ref _lastConsumerStartedTick);
        var timings = new CaptureStageTiming[_lastStageTicks.Length - 1];
        for (int i = 1; i < _lastStageTicks.Length; i++)
            timings[i - 1] = new(((CaptureStage)i).ToString(), Milliseconds(Interlocked.Read(ref _lastStageTicks[i])),
                Milliseconds(Interlocked.Read(ref _maximumStageTicks[i])));
        return new(Interlocked.Read(ref _epoch), Interlocked.Read(ref _readerStarts), Utc(ref _readerStartedUtc),
            Interlocked.Read(ref _arrivals), Interlocked.Read(ref _acquired), Interlocked.Read(ref _copied),
            Interlocked.Read(ref _published), Interlocked.Read(ref _consumerStarted), Interlocked.Read(ref _consumerCompleted),
            Interlocked.Read(ref _busyDrops), Interlocked.Read(ref _rateDrops), Interlocked.Read(ref _emptyFrames),
            Interlocked.Read(ref _obsoleteDrops), Interlocked.Read(ref _captureErrors), Interlocked.Read(ref _consumerErrors),
            Interlocked.Read(ref _nativeFailures), Utc(ref _lastArrivalUtc), Utc(ref _lastAcquiredUtc), Utc(ref _lastCopiedUtc),
            Utc(ref _lastPublishedUtc), Utc(ref _lastConsumerStartedUtc), Utc(ref _lastConsumerCompletedUtc), LatestFrame?.Timestamp,
            Volatile.Read(ref _activeCallbacks), consumers, Volatile.Read(ref _maximumActiveConsumers),
            ((CaptureStage)stage).ToString(), stage == 0 || stageStarted == 0 ? 0 : Milliseconds(tick - stageStarted),
            consumers == 0 || consumerStarted == 0 ? 0 : Milliseconds(tick - consumerStarted),
            Milliseconds(Interlocked.Read(ref _lastConsumerTicks)), Milliseconds(Interlocked.Read(ref _maximumConsumerTicks)), timings,
            Volatile.Read(ref _readerOutputSubtype), sourcePixelFormat < 0 ? null : ((BitmapPixelFormat)sourcePixelFormat).ToString())
        { FramePool = Volatile.Read(ref _framePool)?.GetDiagnostics() };

        static DateTimeOffset? Utc(ref long value) => Interlocked.Read(ref value) is var ticks && ticks != 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    private static double Milliseconds(long ticks) => Math.Max(0, ticks) * 1000d / Stopwatch.Frequency;

    private void SetCaptureStage(CaptureStage stage)
    {
        long now = Stopwatch.GetTimestamp();
        int previous = _captureStage;
        if (previous != 0)
        {
            long elapsed = now - _stageStarted;
            Interlocked.Exchange(ref _lastStageTicks[previous], elapsed);
            UpdateMaximum(ref _maximumStageTicks[previous], elapsed);
        }
        Interlocked.Exchange(ref _stageStarted, stage == CaptureStage.Idle ? 0 : now);
        Volatile.Write(ref _captureStage, (int)stage);
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        long observed = Interlocked.Read(ref maximum);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed) break;
            observed = previous;
        }
    }

    // During the brief setup-dot measurement, do not add the normal copy-rate cap
    // to a faster webcam's measured response. The latest-frame-only policy remains.
    public bool CaptureEveryFrame { get => _captureEveryFrame; set => _captureEveryFrame = value; }

    public event EventHandler<CameraFrame>? FrameReceived;
    public event EventHandler<string>? CaptureFailed;

    /// <summary>Borrowed metadata only. AcquireLatestFrame keeps pixels valid while they are being read.</summary>
    public CameraFrame? LatestFrame => _latestFrame.Frame;
    public CameraFrameLease? AcquireLatestFrame() => _latestFrame.Acquire();
    public string? ActiveDeviceId { get; private set; }
    public bool IsRunning => _running;
    public (int Width, int Height, double FramesPerSecond)? NegotiatedFormat { get; private set; }
    public int RequestedFramesPerSecond { get; private set; } = 30;
    public string? NegotiatedSubtype { get; private set; }
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
            // Stall recovery is manual (Stop, then Start), so a running camera with the
            // same device and rate is never restarted by a repeated request.
            if (_running && StringComparer.OrdinalIgnoreCase.Equals(ActiveDeviceId, id) &&
                RequestedFramesPerSecond == requestedFramesPerSecond) return;
            await StopCoreAsync();
            RequestedFramesPerSecond = requestedFramesPerSecond;

            // Claim the device before enumeration or any WinRT capture initialization.
            // The handle remains held until reader and capture disposal have completed.
            try
            {
                _deviceLease = ExclusiveProcessLease.TryAcquire(DeviceLeaseName(id))
                    ?? throw new CameraAccessBlockedException(
                        "This webcam is already open in Project Tabletop. Close its other instance, then press Start camera.");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new CameraAccessBlockedException(
                    "The webcam safety lock is unavailable. Close other Tabletop sessions, then press Start camera to retry.", ex);
            }

            var groups = await MediaFrameSourceGroup.FindAllAsync();
            MediaFrameSourceGroup? group = null;
            for (var i = 0; i < groups.Count; i++)
                if (groups[i].Id == id) { group = groups[i]; break; }
            if (group is null)
                throw new InvalidOperationException("The selected webcam is no longer connected. Refresh the camera list.");
            if (!HasColorVideo(group))
                throw new InvalidOperationException("The selected webcam does not provide color video frames.");

            var capture = new MediaCapture();
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
            catch (Exception ex)
            {
                // Never retry by opening a shared stream on a busy or failing driver.
                throw new CameraAccessBlockedException(
                    "The webcam could not be opened exclusively. Close other apps using it and check camera access, " +
                    "then press Start camera to retry. " + ex.Message, ex);
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

            HasExclusiveFormatControl = true;
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
                catch (Exception ex) when (!CameraAccessBlockedException.IsContention(ex))
                {
                    lastFailure = ex;
                    failures++;
                    failedFormatTime += Stopwatch.GetElapsedTime(attemptStarted);
                }
            }

            if (!started)
            {
                // No attempted format worked, so restore each source's original native
                // format, which the attempts above replaced, and start with that.
                foreach (var native in CameraFrameRatePolicy.Order(nativeFormats,
                    native => VideoMode(native.Format), requestedFramesPerSecond))
                {
                    try
                    {
                        try { await native.Source.SetFormatAsync(native.Format); }
                        // Some drivers reject format changes but can still stream
                        // their current mode. StartReader validates and reports it.
                        catch (Exception ex) when (!CameraAccessBlockedException.IsContention(ex)) { lastFailure = ex; }
                        await StartReaderAsync(capture, native.Source);
                        started = true;
                        break;
                    }
                    catch (Exception ex) when (!CameraAccessBlockedException.IsContention(ex)) { lastFailure = ex; }
                }
            }
            if (!started)
                throw new InvalidOperationException("The webcam could not provide usable color video. " +
                    "Select a camera with 720p–4K video at 20–100 fps, or close another app holding its format.", lastFailure);
            ActiveDeviceId = id;
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            if (ex is not CameraAccessBlockedException && CameraAccessBlockedException.IsContention(ex))
                throw new CameraAccessBlockedException(
                    "The webcam is busy in another app. Close that app, then press Start camera to retry.", ex);
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
            _framePool?.Dispose();
            _framePool = new CameraFramePool(actual.Width, actual.Height);
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            Volatile.Write(ref _readerOutputSubtype, MediaEncodingSubtypes.Bgra8);
            Volatile.Write(ref _sourceBitmapPixelFormat, -1);
            _reader = reader;
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            _running = true;
            var status = await reader.StartAsync();
            if (status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable)
                throw new CameraAccessBlockedException(
                    "Another app controls this webcam. Close it, then press Start camera to retry.");
            if (status == MediaFrameReaderStartStatus.DeviceNotAvailable)
                throw new CameraAccessBlockedException(
                    "The webcam became unavailable. Check its connection, then press Start camera to retry.");
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"Windows could not start the selected webcam ({status}).");
            Interlocked.Increment(ref _readerStarts);
            Interlocked.Exchange(ref _readerStartedUtc, MonotonicClock.UtcNow.UtcTicks);
            // Always report the native format, even when format fallback prevented
            // the requested rate. CPU copy throttling is not a negotiated camera rate.
            actual = VideoMode(source.CurrentFormat);
            if (!CameraFrameRatePolicy.IsUsable(actual))
                throw new InvalidOperationException("The webcam changed to an unsupported video format on startup.");
            lock (_frameGate)
                _copyBudget = new CameraCopyRateBudget(CameraFrameRatePolicy.CopyRateLimit(
                    RequestedFramesPerSecond, actual.FramesPerSecond));
            NegotiatedFormat = (actual.Width, actual.Height, actual.FramesPerSecond);
            NegotiatedSubtype = source.CurrentFormat.Subtype;
        }
        catch
        {
            _running = false;
            Interlocked.Increment(ref _epoch);
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                try { await reader.StopAsync(); } catch { }
                lock (_frameGate) reader.Dispose();
            }
            _reader = null;
            NegotiatedFormat = null;
            NegotiatedSubtype = null;
            _latestFrame.Clear();
            _framePool?.Dispose();
            throw;
        }
    }

    public CameraFrame? CaptureSnapshot()
    {
        if (!_running) return null;
        using var lease = AcquireLatestFrame();
        if (!_running || lease is null) return null;
        var frame = lease.Frame;
        return new CameraFrame(frame.Width, frame.Height, frame.Stride, (byte[])frame.Bgra.Clone(), frame.Timestamp)
        { ArrivedAt = frame.ArrivedAt };
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var arrivedAt = MonotonicClock.UtcNow;
        Interlocked.Increment(ref _arrivals);
        Interlocked.Exchange(ref _lastArrivalUtc, arrivedAt.UtcTicks);
        Interlocked.Increment(ref _activeCallbacks);
        CameraFrameLease? deliveredLease = null;
        try
        {
            if (!Monitor.TryEnter(_frameGate)) { Interlocked.Increment(ref _busyDrops); return; }
            CameraFrame? delivered = null;
            string? error = null;
            long epoch = 0;
            try
            {
                if (!_running || !ReferenceEquals(sender, _reader)) { Interlocked.Increment(ref _obsoleteDrops); return; }
                epoch = Interlocked.Read(ref _epoch);
                SetCaptureStage(CaptureStage.Acquire);
                using (var frame = sender.TryAcquireLatestFrame())
                using (var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap)
                {
                    SetCaptureStage(CaptureStage.Idle);
                    try
                    {
                        if (bitmap is null) { Interlocked.Increment(ref _emptyFrames); return; }
                        Interlocked.Increment(ref _acquired);
                        Interlocked.Exchange(ref _lastAcquiredUtc, MonotonicClock.UtcNow.UtcTicks);

                        // Keep the existing selected/negotiated copy budget; setup bypasses it.
                        if (!_copyBudget.TryTake(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, _captureEveryFrame))
                        { Interlocked.Increment(ref _rateDrops); return; }

                        SetCaptureStage(CaptureStage.Convert);
                        var sourcePixelFormat = bitmap.BitmapPixelFormat;
                        Volatile.Write(ref _sourceBitmapPixelFormat, (int)sourcePixelFormat);
                        using (var converted = sourcePixelFormat == BitmapPixelFormat.Bgra8
                            ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore))
                        {
                            try
                            {
                                SetCaptureStage(CaptureStage.Allocate);
                                var bgraBitmap = converted ?? bitmap;
                                var width = bgraBitmap.PixelWidth;
                                var height = bgraBitmap.PixelHeight;
                                var pool = _framePool;
                                if (pool is null || pool.Width != width || pool.Height != height)
                                    throw new InvalidOperationException("The webcam changed frame dimensions. Stop and restart the camera.");
                                // Every consumer owns a lease. Exhausted storage drops this arrival;
                                // it never overwrites an in-flight frame or allocates an extra large array.
                                using var buffer = pool.TryRent();
                                if (buffer is null) return;
                                SetCaptureStage(CaptureStage.Copy);
                                bgraBitmap.CopyToBuffer(buffer.Bytes.AsBuffer());
                                var copiedAt = MonotonicClock.UtcNow;
                                Interlocked.Increment(ref _copied);
                                Interlocked.Exchange(ref _lastCopiedUtc, copiedAt.UtcTicks);
                                SetCaptureStage(CaptureStage.Publish);
                                deliveredLease = buffer.Publish(copiedAt, arrivedAt);
                                var owned = deliveredLease.Frame;
                                if (_running && epoch == Interlocked.Read(ref _epoch) && ReferenceEquals(sender, _reader))
                                {
                                    _latestFrame.Set(owned);
                                    delivered = owned;
                                    Interlocked.Increment(ref _published);
                                    Interlocked.Exchange(ref _lastPublishedUtc, copiedAt.UtcTicks);
                                }
                                else Interlocked.Increment(ref _obsoleteDrops);
                            }
                            finally { SetCaptureStage(CaptureStage.Dispose); }
                        }
                    }
                    finally
                    {
                        // Include converted, source-bitmap and frame-reference release.
                        if (_captureStage != (int)CaptureStage.Dispose) SetCaptureStage(CaptureStage.Dispose);
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _captureErrors);
                error = "Webcam frame capture failed: " + ex.Message;
                _running = false;
                Interlocked.Increment(ref _epoch);
                _latestFrame.Clear();
            }
            finally { SetCaptureStage(CaptureStage.Idle); Monitor.Exit(_frameGate); }
            if (delivered is not null && _running && epoch == Interlocked.Read(ref _epoch))
            {
                long started = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _lastConsumerStartedTick, started);
                Interlocked.Exchange(ref _lastConsumerStartedUtc, MonotonicClock.UtcNow.UtcTicks);
                Interlocked.Increment(ref _consumerStarted);
                int active = Interlocked.Increment(ref _activeConsumers), maximum = Volatile.Read(ref _maximumActiveConsumers);
                while (active > maximum)
                {
                    int previous = Interlocked.CompareExchange(ref _maximumActiveConsumers, active, maximum);
                    if (previous == maximum) break;
                    maximum = previous;
                }
                try { FrameReceived?.Invoke(this, delivered); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _consumerErrors);
                    RaiseFailure("Camera frame consumer failed: " + ex.Message);
                }
                finally
                {
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    Interlocked.Exchange(ref _lastConsumerTicks, elapsed);
                    UpdateMaximum(ref _maximumConsumerTicks, elapsed);
                    Interlocked.Exchange(ref _lastConsumerCompletedUtc, MonotonicClock.UtcNow.UtcTicks);
                    Interlocked.Increment(ref _consumerCompleted);
                    Interlocked.Decrement(ref _activeConsumers);
                }
            }
            if (error is not null) RaiseFailure(error);
        }
        finally
        {
            deliveredLease?.Dispose();
            Interlocked.Decrement(ref _activeCallbacks);
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        if (ReferenceEquals(sender, _capture))
        {
            Interlocked.Increment(ref _nativeFailures);
            _running = false;
            Interlocked.Increment(ref _epoch);
            _latestFrame.Clear();
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
        NegotiatedSubtype = null;
        SupportedRequestedFramesPerSecond = Array.Empty<int>();
        HasExclusiveFormatControl = false;
        var reader = _reader;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await reader.StopAsync(); }
            catch { /* Dispose still releases a driver that stopped unexpectedly. */ }
            lock (_frameGate) reader.Dispose();
            _reader = null;
        }
        var capture = _capture;
        if (capture is not null)
        {
            capture.Failed -= OnCaptureFailed;
            capture.Dispose();
            _capture = null;
        }
        _deviceLease?.Dispose();
        _deviceLease = null;
        _latestFrame.Clear();
        _framePool?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    internal static string DeviceLeaseName(string id) => @"Global\ProjectTabletop.Camera." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToUpperInvariant())));

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
