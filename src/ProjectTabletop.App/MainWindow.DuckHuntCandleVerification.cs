#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Native compositor frames with an injected clock, never a live app or capture device.
    private async Task<object> VerifyDuckHuntCandlesAsync()
    {
        const int width = 1920, height = 1080, fps = 60, frames = 180;
        const double aspect = width / (double)height;
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        int assertions = 0, opticalChecks = 0;
        var now = MonotonicClock.UtcNow;
        string directory = Path.Combine(_appDataDirectory, "GalleryCandles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        CheckAdditiveTint();
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", privateInstance)!.GetValue(scene)!;
        scene.SetDisplayAspect(aspect);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowDuckHuntTargetPractice();
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        now = MonotonicClock.UtcNow.AddSeconds(1);
        scene.SetDuckHuntAudioStatus(true, "Isolated candle fixture");
        byte[] ready = Capture();
        Check(scene.DuckHuntArtworkReady && scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, Paused: false },
            "The candle fixture is not ready with PLAY visible.");
        await target.SaveAsync(Path.Combine(directory, "ready.png"), CanvasBitmapFileFormat.Png);

        // Regions include each glass chimney but no moving curtain or skulls.
        BoardRect[] lamps = [SceneRect(.025, .18, .045, .12), SceneRect(.93, .18, .045, .12)];
        BoardRect[] wood = [SceneRect(.082, .18, .012, .15), SceneRect(.906, .18, .012, .15),
            SceneRect(.25, .13, .50, .025)];
        int[] readyChanges = CheckAnimation(ready, [DuckHuntGalleryLayout.PlayBounds, .. wood], "Ready");
        CheckPause("Ready");

        now += TimeSpan.FromMilliseconds(1);
        Check(scene.ActivateDuckHuntAt(.1, .93), "The candle fixture could not open EXIT/BACK.");
        now += TimeSpan.FromMilliseconds(350);
        byte[] controls = Capture();
        var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No stationary caption reference.");
        var captions = new List<BoardRect> { DuckHuntGalleryLayout.PlayBounds };
        foreach (string id in new[] { "duck-hunt-exit", "duck-hunt-back" })
        {
            int index = context.ButtonIds.ToList().IndexOf(id);
            Check(index >= 0 && context.EnabledHoldIds.Contains(id), $"{id} is missing from the active caption controls.");
            var caption = context.ExpectedScene.BoardTriggerRegions![index];
            captions.Add(new(caption.X, caption.Y, caption.Width, caption.Height));
            Check(board.Buttons.Any(button => button.Id == id && button.Enabled), $"{id} was disabled by lamp animation.");
        }
        CheckAnimation(controls, [.. captions, .. wood], "Ready with controls");
        await target.SaveAsync(Path.Combine(directory, "ready-controls.png"), CanvasBitmapFileFormat.Png);

        now += TimeSpan.FromMilliseconds(1);
        Check(scene.SubmitDuckHuntShot(new(.5, .46), now), "The candle fixture could not start the gallery.");
        now += TimeSpan.FromSeconds(4);
        byte[] active = Capture();
        Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.WaitingForHits, UpperPhase: DuckHuntGalleryUpperPhase.WaitingForHits },
            "Both target rows did not reach their waiting positions.");
        var paperField = typeof(SceneCompositor).GetField("_galleryTargets", privateInstance)!;
        var papers = ((CanvasRenderTarget[])paperField.GetValue(scene)!).ToArray();
        var paperPixels = papers.Select(paper => paper.GetPixelBytes()).ToArray();
        string[] cacheNames = ["_galleryCachedCycle", "_galleryCachedPunctures", "_galleryCachedImpactFrame",
            "_galleryCachedAspect", "_galleryCachedRasterWidth"];
        var cacheFields = cacheNames.Select(name => typeof(SceneCompositor).GetField(name, privateInstance)!).ToArray();
        var cacheBefore = cacheFields.Select(field => field.GetValue(scene)).ToArray();
        int[] activeChanges = CheckAnimation(active, wood, "Waiting for hits");
        CheckPause("Waiting for hits");
        var currentPapers = (CanvasRenderTarget[])paperField.GetValue(scene)!;
        Check(cacheBefore.SequenceEqual(cacheFields.Select(field => field.GetValue(scene))) &&
            scene.GalleryVisualFrame(now) == 0, "Lamp frames invalidated the unmarked paper texture cache.");
        for (int index = 0; index < papers.Length; index++)
            Check(ReferenceEquals(papers[index], currentPapers[index]) &&
                paperPixels[index].AsSpan().SequenceEqual(currentPapers[index].GetPixelBytes()),
                "Lamp animation replaced or changed a stationary paper texture.");

        // Save every actual compositor frame. Encoding must preserve this frame rate;
        // this is a synthetic-clock preview, not a projector performance measurement.
        var origin = now + TimeSpan.FromSeconds(1);
        for (int frame = 0; frame < frames; frame++)
        {
            now = origin + TimeSpan.FromSeconds(frame / (double)fps);
            Draw();
            if (frame % 15 == 0) CheckOptics(target.GetPixelBytes(), $"video frame {frame}");
            await target.SaveAsync(Path.Combine(directory, $"frame-{frame:D4}.png"), CanvasBitmapFileFormat.Png);
        }
        using var large = new CanvasRenderTarget(target.Device, 3840, 2160, 96);
        using (var drawing = large.CreateDrawingSession())
            scene.Draw(drawing, 3840, 2160, preview: false, runningSlowly: false);
        string still = Path.Combine(directory, "candles-4k.png");
        await large.SaveAsync(still, CanvasBitmapFileFormat.Png);
        return new { Ok = true, Assertions = assertions, OpticalChecks = opticalChecks, Directory = directory,
            Width = width, Height = height, Fps = fps, Frames = frames, Still = still,
            ReadyChangedPixels = readyChanges, ActiveChangedPixels = activeChanges,
            TimingScope = "180 real compositor frames at a synthetic 60 Hz clock; not live projector pacing." };

        int[] CheckAnimation(byte[] baseline, IReadOnlyList<BoardRect> stationary, string stage)
        {
            var changed = new int[lamps.Length];
            foreach (double advance in new[] { .067, .113, .173, .251, .367, .487 })
            {
                now += TimeSpan.FromSeconds(advance);
                byte[] current = Capture();
                for (int lamp = 0; lamp < lamps.Length; lamp++)
                    changed[lamp] = Math.Max(changed[lamp], ChangedPixels(baseline, current, lamps[lamp], minimumDelta: 3));
                foreach (var region in stationary)
                    Check(ChangedPixels(baseline, current, region) == 0, $"{stage}: candle animation changed neighboring wood or a stationary caption.");
                CheckOptics(current, stage);
            }
            Check(changed.All(count => count >= 12),
                $"{stage}: one or both lamp flames remained static (changed pixels: {string.Join(", ", changed)}).");
            return changed;
        }
        void CheckPause(string stage)
        {
            scene.SetDuckHuntAudioStatus(false, "Isolated candle pause");
            byte[] paused = Capture();
            int pausedFrame = scene.GalleryLampVisualFrame(now);
            Check(scene.GalleryState.Paused, $"{stage}: microphone unavailability did not pause the gallery.");
            now += TimeSpan.FromSeconds(2);
            Check(paused.AsSpan().SequenceEqual(Capture()) && scene.GalleryLampVisualFrame(now) == pausedFrame,
                $"{stage}: paused flames or reflections continued animating.");
            scene.SetDuckHuntAudioStatus(true, "Isolated candle resume");
            Capture();
            Check(scene.GalleryLampVisualFrame(now) == pausedFrame, $"{stage}: resuming skipped over paused flame time.");
            now += TimeSpan.FromMilliseconds(200);
            Capture();
            Check(scene.GalleryLampVisualFrame(now) > pausedFrame, $"{stage}: the lamp clock did not resume.");
        }
        BoardRect SceneRect(double x, double y, double w, double h)
        {
            var top = DuckHuntGalleryLayout.ScenePoint(x, y, aspect);
            var bottom = DuckHuntGalleryLayout.ScenePoint(x + w, y + h, aspect);
            return new(top.U, top.V, bottom.U - top.U, bottom.V - top.V);
        }
        int ChangedPixels(byte[] before, byte[] after, BoardRect region, int minimumDelta = 1)
        {
            var map = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap", privateInstance)!.GetValue(scene)!;
            var top = map.Transform(new(region.X, region.Y));
            var bottom = map.Transform(new(region.X + region.Width, region.Y + region.Height));
            int left = Math.Clamp((int)Math.Floor(top.X * width), 0, width - 1);
            int right = Math.Clamp((int)Math.Ceiling(bottom.X * width), left + 1, width);
            int first = Math.Clamp((int)Math.Floor(top.Y * height), 0, height - 1);
            int last = Math.Clamp((int)Math.Ceiling(bottom.Y * height), first + 1, height), changed = 0;
            for (int y = first; y < last; y++)
            for (int x = left; x < right; x++)
            {
                int pixel = (y * width + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                    if (Math.Abs(before[pixel + channel] - after[pixel + channel]) >= minimumDelta)
                    { changed++; break; }
            }
            return changed;
        }
        void CheckOptics(byte[] pixels, string stage)
        {
            var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No optical context.");
            var detection = new RedDotDetector().Detect(width, height, width * 4, pixels, input.CameraToBoard);
            opticalChecks++;
            Check(!detection.Detected && detection.CandidateCount == 0,
                $"{stage}: a flame created {detection.CandidateCount} false red aiming candidates.");
        }
        void CheckAdditiveTint()
        {
            using var source = new CanvasRenderTarget(target.Device, 2, 2, 96);
            using (var drawing = source.CreateDrawingSession()) drawing.Clear(Color.FromArgb(255, 64, 64, 64));
            using var result = new CanvasRenderTarget(target.Device, 2, 2, 96);
            byte[] full = Blend(1, 1), half = Blend(.5f, 1), combined = Blend(.5f, 2);
            Check(full[0] == 64 && Math.Abs(half[0] - 32) <= 1,
                "Sprite opacity is not applying one straight-alpha tint multiplier.");
            Check(Enumerable.Range(0, 3).All(channel => Math.Abs(full[channel] - combined[channel]) <= 1),
                "Two half-opacity additive sprites did not preserve the full sprite radiance.");

            byte[] Blend(float opacity, int copies)
            {
                using (var drawing = result.CreateDrawingSession())
                {
                    drawing.Clear(Color.FromArgb(255, 0, 0, 0));
                    drawing.Blend = CanvasBlend.Add;
                    using var sprites = drawing.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
                    for (int copy = 0; copy < copies; copy++)
                        sprites.DrawFromSpriteSheet(source, Matrix3x2.Identity, new Rect(0, 0, 2, 2), new Vector4(1, 1, 1, opacity));
                }
                return result.GetPixelBytes();
            }
        }
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
        }
        byte[] Capture() { Draw(); return target.GetPixelBytes(); }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
