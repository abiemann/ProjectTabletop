using System.Diagnostics;
using ProjectTabletop.App.Camera;

namespace ProjectTabletop.Tests;

public class FrameWorkerTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static long Tick(double milliseconds) => (long)(milliseconds / 1000 * Stopwatch.Frequency);

    [Fact]
    public async Task BusyFramesAreDroppedUnderConcurrentLoadWithoutStartingAnotherWorker()
    {
        var worker = new FrameWorker();
        var started = Signal();
        var finish = Signal();
        int executions = 0;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, async _ =>
        {
            Interlocked.Increment(ref executions);
            started.SetResult();
            await finish.Task;
        }));
        await Wait(started.Task);

        Parallel.For(0, 1000, frame =>
            Assert.False(worker.TryRun(frame, TimeSpan.Zero, _ => Interlocked.Increment(ref executions))));

        Assert.True(worker.IsBusy);
        finish.SetResult();
        await Wait(worker.WorkerTask);
        Assert.False(worker.IsBusy);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task IndependentDetectorCanFinishWhileAnotherIsStillWorking()
    {
        var hands = new FrameWorker();
        var stick = new FrameWorker();
        var finish = Signal();
        Assert.True(hands.TryRun(0, TimeSpan.Zero, _ => finish.Task));
        Assert.True(stick.TryRun(0, TimeSpan.Zero, _ => { }));
        await Wait(stick.WorkerTask);
        Assert.False(stick.IsBusy);
        Assert.True(hands.IsBusy);
        finish.SetResult();
        await Wait(hands.WorkerTask);
    }

    [Fact]
    public async Task BackgroundTaskFinishesWithoutWaitingForUiButSlotRemainsOwned()
    {
        var worker = new FrameWorker();
        Action? queued = null;
        int generation = 1, published = 0;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
            Assert.True(work.TryPost(action => { queued = action; return true; }, () =>
            {
                // The detector keeps the same generation check around its state.
                if (generation == 1) published++;
            }))));
        await Wait(worker.WorkerTask);

        generation++;
        worker.ResetCadence();
        Assert.True(worker.IsBusy);
        Assert.False(worker.TryRun(1, TimeSpan.Zero, _ => { }));
        Assert.NotNull(queued);
        queued();
        Assert.Equal(0, published);
        Assert.False(worker.IsBusy);
        Assert.True(worker.TryRun(1, TimeSpan.Zero, _ => { }));
        await Wait(worker.WorkerTask);
    }

    [Fact]
    public async Task EarlyUiPublicationCannotReleaseAStillRunningBackgroundWorker()
    {
        var worker = new FrameWorker();
        var posted = Signal();
        var finish = Signal();
        Action? queued = null;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, async work =>
        {
            Assert.True(work.TryPost(action => { queued = action; return true; }, () => { }));
            posted.SetResult();
            await finish.Task;
        }));
        await Wait(posted.Task);
        queued!();
        Assert.True(worker.IsBusy);
        Assert.False(worker.TryRun(1, TimeSpan.Zero, _ => { }));
        finish.SetResult();
        await Wait(worker.WorkerTask);
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task SynchronousDispatcherKeepsSlotUntilWorkerReturns()
    {
        var worker = new FrameWorker();
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
        {
            Assert.True(work.TryPost(action => { action(); return true; }, () => Assert.True(worker.IsBusy)));
            Assert.True(worker.IsBusy);
            Assert.False(worker.TryRun(1, TimeSpan.Zero, _ => { }));
        }));
        await Wait(worker.WorkerTask);
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task RejectedDispatchReleasesSlotAndDoesNotPublish()
    {
        var worker = new FrameWorker();
        bool published = false;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
            Assert.False(work.TryPost(_ => false, () => published = true))));
        await Wait(worker.WorkerTask);
        Assert.False(published);
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task ThrowingDispatcherReleasesSlotEvenWhenWorkerFaults()
    {
        var worker = new FrameWorker();
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
        {
            work.TryPost(_ => throw new InvalidOperationException("dispatcher unavailable"), () => { });
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wait(worker.WorkerTask));
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task ThrowingQueuedPublicationStillReleasesSlot()
    {
        var worker = new FrameWorker();
        Action? queued = null;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
            Assert.True(work.TryPost(action => { queued = action; return true; },
                () => throw new InvalidOperationException("publication failed")))));
        await Wait(worker.WorkerTask);
        Assert.True(worker.IsBusy);
        Assert.Throws<InvalidOperationException>(() => queued!());
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task ThrowingSynchronousPublicationCannotReleaseWorkerTwice()
    {
        var worker = new FrameWorker();
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
        {
            Assert.Throws<InvalidOperationException>(() => work.TryPost(action => { action(); return true; },
                () => throw new InvalidOperationException("publication failed")));
            Assert.True(worker.IsBusy);
            Assert.False(worker.TryRun(1, TimeSpan.Zero, _ => { }));
        }));
        await Wait(worker.WorkerTask);
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task FaultedWorkReleasesSlotForNextFrame()
    {
        var worker = new FrameWorker();
        Assert.True(worker.TryRun(0, TimeSpan.Zero, (Action<FrameWork>)(_ => throw new InvalidOperationException("detector failed"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wait(worker.WorkerTask));
        Assert.False(worker.IsBusy);
        Assert.True(worker.TryRun(1, TimeSpan.Zero, _ => { }));
        await Wait(worker.WorkerTask);
    }

    [Fact]
    public async Task WorkerFailureDoesNotAbandonAlreadyQueuedPublication()
    {
        var worker = new FrameWorker();
        Action? queued = null;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, (Action<FrameWork>)(work =>
        {
            work.TryPost(action => { queued = action; return true; }, () => { });
            throw new InvalidOperationException("after publication");
        })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wait(worker.WorkerTask));
        Assert.True(worker.IsBusy);
        queued!();
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task CadenceUsesLastAcceptedFrameAndResetAllowsImmediateNextFrame()
    {
        var worker = new FrameWorker();
        var interval = TimeSpan.FromMilliseconds(80);
        Assert.True(worker.TryRun(Tick(0), interval, _ => { }));
        await Wait(worker.WorkerTask);
        Assert.False(worker.TryRun(Tick(50), interval, _ => throw new InvalidOperationException()));
        Assert.True(worker.TryRun(Tick(80), interval, _ => { }));
        await Wait(worker.WorkerTask);
        Assert.False(worker.TryRun(Tick(100), interval, _ => throw new InvalidOperationException()));
        worker.ResetCadence();
        Assert.True(worker.TryRun(Tick(100), interval, _ => { }));
        await Wait(worker.WorkerTask);
    }

    [Fact]
    public async Task PreflightDoesNotConsumeCadenceAndCannotBypassBusyReservation()
    {
        var worker = new FrameWorker();
        var interval = TimeSpan.FromMilliseconds(80);
        Assert.True(worker.CanRun(Tick(0), interval));
        Assert.True(worker.CanRun(Tick(0), interval));
        var finish = Signal();
        Assert.True(worker.TryRun(Tick(0), interval, _ => finish.Task));
        Assert.False(worker.CanRun(Tick(100), interval));
        Assert.False(worker.TryRun(Tick(100), interval, _ => { }));
        finish.SetResult();
        await Wait(worker.WorkerTask);
        Assert.False(worker.CanRun(Tick(50), interval));
        Assert.True(worker.CanRun(Tick(80), interval));
        Assert.True(worker.TryRun(Tick(80), interval, _ => { }));
        await Wait(worker.WorkerTask);
    }

    [Fact]
    public async Task ZeroCadenceAcceptsEveryCompletedFrameAtHighRate()
    {
        var worker = new FrameWorker();
        int completed = 0;
        for (int frame = 0; frame < 1000; frame++)
        {
            Assert.True(worker.TryRun(Tick(frame * 10), TimeSpan.Zero, _ => completed++));
            await Wait(worker.WorkerTask);
        }
        Assert.Equal(1000, completed);
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task CompletedWorkCannotPublishAfterSlotHasBeenReused()
    {
        var worker = new FrameWorker();
        FrameWork? completed = null;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work => { completed = work; }));
        await Wait(worker.WorkerTask);
        Assert.Throws<InvalidOperationException>(() => completed!.TryPost(_ => true, () => { }));
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task MultiplePublicationsEachRetainTheirOwnSlotOwnership()
    {
        var worker = new FrameWorker();
        var queued = new List<Action>();
        Assert.True(worker.TryRun(0, TimeSpan.Zero, work =>
        {
            work.TryPost(action => { queued.Add(action); return true; }, () => { });
            work.TryPost(action => { queued.Add(action); return true; }, () => { });
        }));
        await Wait(worker.WorkerTask);
        queued[0]();
        Assert.True(worker.IsBusy);
        queued[1]();
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public void LatestStateRefreshCoalescesFramesAndReadsCurrentState()
    {
        var refresh = new CoalescedDispatch();
        Action? queued = null;
        int latest = 0, displayed = -1, callbacks = 0;
        bool Dispatch(Action action) { callbacks++; queued = action; return true; }
        for (latest = 0; latest < 1000; latest++)
            Assert.Equal(latest == 0, refresh.TryPost(Dispatch, () => displayed = latest));
        Assert.True(refresh.IsPending);
        Assert.Equal(1, callbacks);
        queued!();
        Assert.Equal(latest, displayed);
        Assert.False(refresh.IsPending);
    }

    [Fact]
    public void RejectedOrThrowingRefreshDispatchCanBeRetried()
    {
        var refresh = new CoalescedDispatch();
        Assert.False(refresh.TryPost(_ => false, () => { }));
        Assert.False(refresh.IsPending);
        Assert.Throws<InvalidOperationException>(() => refresh.TryPost(_ => throw new InvalidOperationException(), () => { }));
        Assert.False(refresh.IsPending);
        Assert.True(refresh.TryPost(action => { action(); return true; }, () => { }));
        Assert.False(refresh.IsPending);
    }

    [Fact]
    public void ThrowingRefreshStillReleasesPendingFlag()
    {
        var refresh = new CoalescedDispatch();
        Action? queued = null;
        Assert.True(refresh.TryPost(action => { queued = action; return true; }, () => throw new InvalidOperationException()));
        Assert.Throws<InvalidOperationException>(() => queued!());
        Assert.False(refresh.IsPending);
    }

    [Fact]
    public void SynchronousRefreshFailureCannotClearNewlyQueuedRefresh()
    {
        var refresh = new CoalescedDispatch();
        Action? queued = null;
        Assert.Throws<InvalidOperationException>(() => refresh.TryPost(action =>
        {
            Assert.Throws<InvalidOperationException>(() => action());
            Assert.True(refresh.TryPost(next => { queued = next; return true; }, () => { }));
            throw new InvalidOperationException("dispatcher rethrew");
        }, () => throw new InvalidOperationException("refresh failed")));
        Assert.True(refresh.IsPending);
        queued!();
        Assert.False(refresh.IsPending);
    }
}
