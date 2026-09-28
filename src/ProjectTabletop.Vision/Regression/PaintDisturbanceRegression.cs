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
        AspectPerspectiveAndStride();
        TimeRevisionAndReferenceBarriers();
        Console.WriteLine("Paint disturbance regression: stationary four-finger and generic-object input, " +
            "measured area cutoff, ignored controls, photometric/noise rejection, delayed rendered-frame matching, " +
            "own-animation rejection, bounded drop cadence, native aspect/perspective/stride, and stale/revision barriers passed.");
    }

    private static void StationaryObstructionAndCadence()
    {
        var tracker = new PaintDisturbanceTracker();
        byte[] expected = Render(0), empty = Camera(expected), fingers = Camera(expected, fingers: true);
        var first = Feed(tracker, fingers, expected, 1000);
        Require(first.ReferenceReady && first.CandidateCount == 1 && first.Drops.Count == 0,
            $"A first-frame stationary hand must be evidence, but wait for confirmation: {first}.");
        var confirmed = Feed(tracker, fingers, expected, 1125);
        var drop = confirmed.Drops.Single();
        Require(Math.Abs(drop.BoardCenter.X - .492) < .02 && Math.Abs(drop.BoardCenter.Y - .59) < .02,
            "The four-finger obstruction was not mapped to its actual board location.");
        Require(drop.ForegroundBoardArea >= PaintDisturbanceTracker.MinimumBoardArea &&
            drop.ObservedAt == Epoch.AddMilliseconds(1125), "A paint drop lost its measured area or source timestamp.");
        foreach (int time in new[] { 1250, 1500, 1750, 2000 })
            Require(Feed(tracker, fingers, expected, time).Drops.Count == 0,
                "A held hand emitted before the renderer's 900 ms same-position interval.");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
