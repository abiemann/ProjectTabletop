#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Camera;
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
        var canvas = new BoardRect(.025, .08, .95, .79);
        var blankFluid = scene.GetPaintDiagnostics().Fluid ??
            throw new InvalidOperationException("Paint has no GPU clear-liquid field.");
        Require(blankFluid.FieldWidth > 0 && blankFluid.FieldHeight > 0 &&
                Math.Max(blankFluid.FieldWidth, blankFluid.FieldHeight) == 1024,
            "Paint has no bounded GPU clear-liquid field.");
        Require(ChangedPixels(blank, Draw(scene), canvas) == 0 &&
                scene.GetPaintDiagnostics().Fluid!.SimulationSteps == blankFluid.SimulationSteps,
            "An idle painting changed without an observed disturbance.");

        // Paint's canvas never uses hand lighting. Its navigation assistance is
        // separate and must first prove interference with a control's label.
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        var acquisition = scene.GetHandAcquisitionContext(now);
        Require(acquisition is { ObserveMotion: true }, "The Paint acquisition fixture did not settle.");
        var exitCenter = new PixelPoint(inset / 2 + (exit.Bounds.X + exit.Bounds.Width / 2) * (1 - inset),
            inset / 2 + (exit.Bounds.Y + exit.Bounds.Height / 2) * (1 - inset));
        var acquisitionHint = new HandAcquisitionHint(new(exitCenter.X - .1, exitCenter.Y - .1, .2, .2),
            exitCenter, .1, now, .1, ControlCoverage: .1);
        scene.CompleteHandAcquisition(acquisition, [acquisitionHint], [], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && blank.SequenceEqual(Draw(scene)),
            "A Paint control disturbance without measured label corruption switched on a spotlight.");
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
        VerifyPaintButtonLighting();

        var firstCenter = new Point2(.47, .55);
        Require(scene.AddPaintDrop(firstCenter, .075, now) && firstOnly.AddPaintDrop(firstCenter, .075, now) &&
                secondReference.AddPaintDrop(new(.20, .78), .075, now),
            "A fresh canvas disturbance did not create its first paint drop.");
        var initial = Draw(scene);
        var injectedField = scene.CapturePaintFieldStatisticsForVerification();
        int initialArea = ChangedPixels(blank, initial, canvas);
        var injectedCenter = scene.CapturePaintFieldProbeForVerification(firstCenter);
        var injectedHalfRadius = scene.CapturePaintFieldProbeForVerification(new(firstCenter.X + .075 * .5, firstCenter.Y));
        var injectedOuterRadius = scene.CapturePaintFieldProbeForVerification(new(firstCenter.X + .075 * .8, firstCenter.Y));
        var injectedThinRim = scene.CapturePaintFieldProbeForVerification(new(firstCenter.X + .075 * .95, firstCenter.Y));
        await Save(target, "paint-single-thick-coat-injected");
        Require(initialArea > 100 && injectedField.HeightMass > 0 && injectedField.SurfaceColorSum > 0 &&
                injectedField.NonFiniteValues == 0 && injectedField.NegativeMaterialValues == 0 &&
                injectedField.OutOfRangeSurfaceColorValues == 0,
            "The new drop has no visible color/positive paint volume or contains invalid floating-point fields.");
        Require(injectedCenter.Height > injectedHalfRadius.Height &&
                injectedHalfRadius.Height > injectedOuterRadius.Height * 1.5 &&
                injectedOuterRadius.Height > injectedThinRim.Height * 2 && injectedThinRim.Height > 0,
            "The deposited paint mound does not taper continuously toward a thinner outer rim.");
        Require(!scene.AddPaintDrop(firstCenter, .075, now), "One camera observation deposited paint twice.");
        Advance(2, scene, firstOnly, secondReference);
        var afterTwo = Draw(scene);
        var evolvedField = scene.CapturePaintFieldStatisticsForVerification();
        await Save(target, "paint-single-thick-coat-2s");
        double heightMassRatio = evolvedField.HeightMass / injectedField.HeightMass;
        Require(evolvedField.NonFiniteValues == 0 && evolvedField.NegativeMaterialValues == 0 &&
                evolvedField.OutOfRangeSurfaceColorValues == 0 &&
                evolvedField.MaximumHeight <= 12.001 && evolvedField.MaximumSpeed < 3 &&
                heightMassRatio is > .95 and < 1.05,
            $"The paint field became invalid or gained/lost excessive volume: ratio {heightMassRatio:F3}. " +
            $"Height {injectedField.HeightMass:F3} -> {evolvedField.HeightMass:F3}. Images: {directory}");
        int twoSecondArea = ChangedPixels(blank, afterTwo, canvas);
        Advance(2, scene, firstOnly, secondReference);
        var afterFour = Draw(scene);
        int fourSecondArea = ChangedPixels(blank, afterFour, canvas);
        var fourSecondField = scene.CapturePaintFieldStatisticsForVerification();
        double fourSecondAreaRatio = fourSecondArea / (double)initialArea;
        double fourSecondPeakRatio = fourSecondField.MaximumHeight / injectedField.MaximumHeight;
        Require(twoSecondArea > initialArea && ChangedPixels(initial, afterFour, canvas) > 100 &&
                fourSecondAreaRatio < 1.60 && fourSecondPeakRatio > .65,
            $"Paint does not spread gradually while retaining a thick mound: areas {initialArea}, {twoSecondArea}, {fourSecondArea}; " +
            $"4 s area ratio {fourSecondAreaRatio:F3}, peak retained {fourSecondPeakRatio:F3}. Images: {directory}");
        await Save(target, "paint-single-thick-coat-4s");
        Require(ChangedPixels(blank, afterFour, exit.Bounds) == 0,
            "Spreading paint changed the Exit control.");

        // Use identical seeds and ages, with the first coat moved away in the
        // comparison scene. Probe unshaded color separately from accumulated
        // height: reflections must not be mistaken for pigment mixing.
        Advance(1, scene, firstOnly, secondReference);
        var secondCenter = firstCenter;
        Require(scene.AddPaintDrop(secondCenter, .075, now) &&
                secondReference.AddPaintDrop(secondCenter, .075, now),
            "An overlapping second disturbance did not add new paint.");
        Draw(scene);
        Draw(secondReference);
        var stackedCenter = scene.CapturePaintFieldProbeForVerification(secondCenter);
        var separateCenter = secondReference.CapturePaintFieldProbeForVerification(secondCenter);
        var oldCenter = firstOnly.CapturePaintFieldProbeForVerification(secondCenter);
        Require(ColorDifference(stackedCenter.SurfaceColor, separateCenter.SurfaceColor) < .02 &&
                ColorDifference(stackedCenter.SurfaceColor, oldCenter.SurfaceColor) > .40 &&
                stackedCenter.Height > separateCenter.Height * 1.5,
            "A thick new coat averaged the previous pigment or failed to build up additional paint height.");
        var fringePoint = new Point2(secondCenter.X + .075 * .94, secondCenter.Y);
        var stackedFringe = scene.CapturePaintFieldProbeForVerification(fringePoint);
        var oldFringe = firstOnly.CapturePaintFieldProbeForVerification(fringePoint);
        var separateFringe = secondReference.CapturePaintFieldProbeForVerification(fringePoint);
        Require(ColorDifference(stackedFringe.SurfaceColor, separateFringe.SurfaceColor) > .05 &&
                ColorDifference(stackedFringe.SurfaceColor, oldFringe.SurfaceColor) > .01 &&
                ColorDifference(stackedFringe.SurfaceColor, oldFringe.SurfaceColor) <
                    ColorDifference(stackedCenter.SurfaceColor, oldCenter.SurfaceColor) &&
                stackedFringe.Height < stackedCenter.Height * .35,
            "Only the thinner coat rim should reveal some of the previous surface color.");
        Advance(2, scene, firstOnly, secondReference);
        var combined = Draw(scene);
        var evolvedStackedCenter = scene.CapturePaintFieldProbeForVerification(secondCenter);
        var evolvedSeparateCenter = secondReference.CapturePaintFieldProbeForVerification(secondCenter);
        Require(ColorDifference(evolvedStackedCenter.SurfaceColor, evolvedSeparateCenter.SurfaceColor) < .04 &&
                evolvedStackedCenter.Height > evolvedSeparateCenter.Height * 1.4,
            "Flow mixed away the newest thick-coat color or flattened the accumulated layers.");
        Draw(scene);
        await Save(target, "paint-new-color-over-thick-base");
        long dropsBeforeFlow = scene.GetPaintDiagnostics().DropCount;
        var beforeFlow = scene.GetPaintDiagnostics().Fluid!;
        Advance(1, scene, firstOnly, secondReference);
        var continuingFlow = Draw(scene);
        var eightSecondField = firstOnly.CapturePaintFieldStatisticsForVerification();
        double eightSecondPeakRatio = eightSecondField.MaximumHeight / injectedField.MaximumHeight;
        Require(eightSecondPeakRatio > .55 && eightSecondField.NonFiniteValues == 0 &&
                eightSecondField.NegativeMaterialValues == 0 && eightSecondField.OutOfRangeSurfaceColorValues == 0 &&
                eightSecondField.HeightMass / injectedField.HeightMass is > .95 and < 1.05,
            $"A deposited coat lost its thick center too quickly: peak retained at 8 s {eightSecondPeakRatio:F3}.");
        Require(scene.GetPaintDiagnostics().DropCount == dropsBeforeFlow &&
                scene.GetPaintDiagnostics().Fluid!.SimulationSteps > beforeFlow.SimulationSteps &&
                ChangedPixels(combined, continuingFlow, new(.35, .40, .32, .30)) > 30,
            "Layered paint stopped settling unless another camera event deposited paint.");

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
        Advance(1, scene);
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

        // Fluid transport uses a bounded field; wet-surface shading and the UI
        // still render at the physical board's native projected pixel density.
        scene.SetDisplayAspect(16d / 9);
        scene.ResetPaint();
        foreach (var position in new Point2[] { new(.29, .38), new(.47, .38), new(.65, .38), new(.38, .55),
            new(.56, .55), new(.74, .55), new(.29, .73), new(.47, .73), new(.65, .73) })
        {
            now += TimeSpan.FromMilliseconds(250);
            Require(scene.AddPaintDrop(position, .105, now), "The dense paint fixture rejected an independent drop.");
        }
        Advance(2, scene);
        DrawNative(scene, native, 3840, 2160);
        var paintRaster = scene.GetPaintDiagnostics();
        var boardRaster = scene.GetBoardResolutionDiagnostics();
        Require(paintRaster.NativeWidth > 3000 && paintRaster.NativeHeight > 1800 &&
                boardRaster.BoardPixelWidth > 3000 && boardRaster.BoardPixelHeight > 1800 &&
                boardRaster.ProjectorPixelWidth == 3840 && boardRaster.ProjectorPixelHeight == 2160 &&
                Math.Abs(paintRaster.Fluid!.FieldWidth / (double)paintRaster.Fluid.FieldHeight - 16d / 9) < .005,
            "The wet surface/UI is not native 4K or the bounded fluid field stretches the physical board's proportions.");
        Require(OutsideBorderIsBlack(native.GetPixelBytes(), 3840, 2160, inset),
            "The native 4K painting escaped its calibrated board boundary.");
        await Save(native, "paint-layered-metallic-coats-native-4k");
        DrawNative(scene, preview, 400, 300, true);
        var afterPreview = scene.GetPaintDiagnostics();
        Require(afterPreview.NativeWidth == paintRaster.NativeWidth && afterPreview.NativeHeight == paintRaster.NativeHeight &&
                afterPreview.Fluid!.SimulationSteps == paintRaster.Fluid!.SimulationSteps &&
                afterPreview.Fluid.FieldWidth == paintRaster.Fluid.FieldWidth &&
                afterPreview.Fluid.FieldHeight == paintRaster.Fluid.FieldHeight,
            "A small laptop preview changed the native surface density or advanced/resized its liquid field.");
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
        Advance(2, fullCanvas);
        var paintedFullCanvas = Draw(fullCanvas);
        Require(ChangedPixels(cleanControls, paintedFullCanvas, new(.18, .012, .72, .035)) > 1000 &&
                ChangedPixels(cleanControls, paintedFullCanvas, new(.075, .965, .11, .025)) > 100 &&
                ChangedPixels(cleanControls, paintedFullCanvas, new(.815, .965, .11, .025)) > 100 &&
                ChangedPixels(enabledControls, paintedFullCanvas, new(.075, .895, .11, .04)) == 0 &&
                ChangedPixels(enabledControls, paintedFullCanvas, new(.815, .895, .11, .04)) == 0,
            "Paint must reach the top and flow below both bottom controls while their interiors remain opaque above it.");
        await Save(target, "paint-beneath-floating-controls");

        // Adjacent paint mounds can meet while their opaque centers keep their
        // own pigments. Save the current layered surface at native density.
        using var liquidGallery = NewScene(out _);
        liquidGallery.SetDisplayAspect(16d / 9);
        DrawNative(liquidGallery, native, 3840, 2160);
        foreach (var position in new Point2[] { new(.42, .49), new(.52, .49), new(.47, .59) })
        {
            now += TimeSpan.FromSeconds(1);
            Require(liquidGallery.AddPaintDrop(position, .09, now), "The layered-paint gallery rejected a distinct drop.");
            Advance(1, liquidGallery);
        }
        Advance(2, liquidGallery);
        DrawNative(liquidGallery, native, 3840, 2160);
        await Save(native, "paint-layered-three-colors-native-4k");

        // Separate drops at their true native raster let visual review compare
        // thick centers and thinning edges without enlarging camera imagery.
        using var coatingGallery = NewScene(out _);
        coatingGallery.SetDisplayAspect(16d / 9);
        DrawNative(coatingGallery, native, 3840, 2160);
        foreach (var position in new Point2[] { new(.30, .50), new(.50, .50), new(.70, .50) })
        {
            now += TimeSpan.FromMilliseconds(250);
            Require(coatingGallery.AddPaintDrop(position, .085, now), "The coating time-series rejected a distinct drop.");
        }
        DrawNative(coatingGallery, native, 3840, 2160);
        await Save(native, "paint-native-coats-0s");
        foreach (double age in new[] { 2d, 4d, 8d })
        {
            Advance(age == 8 ? 4 : 2, coatingGallery);
            DrawNative(coatingGallery, native, 3840, 2160);
            await Save(native, $"paint-native-coats-{age:0}s");
        }

        // The fluid solver uses fixed steps rather than scaling displacement by
        // rendering frequency. Drive three equal drop sequences at 30/60/120 Hz.
        using var thirtyHz = NewScene(out _);
        using var sixtyHz = NewScene(out _);
        using var oneTwentyHz = NewScene(out _);
        foreach (var timingScene in new[] { thirtyHz, sixtyHz, oneTwentyHz })
        {
            Draw(timingScene);
            Require(timingScene.AddPaintDrop(new(.5, .5), .085, now), "The timestep fixture rejected its first drop.");
            Draw(timingScene);
        }
        for (int frame = 1; frame <= 120; frame++)
        {
            now += TimeSpan.FromSeconds(1d / 120);
            DrawNative(oneTwentyHz, target, size, size);
            if (frame % 2 == 0) DrawNative(sixtyHz, target, size, size);
            if (frame % 4 == 0) DrawNative(thirtyHz, target, size, size);
        }
        var thirtyFluid = thirtyHz.GetPaintDiagnostics().Fluid!;
        var sixtyFluid = sixtyHz.GetPaintDiagnostics().Fluid!;
        var oneTwentyFluid = oneTwentyHz.GetPaintDiagnostics().Fluid!;
        Require(Math.Abs(thirtyFluid.SimulatedSeconds - sixtyFluid.SimulatedSeconds) < .035 &&
                Math.Abs(sixtyFluid.SimulatedSeconds - oneTwentyFluid.SimulatedSeconds) < .035 &&
                oneTwentyFluid.SimulatedSeconds > .9 && thirtyFluid.DroppedSeconds < .035 &&
                sixtyFluid.DroppedSeconds < .035 && oneTwentyFluid.DroppedSeconds < .035,
            "Liquid simulation speed depends on whether the same scene is rendered at 30, 60 or 120 Hz.");
        var thirtyPixels = Draw(thirtyHz);
        var sixtyPixels = Draw(sixtyHz);
        var oneTwentyPixels = Draw(oneTwentyHz);
        int changedAtThirtyHz = ChangedPixels(thirtyPixels, sixtyPixels, new(.3, .3, .4, .4));
        int changedAtOneTwentyHz = ChangedPixels(sixtyPixels, oneTwentyPixels, new(.3, .3, .4, .4));
        Require(changedAtThirtyHz < 500 && changedAtOneTwentyHz < 500,
            $"Equal-duration liquid differs across rendering frequency: {changedAtThirtyHz}, {changedAtOneTwentyHz} pixels.");
        long beforeLongGap = sixtyFluid.SimulationSteps;
        now += TimeSpan.FromMinutes(1);
        Draw(sixtyHz);
        var afterLongGap = sixtyHz.GetPaintDiagnostics().Fluid!;
        Require(afterLongGap.SimulationSteps - beforeLongGap is > 0 and <= 4 && afterLongGap.DroppedSeconds > 59,
            "A long frame stall caused an unbounded fluid catch-up instead of capped fixed substeps.");
        Draw(sixtyHz);
        Require(sixtyHz.GetPaintDiagnostics().Fluid!.SimulationSteps == afterLongGap.SimulationSteps,
            "Re-rendering at the same timestamp advanced the liquid a second time.");
        now -= TimeSpan.FromSeconds(1);
        Draw(sixtyHz);
        Require(sixtyHz.GetPaintDiagnostics().Fluid!.SimulationSteps == afterLongGap.SimulationSteps,
            "A backwards timestamp advanced the liquid.");
        now += TimeSpan.FromSeconds(1);

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
        var firstReadyContext = inputScene.GetPaintDisturbanceContext();
        Require(firstReadyContext is not null, "Paint did not become ready after camera settling.");
        // The worker can complete after warmup while its camera frame still
        // belongs to the previous projected board. This frame is otherwise
        // fresh enough to pass the ordinary 350 ms observation lifetime.
        var previousProjectionTime = now.AddMilliseconds(-150);
        var previousProjection = new PaintDisturbanceResult(
            [new(new(.5, .5), .075, .01, previousProjectionTime)], 1, .01, true,
            "delayed-previous-projection", 150);
        Require(inputScene.CompletePaintDisturbance(firstReadyContext!, previousProjection, previousProjectionTime) == 0 &&
                inputScene.GetPaintDiagnostics().DropCount == 0,
            "A delayed camera frame from before projection warmup seeded a new painting.");
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
        return new { passed = true, initialArea, twoSecondArea, fourSecondArea,
            heightMassRatio, fourSecondAreaRatio, fourSecondPeakRatio, eightSecondPeakRatio,
            finiteNonnegativeFlowAndSurfaceColorFields = true,
            restrainedSpreadingAndThickCenterRetained = true, newestThickCoatKeepsItsColor = true,
            accumulatedLayerHeight = true, thinningFringeRevealsPreviousCoat = true, exitProtected = true,
            boundedBoardClip = true, invalidAndRepeatedObservationsRejected = true, freshSessionClearsPainting = true,
            nativePaintRaster = paintRaster, nativeBoardRaster = boardRaster, previewCannotShrinkPaint = true,
            paintCanvasSpotlightsDisabled = true, paintControlLabelSpotlightLifecycle = true,
            paintControlLightExcludedFromDropsIncludingCameraDelay = true,
            animationFramesCompared, cameraDelayMilliseconds = 250,
            renderedAnimationDoesNotRetrigger = true, twoStationaryObstructionsApplyIndependently = true,
            fullCanvasBeneathFloatingControls = true,
            completedInputReplayAndNavigationCalibrationBarriers = true,
            layeredPaintContinuesWithoutNewCameraEvents = true,
            fixedTimeStepIndependentOfRenderFrequency = true, changedAtThirtyHz, changedAtOneTwentyHz,
            boundedLongGapCatchUp = true, zeroAndBackwardsTimeCannotAdvance = true,
            framesObservedBeforeProjectionWarmupRejected = true,
            liveHardwareUnchanged = true, directory, images };

        void VerifyPaintButtonLighting()
        {
            using var lightingScene = NewScene(out double lightingInset, nativeCamera: true);
            for (int frame = 0; frame < 9; frame++)
            {
                Draw(lightingScene);
                lightingScene.GetHandAcquisitionContext(now);
                now += TimeSpan.FromMilliseconds(125);
            }
            var empty = Draw(lightingScene);
            var context = lightingScene.GetHandAcquisitionContext(now)!;
            Require(context is { ObserveMotion: true, AllowsSearchIllumination: true,
                    ExpectedScene.BoardTriggerRegions.Count: 2 },
                "Paint did not expose settled label-gated Exit/Save acquisition.");
            var emptyQuery = Query(empty, context);
            Require(emptyQuery.Hints.Count == 0 && emptyQuery.LightingHints.Count == 0 &&
                    emptyQuery.SearchRegions.Count == 0 && context.IlluminatedHint is null,
                "An idle Paint board illuminated or searched its controls.");
            var canvasOnly = (byte[])empty.Clone();
            FillObstruction(canvasOnly, .5, .55, lightingInset);
            var canvasQuery = Query(canvasOnly, context);
            lightingScene.CompleteHandAcquisition(context, canvasQuery.LightingHints, [], now);
            Require(canvasQuery.Hints.Count == 0 && canvasQuery.SearchRegions.Count == 0 &&
                    lightingScene.GetHandAcquisitionContext(now)?.IlluminatedHint is null &&
                    empty.SequenceEqual(Draw(lightingScene)),
                "An ordinary canvas disturbance illuminated Paint or searched for a navigation gesture.");

            var exitButton = lightingScene.CurrentBoardButtons.Single(button => button.Id == "menu");
            var exitBounds = exitButton.Bounds;
            double exitU = exitBounds.X + exitBounds.Width / 2, exitV = exitBounds.Y + exitBounds.Height / 2;
            var occupied = (byte[])empty.Clone();
            FillObstruction(occupied, exitU, exitV, lightingInset);
            var captionTracker = new HandAcquisitionPresenceTracker();
            var firstArrival = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                context, captionTracker, now);
            Require(firstArrival.LightingHints.Count == 0 && firstArrival.SearchRegions.Count == 0,
                "One generated Exit-label obstruction frame started Paint assistance before confirmation.");
            now += TimeSpan.FromMilliseconds(125);
            var query = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                context, captionTracker, now);
            if (query.LightingHints.Count != 1)
            {
                File.WriteAllBytes(Path.Combine(directory, "button-empty.bgra"), empty);
                File.WriteAllBytes(Path.Combine(directory, "button-occupied.bgra"), occupied);
                File.WriteAllBytes(Path.Combine(directory, "button-expected.bgra"), context.ExpectedScene!.Bgra);
                File.WriteAllText(Path.Combine(directory, "button-presence.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { size, lightingInset,
                        context.SearchPolygon, context.ExpectedScene.CameraToBoard,
                        context.ExpectedScene.BoardSearchRegions, context.ExpectedScene.BoardReferenceRegions,
                        context.ExpectedScene.BoardTriggerRegions, query.Presence }));
            }
            Require(query.LightingHints.Count == 1,
                "Stationary Exit fingers did not produce exactly one label-triggered candidate. " +
                System.Text.Json.JsonSerializer.Serialize(new { query.Presence,
                    context.ExpectedScene!.BoardSearchRegions, context.ExpectedScene.BoardTriggerRegions }) +
                " Diagnostic: " + directory);
            var hint = query.LightingHints[0];
            Require(hint.ControlCoverage is >= .07 && hint.ControlTriggerCoverage is >= .07 &&
                    query.SearchRegions.Count == 1 && query.SearchRegions.Contains(hint.SearchBounds) &&
                    context.IlluminatedHint is null,
                "Stationary fingers over Exit did not request their qualified native crop before fallback illumination.");
            foreach (double? textCoverage in new double?[] { null, .069999, double.NaN,
                         double.PositiveInfinity, double.NegativeInfinity })
            {
                lightingScene.CompleteHandAcquisition(context,
                    [hint with { ControlTriggerCoverage = textCoverage }], [], now);
                Require(lightingScene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                    "Missing, sub-7%, or non-finite Exit-text interference switched on a Paint spotlight.");
            }
            lightingScene.CompleteHandAcquisition(context, [hint with { ObservedAt = now.AddMilliseconds(-1) }], [], now);
            Require(lightingScene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                "An older frame's Exit-text measurement switched on a Paint spotlight.");
            var beforeLightIgnored = lightingScene.GetPaintDisturbanceContext()!.IgnoredRegions!.ToArray();
            lightingScene.CompleteHandAcquisition(context, [hint], [], now);
            var searching = lightingScene.GetHandAcquisitionContext(now)!;
            var litPixels = Draw(lightingScene);
            Require(searching.IlluminatedHint is not null &&
                    Query(occupied, searching).SearchRegions.Contains(hint.SearchBounds) &&
                    ChangedPixels(empty, litPixels, exitBounds) > 300,
                "Exit text interference did not illuminate its hand region and focus the subsequent model search.");

            // A real landmark result must not extinguish this control-specific
            // light. Paint's ordinary hand spotlight still remains disabled.
            var hand = new HandDetection(Hand(exitU, exitV + .03).Landmarks.Select(point =>
                new PixelPoint(size * (lightingInset / 2 + point.X * (1 - lightingInset)),
                    size * (lightingInset / 2 + point.Y * (1 - lightingInset)))).ToArray(), .95, .5);
            var sourceTime = DateTimeOffset.UtcNow;
            lightingScene.SetHandSpotlights([hand], sourceTime);
            for (int frame = 0; frame < 3; frame++)
            {
                now += TimeSpan.FromMilliseconds(400);
                hint = hint with { ObservedAt = now };
                lightingScene.CompleteHandAcquisition(searching, [hint], [hand], now, illuminatedPresence: true);
                searching = lightingScene.GetHandAcquisitionContext(now)!;
                Require(searching.IlluminatedHint is not null && lightingScene.ActiveHandSpotlightCount == 0,
                    "Acquiring real fingers extinguished Paint's Exit light or enabled ordinary canvas hand lighting.");
                litPixels = Draw(lightingScene);
            }
            var paintContext = lightingScene.GetPaintDisturbanceContext()!;
            var lightMask = paintContext.IgnoredRegions!.Except(beforeLightIgnored).Single();
            var outsideControlPoint = new PixelPoint(lightMask.X + lightMask.Width / 2, lightMask.Y + .01);
            Require(!beforeLightIgnored.Any(bounds => Contains(bounds, outsideControlPoint)),
                "The light-exclusion fixture did not extend beyond the ordinary control mask.");
            var syntheticLightDrop = new PaintDisturbanceResult(
                [new(outsideControlPoint, .05, .01, now)], 1, .01, true, "own-button-light");
            Require(lightingScene.CompletePaintDisturbance(paintContext, syntheticLightDrop, now) == 0,
                "Paint accepted its own control spotlight as a drop outside the button mask.");
            var lightDetector = new PaintDisturbanceTracker();
            for (int frame = 0; frame < 2; frame++)
            {
                now += TimeSpan.FromMilliseconds(125);
                hint = hint with { ObservedAt = now };
                lightingScene.CompleteHandAcquisition(searching, [hint], [hand], now, illuminatedPresence: true);
                searching = lightingScene.GetHandAcquisitionContext(now)!;
                litPixels = Draw(lightingScene);
                paintContext = lightingScene.GetPaintDisturbanceContext()!;
                var result = lightDetector.Update(size, size, size * 4, litPixels, paintContext, now, now);
                Require(result.ReferenceReady && result.CandidateCount == 0 && result.Drops.Count == 0,
                    "The rendered Exit spotlight seeded Paint canvas candidates.");
            }

            lightingScene.CompleteHandAcquisition(searching, [], [hand], now, illuminatedPresence: false);
            Require(lightingScene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                "Removing current Exit-text obstruction retained the Paint spotlight after real fingers were acquired.");
            for (int frame = 0; frame < 2; frame++)
            {
                now += TimeSpan.FromMilliseconds(125);
                Draw(lightingScene);
                paintContext = lightingScene.GetPaintDisturbanceContext()!;
                var result = lightDetector.Update(size, size, size * 4, litPixels, paintContext, now, now);
                Require(result.ReferenceReady && result.CandidateCount == 0 && result.Drops.Count == 0 &&
                        paintContext.IgnoredRegions!.Contains(lightMask),
                    "The webcam's delayed view of a switched-off Exit light seeded a Paint drop.");
                var delayedDrop = syntheticLightDrop with
                    { Drops = [syntheticLightDrop.Drops[0] with { ObservedAt = now }] };
                Require(lightingScene.CompletePaintDisturbance(paintContext, delayedDrop, now) == 0,
                    "A late camera result bypassed the recently switched-off light's Paint exclusion.");
            }
            now += TimeSpan.FromMilliseconds(1000);
            Draw(lightingScene);
            Require(!lightingScene.GetPaintDisturbanceContext()!.IgnoredRegions!.Contains(lightMask),
                "The temporary navigation-light exclusion permanently disabled part of the Paint canvas.");

            // Ordinary tracked hands remain dark after navigation assistance is
            // gone. Execution suppression also wins over label measurements.
            context = lightingScene.GetHandAcquisitionContext(now)!;
            hint = hint with { ObservedAt = now };
            lightingScene.CompleteHandAcquisition(context, [hint], [], now);
            Require(lightingScene.GetHandAcquisitionContext(now)?.IlluminatedHint is not null,
                "Paint did not rearm label-gated Exit assistance after departure.");
            sourceTime = DateTimeOffset.UtcNow;
            lightingScene.SetHandCursors([new HandCursor(new(size * .5, size * .5), sourceTime.AddSeconds(1), 7871)
                { TrackingId = 7871 }], sourceTime);
            Require(lightingScene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(7871) &&
                    lightingScene.GetHandAcquisitionContext(now)?.IlluminatedHint is null &&
                    lightingScene.ActiveHandSpotlightCount == 0,
                "An execute gesture retained Paint's navigation spotlight.");
            lightingScene.ShowBoardMenu();
            Require(lightingScene.GetPaintDisturbanceContext() is null,
                "Leaving Paint retained its control-light exclusion context.");

            HandAcquisitionQuery Query(byte[] pixels, SceneCompositor.HandAcquisitionContext request) =>
                CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, pixels, now), request,
                    new HandAcquisitionPresenceTracker(), now);
            static bool Contains(HandTrackingBounds bounds, PixelPoint point) =>
                point.X >= bounds.X && point.X <= bounds.X + bounds.Width &&
                point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.Height;
        }

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
        void Advance(double seconds, params SceneCompositor[] drawingScenes)
        {
            int frames = (int)Math.Ceiling(seconds * 60);
            for (int frame = 0; frame < frames; frame++)
            {
                now += TimeSpan.FromSeconds(seconds / frames);
                foreach (var drawingScene in drawingScenes)
                    DrawNative(drawingScene, target, size, size);
            }
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
        static double ColorDifference(Vector3 first, Vector3 second) =>
            Math.Max(Math.Abs(first.X - second.X), Math.Max(Math.Abs(first.Y - second.Y), Math.Abs(first.Z - second.Z)));
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

}
#endif
