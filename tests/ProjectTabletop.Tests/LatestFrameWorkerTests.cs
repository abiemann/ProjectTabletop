using ProjectTabletop.App.Camera;

namespace ProjectTabletop.Tests;

public class LatestFrameWorkerTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static int Number(CameraFrame frame) => (int)(frame.Timestamp - DateTimeOffset.UnixEpoch).TotalMilliseconds;

    [Fact]
    public async Task BlockedSceneCannotBlockDeliveryAndOnlyNewestPendingFrameRuns()
    {
        using var pool = new CameraFramePool(2, 2, capacity: 3);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var latest = Signal();
        var observed = new List<int>();
        var errors = new List<Exception>();
        var worker = new LatestFrameWorker<int>((frame, number) =>
        {
            // Delivery state stays with its own pixels when a waiting frame is replaced.
            Assert.Equal(number, Number(frame));
            observed.Add(number);
            if (observed.Count == 1)
            {
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                Assert.Equal(17, frame.Bgra[0]);
            }
            else latest.SetResult();
        }, errors.Add);
        void Deliver(int number)
        {
            using var rental = pool.TryRent()!;
            Assert.NotNull(rental);
            rental.Bytes[0] = number == 0 ? (byte)17 : (byte)99;
            using var producer = rental.Publish(DateTimeOffset.UnixEpoch.AddMilliseconds(number));
            Assert.True(worker.Enqueue(producer.Frame, number));
        }
        try
        {
            Deliver(0);
            await Wait(started.Task);
            // All delivery completes while the first scene operation is blocked.
            // Three buffers suffice for the active, waiting, and arriving frame.
            for (int number = 1; number <= 1000; number++) Deliver(number);
            Assert.Equal(2, pool.GetDiagnostics().Outstanding);
            Assert.Equal([0], observed);
            release.Set();
            await Wait(latest.Task);
            await Wait(worker.StopAsync());
            Assert.Equal([0, 1000], observed);
            Assert.Empty(errors);
            Assert.Equal(0, pool.GetDiagnostics().Outstanding);
        }
        finally { release.Set(); await Wait(worker.StopAsync()); }
    }

    [Fact]
    public async Task ResetAndShutdownReleaseWaitingPixelsWithoutRunningThem()
    {
        using var pool = new CameraFramePool(2, 2, capacity: 2);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        int executed = 0;
        var errors = new List<Exception>();
        var worker = new LatestFrameWorker<int>((_, _) =>
        {
            Interlocked.Increment(ref executed);
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }, errors.Add);
        void Deliver()
        {
            using var rental = pool.TryRent()!;
            using var producer = rental.Publish(DateTimeOffset.UnixEpoch);
            Assert.True(worker.Enqueue(producer.Frame, 0));
        }
        try
        {
            Deliver();
            await Wait(started.Task);
            Deliver();
            Assert.Equal(2, pool.GetDiagnostics().Outstanding);
            worker.Clear();
            Assert.Equal(1, pool.GetDiagnostics().Outstanding);
            Deliver();
            var stopped = worker.StopAsync();
            Assert.False(stopped.IsCompleted);
            Assert.Equal(1, pool.GetDiagnostics().Outstanding);
            using (var rental = pool.TryRent()!)
            using (var producer = rental.Publish(DateTimeOffset.UnixEpoch))
                Assert.False(worker.Enqueue(producer.Frame, 0));
            release.Set();
            await Wait(stopped);
            Assert.Equal(1, executed);
            Assert.Empty(errors);
            Assert.Equal(0, pool.GetDiagnostics().Outstanding);
        }
        finally { release.Set(); await Wait(worker.StopAsync()); }
    }

    [Fact]
    public async Task FailedFrameIsReportedWithoutStrandingLaterFramesOrFaultingShutdown()
    {
        using var pool = new CameraFramePool(2, 2, capacity: 2);
        var failed = Signal();
        var recovered = Signal();
        var errors = new List<Exception>();
        var worker = new LatestFrameWorker<int>((_, number) =>
        {
            if (number == 0) throw new InvalidOperationException("Scene scheduling failed.");
            recovered.SetResult();
        }, error =>
        {
            errors.Add(error);
            failed.SetResult();
        });
        void Deliver(int number)
        {
            using var rental = pool.TryRent()!;
            using var producer = rental.Publish(DateTimeOffset.UnixEpoch.AddMilliseconds(number));
            Assert.True(worker.Enqueue(producer.Frame, number));
        }
        try
        {
            Deliver(0);
            await Wait(failed.Task);
            Deliver(1);
            await Wait(recovered.Task);
            // Window close awaits this before releasing the camera; it must not throw.
            await Wait(worker.StopAsync());
            Assert.Equal("Scene scheduling failed.", Assert.IsType<InvalidOperationException>(Assert.Single(errors)).Message);
            Assert.Equal(0, pool.GetDiagnostics().Outstanding);
        }
        finally { await Wait(worker.StopAsync()); }
    }
}
