using System.Buffers;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>A learned black crossbar or a pair of parallel bars. Area and aspect describe one
/// strip; paired spacing is perpendicular centre distance divided by strip length.</summary>
public sealed record BlackTipProfile(int Version, double Value, double Saturation,
    double NormalizedArea, double AspectRatio = 1, int BarCount = 1, double SpacingRatio = 0)
{
    public double NormalizedRadius => Math.Sqrt(NormalizedArea / Math.PI);
    public bool IsValid => Version == 1 && double.IsFinite(Value) && Value is >= 0 and <= .38 &&
        double.IsFinite(Saturation) && Saturation is >= 0 and <= .70 &&
        double.IsFinite(NormalizedArea) && NormalizedArea is >= .0000001 and <= .025 &&
        double.IsFinite(AspectRatio) && AspectRatio is >= 1.6 and <= 16 &&
        double.IsFinite(SpacingRatio) && (BarCount == 1 && SpacingRatio == 0 ||
            BarCount == 2 && SpacingRatio is >= .12 and <= .80);
}

/// <summary>Raw-camera midpoints of a bar's short edges (End1/End2) and long edges
/// (Side1/Side2). These axes have no inherent forward direction: the caller preserves its
/// chosen front across frames. Map all four points through calibration before deriving a pose.</summary>
public sealed record BlackBarGeometry(PixelPoint End1, PixelPoint End2, PixelPoint Side1, PixelPoint Side2);

public sealed record BlackTipLearningOptions(IReadOnlyList<EyeTipProjectionFrame>? ProjectionFrames = null,
    DateTimeOffset FrameTime = default);

/// <summary>A recent measured full marker and a bounded prediction used only to locate an
/// additional local search. It never supplies an unmeasured controller position.</summary>
public sealed record BlackTipSearchHint(ColorTipObservation Marker, PixelPoint ExpectedCenter,
    DateTimeOffset LastFullFrame);

/// <summary>
/// Detects a solid black crossbar or two parallel bars as one marker. Learned
/// size, elongation and pair spacing reject unrelated marks; fresh scene references veto the app's
/// own black artwork. Returns the common measured-component contract for ColorTipTracker.
/// Callers must partition identical bats by calibrated player half BEFORE temporal association.
/// </summary>
public static class BlackTipDetector
{
    private const int MaximumDimension = 1920;

