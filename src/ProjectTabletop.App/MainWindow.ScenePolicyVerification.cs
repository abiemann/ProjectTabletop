#if DEBUG
using System.Reflection;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated scenes and locks exercise scheduling reads without touching the
    // live camera, registration, board, or graphics device.
    private async Task<object> VerifyScenePolicyReadsAsync()
    {
        using var scene = new SceneCompositor();
        var gate = typeof(SceneCompositor).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scene)!;
        var session = (BoardSession)typeof(SceneCompositor)
            .GetField("_boardSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var opened = new List<BoardScreen>();
        scene.BoardOpened += opened.Add;

        await CheckNonBlockingReads(BoardScreen.Menu, expectedHands: true);
        scene.ShowPaint();
        await CheckNonBlockingReads(BoardScreen.Paint, expectedHands: false);
        scene.ShowBoardMenu();
        // This is the same session-selection path used by a menu gesture; it
        // must publish the selection just like direct Show* navigation.
        lock (gate)
            Check(session.ActivateButton("water-garden", MonotonicClock.UtcNow), "The menu did not open Water Garden.");
        await CheckNonBlockingReads(BoardScreen.WaterGarden, expectedHands: true);
        scene.ShowStrykersRun();
        await CheckStrykerRecheck();
        Check(opened.SequenceEqual(new[] { BoardScreen.Paint, BoardScreen.Menu,
            BoardScreen.WaterGarden, BoardScreen.StrykersRun, BoardScreen.WaterGarden }),
            "A navigation path failed to publish its selected board.");
        return new { passed = true, nonBlockingBoards = new[] { "Menu", "WaterGarden", "Paint" },
            directAndButtonNavigationPublishSelection = true,
            strykersRunWaitsForAuthoritativePolicy = true, navigationWhileWaitingRechecked = true };

        async Task CheckNonBlockingReads(BoardScreen expected, bool expectedHands)
        {
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = HoldGate(held, release);
            Task<(BoardScreen Screen, bool Hands)>? reader = null;
            try
            {
                Check(held.Wait(TimeSpan.FromSeconds(5)), "The synthetic render lock was not acquired.");
                reader = Task.Run(() => (scene.CurrentBoardScreen, scene.HandDetectionRequired));
                var result = await reader.WaitAsync(TimeSpan.FromSeconds(5));
                Check(result == (expected, expectedHands), $"Incorrect scheduling policy for {expected}.");
            }
            finally
            {
                release.Set();
                await owner.WaitAsync(TimeSpan.FromSeconds(5));
                if (reader is not null) await reader.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        async Task CheckStrykerRecheck()
        {
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var reading = new ManualResetEventSlim();
            var owner = HoldGate(held, release, scene.ShowWaterGarden);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = new Thread(() =>
            {
                try
                {
                    reading.Set();
                    result.TrySetResult(scene.HandDetectionRequired);
                }
                catch (Exception error) { result.TrySetException(error); }
            }) { IsBackground = true, Name = "Scene policy verification" };
            bool started = false;
            try
            {
                Check(held.Wait(TimeSpan.FromSeconds(5)), "The synthetic render lock was not acquired.");
                reader.Start();
                started = true;
                Check(reading.Wait(TimeSpan.FromSeconds(5)), "The Stryker policy reader did not start.");
                Check(SpinWait.SpinUntil(() => result.Task.IsCompleted ||
                    (reader.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)) &&
                    !result.Task.IsCompleted,
                    "Stryker's dynamic policy bypassed its authoritative scene lock.");
            }
            finally
            {
                // The owner navigates to Water Garden before releasing _gate.
                // A reader that retained Stryker's stale branch would return false.
                release.Set();
                await owner.WaitAsync(TimeSpan.FromSeconds(5));
                if (started)
                {
                    Check(await result.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                        "The waiting policy reader did not recheck the new board.");
                    Check(reader.Join(TimeSpan.FromSeconds(5)), "The policy reader did not finish.");
                }
            }
        }

        Task HoldGate(ManualResetEventSlim held, ManualResetEventSlim release, Action? beforeRelease = null) =>
            Task.Factory.StartNew(() =>
            {
                lock (gate)
                {
                    held.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(15)), "The synthetic render lock was not released.");
                    beforeRelease?.Invoke();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
