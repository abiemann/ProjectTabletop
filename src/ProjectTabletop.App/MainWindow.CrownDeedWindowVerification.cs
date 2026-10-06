#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // The original city supplies the windows and still water. Verify that
    // advancing time preserves the complete stationary native composition.
    private async Task<object> VerifyCrownDeedWindowsAsync()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(SceneCompositor);
        var panes = (Vector2[][][])type.GetField("CrownDeedWindowPanes", statics)!.GetValue(null)!;
        Require(panes.Length > 0 && panes.All(group => group.Length > 0 && group.All(pane => pane.Length >= 3)),
            "The former window apertures are unavailable for rollback probes.");
        Require(new[] { "ClearCrownDeedWindows", "DrawCrownDeedWindows", "DrawCrownDeedWindowsLayer", "DisposeCrownDeedWindows" }
                .All(name => type.GetMethod(name, instance) is null) &&
            !type.GetFields(instance).Any(field => field.Name.StartsWith("_crownDeedWindows", StringComparison.Ordinal)),
            "A random window-darkening render pass or resource remains after rollback.");
        Require(!type.Assembly.GetTypes().Any(candidate => candidate.Name is "CrownDeedWindowsShader" or "CrownDeedWindowTimeline"),
            "The random window shader/timeline remains after rollback.");
        string directory = Path.Combine(_appDataDirectory, "CrownDeedWindowVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var samples = new List<object>();
        int sameClockChecks = 0, staticWindowChecks = 0, sourceArtworkChecks = 0, cacheChecks = 0, stateChecks = 0;
        int staticArtworkChecks = 0, phaseChecks = 0, originalPanePixelChecks = 0;
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var view in new[] { (Name: "measured", Width: 2777, Height: 2160),
            (Name: "wide", Width: 1920, Height: 1080), (Name: "square", Width: 1280, Height: 1280),
            (Name: "portrait", Width: 1080, Height: 1920) })
        {
            using var scene = Fixture(view.Width, view.Height, out double inset);
            await scene.EnsureCrownDeedResourcesAsync(CanvasDevice.GetSharedDevice());
            Require(scene.GetCrownDeedEntranceFrame(now) is { Active: true, ElapsedMilliseconds: 0 },
                "The static-window fixture did not begin its entrance after artwork loading.");
            using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), view.Width, view.Height, 96);
            using var projector = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), view.Width, view.Height, 96);
            now += TimeSpan.FromMilliseconds(4975);
            scene.TickCrownDeed(now);
            Require(!scene.CrownDeedEntranceActive, "The static-window fixture sampled its stationary board before the entrance ended.");
            Draw(scene, preview, false); Draw(scene, projector, true); Draw(scene, preview, false);
            Require(scene.CrownDeedState.Phase == CrownDeedPhase.Landing, "The rollback fixture did not retain the landing screen.");
            var city = Field<CanvasBitmap>(scene, "_crownDeedCityBitmap");
            using var original = await CanvasBitmap.LoadAsync(city.Device,
                Path.Combine(AppContext.BaseDirectory, "Assets", "CrownDeed", "crown-deed-city.png"), 96);
            Require(city.SizeInPixels == original.SizeInPixels && city.GetPixelBytes().SequenceEqual(original.GetPixelBytes()),
                "The city bitmap differs from the original artwork asset.");
            sourceArtworkChecks++;
            var previewProbes = Probes(view.Width, view.Height, Matrix3x2.CreateScale(view.Width / 1000f, view.Height / 1000f));
            var projectedMap = Matrix3x2.CreateScale((float)(view.Width * .93 * (1 - inset) / 1000),
                (float)(view.Height * .93 * (1 - inset) / 1000)) *
                Matrix3x2.CreateTranslation((float)(view.Width * (.035 + .93 * inset / 2)),
                    (float)(view.Height * (.035 + .93 * inset / 2)));
            var projectorProbes = Probes(view.Width, view.Height, projectedMap);
            byte[] firstPreview = Draw(scene, preview, false), firstProjector = Draw(scene, projector, true);
            var foreground = Field<CanvasRenderTarget>(scene, "_crownDeedPreviewTarget");
            CheckOriginalPaneSource(city, foreground);
            byte[] staticForeground = foreground.GetPixelBytes();
            var foregroundKey = Raw(scene, "_crownDeedPreviewState");
            var snapshot = scene.CrownDeedState;
            long gameRevision = snapshot.Revision;
            var buttons = scene.CurrentBoardButtons.ToArray();
            var started = now;
            await Capture(preview, view.Name + "-landing-original-windows");
            foreach (double seconds in new[] { .7, 18, 60 })
            {
                now = started.AddSeconds(seconds);
                byte[] current = Draw(scene, preview, false), projected = Draw(scene, projector, true);
                Require(current.SequenceEqual(Draw(scene, preview, false)) && projected.SequenceEqual(Draw(scene, projector, true)),
                    "Repeated same-clock rollback pixels changed.");
                sameClockChecks += 2;
                CheckWindows(firstPreview, current, previewProbes);
                CheckWindows(firstProjector, projected, projectorProbes);
                Require(ReferenceEquals(foreground, Field<CanvasRenderTarget>(scene, "_crownDeedPreviewTarget")) &&
                    Equals(foregroundKey, Raw(scene, "_crownDeedPreviewState")) && staticForeground.SequenceEqual(foreground.GetPixelBytes()),
                    "Clock advancement repainted the static city artwork.");
                cacheChecks++;
                Require(ReferenceEquals(snapshot, scene.CrownDeedState) && scene.CrownDeedState.Revision == gameRevision &&
                    scene.CurrentBoardButtons.SequenceEqual(buttons),
                    "Static artwork drawing changed the landing game state.");
                stateChecks++;
                Require(firstPreview.SequenceEqual(current) && firstProjector.SequenceEqual(projected),
                    "The original still-water city artwork changed on the stationary landing screen.");
                staticArtworkChecks += 2;
                if (seconds == 18)
                    await Capture(preview, view.Name + "-landing-static-artwork-18s");
            }
            Require(scene.ActivateCrownDeedButton("mp-start-game"), "The static-window fixture could not enter setup.");
            CheckWindows(firstPreview, Draw(scene, preview, false), previewProbes);
            phaseChecks++;
            Require(scene.ActivateCrownDeedButton("mp-ai-minus") && scene.ActivateCrownDeedButton("mp-human-plus") &&
                scene.ActivateCrownDeedButton("mp-start"), "The static-window fixture could not start two human players.");
            CheckWindows(firstPreview, Draw(scene, preview, false), previewProbes);
            phaseChecks++;
            await Capture(preview, view.Name + "-playing-original-windows");
            samples.Add(new { view = view.Name, previewWindowGroups = previewProbes.Count(probe => probe.Length > 0),
                projectedWindowGroups = projectorProbes.Count(probe => probe.Length > 0),
                previewInteriorPixels = previewProbes.Sum(probe => probe.Length),
                projectedInteriorPixels = projectorProbes.Sum(probe => probe.Length),
                previewUnprobedWindowIds = Enumerable.Range(0, panes.Length).Where(index => previewProbes[index].Length == 0).Select(index => index + 1).ToArray(),
                projectedUnprobedWindowIds = Enumerable.Range(0, panes.Length).Where(index => projectorProbes[index].Length == 0).Select(index => index + 1).ToArray() });
        }
        using (var scene = Fixture(1254, 1254, out _))
        using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1254, 1254, 96))
        {
            await scene.EnsureCrownDeedResourcesAsync(CanvasDevice.GetSharedDevice());
            var began = now;
            byte[] first = Draw(scene, target, false);
            var probes = Probes(1254, 1254, Matrix3x2.CreateScale(1.254f));
            now = began.AddMilliseconds(250);
            byte[] current = Draw(scene, target, false);
            CheckWindows(first, current, probes);
            Require(first.SequenceEqual(current), "The original city artwork moved before the first deed arrived.");
            staticArtworkChecks++;
            await Capture(target, "entrance-original-windows");
        }
        string reportPath = Path.Combine(directory, "static-window-samples.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, apertureCount = panes.Length, animatedWindowCount = 0, originalCityArtworkRestored = true,
            fourAspects = true, sameClockChecks, staticWindowChecks, sourceArtworkChecks, cacheChecks, stateChecks,
            staticArtworkChecks, phaseChecks, originalPanePixelChecks, originalWaterArtworkRestored = true,
            liveServicesConstructed = false, directory, reportPath, images };

        SceneCompositor Fixture(int width, int height, out double inset)
        {
            var scene = new SceneCompositor(crownDeed: new CrownDeedGame(157), blackjackClock: () => now,
                crownDeedClock: () => now, boardRevealClock: () => now);
            scene.SetDisplayAspect(width / (double)height);
            scene.SetBoardSetup(true);
            inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false); scene.ShowCrownDeed();
            return scene;
        }
        void CheckWindows(byte[] first, byte[] current, int[][] probes)
        {
            Require(probes.Any(probe => probe.Length > 0), "No original architectural glass interiors were sampled.");
            foreach (var probe in probes)
            {
                foreach (int pixel in probe)
                    Require(first.AsSpan(pixel * 4, 4).SequenceEqual(current.AsSpan(pixel * 4, 4)),
                        "A former random-switching window changed its actual native pixels.");
                if (probe.Length > 0) staticWindowChecks++;
            }
        }
        void CheckOriginalPaneSource(CanvasBitmap city, CanvasRenderTarget foreground)
        {
            int width = (int)foreground.SizeInPixels.Width, height = (int)foreground.SizeInPixels.Height;
            using var expected = new CanvasRenderTarget(foreground.Device, width, height, 96);
            using (var drawing = expected.CreateDrawingSession())
            {
                drawing.Transform = Matrix3x2.CreateScale(width / 1000f, height / 1000f);
                drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                drawing.DrawImage(city, new Windows.Foundation.Rect(0, 0, 1000, 1000),
                    new Windows.Foundation.Rect(0, 0, city.SizeInPixels.Width, city.SizeInPixels.Height));
            }
            byte[] painted = expected.GetPixelBytes(), actual = foreground.GetPixelBytes();
            int checks = 0;
            foreach (int pixel in Probes(width, height, Matrix3x2.CreateScale(width / 1000f, height / 1000f)).SelectMany(probe => probe))
            {
                double x = (pixel % width + .5) * 1000 / width, y = (pixel / width + .5) * 1000 / height;
                // Proof points are original glass outside the boulevard and
                // title/footer overlays. Other panes are still checked exactly
                // over time; deliberate board furniture is not source artwork.
                if ((x - 500) * (x - 500) / (490 * 490) + (y - 500) * (y - 500) / (465 * 465) <= 1 ||
                    y < 65 && x > 100 && x < 900 || y > 935 && x > 150 && x < 850) continue;
                Require(actual[pixel * 4 + 3] == 255 && actual.AsSpan(pixel * 4, 4).SequenceEqual(painted.AsSpan(pixel * 4, 4)),
                    "A visible original window pane is darkened, transparent or differs from the painted city source.");
                checks++;
            }
            Require(checks > 0, "No exposed original glass pixels were verified independently against the painting.");
            originalPanePixelChecks += checks;
        }
        int[][] Probes(int width, int height, Matrix3x2 transform) => panes.Select(group =>
        {
            var indices = new HashSet<int>();
            foreach (var pane in group)
            {
                var polygon = pane.Select(point => Vector2.Transform(point, transform)).ToArray();
                int left = Math.Max(0, (int)Math.Floor(polygon.Min(point => point.X)));
                int right = Math.Min(width, (int)Math.Ceiling(polygon.Max(point => point.X)));
                int top = Math.Max(0, (int)Math.Floor(polygon.Min(point => point.Y)));
                int bottom = Math.Min(height, (int)Math.Ceiling(polygon.Max(point => point.Y)));
                for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    if (InteriorClearance(polygon, new(x + .5f, y + .5f)) >= .5f) indices.Add(y * width + x);
            }
            return indices.ToArray();
        }).ToArray();
        static float InteriorClearance(Vector2[] polygon, Vector2 point)
        {
            bool inside = false;
            float distance = float.MaxValue;
            for (int a = 0, b = polygon.Length - 1; a < polygon.Length; b = a++)
            {
                var first = polygon[a]; var second = polygon[b];
                if ((first.Y > point.Y) != (second.Y > point.Y) &&
                    point.X < (second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y) + first.X) inside = !inside;
                var edge = second - first;
                float t = edge.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(point - first, edge) / edge.LengthSquared(), 0, 1) : 0;
                distance = Math.Min(distance, Vector2.Distance(point, first + edge * t));
            }
            return inside ? distance : -1;
        }
        static byte[] Draw(SceneCompositor scene, CanvasRenderTarget target, bool projector)
        {
            using (var drawing = target.CreateDrawingSession())
                if (projector) scene.Draw(drawing, (float)target.Size.Width, (float)target.Size.Height, false, false);
                else scene.DrawCrownDeedPreview(drawing, (float)target.Size.Width, (float)target.Size.Height);
            return target.GetPixelBytes();
        }
        async Task Capture(CanvasRenderTarget target, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        object? Raw(SceneCompositor scene, string name) => type.GetField(name, instance)!.GetValue(scene);
        T Field<T>(SceneCompositor scene, string name) => (T)(Raw(scene, name) ??
            throw new InvalidOperationException("Missing native static-window resource: " + name));
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
