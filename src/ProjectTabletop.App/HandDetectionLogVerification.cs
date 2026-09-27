#if DEBUG
using System.Diagnostics;
using System.Text.Json;

namespace ProjectTabletop.App;

/// <summary>Pure diagnostic logger checks, isolated from the running hand detector.</summary>
internal static class HandDetectionLogVerification
{
    public static Task<object> RunAsync(string directory) => Task.Run(() => VerifyAsync(directory));

    private static async Task<object> VerifyAsync(string root)
    {
        Directory.CreateDirectory(root);
        string lazy = Path.Combine(root, "lazy");
        await using (var log = new HandDetectionLog(lazy))
            Require(log.Status.CurrentPath is null && !Directory.Exists(lazy), "Constructor created the log directory.");
        Require(!Directory.Exists(lazy), "Disposing an unused log created files.");

        string live = Path.Combine(root, "live");
        await using (var log = new HandDetectionLog(live))
        {
            log.Record(new { Event = "missing", Hands = Array.Empty<object>(), InvalidAnchor = double.NaN });
            await Until(() => log.Status is { WrittenRecords: 1, QueuedRecords: 0 });
            Require(log.Status.QueuedRecords == 0 && log.Status.DroppedRecords == 0, "Written record accounting is wrong.");
            using var file = new FileStream(log.Status.CurrentPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            using var json = JsonDocument.Parse((await reader.ReadLineAsync())!);
            Require(json.RootElement.GetProperty("event").GetString() == "missing" &&
                json.RootElement.GetProperty("hands").GetArrayLength() == 0 &&
                json.RootElement.GetProperty("invalidAnchor").GetString() == "NaN", "Live JSON/casing/nonfinite metadata changed.");
        }

        string rotation = Path.Combine(root, "rotation");
        Directory.CreateDirectory(rotation);
        File.WriteAllText(Path.Combine(rotation, "unrelated.jsonl"), "leave me");
        var rotating = new HandDetectionLog(rotation, 150, 3, 100);
        for (int index = 0; index < 9; index++) rotating.Record(new { Index = index, Text = new string('x', 80) });
        await rotating.DisposeAsync();
        var rotationStatus = rotating.Status;
        string[] files = Directory.GetFiles(rotation, "hand-detections-*.jsonl").Order(StringComparer.Ordinal).ToArray();
        Require(files.Length == 3 && File.Exists(Path.Combine(rotation, "unrelated.jsonl")), "Retention deleted unrelated files or kept wrong count.");
        Require(rotationStatus is { QueuedRecords: 0, WrittenRecords: 9, DroppedRecords: 0, Error: null }, "Rotation lost or miscounted records.");
        var retained = files.SelectMany(File.ReadAllLines).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.GetProperty("index").GetInt32();
        }).ToArray();
        Require(retained.SequenceEqual(new[] { 6, 7, 8 }) && files.All(path => new FileInfo(path).Length <= 150),
            "Rotation did not retain the newest complete records within size limits.");

        string invalid = Path.Combine(root, "not-a-directory");
        File.WriteAllText(invalid, "regular file");
        var broken = new HandDetectionLog(invalid);
        broken.Record(new { Event = "cannot write" });
        await broken.DisposeAsync();
        Require(broken.Status is { QueuedRecords: 0, WrittenRecords: 0, DroppedRecords: 1, Error: not null },
            "An invalid output directory threw or silently lost a record.");

        var overload = new HandDetectionLog(Path.Combine(root, "overload"), 1024, 8, 1);
        var blocking = new BlockingEntry();
        try
        {
            var started = Stopwatch.StartNew();
            overload.Record(blocking);
            Require(started.Elapsed < TimeSpan.FromSeconds(1), "Record serialized a blocking entry on the caller.");
            Require(blocking.Entered.Wait(TimeSpan.FromSeconds(5)), "Serialization worker did not start.");
            overload.Record(new { Event = "queued" });
            overload.Record(new { Event = "overflow" });
            Require(overload.Status is { QueuedRecords: 2, WrittenRecords: 0, DroppedRecords: 1, Error: not null },
                "Queue overflow silently dropped an entry or miscounted pending work.");
            var finishing = overload.DisposeAsync();
            Require(!finishing.IsCompleted, "Dispose did not wait for accepted records.");
            blocking.Release.Set();
            await finishing;
            Require(overload.Status is { QueuedRecords: 0, WrittenRecords: 2, DroppedRecords: 1 }, "Dispose did not drain accepted records.");
            overload.Record(new { Event = "after dispose" });
            Require(overload.Status.DroppedRecords == 2, "Post-disposal write was not counted.");
            await overload.DisposeAsync();
        }
        finally { blocking.Release.Set(); await overload.DisposeAsync(); }

        var limits = new HandDetectionLog(Path.Combine(root, "limits"), 128, 8, 10);
        limits.Record(new { Huge = new string('x', 200) });
        var cycle = new CyclicEntry(); cycle.Self = cycle;
        limits.Record(cycle);
        limits.Record(new { Valid = true });
        await limits.DisposeAsync();
        Require(limits.Status is { QueuedRecords: 0, WrittenRecords: 1, DroppedRecords: 2, Error: not null },
            "Oversize/serialization failures affected later writes or were silently discarded.");
        Require(File.ReadAllLines(limits.Status.CurrentPath!).Length == 1, "Failed entries contaminated valid JSONL.");
        return new { passed = true, rotationRecords = 9, retainedFiles = 3, overloadDropped = 2,
            checks = "lazy creation, live tail, missing/nonfinite metadata, rotation/retention, invalid path, bounded queue, drain/idempotent disposal, serialization/oversize recovery" };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Logger did not finish in time.");
    }

    private sealed class BlockingEntry
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public string Event { get { Entered.Set(); Release.Wait(); return "blocked serialization"; } }
    }

    private sealed class CyclicEntry { public CyclicEntry? Self { get; set; } }
}
#endif
