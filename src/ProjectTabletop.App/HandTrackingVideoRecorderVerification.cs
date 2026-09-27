#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.App.Camera;

namespace ProjectTabletop.App;

/// <summary>Exercises the real encoder and sidecar using isolated synthetic camera frames.</summary>
internal static class HandTrackingVideoRecorderVerification
{
    public static Task<object> RunAsync(string directory) => Task.Run(() => VerifyAsync(directory));

    private static async Task<object> VerifyAsync(string directory)
    {
        string root = Path.Combine(directory, "video-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string lazy = Path.Combine(root, "idle");
        await using (var recorder = new HandTrackingVideoRecorder(lazy))
        {
            var frame = Frame(DateTimeOffset.UtcNow, 20, 40, 220);
            recorder.ObserveFrame(frame);
            recorder.Record(new { Type = "detection", FrameTime = frame.Timestamp, Hands = Array.Empty<object>() });
            recorder.Tick(frame.Timestamp.AddSeconds(6));
            Require(!Directory.Exists(lazy), "Camera frames without a detected hand created recording files.");
        }
        Require(!Directory.Exists(lazy), "Disposing an idle recorder created recording files.");

        string timeline = Path.Combine(root, "timeline");
        var start = DateTimeOffset.UtcNow.AddMilliseconds(-180);
        CameraFrame[] sources =
        [
            Frame(start, 20, 40, 220),
            Frame(start.AddMilliseconds(80), 20, 220, 40),
            Frame(start.AddMilliseconds(160), 220, 40, 20),
            Frame(start.AddMilliseconds(360), 180, 180, 180),
            Frame(start.AddMilliseconds(4980), 180, 40, 180)
        ];
        byte[][] originalPixels = sources.Select(frame => frame.Bgra.ToArray()).ToArray();
        var live = new HandTrackingVideoRecorder(timeline);
        try
        {
            live.ObserveFrame(sources[0]);
            live.ObserveFrame(sources[1]);
            live.ObserveFrame(sources[2]);
            // Recognition may finish after a newer camera frame has arrived.
            // The exact trigger photograph must survive that ordering.
            live.HandDetected(sources[1]);
            live.Record(new { Type = "verification_detection", FrameTime = sources[1].Timestamp,
                SourceFrameId = FrameId(sources[1]), Hands = new[] { new { IndexTip = new { X = 24, Y = 16 } } } });
            live.ObserveFrame(sources[3]);
            live.Record(new { Type = "verification_missing", FrameTime = sources[3].Timestamp, Hands = Array.Empty<object>() });
            live.Tick(sources[1].Timestamp.AddSeconds(4.9));
            Require(live.Status.IsRecording, "An empty detection ended recording before the five-second grace period.");
            // The session must continue across a temporary empty detection.
            live.ObserveFrame(sources[4]);
            live.Tick(sources[1].Timestamp.AddSeconds(5.1));
            Require(!live.Status.IsRecording, "Recording continued beyond five seconds without a detected hand.");
        }
        finally { await live.DisposeAsync(); }

        Require(live.Status.Error is null, "The functional recording failed: " + live.Status.Error);
        var clip = ReadClip(timeline);
        var sessionStart = clip.Entries.Single(entry => Type(entry) == "session_start");
        Require(sessionStart.GetProperty("triggerFrameTime").GetDateTimeOffset() == sources[1].Timestamp &&
            sessionStart.GetProperty("timelineOriginUtc").GetDateTimeOffset() == sources[0].Timestamp,
            "The session header did not distinguish the trigger frame from the prebuffer timeline origin.");
        Require(clip.Entries.Single(entry => Type(entry) == "session_end").GetProperty("reason").GetString() == "no_hand",
            "The five-second detection timeout did not finish the recording with its own stop reason.");
        var frames = VideoFrames(clip.Entries);
        Require(frames.Length >= 145, "Sparse frames compressed almost five seconds into a short video.");
        Require(frames[0].GetProperty("frameTime").GetDateTimeOffset() == sources[0].Timestamp,
            "The pre-trigger camera frame was omitted from the clip.");
        foreach (CameraFrame source in sources)
            Require(frames.Any(entry => entry.GetProperty("sourceFrameId").GetString() == FrameId(source)),
                "A distinct prebuffer, trigger or post-loss camera frame was omitted.");
        Require(frames.Any(entry => entry.GetProperty("duplicate").GetBoolean()),
            "A camera gap was not represented by timestamped duplicate video frames.");
        Require(clip.Entries.Any(entry => Type(entry) == "verification_detection" &&
                entry.GetProperty("frameTime").GetDateTimeOffset() == sources[1].Timestamp &&
                entry.GetProperty("sourceFrameId").GetString() == FrameId(sources[1])) &&
            clip.Entries.Any(entry => Type(entry) == "verification_missing" &&
                entry.GetProperty("hands").GetArrayLength() == 0),
            "Detection and empty-detection metadata did not retain their source-frame identity.");
        var decoded = DecodeAndCorrelate(clip.VideoPath, frames, sources);
        Require(decoded.FramesPerSecond == 30 && decoded.DecodedFrames == frames.Length,
            "Encoded video and sidecar disagree on frame count or playback rate.");
        Require(decoded.DecodedFrames / decoded.FramesPerSecond is >= 4.9 and < 5.4,
            "Recorded playback duration does not preserve the elapsed camera timeline.");
        for (int index = 0; index < sources.Length; index++)
            Require(sources[index].Bgra.SequenceEqual(originalPixels[index]), "Recording modified a raw BGRA camera buffer.");

        string quantized = Path.Combine(root, "trigger-slot");
        var slotRecorder = new HandTrackingVideoRecorder(quantized);
        var slotTime = DateTimeOffset.UtcNow;
        var previousSlot = Frame(slotTime, 30, 60, 190);
        var triggerSlot = Frame(slotTime.AddMilliseconds(10), 30, 190, 60);
        var previousSlotPixels = previousSlot.Bgra.ToArray();
        var triggerSlotPixels = triggerSlot.Bgra.ToArray();
        slotRecorder.ObserveFrame(previousSlot);
        slotRecorder.ObserveFrame(triggerSlot);
        slotRecorder.HandDetected(triggerSlot);
        await slotRecorder.DisposeAsync();
        var slotClip = ReadClip(quantized);
        var slotFrames = VideoFrames(slotClip.Entries);
        Require(slotFrames.Any(entry => entry.GetProperty("sourceFrameId").GetString() == FrameId(triggerSlot) &&
            !entry.GetProperty("duplicate").GetBoolean() && entry.GetProperty("triggerFrame").GetBoolean()),
            "A trigger sharing a 30fps time slot was discarded as a redundant prebuffer frame.");
        DecodeAndCorrelate(slotClip.VideoPath, slotFrames, [previousSlot, triggerSlot]);
        Require(previousSlot.Bgra.SequenceEqual(previousSlotPixels) && triggerSlot.Bgra.SequenceEqual(triggerSlotPixels),
            "Preserving a trigger time-slot collision modified the source camera pixels.");

        string stopped = Path.Combine(root, "stopped");
        var stoppedRecorder = new HandTrackingVideoRecorder(stopped);
        var stopFrame = Frame(DateTimeOffset.UtcNow, 40, 180, 220);
        stoppedRecorder.HandDetected(stopFrame);
        stoppedRecorder.Stop("verification-camera-reset");
        stoppedRecorder.ObserveFrame(Frame(stopFrame.Timestamp.AddMilliseconds(80), 200, 120, 30));
        await stoppedRecorder.DisposeAsync();
        await stoppedRecorder.DisposeAsync();
        var stoppedClip = ReadClip(stopped);
        Require(stoppedClip.Entries.Any(entry => Type(entry) == "session_end" &&
            entry.GetProperty("reason").GetString() == "verification-camera-reset"),
            "Explicit reset did not close the clip with its stop reason.");
        Require(VideoFrames(stoppedClip.Entries).All(entry =>
            entry.GetProperty("sourceFrameId").GetString() == FrameId(stopFrame)),
            "A camera frame after reset entered the completed clip.");
        DecodeAndCorrelate(stoppedClip.VideoPath, VideoFrames(stoppedClip.Entries), [stopFrame]);

        string disposed = Path.Combine(root, "disposed");
        var disposing = new HandTrackingVideoRecorder(disposed);
        var disposeFrame = Frame(DateTimeOffset.UtcNow, 180, 180, 40);
        disposing.HandDetected(disposeFrame);
        await disposing.DisposeAsync();
        var disposedClip = ReadClip(disposed);
        DecodeAndCorrelate(disposedClip.VideoPath, VideoFrames(disposedClip.Entries), [disposeFrame]);
        int filesAfterDispose = Directory.GetFiles(disposed).Length;
        disposing.ObserveFrame(disposeFrame);
        disposing.HandDetected(disposeFrame);
        disposing.Record(new { Type = "after_dispose" });
        await disposing.DisposeAsync();
        Require(Directory.GetFiles(disposed).Length == filesAfterDispose,
            "Using a disposed recorder reopened a video or sidecar.");

        string invalid = Path.Combine(root, "not-a-directory");
        File.WriteAllText(invalid, "regular file");
        var broken = new HandTrackingVideoRecorder(invalid);
        broken.HandDetected(Frame(DateTimeOffset.UtcNow, 20, 80, 140));
        await broken.DisposeAsync();
        Require(broken.Status.Error is not null && !broken.Status.IsRecording,
            "An unwritable recording directory threw or silently hid its failure.");

        string overloaded = Path.Combine(root, "overloaded");
        var overload = new HandTrackingVideoRecorder(overloaded);
        var blocker = new BlockingEntry();
        long droppedFrames;
        var overloadSources = new List<CameraFrame>();
        try
        {
            var first = Frame(DateTimeOffset.UtcNow, 20, 120, 200);
            overloadSources.Add(first);
            overload.HandDetected(first);
            var callerTime = Stopwatch.StartNew();
            overload.Record(blocker);
            Require(callerTime.Elapsed < TimeSpan.FromSeconds(1), "Recording serialized metadata on the caller thread.");
            Require(blocker.Entered.Wait(TimeSpan.FromSeconds(5)), "The recording metadata worker did not start.");
            for (int index = 1; index <= 40; index++)
            {
                var frame = Frame(first.Timestamp.AddMilliseconds(index * 40), 20, 120, 200);
                overloadSources.Add(frame);
                overload.ObserveFrame(frame);
            }
            droppedFrames = overload.Status.DroppedFrames;
            Require(droppedFrames > 0 && overload.Status.QueuedFrames <= 9,
                "A blocked encoder allowed an unbounded camera queue or silently lost overflow frames.");
            var completing = overload.DisposeAsync();
            Require(!completing.IsCompleted, "Disposal did not wait for accepted recording work.");
            blocker.Release.Set();
            await completing;
            Require(overload.Status.QueuedFrames == 0 && overload.Status.QueuedRecords == 0,
                "Disposal left accepted frames or metadata queued.");
        }
        finally { blocker.Release.Set(); await overload.DisposeAsync(); }
        var overloadClip = ReadClip(overloaded);
        DecodeAndCorrelate(overloadClip.VideoPath, VideoFrames(overloadClip.Entries), overloadSources);

        // Full camera dimensions exercise the actual codec cost. Report timing
        // and drops rather than assuming identical throughput on every laptop.
        string fullHd = Path.Combine(root, "full-hd");
        var throughput = new HandTrackingVideoRecorder(fullHd);
        var fullHdSources = new List<CameraFrame>();
        var fullHdPixels = Frame(DateTimeOffset.UtcNow, 30, 140, 200, 1920, 1080);
        var elapsed = Stopwatch.StartNew();
        double acquisitionMilliseconds;
        double drainMilliseconds;
        try
        {
            for (int index = 0; index < 60; index++)
            {
                var frame = fullHdPixels with { Timestamp = DateTimeOffset.UtcNow };
                fullHdSources.Add(frame);
                throughput.ObserveFrame(frame);
                if (index % 15 == 0) throughput.HandDetected(frame);
                if (index < 59) await Task.Delay(TimeSpan.FromMilliseconds(1000.0 / 30));
            }
            acquisitionMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            throughput.Stop("verification-throughput-complete");
            var drain = Stopwatch.StartNew();
            await throughput.DisposeAsync();
            drainMilliseconds = drain.Elapsed.TotalMilliseconds;
        }
        finally { await throughput.DisposeAsync(); }
        var fullHdClip = ReadClip(fullHd);
        var fullHdDecoded = DecodeAndCorrelate(fullHdClip.VideoPath, VideoFrames(fullHdClip.Entries), fullHdSources);
        string previewPath = Path.Combine(root, "full-hd-timestamp-preview.png");
        using (var previewCapture = new VideoCapture(fullHdClip.VideoPath))
        using (var preview = new Mat())
        {
            Require(previewCapture.Read(preview) && !preview.Empty(), "The full-HD timestamp preview could not be decoded.");
            Require(Cv2.ImWrite(previewPath, preview), "The full-HD timestamp preview could not be saved.");
        }

        return new { passed = true, decodedFrames = decoded.DecodedFrames, fps = decoded.FramesPerSecond,
            sourceFrames = sources.Length, idleCreatesNoFiles = true, triggerAndPrebufferRetained = true,
            quantizedTriggerRetained = true,
            correlatedMetadata = true, cameraTimelinePreserved = true, sourcePixelsUnchanged = true,
            fiveSecondNoHandStop = true, explicitStopAndDisposal = true, invalidDirectoryReported = true,
            overloadDroppedFrames = droppedFrames, acceptedWorkDrained = true,
            fullHd = new { width = 1920, height = 1080, suppliedFrames = 60,
                decodedFrames = fullHdDecoded.DecodedFrames, fps = fullHdDecoded.FramesPerSecond,
                droppedFrames = throughput.Status.DroppedFrames, error = throughput.Status.Error,
                acquisitionMilliseconds, drainMilliseconds },
            directory = root, previewPath };
    }

    private static (string VideoPath, JsonElement[] Entries) ReadClip(string directory)
    {
        string[] videos = Directory.GetFiles(directory, "*.avi");
        Require(videos.Length == 1, "Expected exactly one completed video session.");
        string sidecar = Path.ChangeExtension(videos[0], ".jsonl");
        Require(File.Exists(sidecar), "The video did not have a matching JSONL sidecar.");
        JsonElement[] entries = File.ReadAllLines(sidecar).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        Require(entries.Count(entry => Type(entry) == "session_start") == 1 &&
            entries.Count(entry => Type(entry) == "session_end") == 1, "The clip lacks a single start/end boundary.");
        return (videos[0], entries);
    }

    private static JsonElement[] VideoFrames(JsonElement[] entries) =>
        entries.Where(entry => Type(entry) == "video_frame").ToArray();

    private static (int DecodedFrames, double FramesPerSecond) DecodeAndCorrelate(string path,
        JsonElement[] entries, IReadOnlyList<CameraFrame> sources)
    {
        var byId = sources.ToDictionary(FrameId, StringComparer.Ordinal);
        using var capture = new VideoCapture(path);
        Require(capture.IsOpened(), "The completed recording cannot be decoded.");
        double fps = capture.Get(VideoCaptureProperties.Fps);
        using var image = new Mat();
        int index = 0;
        while (capture.Read(image) && !image.Empty())
        {
            Require(index < entries.Length, "The video contains frames missing from its sidecar.");
            JsonElement entry = entries[index];
            Require(entry.GetProperty("frameIndex").GetInt32() == index &&
                Math.Abs(entry.GetProperty("playbackMilliseconds").GetDouble() - index * 1000.0 / fps) < .01,
                "The sidecar video index/timestamp sequence does not match playback.");
            string sourceId = entry.GetProperty("sourceFrameId").GetString()!;
            Require(byId.TryGetValue(sourceId, out CameraFrame? source), "A video frame refers to an unknown source image.");
            Require(entry.GetProperty("frameTime").GetDateTimeOffset() == source!.Timestamp,
                "A video frame's source timestamp and source ID disagree.");
            Require(image.Width == source.Width && image.Height == source.Height,
                "Encoding changed the camera frame dimensions.");
            // The top of the encoded image intentionally contains UTC and frame
            // labels. Match the untouched camera image below that annotation.
            using var photoArea = new Mat(image, new Rect(0, image.Height * 3 / 4, image.Width, image.Height / 4));
            Scalar mean = Cv2.Mean(photoArea);
            Require(Math.Abs(mean.Val0 - source.Bgra[0]) <= 12 &&
                Math.Abs(mean.Val1 - source.Bgra[1]) <= 12 && Math.Abs(mean.Val2 - source.Bgra[2]) <= 12,
                "Decoded video pixels do not match the raw camera image named by the sidecar.");
            index++;
        }
        Require(index > 0 && index == entries.Length, "The sidecar contains missing or undecodable video frames.");
        return (index, fps);
    }

    private static CameraFrame Frame(DateTimeOffset timestamp, byte blue, byte green, byte red,
        int width = 320, int height = 240)
    {
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue; pixels[index + 1] = green; pixels[index + 2] = red; pixels[index + 3] = 255;
        }
        return new(width, height, width * 4, pixels, timestamp);
    }

    private static string FrameId(CameraFrame frame) => frame.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture);
    private static string? Type(JsonElement entry) => entry.TryGetProperty("type", out var type) ? type.GetString() : null;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BlockingEntry
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public string Type { get { Entered.Set(); Release.Wait(); return "blocked_verification"; } }
    }
}
#endif
