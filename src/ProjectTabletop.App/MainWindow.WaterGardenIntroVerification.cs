#if DEBUG
using System.Diagnostics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.WaterGarden;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Offscreen completion timings, not live presentation/FPS measurements.
    // Frozen sweeps isolate shading; active sweeps include three seconds of
    // deterministic fountain, wave and duck simulation at 60 updates/second.
    private async Task<object> VerifyWaterGardenIntroAsync(string? profile = null)
    {
        const int samples = 61;
        int outputWidth = profile == "square" ? 1000 : 1920;
        int outputHeight = profile == "square" ? 1000 : 1080;
        double aspect = outputWidth / (double)outputHeight;
        var device = CanvasDevice.GetSharedDevice();
        var timer = Stopwatch.StartNew();
        using var stone = await CanvasBitmap.LoadAsync(device,
            Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "wet-slate.png"));
        using var sand = await CanvasBitmap.LoadAsync(device,
            Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "sand-ground.png"));
        double artworkLoadMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        using var water = new WaterGardenSimulation(device, 512, (int)Math.Round(512 / aspect), aspect, stone, sand);
        double constructionMilliseconds = timer.Elapsed.TotalMilliseconds;
        using var target = new CanvasRenderTarget(device, outputWidth, outputHeight, 96);
        var countField = typeof(WaterGardenSimulation).GetField("_activeDuckCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bindSurface = typeof(WaterGardenSimulation).GetMethod("BindSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action>(water);
        int actualDucks = (int)countField.GetValue(water)!;
        water.SetEntranceProgress(0);
        var firstDraw = Measure();

        // Exercise real flowing fountain shading and populated duck state before
        // freezing the physics, so the comparison only changes visible ducks.
        for (int frame = 0; frame < 90; frame++) water.Advance(1.0 / 30);
        for (int frame = 0; frame < 12; frame++)
        {
            Prepare((frame % 6) / 5f, frame % 2 == 0 ? actualDucks : 0);
            Measure();
        }
        var before = water.GetDiagnostics();
        var visible = new List<(float Progress, double Submission, double Completed)>();
        var hidden = new List<(float Progress, double Submission, double Completed)>();
        var passes = new List<object>();
        // ABBA order reduces clock/thermal/order bias without treating queued
        // drawing submission as completed GPU work.
        foreach (int count in new[] { actualDucks, 0, 0, actualDucks })
        {
            var frames = new List<(float Progress, double Submission, double Completed)>();
            for (int index = 0; index < samples; index++)
            {
                float progress = index / (float)(samples - 1);
                Prepare(progress, count);
                var elapsed = Measure();
                frames.Add((progress, elapsed.Submission, elapsed.Completed));
            }
            (count == 0 ? hidden : visible).AddRange(frames);
            passes.Add(new { visibleDucks = count, timings = Summarize(frames) });
        }
        var after = water.GetDiagnostics();
        if (before.SimulationSteps != after.SimulationSteps || before.DuckCount != after.DuckCount ||
            (int)countField.GetValue(water)! != actualDucks)
            throw new InvalidOperationException("The rendering benchmark changed the simulation or duck population.");

        string directory = Path.Combine(_appDataDirectory, "WaterGardenIntroVerification", $"{outputWidth}x{outputHeight}");
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        foreach (int count in new[] { actualDucks, 0 })
        {
            Prepare(.5f, count);
            Measure();
            string path = Path.Combine(directory, count == 0 ? "intro-without-ducks.png" : "intro-with-ducks.png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }
        var activePasses = new List<object>();
        foreach (int count in new[] { actualDucks, 0, 0, actualDucks })
        {
            timer.Restart();
            water.Reset();
            double resetMilliseconds = timer.Elapsed.TotalMilliseconds;
            var frames = new List<(int Frame, double Advance, double DrawCompleted, double Total)>();
            for (int frame = 0; frame < 180; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                water.Advance(1.0 / 60);
                double advance = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Prepare(frame / 179f, count);
                var draw = Measure();
                double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                frames.Add((frame, advance, draw.Completed, total));
            }
            var state = water.GetDiagnostics();
            if (state.SimulationSteps != 360 || state.DuckCount != actualDucks ||
                (int)countField.GetValue(water)! != actualDucks)
                throw new InvalidOperationException("An active intro pass changed its physics population or update count.");
            activePasses.Add(new { visibleDucks = count, resetMilliseconds,
                advanceMilliseconds = Distribution(frames.Select(frame => frame.Advance)),
                drawCompletedMilliseconds = Distribution(frames.Select(frame => frame.DrawCompleted)),
                totalFrameMilliseconds = Distribution(frames.Select(frame => frame.Total)),
                opening = Third(frames.Take(60)), middle = Third(frames.Skip(60).Take(60)),
                closing = Third(frames.Skip(120)),
                slowestFrames = frames.OrderByDescending(frame => frame.Total).Take(5)
                    .Select(frame => new { frame.Frame, advanceMilliseconds = frame.Advance,
                        drawCompletedMilliseconds = frame.DrawCompleted, totalMilliseconds = frame.Total }).ToArray(),
                simulatedSeconds = state.SimulatedSeconds });
        }
        return new { passed = true, outputWidth, outputHeight,
            artworkLoadMilliseconds, constructionMilliseconds,
            firstDraw = new { submissionMilliseconds = firstDraw.Submission, completedMilliseconds = firstDraw.Completed },
            visible = Summarize(visible), hidden = Summarize(hidden), passes, activePasses, images,
            frozenSimulationSeconds = before.SimulatedSeconds, actualDucks,
            timingIncludesReadbackSynchronization = true, livePresentationMeasured = false,
            windowCreated = false, cameraOpened = false };

        void Prepare(float progress, int visibleDucks)
        {
            water.SetEntranceProgress(progress);
            // Change only the shader constant, then restore the physical count
            // immediately. No Advance or duck-state update occurs while hidden.
            countField.SetValue(water, visibleDucks);
            try { bindSurface(); }
            finally { countField.SetValue(water, actualDucks); }
        }
        (double Submission, double Completed) Measure()
        {
            long start = Stopwatch.GetTimestamp();
            using (var drawing = target.CreateDrawingSession())
                water.Draw(drawing, new Rect(0, 0, outputWidth, outputHeight));
            double submission = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _ = target.GetPixelBytes(0, 0, 1, 1);
            return (submission, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        static object Summarize(List<(float Progress, double Submission, double Completed)> frames)
        {
            double[] completed = frames.Select(frame => frame.Completed).Order().ToArray();
            return new { count = frames.Count,
                meanSubmissionMilliseconds = frames.Average(frame => frame.Submission),
                meanCompletedMilliseconds = completed.Average(), medianCompletedMilliseconds = completed[completed.Length / 2],
                p95CompletedMilliseconds = completed[(int)Math.Ceiling((completed.Length - 1) * .95)],
                maximumCompletedMilliseconds = completed[^1],
                openingMeanMilliseconds = frames.Where(frame => frame.Progress < 1f / 3).Average(frame => frame.Completed),
                closingMeanMilliseconds = frames.Where(frame => frame.Progress > 2f / 3).Average(frame => frame.Completed) };
        }
        static object Third(IEnumerable<(int Frame, double Advance, double DrawCompleted, double Total)> frames) =>
            new { advanceMilliseconds = Distribution(frames.Select(frame => frame.Advance)),
                totalFrameMilliseconds = Distribution(frames.Select(frame => frame.Total)) };
        static object Distribution(IEnumerable<double> values)
        {
            double[] ordered = values.Order().ToArray();
            return new { count = ordered.Length, mean = ordered.Average(), median = ordered[ordered.Length / 2],
                p95 = ordered[(int)Math.Ceiling((ordered.Length - 1) * .95)], maximum = ordered[^1] };
        }
    }
}
#endif
