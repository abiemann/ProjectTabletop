#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using ProjectTabletop.App.Camera;
using Windows.Graphics.Imaging;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyCameraSafetyAsync()
    {
        if (_camera.IsRunning || _camera.ActiveDeviceId is not null || _cameraWanted || _cameraOperation.CurrentCount == 0 || _closing)
            throw new InvalidOperationException("Stop the camera before checking camera safety.");
        // The latch check below drives the real outage path, which suspends a scan and
        // clears the board clip. Run it only when there is no live session state to lose.
        if (Volatile.Read(ref _boardSetupActive) || _scene.HasBoardMediaClip)
            throw new InvalidOperationException("Stop board setup before checking camera safety.");
        if (SelectedCamera is null)
            throw new InvalidOperationException("Select a camera before checking queued camera-start cancellation.");
        var timeout = TimeSpan.FromSeconds(5);
        object readerConversion = await VerifyCameraReaderConversionAsync();

        // The real app already owns this lease. Dispose a surprise acquisition even
        // when the assertion fails, without starting another app or touching USB.
        using (var contender = ExclusiveProcessLease.TryAcquire(AppInstanceGuard.LeaseName))
            Require(contender is null, "The running app did not retain its process-lifetime instance lease.");

        string fakeDevice = "camera-safety-verification:" + Guid.NewGuid().ToString("N");
        using (var owner = ExclusiveProcessLease.TryAcquire(CameraCaptureService.DeviceLeaseName(fakeDevice))
            ?? throw new InvalidOperationException("Could not acquire the synthetic camera lease."))
        {
            var camera = new CameraCaptureService();
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    bool blocked = false;
                    try { await camera.StartAsync(fakeDevice).WaitAsync(timeout); }
                    catch (CameraAccessBlockedException) { blocked = true; }
                    Require(blocked, "An occupied camera lease did not reject startup before device enumeration.");
                    Require(!camera.IsRunning && camera.ActiveDeviceId is null && camera.LatestFrame is null &&
                        camera.NegotiatedFormat is null,
                        "A blocked camera start retained active capture state.");
                }
            }
            finally { await camera.DisposeAsync().AsTask().WaitAsync(timeout); }
        }

        // A stopped capture must latch a manual recovery request. Health ticks
        // and another Start cannot schedule any driver work while it is latched.
        long healthVersion = _cameraOperationVersion;
        try
        {
            _cameraWanted = true;
            _cameraWantedDeviceId = SelectedCamera.Device.Id;
            UpdateCameraHealth();
            string stalledStatus = CameraStatusText.Text;
            Require(_cameraManualRestartRequired && _cameraHealthWarning,
                "A stopped capture did not pause input for manual recovery.");
            for (int tick = 0; tick < 20; tick++) UpdateCameraHealth();
            Require(_cameraOperationVersion == healthVersion && _cameraOperation.CurrentCount == 1 &&
                CameraStatusText.Text == stalledStatus && !_camera.IsRunning,
                "Repeated camera health checks changed the recovery request or scheduled capture work.");
            Require(!await StartSelectedCameraAsync().WaitAsync(timeout) && _cameraOperationVersion == healthVersion &&
                _cameraOperation.CurrentCount == 1,
                "Start bypassed the manual Stop required after a camera outage.");
        }
        finally { await StopCameraAsync().WaitAsync(timeout); }
        Require(!_cameraManualRestartRequired && !_cameraHealthWarning,
            "Explicit Stop did not clear the manual recovery latch.");

        // Hold the actual UI operation gate so neither start can reach the service.
        // Stop must invalidate them before this gate is released, including on failure.
        if (!await _cameraOperation.WaitAsync(timeout))
            throw new TimeoutException("The camera operation gate did not become available.");
        Task<bool>? first = null, second = null;
        Task? stop = null;
        bool sameTask = false, sameVersion = false, cancelledBeforeRelease = false;
        bool firstStarted = false, secondStarted = false;
        try
        {
            first = StartSelectedCameraAsync();
            long version = _cameraOperationVersion;
            second = StartSelectedCameraAsync();
            sameTask = ReferenceEquals(first, second);
            sameVersion = _cameraOperationVersion == version;
            stop = StopCameraAsync();
            cancelledBeforeRelease = !_cameraWanted && _cameraWantedDeviceId is null &&
                _cameraOperationVersion != version;
        }
        finally
        {
            // StopCameraAsync marks the request cancelled synchronously before it
            // waits on this gate. Always do that before permitting queued starts.
            stop ??= StopCameraAsync();
            _cameraOperation.Release();
            await stop.WaitAsync(timeout);
            if (first is not null) firstStarted = await first.WaitAsync(timeout);
            if (second is not null) secondStarted = await second.WaitAsync(timeout);
        }
        Require(sameTask && sameVersion,
            "Duplicate pending starts did not reuse the same task and operation version.");
        Require(cancelledBeforeRelease && !firstStarted && !secondStarted,
            "Stop did not cancel both queued starts before they could open the camera.");
        Require(!_camera.IsRunning && !_cameraWanted && _camera.ActiveDeviceId is null &&
            _cameraWantedDeviceId is null && _cameraOperation.CurrentCount == 1 &&
            _camera.LatestFrame is null && Volatile.Read(ref _latestCameraFrame) is null,
            "The camera safety check left capture state or a pending operation active.");

        return new { passed = true, readerConversion, appInstanceLeaseHeld = true, occupiedDeviceBlockedBeforeEnumeration = true,
            failedStartPreservesOtherOwner = true, duplicateStartsShareTask = true,
            duplicateStartsPreserveVersion = true, stopCancelsQueuedStarts = true,
            stalledCaptureRequiresManualStop = true, repeatedHealthTicksDoNotReopen = true,
            cameraStopped = true, cameraStateCleared = true, liveHardwareUnchanged = true };

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }

    private static async Task<object> VerifyCameraReaderConversionAsync()
    {
        await using var capture = new CameraCaptureService();
        const int width = 32, height = 16;
        var native = new byte[width * height * 3 / 2];
        Array.Fill(native, (byte)128);
        Array.Fill(native, (byte)64, 0, width * height / 2);
        Array.Fill(native, (byte)192, width * height / 2, width * height / 2);
        var owned = new byte[width * height * 4];
        using (var source = new SoftwareBitmap(BitmapPixelFormat.Nv12, width, height, BitmapAlphaMode.Ignore))
        {
            source.CopyFromBuffer(native.AsBuffer());
            using var converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            Check(converted.PixelWidth == width && converted.PixelHeight == height && converted.BitmapPixelFormat == BitmapPixelFormat.Bgra8,
                "Local NV12 conversion changed dimensions or pixel format.");
            converted.CopyToBuffer(owned.AsBuffer());
            Array.Fill(native, (byte)0);
            source.CopyFromBuffer(native.AsBuffer());
        }
        for (int pixel = 0; pixel < owned.Length; pixel += 4)
            Check(Math.Abs(owned[pixel] - owned[pixel + 1]) <= 1 && Math.Abs(owned[pixel + 1] - owned[pixel + 2]) <= 1,
                "Neutral NV12 chroma converted to a coloured BGRA frame.");
        Check(owned[width * (height - 1) * 4] > owned[0] + 80,
            "The owned BGRA copy lost the source's distinct luminance bands or retained disposed storage.");
        Check(!capture.IsRunning && capture.GetDiagnostics().ReaderStarts == 0,
            "Synthetic conversion verification opened camera hardware.");
        return new { ownedBgraConversion = true, cameraOpened = false };

        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
