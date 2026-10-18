#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.GlobeRendering;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private static async Task<object> VerifyGlobeShootingStarsAsync(string directory, List<string> images)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var renderer = new GlobeSurfaceRenderer(device);
        await renderer.EnsureReadyAsync();
        double first = GlobeShootingStar.GetStartTime(0);
        double duration = GlobeShootingStar.GetDuration(0);
        double peak = first + duration * .5;
        Require(!GlobeShootingStar.Sample(2.9, 1).Active,
            "A shooting star appeared during the Globe entrance.");
        for (int i = 0; i < 64; i++)
        {
            double start = GlobeShootingStar.GetStartTime(i);
            double life = GlobeShootingStar.GetDuration(i);
            if (i > 0)
                Require(start - GlobeShootingStar.GetStartTime(i - 1) is >= 10 and <= 22,
                    "Shooting stars lost their occasional spacing.");
            Require(life is >= .8 and <= 1.2 &&
                !GlobeShootingStar.Sample(start + life + .01, 1).Active,
                "A shooting star remained active beyond its brief lifetime.");
        }

        var checks = new List<object>();
        foreach (var (width, height, name) in new[]
        {
            (3840, 2160, "landscape"), (1440, 1440, "square"),
            (1543, 1200, "measured-board"), (1080, 1920, "portrait")
        })
        {
            using var target = new CanvasRenderTarget(device, width, height, 96);
            double aspect = width / (double)height;
            byte[] quiet = Draw(target, 0);
            await Save(target, "shooting-star-" + name + "-quiet");
            int cacheCount = renderer.StarFieldRenderCount;
            byte[] active = Draw(target, peak);
            await Save(target, "shooting-star-" + name + "-active");
            int changed = 0, bottomChanges = 0, earthChanges = 0;
            int left = width, right = -1, top = height, bottom = -1;
            double radius = .335 * .6 * Math.Min(width, height);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int delta = Delta(quiet, active, (y * width + x) * 4);
                if (delta <= 1) continue;
                if (y >= height * .75) bottomChanges++;
                if (Math.Pow(x + .5 - width * .5, 2) + Math.Pow(y + .5 - height * .5, 2) <
                    radius * radius * .94) earthChanges++;
                if (delta <= 8) continue;
                changed++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            Require(changed > 20 && changed < width * height * .015 && bottom < height * .72,
                "The shooting star is missing, oversized or outside its upper-sky path: " + name);
            Require(bottomChanges == 0 && earthChanges == 0,
                "Shooting-star light changed the swipe band or Earth's opaque interior: " + name);
            Require(Changed(active, Draw(target, peak)) == 0,
                "Repeated shooting-star draws at the same clock differ: " + name);
            Require(Changed(active, Draw(target, peak + .12), 8) > 20 &&
                Changed(quiet, Draw(target, first + duration + .01)) == 0,
                "The shooting star did not move or disappear cleanly: " + name);
            byte[] zoomedQuiet = Draw(target, 0, zoom: 3);
            byte[] zoomedActive = Draw(target, peak, zoom: 3);
            double largeRadius = .335 * 3 * Math.Min(width, height);
            int occlusionChanges = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Math.Pow(x + .5 - width * .5, 2) + Math.Pow(y + .5 - height * .5, 2) <
                    largeRadius * largeRadius * .94 && Delta(zoomedQuiet, zoomedActive, (y * width + x) * 4) > 1)
                    occlusionChanges++;
            Require(occlusionChanges == 0 && renderer.StarFieldRenderCount == cacheCount,
                "Earth did not hide the shooting star, or its motion rebuilt the cached sky: " + name);
            checks.Add(new { name, width, height, changedPixels = changed,
                bounds = new { left, top, right, bottom }, bottomChanges, earthChanges, occlusionChanges,
                cachedSkyReused = true, deterministicClock = true });
        }

        // Inspect motion at a fixed Earth pose, including the quiet lead-in and
        // extinction. These are deterministic snapshots, not a frame-rate claim.
        using (var motion = new CanvasRenderTarget(device, 1280, 720, 96))
            for (int frame = 0; frame < 30; frame++)
            {
                Draw(motion, first - .1 + frame * (duration + .2) / 29);
                await Save(motion, "shooting-star-motion-" + frame.ToString("D2"));
            }
        return new { passed = true, firstAppearanceSeconds = first, durationSeconds = duration,
            checks, motionFrames = 30, motionFramesPerSecond = 29 / (duration + .2),
            bottomQuarterUnchanged = true, earthOcclusion = true, staticSkyCachePreserved = true };

        byte[] Draw(CanvasRenderTarget target, double elapsed, float zoom = .6f)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Transform = Matrix3x2.CreateScale((float)target.Size.Width / 1000,
                    (float)target.Size.Height / 1000);
                Require(renderer.Draw(drawing, zoom, 55.3f, target.Size.Width / target.Size.Height,
                    34.0522f, elapsed), "Shooting-star fixture could not draw Globe textures.");
            }
            return target.GetPixelBytes();
        }
        async Task Save(CanvasRenderTarget target, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }
        static int Delta(byte[] a, byte[] b, int i) => Math.Max(Math.Abs(a[i] - b[i]),
            Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
        static int Changed(byte[] a, byte[] b, int threshold = 1)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i += 4) if (Delta(a, b, i) > threshold) count++;
            return count;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
