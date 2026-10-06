using ProjectTabletop.Vision;

internal static class PaintDisturbanceRegression
{
    private const int Width = 640, Height = 360, RenderSize = 256;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly double[] Mapping = [1.0 / 480, 0, -80.0 / 480, 0, 1.0 / 300, -.10, 0, 0, 1];
    private static readonly HandTrackingBounds PaintBounds = new(.015, .18, .97, .805);

    public static void Run()
    {
        StationaryObstructionAndCadence();
        MeasuredNoiseFloorAndExcludedControls();
        AnimatedProjectionAndDelay();
        NonlinearProjectionAndGroupedFingers();
        NonlinearChangingPigmentAndDelay();
        CurvedAmbientFieldAndDrySupport();
        ShiftedCurvedAmbientFields();
        AspectPerspectiveAndStride();
        TimeRevisionAndReferenceBarriers();
        Console.WriteLine("Paint disturbance regression: stationary four-finger and generic-object input, " +
            "measured area cutoff, ignored controls, photometric/noise rejection, delayed rendered-frame matching, " +
            "nonlinear/clipped optical projection, curved illumination and dark grouped fingers, own-animation rejection, bounded drop cadence, " +
            "native aspect/perspective/stride, and stale/revision barriers passed.");
    }

    private static void StationaryObstructionAndCadence()
    {
        var tracker = new PaintDisturbanceTracker();
        byte[] expected = Render(0), empty = Camera(expected), fingers = Camera(expected, fingers: true);
        var first = Feed(tracker, fingers, expected, 1000);
        Require(first.ReferenceReady && first.CandidateCount == 1 && first.Drops.Count == 0 &&
                first.ConfirmedCandidateCount == 0,
            $"A first-frame stationary hand must be evidence, but wait for confirmation: {first}.");
        var confirmed = Feed(tracker, fingers, expected, 1125);
        Require(confirmed.ConfirmedCandidateCount == 1,
            "A twice-observed physical obstruction did not expose confirmed activity.");
        var drop = confirmed.Drops.Single();
        Require(Math.Abs(drop.BoardCenter.X - .492) < .02 && Math.Abs(drop.BoardCenter.Y - .59) < .02,
            "The four-finger obstruction was not mapped to its actual board location.");
        Require(drop.ForegroundBoardArea >= PaintDisturbanceTracker.MinimumBoardArea &&
            drop.ObservedAt == Epoch.AddMilliseconds(1125), "A paint drop lost its measured area or source timestamp.");
        foreach (int time in new[] { 1250, 1500, 1750, 2000 })
        {
            var held = Feed(tracker, fingers, expected, time);
            Require(held.Drops.Count == 0 && held.ConfirmedCandidateCount == 1,
                "A held hand emitted before the renderer's 900 ms same-position interval.");
        }
        Require(Feed(tracker, fingers, expected, 2025).Drops.Count == 1,
            "A steady hand did not emit again exactly 900 ms after its previous drop.");
        Require(Feed(tracker, empty, expected, 2150).Drops.Count == 0,
            "Removing a hand emitted paint at the vacated position.");
        Feed(tracker, empty, expected, 2650);
        Require(Feed(tracker, fingers, expected, 2775).Drops.Count == 0 &&
            Feed(tracker, fingers, expected, 2900).Drops.Count == 1,
            "A returning hand did not require fresh evidence and rearm.");

        tracker.Reset();
        byte[] box = Camera(expected, obstruction: new(.68, .71, .065, .055), objectColor: (18, 30, 36));
        Feed(tracker, box, expected, 1000);
        Require(Feed(tracker, box, expected, 1125).Drops.Count == 1,
            "Paint incorrectly required a recognized hand instead of a generic physical obstruction.");
        byte[] shiftedBox = Camera(expected, obstruction: new(.73, .71, .065, .055), objectColor: (18, 30, 36));
        Require(Feed(tracker, shiftedBox, expected, 1375).Drops.Count == 1,
            "A moving obstruction could not place the next drop at its new position.");
    }

