#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Exercise the production light mask over real offscreen board rendering.
    // No window, camera, output or live scene participates in these checks.
    private async Task<object> VerifyHoldLightContoursAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "HoldLightContourVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        var images = new List<string>();
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var maskMethod = typeof(SceneCompositor).GetMethod("HoldButtonLightClip", instance)!;
        var captionMethod = typeof(SceneCompositor).GetMethod("BoardButtonTextRegion", instance)!;
        var mapField = typeof(SceneCompositor).GetField("_boardSurfaceMap", instance)!;
        int protectedCaptionSamples = 0, protectedInteriorSamples = 0, illuminatedCornerSamples = 0;
        foreach (var test in new[]
        {
            (Name: "roulette-landscape", Screen: BoardScreen.Roulette, Width: 1600, Height: 1250, Skewed: false),
            (Name: "roulette-portrait", Screen: BoardScreen.Roulette, Width: 1250, Height: 1600, Skewed: false),
            (Name: "roulette-projective", Screen: BoardScreen.Roulette, Width: 1600, Height: 1250, Skewed: true),
            (Name: "menu-arrow", Screen: BoardScreen.Menu, Width: 1600, Height: 1250, Skewed: true),
            (Name: "globe-controls", Screen: BoardScreen.Globe, Width: 1600, Height: 1250, Skewed: true),
            (Name: "photo-controls", Screen: BoardScreen.PhotoCopy, Width: 1600, Height: 1250, Skewed: true)
        })
        {
            // Start after the session constructor's monotonic input barriers;
            // every subsequent action uses this same explicit fixture clock.
            var now = MonotonicClock.UtcNow.AddMinutes(1);
            using var scene = new SceneCompositor(blackjackClock: () => now, globeClock: () => now,
                boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), test.Width, test.Height, 96);
            var output = new Rect(0, 0, test.Width, test.Height);
            scene.SetDisplayAspect(test.Width / (double)test.Height);
            scene.SetBoardSetup(true);
            Vector2[] corners = test.Skewed
                ? [new(.08f, .06f), new(.94f, .11f), new(.87f, .94f), new(.12f, .88f)]
                : [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)];
            scene.SetDetectedBoardGrid(corners,
                Homography.FromFourPoints([new(0, 0), new(test.Width, 0), new(test.Width, test.Height), new(0, test.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            switch (test.Screen)
            {
                case BoardScreen.Roulette:
                    scene.ShowRoulette();
                    await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.Roulette);
                    break;
                case BoardScreen.Menu:
                    scene.ShowBoardMenu();
                    await scene.EnsureMenuPreviewResourcesAsync(target.Device);
                    break;
                case BoardScreen.Globe:
                    scene.ShowGlobe();
                    await scene.EnsureGlobeResourcesAsync(target.Device);
                    now += GlobeState.EntranceDuration + TimeSpan.FromMilliseconds(100);
                    scene.TickGlobe(now);
                    Require(scene.ActivateGlobeButton("globe-drawer-open"), "Globe's contour fixture could not open its controls.");
                    now = now.AddSeconds(1);
                    scene.TickGlobe(now);
                    break;
                case BoardScreen.PhotoCopy:
                    scene.ShowPhotoCopy();
                    now = now.AddSeconds(1);
                    scene.TickPhotoCopy(now);
                    Require(scene.ActivatePhotoCopyButton("photo-drawer-open"), "Photo Copy's contour fixture could not open its controls.");
                    now = now.AddSeconds(1);
                    scene.TickPhotoCopy(now);
                    break;
            }
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, test.Width, test.Height, preview: false, runningSlowly: false);
            byte[] before = target.GetPixelBytes();
            var map = (Homography)mapField.GetValue(scene)!;
            var buttons = scene.CurrentBoardButtons.Where(button => button.IsHold).ToArray();
            Require(buttons.Length > 0 && (test.Screen != BoardScreen.Roulette || buttons.Length == 5),
                test.Name + " did not expose the intended hold controls.");
            if (test.Screen == BoardScreen.Roulette) await Save("before");

            // The two production lights share this exact clip. A uniform white
            // source makes each exposed/protected point unambiguous regardless
            // of spotlight placement, fade timing or hand-model output.
            using (var drawing = target.CreateDrawingSession())
            using (var clip = (CanvasGeometry?)maskMethod.Invoke(scene, [drawing, output]))
            {
                Require(clip is not null, test.Name + " has no hold light protection.");
                using (drawing.CreateLayer(1, clip!)) drawing.FillRectangle(output, Colors.White);
            }
            byte[] after = target.GetPixelBytes();
            if (test.Screen == BoardScreen.Roulette) await Save("illuminated");
            foreach (var button in buttons)
            {
                var b = button.Bounds;
                var caption = (HandTrackingBounds)captionMethod.Invoke(scene, [target.Device, button])!;
                for (double y = caption.Y; y <= caption.Y + caption.Height; y += .002)
                for (double x = caption.X; x <= caption.X + caption.Width; x += .002)
                {
                    Require(Unchanged(x, y), test.Name + ": light altered the " + button.Id + " caption.");
                    protectedCaptionSamples++;
                }
                foreach (double u in new[] { .25, .5, .75 })
                foreach (double v in new[] { .25, .5, .75 })
                {
                    Require(Unchanged(b.X + b.Width * u, b.Y + b.Height * v),
                        test.Name + ": light altered the " + button.Id + " interior.");
                    protectedInteriorSamples++;
                }
                // These points were blocked by the old six-pixel-expanded
                // rectangular hole. They are safely outside a rounded plate.
                foreach (double x in new[] { b.X - .004, b.X + b.Width + .004 })
                foreach (double y in new[] { b.Y - .004, b.Y + b.Height + .004 })
                {
                    int pixel = Index(x, y);
                    Require(after[pixel] >= 250 && after[pixel + 1] >= 250 && after[pixel + 2] >= 250,
                        test.Name + ": the " + button.Id + " corner retained a rectangular dark mask.");
                    illuminatedCornerSamples++;
                }
            }
            results.Add(new { test.Name, test.Width, test.Height, test.Skewed, holds = buttons.Select(button => button.Id).ToArray() });

            int Index(double x, double y)
            {
                var point = map.Transform(new(x, y));
                int px = (int)Math.Round(point.X * test.Width), py = (int)Math.Round(point.Y * test.Height);
                Require(px >= 0 && px < test.Width && py >= 0 && py < test.Height, "Contour fixture sample left the render target.");
                return (py * test.Width + px) * 4;
            }
            bool Unchanged(double x, double y)
            {
                int pixel = Index(x, y);
                return before.AsSpan(pixel, 4).SequenceEqual(after.AsSpan(pixel, 4));
            }
            async Task Save(string state)
            {
                string path = Path.Combine(directory, test.Name + "-" + state + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
        }
        return new { passed = true, protectedCaptionSamples, protectedInteriorSamples, illuminatedCornerSamples,
            productionLightClip = true, liveHardwareTouched = false, results, images };

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