    public static BlackTipProfile Learn(int width, int height, int stride, byte[] bgra,
        PixelPoint click, BlackTipLearningOptions? options = null)
    {
        ValidateFrame(width, height, stride, bgra);
        if (!double.IsFinite(click.X) || !double.IsFinite(click.Y) || click.X < 3 || click.Y < 3 ||
            click.X >= width - 3 || click.Y >= height - 3)
            throw new ArgumentException("Click inside the black crossbar, away from the camera edge.");
        using var image = new BlackImage(width, height, stride, bgra);
        int cx = (int)Math.Round(image.ToImageX(click.X)), cy = (int)Math.Round(image.ToImageY(click.Y));
        if (cx < 2 || cy < 2 || cx >= image.Width - 2 || cy >= image.Height - 2)
            throw new ArgumentException("Click the black crossbar away from the camera edge.");
        var patch = new List<(double Value, double Saturation)>();
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int x = cx - 1; x <= cx + 1; x++)
        {
            double value = image.Value[y * image.Width + x] / 255.0, saturation = image.Saturation(x, y) / 255.0;
            // Projected colour can saturate black ink without making it bright. Rejecting
            // that tint fragments a thin bar as it crosses the pitch's light and dark stripes.
            // Darkness, shape, local contrast and the artwork veto identify the physical ink.
            if (value <= .38) patch.Add((value, saturation));
        }
        if (patch.Count < 7)
            throw new InvalidOperationException("Click the solid black middle of one bar. Keep any handle uncoloured.");
        var seed = new BlackTipProfile(1, patch.Select(item => item.Value).Order().ElementAt(patch.Count / 2),
            Math.Min(.70, patch.Select(item => item.Saturation).Order().ElementAt(patch.Count / 2)), .0001, 3);
        using Mat mask = image.Mask(seed);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        var selected = contours.Where((contour, index) => hierarchy[index].Parent < 0 &&
                Cv2.PointPolygonTest(contour, new Point2f(cx, cy), false) >= 0)
            .Select(contour => Component(contour, image, seed, null))
            .Where(item => item is not null).OrderBy(item => item!.Observation.AreaPixels).FirstOrDefault();
        if (selected is null || selected.Aspect < 1.8)
            throw new InvalidOperationException("The selected mark is not a separate black crossbar. Show the whole long bar and click its middle.");
        var result = seed with
        {
            NormalizedArea = selected.Observation.AreaPixels / (width * (double)height),
            AspectRatio = selected.Aspect
        };
        if (!result.IsValid) throw new InvalidOperationException("The crossbar is outside the supported size or shape. Make its width about 2–16 times its thickness.");
        EyeTipProjectionMatcher? projection = null;
        if (options?.ProjectionFrames is { } frames)
        {
            projection = new EyeTipProjectionMatcher(frames, options.FrameTime);
            if (!projection.Ready)
                throw new InvalidOperationException("Wait for a current projected-board reference, then click the physical crossbar again.");
            if (!projection.ContainsBoardPoint(selected.Observation.Center))
                throw new InvalidOperationException("Place the black marker on the projected board, then click inside one bar again.");
            if (!Physical(projection, selected.Observation, image))
                throw new InvalidOperationException("That mark is part of the projected picture. Click inside a physical black bar.");
        }
        var strips = contours.Where((_, index) => hierarchy[index].Parent < 0)
            .Select(contour => Component(contour, image, result, selected.Observation.AreaPixels))
            .Where(item => item is not null).Select(item => item!.Observation)
            .OrderBy(item => ColorTipDetector.Distance(item.Center, selected.Observation.Center)).Take(128)
            .Where(item => projection is null || Physical(projection, item, image)).ToArray();
        var pairs = FindPairs(strips, null);
        int selectedIndex = Array.FindIndex(strips, item =>
            ColorTipDetector.Distance(item.Center, selected.Observation.Center) < 1);
        var touching = pairs.Where(pair => pair.First == selectedIndex || pair.Second == selectedIndex).ToArray();
        if (touching.Length > 1 || touching.Length == 1 && pairs.Any(pair => pair != touching[0] &&
            (pair.First == touching[0].First || pair.Second == touching[0].First ||
             pair.First == touching[0].Second || pair.Second == touching[0].Second)))
            throw new InvalidOperationException("This bar has more than one matching neighbour. Show one separate pair of parallel bars, then click either strip.");
        if (touching.Length == 1)
        {
            var pair = touching[0];
            var first = strips[pair.First];
            var second = strips[pair.Second];
            result = result with
            {
                BarCount = 2,
                SpacingRatio = pair.Match.SpacingRatio,
                NormalizedArea = (first.AreaPixels + second.AreaPixels) / (2 * width * (double)height),
                AspectRatio = (StripAspect(first) + StripAspect(second)) * .5
            };
        }
        if (!result.IsValid)
            throw new InvalidOperationException("The marker is outside the supported size or shape. Show the complete bar or parallel pair.");
        return result;
    }

    public static ColorTipDetectionResult Detect(int width, int height, int stride, byte[] bgra,
        BlackTipProfile profile, ColorTipDetectionOptions? options = null) =>
        DetectEach(width, height, stride, bgra, [profile], options)[0];

    /// <summary>Detects each learned bar in one camera frame, in profile order. The colour
    /// conversion and projected-scene reference are shared; only the dark threshold and the
    /// learned size and shape differ per profile.</summary>
    /// <param name="searchArea">Optional raw-camera polygon containing every possible marker
    /// centre, such as the calibrated grass. Only its bounds plus a marker-sized margin are
    /// converted and searched; the camera's surroundings cannot add work or candidates.</param>
    public static IReadOnlyList<ColorTipDetectionResult> DetectEach(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<BlackTipProfile> profiles, ColorTipDetectionOptions? options = null,
        IReadOnlyList<BlackTipSearchHint?>? searchHints = null, IReadOnlyList<PixelPoint>? searchArea = null)
    {
        ValidateFrame(width, height, stride, bgra);
        ArgumentNullException.ThrowIfNull(profiles);
        if (searchHints is not null && searchHints.Count != profiles.Count)
            throw new ArgumentException("Supply one search hint per profile.", nameof(searchHints));
        foreach (var profile in profiles)
        {
            ArgumentNullException.ThrowIfNull(profile, nameof(profiles));
            if (!profile.IsValid) throw new ArgumentException("Invalid black-crossbar profile.", nameof(profiles));
        }
        if (options?.PreferredCenter is { } preferred && (!double.IsFinite(preferred.X) ||
            !double.IsFinite(preferred.Y) || preferred.X < 0 || preferred.Y < 0))
            throw new ArgumentException("Invalid preferred black-crossbar center.", nameof(options));
        EyeTipProjectionMatcher? projection = options?.ProjectionFrames is null ? null :
            new(options.ProjectionFrames, options.FrameTime);
        if (projection is { Ready: false })
            return profiles.Select(_ => new ColorTipDetectionResult([], "waiting-for-projected-bat-reference")).ToArray();
        if (profiles.Count == 0) return [];
        using var image = new BlackImage(width, height, stride, bgra, SearchBounds(searchArea, profiles, width, height));
        // Both players usually see the same physical strips. The projected-scene check
        // depends only on the measured strip, so compare each one at most once per frame.
        var vetoes = new Dictionary<ColorTipObservation, bool>();
        return profiles.Select((profile, index) => Detect(image, profile, width * (double)height,
            options?.PreferredCenter, projection, searchHints?[index], options?.FrameTime ?? default, vetoes)).ToArray();
    }

    // Marker strips extend beyond their centre, and both the contrast ring and the
    // projected-scene comparison sample around each strip. Keep all of that inside.
    private static Rect? SearchBounds(IReadOnlyList<PixelPoint>? area, IReadOnlyList<BlackTipProfile> profiles,
        int width, int height)
    {
        if (area is not { Count: >= 3 } || area.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return null;
        double length = profiles.Max(profile =>
            Math.Sqrt(profile.NormalizedArea * width * (double)height * profile.AspectRatio));
        double margin = Math.Max(96, length * 2.5);
        int left = (int)Math.Floor(Math.Max(0, area.Min(point => point.X) - margin));
        int top = (int)Math.Floor(Math.Max(0, area.Min(point => point.Y) - margin));
        int right = (int)Math.Ceiling(Math.Min(width, area.Max(point => point.X) + margin));
        int bottom = (int)Math.Ceiling(Math.Min(height, area.Max(point => point.Y) + margin));
        return right - left < 16 || bottom - top < 16 ? null : new(left, top, right - left, bottom - top);
    }

    private static bool Physical(EyeTipProjectionMatcher projection, ColorTipObservation strip, BlackImage image) =>
        projection.IsPhysicalBlackBarCandidate(strip, image.Gray, image.Width, image.Height,
            image.ScaleX, image.ScaleY, image.OriginX, image.OriginY);

    private static ColorTipDetectionResult Detect(BlackImage image, BlackTipProfile profile, double framePixels,
        PixelPoint? preferred, EyeTipProjectionMatcher? projection, BlackTipSearchHint? hint, DateTimeOffset frameTime,
        Dictionary<ColorTipObservation, bool> vetoes)
    {
        double expectedArea = profile.NormalizedArea * framePixels;
        var strips = ExtractStrips(image, profile, expectedArea, preferred, projection, vetoes);
        if (profile.BarCount == 2)
        {
            var supporting = strips.ToList();
            var ambiguous = new List<PixelPoint>();
            var pairs = FindPairs(strips, profile.SpacingRatio);
            var partners = new int[strips.Length];
            foreach (var pair in pairs) { partners[pair.First]++; partners[pair.Second]++; }
            ambiguous.AddRange(pairs.Where(pair => partners[pair.First] > 1 || partners[pair.Second] > 1)
                .Select(pair => pair.Match.Observation.Center));
            // A strip cannot be used in two markers. Reject ambiguous local pairings instead
            // of choosing whichever partner happens to score first in this camera frame.
            var unambiguous = pairs.Where(pair => partners[pair.First] == 1 && partners[pair.Second] == 1)
                .Select(pair => pair.Match.Observation).ToList();
            if (partners.Any(count => count == 0))
            {
                // White goal lines can brighten one bar. Recover its partner only when a
                // normally dark physical strip anchors the learned pair. Two newly bright
                // strips cannot become a controller, and complete pairs keep their identity.
                var lit = ExtractStrips(image, profile, expectedArea, preferred, projection, vetoes, .08);
                MergeStrips(supporting, lit);
                var anchor = lit.Select(strip => Array.FindIndex(strips, normal => SameStrip(normal, strip))).ToArray();
                var supported = FindPairs(lit, profile.SpacingRatio).Where(pair =>
                    (anchor[pair.First] >= 0 && partners[anchor[pair.First]] == 0 ||
                     anchor[pair.Second] >= 0 && partners[anchor[pair.Second]] == 0) &&
                    (anchor[pair.First] < 0 || partners[anchor[pair.First]] == 0) &&
                    (anchor[pair.Second] < 0 || partners[anchor[pair.Second]] == 0)).ToArray();
                var litPartners = new int[lit.Length];
                foreach (var pair in supported) { litPartners[pair.First]++; litPartners[pair.Second]++; }
                ambiguous.AddRange(supported.Where(pair => litPartners[pair.First] > 1 || litPartners[pair.Second] > 1)
                    .Select(pair => pair.Match.Observation.Center));
                unambiguous.AddRange(supported.Where(pair => litPartners[pair.First] == 1 && litPartners[pair.Second] == 1)
                    .Select(pair => pair.Match.Observation));
            }
            string source = "global";
            // Both strips can brighten together under a projected goal or car. A short-lived
            // acquired pose permits a bounded local threshold, still using measured shape,
            // contrast and per-strip artwork rejection. Keep the global scan above so another
            // marker in the same half is never hidden by this search window.
            if (ValidHint(hint, frameTime) && !unambiguous.Any(item => NearHint(item, hint!)))
            {
                var region = image.SearchRegion(hint!);
                double allowance = image.LocalAllowance(profile, region);
                var local = ExtractStrips(image, profile, expectedArea, hint!.ExpectedCenter, projection, vetoes,
                    allowance, region);
                MergeStrips(supporting, local);
                var localPairs = FindPairs(local, profile.SpacingRatio);
                var localPartners = new int[local.Length];
                foreach (var pair in localPairs) { localPartners[pair.First]++; localPartners[pair.Second]++; }
                ambiguous.AddRange(localPairs.Where(pair => localPartners[pair.First] > 1 || localPartners[pair.Second] > 1)
                    .Select(pair => pair.Match.Observation.Center));
                var recovered = localPairs.Where(pair => localPartners[pair.First] == 1 && localPartners[pair.Second] == 1)
                    .Select(pair => pair.Match.Observation).Where(item => NearHint(item, hint)).ToArray();
                unambiguous.AddRange(recovered);
                if (recovered.Length > 0) source = "local-contrast";
            }
            var paired = (preferred is { } center
                ? unambiguous.OrderBy(item => ColorTipDetector.Distance(item.Center, center))
                : unambiguous.OrderByDescending(item => item.Score)).Take(16).ToArray();
            return new(paired, paired.Length == 0 ? "no-black-bar-pair" :
                paired.Length == 1 ? "black-bar-pair-candidate" : "multiple-black-bar-pairs")
            { SupportingBars = supporting, AmbiguousMarkerCenters = ambiguous, Source = source };
        }
        var accepted = strips.Take(16).ToArray();
        return new(accepted, accepted.Length == 0 ? "no-black-crossbar" :
            accepted.Length == 1 ? "black-crossbar-candidate" : "multiple-black-crossbars");
    }

    private static ColorTipObservation[] ExtractStrips(BlackImage image, BlackTipProfile profile, double expectedArea,
        PixelPoint? preferred, EyeTipProjectionMatcher? projection, Dictionary<ColorTipObservation, bool> vetoes,
        double valueAllowance = 0, Rect? region = null)
    {
        using Mat mask = image.Mask(profile, valueAllowance, region);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple, region?.TopLeft);
        var candidates = contours.Where((_, index) => hierarchy[index].Parent < 0)
            .Where(contour => region is not { } bounds || contour.All(point => point.X > bounds.Left &&
                point.X < bounds.Right - 1 && point.Y > bounds.Top && point.Y < bounds.Bottom - 1))
            .Select(contour => Component(contour, image, profile, expectedArea, valueAllowance))
            .Where(item => item is not null).Select(item => item!.Observation);
        var ranked = preferred is { } point
            ? candidates.OrderBy(item => ColorTipDetector.Distance(item.Center, point)).ThenByDescending(item => item.Score)
            : candidates.OrderByDescending(item => item.Score);
        return ranked.Take(128).Where(item => projection is null ||
            (vetoes.TryGetValue(item, out bool physical) ? physical : vetoes[item] = Physical(projection, item, image)))
            .ToArray();
    }

    private static void MergeStrips(List<ColorTipObservation> target, IEnumerable<ColorTipObservation> additions)
    {
        foreach (var strip in additions)
        {
            int same = target.FindIndex(previous => SameStrip(previous, strip));
            if (same < 0) target.Add(strip);
            else if (strip.Score > target[same].Score) target[same] = strip;
        }
    }

    private static bool ValidHint(BlackTipSearchHint? hint, DateTimeOffset frameTime) =>
        hint?.Marker.Bar is not null && frameTime != default && hint.LastFullFrame != default &&
        frameTime >= hint.LastFullFrame && frameTime - hint.LastFullFrame <= TimeSpan.FromMilliseconds(350) &&
        double.IsFinite(hint.ExpectedCenter.X) && double.IsFinite(hint.ExpectedCenter.Y) &&
        double.IsFinite(hint.Marker.Center.X) && double.IsFinite(hint.Marker.Center.Y) &&
        ColorTipDetector.Distance(hint.Marker.Bar.End1, hint.Marker.Bar.End2) is >= 6 and <= 1000;

    private static bool NearHint(ColorTipObservation item, BlackTipSearchHint hint)
    {
        if (item.Bar is not { } a || hint.Marker.Bar is not { } b) return false;
        double length = ColorTipDetector.Distance(b.End1, b.End2), measured = ColorTipDetector.Distance(a.End1, a.End2);
        double width = ColorTipDetector.Distance(b.Side1, b.Side2), measuredWidth = ColorTipDetector.Distance(a.Side1, a.Side2);
        double dot = ((a.End2.X - a.End1.X) * (b.End2.X - b.End1.X) +
            (a.End2.Y - a.End1.Y) * (b.End2.Y - b.End1.Y)) / (length * measured);
        return measured >= length * .78 && measured <= length * 1.28 &&
            measuredWidth >= width * .70 && measuredWidth <= width * 1.4 && Math.Abs(dot) >= .766 &&
            ColorTipDetector.Distance(item.Center, hint.ExpectedCenter) <= Math.Max(28, length * .8);
    }

    private static bool SameStrip(ColorTipObservation normal, ColorTipObservation lit)
    {
        if (normal.Bar is not { } a || lit.Bar is not { } b) return false;
        double length = ColorTipDetector.Distance(a.End1, a.End2), litLength = ColorTipDetector.Distance(b.End1, b.End2);
        double width = ColorTipDetector.Distance(a.Side1, a.Side2);
        if (length < 1 || litLength < 1 || Math.Min(length, litLength) / Math.Max(length, litLength) < .75 ||
            ColorTipDetector.Distance(normal.Center, lit.Center) > Math.Max(3, width * .65)) return false;
        double dot = ((a.End2.X - a.End1.X) * (b.End2.X - b.End1.X) +
            (a.End2.Y - a.End1.Y) * (b.End2.Y - b.End1.Y)) / (length * litLength);
        return Math.Abs(dot) >= .966;
    }

    private sealed record PairMatch(ColorTipObservation Observation, double SpacingRatio);
    private sealed record StripPair(int First, int Second, PairMatch Match);

    private static List<StripPair> FindPairs(IReadOnlyList<ColorTipObservation> strips, double? expectedSpacing)
    {
        var pairs = new List<StripPair>();
        for (int first = 0; first < strips.Count; first++)
        for (int second = first + 1; second < strips.Count; second++)
            if (MatchPair(strips[first], strips[second], expectedSpacing) is { } match)
                pairs.Add(new(first, second, match));
        return pairs;
    }

    private static double StripAspect(ColorTipObservation strip) => strip.Bar is { } bar
        ? ColorTipDetector.Distance(bar.End1, bar.End2) / ColorTipDetector.Distance(bar.Side1, bar.Side2) : 0;

    private static PairMatch? MatchPair(ColorTipObservation first, ColorTipObservation second, double? expectedSpacing)
    {
        if (first.Bar is not { } a || second.Bar is not { } b) return null;
        double al = ColorTipDetector.Distance(a.End1, a.End2), bl = ColorTipDetector.Distance(b.End1, b.End2);
        double aw = ColorTipDetector.Distance(a.Side1, a.Side2), bw = ColorTipDetector.Distance(b.Side1, b.Side2);
        if (al < 1 || bl < 1 || aw < 1 || bw < 1 || al / aw < 2.4 || bl / bw < 2.4 ||
            Math.Min(al, bl) / Math.Max(al, bl) < .70 || Math.Min(aw, bw) / Math.Max(aw, bw) < .50 ||
            Math.Min(first.AreaPixels, second.AreaPixels) / Math.Max(first.AreaPixels, second.AreaPixels) < .55) return null;
        double ax = (a.End2.X - a.End1.X) / al, ay = (a.End2.Y - a.End1.Y) / al;
        double bx = (b.End2.X - b.End1.X) / bl, by = (b.End2.Y - b.End1.Y) / bl;
        double alignment = ax * bx + ay * by;
        if (Math.Abs(alignment) < .966) return null; // About fifteen degrees, independent of axis sign.
        if (alignment < 0) { bx = -bx; by = -by; }
        double ux = ax + bx, uy = ay + by, norm = Math.Sqrt(ux * ux + uy * uy);
        ux /= norm; uy /= norm;
        double vx = -uy, vy = ux, length = (al + bl) * .5;
        double dx = second.Center.X - first.Center.X, dy = second.Center.Y - first.Center.Y;
        double along = Math.Abs(dx * ux + dy * uy), across = Math.Abs(dx * vx + dy * vy);
        double spacing = across / length;
        if (along > length * .22 + 1 || across < Math.Max(aw, bw) * 1.35 ||
            spacing is < .12 or > .80 || expectedSpacing is { } expected &&
            Math.Abs(spacing - expected) > Math.Max(.04, expected * .27)) return null;

        var corners = Corners(a).Concat(Corners(b)).ToArray();
        double minU = corners.Min(p => p.X * ux + p.Y * uy), maxU = corners.Max(p => p.X * ux + p.Y * uy);
        double minV = corners.Min(p => p.X * vx + p.Y * vy), maxV = corners.Max(p => p.X * vx + p.Y * vy);
        double midU = (minU + maxU) * .5, midV = (minV + maxV) * .5;
        PixelPoint Point(double u, double v) => new(u * ux + v * vx, u * uy + v * vy);
        double area = first.AreaPixels + second.AreaPixels;
        var observation = new ColorTipObservation(Point(midU, midV), Math.Sqrt(area / Math.PI), area,
            (first.Score + second.Score) * .5)
        {
            Bar = new(Point(minU, midV), Point(maxU, midV), Point(midU, minV), Point(midU, maxV))
        };
        return new(observation, spacing);

        static IEnumerable<PixelPoint> Corners(BlackBarGeometry bar)
        {
            double hx = (bar.Side2.X - bar.Side1.X) * .5, hy = (bar.Side2.Y - bar.Side1.Y) * .5;
            yield return new(bar.End1.X + hx, bar.End1.Y + hy);
            yield return new(bar.End1.X - hx, bar.End1.Y - hy);
            yield return new(bar.End2.X + hx, bar.End2.Y + hy);
            yield return new(bar.End2.X - hx, bar.End2.Y - hy);
        }
    }

    private sealed record ComponentShape(ColorTipObservation Observation, double Aspect);

    private static ComponentShape? Component(Point[] contour, BlackImage image, BlackTipProfile profile,
        double? expectedArea, double valueAllowance = 0)
    {
        double area = Math.Abs(Cv2.ContourArea(contour));
        if (area < 12 || area > image.FrameWidth * (double)image.FrameHeight * .025) return null;
        Rect bounds = Cv2.BoundingRect(contour);
        if (bounds.X < 1 || bounds.Y < 1 || bounds.Right >= image.Width - 1 || bounds.Bottom >= image.Height - 1)
            return null;
        RotatedRect rotated = Cv2.MinAreaRect(contour);
        double longSide = Math.Max(rotated.Size.Width, rotated.Size.Height),
            shortSide = Math.Min(rotated.Size.Width, rotated.Size.Height);
        if (shortSide < 2.4 || longSide / shortSide > 16.4) return null;
        double aspect = longSide / shortSide;
        double fill = area / Math.Max(1, longSide * shortSide);
        double hullArea = Math.Abs(Cv2.ContourArea(Cv2.ConvexHull(contour)));
        // A half-pixel change at the edge is substantial for a four-pixel-wide printed
        // bar, especially beneath bright projected artwork. Allow that bounded raster
        // uncertainty only for thin bars, while still rejecting deep concavities.
        double edgeAreaAllowance = shortSide <= 6 && aspect >= 6 ? longSide * .5 : 0;
        if (fill < .54 || area / Math.Max(1, hullArea) < .60 ||
            area + edgeAreaAllowance < hullArea * .74) return null;
        if (expectedArea is not null && (aspect < Math.Max(1.45, profile.AspectRatio / 1.8) ||
            aspect > profile.AspectRatio * 1.8)) return null;
        double rawArea = area * image.ScaleX * image.ScaleY;
        if (expectedArea is { } expected && (rawArea < expected * .45 || rawArea > expected * 2.2)) return null;
        Moments moments = Cv2.Moments(contour);
        if (moments.M00 <= 0) return null;
        double cx = moments.M10 / moments.M00, cy = moments.M01 / moments.M00;
        // Hole contours are children; this explicit dark-core check also rejects an enclosing
        // ring whose geometric centroid falls in its light interior.
        if (!image.DarkCore(cx, cy, profile, valueAllowance)) return null;
        double contrast = image.Contrast(contour, bounds);
        if (contrast < 13) return null;
        double sizeScore = expectedArea is { } desired ? Math.Exp(-Math.Abs(Math.Log(rawArea / desired))) : 1;
        double shapeScore = expectedArea is null ? fill : Math.Exp(-Math.Abs(Math.Log(aspect / profile.AspectRatio))) * fill;
        double score = Math.Clamp(.45 * sizeScore + .40 * shapeScore + .15 * Math.Min(1, contrast / 55), 0, 1);
        // Fit in raw pixels after scaling each coordinate independently. Carrying a reduced
        // image's angle through nonidentical X/Y scales would skew both orientation and width.
        RotatedRect rawRectangle = Cv2.MinAreaRect(contour.Select(point =>
            new Point2f((float)image.ToRawX(point.X), (float)image.ToRawY(point.Y))).ToArray());
        Point2f[] corners = rawRectangle.Points();
        PixelPoint Midpoint(int first, int second) => new(
            (corners[first].X + corners[second].X) * .5, (corners[first].Y + corners[second].Y) * .5);
        var acrossFirstEdges = (First: Midpoint(0, 1), Second: Midpoint(2, 3));
        var acrossOtherEdges = (First: Midpoint(1, 2), Second: Midpoint(3, 0));
        bool firstIsLong = ColorTipDetector.Distance(acrossFirstEdges.First, acrossFirstEdges.Second) >=
            ColorTipDetector.Distance(acrossOtherEdges.First, acrossOtherEdges.Second);
        var ends = firstIsLong ? acrossFirstEdges : acrossOtherEdges;
        var sides = firstIsLong ? acrossOtherEdges : acrossFirstEdges;
        return new(new(new(image.ToRawX(cx), image.ToRawY(cy)), Math.Sqrt(rawArea / Math.PI), rawArea, score)
        {
            Bar = new(ends.First, ends.Second, sides.First, sides.Second)
        }, aspect);
    }

    private static readonly Mat CrossKernel = Cv2.GetStructuringElement(MorphShapes.Cross, new Size(3, 3));

    /// <summary>The HSV value (max of R, G and B) and luminance of the searched area. Only
    /// value is needed to find black ink, so the full HSV conversion is never computed for
    /// detection. Buffers are pooled: per-frame multi-megabyte arrays forced repeated
    /// full garbage collections while a game was running.</summary>
    private sealed class BlackImage : IDisposable
    {
        private readonly Mat _source, _reduced, _value;
        public int Width { get; }
        public int Height { get; }
        // The reduced dimensions of the whole camera frame, for frame-relative limits.
        public int FrameWidth { get; }
        public int FrameHeight { get; }
        public double ScaleX { get; }
        public double ScaleY { get; }
        // The raw camera pixel at image pixel (0, 0).
        public double OriginX { get; }
        public double OriginY { get; }
        public byte[] Value { get; }
        public byte[] Gray { get; }

        public BlackImage(int width, int height, int stride, byte[] bgra, Rect? crop = null)
        {
            _source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
            var area = crop ?? new Rect(0, 0, width, height);
            double reduction = Math.Min(1, MaximumDimension / (double)Math.Max(width, height));
            FrameWidth = reduction < 1 ? Math.Max(1, (int)Math.Round(width * reduction)) : width;
            FrameHeight = reduction < 1 ? Math.Max(1, (int)Math.Round(height * reduction)) : height;
            // A full-resolution crop is only a view of the caller's pinned camera buffer.
            var view = new Mat(_source, area);
            if (reduction < 1)
            {
                _reduced = new Mat();
                Cv2.Resize(view, _reduced, new Size(Math.Max(1, (int)Math.Round(area.Width * reduction)),
                    Math.Max(1, (int)Math.Round(area.Height * reduction))), interpolation: InterpolationFlags.Area);
                view.Dispose();
            }
            else _reduced = view;
            Width = _reduced.Width; Height = _reduced.Height;
            ScaleX = area.Width / (double)Width; ScaleY = area.Height / (double)Height;
            OriginX = area.X; OriginY = area.Y;
            _value = new Mat();
            Cv2.Split(_reduced, out Mat[] channels);
            try
            {
                // HSV value is exactly max(B, G, R) for 8-bit images.
                Cv2.Max(channels[0], channels[1], _value);
                Cv2.Max(_value, channels[2], _value);
            }
            finally { foreach (var channel in channels) channel.Dispose(); }
            using Mat gray = new();
            Cv2.CvtColor(_reduced, gray, ColorConversionCodes.BGRA2GRAY);
            int pixels = Width * Height;
            Value = ArrayPool<byte>.Shared.Rent(pixels);
            Gray = ArrayPool<byte>.Shared.Rent(pixels);
            Marshal.Copy(_value.Data, Value, 0, pixels);
            Marshal.Copy(gray.Data, Gray, 0, pixels);
        }

        public double ToImageX(double rawX) => (rawX - OriginX) / ScaleX;
        public double ToImageY(double rawY) => (rawY - OriginY) / ScaleY;
        public double ToRawX(double imageX) => OriginX + imageX * ScaleX;
        public double ToRawY(double imageY) => OriginY + imageY * ScaleY;

        /// <summary>OpenCV's 8-bit HSV saturation of one pixel; learning reads only a 3×3 patch.</summary>
        public byte Saturation(int x, int y)
        {
            using var pixel = new Mat(_reduced, new Rect(x, y, 1, 1));
            using Mat bgr = new(), hsv = new();
            Cv2.CvtColor(pixel, bgr, ColorConversionCodes.BGRA2BGR);
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            return hsv.At<Vec3b>(0, 0).Item1;
        }

        private static double MaximumValue(BlackTipProfile profile, double valueAllowance) =>
            Math.Min(.50, Math.Clamp(profile.Value * 1.7 + .07, .18, .42) + valueAllowance);

        private bool IsDark(int pixel, BlackTipProfile profile, double valueAllowance = 0) =>
            Value[pixel] / 255.0 <= MaximumValue(profile, valueAllowance);

        public Mat Mask(BlackTipProfile profile, double valueAllowance = 0, Rect? region = null)
        {
            Rect bounds = region ?? new(0, 0, Width, Height);
            // The largest 8-bit value that IsDark accepts, so the vectorized threshold
            // selects exactly the same pixels as the per-pixel test.
            double maximum = MaximumValue(profile, valueAllowance);
            int threshold = -1;
            for (int value = 0; value < 256 && value / 255.0 <= maximum; value++) threshold = value;
            using var source = new Mat(_value, bounds);
            Mat mask = new();
            Cv2.Threshold(source, mask, threshold, 255, ThresholdTypes.BinaryInv);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, CrossKernel);
            return mask;
        }

        public Rect SearchRegion(BlackTipSearchHint hint)
        {
            double radius = Math.Clamp(ColorTipDetector.Distance(hint.Marker.Bar!.End1, hint.Marker.Bar.End2) * 1.8, 48, 192);
            int left = Math.Clamp((int)Math.Floor(ToImageX(hint.ExpectedCenter.X - radius)), 0, Width - 1);
            int top = Math.Clamp((int)Math.Floor(ToImageY(hint.ExpectedCenter.Y - radius)), 0, Height - 1);
            int right = Math.Clamp((int)Math.Ceiling(ToImageX(hint.ExpectedCenter.X + radius)), left + 1, Width);
            int bottom = Math.Clamp((int)Math.Ceiling(ToImageY(hint.ExpectedCenter.Y + radius)), top + 1, Height);
            return new(left, top, right - left, bottom - top);
        }

        public double LocalAllowance(BlackTipProfile profile, Rect region)
        {
            var histogram = new int[256];
            int count = 0, step = Math.Max(1, Math.Max(region.Width, region.Height) / 50);
            for (int y = region.Top; y < region.Bottom; y += step)
            for (int x = region.Left; x < region.Right; x += step)
            { histogram[Value[y * Width + x]]++; count++; }
            int percentile = 0, total = 0;
            for (; percentile < 255; percentile++)
            { total += histogram[percentile]; if (total >= count * .80) break; }
            double baseline = Math.Clamp(profile.Value * 1.7 + .07, .18, .42);
            // Ink must remain appreciably darker than its local illuminated surroundings.
            return Math.Clamp(percentile / 255.0 * .72 - baseline, 0, .12);
        }

        public bool DarkCore(double x, double y, BlackTipProfile profile, double valueAllowance = 0)
        {
            int cx = (int)Math.Round(x), cy = (int)Math.Round(y), count = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (IsDark((cy + dy) * Width + cx + dx, profile, valueAllowance)) count++;
            return count >= 7;
        }

        public double Contrast(Point[] contour, Rect bounds)
        {
            double core = 0, surround = 0;
            int coreCount = 0, surroundCount = 0;
            int margin = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 2, 3, 10);
            int left = Math.Max(0, bounds.Left - margin), right = Math.Min(Width - 1, bounds.Right + margin);
            int top = Math.Max(0, bounds.Top - margin), bottom = Math.Min(Height - 1, bounds.Bottom + margin);
            // Sampling is bounded even for a large camera frame. The surrounding ring supplies
            // a local contrast test, avoiding acceptance of a whole uniformly dark patch.
            int step = Math.Max(1, Math.Max(right - left, bottom - top) / 70);
            for (int y = top; y <= bottom; y += step)
            for (int x = left; x <= right; x += step)
            {
                double distance = Cv2.PointPolygonTest(contour, new Point2f(x, y), true);
                if (distance >= 1) { core += Gray[y * Width + x]; coreCount++; }
                else if (distance < -1 && distance > -margin)
                { surround += Gray[y * Width + x]; surroundCount++; }
            }
            return coreCount < 3 || surroundCount < 8 ? 0 : surround / surroundCount - core / coreCount;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Value);
            ArrayPool<byte>.Shared.Return(Gray);
            _value.Dispose();
            _reduced.Dispose();
            _source.Dispose();
        }
    }

    private static void ValidateFrame(int width, int height, int stride, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
    }
}