    private static void MeasuredNoiseFloorAndExcludedControls()
    {
        byte[] expected = Render(0);
        Require(Math.Abs(PaintDisturbanceTracker.MinimumBoardArea - .00133812) < 1e-10,
            "Paint lost the physical-area equivalent of the measured seven-percent control cutoff.");
        foreach (var obstruction in new[] { new HandTrackingBounds(.47, .57, .012, .03),
            new HandTrackingBounds(.10, .04, .16, .09), new HandTrackingBounds(.001, .4, .008, .2) })
        {
            var tracker = new PaintDisturbanceTracker();
            byte[] image = Camera(expected, obstruction: obstruction, objectColor: (20, 30, 40));
            Require(Feed(tracker, image, expected, 1000).CandidateCount == 0 &&
                Feed(tracker, image, expected, 1125).Drops.Count == 0,
                "A sub-threshold disturbance, excluded Exit control, or board edge emitted paint.");
        }
        var noiseTracker = new PaintDisturbanceTracker();
        foreach (int time in new[] { 1000, 1125, 1250, 1375 })
        {
            byte[] noisy = Camera(expected, exposure: 1 + (time % 3) * .035, noise: 10);
            Require(Feed(noiseTracker, noisy, expected, time).Drops.Count == 0,
                "Camera noise or an exposure change became paint input.");
        }
        var ignoredTracker = new PaintDisturbanceTracker();
        byte[] ignoredObject = Camera(expected, obstruction: new(.66, .7, .1, .08), objectColor: (18, 30, 36));
        var scene = Scene(expected, 1000) with { IgnoredRegions = [new(.63, .67, .16, .14)] };
        Require(Update(ignoredTracker, ignoredObject, scene, 1000).CandidateCount == 0,
            "An explicitly ignored projected UI region leaked into paint candidates.");
    }

    private static void AnimatedProjectionAndDelay()
    {
        var tracker = new PaintDisturbanceTracker();
        var history = Enumerable.Range(0, 7).Select(index =>
            new PaintExpectedFrame(RenderSize, RenderSize, Render(index), Epoch.AddMilliseconds(125 * index))).ToArray();
        var scene = new PaintDisturbanceScene(1, Mapping, history, PaintBounds);
        byte[] delayed = Camera(history[3].Bgra);
        var result = Update(tracker, delayed, scene, 800);
        Require(result.ReferenceReady && result.Drops.Count == 0 && result.CandidateCount == 0,
            $"The app's own delayed spreading paint became input: {result}.");
        Require(result.EstimatedDelayMilliseconds is >= 300 and <= 550,
            $"The detector failed to choose the earlier matching projected frame: {result.EstimatedDelayMilliseconds} ms.");
        byte[] withFingers = Camera(history[3].Bgra, fingers: true);
        tracker.Reset();
        Update(tracker, withFingers, scene, 800);
        Require(Update(tracker, withFingers, scene, 900).Drops.Count == 1,
            "Historical animation matching suppressed a real stationary hand.");

        // A camera exposure that integrates two adjacent displayed frames is still
        // entirely expected, including color transitions inside metallic paint.
        byte[] blend = new byte[history[3].Bgra.Length];
        for (int index = 0; index < blend.Length; index++)
            blend[index] = (byte)((history[3].Bgra[index] + history[4].Bgra[index]) / 2);
        tracker.Reset();
        byte[] blendedCamera = Camera(blend);
        Require(Update(tracker, blendedCamera, scene, 800).CandidateCount == 0 &&
            Update(tracker, blendedCamera, scene, 900).Drops.Count == 0,
            "An exposure blending adjacent projected frames generated paint.");

        tracker.Reset();
        for (int time = 1000; time <= 1750; time += 125)
        {
            int step = (time - 1000) / 125;
            var frames = Enumerable.Range(Math.Max(0, step - 5), Math.Min(step + 1, 6))
                .Select(index => new PaintExpectedFrame(RenderSize, RenderSize, Render(index),
                    Epoch.AddMilliseconds(875 + index * 125))).ToArray();
            var movingScene = scene with { ExpectedHistory = frames };
            var observation = Update(tracker, Camera(Render(Math.Max(0, step - 1))), movingScene, time);
            Require(observation.Drops.Count == 0, "The app's animation entered a self-feeding paint loop.");
        }
    }

