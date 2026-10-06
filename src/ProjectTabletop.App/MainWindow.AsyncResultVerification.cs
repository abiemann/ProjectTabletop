#if DEBUG
namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Independent gates and workers: no live camera, calibration, board or model is changed.
    private static async Task<object> VerifyAsyncResultsAsync()
    {
        var gate = new AsyncResultGate();
        string? visible = null;
        string? error = null;
        long obsolete = gate.Capture();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> delayed = Task.Run(async () =>
        {
            ready.SetResult();
            await finish.Task;
            return gate.TryApply(obsolete, () => { visible = "old-camera"; return true; });
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Invalidate(() => { visible = null; error = null; });
        Require(gate.TryApply(gate.Capture(), () => { visible = "current-camera"; return true; }),
            "A reset rejected fresh camera results.");
        finish.SetResult();
        Require(!await delayed.WaitAsync(TimeSpan.FromSeconds(5)) && visible == "current-camera",
            "A worker finishing after reset overwrote fresh camera results.");
        Require(!gate.TryApply(obsolete, () => { error = "obsolete failure"; return true; }) && error is null,
            "An obsolete worker error overwrote current status.");

        // A newer calibration request must win even when the older read finishes last.
        long olderRead = gate.Capture();
        gate.Invalidate();
        long newerRead = gate.Capture();
        Require(gate.TryApply(newerRead, () => { visible = "new-calibration"; return true; }) &&
            !gate.TryApply(olderRead, () => { visible = "old-calibration"; return true; }) &&
            visible == "new-calibration", "Out-of-order reads restored stale calibration.");
        gate.Invalidate(() => visible = null);
        Require(!gate.TryApply(newerRead, () => { visible = "after-close"; return true; }) && visible is null,
            "A closed generation accepted a pending read.");

        // Exercise the other ordering too: reset waits for an already-publishing result,
        // then clears it, so there is no check-before-reset/publication-after-reset race.
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        long current = gate.Capture();
        Task<bool> publish = Task.Run(() => gate.TryApply(current, () =>
        {
            publishing.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Publication test was not released.");
            visible = "already-publishing";
            return true;
        }));
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task reset = Task.Run(() =>
        {
            resetting.SetResult();
            gate.Invalidate(() => visible = null);
        });
        await resetting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.Set();
        Require(await publish.WaitAsync(TimeSpan.FromSeconds(5)), "An already-started publication unexpectedly failed.");
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Require(visible is null && !gate.TryApply(current, () => { visible = "late"; return true; }),
            "A reset left a concurrently published old result visible.");
        return new { passed = true, delayedCameraResultRejected = true, obsoleteErrorsRejected = true,
            newestCalibrationReadWins = true, closeCancelsReads = true, resetAndPublicationSerialized = true };

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
