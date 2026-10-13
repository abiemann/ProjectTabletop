#if DEBUG
using ProjectTabletop.App.Camera;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyCameraSafetyAsync()
    {
        if (_camera.IsRunning || _cameraWanted || _cameraOperation.CurrentCount == 0 || _closing)
            throw new InvalidOperationException("Stop the camera before checking camera safety.");
        if (SelectedCamera is null)
            throw new InvalidOperationException("Select a camera before checking queued camera-start cancellation.");
        var timeout = TimeSpan.FromSeconds(5);

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

        return new { passed = true, appInstanceLeaseHeld = true, occupiedDeviceBlockedBeforeEnumeration = true,
            failedStartPreservesOtherOwner = true, duplicateStartsShareTask = true,
            duplicateStartsPreserveVersion = true, stopCancelsQueuedStarts = true,
            cameraStopped = true, cameraStateCleared = true, liveHardwareUnchanged = true };

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
