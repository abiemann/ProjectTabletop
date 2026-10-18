#if DEBUG
using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private GPU fields and synthetic observations only. This check never
    // constructs a window, opens a camera or changes the running board.
    private async Task<object> VerifyWaterGardenRockObstaclesAsync()
    {
        var device = CanvasDevice.GetSharedDevice();
        const int size = 256;
        var mask = new byte[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            if ((x + .5f) / size is >= .49f and <= .51f &&
                (y + .5f) / size is >= .30f and <= .70f) mask[y * size + x] = 1;
        var slab = new WaterGardenObstacles(size, size, 1, mask);
        using var blocked = new WaterGardenSimulation(device, size, size, 1, null, null, slab);
        using var open = new WaterGardenSimulation(device, size, size, 1);
        Quiet(blocked);
        Quiet(open);
        CheckField(blocked, slab, "initialization");
        Require(!slab.IsPathClear(new(.4f, .5f), new(.6f, .5f)) &&
                slab.IsPathClear(new(.4f, .25f), new(.6f, .25f)),
            "The synthetic obstacle did not block the direct route while leaving its ends open.");
        Require(slab.IsPathClear(new(0, .25f), new(1, .25f)) &&
                slab.IsPathClear(new(1, .25f), new(0, .25f)) &&
                slab.IsPathClear(new(0, 0), new(0, 1)) &&
                slab.IsPathClear(new(1, 1), new(1, 0)) &&
                !slab.IsPathClear(new(0, 0), new(1, 1)) &&
                !slab.IsPathClear(new(1, 0), new(0, 1)) &&
                !slab.IsPathClear(new(125f / size, 77f / size), new(.6f, .5f)) &&
                slab.IsPathClear(new(131f / size, .25f), new(131f / size, .75f)),
            "Grid-line or corner endpoints broke obstacle traversal or its cell ownership.");

        // A wide contact beside a thin rock must not seed a ripple directly on
        // its far side before the wave solver has advanced at all.
        blocked.AddDisturbance(new(.45f, .5f), .046f, .012f);
        var besideRock = ReadField(blocked);
        Require(blocked.GetDiagnostics().DisturbanceCount == 1 &&
                MaximumHeight(besideRock, size, size, 0, .48f, .3f, .7f) > .001 &&
                MaximumHeight(besideRock, size, size, .515f, 1, 0, 1) == 0,
            "A wide marker impulse tunneled through a thin obstacle.");
        CheckField(blocked, slab, "near-edge impulse");
        var beforeSolidImpulse = blocked.GetDiagnostics().DisturbanceCount;
        blocked.AddDisturbance(new(.5f, .5f), .046f, .012f);
        blocked.AddDisturbance(new(.48f, .5f), .046f, .012f);
        Require(blocked.GetDiagnostics().DisturbanceCount == beforeSolidImpulse,
            "A solid-centered impulse was applied to the water.");
        blocked.Reset();
        CheckField(blocked, slab, "reset before propagation");
        Require(blocked.GetFieldStatistics().TotalEnergy == 0,
            "Reset left an active wave beside the restored rock mask.");

        blocked.AddDisturbance(new(.4f, .5f), .01f, .012f);
        open.AddDisturbance(new(.4f, .5f), .01f, .012f);
        double openEarly = 0, blockedEarly = 0, blockedLate = 0;
        double openReflected = 0, blockedReflected = 0;
        var arrivals = new List<object>();
        for (int frame = 1; frame <= 150; frame++)
        {
            open.Advance(1.0 / 30);
            blocked.Advance(1.0 / 30);
            double seconds = frame / 30.0;
            double openHeight = Math.Abs(open.GetFieldProbe(new(.6f, .5f)).Height);
            double blockedHeight = Math.Abs(blocked.GetFieldProbe(new(.6f, .5f)).Height);
            if (seconds <= 2.3)
            {
                openEarly = Math.Max(openEarly, openHeight);
                blockedEarly = Math.Max(blockedEarly, blockedHeight);
            }
            if (seconds >= 3.3) blockedLate = Math.Max(blockedLate, blockedHeight);
            if (seconds is >= 1.1 and <= 1.9)
            {
                openReflected = Math.Max(openReflected, Math.Abs(open.GetFieldProbe(new(.4f, .5f)).Height));
                blockedReflected = Math.Max(blockedReflected, Math.Abs(blocked.GetFieldProbe(new(.4f, .5f)).Height));
            }
            if (frame % 15 == 0) arrivals.Add(new { seconds, openHeight, blockedHeight });
        }
        Require(openEarly > 1e-6 && blockedEarly < openEarly * .005,
            $"The rock transmitted the direct wave: open={openEarly:G9}, blocked={blockedEarly:G9}.");
        Require(blockedLate > 1e-8 && blockedLate > blockedEarly * 50,
            $"Water did not carry a later wave around the rock's ends: early={blockedEarly:G9}, late={blockedLate:G9}.");
        Require(blockedReflected > openReflected * 1.3 && blockedReflected > 1e-6,
            $"The rock did not reflect the incoming wave: open={openReflected:G9}, reflected={blockedReflected:G9}.");
        CheckField(blocked, slab, "propagation");

        for (int frame = 0; frame < 180; frame++)
        {
            blocked.AddDisturbance(new(.45f, .32f + .36f * (frame % 60) / 59), .046f, .004f);
            blocked.Advance(1.0 / 30);
        }
        CheckField(blocked, slab, "repeated near-edge stirring");
        var stirred = blocked.GetFieldStatistics();
        Require(Math.Abs(stirred.HeightMass) < 2e-5,
            $"Repeated rock-edge stirring changed the water volume: mass={stirred.HeightMass:G9}.");
        // Strong sustained forcing intentionally reaches the solver's safety
        // caps; check stability separately from linear-regime volume balance.
        for (int frame = 0; frame < 180; frame++)
        {
            blocked.AddDisturbance(new(.45f, .32f + .36f * (frame % 60) / 59), .046f, .016f);
            blocked.Advance(1.0 / 30);
        }
        CheckField(blocked, slab, "saturated near-edge stirring");
        var saturated = blocked.GetFieldStatistics();
        blocked.Reset();
        CheckField(blocked, slab, "final reset");
        Require(blocked.GetFieldStatistics().TotalEnergy == 0,
            "Reset retained wave energy after repeated rock-edge stirring.");

        string directory = Path.Combine(_appDataDirectory, "WaterGardenRockVerification");
        Directory.CreateDirectory(directory);
        var geometry = new List<object>();
        var now = DateTimeOffset.UtcNow;
        foreach (double aspect in new[] { 1.0, 16.0 / 9, 9.0 / 16 })
        {
            using var scene = new SceneCompositor(waterClock: () => now, boardRevealClock: () => now);
            scene.SetDisplayAspect(aspect);
            scene.SetBoardFacingDegrees(0);
            scene.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            double inset = scene.SetDetectedBoardGrid(corners, Homography.FromFourPoints(unit, unit));
            scene.SetBoardSetup(false);
            scene.ShowWaterGarden();
            await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
            int width = aspect >= 1 ? 1000 : (int)Math.Round(1000 * aspect);
            int height = aspect <= 1 ? 1000 : (int)Math.Round(1000 / aspect);
            using var target = new CanvasRenderTarget(device, width, height, 96);
            Draw();
            var simulation = Private<WaterGardenSimulation>(scene, "_waterSimulation");
            var rocks = Private<WaterGardenObstacles>(scene, "_waterObstacles");
            Require(ReferenceEquals(rocks, Private<WaterGardenObstacles>(simulation, "_obstacles")),
                "The production input and wave solver did not share the same rock geometry.");
            Quiet(simulation);
            CheckField(simulation, rocks, "production entrance");
            now += TimeSpan.FromSeconds(WaterGardenView.EntranceDurationSeconds + .1);
            Draw();
            CheckField(simulation, rocks, "production settled view");
            var isRock = typeof(SceneCompositor).GetMethod("IsWaterGardenRock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<double, double, bool>>(scene);
            int left = 0, right = 0, outside = 0, artworkOverlap = 0;
            for (int y = 0; y < rocks.Height; y++)
            for (int x = 0; x < rocks.Width; x++)
            {
                if (!rocks.IsSolid(x, y)) continue;
                var surface = new Vector2((x + .5f) / rocks.Width, (y + .5f) / rocks.Height);
                var screen = WaterGardenView.SurfaceToScreen(surface);
                if (surface.X < .5) left++; else right++;
                if (!WaterGardenView.ContainsWater(screen, (float)aspect)) outside++;
                if (isRock(screen.X, screen.Y)) artworkOverlap++;
            }
            int solid = left + right;
            Require(left > 20 && right > left && solid < rocks.Width * rocks.Height * .15 && outside == 0,
                $"The {aspect:G4} production rock footprint is missing, oversized or outside the pond: left={left}, right={right}, outside={outside}.");
            Require(artworkOverlap > solid * .9,
                $"The {aspect:G4} water obstacles did not sit beneath their visible rocks: {artworkOverlap}/{solid}.");
            string name = aspect == 1 ? "square" : aspect > 1 ? "wide" : "portrait";
            string scenePath = Path.Combine(directory, name + ".png");
            await target.SaveAsync(scenePath, CanvasBitmapFileFormat.Png);
            using (var drawing = target.CreateDrawingSession())
            {
                var color = Color.FromArgb(105, 255, 70, 190);
                for (int y = 0; y < rocks.Height; y++)
                for (int x = 0; x < rocks.Width; x++)
                {
                    if (!rocks.IsSolid(x, y)) continue;
                    var screen = WaterGardenView.SurfaceToScreen(new((x + .5f) / rocks.Width, (y + .5f) / rocks.Height));
                    drawing.FillRectangle(new Rect(width * (inset / 2 + screen.X * (1 - inset)) - 1,
                        height * (inset / 2 + screen.Y * (1 - inset)) - 1, 2, 2), color);
                }
            }
            string overlayPath = Path.Combine(directory, name + "-obstacles.png");
            await target.SaveAsync(overlayPath, CanvasBitmapFileFormat.Png);
            geometry.Add(new { aspect, solid, left, right, outside, artworkOverlap, scenePath, overlayPath });

            if (aspect == 1)
            {
                // Put a thin fixture across otherwise open water so the real
                // input path is tested independently of billboard occlusion.
                Field(scene, "_waterObstacles").SetValue(scene, slab);
                var pulses = Private<Queue<(Vector2 Position, float Strength)>>(scene, "_waterDisturbances");
                var strokes = Private<Queue<(Vector2 Previous, Vector2 Current, float Strength)>>(scene, "_waterStickStrokes");
                pulses.Clear();
                strokes.Clear();
                Require(Touch(new(.46f, .5f)), "The input fixture rejected open water before its rock.");
                pulses.Clear();
                strokes.Clear();
                Require(Touch(new(.54f, .5f)) && pulses.Count == 1 && strokes.Count == 0 &&
                        Vector2.Distance(pulses.Peek().Position, new(.54f, .5f)) < .001,
                    "A marker jump across a rock interpolated ripples or a wake through the obstruction.");
                pulses.Clear();
                Require(!Touch(new(.5f, .5f)) && pulses.Count == 0,
                    "The production marker input accepted a submerged rock cell.");
                Field(scene, "_waterObstacles").SetValue(scene, rocks);

                bool Touch(Vector2 surface)
                {
                    now += TimeSpan.FromMilliseconds(20);
                    var screen = WaterGardenView.SurfaceToScreen(surface);
                    return scene.SetWaterStickTip(new(inset / 2 + screen.X * (1 - inset),
                        inset / 2 + screen.Y * (1 - inset)), now);
                }
            }

            void Draw()
            {
                using var drawing = target.CreateDrawingSession();
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            }
        }
        return new { passed = true, openEarly, blockedEarly, blockedLate, openReflected, blockedReflected,
            arrivals, stirred, saturated, geometry, solidCellsRemainStationary = true, resetPreservesObstacles = true,
            disturbanceCannotTunnel = true, crossingStrokeDoesNotInterpolate = true, hardwareUnchanged = true };

        static void Quiet(WaterGardenSimulation simulation)
        {
            simulation.SetFountainEnabledForVerification(false);
            simulation.SetAmbientEnabledForVerification(false);
        }
        static FieldInfo Field(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing verification field: " + name);
        static T Private<T>(object owner, string name) where T : class =>
            Field(owner, name).GetValue(owner) as T ?? throw new InvalidOperationException("Missing verification object: " + name);
        static byte[] ReadField(WaterGardenSimulation simulation) =>
            Private<CanvasRenderTarget>(simulation, "_field").GetPixelBytes();
        static float Read(byte[] pixels, int offset) => BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(offset, 4));
        static void CheckField(WaterGardenSimulation simulation, WaterGardenObstacles obstacles, string stage)
        {
            byte[] pixels = ReadField(simulation);
            for (int y = 0; y < simulation.FieldHeight; y++)
            for (int x = 0; x < simulation.FieldWidth; x++)
            {
                int offset = (y * simulation.FieldWidth + x) * 16;
                float h = Read(pixels, offset), v = Read(pixels, offset + 4), solid = Read(pixels, offset + 8);
                bool expectedSolid = obstacles.IsSolid(x, y);
                Require(float.IsFinite(h) && float.IsFinite(v) && Math.Abs(h) <= .040001f && Math.Abs(v) <= .300001f,
                    $"The water field became invalid at {stage}: ({x},{y}) h={h}, v={v}.");
                Require(solid == (expectedSolid ? 1 : 0) && (!expectedSolid || h == 0 && v == 0),
                    $"The obstacle changed or contained moving water at {stage}: ({x},{y}) mask={solid}, h={h}, v={v}.");
            }
        }
        static double MaximumHeight(byte[] pixels, int width, int height, float x0, float x1, float y0, float y1)
        {
            double maximum = 0;
            for (int y = (int)Math.Ceiling(y0 * height); y < y1 * height; y++)
            for (int x = (int)Math.Ceiling(x0 * width); x < x1 * width; x++)
                maximum = Math.Max(maximum, Math.Abs(Read(pixels, (y * width + x) * 16)));
            return maximum;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