    private static void NonlinearProjectionAndGroupedFingers()
    {
        // The actual liquid surface has a nearly black underlay, saturated
        // pigment, and wet highlights. A white synthetic canvas with an affine
        // RGB transfer does not exercise the projector's light output added to
        // ambient light, the camera's tone curve, or clipped pigment channels.
        byte[] expected = RenderWetPaint();
        var failures = new List<string>();
        foreach (var optics in new[] {
            new PaintOptics("ambient/gamma", .95, .15, 2.0, 0, 0, .6),
            new PaintOptics("clipped/highlight", 1.65, .18, 2.0, 0, 0, .6),
            new PaintOptics("bright/clipped/highlight", 2.1, .22, 2.2, 0, 0, .8),
            new PaintOptics("clipped/registered/blurred", 1.65, .18, 2.0, 1.1, -.7, .8),
            new PaintOptics("ISP/colour-correction/clipping", 1.65, .18, 2.0, 1.1, -.7, .8, CorrectColors: true) })
        {
            byte[] empty = OpticalCamera(expected, optics), fingers = OpticalCamera(expected, optics, fingers: true);
            var tracker = new PaintDisturbanceTracker();
            for (int time = 1000; time <= 1375; time += 125)
            {
                var result = Feed(tracker, empty, expected, time);
                if (!result.ReferenceReady || result.CandidateCount != 0 || result.ConfirmedCandidateCount != 0 || result.Drops.Count != 0)
                    failures.Add($"{optics.Name}, no hand at {time} ms: {Describe(result)}");
            }
            tracker.Reset();
            var first = Feed(tracker, fingers, expected, 1500);
            var confirmed = Feed(tracker, fingers, expected, 1625);
            if (!first.ReferenceReady || first.Drops.Count != 0 || first.ConfirmedCandidateCount != 0 ||
                confirmed.Drops.Count != 1 || confirmed.ConfirmedCandidateCount != 1 ||
                confirmed.Drops.Any(drop => Math.Abs(drop.BoardCenter.X - .490) > .025 ||
                    Math.Abs(drop.BoardCenter.Y - .510) > .03 || drop.ForegroundBoardArea < PaintDisturbanceTracker.MinimumBoardArea))
                failures.Add($"{optics.Name}, grouped fingers: first {Describe(first)}; confirmed {Describe(confirmed)}");
            var removed = Feed(tracker, empty, expected, 1750);
            if (!removed.ReferenceReady || removed.CandidateCount != 0 || removed.ConfirmedCandidateCount != 0 || removed.Drops.Count != 0)
                failures.Add($"{optics.Name}, hand removed: {Describe(removed)}");
        }
        Require(failures.Count == 0, "Nonlinear Paint optics must reject the known projection and preserve physical input:\n" +
            string.Join("\n", failures));

        static string Describe(PaintDisturbanceResult result) =>
            $"ready={result.ReferenceReady}, candidates={result.CandidateCount}, confirmed={result.ConfirmedCandidateCount}, " +
            $"drops={result.Drops.Count}, area={result.ForegroundBoardArea:F6}, reason={result.Reason}" +
            (result.Drops.Count == 0 ? "" : ", centers=" + string.Join(";", result.Drops.Select(drop =>
                $"({drop.BoardCenter.X:F4},{drop.BoardCenter.Y:F4})/{drop.ForegroundBoardArea:F6}")));
    }

    private static void NonlinearChangingPigmentAndDelay()
    {
        // Cross-channel clipping and time selection must be exercised together.
        // The phone really sees a new blue surface; an older red coat cannot be
        // ranked with an easier optical model and then treated as the reference.
        var optics = new PaintOptics("changing pigment/ISP", 1.65, .18, 2.0, 1.1, -.7, .8, CorrectColors: true);
        byte[][] rendered = Enumerable.Range(0, 8).Select(ChangingCoat).ToArray();
        var failures = new List<string>();
        foreach (var sample in new[] {
            (Name: "latest blue", Index: 7, Blend: false, Fingers: false),
            (Name: "actual delayed pigment", Index: 3, Blend: false, Fingers: false),
            (Name: "adjacent exposure blend", Index: 6, Blend: true, Fingers: false),
            (Name: "held fingers over changing paint", Index: 7, Blend: false, Fingers: true) })
        {
            var tracker = new PaintDisturbanceTracker();
            byte[] camera = OpticalCamera(rendered[sample.Index], optics, sample.Fingers,
                sample.Blend ? rendered[sample.Index + 1] : null);
            for (int time = 1000; time <= 1125; time += 125)
            {
                var scene = new PaintDisturbanceScene(1, Mapping, rendered.Select((pixels, index) =>
                    new PaintExpectedFrame(RenderSize, RenderSize, pixels,
                        Epoch.AddMilliseconds(time - 950 + index * 125))).ToArray(), PaintBounds);
                var result = Update(tracker, camera, scene, time);
                bool physical = sample.Fingers && time == 1125;
                bool valid = result.ReferenceReady && result.Drops.Count == (physical ? 1 : 0) &&
                    result.ConfirmedCandidateCount == (physical ? 1 : 0) && result.CandidateCount == (sample.Fingers ? 1 : 0);
                if (physical)
                    valid &= result.Drops.All(drop => Math.Abs(drop.BoardCenter.X - .490) < .025 &&
                        Math.Abs(drop.BoardCenter.Y - .510) < .03 && drop.ForegroundBoardArea >= PaintDisturbanceTracker.MinimumBoardArea);
                if (sample.Name == "latest blue") valid &= result.EstimatedDelayMilliseconds is >= 0 and <= 200;
                // Selecting the next frame is legitimate when its immediate
                // predecessor supplies the exact old red reference. The first
                // five red images are identical, so no exact age is observable.
                if (sample.Name == "actual delayed pigment") valid &= result.EstimatedDelayMilliseconds is >= 325 and <= 1000;
                if (!valid)
                    failures.Add($"{sample.Name}, {time} ms: ready={result.ReferenceReady}, candidates={result.CandidateCount}, " +
                        $"confirmed={result.ConfirmedCandidateCount}, drops={result.Drops.Count}, area={result.ForegroundBoardArea:F6}, " +
                        $"delay={result.EstimatedDelayMilliseconds:F3}, reason={result.Reason}.");
            }
        }
        Require(failures.Count == 0, "A nonlinear changing-pigment history selected the wrong projection or hid physical input:\n" +
            string.Join("\n", failures));

        static byte[] ChangingCoat(int step)
        {
            byte[] pixels = RenderWetPaint();
            double[] red = [8, 60, 211], blue = [122, 14, 34];
            for (int y = 0; y < RenderSize; y++)
            for (int x = 0; x < RenderSize; x++)
            {
                double u = x / (double)(RenderSize - 1), v = y / (double)(RenderSize - 1);
                double distance = Math.Sqrt(Math.Pow(u - .49, 2) + Math.Pow(v - .51, 2));
                double coverage = Math.Clamp((.065 - distance) / .012, 0, 1);
                double newRadius = step switch { 5 => .018, 6 => .045, 7 => .062, _ => 0 };
                double blueCoverage = Math.Clamp((newRadius - distance) / .010, 0, 1);
                int offset = (y * RenderSize + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    double undercoat = pixels[offset + channel] * (1 - coverage) + red[channel] * coverage;
                    pixels[offset + channel] = (byte)(undercoat * (1 - blueCoverage) + blue[channel] * blueCoverage);
                }
            }
            return pixels;
        }
    }

