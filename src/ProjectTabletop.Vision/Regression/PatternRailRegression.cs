using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PatternRailRegression
{
    private const int Width = PatternRailGeometry.ReferenceWidth, Height = PatternRailGeometry.ReferenceHeight;
    private const int Columns = PatternRailGeometry.Columns, Marks = Columns * 2;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Straight = [new(20, 20), new(980, 20), new(980, 84), new(20, 84)];

    public static void Run()
    {
        CheckSymmetricPattern();
        CheckColdStationaryAndRemoval();
        CheckOpticsAndAmbiguity();
        CheckIsolatedMarkNoise();
        CheckExpandedProjection();
        CheckBloomRecovery();
        CheckBloomDuringContact();
        CheckPartialContactBoundaries();
        CheckBoundedGeometryRefinement();
        CheckRepeatedBoundedRefinement();
        CheckPeriodicPhaseWitnesses();
        CheckPerspectiveRotationAndOutsideAnimation();
        CheckFrameAndGeometryBarriers();
        Console.WriteLine("Stryker's Run pattern rail passed: symmetric marks, cold and stationary shape loss, end/centre positions, " +
            "brightness/colour/blur invariance, radius-eight clear and held-contact recovery, perspective/rotation, protected animation, ambiguity, " +
            "partial contact boundaries, isolated-mark noise, repeated bounded all-clear geometry refinement, fixed periodic-pattern registration, " +
            "removal and frame/geometry barriers.");
    }

    private static void CheckSymmetricPattern()
    {
        Require(Columns == 49, "The rail must include the additional leading colon.");
        for (int column = 0; column < Columns; column++)
        {
            int opposite = Columns - column - 1;
            Require(PatternRailGeometry.ColumnCenter(column) + PatternRailGeometry.ColumnCenter(opposite) == Width &&
                PatternRailGeometry.DotY1ForColumn(column) == PatternRailGeometry.DotY1ForColumn(opposite) &&
                PatternRailGeometry.DotY2ForColumn(column) == PatternRailGeometry.DotY2ForColumn(opposite),
                "The rail's dots or wider spacing witnesses are not mirror-symmetric.");
        }
        using var clear = Rail();
        var observation = Observe(new(), Camera(clear, Straight, 1000, 120), new(12, Straight), 0);
        Require(observation.Reliable && observation.Position is null && observation.IntactCount == Marks &&
            observation.DamageCount == 0, "The symmetric rail did not register every clear mark: " + observation);
    }

    private static void CheckColdStationaryAndRemoval()
    {
        foreach (var (first, count) in new[] { (0, 1), (22, 4), (Columns - 1, 1) })
        {
            var detector = new PatternRailDetector();
            using var covered = Rail((first, count));
            var pixels = Camera(covered, Straight, 1000, 120);
            var geometry = new PatternRailGeometry(1, Straight);
            var initial = Observe(detector, pixels, geometry, 0);
            Require(initial.Position is null && initial.Reliable && initial.Reason == "confirming-occlusion",
                "A cold covered rail did not require one fresh confirmation: " + initial);
            for (int frame = 1; frame <= 20; frame++)
            {
                var held = Observe(detector, pixels, geometry, frame * 33);
                Require(held.Position is { } x && Math.Abs(x - (first + count / 2d) / PatternRailGeometry.Columns) < .025 &&
                    !held.Ambiguous && held.DamageCount >= 2,
                    "A stationary object was absorbed or its position was wrong: " + held);
            }
            using var clear = Rail();
            var removed = Observe(detector, Camera(clear, Straight, 1000, 120), geometry, 700);
            Require(removed.Reliable && removed.Position is null && removed.DamageCount == 0,
                "Restored marks did not clear input immediately: " + removed);
            Require(Observe(detector, pixels, geometry, 733).Position is null,
                "Removal failed to clear prior confirmation.");
        }
    }

    private static void CheckOpticsAndAmbiguity()
    {
        using var clear = Rail();
        var geometry = new PatternRailGeometry(2, Straight);
        foreach (var (gain, offsets, local, columns) in new[]
        {
            (.25, new[] { 8, 8, 8 }, false, 6), (1.5, new[] { 35, 35, 35 }, false, 6),
            (.7, new[] { 50, 4, 30 }, false, 6), (.65, new[] { 50, 7, 30 }, true, 6),
            (.4, new[] { 25, 25, 25 }, true, 1), (.4, new[] { 20, 5, 30 }, true, 3),
            (2d, new[] { 80, 80, 80 }, true, 3)
        })
        {
            using var altered = clear.Clone();
            Transform(altered, gain, offsets, local, columns);
            var detector = new PatternRailDetector();
            var alteredCamera = Camera(altered, Straight, 1000, 120);
            var first = Observe(detector, alteredCamera, geometry, 0);
            var second = Observe(detector, alteredCamera, geometry, 33);
            Require(first.Reliable && second.Reliable && second.Position is null && second.DamageCount == 0,
                "Intact brightness or colour changes became physical input: " + second);
            using var partial = Rail((23, 2));
            var partialCamera = Camera(partial, Straight, 1000, 120);
            Observe(detector, partialCamera, geometry, 66);
            var restored = Observe(detector, alteredCamera, geometry, 99);
            Require(restored.Reliable && restored.Position is null && restored.DamageCount == 0 &&
                Observe(detector, partialCamera, geometry, 132).Position is null,
                "Intact projected marks on a changed surface retained partial acquisition: " + restored);
        }
        foreach (double sigma in new[] { .8, 1.6, 2.4 })
        {
            using var blurred = new Mat();
            Cv2.GaussianBlur(clear, blurred, new Size(0, 0), sigma);
            var detector = new PatternRailDetector();
            var observation = Observe(detector, Camera(blurred, Straight, 1000, 120), geometry, 0);
            Require(observation.Reliable && observation.Position is null && observation.DamageCount == 0,
                "Coherent focus changes became physical input: " + observation);
        }
        foreach (double gain in new[] { .015, .025 })
        {
            using var dim = clear.Clone();
            Transform(dim, gain, [0, 0, 0], true, 3);
            var detector = new PatternRailDetector();
            var camera = Camera(dim, Straight, 1000, 120);
            Observe(detector, camera, geometry, 0);
            var result = Observe(detector, camera, geometry, 33);
            Require(result.Position is null,
                "A dim intact shape below the contrast floor became physical input: " + result);
        }

        var tracked = new PatternRailDetector();
        var registered = Observe(tracked, Camera(clear, Straight, 1000, 120), geometry, 0);
        using var hidden = Rail((23, 2));
        var covered = Camera(hidden, Straight, 1000, 120);
        Observe(tracked, covered, geometry, 33);
        var acquired = Observe(tracked, covered, geometry, 66);
        Require(acquired.Position is not null && acquired.RegistrationX == registered.RegistrationX &&
            acquired.RegistrationY == registered.RegistrationY && acquired.Blur == registered.Blur,
            "A repeated neighbouring colon was used to explain away missing marks.");
        using var twoObjects = Rail((8, 2), (37, 2));
        var ambiguous = Observe(tracked, Camera(twoObjects, Straight, 1000, 120), geometry, 99);
        Require(ambiguous.Ambiguous && !ambiguous.Reliable && ambiguous.Position is null,
            "Separated physical objects were averaged or silently assigned to the previous input.");
        Require(Observe(tracked, covered, geometry, 132).Position is null,
            "Ambiguity retained old acquisition confirmation.");
        using var localClipping = clear.Clone();
        Transform(localClipping, 1, [255, 255, 255], true, 3);
        var clippedCamera = Camera(localClipping, Straight, 1000, 120);
        foreach (int time in new[] { 165, 198 })
        {
            var clipped = Observe(tracked, clippedCamera, geometry, time);
            Require(!clipped.Reliable && clipped.Position is null && clipped.Reason == "rail-exposure-clipped",
                "Local sensor clipping was interpreted as physical input: " + clipped);
        }
        Require(Observe(tracked, covered, geometry, 231).Position is null,
            "Clipping retained old acquisition confirmation.");
        using var white = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(255));
        using var black = new Mat(Height, Width, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
        foreach (var unavailable in new[] { white, black })
        {
            var detector = new PatternRailDetector();
            var observation = Observe(detector, Camera(unavailable, Straight, 1000, 120), geometry, 0);
            Require(!observation.Reliable && observation.Position is null,
                "Global clipping or absent contrast was interpreted as an object.");
        }
    }

    private static void CheckIsolatedMarkNoise()
    {
        var geometry = new PatternRailGeometry(15, Straight);
        var main = Render(true);
        foreach (int column in new[] { 0, 20, 24, Columns - 1 })
        foreach (bool alreadyHeld in new[] { false, true })
        {
            var detector = new PatternRailDetector();
            Observe(detector, Render(false), geometry, 0);
            var baseline = new PatternRailDetector();
            Observe(baseline, main, geometry, 0);
            var expected = Observe(baseline, main, geometry, 33).Position;
            Require(expected is not null, "The isolated-mark fixture did not acquire its main contact.");
            if (alreadyHeld)
            {
                Observe(detector, main, geometry, 33);
                Require(Observe(detector, main, geometry, 66).Position is not null,
                    "The isolated-mark fixture did not establish held input.");
            }
            var noisy = Render(true, (column, column % 2, "erase"));
            var initial = Observe(detector, noisy, geometry, 99);
            Require(initial.Reliable && !initial.Ambiguous && (alreadyHeld
                ? initial.Position is { } firstX && Math.Abs(firstX - expected.GetValueOrDefault()) < .00001
                : initial.Position is null),
                "One stray mark interrupted contact or bypassed fresh confirmation: " + initial);
            for (int frame = 4; frame <= 6; frame++)
            {
                var held = Observe(detector, noisy, geometry, frame * 33);
                Require(held.Reliable && !held.Ambiguous && held.Position is { } x && Math.Abs(x - expected.GetValueOrDefault()) < .00001,
                    "One stray mark paused or biased the valid contact: " + held);
            }
            var diagnostics = detector.GetDiagnostics();
            Require(diagnostics.Correlations.Count(value => value < .60) == 3,
                "The isolated-mark fixture must damage exactly two contact dots and one separate dot.");
            var lonePixels = Render(false, (column, column % 2, "erase"));
            var lone = new PatternRailDetector();
            Require(Observe(lone, lonePixels, geometry, 0).Position is null &&
                Observe(lone, lonePixels, geometry, 33).Reason == "insufficient-shape-damage",
                "A lone stray mark acquired input without a valid contact.");
            var remaining = Observe(detector, lonePixels, geometry, 231);
            Require(!remaining.Reliable && remaining.Position is null && !remaining.Ambiguous &&
                remaining.Reason == "insufficient-shape-damage",
                "Lifting the main contact left stale input on a stray dot: " + remaining);
            Require(Observe(detector, noisy, geometry, 264).Position is null &&
                Observe(detector, noisy, geometry, 297).Position is not null,
                "A lone stray dot retained old contact confirmation.");
        }
        foreach (var marks in new (int Column, int Dot, string Kind)[][]
        {
            [(4, 0, "erase"), (4, 1, "erase")], // Two independently valid contact spans.
            [(4, 0, "erase"), (39, 1, "erase")], // Two separate singletons cannot both be ignored.
            [(4, 0, "clip")],
            [(4, 0, "dim")]
        })
        {
            var detector = new PatternRailDetector();
            Observe(detector, main, geometry, 0);
            Require(Observe(detector, main, geometry, 33).Position is not null, "Guard fixture did not acquire contact.");
            var rejected = Observe(detector, Render(true, marks), geometry, 66);
            string expectedReason = marks[0].Kind switch
            {
                "clip" => "rail-exposure-clipped", "dim" => "rail-contrast-uncertain", _ => "multiple-occlusions"
            };
            Require(!rejected.Reliable && rejected.Position is null &&
                rejected.Reason == expectedReason && (marks.Length == 1 || rejected.Ambiguous),
                "Isolated-mark filtering accepted multiple obstructions or unavailable optical evidence: " + rejected);
            Require(Observe(detector, main, geometry, 99).Position is null,
                "Rejected isolated-mark evidence retained old contact confirmation.");
        }

        static Raster Render(bool contact, params (int Column, int Dot, string Kind)[] marks)
        {
            using var rail = contact ? Rail((22, 1)) : Rail();
            foreach (var (column, dot, kind) in marks)
            {
                int x = PatternRailGeometry.ColumnCenter(column);
                int y = dot == 0 ? PatternRailGeometry.DotY1ForColumn(column) : PatternRailGeometry.DotY2ForColumn(column);
                if (kind == "dim")
                {
                    using var patch = new Mat(rail, new Rect(x - 8, y - 8, 17, 17));
                    patch.ConvertTo(patch, MatType.CV_8UC4, .015);
                }
                else
                {
                    int radius = kind == "clip" ? 9 : 7;
                    Cv2.Rectangle(rail, new Rect(x - radius, y - radius, radius * 2 + 1, radius * 2 + 1),
                        kind == "clip" ? Scalar.All(255) : new Scalar(0, 0, 0, 255), -1);
                }
            }
            return Camera(rail, Straight, 1000, 120);
        }
    }

    private static void CheckPerspectiveRotationAndOutsideAnimation()
    {
        foreach (var (corners, width, height) in new[]
        {
            (new PixelPoint[] { new(80, 90), new(1100, 125), new(1065, 213), new(120, 195) }, 1200, 350),
            // Unequal end heights require projective division, not interpolation of four corners.
            (new PixelPoint[] { new(80, 60), new(1100, 180), new(1070, 230), new(120, 170) }, 1200, 350),
            // A 720-line camera with a 405-pixel portrait field and its bottom-edge rail.
            (new PixelPoint[] { new(190, 650), new(530, 650), new(530, 679), new(190, 679) }, 1280, 720),
            (new PixelPoint[] { new(280, 80), new(280, 1040), new(216, 1040), new(216, 80) }, 400, 1200)
        })
        {
            using var clear = Rail();
            var geometry = new PatternRailGeometry(3, corners);
            var detector = new PatternRailDetector();
            var clearCamera = Camera(clear, corners, width, height);
            var initial = Observe(detector, clearCamera, geometry, 0);
            Require(initial.Reliable && initial.DamageCount == 0, "Calibrated rail transform was not recognized: " + initial);
            using var hidden = Rail((12, 3));
            var covered = Camera(hidden, corners, width, height);
            Observe(detector, covered, geometry, 33);
            var result = Observe(detector, covered, geometry, 66);
            Require(result.Position is { } x && Math.Abs(x - 13.5 / Columns) < .025,
                "Perspective or portrait rotation changed the measured along-rail coordinate: " + result);
            // This animated patch is deliberately outside both supplied rail quadrilaterals.
            for (int y = 0; y < Math.Min(50, height); y++)
            for (int animationX = 0; animationX < width; animationX++)
            {
                int offset = (y * width + animationX) * 4;
                clearCamera.Bgra[offset] = (byte)(animationX % 255);
                clearCamera.Bgra[offset + 1] = 255;
                clearCamera.Bgra[offset + 2] = (byte)(y * 5);
            }
            var animation = Observe(detector, clearCamera, geometry, 99);
            Require(animation.Reliable && animation.Position is null && animation.DamageCount == 0,
                "Artwork outside the protected rail generated physical input.");
        }
    }

    private static void CheckPeriodicPhaseWitnesses()
    {
        var geometry = new PatternRailGeometry(6, Straight);
        foreach (int radius in new[] { PatternRailGeometry.DotRadius, 7, 8 })
        foreach (int shift in new[] { -PatternRailGeometry.CellWidth, PatternRailGeometry.CellWidth })
        foreach (int coveredEnd in new[] { -1, 0, PatternRailGeometry.Columns - 1 })
        {
            using var reference = coveredEnd < 0 ? RailWithRadius(radius) : RailWithRadius(radius, (coveredEnd, 1));
            using var shifted = new Mat();
            using var translation = Mat.FromArray(new double[,] { { 1, 0, shift }, { 0, 1, 0 } });
            Cv2.WarpAffine(reference, shifted, translation,
                new Size(PatternRailGeometry.ReferenceWidth, PatternRailGeometry.ReferenceHeight),
                InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(0, 0, 0, 255));
            var camera = Camera(shifted, Straight, 1000, 120);
            foreach (bool registeredFirst in new[] { false, true })
            {
                var detector = new PatternRailDetector();
                using var clear = RailWithRadius(radius);
                if (registeredFirst) Observe(detector, Camera(clear, Straight, 1000, 120), geometry, 0);
                Observe(detector, camera, geometry, 33);
                var result = Observe(detector, camera, geometry, 66);
                Require(result.Position is null && !result.Reliable,
                    "A whole-cell pattern shift was mistaken for an end contact: " + result);
            }
        }
    }

    private static void CheckExpandedProjection()
    {
        var geometry = new PatternRailGeometry(7, Straight);
        foreach (int radius in new[] { 6, 7, 8 })
        foreach (double blur in new[] { .8, 2.4 })
        {
            using var clear = RailWithRadius(radius);
            Cv2.GaussianBlur(clear, clear, new Size(0, 0), blur);
            var clearCamera = Camera(clear, Straight, 1000, 120);
            var detector = new PatternRailDetector();
            var initial = Observe(detector, clearCamera, geometry, 0);
            Require(initial.Reliable && initial.Position is null && initial.DamageCount == 0 && initial.IntactCount >= 64,
                "Common bright-dot expansion was mistaken for corrupted shapes: " + initial);
            using var hidden = RailWithRadius(radius, (22, 4));
            Cv2.GaussianBlur(hidden, hidden, new Size(0, 0), blur);
            var covered = Camera(hidden, Straight, 1000, 120);
            Require(Observe(detector, covered, geometry, 33).Position is null,
                "Expanded projection bypassed contact confirmation.");
            var contact = Observe(detector, covered, geometry, 66);
            Require(contact.Position is { } x && Math.Abs(x - .5) < .025 && contact.OpticalRadius == initial.OpticalRadius &&
                contact.Blur == initial.Blur && contact.RegistrationX == initial.RegistrationX &&
                contact.RegistrationY == initial.RegistrationY,
                "Contact changed the common optical profile or lost its location: " + contact);
            using var altered = clear.Clone();
            Transform(altered, .65, [35, 6, 20], true, 5);
            var restored = Observe(detector, Camera(altered, Straight, 1000, 120), geometry, 99);
            Require(restored.Reliable && restored.Position is null && restored.DamageCount == 0 &&
                Observe(detector, covered, geometry, 132).Position is null,
                "Intact tinted marks retained expanded-projection contact: " + restored);
        }
    }

    private static void CheckBloomRecovery()
    {
        var geometry = new PatternRailGeometry(10, Straight);
        using var initial = RailWithRadius(7);
        using var expanded = RailWithRadius(8);
        Cv2.GaussianBlur(initial, initial, new Size(0, 0), 1.6);
        Cv2.GaussianBlur(expanded, expanded, new Size(0, 0), 1.6);
        var initialCamera = Camera(initial, Straight, 1000, 120);
        var clearCamera = BloomCamera(expanded);
        var fresh = Observe(new(), clearCamera, geometry, 0);
        Require(fresh.Reliable && fresh.IntactCount == Marks && fresh.Position is null && fresh.DamageCount == 0,
            "The expanded clear rail failed cold registration: " + fresh);
        var recovered = new PatternRailDetector();
        var original = Observe(recovered, initialCamera, geometry, 0);
        Require(original.Reliable && original.IntactCount == Marks && original.OpticalRadius == 7,
            "The bloom-recovery fixture did not establish the original radius-seven profile: " + original);
        var cleared = Observe(recovered, clearCamera, geometry, 33);
        Require(cleared.Reliable && cleared.IntactCount == Marks && cleared.Position is null && cleared.DamageCount == 0 &&
            cleared.OpticalRadius == 8 && !cleared.GeometryRefined,
            "A coherent bloom change failed all-clear recovery on the frozen map: " + cleared);
        foreach (var (gain, tint) in new[] { (.35, new[] { 10, 10, 10 }), (.8, new[] { 30, 4, 18 }) })
        {
            using var changed = expanded.Clone();
            Transform(changed, gain, tint, false);
            var intact = Observe(new(), BloomCamera(changed), geometry, 0);
            Require(intact.Reliable && intact.IntactCount == Marks && intact.Position is null && intact.DamageCount == 0,
                "Global brightness or tint of expanded dots became contact: " + intact);
        }
        foreach (var (first, count) in new[] { (0, 1), (22, 4), (Columns - 1, 1) })
        {
            using var hidden = RailWithRadius(8, (first, count));
            Cv2.GaussianBlur(hidden, hidden, new Size(0, 0), 1.6);
            var covered = BloomCamera(hidden);
            foreach (bool warmed in new[] { false, true })
            {
                var detector = new PatternRailDetector();
                if (warmed) Observe(detector, clearCamera, geometry, 0);
                var pending = Observe(detector, covered, geometry, 33);
                Require(pending.Reliable && pending.Position is null && pending.Reason == "confirming-occlusion",
                    "Expanded contact bypassed fresh confirmation or could not acquire: " + pending);
                var confirmed = Observe(detector, covered, geometry, 66);
                Require(confirmed.Reliable && confirmed.Position is { } x &&
                    Math.Abs(x - (first + count / 2d) / Columns) < .025 && confirmed.DamageCount >= 2,
                    "Expanded left/centre/right contact lost its location: " + confirmed);
                using var restored = expanded.Clone();
                Transform(restored, .55, [35, 8, 22], true, 4);
                var intact = Observe(detector, BloomCamera(restored), geometry, 99);
                Require(intact.Reliable && intact.Position is null && intact.DamageCount == 0 &&
                    Observe(detector, covered, geometry, 132).Position is null,
                    "Intact tinted expanded marks retained the old contact confirmation: " + intact);
            }
        }
        using var separated = RailWithRadius(8, (8, 2), (37, 2));
        Cv2.GaussianBlur(separated, separated, new Size(0, 0), 1.6);
        var ambiguous = Observe(recovered, BloomCamera(separated), geometry, 66);
        Require(ambiguous.Ambiguous && !ambiguous.Reliable && ambiguous.Position is null,
            "The expanded optical profile merged separated contacts: " + ambiguous);

    }

    private static void CheckBloomDuringContact()
    {
        var geometry = new PatternRailGeometry(11, Straight);
        using var initial = BlurredRail(7);
        using var expanded = BlurredRail(8);
        var initialCamera = Camera(initial, Straight, 1000, 120);
        foreach (var (first, count) in new[] { (0, 3), (22, 4), (Columns - 3, 3) })
        {
            using var initialContact = BlurredRail(7, (first, count));
            using var expandedContact = BlurredRail(8, (first, count));
            var oldPixels = Camera(initialContact, Straight, 1000, 120);
            var newPixels = BloomCamera(expandedContact);
            var detector = new PatternRailDetector();
            var registered = Observe(detector, initialCamera, geometry, 0);
            Require(registered.Reliable && registered.OpticalRadius == 7, "Held-bloom fixture failed initial registration.");
            Observe(detector, oldPixels, geometry, 33);
            Require(Observe(detector, oldPixels, geometry, 66).Position is not null,
                "Held-bloom fixture did not acquire its original contact.");
            var frozenCorners = detector.GetDiagnostics().EffectiveCameraCorners.ToArray();
            var pending = Observe(detector, newPixels, geometry, 99);
            Require(pending.Reliable && pending.Position is null && pending.Reason == "confirming-occlusion",
                "A coherent bloom during contact either stalled or reused old confirmation: " + pending);
            for (int frame = 4; frame <= 12; frame++)
            {
                var held = Observe(detector, newPixels, geometry, frame * 33);
                Require(held.Reliable && held.Position is { } x && Math.Abs(x - (first + count / 2d) / Columns) < .025 &&
                    held.OpticalRadius == 8 && held.DamageCount >= 2 && !held.GeometryRefined,
                    "Held contact required a lift after a common optical change: " + held);
            }
            Require(detector.GetDiagnostics().EffectiveCameraCorners.SequenceEqual(frozenCorners),
                "Contact recovery changed the frozen camera mapping.");
            var cold = new PatternRailDetector();
            Require(Observe(cold, newPixels, geometry, 99).Position is null &&
                Observe(cold, newPixels, geometry, 132).Position is { } coldX &&
                Math.Abs(coldX - (first + count / 2d) / Columns) < .025,
                "The same covered pixels disagreed between cold acquisition and optical recovery.");
        }

        using var oldContact = BlurredRail(7, (22, 4));
        using var newContact = BlurredRail(8, (22, 4));
        var covered = BloomCamera(newContact);
        using var separated = BlurredRail(8, (8, 2), (37, 2));
        using var clipped = expanded.Clone();
        Transform(clipped, 1, [255, 255, 255], true, 4);
        using var dim = expanded.Clone();
        Transform(dim, .015, [0, 0, 0], true, 4);
        using var incoherent = RailWithRadius(8, (22, 4));
        using (var patch = new Mat(incoherent, new Rect(6 * 20, 0, 2 * 20, 64)))
        using (var original = patch.Clone())
        using (var translation = Mat.FromArray(new double[,] { { 1, 0, 0 }, { 0, 1, 3 } }))
            Cv2.WarpAffine(original, patch, translation, new Size(40, 64), InterpolationFlags.Linear,
                BorderTypes.Constant, new Scalar(0, 0, 0, 255));
        Cv2.GaussianBlur(incoherent, incoherent, new Size(0, 0), 1.6);
        foreach (var unavailable in new[]
        {
            BloomCamera(separated), BloomCamera(clipped), BloomCamera(dim), BloomCamera(incoherent),
            BloomCamera(expanded, -20), BloomCamera(expanded, 20)
        })
        {
            var detector = AcquiredOriginal();
            var rejected = Observe(detector, unavailable, geometry, 99);
            Require(!rejected.Reliable && rejected.Position is null &&
                Observe(detector, unavailable, geometry, 132).Position is null &&
                detector.GetDiagnostics().OpticalRadius == 7,
                "Optical recovery committed ambiguous, clipped, dim, incoherent or aliased marks: " + rejected);
            var pending = Observe(detector, covered, geometry, 165);
            Require(pending.Reliable && pending.Position is null &&
                Observe(detector, covered, geometry, 198).Position is not null,
                "A rejected optical candidate prevented fresh contact recovery or retained old confirmation: " + pending);
        }
        foreach (var (gain, offsets) in new[] { (.35, new[] { 10, 10, 10 }), (.8, new[] { 30, 4, 18 }) })
        {
            using var changed = expanded.Clone();
            Transform(changed, gain, offsets, false);
            var detector = AcquiredOriginal();
            var restored = Observe(detector, BloomCamera(changed), geometry, 99);
            Require(restored.Reliable && restored.Position is null && restored.DamageCount == 0 &&
                Observe(detector, covered, geometry, 132).Position is null,
                "Intact changed brightness or colour retained contact through optical recovery: " + restored);
        }

        PatternRailDetector AcquiredOriginal()
        {
            var detector = new PatternRailDetector();
            Observe(detector, initialCamera, geometry, 0);
            var pixels = Camera(oldContact, Straight, 1000, 120);
            Observe(detector, pixels, geometry, 33);
            Require(Observe(detector, pixels, geometry, 66).Position is not null, "Recovery guard fixture failed original acquisition.");
            return detector;
        }
        static Mat BlurredRail(int radius, params (int First, int Count)[] occlusions)
        {
            var rail = RailWithRadius(radius, occlusions);
            Cv2.GaussianBlur(rail, rail, new Size(0, 0), 1.6);
            return rail;
        }
    }

    private static void CheckPartialContactBoundaries()
    {
        var geometry = new PatternRailGeometry(13, Straight);
        using var initial = RailWithRadius(7);
        Cv2.GaussianBlur(initial, initial, new Size(0, 0), 1.6);
        var initialCamera = Camera(initial, Straight, 1000, 120);
        foreach (int first in new[] { 0, 22, Columns - 3 })
        {
            int boundary = first == 0 ? 3 : first - 1;
            using var partial = RailWithRadius(8, (first, 3));
            ClipBoundary(partial, boundary, first == 0);
            Cv2.GaussianBlur(partial, partial, new Size(0, 0), 1.6);
            var pixels = BloomCamera(partial);
            var detector = Warm();
            var originalCorners = detector.GetDiagnostics().EffectiveCameraCorners.ToArray();
            var pending = Observe(detector, pixels, geometry, 33);
            var diagnostics = detector.GetDiagnostics();
            Require(diagnostics.Correlations.Skip(boundary * 2).Take(2).Any(value => value is >= .60 and < .82) &&
                diagnostics.Correlations.Skip(boundary * 2).Take(2).All(value => value >= .60) &&
                diagnostics.Contrasts.Skip(boundary * 2).Take(2).All(value => value >= 2.5),
                "The partial-boundary fixture did not retain recognizable but incomplete adjacent dots.");
            Require(pending.Reliable && pending.Position is null && pending.Reason == "confirming-occlusion",
                "An adjacent partial dot blocked optical recovery or reused old confirmation: " + pending);
            for (int frame = 2; frame <= 6; frame++)
            {
                var held = Observe(detector, pixels, geometry, frame * 33);
                Require(held.Reliable && held.Position is { } x && Math.Abs(x - (first + 1.5) / Columns) < .025 &&
                    held.DamageCount >= 2 && held.OpticalRadius == 8 && !held.GeometryRefined,
                    "A partially covered contact boundary stalled or changed steering: " + held);
            }
            Require(detector.GetDiagnostics().EffectiveCameraCorners.SequenceEqual(originalCorners),
                "Partial boundary recovery changed the camera mapping while a finger was present.");
        }

        using var valid = RailWithRadius(8, (22, 3));
        ClipBoundary(valid, 21, false);
        Cv2.GaussianBlur(valid, valid, new Size(0, 0), 1.6);
        var validPixels = BloomCamera(valid);
        foreach (int failure in new[] { 0, 1, 2 })
        {
            using var rejectedRail = RailWithRadius(8, (22, 3));
            ClipBoundary(rejectedRail, 21, false);
            if (failure == 0) ClipBoundary(rejectedRail, 6, false);
            if (failure == 1)
                Cv2.Rectangle(rejectedRail, new Rect(7 * 20, 10, 2 * 20, 44), new Scalar(55, 85, 120, 255), -1);
            Cv2.GaussianBlur(rejectedRail, rejectedRail, new Size(0, 0), 1.6);
            if (failure == 2) Transform(rejectedRail, .07, [0, 0, 0], false);
            var pixels = BloomCamera(rejectedRail);
            var detector = Warm();
            var originalCorners = detector.GetDiagnostics().EffectiveCameraCorners.ToArray();
            var rejected = Observe(detector, pixels, geometry, 33);
            Require(!rejected.Reliable && rejected.Position is null &&
                Observe(detector, pixels, geometry, 66).Position is null &&
                detector.GetDiagnostics().OpticalRadius == 7 &&
                detector.GetDiagnostics().EffectiveCameraCorners.SequenceEqual(originalCorners),
                "Boundary recovery accepted remote marginal damage, separated contacts, or a dim boundary: " + rejected);
            Require(Observe(detector, validPixels, geometry, 99).Position is null &&
                Observe(detector, validPixels, geometry, 132).Position is not null,
                "A rejected boundary candidate prevented fresh valid contact acquisition.");
        }

        PatternRailDetector Warm()
        {
            var detector = new PatternRailDetector();
            var clear = Observe(detector, initialCamera, geometry, 0);
            Require(clear.Reliable && clear.OpticalRadius == 7 && clear.IntactCount == Marks,
                "The boundary fixture did not establish the original optics.");
            return detector;
        }
        static void ClipBoundary(Mat rail, int column, bool left)
        {
            // A fingertip erases only the outer edge of the adjacent colon, preserving most of each dot.
            int width = left ? 6 : 5;
            Cv2.Rectangle(rail, new Rect(column * 20 + (left ? 0 : 20 - width), 10, width, 44),
                new Scalar(55, 85, 120, 255), -1);
        }
    }

    private static Raster BloomCamera(Mat rail, int horizontalShift = 0)
    {
        // The physical clear failure combined expanded dots with a small coherent centroid drift.
        using var shifted = new Mat();
        using var translation = Mat.FromArray(new double[,] { { 1, 0, horizontalShift }, { 0, 1, 1 } });
        Cv2.WarpAffine(rail, shifted, translation, new Size(Width, Height), InterpolationFlags.Linear,
            BorderTypes.Constant, new Scalar(0, 0, 0, 255));
        return Camera(shifted, Straight, 1000, 120);
    }

    private static void CheckBoundedGeometryRefinement()
    {
        // Generated dots with measured near-identity projective error; no captured pixels or background.
        PixelPoint[] calibration = [new(1590.602, 811.949), new(1589.374, 277.419),
            new(1634.856, 276.990), new(1635.900, 811.646)];
        PixelPoint[] projected = [new(1587.729, 810.946), new(1590.047, 277.388),
            new(1634.917, 276.984), new(1632.193, 810.558)];
        var geometry = new PatternRailGeometry(8, calibration);
        var clear = Render();
        var detector = new PatternRailDetector();
        var initial = Observe(detector, clear, geometry, 0);
        Require(initial.Reliable && initial.Position is null && initial.DamageCount == 0 &&
            initial.IntactCount == Marks && initial.GeometryRefined,
            "A bounded coherent projective error did not recover all clear marks: " + initial);

        foreach (var (first, count) in new[] { (0, 1), (22, 4), (Columns - 1, 1) })
        {
            var covered = Render((first, count));
            var cold = new PatternRailDetector();
            Observe(cold, covered, geometry, 0);
            var coldResult = Observe(cold, covered, geometry, 33);
            Require(!coldResult.Reliable && coldResult.Position is null && !coldResult.GeometryRefined,
                "A damaged cold rail committed a geometric correction: " + coldResult);
            var warmed = new PatternRailDetector();
            Observe(warmed, clear, geometry, 0);
            Require(Observe(warmed, covered, geometry, 33).Position is null,
                "Refined geometry bypassed fresh contact confirmation.");
            for (int frame = 2; frame < 8; frame++)
            {
                var held = Observe(warmed, covered, geometry, frame * 33);
                Require(held.Position is { } x && Math.Abs(x - (first + count / 2d) / Columns) < .025 &&
                    held.GeometryRefined && held.RegistrationX == initial.RegistrationX &&
                    held.RegistrationY == initial.RegistrationY && held.OpticalRadius == initial.OpticalRadius,
                    "A cached global correction absorbed contact or changed its location: " + held);
            }
            Require(Observe(warmed, covered, geometry, 500).Position is null,
                "Refined geometry retained confirmation through a stale-frame gap.");
            Require(!Observe(warmed, covered, geometry, 500).Reliable,
                "A duplicate frame advanced refined contact.");
            var removed = Observe(warmed, clear, geometry, 533);
            Require(removed.Reliable && removed.Position is null && removed.DamageCount == 0 && removed.GeometryRefined,
                "Removal failed to clear a cached corrected rail: " + removed);
            var epochChanged = Observe(warmed, covered, geometry with { Revision = 9 }, 566);
            Require(!epochChanged.Reliable && !epochChanged.GeometryRefined && epochChanged.Position is null,
                "A geometry epoch reused the old fitted mapping while marks were covered: " + epochChanged);
            warmed.Reset();
            Require(!Observe(warmed, covered, geometry, 599).GeometryRefined,
                "Reset retained a learned rail correction.");
        }
        var two = Render((8, 2), (37, 2));
        var ambiguous = Observe(detector, two, geometry, 33);
        Require(ambiguous.Ambiguous && !ambiguous.Reliable && ambiguous.Position is null && ambiguous.GeometryRefined,
            "A corrected rail averaged separate contacts or discarded its frozen geometry: " + ambiguous);
        var unavailableGeometry = geometry with
        {
            CameraCorners = projected.Select(point => new PixelPoint(point.X + 6, point.Y)).ToArray()
        };
        var beyond = Observe(new(), clear, unavailableGeometry, 0);
        Require(!beyond.Reliable && !beyond.GeometryRefined && beyond.Position is null,
            "Registration exceeded its four-native-pixel bound: " + beyond);
        foreach (int shift in new[] { -20, 20 })
        {
            using var rail = RailWithRadius(7);
            using var shifted = new Mat();
            using var translation = Mat.FromArray(new double[,] { { 1, 0, shift }, { 0, 1, 0 } });
            Cv2.WarpAffine(rail, shifted, translation, new Size(Width, Height), InterpolationFlags.Linear,
                BorderTypes.Constant, new Scalar(0, 0, 0, 255));
            var periodic = Observe(new(), Camera(shifted, projected, 1920, 1080), geometry, 0);
            Require(!periodic.Reliable && !periodic.GeometryRefined && periodic.Position is null,
                "Global refinement accepted a neighbouring periodic pattern: " + periodic);
        }

        Raster Render(params (int First, int Count)[] occlusions)
        {
            using var rail = RailWithRadius(7, occlusions);
            Cv2.GaussianBlur(rail, rail, new Size(0, 0), 1.6);
            return Camera(rail, projected, 1920, 1080);
        }
    }

    private static void CheckRepeatedBoundedRefinement()
    {
        PixelPoint[] calibration = [new(1590.602, 811.949), new(1589.374, 277.419),
            new(1634.856, 276.990), new(1635.900, 811.646)];
        PixelPoint[] projected = [new(1587.729, 810.946), new(1590.047, 277.388),
            new(1634.917, 276.984), new(1632.193, 810.558)];
        var geometry = new PatternRailGeometry(14, calibration);
        var detector = new PatternRailDetector();
        var initial = Observe(detector, Render(0), geometry, 0);
        Require(initial.Reliable && initial.IntactCount == Marks && initial.GeometryRefined,
            "The drift fixture did not establish an initial corrected map: " + initial);
        var firstCorners = detector.GetDiagnostics().EffectiveCameraCorners.ToArray();
        // A later coherent movement exceeds the old profile's tolerance. All dots must be clear
        // before fitting again; a covered frame must preserve the previous map even after refinement.
        Observe(detector, Render(3.2, (22, 3)), geometry, 33);
        Require(detector.GetDiagnostics().EffectiveCameraCorners.SequenceEqual(firstCorners),
            "A previously refined map learned new geometry from an obscured rail.");
        var recovered = Observe(detector, Render(3.2), geometry, 66);
        var after = detector.GetDiagnostics().EffectiveCameraCorners;
        Require(recovered.Reliable && recovered.Position is null && recovered.DamageCount == 0 &&
            recovered.IntactCount == Marks && recovered.GeometryRefined && !after.SequenceEqual(firstCorners) &&
            after.Select((point, i) => Distance(point, calibration[i])).All(value => value <= 4),
            "A clear rail failed repeated refinement inside the original calibration bound: " + recovered);
        var contact = Render(3.2, (22, 3));
        Require(Observe(detector, contact, geometry, 99).Position is null &&
            Observe(detector, contact, geometry, 132).Position is { } x && Math.Abs(x - 23.5 / Columns) < .025 &&
            detector.GetDiagnostics().EffectiveCameraCorners.SequenceEqual(after),
            "Refinement reused contact confirmation, displaced steering, or moved geometry under contact.");
        var bounded = detector.GetDiagnostics().EffectiveCameraCorners.ToArray();
        foreach (int frame in new[] { 0, 1, 2 })
        {
            // Another 3.2 pixels is locally small but must not accumulate beyond the original four-pixel bound.
            var rejected = Observe(detector, Render(6.4), geometry, 165 + frame * 33);
            Require(!rejected.Reliable && rejected.Position is null &&
                detector.GetDiagnostics().EffectiveCameraCorners.SequenceEqual(bounded),
                "Repeated refinement accumulated movement beyond the original calibration bound: " + rejected);
        }
        var restored = Observe(detector, Render(3.2), geometry, 264);
        Require(restored.Reliable && restored.Position is null && restored.IntactCount == Marks,
            "A rejected distant candidate damaged the previously valid map.");

        Raster Render(double shift, params (int First, int Count)[] occlusions)
        {
            using var rail = RailWithRadius(7, occlusions);
            Cv2.GaussianBlur(rail, rail, new Size(0, 0), 1.6);
            return Camera(rail, projected.Select(point => new PixelPoint(point.X + shift, point.Y)).ToArray(), 1920, 1080);
        }
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    private static void CheckFrameAndGeometryBarriers()
    {
        using var hidden = Rail((20, 3));
        var pixels = Camera(hidden, Straight, 1000, 120);
        var geometry = new PatternRailGeometry(4, Straight);
        var detector = new PatternRailDetector();
        Require(Observe(detector, pixels, geometry, 100).Position is null, "Cold input bypassed confirmation.");
        var duplicate = Observe(detector, pixels, geometry, 100);
        var old = Observe(detector, pixels, geometry, 90);
        Require(!duplicate.Reliable && !old.Reliable && duplicate.Reason == "old-camera-frame" && old.Position is null,
            "Repeated or old frames advanced input confirmation.");
        Require(Observe(detector, pixels, geometry, 133).Position is not null, "Two distinct fresh frames failed to confirm.");
        Require(Observe(detector, pixels, geometry, 500).Position is null, "An input gap retained stale confirmation.");
        Require(Observe(detector, pixels, geometry, 533).Position is not null, "Fresh input failed after a gap.");
        Require(Observe(detector, pixels, geometry with { Revision = 5 }, 566).Position is null,
            "A new scene revision retained input confirmation.");
        detector.Reset();
        Require(Observe(detector, pixels, geometry, 599).Position is null, "Reset retained input confirmation.");
        var invalid = geometry with { CameraCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)] };
        var tooSmall = Observe(detector, pixels, invalid, 632);
        Require(!tooSmall.Reliable && tooSmall.Position is null, "Unresolved camera marks were treated as a valid rail.");
    }

    private sealed record Raster(int Width, int Height, byte[] Bgra);
    private static PatternRailObservation Observe(PatternRailDetector detector, Raster frame,
        PatternRailGeometry geometry, int milliseconds) => detector.Observe(frame.Bgra, frame.Width,
            frame.Height, frame.Width * 4, geometry, Epoch.AddMilliseconds(milliseconds));

    private static Mat Rail(params (int First, int Count)[] occlusions) =>
        RailWithRadius(PatternRailGeometry.DotRadius, occlusions);

    private static Mat RailWithRadius(int radius, params (int First, int Count)[] occlusions)
    {
        var image = new Mat(PatternRailGeometry.ReferenceHeight, PatternRailGeometry.ReferenceWidth,
            MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
        for (int column = 0; column < PatternRailGeometry.Columns; column++)
        foreach (int y in new[] { PatternRailGeometry.DotY1ForColumn(column), PatternRailGeometry.DotY2ForColumn(column) })
            Cv2.Circle(image, new Point(PatternRailGeometry.ColumnCenter(column), y), radius,
                new Scalar(235, 235, 235, 255), -1, LineTypes.AntiAlias);
        foreach (var (first, count) in occlusions)
            Cv2.Rectangle(image, new Rect(first * 20, 10, count * 20, 44), new Scalar(55, 85, 120, 255), -1);
        return image;
    }

    private static Raster Camera(Mat rail, IReadOnlyList<PixelPoint> corners, int width, int height)
    {
        using var transform = Cv2.GetPerspectiveTransform(new Point2f[] { new(0, 0), new(Width, 0), new(Width, Height), new(0, Height) },
            corners.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray());
        using var image = new Mat();
        Cv2.WarpPerspective(rail, image, transform, new Size(width, height), InterpolationFlags.Linear,
            BorderTypes.Constant, new Scalar(0, 0, 0, 255));
        byte[] pixels = new byte[width * height * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new(width, height, pixels);
    }

    private static void Transform(Mat image, double gain, int[] offsets, bool local, int columns = 6)
    {
        int width = image.Width, height = image.Height;
        byte[] pixels = new byte[width * height * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        for (int y = 0; y < height; y++)
        for (int x = local ? 20 * 20 : 0; x < (local ? (20 + columns) * 20 : width); x++)
        for (int channel = 0; channel < 3; channel++)
        {
            int index = (y * width + x) * 4 + channel;
            pixels[index] = (byte)Math.Clamp(pixels[index] * gain + offsets[channel], 0, 255);
        }
        Marshal.Copy(pixels, 0, image.Data, pixels.Length);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
