#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // All paint and pixels belong to isolated scenes; the live camera, projector,
    // user's painting, and board registration are never touched by this check.
    private async Task<object> VerifyPaintAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "PaintVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, size, size, 96);
        using var native = new CanvasRenderTarget(device, 3840, 2160, 96);
        using var preview = new CanvasRenderTarget(device, 400, 300, 96);
        using var scene = NewScene(out double inset);
        using var firstOnly = NewScene(out _);
        using var secondReference = NewScene(out _);
        var blank = Draw(scene);
        var exit = scene.CurrentBoardButtons.Single(button => button.Id == "menu");
        Require(scene.CurrentBoardScreen == BoardScreen.Paint && exit is { Id: "menu", Label: "Exit" } &&
                scene.GetPaintDiagnostics().DropCount == 0 &&
                scene.CurrentBoardButtons.Any(button => button.Id == "paint-save" && !button.Enabled),
            "Paint must open blank with bottom-side Exit and an unavailable Save until paint exists.");
        await Save(target, "paint-blank");
        var canvas = new BoardRect(.025, .19, .95, .79);
        Require(ChangedPixels(blank, Draw(scene), canvas) == 0,
            "An idle painting changed without an observed disturbance.");

        // Both ordinary hand lighting and preliminary acquisition lighting must
        // stay dark on Paint, even when their real inputs are valid and fresh.
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        var acquisition = scene.GetHandAcquisitionContext(now);
        Require(acquisition is { ObserveMotion: true }, "The Paint acquisition fixture did not settle.");
        var exitCenter = new PixelPoint(inset / 2 + (exit.Bounds.X + exit.Bounds.Width / 2) * (1 - inset),
            inset / 2 + (exit.Bounds.Y + exit.Bounds.Height / 2) * (1 - inset));
        var acquisitionHint = new HandAcquisitionHint(new(exitCenter.X - .1, exitCenter.Y - .1, .2, .2),
            exitCenter, .1, now, .1);
        scene.CompleteHandAcquisition(acquisition, [acquisitionHint], [], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && blank.SequenceEqual(Draw(scene)),
            "A valid Paint Exit disturbance switched on an acquisition spotlight.");
        await Task.Delay(2);
        var handTime = DateTimeOffset.UtcNow;
        var hand = Hand(.5, .55);
        scene.SetHandCursors([new HandCursor(hand.IndexTip, DateTimeOffset.MinValue) { TrackingId = 7811 }], handTime);
        scene.SetHandSpotlights([hand], handTime);
        var handLighting = scene.GetHandLightingDiagnostics();
        Require(handLighting.Lights.Length == 1 && handLighting.Opacity == 0 && scene.ActiveHandSpotlightCount == 0 &&
                ChangedPixels(blank, Draw(scene), canvas) == 0,
            "A real hand input illuminated Paint or drew its fingertip marker over the painting.");
        scene.ClearHandTips();

        var firstCenter = new Point2(.47, .55);
        Require(scene.AddPaintDrop(firstCenter, .075, now) && firstOnly.AddPaintDrop(firstCenter, .075, now) &&
                secondReference.AddPaintDrop(new(.20, .78), .075, now),
            "A fresh canvas disturbance did not create its first paint drop.");
        var initial = Draw(scene);
        int initialArea = ChangedPixels(blank, initial, canvas);
        Require(initialArea > 100, "The new paint drop has no visible initial pigment.");
        Require(!scene.AddPaintDrop(firstCenter, .075, now), "One camera observation deposited paint twice.");
        now += TimeSpan.FromSeconds(2);
        var afterTwo = Draw(scene);
        int twoSecondArea = ChangedPixels(blank, afterTwo, canvas);
        now += TimeSpan.FromSeconds(6);
        var afterEight = Draw(scene);
        int eightSecondArea = ChangedPixels(blank, afterEight, canvas);
        Require(twoSecondArea > initialArea * 1.2 && eightSecondArea > twoSecondArea * 1.03,
            $"Paint did not spread gradually: areas {initialArea}, {twoSecondArea}, {eightSecondArea} at 0, 2, 8 seconds.");
        await Save(target, "paint-single-spread-8s");
        Require(ChangedPixels(blank, afterEight, exit.Bounds) == 0,
            "Spreading paint changed the Exit control.");

        // The comparison scene has the same drop sequence and second-drop age,
        // but its first pigment is far away. This distinguishes mixed overlap
        // from painting a completely opaque second blob over the old color.
        now += TimeSpan.FromSeconds(1);
        var secondCenter = new Point2(.54, .55);
        Require(scene.AddPaintDrop(secondCenter, .075, now) &&
                secondReference.AddPaintDrop(secondCenter, .075, now),
            "An overlapping second disturbance did not add new paint.");
        now += TimeSpan.FromSeconds(8);
        var combined = Draw(scene);
        var oldPigmentOnly = Draw(firstOnly);
        var newPigmentOnly = Draw(secondReference);
        var overlap = new BoardRect(.493, .529, .024, .042);
        int mixedPixels = PixelsDifferentFromBoth(combined, oldPigmentOnly, newPigmentOnly, overlap);
        Require(mixedPixels > 30,
            $"Overlapping pigment does not visibly combine the old and new colors ({mixedPixels} mixed pixels).");
        Draw(scene);
        await Save(target, "paint-overlapping-pigments");

        long countBeforeRejectedInput = scene.GetPaintDiagnostics().DropCount;
        foreach (var point in new[] { new Point2(.2, -.01), new Point2(-.1, .5), new Point2(.5, 1.1),
            new Point2(double.NaN, .5), new Point2(.5, double.PositiveInfinity) })
            Require(!scene.AddPaintDrop(point, .075, now), "An Exit, off-board or invalid disturbance deposited paint.");
        Require(!scene.AddPaintDrop(new(.8, .8), double.NaN, now) &&
                !scene.AddPaintDrop(new(.8, .8), 0, now) &&
                !scene.AddPaintDrop(new(.8, .8), .075, now.AddSeconds(-2)) &&
                !scene.AddPaintDrop(new(.8, .8), .075, now.AddSeconds(2)) &&
                scene.GetPaintDiagnostics().DropCount == countBeforeRejectedInput,
            "Invalid-radius, stale or future observations deposited paint.");
        Require(scene.AddPaintDrop(new(.985, .98), .11, now), "A valid near-edge drop was rejected.");
        Require(!scene.AddPaintDrop(new(.75, .85), .075, now.AddMilliseconds(-1)),
            "An out-of-order camera observation deposited paint.");
        now += TimeSpan.FromSeconds(8);
        var edgePixels = Draw(scene);
        Require(OutsideBorderIsBlack(edgePixels, size, size, inset) &&
                ChangedPixels(blank, edgePixels, exit.Bounds) == 0,
            "Paint escaped the physical board clip or covered Exit.");

        scene.ShowBoardMenu();
        Draw(scene);
        Require(!scene.AddPaintDrop(new(.5, .5), .075, now), "Paint accepted a disturbance after Exit.");
        scene.ShowPaint();
        var reopened = Draw(scene);
        Require(scene.GetPaintDiagnostics().DropCount == 0 && ChangedPixels(blank, reopened, canvas) == 0,
            "A fresh Paint session retained the previous painting.");

        // Output dimensions are physical pixels. Inspect the dedicated paint
        // layer as well as the shared UI raster so a low-resolution paint image
        // enlarged underneath a sharp Exit button cannot pass this check.
        scene.SetDisplayAspect(16d / 9);
        foreach (var position in new Point2[] { new(.29, .38), new(.47, .38), new(.65, .38), new(.38, .55),
            new(.56, .55), new(.74, .55), new(.29, .73), new(.47, .73), new(.65, .73) })
        {
            now += TimeSpan.FromMilliseconds(250);
            Require(scene.AddPaintDrop(position, .105, now), "The dense paint fixture rejected an independent drop.");
        }
        now += TimeSpan.FromSeconds(8);
        DrawNative(scene, native, 3840, 2160);
        var paintRaster = scene.GetPaintDiagnostics();
        var boardRaster = scene.GetBoardResolutionDiagnostics();
        Require(paintRaster.NativeWidth > 3000 && paintRaster.NativeHeight > 1800 &&
                boardRaster.BoardPixelWidth > 3000 && boardRaster.BoardPixelHeight > 1800 &&
                boardRaster.ProjectorPixelWidth == 3840 && boardRaster.ProjectorPixelHeight == 2160,
            "The 4K board enlarged a low-resolution paint or interface layer.");
        Require(OutsideBorderIsBlack(native.GetPixelBytes(), 3840, 2160, inset),
            "The native 4K painting escaped its calibrated board boundary.");
        await Save(native, "paint-metallic-blends-native-4k");
        DrawNative(scene, preview, 400, 300, true);
        var afterPreview = scene.GetPaintDiagnostics();
        Require(afterPreview.NativeWidth == paintRaster.NativeWidth && afterPreview.NativeHeight == paintRaster.NativeHeight,
            "A small laptop preview shrank the native paint cache.");
        scene.SetBoardSetup(true);
        Require(!scene.AddPaintDrop(new(.5, .5), .075, now), "Calibration accepted a paint disturbance.");
        scene.ClearBoardMediaClip();
        Require(scene.GetPaintDiagnostics().DropCount == 0, "Clearing calibration retained the prior painting.");

        using var fullCanvas = NewScene(out _);
        var cleanControls = Draw(fullCanvas);
        foreach (var position in new Point2[] { new(.20, .04), new(.5, .025), new(.82, .04), new(.02, .20) })
        {
            now += TimeSpan.FromMilliseconds(250);
            Require(fullCanvas.AddPaintDrop(position, .10, now), "The former header is still excluded from paint.");
        }
        // Enable Save before comparing its opaque interior: availability alone
        // changes the label styling independently of paint beneath the button.
        var enabledControls = Draw(fullCanvas);
        foreach (var position in new Point2[] { new(.13, .9175), new(.87, .9175) })
        {
            now += TimeSpan.FromMilliseconds(250);
            Require(fullCanvas.AddPaintDrop(position, .10, now), "Paint cannot flow beneath the bottom-side controls.");
        }
        now += TimeSpan.FromSeconds(8);
        var paintedFullCanvas = Draw(fullCanvas);
        Require(ChangedPixels(cleanControls, paintedFullCanvas, new(.18, .012, .72, .035)) > 1000 &&
                ChangedPixels(cleanControls, paintedFullCanvas, new(.075, .965, .11, .025)) > 100 &&
                ChangedPixels(cleanControls, paintedFullCanvas, new(.815, .965, .11, .025)) > 100 &&
                ChangedPixels(enabledControls, paintedFullCanvas, new(.075, .895, .11, .04)) == 0 &&
                ChangedPixels(enabledControls, paintedFullCanvas, new(.815, .895, .11, .04)) == 0,
            "Paint must reach the top and flow below both bottom controls while their interiors remain opaque above it.");
        await Save(target, "paint-beneath-floating-controls");

        using var gallery = NewScene(out double galleryInset);
        gallery.SetDisplayAspect(16d / 9);
        DrawNative(gallery, native, 3840, 2160);
        var emptyGallery = native.GetPixelBytes();
        Point2[] galleryPositions = (from y in new[] { .34, .58, .82 }
                                    from x in new[] { .18, .40, .62, .84 }
                                    select new Point2(x, y)).ToArray();
        foreach (var position in galleryPositions)
        {
            // Each observation is distinct, but all drops have effectively the
            // same age so the snapshots show comparable spreading stages.
            now += TimeSpan.FromMilliseconds(1);
            Require(gallery.AddPaintDrop(position, .035, now), "The varied paint gallery rejected an independent drop.");
        }
        var galleryStarted = now;
        foreach (double age in new[] { .6, 2d, 8d })
        {
            now = galleryStarted.AddSeconds(age);
            DrawNative(gallery, native, 3840, 2160);
            await Save(native, age == 8 ? "paint-variety-native-4k" :
                age == 2 ? "paint-variety-2s-native-4k" : "paint-variety-0_6s-native-4k");
        }
        var galleryPixels = native.GetPixelBytes();
        var silhouettes = galleryPositions.Select((position, index) =>
            MeasurePaintSilhouette(galleryPixels, emptyGallery, position, galleryInset, index + 1)).ToArray();
        Require(silhouettes.All(shape => shape.Area > 300 && double.IsFinite(shape.Elongation) &&
                double.IsFinite(shape.Compactness)), "One of the twelve paint seeds has no measurable silhouette.");
        Require(silhouettes.All(shape => shape.BoundaryPixels == 0),
            "The paint gallery does not isolate complete silhouettes: " + string.Join("; ",
                silhouettes.Where(shape => shape.BoundaryPixels > 0).Select(shape =>
                    $"seed{shape.Seed}:boundary pixels={shape.BoundaryPixels}")));
        double areaRatio = (double)silhouettes.Max(shape => shape.Area) / silhouettes.Min(shape => shape.Area);
        double elongationRange = silhouettes.Max(shape => shape.Elongation) - silhouettes.Min(shape => shape.Elongation);
        double compactnessRange = silhouettes.Max(shape => shape.Compactness) - silhouettes.Min(shape => shape.Compactness);
        var distinctProfiles = new List<PaintSilhouetteMetrics>();
        foreach (var shape in silhouettes)
            if (distinctProfiles.All(other => Math.Abs(Math.Log((double)shape.Area / other.Area)) > .22 ||
                Math.Abs(shape.Elongation - other.Elongation) > .25 ||
                Math.Abs(shape.Compactness - other.Compactness) > .08)) distinctProfiles.Add(shape);
        Require(areaRatio > 1.25 && (elongationRange > .3 || compactnessRange > .08) && distinctProfiles.Count >= 3,
            $"Paint repeats substantially the same rendered silhouette across colors: area ratio {areaRatio:F3}, " +
            $"elongation range {elongationRange:F3}, compactness range {compactnessRange:F3}, " +
            $"distinct profiles {distinctProfiles.Count}. " + string.Join("; ", silhouettes.Select(shape =>
                $"seed{shape.Seed}:area={shape.Area},elongation={shape.Elongation:F3},compactness={shape.Compactness:F3}")));

        using var inputScene = NewScene(out double inputInset, nativeCamera: true);
        var detector = new PaintDisturbanceTracker();
        var submittedFrames = new List<byte[]>();
        for (int warmup = 0; warmup < 8; warmup++)
        {
            Draw(inputScene);
            Require(inputScene.GetPaintDisturbanceContext() is null,
                "Paint accepted camera input before the previous projection could leave the webcam feed.");
            now += TimeSpan.FromMilliseconds(125);
        }
        Draw(inputScene);
        Require(inputScene.GetPaintDisturbanceContext() is not null, "Paint did not become ready after camera settling.");
        Require(inputScene.AddPaintDrop(new(.45, .03), .11, now), "The Paint animation fixture could not begin above the old header.");
        int animationFramesCompared = 0;
        // A webcam observes an older submitted projector frame. Feed actual GPU
        // pixels with 250 ms delay while the current painting keeps spreading.
        for (int frame = 0; frame < 32; frame++)
        {
            now += TimeSpan.FromMilliseconds(125);
            Point2? addedPosition = frame switch
            {
                4 => new Point2(.62, .50),
                8 => new Point2(.35, .50),
                12 => new Point2(.72, .65),
                16 => new Point2(.38, .66),
                _ => null
            };
            if (addedPosition is { } position)
                Require(inputScene.AddPaintDrop(position, .10, now),
                    "The multicolor delayed-render fixture rejected a separate drop.");
            submittedFrames.Add(Draw(inputScene));
            if (submittedFrames.Count < 3) continue;
            var context = inputScene.GetPaintDisturbanceContext();
            Require(context is not null, "A rendered Paint board has no disturbance context.");
            var result = detector.Update(size, size, size * 4, submittedFrames[^3], context!, now, now);
            Require(result.ReferenceReady && result.Drops.Count == 0,
                $"The app's own delayed paint animation created foreground drops: frame {frame}, {result.Reason}.");
            Require(inputScene.CompletePaintDisturbance(context!, result, now) == 0,
                "A clean projected animation changed the painting through camera input.");
            animationFramesCompared++;
        }
        long beforePhysicalInput = inputScene.GetPaintDiagnostics().DropCount;
        // Selecting floating controls must not deposit paint beneath the hand.
        // Paint can already be flowing behind those opaque controls.
        var controlDetector = new PaintDisturbanceTracker();
        for (int observation = 0; observation < 2; observation++)
        {
            now += TimeSpan.FromMilliseconds(125);
            submittedFrames.Add(Draw(inputScene));
            var overControls = (byte[])submittedFrames[^3].Clone();
            // A small camera registration error and projector blur can move the
            // bright bevel/shadow outside a control's logical hit rectangle.
            foreach (var control in inputScene.CurrentBoardButtons.Select(button => button.Bounds)
                .Append(new BoardRect(.06, .018, .10, .028)))
                ShiftControlEdge(overControls, submittedFrames[^3], control, inputInset);
            FillObstruction(overControls, .13, .9175, inputInset);
            FillObstruction(overControls, .87, .9175, inputInset);
            var controlContext = inputScene.GetPaintDisturbanceContext()!;
            var controlResult = controlDetector.Update(size, size, size * 4, overControls, controlContext, now, now);
            Require(controlResult.ReferenceReady && controlResult.Drops.Count == 0 && controlResult.CandidateCount == 0,
                "Floating control edges or operating Exit/Save deposited paint.");
        }
        PaintDisturbanceScene? physicalContext = null;
        PaintDisturbanceResult? physicalResult = null;
        foreach (int observation in new[] { 0, 1 })
        {
            now += TimeSpan.FromMilliseconds(125);
            submittedFrames.Add(Draw(inputScene));
            var occupied = (byte[])submittedFrames[^3].Clone();
            FillObstruction(occupied, .32, .73, inputInset);
            FillObstruction(occupied, .74, .73, inputInset);
            physicalContext = inputScene.GetPaintDisturbanceContext();
            physicalResult = detector.Update(size, size, size * 4, occupied, physicalContext!, now, now);
            Require(physicalResult.ReferenceReady && physicalResult.CandidateCount == 2,
                $"The two stationary obstructions were not isolated from paint: {physicalResult.Reason}.");
            if (observation == 0)
                Require(physicalResult.Drops.Count == 0, "One unconfirmed obstruction frame deposited paint.");
            else
                Require(physicalResult.Drops.Count == 2 &&
                        physicalResult.Drops.Any(drop => Math.Abs(drop.BoardCenter.X - .32) < .04) &&
                        physicalResult.Drops.Any(drop => Math.Abs(drop.BoardCenter.X - .74) < .04),
                    "Confirmed stationary obstructions did not produce two correctly mapped drop positions.");
        }
        Require(inputScene.CompletePaintDisturbance(physicalContext!, physicalResult!, now) == 2 &&
                inputScene.GetPaintDiagnostics().DropCount == beforePhysicalInput + 2,
            "Two physical disturbances in one camera frame did not both reach the painting.");
        Require(inputScene.CompletePaintDisturbance(physicalContext!, physicalResult!, now) == 0,
            "Replaying a completed camera result duplicated its paint drops.");
        inputScene.ShowBoardMenu();
        Require(inputScene.CompletePaintDisturbance(physicalContext!, physicalResult!, now) == 0,
            "An in-flight camera result painted after Exit.");
        inputScene.ShowPaint();
        Require(inputScene.CompletePaintDisturbance(physicalContext!, physicalResult!, now) == 0,
            "The previous Paint visit's camera result contaminated a new painting.");
        now += TimeSpan.FromSeconds(1);
        Draw(inputScene);
        var beforeRescan = inputScene.GetPaintDisturbanceContext()!;
        inputScene.SetBoardSetup(true);
        Point2[] cameraPixels = [new(0, 0), new(size, 0), new(size, size), new(0, size)];
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        inputScene.SetDetectedBoardGrid([new(.02f, .02f), new(.98f, .02f), new(.98f, .98f), new(.02f, .98f)],
            Homography.FromFourPoints(cameraPixels, unit));
        inputScene.SetBoardSetup(false);
        Require(inputScene.GetPaintDisturbanceContext() is null &&
                inputScene.CompletePaintDisturbance(beforeRescan, physicalResult!, now) == 0 &&
                inputScene.GetPaintDiagnostics().DropCount == 0,
            "A result mapped before recalibration deposited paint at an obsolete board position.");
        now += TimeSpan.FromSeconds(1);
        Draw(inputScene);
        Require(inputScene.GetPaintDisturbanceContext()!.Revision != beforeRescan.Revision,
            "Recalibration reused the old Paint reference generation.");

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated Paint verification changed the live camera, projector or board.");
        return new { passed = true, initialArea, twoSecondArea, eightSecondArea, mixedPixels,
            progressiveSpreading = true, overlappingPigmentsBlend = true, exitProtected = true,
            boundedBoardClip = true, invalidAndRepeatedObservationsRejected = true, freshSessionClearsPainting = true,
            nativePaintRaster = paintRaster, nativeBoardRaster = boardRaster, previewCannotShrinkPaint = true,
            allPaintSpotlightsDisabled = true, animationFramesCompared, cameraDelayMilliseconds = 250,
            renderedAnimationDoesNotRetrigger = true, twoStationaryObstructionsApplyIndependently = true,
            fullCanvasBeneathFloatingControls = true,
            completedInputReplayAndNavigationCalibrationBarriers = true,
            variedRenderedSilhouettes = true, silhouettes, silhouetteAreaRatio = areaRatio,
            silhouetteElongationRange = elongationRange, silhouetteCompactnessRange = compactnessRange,
            distinctSilhouetteProfiles = distinctProfiles.Count,
            liveHardwareUnchanged = true, directory, images };

        SceneCompositor NewScene(out double safetyInset, bool nativeCamera = false)
        {
            var result = new SceneCompositor(blackjackClock: () => now, paintClock: () => now);
            result.SetDisplayAspect(1);
            result.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] camera = nativeCamera ? [new(0, 0), new(size, 0), new(size, size), new(0, size)] : unit;
            safetyInset = result.SetDetectedBoardGrid(corners, Homography.FromFourPoints(camera, unit));
            result.SetBoardSetup(false);
            result.ShowPaint();
            return result;
        }
        byte[] Draw(SceneCompositor drawingScene)
        {
            DrawNative(drawingScene, target, size, size);
            return target.GetPixelBytes();
        }
        static void DrawNative(SceneCompositor drawingScene, CanvasRenderTarget output, float width, float height,
            bool isPreview = false)
        {
            using var drawing = output.CreateDrawingSession();
            drawingScene.Draw(drawing, width, height, preview: isPreview, runningSlowly: false);
        }
        async Task Save(CanvasRenderTarget output, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await output.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        IEnumerable<int> RegionOffsets(BoardRect region)
        {
            int left = (int)Math.Ceiling(size * (inset / 2 + region.X * (1 - inset)));
            int top = (int)Math.Ceiling(size * (inset / 2 + region.Y * (1 - inset)));
            int right = (int)Math.Floor(size * (inset / 2 + (region.X + region.Width) * (1 - inset)));
            int bottom = (int)Math.Floor(size * (inset / 2 + (region.Y + region.Height) * (1 - inset)));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++) yield return (y * size + x) * 4;
        }
        int ChangedPixels(byte[] first, byte[] second, BoardRect region) =>
            RegionOffsets(region).Count(offset => MaxDifference(first, second, offset) > 12);
        int PixelsDifferentFromBoth(byte[] combinedPixels, byte[] first, byte[] second, BoardRect region) =>
            RegionOffsets(region).Count(offset => MaxDifference(combinedPixels, first, offset) > 12 &&
                MaxDifference(combinedPixels, second, offset) > 12);
        static int MaxDifference(byte[] first, byte[] second, int offset) =>
            Math.Max(Math.Abs(first[offset] - second[offset]), Math.Max(Math.Abs(first[offset + 1] - second[offset + 1]),
                Math.Abs(first[offset + 2] - second[offset + 2])));
        static void FillObstruction(byte[] pixels, double u, double v, double safetyInset)
        {
            int centerX = (int)(size * (safetyInset / 2 + u * (1 - safetyInset)));
            int centerY = (int)(size * (safetyInset / 2 + v * (1 - safetyInset)));
            for (int y = centerY - 42; y < centerY + 43; y++)
            for (int x = centerX - 36; x < centerX + 37; x++)
            {
                int offset = (y * size + x) * 4;
                pixels[offset] = 95; pixels[offset + 1] = 145; pixels[offset + 2] = 195; pixels[offset + 3] = 255;
            }
        }
        static void ShiftControlEdge(byte[] pixels, byte[] source, BoardRect bounds, double safetyInset)
        {
            int left = (int)(size * (safetyInset / 2 + bounds.X * (1 - safetyInset)));
            int top = (int)(size * (safetyInset / 2 + bounds.Y * (1 - safetyInset)));
            int right = (int)(size * (safetyInset / 2 + (bounds.X + bounds.Width) * (1 - safetyInset)));
            int bottom = (int)(size * (safetyInset / 2 + (bounds.Y + bounds.Height) * (1 - safetyInset)));
            for (int y = top - 3; y <= bottom + 7; y++)
            for (int x = left - 3; x <= right + 3; x++)
                Buffer.BlockCopy(source, (y * size + x) * 4, pixels, ((y + 7) * size + x + 6) * 4, 4);
        }
        static HandDetection Hand(double x, double y)
        {
            PixelPoint[] local =
            [
                new(0, .09), new(-.03, .06), new(-.055, .035), new(-.07, .015), new(-.09, 0),
                new(-.035, .015), new(-.04, -.02), new(-.045, -.05), new(-.05, -.08),
                new(0, 0), new(0, -.04), new(0, -.07), new(0, -.1),
                new(.03, .015), new(.035, -.02), new(.04, -.045), new(.045, -.07),
                new(.055, .03), new(.065, .005), new(.075, -.01), new(.08, -.025)
            ];
            return new(local.Select(point => new PixelPoint(x + point.X, y + point.Y)).ToArray(), .95, .5);
        }
        static bool OutsideBorderIsBlack(byte[] pixels, int width, int height, double safetyInset)
        {
            int xMargin = Math.Max(1, (int)(width * safetyInset / 2) - 2);
            int yMargin = Math.Max(1, (int)(height * safetyInset / 2) - 2);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= xMargin && x < width - xMargin && y >= yMargin && y < height - yMargin) continue;
                int offset = (y * width + x) * 4;
                if (pixels[offset] > 2 || pixels[offset + 1] > 2 || pixels[offset + 2] > 2) return false;
            }
            return true;
        }
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }

    private sealed record PaintSilhouetteMetrics(int Seed, int Area, double Elongation, double Compactness,
        int Width, int Height, int BoundaryPixels);

    private static PaintSilhouetteMetrics MeasurePaintSilhouette(byte[] pixels, byte[] empty, Point2 center,
        double inset, int seed)
    {
        const int width = 3840, height = 2160, step = 3;
        int left = (int)(width * (inset / 2 + (center.X - .11) * (1 - inset)));
        int top = (int)(height * (inset / 2 + (center.Y - .12) * (1 - inset)));
        int columns = (int)(width * .22 * (1 - inset)) / step;
        int rows = (int)(height * .24 * (1 - inset)) / step;
        var ink = new bool[columns * rows];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            int offset = ((top + y * step + step / 2) * width + left + x * step + step / 2) * 4;
            int pixelDifference = Math.Max(Math.Abs(pixels[offset] - empty[offset]),
                Math.Max(Math.Abs(pixels[offset + 1] - empty[offset + 1]),
                    Math.Abs(pixels[offset + 2] - empty[offset + 2])));
            ink[y * columns + x] = pixelDifference > 12;
        }

        // Discard color and fill enclosed texture holes. The remaining binary
        // shape measures footprint and edge structure, rather than glitter,
        // pigment hue, or a particular implementation's family identifier.
        var outside = new bool[ink.Length];
        var pending = new Queue<int>();
        void Visit(int x, int y)
        {
            if (x < 0 || x >= columns || y < 0 || y >= rows) return;
            int index = y * columns + x;
            if (ink[index] || outside[index]) return;
            outside[index] = true;
            pending.Enqueue(index);
        }
        for (int x = 0; x < columns; x++) { Visit(x, 0); Visit(x, rows - 1); }
        for (int y = 0; y < rows; y++) { Visit(0, y); Visit(columns - 1, y); }
        while (pending.TryDequeue(out int index))
        {
            int x = index % columns, y = index / columns;
            Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
        }
        int area = 0, perimeter = 0, boundary = 0;
        int minX = columns, maxX = -1, minY = rows, maxY = -1;
        double sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;
        bool Filled(int x, int y) => x >= 0 && x < columns && y >= 0 && y < rows && !outside[y * columns + x];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            if (!Filled(x, y)) continue;
            area++;
            sumX += x; sumY += y; sumXX += x * x; sumYY += y * y; sumXY += x * y;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            if (!Filled(x - 1, y)) perimeter++;
            if (!Filled(x + 1, y)) perimeter++;
            if (!Filled(x, y - 1)) perimeter++;
            if (!Filled(x, y + 1)) perimeter++;
            if (x is 0 || x == columns - 1 || y is 0 || y == rows - 1) boundary++;
        }
        if (area == 0) return new(seed, 0, double.NaN, double.NaN, 0, 0, 0);
        double varianceX = sumXX / area - Math.Pow(sumX / area, 2);
        double varianceY = sumYY / area - Math.Pow(sumY / area, 2);
        double covariance = sumXY / area - sumX * sumY / (area * (double)area);
        double trace = varianceX + varianceY;
        double eigenvalueGap = Math.Sqrt(Math.Pow(varianceX - varianceY, 2) + 4 * covariance * covariance);
        double elongation = Math.Sqrt((trace + eigenvalueGap) / Math.Max(.000001, trace - eigenvalueGap));
        double compactness = 4 * Math.PI * area / (perimeter * (double)perimeter);
        return new(seed, area * step * step, elongation, compactness,
            (maxX - minX + 1) * step, (maxY - minY + 1) * step, boundary * step);
    }
}
#endif
