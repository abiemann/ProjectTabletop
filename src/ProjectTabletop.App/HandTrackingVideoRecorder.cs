using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCvSharp;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

/// <summary>
/// Records owned camera frames and their matching detection metadata. Public
/// calls only queue references; encoding, serialization and files use one worker.
/// Frame pixels are read-only. The caller limits observation to the tester.
/// </summary>
internal sealed class HandTrackingVideoRecorder : IAsyncDisposable
{
    private const int Fps = 30, FrameCapacity = 8, MetadataCapacity = 256;
    private const int FramesPerSegment = Fps * 120;
    private static readonly TimeSpan PrebufferTime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HandTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Queue<CameraFrame> _prebuffer = new();
    private readonly Queue<Work> _work = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Task _worker;
    private Session? _active, _finishing, _last;
    private int _queuedFrames, _queuedRecords;
    private bool _disposed, _faulted;
    private string? _error;

    public HandTrackingVideoRecorder(string directory)
    {
        _directory = directory;
        _worker = Task.Run(WorkAsync);
    }

    public HandTrackingVideoRecorderStatus Status
    {
        get
        {
            lock (_gate)
                return new(_last?.Id, _last?.VideoPath, _last?.TimelinePath, _active is not null,
                    _last?.WrittenFrames ?? 0, _last?.DroppedFrames ?? 0, _last?.DroppedRecords ?? 0,
                    _error, _queuedFrames, _queuedRecords, _last?.StartedAtUtc, _last?.LastHandUtc,
                    _last?.Origin, Fps, _last?.SegmentNumber ?? 0, _last?.WrittenRecords ?? 0);
        }
    }

    public void ObserveFrame(CameraFrame frame)
    {
        if (frame is null) return;
        lock (_gate)
        {
            if (_disposed || _faulted) return;
            if (!Valid(frame))
            {
                if (_active is { } invalid) DropFrame(invalid, frame.Timestamp, "Invalid camera frame.");
                return;
            }
            if (_active is { } session)
            {
                if (frame.Timestamp - session.LastHandUtc >= HandTimeout)
                    StopLocked("no_hand", frame.Timestamp);
                else if (frame.Width != session.Width || frame.Height != session.Height)
                    StopLocked("camera_dimensions_changed");
                else if (frame.Timestamp - session.LastQueuedFrame > HandTimeout)
                    StopLocked("camera_timestamp_gap");
                else
                {
                    QueueFrame(session, frame);
                    return;
                }
            }
            if (_finishing is not null) return;
            if (_prebuffer.Count > 0 && frame.Timestamp <= _prebuffer.Last().Timestamp) return;
            _prebuffer.Enqueue(frame);
            while (_prebuffer.Count > FrameCapacity || frame.Timestamp - _prebuffer.Peek().Timestamp > PrebufferTime)
                _prebuffer.Dequeue();
        }
    }

    /// <summary>The caller supplies a fresh, accepted detection from this exact source frame.</summary>
    public void HandDetected(CameraFrame frame)
    {
        if (frame is null) return;
        lock (_gate)
        {
            if (_disposed || _faulted || !Valid(frame)) return;
            if (_active is { } existing)
            {
                if (frame.Timestamp > existing.LastHandUtc) existing.LastHandUtc = frame.Timestamp;
                return;
            }
            if (_finishing is not null) return;
            var frames = _prebuffer.Where(item => item.Width == frame.Width && item.Height == frame.Height &&
                item.Timestamp >= frame.Timestamp - PrebufferTime).Append(frame)
                .DistinctBy(item => item.Timestamp).OrderBy(item => item.Timestamp).ToList();
            while (frames.Count > FrameCapacity)
            {
                // Keep the source image that actually triggered detection, even
                // when inference finished after it left the short idle buffer.
                int remove = frames.FindIndex(item => item.Timestamp != frame.Timestamp);
                frames.RemoveAt(remove);
            }
            _prebuffer.Clear();
            var session = new Session(frame, frames[0].Timestamp);
            _active = _last = session;
            _error = null;
            Enqueue(new Start(session));
            foreach (CameraFrame buffered in frames) QueueFrame(session, buffered);
        }
    }

    public void Record(object immutableEntry)
    {
        lock (_gate)
        {
            if (_disposed || _active is not { } session) return;
            if (immutableEntry is null || _queuedRecords >= MetadataCapacity)
            {
                session.DroppedRecords++;
                _error = "Recording metadata queue is full or the entry is null.";
                return;
            }
            _queuedRecords++;
            Enqueue(new Metadata(session, immutableEntry));
        }
    }

