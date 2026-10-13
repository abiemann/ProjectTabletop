using System.Text.Json;
using ProjectTabletop.App;
using ProjectTabletop.App.Camera;

namespace ProjectTabletop.Tests;

public class RecordingTests
{
    [Fact]
    public async Task IdlePrebufferReleasesCapacityAndAgeEvictionsOnStopAndDispose()
    {
        using var pool = new CameraFramePool(8, 8, capacity: 12);
        string directory = Path.Combine(Path.GetTempPath(), "ProjectTabletop-idle-lease-" + Guid.NewGuid().ToString("N"));
        var recorder = new HandTrackingVideoRecorder(directory);
        try
        {
            CameraFrame? oldest = null;
            for (int index = 0; index < 9; index++)
            {
                using var frame = Publish(pool, index * 10);
                oldest ??= frame.Frame;
                recorder.ObserveFrame(frame.Frame);
            }
            Assert.Equal(8, pool.GetDiagnostics().Outstanding);
            Assert.Null(oldest!.TryRetain());
            using (var later = Publish(pool, 500)) recorder.ObserveFrame(later.Frame);
            Assert.Equal(1, pool.GetDiagnostics().Outstanding);
            recorder.Stop("test-stop");
            Assert.Equal(0, pool.GetDiagnostics().Outstanding);
            using (var final = Publish(pool, 600)) recorder.ObserveFrame(final.Frame);
            await recorder.DisposeAsync();
            Assert.Equal(0, pool.GetDiagnostics().Outstanding);
            Assert.False(Directory.Exists(directory));
        }
        finally { await recorder.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecorderReleasesQueuedDroppedAndFailedFrames(bool failWorker)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ProjectTabletop-queued-lease-" + Guid.NewGuid().ToString("N"));
        using var pool = new CameraFramePool(320, 240, capacity: 12);
        var recorder = new HandTrackingVideoRecorder(directory);
        var blocker = new BlockingMetadata(failWorker);
        try
        {
            using (var first = Publish(pool, 0)) recorder.HandDetected(first.Frame);
            recorder.Record(blocker);
            Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(0, pool.GetDiagnostics().Outstanding); // Encoding retains its own BGR image, not the camera buffer.
            for (int index = 1; index <= 8; index++)
                using (var frame = Publish(pool, index * 40)) recorder.ObserveFrame(frame.Frame);
            Assert.Equal(8, pool.GetDiagnostics().Outstanding);
            CameraFrame dropped;
            using (var overflow = Publish(pool, 400))
            {
                dropped = overflow.Frame;
                recorder.ObserveFrame(dropped);
            }
            Assert.Null(dropped.TryRetain());
            Assert.Equal(8, pool.GetDiagnostics().Outstanding);
            Assert.True(recorder.Status.DroppedFrames > 0);
            var completion = recorder.DisposeAsync().AsTask();
            Assert.False(completion.IsCompleted);
            blocker.Release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, pool.GetDiagnostics().Outstanding);
            Assert.Equal(0, recorder.Status.QueuedFrames);
            Assert.Equal(0, recorder.Status.QueuedRecords);
            if (failWorker) Assert.Contains("recording failed", recorder.Status.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            blocker.Release.Set();
            await recorder.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CameraFrameLease Publish(CameraFramePool pool, int milliseconds)
    {
        using var rental = pool.TryRent() ?? throw new InvalidOperationException("The test pool unexpectedly exhausted.");
        Array.Fill(rental.Bytes, (byte)64);
        return rental.Publish(DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds));
    }

    private sealed class BlockingMetadata(bool fail)
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public string Type
        {
            get
            {
                Entered.Set(); Release.Wait();
                if (fail) throw new IOException("Deliberate metadata write failure.");
                return "blocked_test";
            }
        }
    }

    [Fact]
    public async Task BuiltInMjpegRecordsAndDecodesCameraTimeline()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ProjectTabletop-recording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = await HandTrackingVideoRecorderVerification.RunAsync(directory);
            var result = JsonSerializer.SerializeToElement(report);
            Assert.True(result.GetProperty("passed").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