    private static void CurvedAmbientFieldAndDrySupport()
    {
        // Native empty-board captures show a smooth dry-camera response near
        // 131 -> 67 -> 134, despite the same known dark rendered underlay.
        // This fixed photon-ambient trough exercises spatial illumination,
        // while the coloured coats still require the existing nonlinear fit.
        var optics = new PaintOptics("curved ambient", 1.65, .28, 2.0, 1.1, -.7, .8,
            CorrectColors: true, CurvedAmbient: true);
        HandTrackingBounds[] tinyDryPatches = [new(.06, .14, .018, .018),
            new(.89, .14, .018, .018), new(.89, .74, .018, .018)];
        var failures = new List<string>();
        foreach (string support in new[] { "wide dry support", "no dry support", "sparse dry support", "occluded sparse dry support" })
        {
            byte[] expected = RenderWetPaint(paintedBackground: support != "wide dry support");
            bool sparse = support.Contains("sparse", StringComparison.Ordinal);
            if (sparse)
                for (int y = 0; y < RenderSize; y++)
                for (int x = 0; x < RenderSize; x++)
                {
                    double u = x / (double)(RenderSize - 1), v = y / (double)(RenderSize - 1);
                    if (!tinyDryPatches.Any(rect => u >= rect.X && u <= rect.X + rect.Width &&
                            v >= rect.Y && v <= rect.Y + rect.Height)) continue;
                    int offset = (y * RenderSize + x) * 4;
                    expected[offset] = 9; expected[offset + 1] = 7; expected[offset + 2] = 5;
                }
            var occlusions = support.StartsWith("occluded", StringComparison.Ordinal) ? tinyDryPatches : null;
            byte[] empty = OpticalCamera(expected, optics, fingerCenter: new(.50, .24), occlusions: occlusions);
            byte[] fingers = OpticalCamera(expected, optics, fingers: true, fingerCenter: new(.50, .24), occlusions: occlusions);
            if (support == "wide dry support")
            {
                int left = Blue(.10, .24), trough = Blue(.50, .24), right = Blue(.90, .24);
                Require(left is >= 120 and <= 145 && trough is >= 60 and <= 80 && right is >= 120 and <= 145 &&
                        left - trough >= 45 && right - trough >= 45,
                    $"The curved ambient fixture lost its measured native scale: {left} -> {trough} -> {right}.");
            }
            var tracker = new PaintDisturbanceTracker();
            foreach (int time in new[] { 1000, 1125 })
                Check(Feed(tracker, empty, expected, time), "empty", time, physical: false);
            // Starting with a hand already held over dry underlay must not
            // teach that local dark patch as the global ambient background.
            tracker.Reset();
            Check(Feed(tracker, fingers, expected, 1250), "cold fingers", 1250, physical: true);
            Check(Feed(tracker, fingers, expected, 1375), "confirmed fingers", 1375, physical: true);
            Check(Feed(tracker, fingers, expected, 1500), "held fingers", 1500, physical: true);
            Check(Feed(tracker, empty, expected, 1625), "fingers removed", 1625, physical: false);

            int Blue(double u, double v) => empty[((int)Math.Round(30 + v * 300) * Width +
                (int)Math.Round(80 + u * 480)) * 4];

            void Check(PaintDisturbanceResult result, string phase, int time, bool physical)
            {
                int confirmations = physical && time != 1250 ? 1 : 0;
                int drops = physical && time == 1375 ? 1 : 0;
                bool valid = result.ReferenceReady && result.CandidateCount == (physical ? 1 : 0) &&
                    result.ConfirmedCandidateCount == confirmations && result.Drops.Count == drops;
                if (drops == 1)
                    valid &= result.Drops.All(drop => Math.Abs(drop.BoardCenter.X - .50) < .025 &&
                        Math.Abs(drop.BoardCenter.Y - .24) < .03 && drop.ForegroundBoardArea >= PaintDisturbanceTracker.MinimumBoardArea);
                if (!valid)
                    failures.Add($"{support}/{phase}: ready={result.ReferenceReady}, candidates={result.CandidateCount}, " +
                        $"confirmed={result.ConfirmedCandidateCount}, drops={result.Drops.Count}, area={result.ForegroundBoardArea:F6}, " +
                        $"reason={result.Reason}.");
            }
        }
        Require(failures.Count == 0, "Curved ambient response must preserve physical input and safe dry-support fallback:\n" +
            string.Join("\n", failures));
    }

