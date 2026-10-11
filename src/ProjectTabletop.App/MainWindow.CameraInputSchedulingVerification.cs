#if DEBUG
namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyCameraInputSchedulingAsync()
    {
        // Exercise the real UI queue without touching a camera, inference engine,
        // gesture, or projected scene. Do not borrow a live worker's slot.
        if (_camera.IsRunning || _cameraWanted || _cameraOperation.CurrentCount == 0 || _closing)
            throw new InvalidOperationException("Stop the camera before checking input scheduling.");
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_handGate)
            if (!_handWorker.TryRun(System.Diagnostics.Stopwatch.GetTimestamp(), TimeSpan.Zero, work =>
            {
                if (!work.TryPost(_dispatchInputResult, () => ran.TrySetResult(_handWorker.IsBusy)))
                    ran.TrySetException(new InvalidOperationException("The hand result could not enter the UI queue."));
            })) throw new InvalidOperationException("Wait for the pending hand result before checking input scheduling.");
        if (!await ran.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The hand worker was not held through UI publication.");
        await _handWorker.WorkerTask.WaitAsync(TimeSpan.FromSeconds(5));
        if (_handWorker.IsBusy)
            throw new InvalidOperationException("A completed hand result did not release its worker.");
        _handWorker.ResetCadence();

        for (int frame = 0; frame < 100; frame++) QueueEyeTipStatusRefresh();
        if (!_eyeTipUiRefresh.IsPending)
            throw new InvalidOperationException("Stick status refreshes were not coalesced into one pending update.");
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => drained.TrySetResult(true)))
            throw new InvalidOperationException("The UI queue could not complete the scheduling check.");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (_eyeTipUiRefresh.IsPending)
            throw new InvalidOperationException("A completed stick status update did not release its pending slot.");
        return new { passed = true, handWorkerHeldThroughUiPublication = true,
            completedHandResultReleasesWorker = true, stickStatusRefreshesCoalesced = true };
    }
}
#endif
