using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionPresenceRegression
{
    private const int Width = 640, Height = 360;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(80, 100), new(560, 100), new(580, 330), new(60, 330)];
    private static readonly double[] Matrix = [1.0 / Width, .0001, -.02, .00002, 1.0 / Height, -.02, .00013, .00006, 1];

    public static void Run()
    {
        StationaryAtStartupAndRemoval();
        ExposureGeometryAndNoise();
        UniqueControlCoveredAtStartup();
        OwnLightPresenceAndRemoval();
        HandAcquisitionIlluminatedRenewalRegression.Run();
        IlluminatedControlAreaFloor();
        PointOfInterestSampling();
        CompactReferenceControlOcclusion();
        ControlTriggerRegions();
        HandAcquisitionManyControlsRegression.Run();
        HandAcquisitionTransparentReferenceRegression.Run();
        RenderedCompactControls();
        HandAcquisitionOpticalHistoryRegression.Run();
        HandAcquisitionGoldDealRegression.Run();
        HandAcquisitionShortLabelRegression.Run();
        HandAcquisitionCaptionResolutionRegression.Run();
        HandAcquisitionCompactControlRegression.Run();
        HandAcquisitionCoherentShapeRegression.Run();
        HandAcquisitionReflectanceFitRegression.Run();
        HandAcquisitionLocalContextRegression.Run();
        HandAcquisitionReachingRegression.Run();
        HandAcquisitionHintRegression.Run();
        RenderedLocalCaptionLoss();
        BaselineFallbackAndBarriers();
        PhotoCopyControlRegionsRegression.Run();
        Console.WriteLine("Hand acquisition presence regression: stationary foreground present at startup, persistent " +
            "known-render comparison, native projective geometry, photometric/exposure compensation, raster-edge/noise " +
            "rejection, point-of-interest-only camera sampling, bounded crops, own-white-light exclusion, " +
            "lit foreground versus empty light, compact-control reference obstruction safeguards, paired fresh text-trigger " +
            "coverage with independent 7% floors, generated bright/dark glyph structure, palette/noise/blur " +
            "rejection, bounded cached registration, removal, and reset/time barriers passed.");
    }

    private static void StationaryAtStartupAndRemoval()
    {
        var scene = Scene();
        byte[] empty = Camera(scene), hand = Camera(scene);
        Rectangle(hand, 265, 205, 65, 95, 60, 85, 150);
        var tracker = new HandAcquisitionPresenceTracker();
        HandAcquisitionPresenceResult initial = Feed(tracker, hand, scene, 0);
        Require(initial.Hints.Count == 1 && initial.BaselineReady, "A hand already present in the first frame was learned as empty background.");
        HandAcquisitionHint hint = initial.Hints[0];
        Require(Math.Abs(hint.Center.X - 297.5) < 15 && Math.Abs(hint.Center.Y - 252.5) < 15,
            "The known-render comparison located foreground outside its native camera position.");
        CheckCrop(hint);
        for (int frame = 1; frame <= 18; frame++)
        {
            var current = Feed(tracker, hand, scene, frame * 100);
            Require(current.Hints.Count == 1 && current.Hints[0].ObservedAt == Epoch.AddMilliseconds(frame * 100),
                "A stationary foreground object was absorbed or treated as an expired motion hint.");
        }
        Require(Feed(tracker, empty, scene, 1900).Hints.Count == 0, "Removed foreground left a presence ghost.");
        var cleanStartup = Feed(new(), empty, scene, 0);
        Require(cleanStartup.Hints.Count == 0, "The projected controls themselves acquired a spotlight.");
    }

    private static void ExposureGeometryAndNoise()
    {
        var scene = Scene();
        var tracker = new HandAcquisitionPresenceTracker();
        Require(Feed(tracker, Camera(scene), scene, 0).Hints.Count == 0, "The initial projected template did not match.");
        byte[] brighter = Camera(scene, brightness: 23, gain: 1.08);
        Require(Feed(tracker, brighter, scene, 100).Hints.Count == 0, "Auto-exposure changes were mistaken for foreground.");
        byte[] darker = Camera(scene, brightness: -12, gain: .82, noise: 3);
        Require(Feed(tracker, darker, scene, 200).Hints.Count == 0, "Camera noise or darker exposure created a presence hint.");
        byte[] shifted = Camera(scene, shiftX: 2, shiftY: -1);
        Require(Feed(tracker, shifted, scene, 300).Hints.Count == 0,
            "Small calibration/raster edge differences created a hand-sized region.");
        Rectangle(brighter, 275, 215, 60, 85, 70, 95, 160);
        Require(Feed(tracker, brighter, scene, 400).Hints.Count == 1,
            "Exposure compensation erased foreground along with the global camera change.");

        byte[] tiny = Camera(scene);
        Rectangle(tiny, 180, 180, 8, 10, 255, 255, 255);
        Require(Feed(new(), tiny, scene, 0).Hints.Count == 0, "A tiny unmatched decoration became an acquisition region.");
        byte[] outside = Camera(scene);
        Rectangle(outside, 0, 0, 70, 70, 255, 0, 255);
        Require(Feed(new(), outside, scene, 0).Hints.Count == 0, "Foreground outside the calibrated polygon was included.");
        byte[] multiple = Camera(scene);
        Rectangle(multiple, 110, 175, 45, 65, 70, 90, 155);
        Rectangle(multiple, 285, 180, 45, 65, 70, 90, 155);
        Rectangle(multiple, 465, 180, 45, 65, 70, 90, 155);
        var hints = Feed(new(), multiple, scene, 0).Hints;
        Require(hints.Count == 2, "Persistent foreground was not bounded to two useful search regions.");
        foreach (var hint in hints) CheckCrop(hint);
    }

    private static void OwnLightPresenceAndRemoval()
    {
        var scene = Scene();
        var tracker = new HandAcquisitionPresenceTracker();
        var hint = new HandAcquisitionHint(new(200, 150, 200, 200), new(300, 250), 65, Epoch, .04);
        byte[] empty = Camera(scene), lit = Camera(scene), occupied = Camera(scene);
        Circle(lit, hint.Center, hint.RadiusPixels, 215, 225, 220);
        Circle(occupied, hint.Center, hint.RadiusPixels, 215, 225, 220);
        Rectangle(occupied, 283, 220, 34, 60, 85, 115, 165);
        Feed(tracker, empty, scene, 0);
        Require(Feed(tracker, occupied, scene, 100, hint, 100).IlluminatedPresence is null,
            "The first projector/camera light transition was judged as a physical object.");
        var present = Feed(tracker, occupied, scene, 350, hint, 100);
        Require(present.IlluminatedPresence == true && present.Hints.Count > 0,
            "An illuminated stationary hand failed to keep fresh foreground evidence.");
        Require(present.Hints[0].ControlCoverage is null,
            "A generic white-core observation invented rendered-control coverage.");
        for (int frame = 4; frame <= 14; frame++)
            Require(Feed(tracker, occupied, scene, frame * 100, hint, 100).IlluminatedPresence == true,
                "Own lighting absorbed stationary foreground after the initial illumination.");
        var removed = Feed(tracker, lit, scene, 1500, hint, 100);
        Require(removed.IlluminatedPresence == false && removed.Hints.Count == 0,
            "An empty white search light kept itself on or became foreground.");
        Require(Feed(tracker, empty, scene, 1600).Hints.Count == 0,
            "Turning the search light off created a persistent false acquisition.");

        byte[] occluded = Camera(scene);
        Circle(occluded, hint.Center, hint.RadiusPixels, 40, 50, 90);
        var uncertain = Feed(tracker, occluded, scene, 1700, hint, 100);
        Require(uncertain.IlluminatedPresence is not false,
            "A fully dark, uniformly occluded light was confidently called empty.");
    }

    private static void IlluminatedControlAreaFloor()
    {
        var scene = Scene() with
        {
            BoardSearchRegions = [new(.32, .48, .27, .31)],
            BoardReferenceRegions = [new(.13, .32, .72, .14)]
        };
        var hint = new HandAcquisitionHint(new(200, 150, 200, 200), new(300, 250), 65,
            Epoch, .04, ControlCoverage: .91);
        var tracker = new HandAcquisitionPresenceTracker();
        Feed(tracker, Camera(scene), scene, 0);
        byte[] occupied = Camera(scene);
        Circle(occupied, hint.Center, hint.RadiusPixels, 215, 225, 220);
        Rectangle(occupied, 283, 220, 34, 60, 85, 115, 165);
        var retained = Feed(tracker, occupied, scene, 350, hint, 100);
        Require(retained.IlluminatedPresence == true && retained.Hints.Count > 0 &&
                retained.Hints[0].ControlCoverage is >= HandAcquisitionPresenceTracker.MinimumControlCoverage and < .91 &&
                retained.Hints[0].ObservedAt == Epoch.AddMilliseconds(350),
            "An illuminated hand did not provide fresh measured control coverage: " + retained.Reason + ".");

        // This dark patch is large enough for the former white-core-only test,
        // but covers less than 7% of the control. Old .91 coverage must not help.
        byte[] fragment = Camera(scene);
        Circle(fragment, hint.Center, hint.RadiusPixels, 215, 225, 220);
        Rectangle(fragment, 290, 234, 20, 30, 85, 115, 165);
        var rejected = Feed(tracker, fragment, scene, 500, hint, 100);
        Require(rejected.IlluminatedPresence == false && rejected.Hints.Count == 0,
            "A sub-7% illuminated fragment reused old coverage to keep a search light on: " + rejected.Reason + ".");

        var generic = new HandAcquisitionPresenceTracker();
        var unrestricted = Scene();
        Feed(generic, Camera(unrestricted), unrestricted, 0);
        var genericPresence = Feed(generic, occupied, unrestricted, 350, hint, 100);
        Require(genericPresence.IlluminatedPresence == true && genericPresence.Hints[0].ControlCoverage is null,
            "An unrestricted white-core observation retained an old control coverage value.");
    }

    private static void PointOfInterestSampling()
    {
        HandTrackingBounds[] controls = [new(.32, .48, .27, .31)];
        HandTrackingBounds[] referenceRegions = [new(.10, .60, .18, .19), new(.62, .60, .22, .19)];
        var scene = Scene() with { BoardSearchRegions = controls, BoardReferenceRegions = referenceRegions };
        var tracker = new HandAcquisitionPresenceTracker();
        Require(Feed(tracker, Camera(scene), scene, 0).Hints.Count == 0,
            "Unoccluded points of interest did not match their generated reference.");
        int restrictedCells = tracker.SampledCellCount;
        var unrestricted = new HandAcquisitionPresenceTracker();
        Feed(unrestricted, Camera(scene), Scene(), 0);
        Require(restrictedCells > 80 && restrictedCells < unrestricted.SampledCellCount * .45,
            "Known control geometry still sampled the whole board instead of controls and exposure reference patches.");

        // Change every unrelated board pixel, rather than requiring motion near
        // a button. Neither rendering elsewhere nor a still off-button object
        // can supply candidate evidence or expand the camera sampling footprint.
        byte[] elsewhere = Camera(scene);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double divisor = Matrix[6] * x + Matrix[7] * y + Matrix[8];
            double u = (Matrix[0] * x + Matrix[1] * y + Matrix[2]) / divisor;
            double v = (Matrix[3] * x + Matrix[4] * y + Matrix[5]) / divisor;
            // Keep one sampling-cell margin so samples at a region boundary
            // do not mix the deliberately changed neighboring pixels.
            if (controls.Concat(referenceRegions).Any(region => u >= region.X - .012 &&
                u <= region.X + region.Width + .012 && v >= region.Y - .012 && v <= region.Y + region.Height + .012)) continue;
            Set(elsewhere, x, y, 255, 0, 255);
        }
        Require(Feed(tracker, elsewhere, scene, 100).Hints.Count == 0 && tracker.SampledCellCount == restrictedCells,
            "Off-button board disturbance acquired a light or caused whole-board camera sampling.");

        byte[] hand = Camera(scene, brightness: 18, gain: .90, noise: 2);
        Rectangle(hand, 265, 205, 65, 95, 60, 85, 150);
        for (int frame = 2; frame <= 7; frame++)
        {
            var presence = Feed(tracker, hand, scene, frame * 100);
            Require(presence.Hints.Count == 1 && presence.Hints[0].ControlCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage &&
                tracker.SampledCellCount == restrictedCells,
                "Point-of-interest sampling lost a stationary hand or its independent exposure compensation.");
        }

        // The renewal core can cross the button edge. Read its visible white
        // pixels as well, while retaining the measured control-area floor.
        var light = new HandAcquisitionHint(new(200, 150, 200, 200), new(300, 285), 75, Epoch, .04);
        byte[] litHand = Camera(scene);
        Circle(litHand, light.Center, light.RadiusPixels, 215, 225, 220);
        Rectangle(litHand, 283, 253, 34, 47, 85, 115, 165);
        var retained = Feed(tracker, litHand, scene, 1000, light, 700);
        Require(retained.IlluminatedPresence == true && retained.Hints[0].ControlCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage,
            "Restricted camera sampling missed foreground under a search light crossing the control edge: " + retained.Reason + ".");
        Require(tracker.SampledCellCount > restrictedCells && tracker.SampledCellCount < unrestricted.SampledCellCount * .50,
            "Search-light renewal did not add just the visible white core to point-of-interest sampling.");
        byte[] litEmpty = Camera(scene);
        Circle(litEmpty, light.Center, light.RadiusPixels, 215, 225, 220);
        Require(Feed(tracker, litEmpty, scene, 1100, light, 700).IlluminatedPresence == false,
            "An empty white core was mistaken for foreground after restricting camera sampling.");
        Require(Feed(tracker, Camera(scene), scene, 1200).Hints.Count == 0 && tracker.SampledCellCount == restrictedCells,
            "Removing the search light retained expanded sampling or a stale foreground hint.");
    }

    private static void UniqueControlCoveredAtStartup()
    {
        var scene = Scene();
        for (int y = 77; y <= 105; y++)
        for (int x = 77; x <= 106; x++)
        {
            int offset = (y * scene.Width + x) * 4;
            scene.Bgra[offset] = 25; scene.Bgra[offset + 1] = 180; scene.Bgra[offset + 2] = 235;
        }
        byte[] covered = Camera(scene);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double divisor = Matrix[6] * x + Matrix[7] * y + Matrix[8];
            double u = (Matrix[0] * x + Matrix[1] * y + Matrix[2]) / divisor;
            double v = (Matrix[3] * x + Matrix[4] * y + Matrix[5]) / divisor;
            // A hand covering the control also occupies the neighboring felt. A perfectly
            // uniform occluder exactly matching a unique control's footprint is inherently
            // ambiguous for a first-frame photometric fit; fixed palm searches cover that case.
            if (u * (scene.Width - 1) is >= 72 and <= 111 && v * (scene.Height - 1) is >= 72 and <= 110)
                Set(covered, x, y, 40, 65, 110);
        }
        Require(Feed(new(), covered, scene, 0).Hints.Count > 0,
            "A hand already covering a uniquely colored control supplied its own false empty-color reference.");
    }

    private static void CompactReferenceControlOcclusion()
    {
        const int size = 1000;
        PixelPoint[] polygon = [new(0, 0), new(size, 0), new(size, size), new(0, size)];
        HandTrackingBounds[] controls = [new(.072, .892, .116, .051), new(.812, .892, .116, .051)];
        var empty = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int pixel = (y * size + x) * 4;
            empty[pixel] = 12; empty[pixel + 1] = 5; empty[pixel + 2] = 3; empty[pixel + 3] = 255;
            if ((x is >= 60 and < 200 || x is >= 800 and < 940) && y is >= 880 and < 955)
            {
                double fraction = (y - 880) / 75.0;
                empty[pixel] = (byte)(94 - 55 * fraction);
                empty[pixel + 1] = (byte)(73 - 49 * fraction);
                empty[pixel + 2] = (byte)(49 - 36 * fraction);
            }
        }
        var scene = new HandAcquisitionSceneImage(size, size, empty,
            [1.0 / 999, 0, 0, 0, 1.0 / 999, 0, 0, 0, 1], controls, controls);
        // These compact controls are also their own appearance references. A
        // broad neutral/dark obstruction previously trained a false exposure
        // gradient between them and made even full coverage disappear.
        foreach (int target in new[] { 0, 1 })
        foreach (double coverage in new[] { .5, .75, 1.0 })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            Require(FeedLocal(tracker, empty, 0).Hints.Count == 0, "Compact controls appeared obstructed while empty.");
            byte[] occupied = (byte[])empty.Clone();
            int left = target == 0 ? 72 : 812;
            for (int y = 892; y < 943; y++)
            for (int x = left; x < left + (int)Math.Round(116 * coverage); x++)
            for (int channel = 0; channel < 3; channel++)
            {
                int pixel = (y * size + x) * 4 + channel;
                occupied[pixel] = (byte)Math.Max(0, occupied[pixel] - 40);
            }
            for (int frame = 1; frame <= 12; frame++)
            {
                var observed = FeedLocal(tracker, occupied, frame * 100);
                Require(observed.Hints.Count == 1 && observed.Hints[0].ControlCoverage >= .30 &&
                        Math.Abs(observed.Hints[0].Center.X - (left + 116 * coverage / 2)) < 12,
                    "An obstructed compact control poisoned its reference, lost stationary evidence, or acquired the other control.");
            }
            Require(FeedLocal(tracker, empty, 1300).Hints.Count == 0, "Removed compact-control obstruction left a presence ghost.");
        }
        var exposureTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(exposureTracker, empty, 0);
        int exposureFrame = 0;
        foreach (int change in new[] { 40, -15, 20 })
        {
            byte[] exposed = (byte[])empty.Clone();
            for (int pixel = 0; pixel < exposed.Length; pixel += 4)
            for (int channel = 0; channel < 3; channel++)
                exposed[pixel + channel] = (byte)Math.Clamp(exposed[pixel + channel] + change + (pixel % 7 - 3), 0, 255);
            var observed = FeedLocal(exposureTracker, exposed, ++exposureFrame * 100);
            Require(observed.BaselineReady && observed.Reason == "rendered-scene-foreground" && observed.Hints.Count == 0,
                "Global exposure/noise on compact reference controls became foreground.");
        }
        byte[] fragment = (byte[])empty.Clone();
        for (int y = 908; y < 922; y++)
        for (int x = 111; x < 131; x++)
        for (int channel = 0; channel < 3; channel++) fragment[(y * size + x) * 4 + channel] = 0;
        var fragmentTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(fragmentTracker, empty, 0);
        Require(FeedLocal(fragmentTracker, fragment, 100).Hints.Count == 0,
            "A compact-control disturbance below the 7% area floor became an acquisition hint.");

        HandAcquisitionPresenceResult FeedLocal(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            return tracker.Update(size, size, size * 4, pixels, polygon, scene, now, now);
        }
    }

    private static void ControlTriggerRegions()
    {
        const int size = 1000;
        PixelPoint[] polygon = [new(0, 0), new(size, 0), new(size, size), new(0, size)];
        HandTrackingBounds[] controls = [new(.072, .892, .116, .051), new(.812, .892, .116, .051)];
        HandTrackingBounds[] triggers = [new(.105, .90, .05, .035), new(.845, .90, .05, .035)];
        var pixels = new byte[size * size * 4];
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            pixels[pixel] = 85; pixels[pixel + 1] = 65; pixels[pixel + 2] = 40; pixels[pixel + 3] = 255;
        }
        var scene = new HandAcquisitionSceneImage(size, size, pixels,
            [1.0 / 999, 0, 0, 0, 1.0 / 999, 0, 0, 0, 1], controls, controls, triggers);
        var empty = FeedLocal(new(), pixels, scene, 0);
        Require(empty.BaselineReady && empty.Hints.Count == 0,
            "A generated control with a trigger-region requirement acquired itself.");
        var tracker = new HandAcquisitionPresenceTracker();
        FeedLocal(tracker, pixels, scene, 0);
        byte[] text = Patch(pixels, 97, 896, 71, 45, 40, 35, 20);
        var obstructed = FeedLocal(tracker, text, scene, 100);
        Require(obstructed.Hints.Count == 1 && obstructed.Hints[0].ControlCoverage >= .07 &&
                obstructed.Hints[0].ControlTriggerCoverage >= .07,
            "A current text obstruction lost its paired trigger/control area measurements.");
        for (int frame = 2; frame <= 8; frame++)
            Require(FeedLocal(tracker, text, scene, frame * 100).Hints.Count == 1,
                "A stationary control-label obstruction stopped producing fresh evidence.");
        byte[] nonText = Patch(pixels, 75, 895, 25, 46, 40, 35, 20);
        Require(FeedLocal(tracker, nonText, scene, 900).Hints.Count == 0,
            "A substantial control disturbance outside its text trigger acquired a light.");
        byte[] small = Patch(pixels, 115, 905, 20, 14, 40, 35, 20);
        Require(FeedLocal(tracker, small, scene, 1000).Hints.Count == 0,
            "Text coverage bypassed the separate 7% whole-control requirement.");

        var withoutTrigger = scene with { BoardTriggerRegions = null };
        var genericTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(genericTracker, pixels, withoutTrigger, 0);
        var generic = FeedLocal(genericTracker, nonText, withoutTrigger, 100);
        Require(generic.Hints.Count == 1 && generic.Hints[0].ControlTriggerCoverage is null,
            "An ordinary board acquired a new text requirement or invented trigger coverage.");
        foreach (HandTrackingBounds[] invalid in new[]
        {
            new[] { triggers[0] },
            new[] { triggers[0] with { Width = double.NaN }, triggers[1] },
            new[] { triggers[0] with { X = .01 }, triggers[1] },
            new[] { triggers[0] with { Y = .99 }, triggers[1] }
        })
        {
            var rejected = FeedLocal(new(), text, scene with { BoardTriggerRegions = invalid }, 0);
            Require(rejected.Reason == "invalid-rendered-scene" && !rejected.BaselineReady && rejected.Hints.Count == 0,
                "Invalid or unpaired control trigger geometry silently acquired a hand.");
        }
        var hint = obstructed.Hints[0] with { Center = new(132, 918), RadiusPixels = 75,
            ControlCoverage = .99, ControlTriggerCoverage = .99 };
        byte[] lit = (byte[])pixels.Clone();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            if (Math.Pow(x - hint.Center.X, 2) + Math.Pow(y - hint.Center.Y, 2) < hint.RadiusPixels * hint.RadiusPixels)
                for (int channel = 0; channel < 3; channel++) lit[(y * size + x) * 4 + channel] = 220;
        var litTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(litTracker, pixels, scene, 0);
        var litText = FeedLocal(litTracker, Patch(lit, 103, 900, 55, 36, 55, 75, 95), scene, 350, hint, 100);
        Require(litText.IlluminatedPresence == true && litText.Hints.Count == 1 &&
                litText.Hints[0].ControlCoverage is >= .07 and < .99 &&
                litText.Hints[0].ControlTriggerCoverage is >= .07 and < .99 &&
                litText.Hints[0].ObservedAt == Epoch.AddMilliseconds(350),
            "An illuminated text obstruction reused old trigger evidence or lost fresh paired coverage.");
        var litNonText = FeedLocal(litTracker, Patch(lit, 75, 895, 25, 46, 55, 75, 95), scene, 500, hint, 100);
        Require(litNonText.IlluminatedPresence == false && litNonText.Hints.Count == 0,
            "Nontext foreground under a search light inherited a previous text obstruction.");
        var litSmall = FeedLocal(litTracker, Patch(lit, 115, 905, 20, 14, 55, 75, 95), scene, 600, hint, 100);
        Require(litSmall.IlluminatedPresence == false && litSmall.Hints.Count == 0,
            "Illuminated text interference bypassed the 7% whole-control requirement.");
        var wrongRegion = FeedLocal(litTracker, Patch(lit, 103, 900, 55, 36, 55, 75, 95), scene,
            700, hint with { Center = new(870, 918) }, 100);
        Require(wrongRegion.IlluminatedPresence != true && wrongRegion.Hints.Count == 0,
            "A search light renewed from text obstruction on a different control.");
        var removed = FeedLocal(litTracker, lit, scene, 800, hint, 100);
        Require(removed.IlluminatedPresence == false && removed.Hints.Count == 0,
            "An empty search light renewed itself using stored trigger coverage.");

        HandAcquisitionPresenceResult FeedLocal(HandAcquisitionPresenceTracker detector, byte[] current,
            HandAcquisitionSceneImage expected, int milliseconds, HandAcquisitionHint? light = null, int? started = null)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            return detector.Update(size, size, size * 4, current, polygon, expected, now, now, light,
                started is null ? null : Epoch.AddMilliseconds(started.Value));
        }
        static byte[] Patch(byte[] source, int left, int top, int width, int height, byte blue, byte green, byte red)
        {
            byte[] result = (byte[])source.Clone();
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                int pixel = (y * size + x) * 4;
                result[pixel] = blue; result[pixel + 1] = green; result[pixel + 2] = red;
            }
            return result;
        }
    }

    private static void ControlReferenceMetadata(byte[] empty, PixelPoint[] polygon,
        HandAcquisitionSceneImage scene)
    {
        // Reuse the opaque, generated-caption fixture and its synthetic camera
        // projection. A malformed plate map must never become a learned camera
        // baseline, retain old text evidence, or poison a later valid scene.
        HandTrackingBounds[] plates = [new(.06, .88, .14, .075), new(.80, .88, .14, .075)];
        var valid = scene with { BoardControlReferenceRegions = plates };
        var tracker = new HandAcquisitionPresenceTracker();
        int milliseconds = 0;
        AssertReady(Feed(valid), "initial explicit plate metadata");
        foreach (var (name, invalidPlates) in new (string, HandTrackingBounds[])[]
        {
            ("count mismatch", [plates[0]]),
            ("nonfinite rectangle", [plates[0] with { Width = double.NaN }, plates[1]]),
            ("out-of-board rectangle", [plates[0] with { X = .95 }, plates[1]]),
            ("uncontained candidate", [plates[0] with { X = .09, Width = .11 }, plates[1]]),
            ("swapped control pairing", [plates[1], plates[0]])
        })
        {
            var invalid = valid with { BoardControlReferenceRegions = invalidPlates };
            AssertRejected(Feed(invalid), name);
            AssertRejected(Feed(invalid), name + " repeated");
            AssertReady(Feed(valid), name + " explicit metadata recovery");
            AssertRejected(Feed(invalid), name + " after recovery");
            AssertReady(Feed(scene), name + " legacy null metadata recovery");
        }
        // Older callers may omit both reference lists. Their candidate pixels
        // remain the appearance witnesses after an explicitly mapped scene.
        AssertReady(Feed(valid), "explicit metadata before legacy reference reset");
        AssertReady(Feed(scene with { BoardReferenceRegions = null }),
            "legacy null reference-region recovery");
        Console.WriteLine("Control reference metadata regression: five malformed maps fail closed; " +
            "explicit and legacy null metadata recover on the same tracker.");

        HandAcquisitionPresenceResult Feed(HandAcquisitionSceneImage expected)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            milliseconds += 100;
            return tracker.Update(scene.Width, scene.Height, scene.Width * 4, empty, polygon,
                expected, now, now);
        }
        static void AssertRejected(HandAcquisitionPresenceResult result, string description) =>
            Require(!result.BaselineReady && result.Hints.Count == 0 && result.TextPatterns is null &&
                result.Reason == "invalid-rendered-scene",
                $"Invalid control reference metadata did not fail closed ({description}).");
        static void AssertReady(HandAcquisitionPresenceResult result, string description) =>
            Require(result.BaselineReady && result.Hints.Count == 0 && result.TextPatterns is { Count: 2 } &&
                result.TextPatterns.All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted &&
                    pattern.ConfirmationFrames == 0),
                $"Clean generated captions did not recover after plate metadata changed ({description}).");
    }

    private static void RenderedCompactControls()
    {
        const int size = 1000;
        // Actual Win2D-generated compact controls and a native projection of
        // them. The occupied fixture adds four synthetic strips over Exit;
        // these contain no user camera pixels. A free spatial fit previously
        // explained away Exit and confidently acquired untouched Save instead.
        byte[] expected = ReadPixels("paint-compact-controls-expected.png");
        byte[] empty = ReadPixels("paint-compact-controls-empty.png");
        byte[] occupied = ReadPixels("paint-compact-controls-occupied.png");
        PixelPoint[] polygon = [new(44.253500513732384, 44.253500513732384),
            new(955.746477134526, 44.253500513732384), new(955.746477134526, 955.746477134526),
            new(44.253500513732384, 955.746477134526)];
        HandTrackingBounds[] controls = [new(.072, .892, .11600000000000002, .051),
            new(.812, .892, .11600000000000002, .051)];
        HandTrackingBounds[] text = [new(.107, .902, .048, .027), new(.840, .903, .060, .026)];
        var scene = new HandAcquisitionSceneImage(size, size, expected,
            [.0010861301462467192, 0, -.04306506098490942, 0, .0010861301462467192,
                -.04306506098490942, 0, 0, 1], controls, controls, text);
        ControlReferenceMetadata(empty, polygon, scene);
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm)
                Require(FeedLocal(tracker, empty, 0).Hints.Count == 0,
                    "The actual rendered compact controls acquired an empty-table light.");
            var pending = FeedLocal(tracker, occupied, 100);
            Require(pending.Hints.Count == 0 && pending.TextPatterns!.Any(pattern =>
                pattern.ControlRegion == 0 && pattern.ShapeCorrupted && pattern.ConfirmationFrames == 1),
                "A single generated-label corruption frame started an acquisition light.");
            var observed = FeedLocal(tracker, occupied, 200);
            Require(observed.Hints.Count == 1 && Math.Abs(observed.Hints[0].Center.X - 159.341) < 10 &&
                    Math.Abs(observed.Hints[0].Center.Y - 884.392) < 10 &&
                    observed.Hints[0].ControlCoverage >= .07 && observed.Hints[0].ControlTriggerCoverage >= .07,
                "The native rendered Exit fixture disappeared into a spatial fit or acquired untouched Save.");
            var stationary = FeedLocal(tracker, occupied, 300);
            var removed = FeedLocal(tracker, empty, 400);
            Require(stationary.Hints.Count == 1 && removed.Hints.Count == 0,
                "Native stationary text obstruction was absorbed or left an empty-table ghost: " +
                System.Text.Json.JsonSerializer.Serialize(new { stationary, removed }));
        }
        // Preserve actual generated glyph geometry under camera color/exposure,
        // optical blur and registration changes. Both bright and dark ink must
        // be recognizable; broad panel color residuals cannot acquire a light.
        foreach (bool darkInk in new[] { false, true })
        {
            byte[] rendered = darkInk ? Invert(expected) : expected;
            byte[] clean = darkInk ? Invert(empty) : empty;
            byte[] obstructed = darkInk ? Invert(occupied) : occupied;
            var textScene = scene with { Bgra = rendered };
            var patterns = new HandAcquisitionTextPatterns(textScene);
            var cleanPatterns = patterns.Observe(size, size, size * 4, clean, null);
            Require(cleanPatterns.Count == 2 && cleanPatterns.All(pattern => pattern.Clean),
                "Known generated compact labels did not establish clean bright/dark glyph shapes.");
            foreach (byte[] changedAppearance in new[] { Appearance(clean), Blurred(clean) })
            {
                var shapes = patterns.Observe(size, size, size * 4, changedAppearance, null);
                Require(shapes.Count == 2 && shapes.All(pattern => pattern.Clean),
                    "Exposure, palette, noise or optical blur was mistaken for damaged generated letters.");
                var tracker = new HandAcquisitionPresenceTracker();
                Require(tracker.Update(size, size, size * 4, changedAppearance, polygon, textScene,
                    Epoch, Epoch).Hints.Count == 0,
                    "An intact generated caption acquired a spotlight after a camera-appearance change.");
            }
            var occupiedPatterns = patterns.Observe(size, size, size * 4, obstructed, null);
            Require(occupiedPatterns.Single(pattern => pattern.Region == 0).Clean == false &&
                occupiedPatterns.Single(pattern => pattern.Region == 1).Clean,
                "Four strips over the generated Exit caption did not damage its letter structure independently of untouched Save.");
            var lightTracker = new HandAcquisitionPresenceTracker();
            FeedLocal(lightTracker, occupied, 400);
            var occupiedHint = FeedLocal(lightTracker, occupied, 500).Hints.Single();
            var underLight = patterns.Observe(size, size, size * 4, obstructed, occupiedHint);
            Require(underLight.Single(pattern => pattern.Region == 0) is { Clean: false, StrongCorruption: false, Correlation: 0 } unknown &&
                unknown.ChangedBoardPixels.Count == 0,
                "The intentional white acquisition light was compared against its replaced unlit label.");
        }
        // Phone auto-exposure clips bright strokes after their optical blur.
        // Compare cold clipping and normal-clean -> clipped-clean transitions;
        // a bounded exposure adaptation must not absorb a later real occlusion.
        foreach (bool warm in new[] { false, true })
        foreach (bool darkInk in new[] { false, true })
        {
            byte[] generated = darkInk ? Invert(expected) : expected;
            byte[] optical = Blurred(generated);
            byte[] clipped = Clipped(generated);
            var direct = scene with { Bgra = generated,
                CameraToBoard = [1.0 / 999, 0, 0, 0, 1.0 / 999, 0, 0, 0, 1] };
            PixelPoint[] directPolygon = [new(0,0),new(size,0),new(size,size),new(0,size)];
            var detector = new HandAcquisitionPresenceTracker();
            if (warm) Require(FeedDirect(optical,0).Hints.Count == 0,
                "Normal generated lettering acquired a light before the clipping transition.");
            for (int frame = 1; frame <= 3; frame++)
            {
                var intact = FeedDirect(clipped,frame*100);
                Require(intact.ProjectedWhiteClipped == true,
                    $"Intact clipped lettering did not report clipped projected white (warm={warm}, darkInk={darkInk})");
                Require(intact.Hints.Count == 0 && intact.TextPatterns!.All(pattern => !pattern.ShapeCorrupted),
                    $"Optically blurred, exposure-clipped intact lettering became hand evidence (warm={warm}, darkInk={darkInk}): " +
                    System.Text.Json.JsonSerializer.Serialize(intact));
            }
            byte[] erased = (byte[])clipped.Clone();
            for (int y = 896; y <= 939; y++)
            for (int x = 109; x <= 155; x++)
            {
                int pixel = (y*size+x)*4;
                erased[pixel] = 95; erased[pixel+1] = 115; erased[pixel+2] = 165;
            }
            Require(FeedDirect(erased,400).Hints.Count == 0 && FeedDirect(erased,500).Hints.Count == 1,
                "Exposure adaptation absorbed a stationary obstruction of the clipped caption.");
            Require(FeedDirect(clipped,600).Hints.Count == 0,
                "Removing an obstruction from clipped lettering left a presence ghost.");
            // An inference or camera gap invalidates temporal evidence, not
            // the already verified projector/phone optical registration. Keep
            // both facts observable: empty lettering remains clean afterward,
            // and a later hand still needs two new nearby camera observations.
            var gapClean = FeedDirect(clipped,1100);
            Require(gapClean.Hints.Count == 0 && gapClean.TextPatterns!.All(pattern => !pattern.ShapeCorrupted),
                "A long camera interval discarded usable clipped-letter optics.");
            var gapPending = FeedDirect(erased,1500);
            Require(gapPending.Hints.Count == 0 && gapPending.TextPatterns!.Single(pattern =>
                pattern.ControlRegion == 0).ConfirmationFrames == 1,
                "A later caption obstruction borrowed confirmation from before a camera gap.");
            Require(FeedDirect(erased,1600).Hints.Count == 1 && FeedDirect(clipped,1700).Hints.Count == 0,
                "Retained optical registration could not confirm a fresh stationary hand after a camera gap.");
            // The same letters exposed below clipping must not dim a search light.
            byte[] unclipped = optical.Select((value, index) => index % 4 == 3 ? value : (byte)(value * .8)).ToArray();
            var exposed = new HandAcquisitionPresenceTracker().Update(size,size,size*4,unclipped,directPolygon,direct,
                Epoch.AddMilliseconds(2000),Epoch.AddMilliseconds(2000));
            Require(exposed.ProjectedWhiteClipped == false,
                $"Lettering below clipping reported clipped projected white (warm={warm}, darkInk={darkInk}): " +
                System.Text.Json.JsonSerializer.Serialize(exposed.TextPatterns));
            HandAcquisitionPresenceResult FeedDirect(byte[] pixels,int milliseconds)
            {
                var at = Epoch.AddMilliseconds(milliseconds);
                return detector.Update(size,size,size*4,pixels,directPolygon,direct,at,at);
            }
        }
        // Auto-exposure often moves a little on every frame. No single step
        // needs to exceed the registration-change threshold before its cached
        // exposure becomes obsolete. Keep every intact intermediate label
        // quiet, then retain real stationary obstruction acquisition.
        foreach (bool darkInk in new[] { false,true })
        {
            byte[] generated = darkInk ? Invert(expected) : expected;
            var gradualScene = scene with { Bgra = generated,
                CameraToBoard = [1.0 / 999,0,0,0,1.0 / 999,0,0,0,1] };
            PixelPoint[] gradualPolygon = [new(0,0),new(size,0),new(size,size),new(0,size)];
            var gradual = new HandAcquisitionPresenceTracker();
            byte[] final = generated;
            for (int step = 0; step <= 32; step++)
            {
                final = Clipped(generated,4+step*.25,120);
                var time = Epoch.AddMilliseconds(step*100);
                var observed = gradual.Update(size,size,size*4,final,gradualPolygon,gradualScene,time,time);
                Require(observed.Hints.Count == 0 && observed.TextPatterns!.All(pattern => !pattern.ShapeCorrupted),
                    $"Gradual exposure drift became damaged text (darkInk={darkInk}, step={step}): " +
                    System.Text.Json.JsonSerializer.Serialize(observed));
            }
            byte[] occupiedAfterDrift = (byte[])final.Clone();
            for (int y = 896; y <= 939; y++)
            for (int x = 109; x <= 155; x++)
            {
                int pixel = (y*size+x)*4;
                int texture = (x*13+y*7)%23;
                occupiedAfterDrift[pixel] = (byte)(85+texture);
                occupiedAfterDrift[pixel+1] = (byte)(105+texture);
                occupiedAfterDrift[pixel+2] = (byte)(155+texture);
            }
            var firstTime = Epoch.AddMilliseconds(3300);
            var nextTime = Epoch.AddMilliseconds(3400);
            var pending = gradual.Update(size,size,size*4,occupiedAfterDrift,gradualPolygon,gradualScene,firstTime,firstTime);
            var confirmed = gradual.Update(size,size,size*4,occupiedAfterDrift,gradualPolygon,gradualScene,nextTime,nextTime);
            Require(pending.Hints.Count == 0 && confirmed.TextPatterns!.Single(pattern => pattern.ControlRegion == 0).ShapeCorrupted &&
                confirmed.Hints.All(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07),
                $"Gradual exposure compensation absorbed missing letters or bypassed their measured area floors (darkInk={darkInk}): " +
                System.Text.Json.JsonSerializer.Serialize(new { pending, confirmed }));
            // Return to usable exposure and verify acquisition after the entire
            // drift history. Saturation may erase too much projected contrast
            // to measure 7% of a control; a shape score cannot invent that area.
            final = Clipped(generated);
            var recovered = Epoch.AddMilliseconds(3500);
            Require(gradual.Update(size,size,size*4,final,gradualPolygon,gradualScene,recovered,recovered).Hints.Count == 0,
                "Exposure recovery left a false caption obstruction.");
            for (int y = 896; y <= 939; y++)
            for (int x = 109; x <= 155; x++)
            {
                int pixel = (y*size+x)*4;
                final[pixel] = 95; final[pixel+1] = 115; final[pixel+2] = 165;
            }
            var recoveringHandTime = Epoch.AddMilliseconds(3600);
            var confirmedHandTime = Epoch.AddMilliseconds(3700);
            Require(gradual.Update(size,size,size*4,final,gradualPolygon,gradualScene,recoveringHandTime,recoveringHandTime).Hints.Count == 0 &&
                gradual.Update(size,size,size*4,final,gradualPolygon,gradualScene,confirmedHandTime,confirmedHandTime).Hints.Count == 1,
                "A later stationary hand could not be acquired after gradual exposure drift and recovery.");
        }
        var duplicateTracker = new HandAcquisitionPresenceTracker();
        Require(FeedLocal(duplicateTracker, occupied, 0).Hints.Count == 0 &&
            FeedLocal(duplicateTracker, occupied, 0).Hints.Count == 0 &&
            FeedLocal(duplicateTracker, occupied, 100).Hints.Count == 1,
            "A duplicate camera timestamp confirmed corruption or erased consecutive distinct camera evidence.");
        var gapTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(gapTracker, occupied, 0);
        Require(FeedLocal(gapTracker, occupied, 500).Hints.Count == 0 &&
            FeedLocal(gapTracker, occupied, 600).Hints.Count == 1,
            "Separated one-off glyph disturbances combined across the confirmation lifetime.");
        var staleTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(staleTracker, occupied, 0);
        Require(staleTracker.Update(size, size, size * 4, occupied, polygon, scene,
            Epoch.AddMilliseconds(100), Epoch.AddMilliseconds(450)).Hints.Count == 0 &&
            FeedLocal(staleTracker, occupied, 200).Hints.Count == 0 &&
            FeedLocal(staleTracker, occupied, 300).Hints.Count == 1,
            "Rejected stale evidence preserved a prior pending glyph confirmation.");
        var backwardTracker = new HandAcquisitionPresenceTracker();
        FeedLocal(backwardTracker, occupied, 100);
        Require(FeedLocal(backwardTracker, occupied, 0).Hints.Count == 0 &&
            FeedLocal(backwardTracker, occupied, 200).Hints.Count == 0 &&
            FeedLocal(backwardTracker, occupied, 300).Hints.Count == 1,
            "Rejected backwards evidence preserved a prior pending glyph confirmation.");
        // A matte object can erase the generated caption without supplying any
        // local texture. Gain=0 must not explain missing glyphs away, while a
        // camera-wide exposure blackout must not be called an object.
        byte[] flat = (byte[])empty.Clone();
        int flatColor = (901 * size + 116) * 4;
        byte flatBlue = empty[flatColor], flatGreen = empty[flatColor + 1], flatRed = empty[flatColor + 2];
        for (int y = 858; y <= 910; y++)
        for (int x = 104; x <= 215; x++)
        {
            int pixel = (y * size + x) * 4;
            flat[pixel] = flatBlue; flat[pixel + 1] = flatGreen; flat[pixel + 2] = flatRed;
        }
        var flatTracker = new HandAcquisitionPresenceTracker();
        Require(FeedLocal(flatTracker, flat, 0).Hints.Count == 0 &&
            FeedLocal(flatTracker, flat, 100).Hints.Count == 1,
            "A stationary complete flat caption obstruction was explained away by zero optical gain.");
        byte[] blackout = new byte[empty.Length];
        for (int index = 3; index < blackout.Length; index += 4) blackout[index] = 255;
        var blackoutTracker = new HandAcquisitionPresenceTracker();
        Require(FeedLocal(blackoutTracker, blackout, 0).Hints.Count == 0 &&
            FeedLocal(blackoutTracker, blackout, 100).Hints.Count == 0,
            "Camera-wide exposure blackout fabricated missing-caption acquisition evidence.");
        HandAcquisitionPresenceResult FeedLocal(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            return tracker.Update(size, size, size * 4, pixels, polygon, scene, now, now);
        }
        static byte[] ReadPixels(string filename)
        {
            using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", filename), ImreadModes.Unchanged);
            Require(image.Width == size && image.Height == size && image.Channels() == 4 && image.IsContinuous(),
                "The native compact-control fixture dimensions or BGRA format changed.");
            byte[] pixels = new byte[size * size * 4];
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            return pixels;
        }
        static byte[] Invert(byte[] source)
        {
            byte[] result = (byte[])source.Clone();
            for (int index = 0; index < result.Length; index++)
                if (index % 4 != 3) result[index] = (byte)(255 - result[index]);
            return result;
        }
        static byte[] Appearance(byte[] source)
        {
            byte[] result = (byte[])source.Clone();
            for (int index = 0; index < result.Length; index++)
            {
                int channel = index % 4;
                if (channel == 3) continue;
                double gain = channel == 0 ? .7 : channel == 1 ? .92 : .85;
                double offset = channel == 0 ? 30 : channel == 1 ? 17 : 23;
                int noise = (index * 13 % 7) - 3;
                result[index] = (byte)Math.Clamp((int)(result[index] * gain + offset + noise), 0, 255);
            }
            return result;
        }
        static byte[] Blurred(byte[] source)
        {
            using var pixels = new Mat(size, size, MatType.CV_8UC4);
            Marshal.Copy(source, 0, pixels.Data, source.Length);
            using var blurred = new Mat();
            Cv2.GaussianBlur(pixels, blurred, new Size(0, 0), .8);
            byte[] result = new byte[source.Length];
            Marshal.Copy(blurred.Data, result, 0, result.Length);
            return result;
        }
        byte[] Clipped(byte[] source,double exposureGain = 6,double exposureBackground = 160)
        {
            byte[] blurred = Blurred(source);
            byte[] result = (byte[])source.Clone();
            foreach (var label in text)
            {
                int left = (int)(label.X*size)-10, top = (int)(label.Y*size)-10;
                int right = (int)((label.X+label.Width)*size)+10;
                int bottom = (int)((label.Y+label.Height)*size)+10;
                var backgroundSamples = new List<double>();
                for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++) backgroundSamples.Add(Luminance(source,(y*size+x)*4));
                double background = backgroundSamples.Order().ElementAt(backgroundSamples.Count/10);
                for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int pixel = (y*size+x)*4;
                    byte value = (byte)Math.Clamp(exposureBackground+exposureGain*(Luminance(blurred,pixel)-background),0,255);
                    result[pixel] = result[pixel+1] = result[pixel+2] = value;
                }
            }
            return result;
            static double Luminance(byte[] pixels,int index) =>
                pixels[index]*.114+pixels[index+1]*.587+pixels[index+2]*.299;
        }
    }

    private static void RenderedLocalCaptionLoss()
    {
        const int size = 1000;
        byte[] expected = Read("menu-local-caption-expected.png");
        byte[] empty = Read("menu-local-caption-empty.png");
        byte[] occupied = Read("menu-local-caption-occupied.png");
        PixelPoint[] polygon = [new(44.253500513732384, 44.253500513732384),
            new(955.746477134526, 44.253500513732384), new(955.746477134526, 955.746477134526),
            new(44.253500513732384, 955.746477134526)];
        HandTrackingBounds[] controls = [new(.092,.262,.376,.136),new(.532,.262,.376,.136),
            new(.092,.462,.376,.136),new(.532,.462,.376,.136),new(.092,.662,.376,.136),new(.532,.662,.376,.136)];
        HandTrackingBounds[] labels = [new(.110765625,.30884375,.215671875,.03921875),
            new(.550765625,.30884375,.1754375,.03921875),new(.110765625,.50884375,.138453125,.03921875),
            new(.550765625,.509046875,.077546875,.031859375),new(.109328125,.70975,.06478125,.03115625),
            new(.550765625,.70884375,.098671875,.0320625)];
        var scene = new HandAcquisitionSceneImage(size,size,expected,
            [.0010861301462467192,0,-.04306506098490942,0,.0010861301462467192,-.04306506098490942,0,0,1],
            controls,null,labels);
        foreach (bool warm in new[] { false,true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Require(Feed(empty,0).Hints.Count == 0,"Empty generated Menu captions acquired a light.");
            var pending = Feed(occupied,100);
            var confirmed = Feed(occupied,200);
            Require(pending.Hints.Count == 0 && confirmed.Hints.Count == 1 &&
                confirmed.Hints[0].ControlCoverage >= .07 && confirmed.Hints[0].ControlTriggerCoverage >= .07,
                "Localized missing letters were hidden by their intact whole-word shape.");
            var damaged = confirmed.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
            Require(damaged.ShapeCorrupted && damaged.LocalDamageCoverage >= .07 &&
                damaged.SectorCorrelations!.Count(correlation => correlation < .4) >= 2 &&
                confirmed.TextPatterns!.Where(pattern => pattern.ControlRegion != 0).All(pattern => pattern.LabelIntact),
                "Localized loss invented damaged letter area or acquired an untouched Menu control.");
            Require(Feed(empty,300).Hints.Count == 0,"Removed local letter obstruction left a presence ghost.");
            HandAcquisitionPresenceResult Feed(byte[] pixels,int milliseconds)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                return tracker.Update(size,size,size*4,pixels,polygon,scene,time,time);
            }
        }
        static byte[] Read(string name)
        {
            using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,"Fixtures",name),ImreadModes.Unchanged);
            Require(image.Width == size && image.Height == size && image.Channels() == 4 && image.IsContinuous(),
                "The generated local-caption fixture dimensions or BGRA format changed.");
            byte[] pixels = new byte[size*size*4];
            Marshal.Copy(image.Data,pixels,0,pixels.Length);
            return pixels;
        }
    }

    private static void BaselineFallbackAndBarriers()
    {
        var scene = Scene();
        byte[] empty = Camera(scene), hand = Camera(scene);
        Rectangle(hand, 265, 205, 65, 95, 60, 85, 150);
        var tracker = new HandAcquisitionPresenceTracker();
        Require(Feed(tracker, empty, null, 0).Hints.Count == 0, "Fallback camera initialization invented an object.");
        Require(Feed(tracker, hand, null, 100).Hints.Count == 1, "Foreground failed against a previously empty camera baseline.");
        Require(Feed(tracker, hand, null, 5000).Hints.Count == 1, "The fallback baseline absorbed a motionless hand.");
        Require(Feed(tracker, empty, null, 5100).Hints.Count == 0, "Fallback removal left an acquisition ghost.");
        Require(Feed(tracker, hand, scene, 5050).Hints.Count == 0, "An out-of-order camera frame supplied presence evidence.");
        Require(Feed(tracker, hand, scene, 5200, now: 5600).Hints.Count == 0, "Stale camera frames supplied presence evidence.");
        Require(Feed(tracker, hand, scene, 5800, now: 5700).Hints.Count == 0, "Future camera frames supplied presence evidence.");
        tracker.Reset();
        Require(Feed(tracker, hand, scene, 0).Hints.Count == 1,
            "Reset lost known-render acquisition for an already present hand.");
        tracker.Reset();
        Require(Feed(tracker, hand, null, 0).Hints.Count == 0,
            "A camera-only baseline claimed it could identify what was already present at startup.");

        var invalid = scene with { CameraToBoard = new double[9] };
        Require(Feed(new(), empty, invalid, 0).Hints.Count == 0, "An invalid projective mapping generated foreground.");
        var shiftedPolygon = Polygon.Select(point => new PixelPoint(point.X + 5, point.Y)).ToArray();
        Require(tracker.Update(Width, Height, Width * 4, hand, shiftedPolygon, null,
            Epoch.AddMilliseconds(100), Epoch.AddMilliseconds(100)).Hints.Count == 0,
            "A changed calibration reused an unrelated fallback baseline.");
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage? scene, int milliseconds, HandAcquisitionHint? light = null,
        int? lightStarted = null, int? now = null) => tracker.Update(Width, Height, Width * 4, pixels, Polygon,
            scene, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(now ?? milliseconds), light,
            lightStarted is null ? null : Epoch.AddMilliseconds(lightStarted.Value));

    private static HandAcquisitionSceneImage Scene()
    {
        const int width = 192, height = 128;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(25 + x * .12);
            pixels[offset + 1] = (byte)(70 + x * .16 + y * .09);
            pixels[offset + 2] = (byte)(20 + y * .18);
            if (y is > 82 and < 113 && x % 42 is > 4 and < 38)
            {
                int button = x / 42;
                pixels[offset] = (byte)(20 + button * 14);
                pixels[offset + 1] = (byte)(25 + button * 17);
                pixels[offset + 2] = (byte)(35 + button * 24);
                if (y is 91 or 92 && x % 7 < 4)
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 225;
            }
            pixels[offset + 3] = 255;
        }
        return new(width, height, pixels, Matrix);
    }

    private static byte[] Camera(HandAcquisitionSceneImage scene, double brightness = 0, double gain = 1,
        int noise = 0, int shiftX = 0, int shiftY = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double px = x + shiftX, py = y + shiftY;
            double divisor = Matrix[6] * px + Matrix[7] * py + Matrix[8];
            double u = Math.Clamp((Matrix[0] * px + Matrix[1] * py + Matrix[2]) / divisor, 0, 1);
            double v = Math.Clamp((Matrix[3] * px + Matrix[4] * py + Matrix[5]) / divisor, 0, 1);
            double tx = u * (scene.Width - 1), ty = v * (scene.Height - 1);
            int ix = (int)tx, iy = (int)ty, rx = Math.Min(scene.Width - 1, ix + 1), by = Math.Min(scene.Height - 1, iy + 1);
            double fx = tx - ix, fy = ty - iy;
            for (int channel = 0; channel < 3; channel++)
            {
                double expected = scene.Bgra[(iy * scene.Width + ix) * 4 + channel] * (1 - fx) * (1 - fy) +
                    scene.Bgra[(iy * scene.Width + rx) * 4 + channel] * fx * (1 - fy) +
                    scene.Bgra[(by * scene.Width + ix) * 4 + channel] * (1 - fx) * fy +
                    scene.Bgra[(by * scene.Width + rx) * 4 + channel] * fx * fy;
                double actual = 12 + channel * 6 + (.65 + channel * .08) * expected + .0008 * expected * expected +
                    x * 12.0 / Width + y * 8.0 / Height;
                actual = actual * gain + brightness + (noise == 0 ? 0 : (x * 17 + y * 31) % (noise * 2 + 1) - noise);
                pixels[(y * Width + x) * 4 + channel] = (byte)Math.Clamp(Math.Round(actual), 0, 255);
            }
            pixels[(y * Width + x) * 4 + 3] = 255;
        }
        return pixels;
    }

    private static void Rectangle(byte[] pixels, int x, int y, int width, int height, byte b, byte g, byte r)
    {
        for (int py = y; py < y + height; py++)
        for (int px = x; px < x + width; px++) Set(pixels, px, py, b, g, r);
    }
    private static void Circle(byte[] pixels, PixelPoint center, double radius, byte b, byte g, byte r)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            if (Math.Pow(x - center.X, 2) + Math.Pow(y - center.Y, 2) <= radius * radius) Set(pixels, x, y, b, g, r);
    }
    private static void Set(byte[] pixels, int x, int y, byte b, byte g, byte r)
    {
        int offset = (y * Width + x) * 4;
        pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r;
    }
    private static void CheckCrop(HandAcquisitionHint hint) => Require(hint.SearchBounds.Width == hint.SearchBounds.Height &&
        hint.SearchBounds.X >= 0 && hint.SearchBounds.Y >= 0 && hint.SearchBounds.X + hint.SearchBounds.Width <= Width &&
        hint.SearchBounds.Y + hint.SearchBounds.Height <= Height, "A presence crop stretched or exceeded the native camera.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