    private static void ShiftedCurvedAmbientFields()
    {
        byte[] expected = RenderWetPaint();
        foreach (PixelPoint center in new[] { new PixelPoint(.29, .57), new PixelPoint(.70, .62) })
        {
            var optics = new PaintOptics("shifted curved ambient", 1.65, .28, 2.0, 1.1, -.7, .8,
                CorrectColors: true, CurvedAmbient: true, AmbientCenter: center);
            byte[] empty = OpticalCamera(expected, optics);
            byte[] fingers = OpticalCamera(expected, optics, fingers: true, fingerCenter: center);
            var tracker = new PaintDisturbanceTracker();
            foreach (int time in new[] { 1000, 1125, 1250 })
            {
                var result = Feed(tracker, empty, expected, time);
                Require(result.ReferenceReady && result.CandidateCount == 0 && result.Drops.Count == 0,
                    $"An empty shifted ambient trough at {center} became input: {result}.");
            }
            tracker.Reset();
            var first = Feed(tracker, fingers, expected, 1500);
            var confirmed = Feed(tracker, fingers, expected, 1625);
            Require(first.ReferenceReady && first.CandidateCount == 1 && first.Drops.Count == 0 &&
                confirmed.ConfirmedCandidateCount == 1 && confirmed.Drops.Count == 1 &&
                Math.Abs(confirmed.Drops[0].BoardCenter.X - center.X) < .025 &&
                Math.Abs(confirmed.Drops[0].BoardCenter.Y - center.Y) < .03,
                $"The smooth field learned cold grouped fingers at {center}: first={first}, confirmed={confirmed}.");
            var removed = Feed(tracker, empty, expected, 1750);
            Require(removed.ReferenceReady && removed.CandidateCount == 0 && removed.Drops.Count == 0,
                $"Removing fingers from a shifted ambient trough at {center} left input: {removed}.");
        }
    }

    private static void AspectPerspectiveAndStride()
    {
        byte[] expected = Render(0);
        double[] perspective = [1.0 / 455, .00022, -.20, -.00013, 1.0 / 275, -.045, .00023, -.00008, 1];
        PixelPoint? previous = null;
        foreach (int scale in new[] { 1, 2 })
        {
            double[] map = (double[])perspective.Clone();
            foreach (int index in new[] { 0, 1, 3, 4, 6, 7 }) map[index] /= scale;
            int width = Width * scale, height = Height * scale, stride = width * 4 + 32;
            byte[] frame = Camera(expected, width, height, stride, map,
                obstruction: new(.43, .53, .065, .06), objectColor: (20, 30, 40));
            var tracker = new PaintDisturbanceTracker();
            var scene = Scene(expected, 1000) with { CameraToBoard = map };
            tracker.Update(width, height, stride, frame, scene, Epoch.AddMilliseconds(1000), Epoch.AddMilliseconds(1000));
            var result = tracker.Update(width, height, stride, frame, Scene(expected, 1125) with { CameraToBoard = map },
                Epoch.AddMilliseconds(1125), Epoch.AddMilliseconds(1125));
            PixelPoint center = result.Drops.Single().BoardCenter;
            Require(Math.Abs(center.X - .4625) < .01 && Math.Abs(center.Y - .56) < .01,
                "A rectangular, perspective camera image was stretched or mapped to the wrong paint location.");
            if (previous is { } value) Require(Math.Abs(value.X - center.X) < .004 && Math.Abs(value.Y - center.Y) < .004,
                "Changing camera resolution changed the board location.");
            previous = center;
        }
    }

