#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Real compositor frames with a synthetic clock; no live app or capture devices.
    private async Task<object> VerifyDuckHuntCrystalBurstAsync()
    {
        const int width = 1920, height = 1080, fps = 60, frames = 180;
        const double aspect = width / (double)height;
        int assertions = 0;
        var now = MonotonicClock.UtcNow;
        string directory = Path.Combine(_appDataDirectory, "CrystalBurst", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(aspect);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        now = MonotonicClock.UtcNow; // Calibration advances the real input timestamp.
        scene.ShowDuckHuntTargetPractice();
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        now = MonotonicClock.UtcNow.AddSeconds(1);
        scene.SetDuckHuntAudioStatus(true, "Isolated burst fixture");
        Draw();
        now += TimeSpan.FromMilliseconds(1);
        Check(ActivateGalleryPlay(scene), "The drawer PLAY button did not start the two-shelf gallery.");
        now += TimeSpan.FromSeconds(4);
        Draw();

        // Isolate the production effect so neighboring flickering skulls cannot
        // masquerade as an expanding cloud or moving settled glass.
        const int isolatedWidth = 3840, isolatedHeight = 2160;
        using var isolated = new CanvasRenderTarget(target.Device, isolatedWidth, isolatedHeight, 96);
        var drawShards = typeof(SceneCompositor).GetMethod("DrawGallerySkullShards",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bounds = DuckHuntGalleryLayout.SkullBounds(2, aspect);
        var measurements = new List<CrystalBurstPixels>();
        foreach (double seconds in new[] { .017, .080, .140, .220 })
        {
            var pixels = DrawIsolated(seconds);
            var measured = Measure(pixels, seconds);
            measurements.Add(measured);
            await isolated.SaveAsync(Path.Combine(directory, $"isolated-{seconds * 1000:000}.png"), CanvasBitmapFileFormat.Png);
        }
        var initial = measurements[0];
        var burst = measurements[2];
        Check(measurements.Skip(1).Max(sample => sample.Components) >= 40,
            "The burst does not separate into many small visible pieces.");
        Check(burst.Radius90 >= initial.Radius90 * 1.5,
            "The first 140 ms do not expand sharply outward.");
        Check(burst.LeftPixels >= 20 && burst.RightPixels >= 20,
            "Glass does not burst beyond both sides of the original skull.");
        Check(burst.UpperPixels >= 20, "The burst has no upward component and reads as a collapse.");
        byte[] settled = DrawIsolated(1.0);
        Check(settled.AsSpan().SequenceEqual(DrawIsolated(2.0)),
            "Glass has not settled before the final one-second curtain wait ends.");
        var resting = Measure(settled, 1.0);
        double shelfY = (bounds.Y + bounds.Height * DuckHuntGalleryLayout.SkullBaseV) * isolatedHeight;
        Check(resting.Pixels > 100 && resting.Bottom <= shelfY + 2 && resting.Top >= shelfY - bounds.Height * isolatedHeight * .12,
            "Persistent fragments are missing, floating well above the shelf, or falling through it.");
        await isolated.SaveAsync(Path.Combine(directory, "isolated-settled.png"), CanvasBitmapFileFormat.Png);

        // Save every simulated frame, not interpolated stills. This shows the
        // initial impulse, tumble, collisions and resting debris at normal speed.
        var origin = now + TimeSpan.FromSeconds(1);
        var detector = new RedDotDetector();
        var renderMilliseconds = new List<double>();
        for (int frame = 0; frame < frames; frame++)
        {
            now = origin + TimeSpan.FromSeconds(frame / (double)fps);
            if (frame is 30 or 90)
            {
                var skull = DuckHuntGalleryLayout.SkullBounds(frame == 30 ? 2 : 0, aspect);
                Check(scene.SubmitDuckHuntShot(new(skull.X + skull.Width * .5, skull.Y + skull.Height * .4), now),
                    "The motion capture could not hit its skull.");
            }
            long started = Stopwatch.GetTimestamp();
            Draw();
            renderMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (frame is 31 or 33 or 36 or 39 or 43 or 48 or 60 or 84 or 91 or 96 or 103 or 115 or 150)
            {
                var context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No optical context.");
                var detection = detector.Detect(width, height, width * 4, target.GetPixelBytes(), context.CameraToBoard);
                Check(!detection.Detected && detection.CandidateCount == 0,
                    $"Burst frame {frame} created a false aiming dot.");
            }
            await target.SaveAsync(Path.Combine(directory, $"frame-{frame:D4}.png"), CanvasBitmapFileFormat.Png);
        }
        Check(scene.GalleryState is { HitSkulls: 2, HitTargets: 0, UpperPhase: DuckHuntGalleryUpperPhase.WaitingForHits },
            "The effect interrupted the independent paper row or remaining skulls.");
        return new { Ok = true, Assertions = assertions, Directory = directory, Width = width, Height = height,
            Fps = fps, Frames = frames, Measurements = measurements, Resting = resting,
            DrawSubmissionMedianMs = Percentile(renderMilliseconds, .5), DrawSubmissionP95Ms = Percentile(renderMilliseconds, .95),
            TimingScope = "Synthetic 60 Hz frame clock; draw submission excludes readback/PNG encoding, not live projector pacing." };

        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
        }
        byte[] DrawIsolated(double seconds)
        {
            using (var drawing = isolated.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = Matrix3x2.CreateScale(isolatedWidth / 1000f, isolatedHeight / 1000f);
                drawShards.Invoke(scene, [drawing, 2, bounds, seconds, aspect]);
            }
            return isolated.GetPixelBytes();
        }
        CrystalBurstPixels Measure(byte[] pixels, double seconds)
        {
            var visible = new HashSet<int>();
            var radii = new List<double>();
            int top = isolatedHeight, bottom = -1, left = 0, right = 0, upper = 0;
            double cx = (bounds.X + bounds.Width * .5) * isolatedWidth;
            double cy = (bounds.Y + bounds.Height * .5) * isolatedHeight;
            for (int y = 0; y < isolatedHeight; y++)
            for (int x = 0; x < isolatedWidth; x++)
            {
                if (pixels[(y * isolatedWidth + x) * 4 + 3] < 40) continue;
                visible.Add(y * isolatedWidth + x);
                radii.Add(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                if (x < (bounds.X + bounds.Width * .12) * isolatedWidth) left++;
                if (x > (bounds.X + bounds.Width * .88) * isolatedWidth) right++;
                if (y < (bounds.Y + bounds.Height * .04) * isolatedHeight) upper++;
            }
            int count = visible.Count, components = 0;
            var pending = new Queue<int>();
            while (visible.Count > 0)
            {
                int seed = visible.First();
                visible.Remove(seed); pending.Enqueue(seed);
                int componentPixels = 0;
                while (pending.TryDequeue(out int pixel))
                {
                    componentPixels++;
                    int x = pixel % isolatedWidth, y = pixel / isolatedWidth;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (x + dx >= 0 && x + dx < isolatedWidth && y + dy >= 0 && y + dy < isolatedHeight &&
                            visible.Remove((y + dy) * isolatedWidth + x + dx))
                            pending.Enqueue((y + dy) * isolatedWidth + x + dx);
                }
                if (componentPixels >= 3) components++;
            }
            return new(seconds, count, components, Percentile(radii, .9), left, right, upper, top, bottom);
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private sealed record CrystalBurstPixels(double Seconds, int Pixels, int Components, double Radius90,
        int LeftPixels, int RightPixels, int UpperPixels, int Top, int Bottom);

    private static double Percentile(List<double> samples, double fraction)
    {
        if (samples.Count == 0) return 0;
        samples.Sort();
        return samples[Math.Min(samples.Count - 1, (int)(samples.Count * fraction))];
    }
}
#endif
