#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.GlobeRendering;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private static async Task<object> VerifyGlobeShootingStarsAsync(string directory, List<string> images,
        GlobeSurfaceRenderer renderer)
    {
        var device = renderer.Device;
        await renderer.EnsureReadyAsync();
        Require(renderer.SkyTextureWidth == 16384 && renderer.SkyTextureHeight == 8192 &&
            renderer.SkyTextureLoadCount == 1, "The sphere fixture did not retain the complete 16K panorama.");
        Require(Math.Abs(GlobeSurfaceRenderer.GetSkyRotationRadians(20) - Math.PI / 300) < .000001 &&
            GlobeSurfaceRenderer.GetSkyRotationRadians(12000) == 0,
            "Celestial-sphere rotation lost its 0.03-degree-per-second clock.");
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
            Require(life is >= 1.36 and <= 2.041 &&
                !GlobeShootingStar.Sample(start + life + .01, 1).Active,
                "A shooting star remained active beyond its brief lifetime.");
            foreach (double aspect in new[] { .2, 1080 / 1920d, 1, 1543 / 1200d, 3840 / 2160d, 5 })
            {
                var arrival = GlobeShootingStar.Sample(start + life * .001, aspect);
                var departure = GlobeShootingStar.Sample(start + life * .999, aspect);
                double distance = ((departure.HeadUv - arrival.HeadUv) * arrival.PhysicalScale).Length() / .998;
                Require(distance is >= .407 and <= .715,
                    "The shooting-star path lost its extended physical travel distance.");
                var tail = arrival.HeadUv - arrival.Direction * arrival.TailLength / arrival.PhysicalScale;
                Require(Math.Min(tail.X, departure.HeadUv.X) >= .055 &&
                    Math.Max(tail.X, departure.HeadUv.X) <= .945 &&
                    tail.Y >= .095 && departure.HeadUv.Y <= .69,
                    "An extended shooting-star path clips an edge or reaches the control/swipe band.");
            }
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
            int textureLoads = renderer.SkyTextureLoadCount;
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
            Require(occlusionChanges == 0 && renderer.SkyTextureLoadCount == textureLoads,
                "Earth did not hide the shooting star, or its motion reloaded the sky: " + name);
            var sphere = await CheckSphere(target, quiet, name);
            checks.Add(new { name, width, height, changedPixels = changed,
                bounds = new { left, top, right, bottom }, bottomChanges, earthChanges, occlusionChanges,
                skyTextureReused = true, deterministicClock = true, sphere });
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
            cometLeavesBottomQuarterUnchangedAtFixedSky = true, earthOcclusion = true,
            celestialSphereTextureReused = true, sphereRotationPeriodSeconds = 12000 };

        byte[] Draw(CanvasRenderTarget target, double elapsed, float zoom = .6f, float skyRotation = 0)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Transform = Matrix3x2.CreateScale((float)target.Size.Width / 1000,
                    (float)target.Size.Height / 1000);
                // Hold the celestial sphere still while isolating comet motion;
                // its independent rotation is checked explicitly below.
                Require(renderer.DrawFrame(drawing, zoom, 55.3f, target.Size.Width / target.Size.Height,
                    34.0522f, elapsed, skyRotation), "Shooting-star fixture could not draw Globe textures.");
            }
            return target.GetPixelBytes();
        }
        async Task<object> CheckSphere(CanvasRenderTarget target, byte[] initial, string name)
        {
            int width = (int)target.Size.Width, height = (int)target.Size.Height;
            float angle = GlobeSurfaceRenderer.GetSkyRotationRadians(20);
            byte[] rotated = Draw(target, 0, skyRotation: angle);
            await Save(target, "celestial-sphere-" + name + "-after-20-seconds");
            int skyChanges = 0, bottomSkyChanges = 0, earthChanges = 0;
            double radius = .335 * .6 * Math.Min(width, height);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int delta = Delta(initial, rotated, (y * width + x) * 4);
                double distanceSquared = Math.Pow(x + .5 - width * .5, 2) + Math.Pow(y + .5 - height * .5, 2);
                if (distanceSquared < radius * radius * .94 && delta > 1) earthChanges++;
                if (distanceSquared <= radius * radius * 1.1 || delta <= 8) continue;
                skyChanges++;
                if (y >= height * .75) bottomSkyChanges++;
            }
            Require(skyChanges > 100 && bottomSkyChanges > 20 && earthChanges == 0,
                "The sphere did not rotate behind the fixed Earth across the whole sky: " + name);
            Require(Changed(rotated, Draw(target, 0, skyRotation: angle)) == 0,
                "A fixed sphere angle rendered differently on repeated draws: " + name);
            int wrapChanges = Changed(initial, Draw(target, 0,
                skyRotation: GlobeSurfaceRenderer.GetSkyRotationRadians(12000)));
            int phaseWrapChanges = Changed(rotated, Draw(target, 0,
                skyRotation: GlobeSurfaceRenderer.GetSkyRotationRadians(12020)));
            Require(wrapChanges == 0 && phaseWrapChanges == 0,
                "The sphere's 12000-second clock wrap changed its orientation: " + name);
            byte[] beforeWrap = Draw(target, 0, skyRotation: GlobeSurfaceRenderer.GetSkyRotationRadians(11999.9));
            byte[] afterWrap = Draw(target, 0, skyRotation: GlobeSurfaceRenderer.GetSkyRotationRadians(12000.1));
            int seamChanges = Changed(beforeWrap, afterWrap, 8);
            Require(seamChanges < skyChanges && renderer.SkyTextureLoadCount == 1,
                "The celestial sphere jumped at its longitude seam or reloaded its panorama: " + name);
            return new { skyChanges, bottomSkyChanges, earthChanges, wrapChanges, phaseWrapChanges,
                seamChanges, renderer.SkyTextureWidth, renderer.SkyTextureHeight, renderer.SkyTextureLoadCount };
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
