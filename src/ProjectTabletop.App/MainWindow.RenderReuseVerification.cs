#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Standalone native scenes only: no camera, projector window or live scene
    // is opened, read or changed by this cache/lifecycle regression.
    private async Task<object> VerifyRenderReuseAsync()
    {
        var device = CanvasDevice.GetSharedDevice();
        long frameTicks = TimeSpan.TicksPerSecond / 60;
        var now = new DateTimeOffset(MonotonicClock.UtcNow.UtcTicks / frameTicks * frameTicks,
            TimeSpan.Zero).AddMinutes(1);
        using var projected = new CanvasRenderTarget(device, 3840, 2160, 96);
        using var preview = new CanvasRenderTarget(device, 480, 270, 96);

        using (var globe = NewScene(BoardScreen.Globe))
        {
            await globe.EnsureGlobeResourcesAsync(device);
            now += TimeSpan.FromSeconds(4);
            Draw(globe, projected);
            var first = globe.GetGlobeFrameCacheForVerification();
            var firstPixels = projected.GetPixelBytes();
            DrawGlobePreview(globe, preview);
            Draw(globe, projected);
            var reused = globe.GetGlobeFrameCacheForVerification();
            var nativeResolution = globe.GetBoardResolutionDiagnostics();
            Require(first.RenderCount == 1 && reused.RenderCount == 1 &&
                    ReferenceEquals(first.Target, reused.Target) &&
                    firstPixels.AsSpan().SequenceEqual(projected.GetPixelBytes()) &&
                    reused.Width == nativeResolution.BoardPixelWidth &&
                    reused.Height == nativeResolution.BoardPixelHeight && reused.Width > 3000 && reused.Height > 1700,
                "Globe's laptop preview reran the native shader or changed projector pixels in the same frame.");
            now += TimeSpan.FromTicks(1);
            DrawGlobePreview(globe, preview);
            Draw(globe, projected);
            Require(globe.GetGlobeFrameCacheForVerification().RenderCount == 1,
                "Globe rerendered continuous animation twice within one visual frame.");
            now += TimeSpan.FromMilliseconds(20);
            DrawGlobePreview(globe, preview);
            Draw(globe, projected);
            Require(globe.GetGlobeFrameCacheForVerification().RenderCount == 2 &&
                    !firstPixels.AsSpan().SequenceEqual(projected.GetPixelBytes()),
                "Globe's shared frame stopped rotating or rerendered twice on the next animation frame.");
            Require(globe.ActivateGlobeButton("globe-drawer-open"), "Globe's drawer did not open.");
            now += TimeSpan.FromMilliseconds(300);
            globe.TickGlobe(now);
            Draw(globe, projected);
            var drawer = globe.GetGlobeFrameCacheForVerification();
            Require(globe.ActivateGlobeButton("globe-zoom-in"), "Globe's zoom control did not accept a selection.");
            DrawGlobePreview(globe, preview);
            Draw(globe, projected);
            Require(globe.GetGlobeFrameCacheForVerification().RenderCount == drawer.RenderCount + 1,
                "A same-frame Globe control change was stale or rendered separately for each view.");
        }

        using (var globe = NewScene(BoardScreen.Globe))
        {
            await globe.EnsureGlobeResourcesAsync(device);
            DrawGlobePreview(globe, preview);
            var small = globe.GetGlobeFrameCacheForVerification();
            Draw(globe, projected);
            var grown = globe.GetGlobeFrameCacheForVerification();
            DrawGlobePreview(globe, preview);
            Require(small.RenderCount == 1 && grown.RenderCount == 2 &&
                    grown.Width > small.Width && grown.Height > small.Height &&
                    globe.GetGlobeFrameCacheForVerification().RenderCount == 2,
                "Preview-first Globe entry did not grow once to native quality and reuse that frame.");
            using var replacementDevice = new CanvasDevice();
            using var replacementPreview = new CanvasRenderTarget(replacementDevice, 480, 270, 96);
            await globe.EnsureGlobeResourcesAsync(replacementDevice);
            DrawGlobePreview(globe, replacementPreview);
            var changedDevice = globe.GetGlobeFrameCacheForVerification();
            Require(changedDevice.RenderCount == 3 && !ReferenceEquals(grown.Target, changedDevice.Target) &&
                    changedDevice.Target!.Device == replacementDevice,
                "A Globe graphics-device change reused a target owned by the old device.");
        }

        using (var paint = NewScene(BoardScreen.Paint))
        {
            paint.DisablePaintIdleStreaksForVerification();
            paint.ResetPaint();
            Draw(paint, projected);
            Require(paint.GetPaintReferenceCacheForVerification() is { Readbacks: 0, Active: false },
                "Paint downloaded camera reference pixels before input became available.");
            paint.SetPaintInputAvailable(true);
            Draw(paint, projected);
            var first = paint.GetPaintReferenceCacheForVerification();
            for (int index = 0; index < 9; index++)
            {
                now += TimeSpan.FromMilliseconds(125);
                Draw(paint, projected);
            }
            var unchanged = paint.GetPaintReferenceCacheForVerification();
            Require(first.Readbacks == 1 && unchanged.Readbacks == 1 &&
                    unchanged.Frames.Length >= 7 && unchanged.Frames.Length <= 9 &&
                    unchanged.Frames.All(frame => ReferenceEquals(first.Frames[0].Bgra, frame.Bgra)) &&
                    unchanged.Frames.Select(frame => frame.PresentedAt).Distinct().Count() == unchanged.Frames.Length &&
                    paint.GetPaintDisturbanceContext() is not null,
                "Unchanged Paint frames lost latency timestamps or repeatedly downloaded identical pixels.");
            var beforeDisable = unchanged.Revision;
            paint.SetPaintInputAvailable(false);
            for (int index = 0; index < 3; index++)
            {
                now += TimeSpan.FromMilliseconds(125);
                Draw(paint, projected);
            }
            var disabled = paint.GetPaintReferenceCacheForVerification();
            Require(!disabled.Active && disabled.Frames.Length == 0 && disabled.Readbacks == 1 &&
                    disabled.Revision > beforeDisable && paint.GetPaintDisturbanceContext() is null,
                "Paint retained usable camera history or read back images while input was unavailable.");
            paint.SetPaintInputAvailable(true);
            Draw(paint, projected);
            var restarted = paint.GetPaintReferenceCacheForVerification();
            Require(restarted.Readbacks == 2 && restarted.Frames.Length == 1 &&
                    !ReferenceEquals(first.Frames[0].Bgra, restarted.Frames[0].Bgra) &&
                    paint.GetPaintDisturbanceContext() is null,
                "Paint reused pre-pause reference pixels or skipped its camera settling delay.");
            now += TimeSpan.FromMilliseconds(125);
            Require(paint.AddPaintDrop(new(.5, .5), .07, now), "Paint reference fixture rejected a real artwork change.");
            Draw(paint, projected);
            var changed = paint.GetPaintReferenceCacheForVerification();
            Require(changed.Readbacks == 3 && !ReferenceEquals(restarted.Frames[0].Bgra, changed.Frames[^1].Bgra) &&
                    !restarted.Frames[0].Bgra.AsSpan().SequenceEqual(changed.Frames[^1].Bgra),
                "Changed Paint artwork kept stale camera reference pixels.");
        }

        using (var paint = NewScene(BoardScreen.Paint))
        {
            paint.DisablePaintIdleStreaksForVerification();
            paint.ResetPaint();
            paint.SetPaintInputAvailable(true);
            Draw(paint, preview);
            var small = paint.GetPaintReferenceCacheForVerification();
            now += TimeSpan.FromMilliseconds(125);
            Draw(paint, projected);
            var grown = paint.GetPaintReferenceCacheForVerification();
            Require(small.Readbacks == 1 && grown.Readbacks == 2 &&
                    !ReferenceEquals(small.Frames[0].Bgra, grown.Frames[^1].Bgra),
                "Paint reused low-resolution camera pixels after its native source target grew.");
            using var replacementDevice = new CanvasDevice();
            using var replacementOutput = new CanvasRenderTarget(replacementDevice, 480, 270, 96);
            now += TimeSpan.FromMilliseconds(125);
            Draw(paint, replacementOutput);
            var changedDevice = paint.GetPaintReferenceCacheForVerification();
            Require(changedDevice.Readbacks == 3 &&
                    !ReferenceEquals(grown.Frames[^1].Bgra, changedDevice.Frames[^1].Bgra),
                "Paint reused old-device pixels after its graphics resources changed.");
        }

        using (var paint = NewScene(BoardScreen.Paint))
        {
            paint.DisablePaintIdleStreaksForVerification();
            paint.ResetPaint();
            Draw(paint, projected);
            paint.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(500);
            Draw(paint, projected);
            var settled = paint.GetHandAcquisitionContext(now)!;
            Require(settled is { ObserveMotion: true, ExpectedScene: not null },
                "Paint did not establish a settled stationary-caption acquisition reference.");
            // Force the publication transition even if this GPU completed the
            // independent menu PNG load before the initial Paint draw. Await
            // the real loader, then let normal Draw publish its readiness.
            var readiness = typeof(SceneCompositor).GetField("_menuPreviewReady",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            readiness.SetValue(paint, false);
            await paint.EnsureMenuPreviewResourcesAsync(device);
            Draw(paint, projected);
            var afterPublication = paint.GetHandAcquisitionContext(now)!;
            Require(readiness.GetValue(paint) is true && paint.GetMenuPreviewDiagnostics().Ready &&
                    afterPublication.ObserveMotion && afterPublication.Revision == settled.Revision &&
                    ReferenceEquals(settled.ExpectedScene, afterPublication.ExpectedScene),
                "Publishing unrelated menu thumbnails reset Paint's active caption acquisition reference.");
        }

        return new { passed = true, globeProjectorAndPreviewShareNativeFrame = true,
            globeBothDrawingOrdersAndSameFrameActions = true, globeNativeGrowthAndDeviceChange = true,
            paintUnchangedFramesReusePixelsAndKeepTimestamps = true,
            paintUnavailableInputHasNoReadbacks = true, paintRestartRejectsOldHistory = true,
            paintChangedArtworkRefreshesPixels = true, paintNativeGrowthAndDeviceChange = true,
            unrelatedThumbnailPublicationPreservesPaintAcquisition = true };

        SceneCompositor NewScene(BoardScreen screen)
        {
            var scene = new SceneCompositor(blackjackClock: () => now, globeClock: () => now, paintClock: () => now);
            scene.SetDisplayAspect(3840 / (double)2160);
            scene.SetBoardSetup(true);
            Vector2[] corners = [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)];
            scene.SetDetectedBoardGrid(corners, Homography.FromFourPoints(
                [new(0, 0), new(3840, 0), new(3840, 2160), new(0, 2160)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            if (screen == BoardScreen.Globe) scene.ShowGlobe();
            else scene.ShowPaint();
            return scene;
        }

        static void Draw(SceneCompositor scene, CanvasRenderTarget target)
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, (float)target.Size.Width, (float)target.Size.Height,
                preview: false, runningSlowly: false);
        }

        static void DrawGlobePreview(SceneCompositor scene, CanvasRenderTarget target)
        {
            using var drawing = target.CreateDrawingSession();
            scene.DrawGlobePreview(drawing, (float)target.Size.Width, (float)target.Size.Height);
        }

        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