    private static void TimeRevisionAndReferenceBarriers()
    {
        byte[] expected = Render(0), frame = Camera(expected, fingers: true);
        var tracker = new PaintDisturbanceTracker();
        Feed(tracker, frame, expected, 1000);
        Require(Feed(tracker, frame, expected, 1000).Drops.Count == 0 &&
            Feed(tracker, frame, expected, 900).Drops.Count == 0, "Duplicate or old frames advanced paint evidence.");
        var revised = Scene(expected, 1125) with { Revision = 2 };
        Require(Update(tracker, frame, revised, 1125).Drops.Count == 0,
            "Navigation or calibration reused an earlier board's obstruction confirmation.");
        Require(Update(tracker, frame, revised, 1250).Drops.Count == 1, "A new revision could not gather fresh evidence.");
        Require(tracker.Update(Width, Height, Width * 4, frame, revised, Epoch.AddMilliseconds(1250),
            Epoch.AddMilliseconds(1700)).Reason == "stale-camera-frame", "Stale camera frames were accepted.");
        Require(Feed(tracker, frame, expected, 1800).Drops.Count == 0, "Stale input did not discard old emission state.");
        Require(tracker.Update(Width, Height, Width * 4, frame, Scene(expected, 2000), Epoch.AddMilliseconds(2000),
            Epoch.AddMilliseconds(1900)).Reason == "stale-camera-frame", "Future-dated input was accepted.");
        var missing = Scene(expected, 2200) with { ExpectedHistory = [] };
        Require(Update(tracker, frame, missing, 2200).Reason == "waiting-for-current-paint-render",
            "A missing rendered reference silently learned the hand as a background.");
        var single = Scene(expected, 2300) with { ExpectedHistory = [new(RenderSize, RenderSize, expected, Epoch.AddMilliseconds(2300))] };
        Require(Update(tracker, frame, single, 2300).Drops.Count == 0,
            "A newly entered board did not wait for projected history to settle.");
        var invalid = Scene(expected, 2400) with { CameraToBoard = [0, 0, 0, 0, 0, 0, 0, 0, 0] };
        Require(Update(tracker, frame, invalid, 2400).Reason == "invalid-paint-geometry", "A singular calibration generated paint.");
        tracker.Reset();
        Require(Feed(tracker, frame, expected, 2500).Drops.Count == 0, "Reset retained old paint input.");
    }

    private static PaintDisturbanceResult Feed(PaintDisturbanceTracker tracker, byte[] frame, byte[] expected, int time) =>
        Update(tracker, frame, Scene(expected, time), time);
    private static PaintDisturbanceResult Update(PaintDisturbanceTracker tracker, byte[] frame, PaintDisturbanceScene scene, int time) =>
        tracker.Update(Width, Height, Width * 4, frame, scene, Epoch.AddMilliseconds(time), Epoch.AddMilliseconds(time));
    private static PaintDisturbanceScene Scene(byte[] expected, int time) => new(1, Mapping,
        [new(RenderSize, RenderSize, expected, Epoch.AddMilliseconds(time - 250)),
            new(RenderSize, RenderSize, expected, Epoch.AddMilliseconds(time - 125))], PaintBounds);

    private static byte[] Render(int step)
    {
        byte[] image = new byte[RenderSize * RenderSize * 4];
        for (int y = 0; y < RenderSize; y++)
        for (int x = 0; x < RenderSize; x++)
        {
            double u = x / (double)(RenderSize - 1), v = y / (double)(RenderSize - 1);
            double b = 222 + 6 * u, g = 228 + 5 * v, r = 230;
            if (v < .14) { b = 40 + 70 * u; g = 45 + 90 * u; r = 60 + 100 * u; }
            for (int pigment = 0; pigment < 5; pigment++)
            {
                double centerX = .12 + pigment * .18, centerY = .33 + (pigment % 2) * .47;
                double radius = .035 + step * .008;
                double distance = Math.Sqrt(Math.Pow(u - centerX, 2) + Math.Pow(v - centerY, 2));
                if (distance >= radius) continue;
                double amount = Math.Clamp((radius - distance) * 180, 0, .92);
                double pb = 35 + pigment * 27, pg = 30 + (pigment * 73) % 150, pr = 50 + (pigment * 59) % 175;
                b = b * (1 - amount) + pb * amount; g = g * (1 - amount) + pg * amount; r = r * (1 - amount) + pr * amount;
            }
            int offset = (y * RenderSize + x) * 4;
            image[offset] = (byte)b; image[offset + 1] = (byte)g; image[offset + 2] = (byte)r; image[offset + 3] = 255;
        }
        return image;
    }