    public void Tick(DateTimeOffset now)
    {
        lock (_gate)
            if (_active is { } session && now - session.LastHandUtc >= HandTimeout) StopLocked("no_hand", now);
    }

    public void Stop(string reason)
    {
        lock (_gate)
        {
            _prebuffer.Clear();
            StopLocked(reason ?? "stopped");
            // A deliberate camera/tester state transition permits a later retry.
            // A failing recorder otherwise remains latched against rapid starts.
            _faulted = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                StopLocked("disposed");
                _prebuffer.Clear();
                _disposed = true;
                _wake.Release();
            }
        }
        return new ValueTask(_worker);
    }

    private void StopLocked(string reason, DateTimeOffset? requestedAt = null)
    {
        if (_active is not { } session) return;
        _active = null;
        _finishing = session;
        _prebuffer.Clear();
        Enqueue(new End(session, reason, requestedAt ?? MonotonicClock.UtcNow));
    }

    private void QueueFrame(Session session, CameraFrame frame)
    {
        if (frame.Timestamp <= session.LastQueuedFrame) return;
        session.LastQueuedFrame = frame.Timestamp;
        if (_queuedFrames >= FrameCapacity)
        {
            DropFrame(session, frame.Timestamp, "Recording frame queue is full.");
            return;
        }
        _queuedFrames++;
        Enqueue(new Frame(session, frame));
    }

    private void DropFrame(Session session, DateTimeOffset frameTime, string? error = null)
    {
        session.DroppedFrames++;
        session.FirstDroppedFrameTime ??= frameTime;
        session.LastDroppedFrameTime = frameTime;
        if (error is not null) _error = error;
    }

    private void Enqueue(Work work) { _work.Enqueue(work); _wake.Release(); }

    private async Task WorkAsync()
    {
        Encoder? encoder = null;
        while (true)
        {
            await _wake.WaitAsync().ConfigureAwait(false);
            Work? work;
            lock (_gate)
            {
                if (_work.Count == 0)
                {
                    if (_disposed) break;
                    continue;
                }
                work = _work.Dequeue();
                if (work is Frame) _queuedFrames--;
                if (work is Metadata) _queuedRecords--;
            }
            try
            {
                if (work is Start start)
                {
                    encoder = new Encoder(start.Session);
                    await OpenSegmentAsync(encoder).ConfigureAwait(false);
                }
                else if (work is End end)
                {
                    if (encoder is not null)
                    {
                        await FinishAsync(encoder, end.Reason, end.RequestedAtUtc).ConfigureAwait(false);
                        encoder = null;
                    }
                }
                else if (work.Session.Failed || encoder is null)
                {
                    lock (_gate)
                    {
                        if (work is Frame frame) DropFrame(work.Session, frame.Source.Timestamp);
                        if (work is Metadata) work.Session.DroppedRecords++;
                    }
                }
                else if (work is Frame frame)
                    await WriteFrameAsync(encoder, frame.Source).ConfigureAwait(false);
                else if (work is Metadata metadata)
                {
                    // Serialization failure is a metadata loss, not a reason to
                    // discard an otherwise usable video recording.
                    try
                    {
                        string json = JsonSerializer.Serialize(metadata.Entry, metadata.Entry.GetType(), JsonOptions);
                        await WriteJsonAsync(encoder, json).ConfigureAwait(false);
                        lock (_gate) metadata.Session.WrittenRecords++;
                    }
                    catch (JsonException error)
                    {
                        lock (_gate) { metadata.Session.DroppedRecords++; _error = error.Message; }
                    }
                    catch (NotSupportedException error)
                    {
                        lock (_gate) { metadata.Session.DroppedRecords++; _error = error.Message; }
                    }
                }
                if (encoder is not null && !work.Session.Failed)
                    await ReportDropsAsync(encoder).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    work.Session.Failed = true;
                    _faulted = true;
                    work.Session.Error = _error = "Hand recording failed: " + error.Message;
                    if (work is Frame failedFrame) DropFrame(work.Session, failedFrame.Source.Timestamp);
                    if (work is Metadata) work.Session.DroppedRecords++;
                    if (ReferenceEquals(_active, work.Session)) StopLocked("recording_error");
                    // Include queued losses in the final sidecar before closing
                    // it. Keep the End control to release the finishing barrier.
                    int pending = _work.Count;
                    for (int index = 0; index < pending; index++)
                    {
                        Work queued = _work.Dequeue();
                        if (!ReferenceEquals(queued.Session, work.Session)) { _work.Enqueue(queued); continue; }
                        if (queued is Frame discarded)
                        {
                            _queuedFrames--;
                            DropFrame(work.Session, discarded.Source.Timestamp);
                        }
                        else if (queued is Metadata)
                        {
                            _queuedRecords--;
                            work.Session.DroppedRecords++;
                        }
                        else _work.Enqueue(queued);
                    }
                    _prebuffer.Clear();
                }
                if (encoder is not null)
                {
                    try { await FinishAsync(encoder, "recording_error").ConfigureAwait(false); }
                    catch { encoder.Dispose(); }
                    encoder = null;
                }
            }
            finally
            {
                // A failed final flush must not permanently block future starts
                // after an explicit state transition has permitted a retry.
                if (work is End end)
                    lock (_gate) if (ReferenceEquals(_finishing, end.Session)) _finishing = null;
            }
        }
        if (encoder is not null)
        {
            try { await FinishAsync(encoder, "disposed").ConfigureAwait(false); }
            catch { encoder.Dispose(); }
        }
    }

    private async Task WriteFrameAsync(Encoder encoder, CameraFrame source)
    {
        long target = (long)Math.Round((source.Timestamp - encoder.Session.Origin).TotalSeconds * Fps,
            MidpointRounding.AwayFromZero);
        // Timestamp jitter can place two source frames in the same 30fps slot.
        // Preserve the actual initiating image even if it needs the next slot;
        // its exact source timestamp remains explicit in the sidecar.
        if (target < encoder.NextIndex && source.Timestamp == encoder.Session.TriggerTime)
            target = encoder.NextIndex;
        if (target < encoder.NextIndex)
        {
            lock (_gate) DropFrame(encoder.Session, source.Timestamp);
            return;
        }
        while (encoder.NextIndex < target && encoder.LastBgr is not null)
            await EncodeAsync(encoder, encoder.LastBgr, encoder.LastFrameTime, duplicate: true).ConfigureAwait(false);
        using Mat bgra = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC4, source.Bgra, source.Stride);
        using Mat bgr = new();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        await EncodeAsync(encoder, bgr, source.Timestamp, duplicate: false).ConfigureAwait(false);
        encoder.LastBgr?.Dispose();
        encoder.LastBgr = bgr.Clone();
        encoder.LastFrameTime = source.Timestamp;
    }

    private async Task EncodeAsync(Encoder encoder, Mat sourceBgr, DateTimeOffset sourceTime, bool duplicate)
    {
        if (encoder.NextIndex > 0 && encoder.NextIndex % FramesPerSegment == 0)
        {
            await CloseSegmentAsync(encoder, "segment_limit").ConfigureAwait(false);
            await OpenSegmentAsync(encoder).ConfigureAwait(false);
        }
        long frameIndex = encoder.NextIndex % FramesPerSegment;
        double playbackMilliseconds = frameIndex * 1000.0 / Fps;
        using Mat annotated = sourceBgr.Clone();
        string[] labels =
        [
            sourceTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture),
            $"frame {frameIndex}  video {playbackMilliseconds / 1000:F3}s{(duplicate ? "  DUP" : "") }"
        ];
        double fontScale = Math.Clamp(sourceBgr.Width / 1600.0, .35, .8);
        int lineHeight = Math.Max(16, (int)Math.Round(30 * fontScale));
        for (int row = 0; row < labels.Length; row++)
        {
            var origin = new Point(6, 6 + (row + 1) * lineHeight);
            Cv2.PutText(annotated, labels[row], origin, HersheyFonts.HersheySimplex, fontScale, Scalar.Black, 3, LineTypes.AntiAlias);
            Cv2.PutText(annotated, labels[row], origin, HersheyFonts.HersheySimplex, fontScale, Scalar.White, 1, LineTypes.AntiAlias);
        }
        encoder.Video!.Write(annotated);
        await WriteJsonAsync(encoder, new
        {
            type = "video_frame", recordingId = encoder.Session.Id, segment = encoder.Segment,
            frameIndex, playbackMilliseconds, frameTime = sourceTime,
            sourceFrameId = sourceTime.UtcTicks.ToString(CultureInfo.InvariantCulture), duplicate,
            triggerFrame = sourceTime == encoder.Session.TriggerTime
        }).ConfigureAwait(false);
        encoder.NextIndex++;
        lock (_gate) encoder.Session.WrittenFrames++;
    }

    private async Task OpenSegmentAsync(Encoder encoder)
    {
        Directory.CreateDirectory(_directory);
        encoder.Segment++;
        string stem = Path.Combine(_directory, $"hand-tracking-{encoder.Session.Id}-{encoder.Segment:D3}");
        string videoPath = stem + ".avi", timelinePath = stem + ".jsonl";
        encoder.Timeline = new StreamWriter(new FileStream(timelinePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite, 4096, FileOptions.Asynchronous), new UTF8Encoding(false));
        // The built-in MJPEG backend keeps diagnostic recording independent of
        // OpenCV's optional FFmpeg plugin, which is not shipped in the installer.
        encoder.Video = new VideoWriter(videoPath, VideoCaptureAPIs.OPENCV_MJPEG, FourCC.MJPG, Fps,
            new Size(encoder.Session.Width, encoder.Session.Height));
        if (!encoder.Video.IsOpened()) throw new IOException("MJPG video encoder could not open the output file.");
        lock (_gate)
        {
            encoder.Session.VideoPath = videoPath;
            encoder.Session.TimelinePath = timelinePath;
            encoder.Session.SegmentNumber = encoder.Segment;
        }
        await WriteJsonAsync(encoder, new
        {
            type = "session_start", recordingId = encoder.Session.Id, segment = encoder.Segment,
            startedAtUtc = encoder.Session.StartedAtUtc, triggerFrameTime = encoder.Session.TriggerTime,
            timelineOriginUtc = encoder.Session.Origin.AddSeconds((encoder.Segment - 1) * 120),
            fps = Fps, width = encoder.Session.Width, height = encoder.Session.Height,
            timestampMeaning = "Host UTC at app start plus monotonic elapsed time, assigned after camera BGRA copy; not a hardware exposure timestamp.",
            videoPath, timelinePath
        }).ConfigureAwait(false);
    }

    private async Task ReportDropsAsync(Encoder encoder)
    {
        long frames, records;
        DateTimeOffset? first, last;
        string? error;
        lock (_gate)
        {
            frames = encoder.Session.DroppedFrames; records = encoder.Session.DroppedRecords;
            first = encoder.Session.FirstDroppedFrameTime; last = encoder.Session.LastDroppedFrameTime;
            error = _error;
        }
        if (frames == encoder.ReportedFrameDrops && records == encoder.ReportedRecordDrops) return;
        await WriteJsonAsync(encoder, new { type = "recorder_drops", recordingId = encoder.Session.Id,
            droppedFrames = frames, droppedRecords = records, firstDroppedFrameTime = first,
            lastDroppedFrameTime = last, error }).ConfigureAwait(false);
        encoder.ReportedFrameDrops = frames;
        encoder.ReportedRecordDrops = records;
    }

    private async Task CloseSegmentAsync(Encoder encoder, string reason, DateTimeOffset? requestedAt = null)
    {
        try
        {
            if (encoder.Timeline is not null)
            {
                await ReportDropsAsync(encoder).ConfigureAwait(false);
                long frames, droppedFrames, droppedRecords;
                lock (_gate)
                {
                    frames = encoder.Session.WrittenFrames; droppedFrames = encoder.Session.DroppedFrames;
                    droppedRecords = encoder.Session.DroppedRecords;
                }
                await WriteJsonAsync(encoder, new { type = "session_end", recordingId = encoder.Session.Id,
                    segment = encoder.Segment, reason, endedAtUtc = MonotonicClock.UtcNow,
                    stopRequestedAtUtc = requestedAt,
                    segmentFrames = encoder.NextIndex - (encoder.Segment - 1L) * FramesPerSegment,
                    writtenFrames = frames, droppedFrames, droppedRecords, error = encoder.Session.Error }).ConfigureAwait(false);
            }
        }
        finally
        {
            encoder.Video?.Dispose(); encoder.Video = null;
            if (encoder.Timeline is { } timeline)
            {
                encoder.Timeline = null;
                await timeline.DisposeAsync().ConfigureAwait(false);
            }
        }
        RetainCompletedPairs();
    }

    private async Task FinishAsync(Encoder encoder, string reason, DateTimeOffset? requestedAt = null)
    {
        try { await CloseSegmentAsync(encoder, reason, requestedAt).ConfigureAwait(false); }
        finally { encoder.LastBgr?.Dispose(); encoder.LastBgr = null; }
    }

    private static async Task WriteJsonAsync(Encoder encoder, object entry)
    {
        string json = entry is string text ? text : JsonSerializer.Serialize(entry, entry.GetType(), JsonOptions);
        await encoder.Timeline!.WriteLineAsync(json).ConfigureAwait(false);
        await encoder.Timeline.FlushAsync().ConfigureAwait(false);
    }

    private void RetainCompletedPairs()
    {
        try
        {
            var pairs = new List<(string Video, string Timeline, long Bytes)>();
            foreach (string video in Directory.EnumerateFiles(_directory, "hand-tracking-*.avi"))
            {
                if (!Regex.IsMatch(Path.GetFileName(video), @"^hand-tracking-\d{8}-\d{6}-\d{7}-[0-9a-f]{32}-\d{3,}\.avi$")) continue;
                string timeline = Path.ChangeExtension(video, ".jsonl");
                if (!File.Exists(timeline) || !HasSessionEnd(timeline)) continue;
                pairs.Add((video, timeline, new FileInfo(video).Length + new FileInfo(timeline).Length));
            }
            pairs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Video, b.Video));
            long bytes = pairs.Sum(pair => pair.Bytes);
            int remaining = pairs.Count;
            foreach (var pair in pairs)
            {
                if (remaining <= 20 && bytes <= 2L * 1024 * 1024 * 1024) break;
                File.Delete(pair.Video);
                File.Delete(pair.Timeline);
                bytes -= pair.Bytes;
                remaining--;
            }
        }
        catch (Exception error) { lock (_gate) _error = "Recording retention failed: " + error.Message; }
    }

    private static bool HasSessionEnd(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Seek(Math.Max(0, file.Length - 4096), SeekOrigin.Begin);
        using var reader = new StreamReader(file);
        string? last = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is null) return false;
        try
        {
            using var json = JsonDocument.Parse(last);
            return json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "session_end";
        }
        catch (JsonException) { return false; }
    }

    private static bool Valid(CameraFrame frame) => frame.Width > 0 && frame.Height > 0 &&
        frame.Width <= 16384 && frame.Height <= 16384 && frame.Stride >= frame.Width * 4L &&
        frame.Bgra is not null && frame.Bgra.LongLength >= (frame.Height - 1L) * frame.Stride + frame.Width * 4L;

    private sealed class Session(CameraFrame trigger, DateTimeOffset origin)
    {
        public readonly string Id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}";
        public readonly DateTimeOffset StartedAtUtc = MonotonicClock.UtcNow, TriggerTime = trigger.Timestamp, Origin = origin;
        public readonly int Width = trigger.Width, Height = trigger.Height;
        public DateTimeOffset LastHandUtc = trigger.Timestamp, LastQueuedFrame = DateTimeOffset.MinValue;
        public DateTimeOffset? FirstDroppedFrameTime, LastDroppedFrameTime;
        public long WrittenFrames, WrittenRecords, DroppedFrames, DroppedRecords;
        public string? VideoPath, TimelinePath, Error;
        public int SegmentNumber;
        public bool Failed;
    }

    private sealed class Encoder(Session session) : IDisposable
    {
        public readonly Session Session = session;
        public VideoWriter? Video;
        public StreamWriter? Timeline;
        public Mat? LastBgr;
        public DateTimeOffset LastFrameTime;
        public long NextIndex, ReportedFrameDrops, ReportedRecordDrops;
        public int Segment;
        public void Dispose()
        {
            try { Video?.Dispose(); } catch { }
            try { Timeline?.Dispose(); } catch { }
            try { LastBgr?.Dispose(); } catch { }
            Video = null; Timeline = null; LastBgr = null;
        }
    }

    private abstract record Work(Session Session);
    private sealed record Start(Session Session) : Work(Session);
    private sealed record Frame(Session Session, CameraFrame Source) : Work(Session);
    private sealed record Metadata(Session Session, object Entry) : Work(Session);
    private sealed record End(Session Session, string Reason, DateTimeOffset RequestedAtUtc) : Work(Session);
}

internal sealed record HandTrackingVideoRecorderStatus(string? RecordingId, string? VideoPath,
    string? TimelinePath, bool IsRecording, long WrittenFrames, long DroppedFrames, long DroppedRecords,
    string? Error, int QueuedFrames, int QueuedRecords, DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastHandUtc, DateTimeOffset? TimelineOriginUtc, int Fps, int SegmentNumber, long WrittenRecords);
