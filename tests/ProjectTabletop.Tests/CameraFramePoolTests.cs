using ProjectTabletop.App.Camera;

namespace ProjectTabletop.Tests;

public class CameraFramePoolTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ExhaustionNeverOverwritesPixelsUntilEveryLeaseIsReleased()
    {
        using var pool = new CameraFramePool(2, 2, capacity: 2);
        using var rental = pool.TryRent()!;
        rental.Bytes[0] = 17;
        var producer = rental.Publish(Epoch);
        var frame = producer.Frame;
        byte[] originalBuffer = frame.Bgra;
        var firstReader = frame.Retain();
        var secondReader = frame.Retain();
        rental.Dispose();
        producer.Dispose();
        producer.Dispose();
        using var other = pool.TryRent()!;
        other.Bytes[0] = 99;
        Assert.Null(pool.TryRent());
        Assert.Equal(17, firstReader.Frame.Bgra[0]);
        firstReader.Dispose();
        Assert.Null(pool.TryRent());
        Assert.Equal(17, secondReader.Frame.Bgra[0]);
        secondReader.Dispose();
        secondReader.Dispose();
        Assert.Null(frame.TryRetain());
        using var reused = pool.TryRent()!;
        Assert.Same(originalBuffer, reused.Bytes);
        reused.Bytes[0] = 42;
        Assert.Null(frame.TryRetain()); // An old frame cannot reacquire the buffer's next generation.
        Assert.Equal(2, pool.GetDiagnostics().Outstanding);
    }

    [Fact]
    public void UnpublishedRentalAndRetiredPoolReleaseWithoutAffectingNewPool()
    {
        using var previous = new CameraFramePool(2, 2, capacity: 1);
        var unused = previous.TryRent()!;
        unused.Dispose(); unused.Dispose();
        Assert.Equal(1, previous.GetDiagnostics().Available);
        using var rental = previous.TryRent()!;
        rental.Bytes[0] = 23;
        var held = rental.Publish(Epoch);
        previous.Dispose();
        Assert.True(previous.GetDiagnostics().Retired);
        Assert.Null(previous.TryRent());
        using var restarted = new CameraFramePool(2, 2, capacity: 1);
        using var newRental = restarted.TryRent()!;
        Assert.NotSame(held.Frame.Bgra, newRental.Bytes);
        newRental.Bytes[0] = 91;
        Assert.Equal(23, held.Frame.Bgra[0]);
        held.Dispose();
        Assert.Equal(0, previous.GetDiagnostics().Outstanding);
        Assert.Equal(1, restarted.GetDiagnostics().Outstanding);
    }

    [Fact]
    public void LatestSlotReplacementPreservesAnAcquiredReader()
    {
        using var pool = new CameraFramePool(2, 2, capacity: 2);
        using var slot = new CameraFrameSlot();
        using var firstRental = pool.TryRent()!;
        firstRental.Bytes[0] = 31;
        var first = firstRental.Publish(Epoch);
        slot.Set(first.Frame);
        first.Dispose();
        var reader = slot.Acquire()!;
        using var secondRental = pool.TryRent()!;
        secondRental.Bytes[0] = 67;
        var second = secondRental.Publish(Epoch.AddMilliseconds(1));
        slot.Set(second.Frame);
        second.Dispose();
        slot.Clear();
        Assert.Null(slot.Acquire());
        Assert.Equal(31, reader.Frame.Bgra[0]);
        Assert.Equal(1, pool.GetDiagnostics().Outstanding);
        reader.Dispose();
        Assert.Equal(2, pool.GetDiagnostics().Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerRetainsPixelsUntilQueuedPublicationFinishes(bool throwingPublication)
    {
        using var pool = new CameraFramePool(2, 2, capacity: 1);
        using var rental = pool.TryRent()!;
        rental.Bytes[0] = 53;
        var producer = rental.Publish(Epoch);
        var frame = producer.Frame;
        var worker = new FrameWorker();
        Action? queued = null;
        Assert.True(worker.TryRun(0, TimeSpan.Zero, frame, work =>
            Assert.True(work.TryPost(action => { queued = action; return true; }, () =>
            {
                Assert.Equal(53, frame.Bgra[0]);
                if (throwingPublication) throw new InvalidOperationException("publication failed");
            }))));
        producer.Dispose();
        await worker.WorkerTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(pool.TryRent());
        Assert.NotNull(queued);
        if (throwingPublication) Assert.Throws<InvalidOperationException>(() => queued());
        else queued();
        Assert.Equal(1, pool.GetDiagnostics().Available);
        Assert.Null(frame.TryRetain());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerRejectionDoesNotKeepAFrameLease(bool rejectDispatch)
    {
        using var pool = new CameraFramePool(2, 2, capacity: 1);
        using var rental = pool.TryRent()!;
        var producer = rental.Publish(Epoch);
        var worker = new FrameWorker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (rejectDispatch)
            Assert.True(worker.TryRun(0, TimeSpan.Zero, producer.Frame, work =>
                Assert.False(work.TryPost(_ => false, () => throw new InvalidOperationException("must not publish")))));
        else
        {
            Assert.True(worker.TryRun(0, TimeSpan.Zero, _ => release.Task));
            Assert.False(worker.TryRun(1, TimeSpan.Zero, producer.Frame,
                (Action<FrameWork>)(_ => throw new InvalidOperationException("must not run"))));
        }
        producer.Dispose();
        release.SetResult();
        await worker.WorkerTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, pool.GetDiagnostics().Available);
    }
}