    private static byte[] Camera(byte[] expected, int width = Width, int height = Height, int stride = Width * 4,
        double[]? matrix = null, bool fingers = false, HandTrackingBounds? obstruction = null,
        (byte B, byte G, byte R)? objectColor = null, double exposure = 1, int noise = 2)
    {
        matrix ??= Mapping;
        byte[] frame = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double divisor = matrix[6] * x + matrix[7] * y + matrix[8];
            double u = (matrix[0] * x + matrix[1] * y + matrix[2]) / divisor;
            double v = (matrix[3] * x + matrix[4] * y + matrix[5]) / divisor;
            int offset = y * stride + x * 4;
            if (u is < 0 or > 1 || v is < 0 or > 1) { frame[offset + 3] = 255; continue; }
            int ix = (int)Math.Round(u * (RenderSize - 1)), iy = (int)Math.Round(v * (RenderSize - 1));
            int source = (iy * RenderSize + ix) * 4;
            double b = expected[source], g = expected[source + 1], r = expected[source + 2];
            double random = noise * (((x * 17 + y * 31) % 17) / 8.0 - 1);
            frame[offset] = (byte)Math.Clamp((b * .63 + g * .09 + 16 + u * 5) * exposure + random, 0, 255);
            frame[offset + 1] = (byte)Math.Clamp((g * .7 + r * .04 + 10 + v * 8) * exposure + random, 0, 255);
            frame[offset + 2] = (byte)Math.Clamp((r * .74 + 11) * exposure + random, 0, 255);
            frame[offset + 3] = 255;
            bool occupied = obstruction is { } rect && u >= rect.X && u <= rect.X + rect.Width && v >= rect.Y && v <= rect.Y + rect.Height;
            if (fingers)
                for (int finger = 0; finger < 4; finger++)
                    occupied |= u >= .46 + finger * .018 && u <= .473 + finger * .018 && v >= .54 && v <= .64;
            if (!occupied) continue;
            var color = objectColor ?? (75, 115, 165);
            frame[offset] = (byte)color.Item1; frame[offset + 1] = (byte)color.Item2; frame[offset + 2] = (byte)color.Item3;
        }
        return frame;
    }

    private sealed record PaintOptics(string Name, double Exposure, double Ambient, double CameraGamma,
        double ShiftX, double ShiftY, double BlurPixels, bool CorrectColors = false, bool CurvedAmbient = false,
        PixelPoint? AmbientCenter = null);

    private static byte[] RenderWetPaint(bool paintedBackground = false)
    {
        byte[] image = new byte[RenderSize * RenderSize * 4];
        (double U, double V, double Radius, double B, double G, double R)[] coats = [
            (.35, .45, .095, 20, 35, 238), (.47, .50, .105, 245, 78, 18),
            (.60, .57, .090, 37, 232, 60), (.53, .39, .070, 208, 35, 230),
            (.68, .38, .055, 245, 216, 31), (.31, .62, .065, 20, 142, 245) ];
        for (int y = 0; y < RenderSize; y++)
        for (int x = 0; x < RenderSize; x++)
        {
            double u = x / (double)(RenderSize - 1), v = y / (double)(RenderSize - 1);
            double b = paintedBackground ? 190 : 9 + u, g = paintedBackground ? 180 : 7 + v,
                r = paintedBackground ? 175 : 5;
            foreach (var coat in coats)
            {
                double distance = Math.Sqrt(Math.Pow(u - coat.U, 2) + Math.Pow(v - coat.V, 2));
                double coverage = Math.Clamp((coat.Radius - distance) / .013, 0, 1);
                if (coverage == 0) continue;
                double sheen = 24 * Math.Exp(-(Math.Pow(u - coat.U + .012, 2) + Math.Pow(v - coat.V + .017, 2)) / .00015);
                b = b * (1 - coverage) + Math.Min(255, coat.B + sheen) * coverage;
                g = g * (1 - coverage) + Math.Min(255, coat.G + sheen) * coverage;
                r = r * (1 - coverage) + Math.Min(255, coat.R + sheen) * coverage;
            }
            int offset = (y * RenderSize + x) * 4;
            image[offset] = (byte)b; image[offset + 1] = (byte)g; image[offset + 2] = (byte)r; image[offset + 3] = 255;
        }
        return image;
    }

    private static byte[] OpticalCamera(byte[] expected, PaintOptics optics, bool fingers = false,
        byte[]? adjacentExposure = null, PixelPoint? fingerCenter = null, HandTrackingBounds[]? occlusions = null)
    {
        byte[] frame = new byte[Width * Height * 4];
        // Projector gamma is applied before blur: the lens spreads emitted
        // light, not encoded RGB. The camera then adds ambient illumination,
        // white balance and its response curve, clipping only at capture.
        double[] emitted = new double[expected.Length];
        for (int index = 0; index < expected.Length; index++)
            emitted[index] = adjacentExposure is null ? Math.Pow(expected[index] / 255.0, 2.2) :
                (Math.Pow(expected[index] / 255.0, 2.2) + Math.Pow(adjacentExposure[index] / 255.0, 2.2)) / 2;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double u = (x - 80) / 480.0, v = (y - 30) / 300.0;
            int offset = (y * Width + x) * 4;
            frame[offset + 3] = 255;
            if (u is < 0 or > 1 || v is < 0 or > 1) continue;
            double b = 0, g = 0, r = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                double weight = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1) / 16.0;
                double sourceU = u + (optics.ShiftX + dx * optics.BlurPixels) / 480;
                double sourceV = v + (optics.ShiftY + dy * optics.BlurPixels) / 300;
                b += weight * Sample(sourceU, sourceV, 0);
                g += weight * Sample(sourceU, sourceV, 1);
                r += weight * Sample(sourceU, sourceV, 2);
            }
            // Four naturally grouped finger backs retain the projected paint
            // colours, but reflect substantially less light than the board.
            bool occupied = occlusions?.Any(rect => u >= rect.X && u <= rect.X + rect.Width &&
                v >= rect.Y && v <= rect.Y + rect.Height) == true;
            if (fingers)
                for (int finger = 0; finger < 4; finger++)
                {
                    double shiftU = (fingerCenter?.X ?? .489) - .489, shiftV = (fingerCenter?.Y ?? .510) - .510;
                    double centerU = .462 + finger * .018 + shiftU,
                        top = .457 + shiftV + (finger == 1 ? -.012 : finger == 3 ? .009 : 0);
                    double closestV = Math.Clamp(v, top + .007, .563 + shiftV);
                    occupied |= Math.Pow((u - centerU) / .007, 2) + Math.Pow((v - closestV) / .007, 2) <= 1;
                }
            double reflectance = occupied ? .26 : 1;
            PixelPoint ambientCenter = optics.AmbientCenter ?? new(.50, .24);
            double ambient = optics.CurvedAmbient ? optics.Ambient - .205 * Math.Exp(
                -Math.Pow((u - ambientCenter.X) / .20, 2) - Math.Pow((v - ambientCenter.Y) / .42, 2)) : optics.Ambient;
            ambient *= 1 + .08 * u - .05 * v;
            double noise = (((x * 17 + y * 31) % 17) / 8.0 - 1) * 1.5;
            double capturedB = Capture((ambient * .95 + optics.Exposure * (b * .86 + g * .05)) * reflectance, noise);
            double capturedG = Capture((ambient + optics.Exposure * (g * .93 + r * .025)) * reflectance, noise);
            double capturedR = Capture((ambient * 1.05 + optics.Exposure * (r * 1.04 + g * .035)) * reflectance, noise);
            if (optics.CorrectColors)
            {
                // ISP colour correction can subtract channels after the tone
                // curve. This white-preserving matrix models a captured green
                // pigment whose red response reaches the black clamp, unlike
                // positive optical cross-talk alone. Real phone captures show
                // this condition; it must not become a physical obstruction.
                (capturedB, capturedG, capturedR) =
                    (1.1 * capturedB - .1 * capturedG, 1.15 * capturedG - .15 * capturedB,
                        1.8 * capturedR - .8 * capturedG);
            }
            frame[offset] = (byte)Math.Clamp(capturedB, 0, 255);
            frame[offset + 1] = (byte)Math.Clamp(capturedG, 0, 255);
            frame[offset + 2] = (byte)Math.Clamp(capturedR, 0, 255);
        }
        return frame;

        double Capture(double light, double noise) =>
            Math.Pow(Math.Max(0, light), 1 / optics.CameraGamma) * 255 + noise;

        double Sample(double u, double v, int channel)
        {
            double px = Math.Clamp(u * (RenderSize - 1), 0, RenderSize - 1), py = Math.Clamp(v * (RenderSize - 1), 0, RenderSize - 1);
            int ix = (int)px, iy = (int)py, right = Math.Min(RenderSize - 1, ix + 1), bottom = Math.Min(RenderSize - 1, iy + 1);
            double fx = px - ix, fy = py - iy;
            return emitted[(iy * RenderSize + ix) * 4 + channel] * (1 - fx) * (1 - fy) +
                emitted[(iy * RenderSize + right) * 4 + channel] * fx * (1 - fy) +
                emitted[(bottom * RenderSize + ix) * 4 + channel] * (1 - fx) * fy +
                emitted[(bottom * RenderSize + right) * 4 + channel] * fx * fy;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
