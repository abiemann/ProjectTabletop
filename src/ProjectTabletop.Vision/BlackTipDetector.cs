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

public sealed record BlackTipLearningOptions(IReadOnlyList<MarkerProjectionFrame>? ProjectionFrames = null,
    DateTimeOffset FrameTime = default);

/// <summary>A recent measured full marker and a bounded prediction used only to locate an
/// additional local search. It never supplies an unmeasured controller position.</summary>
public sealed record BlackTipSearchHint(MarkerTipObservation Marker, PixelPoint ExpectedCenter,
    DateTimeOffset LastFullFrame);

/// <summary>
/// Detects a solid black crossbar or two parallel bars as one marker. Learned
/// size, elongation and pair spacing reject unrelated marks; fresh scene references veto the app's
/// own black artwork. Returns the common measured-component contract for MarkerTipTracker.
/// Callers must partition identical bats by calibrated player half BEFORE temporal association.
/// </summary>
public static partial class BlackTipDetector
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
        double learningAllowance = 0;
        ComponentShape? SelectClicked() => contours.Where((contour, index) => hierarchy[index].Parent < 0 &&
                Cv2.PointPolygonTest(contour, new Point2f(cx, cy), false) >= 0)
            .Select(contour => Component(contour, image, seed, null, learningAllowance))
            .Where(item => item is not null).OrderBy(item => item!.Observation.AreaPixels).FirstOrDefault();
        var selected = SelectClicked();
        if (selected is null || selected.Aspect < 1.8)
        {
            learningAllowance = image.DarkCoreAllowance(seed);
            using Mat coreMask = image.Mask(seed, learningAllowance);
            Cv2.FindContours(coreMask, out contours, out hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
            selected = SelectClicked();
        }
        if (selected is null || selected.Aspect < 1.8)
            throw new InvalidOperationException("The selected mark is not a separate black crossbar. Show the whole long bar and click its middle.");
        var result = seed with
        {
            NormalizedArea = selected.Observation.AreaPixels / (width * (double)height),
            AspectRatio = selected.Aspect
        };
        if (!result.IsValid) throw new InvalidOperationException("The crossbar is outside the supported size or shape. Make its width about 2–16 times its thickness.");
        MarkerProjectionMatcher? projection = null;
        if (options?.ProjectionFrames is { } frames)
        {
            projection = new MarkerProjectionMatcher(frames, options.FrameTime);
            if (!projection.Ready)
                throw new InvalidOperationException("Wait for a current projected-board reference, then click the physical crossbar again.");
            if (!projection.ContainsBoardPoint(selected.Observation.Center))
                throw new InvalidOperationException("Place the black marker on the projected board, then click inside one bar again.");
            if (!Physical(projection, selected.Observation, image))
                throw new InvalidOperationException("That mark is part of the projected picture. Click inside a physical black bar.");
        }
        var strips = contours.Where((_, index) => hierarchy[index].Parent < 0)
            .Select(contour => Component(contour, image, result, selected.Observation.AreaPixels, learningAllowance))
            .Where(item => item is not null).Select(item => item!.Observation)
            .OrderBy(item => MarkerTipGeometry.Distance(item.Center, selected.Observation.Center)).Take(128)
            .Where(item => projection is null || Physical(projection, item, image)).ToArray();
        var pairs = FindPairs(strips, null);
        if (learningAllowance < 0)
            pairs.RemoveAll(pair => !image.HasSeparatingGap(strips[pair.First], strips[pair.Second], result));
        int selectedIndex = Array.FindIndex(strips, item =>
            MarkerTipGeometry.Distance(item.Center, selected.Observation.Center) < 1);
        var touching = pairs.Where(pair => pair.First == selectedIndex || pair.Second == selectedIndex).ToArray();
        if (touching.Length == 0 && learningAllowance == 0)
        {
            // Dark projected cracks can join the ink and inflate its bounding rectangle.
            // Recover the two darker cores, keeping the same pair geometry and artwork veto.
            var cores = ExtractStrips(image, result, selected.Observation.AreaPixels,
                selected.Observation.Center, projection, new(), image.DarkCoreAllowance(result));
            int coreIndex = Array.FindIndex(cores, item => SameStrip(selected.Observation, item));
            var corePairs = FindPairs(cores, null);
            corePairs.RemoveAll(pair => !image.HasSeparatingGap(cores[pair.First], cores[pair.Second], result));
            var coreTouching = corePairs.Where(pair => pair.First == coreIndex || pair.Second == coreIndex).ToArray();
            if (coreTouching.Length > 0)
            {
                strips = cores;
                pairs = corePairs;
                touching = coreTouching;
            }
        }
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
        if (learningAllowance < 0 && result.BarCount != 2)
            throw new InvalidOperationException("The selected mark is not a separate black pair. Show both complete bars and click either middle.");
        if (!result.IsValid)
            throw new InvalidOperationException("The marker is outside the supported size or shape. Show the complete bar or parallel pair.");
        return result;
    }

    public static MarkerTipDetectionResult Detect(int width, int height, int stride, byte[] bgra,
        BlackTipProfile profile, MarkerTipDetectionOptions? options = null) =>
        DetectEach(width, height, stride, bgra, [profile], options)[0];

    /// <summary>Detects each learned bar in one camera frame, in profile order. The colour
    /// conversion and projected-scene reference are shared; only the dark threshold and the
    /// learned size and shape differ per profile.</summary>
    /// <param name="searchArea">Optional raw-camera polygon containing every possible marker
    /// centre, such as the calibrated grass. Only its bounds plus a marker-sized margin are
    /// converted and searched; the camera's surroundings cannot add work or candidates.</param>
    public static IReadOnlyList<MarkerTipDetectionResult> DetectEach(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<BlackTipProfile> profiles, MarkerTipDetectionOptions? options = null,
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
        MarkerProjectionMatcher? projection = options?.ProjectionFrames is null ? null :
            new(options.ProjectionFrames, options.FrameTime);
        if (projection is { Ready: false })
            return profiles.Select(_ => new MarkerTipDetectionResult([], "waiting-for-projected-bat-reference")).ToArray();
        if (profiles.Count == 0) return [];
        using var image = new BlackImage(width, height, stride, bgra, SearchBounds(searchArea, profiles, width, height));
        // Both players usually see the same physical strips. The projected-scene check
        // depends only on the measured strip, so compare each one at most once per frame.
        var vetoes = new Dictionary<MarkerTipObservation, bool>();
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

    private static bool Physical(MarkerProjectionMatcher projection, MarkerTipObservation strip, BlackImage image) =>
        projection.IsPhysicalBlackBarCandidate(strip, image.Gray, image.Width, image.Height,
            image.ScaleX, image.ScaleY, image.OriginX, image.OriginY);

    private static MarkerTipDetectionResult Detect(BlackImage image, BlackTipProfile profile, double framePixels,
        PixelPoint? preferred, MarkerProjectionMatcher? projection, BlackTipSearchHint? hint, DateTimeOffset frameTime,
        Dictionary<MarkerTipObservation, bool> vetoes)
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
            var claimed = pairs.Where(pair => partners[pair.First] == 1 && partners[pair.Second] == 1)
                .Select(pair => (First: strips[pair.First], Second: strips[pair.Second], Marker: pair.Match.Observation)).ToList();
            string source = "global";
            MarkerTipObservation[] coreAnchors = [];
            if (ambiguous.Count == 0 && (unambiguous.Count == 0 || partners.Any(count => count == 0)))
            {
                // One additional threshold over the existing value image separates ink
                // from touching scenery. Never relax shape, spacing or physical evidence.
                var cores = ExtractStrips(image, profile, expectedArea, preferred, projection, vetoes,
                    image.DarkCoreAllowance(profile));
                // When lighting joins both strips to the scenery, neither earlier
                // mask leaves an anchor. Try one stricter core over the same value
                // image; never replace an existing physical strip or ambiguous pair.
                if (strips.Length == 0 && cores.Length == 0)
                    cores = ExtractStrips(image, profile, expectedArea, preferred, projection, vetoes,
                        image.DeepCoreAllowance(profile));
                coreAnchors = cores;
                var corePairs = FindPairs(cores, profile.SpacingRatio);
                corePairs.RemoveAll(pair => !image.HasSeparatingGap(cores[pair.First], cores[pair.Second], profile) ||
                    unambiguous.Any(existing => SameStrip(existing, pair.Match.Observation)) ||
                    strips.Where((_, index) => partners[index] > 0).Any(strip =>
                        SameStrip(strip, cores[pair.First]) || SameStrip(strip, cores[pair.Second])));
                var corePartners = new int[cores.Length];
                foreach (var pair in corePairs) { corePartners[pair.First]++; corePartners[pair.Second]++; }
                ambiguous.AddRange(corePairs.Where(pair => corePartners[pair.First] > 1 || corePartners[pair.Second] > 1)
                    .Select(pair => pair.Match.Observation.Center));
                var recovered = corePairs.Where(pair => corePartners[pair.First] == 1 && corePartners[pair.Second] == 1)
                    .Select(pair => pair.Match.Observation).ToArray();
                unambiguous.AddRange(recovered);
                claimed.AddRange(corePairs.Where(pair => corePartners[pair.First] == 1 && corePartners[pair.Second] == 1)
                    .Select(pair => (cores[pair.First], cores[pair.Second], pair.Match.Observation)));
                if (recovered.Length > 0)
                    for (int index = 0; index < strips.Length; index++)
                        if (corePairs.Any(pair => corePartners[pair.First] == 1 && corePartners[pair.Second] == 1 &&
                            (SameStrip(strips[index], cores[pair.First]) || SameStrip(strips[index], cores[pair.Second]))))
                            partners[index] = 1;
                if (corePairs.Count > 0) MergeStrips(supporting, cores);
                if (recovered.Length > 0) source = "dark-core";
            }
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
                claimed.AddRange(supported.Where(pair => litPartners[pair.First] == 1 && litPartners[pair.Second] == 1)
                    .Select(pair => (lit[pair.First], lit[pair.Second], pair.Match.Observation)));
            }
            if (ambiguous.Count == 0)
            {
                // Projected cracks can attach to ink while bright pebbles fragment its
                // interior. Measure actual opposing edges beside a fresh physical strip;
                // neither the connected contour nor the predicted position is the marker.
                var anchors = strips.Where((strip, index) => partners[index] == 0 &&
                    !claimed.Any(pair => SameStrip(pair.First, strip) || SameStrip(pair.Second, strip))).ToList();
                // A darker mask can reveal valid physical strips without a pair: small
                // attached cracks can still spoil their measured spacing or width. Keep
                // those fresh strips as edge anchors instead of discarding their evidence.
                // Earlier pairs retain ownership, and ordinary anchors keep precedence.
                int ordinaryAnchorCount = anchors.Count;
                foreach (var strip in coreAnchors)
                    if (!claimed.Any(pair => SameStrip(pair.First, strip) || SameStrip(pair.Second, strip)) &&
                        !anchors.Any(anchor => SameStrip(anchor, strip))) anchors.Add(strip);
                if (anchors.Count is > 0 and <= 8)
                {
                    var fitted = new List<MarkerTipObservation>();
                    HashSet<MarkerTipObservation>? ordinaryFits = ordinaryAnchorCount > 0 &&
                        ordinaryAnchorCount < anchors.Count ? [] : null;
                    bool complete = true;
                    for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
                    {
                        if (image.FitEdges(anchors[anchorIndex], profile, expectedArea) is not { } measured)
                        { complete = false; break; }
                        if (measured.Count > 256 - fitted.Count) { complete = false; break; }
                        fitted.AddRange(measured);
                        if (anchorIndex < ordinaryAnchorCount) ordinaryFits?.UnionWith(measured);
                    }
                    var aliases = new Dictionary<MarkerTipObservation, IReadOnlyList<MarkerTipObservation>>();
                    if (complete) fitted = ConsolidatePreferredStrips(fitted, ordinaryFits, out aliases);
                    bool SameMeasuredStrip(MarkerTipObservation strip, MarkerTipObservation representative) =>
                        SameStrip(strip, representative) || aliases.TryGetValue(representative, out var group) &&
                        group.Any(measurement => SameStrip(strip, measurement));
                    if (complete && fitted.Count <= 24)
                    {
                        fitted.RemoveAll(strip => projection is not null && !Physical(projection, strip, image));
                        // Include earlier pairs in this ownership graph. A recovered third
                        // strip must invalidate a competing pairing from any previous pass.
                        foreach (var pair in claimed)
                        foreach (var strip in new[] { pair.First, pair.Second })
                            if (!fitted.Any(existing => SameMeasuredStrip(strip, existing))) fitted.Add(strip);
                        var edgePairs = FindPairs(fitted, profile.SpacingRatio);
                        edgePairs.RemoveAll(pair => !image.HasSeparatingGap(fitted[pair.First], fitted[pair.Second], profile));
                        var earlier = claimed.Select(pair => (Pair: pair,
                            First: fitted.FindIndex(strip => SameMeasuredStrip(pair.First, strip)),
                            Second: fitted.FindIndex(strip => SameMeasuredStrip(pair.Second, strip)))).ToArray();
                        foreach (var item in earlier)
                            if (!edgePairs.Any(pair => pair.First == Math.Min(item.First, item.Second) &&
                                pair.Second == Math.Max(item.First, item.Second)))
                                edgePairs.Add(new(Math.Min(item.First, item.Second), Math.Max(item.First, item.Second),
                                    new(item.Pair.Marker, profile.SpacingRatio)));
                        var edgePartners = new int[fitted.Count];
                        foreach (var pair in edgePairs) { edgePartners[pair.First]++; edgePartners[pair.Second]++; }
                        ambiguous.AddRange(edgePairs.Where(pair => edgePartners[pair.First] > 1 || edgePartners[pair.Second] > 1)
                            .Select(pair => pair.Match.Observation.Center));
                        foreach (var item in earlier)
                            if (edgePartners[item.First] > 1 || edgePartners[item.Second] > 1) unambiguous.Remove(item.Pair.Marker);
                        var recovered = edgePairs.Where(pair => edgePartners[pair.First] == 1 && edgePartners[pair.Second] == 1 &&
                            anchors.Any(anchor => SameMeasuredStrip(anchor, fitted[pair.First]) || SameMeasuredStrip(anchor, fitted[pair.Second])))
                            .Select(pair => pair.Match.Observation).ToArray();
                        unambiguous.AddRange(recovered);
                        if (recovered.Length > 0) { MergeStrips(supporting, fitted); source = "measured-edges"; }
                    }
                }
            }
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
                if (recovered.Length == 0 && local.Length > 0)
                {
                    // A projected red car can brighten one colour channel of black ink
                    // while its luminance and outline remain dark. Recover that strip
                    // only near a recently measured pair and beside a normally dark,
                    // physically validated partner. Never acquire coloured pairs this way.
                    var luminance = ExtractStrips(image, profile, expectedArea, hint.ExpectedCenter, projection,
                        vetoes, region: region, useLuminance: true);
                    var luminancePairs = FindPairs(luminance, profile.SpacingRatio);
                    var luminancePartners = new int[luminance.Length];
                    foreach (var pair in luminancePairs)
                    { luminancePartners[pair.First]++; luminancePartners[pair.Second]++; }
                    bool Anchored(StripPair pair) => local.Any(strip => SameStrip(strip, luminance[pair.First]) ||
                        SameStrip(strip, luminance[pair.Second]));
                    ambiguous.AddRange(luminancePairs.Where(pair => Anchored(pair) &&
                            (luminancePartners[pair.First] > 1 || luminancePartners[pair.Second] > 1))
                        .Select(pair => pair.Match.Observation.Center));
                    recovered = luminancePairs.Where(pair => Anchored(pair) &&
                            luminancePartners[pair.First] == 1 && luminancePartners[pair.Second] == 1)
                        .Select(pair => pair.Match.Observation).Where(item => NearHint(item, hint)).ToArray();
                    if (recovered.Length > 0) MergeStrips(supporting, luminance);
                }
                unambiguous.AddRange(recovered);
                if (recovered.Length > 0) source = "local-contrast";
            }
            var paired = (preferred is { } center
                ? unambiguous.OrderBy(item => MarkerTipGeometry.Distance(item.Center, center))
                : unambiguous.OrderByDescending(item => item.Score)).Take(16).ToArray();
            return new(paired, paired.Length == 0 ? "no-black-bar-pair" :
                paired.Length == 1 ? "black-bar-pair-candidate" : "multiple-black-bar-pairs")
            { SupportingBars = supporting, AmbiguousMarkerCenters = ambiguous, Source = source };
        }
        var accepted = strips.Take(16).ToArray();
        return new(accepted, accepted.Length == 0 ? "no-black-crossbar" :
            accepted.Length == 1 ? "black-crossbar-candidate" : "multiple-black-crossbars");
    }

    private static MarkerTipObservation[] ExtractStrips(BlackImage image, BlackTipProfile profile, double expectedArea,
        PixelPoint? preferred, MarkerProjectionMatcher? projection, Dictionary<MarkerTipObservation, bool> vetoes,
        double valueAllowance = 0, Rect? region = null, bool useLuminance = false)
    {
        using Mat mask = image.Mask(profile, valueAllowance, region, useLuminance);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple, region?.TopLeft);
        var candidates = contours.Where((_, index) => hierarchy[index].Parent < 0)
            .Where(contour => region is not { } bounds || contour.All(point => point.X > bounds.Left &&
                point.X < bounds.Right - 1 && point.Y > bounds.Top && point.Y < bounds.Bottom - 1))
            .Select(contour => Component(contour, image, profile, expectedArea, valueAllowance, useLuminance))
            .Where(item => item is not null).Select(item => item!.Observation);
        var ranked = preferred is { } point
            ? candidates.OrderBy(item => MarkerTipGeometry.Distance(item.Center, point)).ThenByDescending(item => item.Score)
            : candidates.OrderByDescending(item => item.Score);
        return ranked.Take(128).Where(item => projection is null ||
            (vetoes.TryGetValue(item, out bool physical) ? physical : vetoes[item] = Physical(projection, item, image)))
            .ToArray();
    }

    private static void MergeStrips(List<MarkerTipObservation> target, IEnumerable<MarkerTipObservation> additions)
    {
        foreach (var strip in additions)
        {
            int same = target.FindIndex(previous => SameStrip(previous, strip));
            if (same < 0) target.Add(strip);
            else if (strip.Score > target[same].Score) target[same] = strip;
        }
    }

    private static List<MarkerTipObservation> ConsolidateStrips(IReadOnlyList<MarkerTipObservation> measurements,
        out Dictionary<MarkerTipObservation, IReadOnlyList<MarkerTipObservation>> aliases) =>
        ConsolidatePreferredStrips(measurements, null, out aliases);

    private static List<MarkerTipObservation> ConsolidatePreferredStrips(IReadOnlyList<MarkerTipObservation> measurements,
        IReadOnlySet<MarkerTipObservation>? preferred,
        out Dictionary<MarkerTipObservation, IReadOnlyList<MarkerTipObservation>> aliases)
    {
        // Keep identity evidence until grouping is complete. A high-scoring short
        // subsection must not replace the full outline before another subsection
        // is compared, otherwise grouping depends on anchor/iteration order.
        var parents = Enumerable.Range(0, measurements.Count).ToArray();
        int Root(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
        for (int first = 0; first < measurements.Count; first++)
        for (int second = first + 1; second < measurements.Count; second++)
            if (SameStrip(measurements[first], measurements[second])) parents[Root(second)] = Root(first);
        aliases = measurements.Select((strip, index) => (Strip: strip, Group: Root(index)))
            .GroupBy(item => item.Group).Select(group => group.Select(item => item.Strip)
                // Extra core fits must not displace a successful ordinary measurement
                // by a fraction of a pixel and move its separating-gap samples onto a
                // pebble. Keep all aliases for ownership and third-strip ambiguity.
                .OrderByDescending(strip => preferred?.Contains(strip) == true)
                .ThenByDescending(strip => strip.Score).ThenByDescending(strip => strip.AreaPixels)
                .ThenBy(strip => strip.Center.X).ThenBy(strip => strip.Center.Y)
                .ThenBy(strip => strip.Bar!.End1.X).ThenBy(strip => strip.Bar!.End1.Y)
                .ThenBy(strip => strip.Bar!.End2.X).ThenBy(strip => strip.Bar!.End2.Y)
                .ThenBy(strip => strip.Bar!.Side1.X).ThenBy(strip => strip.Bar!.Side1.Y)
                .ThenBy(strip => strip.Bar!.Side2.X).ThenBy(strip => strip.Bar!.Side2.Y).ToArray())
            .ToDictionary(group => group[0], group => (IReadOnlyList<MarkerTipObservation>)group);
        return aliases.Keys.OrderBy(strip => strip.Center.X).ThenBy(strip => strip.Center.Y).ToList();
    }

    private static bool ValidHint(BlackTipSearchHint? hint, DateTimeOffset frameTime) =>
        hint?.Marker.Bar is not null && frameTime != default && hint.LastFullFrame != default &&
        frameTime >= hint.LastFullFrame && frameTime - hint.LastFullFrame <= TimeSpan.FromMilliseconds(350) &&
        double.IsFinite(hint.ExpectedCenter.X) && double.IsFinite(hint.ExpectedCenter.Y) &&
        double.IsFinite(hint.Marker.Center.X) && double.IsFinite(hint.Marker.Center.Y) &&
        MarkerTipGeometry.Distance(hint.Marker.Bar.End1, hint.Marker.Bar.End2) is >= 6 and <= 1000;

    private static bool NearHint(MarkerTipObservation item, BlackTipSearchHint hint)
    {
        if (item.Bar is not { } a || hint.Marker.Bar is not { } b) return false;
        double length = MarkerTipGeometry.Distance(b.End1, b.End2), measured = MarkerTipGeometry.Distance(a.End1, a.End2);
        double width = MarkerTipGeometry.Distance(b.Side1, b.Side2), measuredWidth = MarkerTipGeometry.Distance(a.Side1, a.Side2);
        double dot = ((a.End2.X - a.End1.X) * (b.End2.X - b.End1.X) +
            (a.End2.Y - a.End1.Y) * (b.End2.Y - b.End1.Y)) / (length * measured);
        return measured >= length * .78 && measured <= length * 1.28 &&
            measuredWidth >= width * .70 && measuredWidth <= width * 1.4 && Math.Abs(dot) >= .766 &&
            MarkerTipGeometry.Distance(item.Center, hint.ExpectedCenter) <= Math.Max(28, length * .8);
    }

    private static bool SameStrip(MarkerTipObservation normal, MarkerTipObservation lit)
    {
        if (normal.Bar is not { } a || lit.Bar is not { } b) return false;
        double al = MarkerTipGeometry.Distance(a.End1, a.End2), bl = MarkerTipGeometry.Distance(b.End1, b.End2);
        double aw = MarkerTipGeometry.Distance(a.Side1, a.Side2), bw = MarkerTipGeometry.Distance(b.Side1, b.Side2);
        if (Math.Min(al, bl) < 1 || Math.Min(aw, bw) < 1 ||
            Math.Min(al, bl) < Math.Max(al, bl) * .5 || Math.Min(aw, bw) < Math.Max(aw, bw) * .5) return false;
        double ax = (a.End2.X - a.End1.X) / al, ay = (a.End2.Y - a.End1.Y) / al;
        double bx = (b.End2.X - b.End1.X) / bl, by = (b.End2.Y - b.End1.Y) / bl;
        double dot = ax * bx + ay * by;
        if (Math.Abs(dot) < .966) return false;
        if (dot < 0) { bx = -bx; by = -by; }
        double ux = ax + bx, uy = ay + by, norm = Math.Sqrt(ux * ux + uy * uy);
        ux /= norm; uy /= norm;
        // Bright projected patches can leave a shorter measurement inside the same ink.
        // Compare symmetric outline spans, not their shifted dark-pixel mass centroids.
        double dx = (b.End1.X + b.End2.X - a.End1.X - a.End2.X) * .5;
        double dy = (b.End1.Y + b.End2.Y - a.End1.Y - a.End2.Y) * .5;
        double along = Math.Abs(dx * ux + dy * uy), across = Math.Abs(-dx * uy + dy * ux);
        double aSpan = al * Math.Abs(ax * ux + ay * uy), bSpan = bl * Math.Abs(bx * ux + by * uy);
        double overlap = Math.Min(Math.Min(aSpan, bSpan), (aSpan + bSpan) * .5 - along);
        return overlap >= Math.Min(aSpan, bSpan) * .85 && across <= Math.Max(2, Math.Min(aw, bw) * .5);
    }

    private sealed record PairMatch(MarkerTipObservation Observation, double SpacingRatio);
    private sealed record StripPair(int First, int Second, PairMatch Match);

    private static List<StripPair> FindPairs(IReadOnlyList<MarkerTipObservation> strips, double? expectedSpacing)
    {
        var pairs = new List<StripPair>();
        for (int first = 0; first < strips.Count; first++)
        for (int second = first + 1; second < strips.Count; second++)
            if (MatchPair(strips[first], strips[second], expectedSpacing) is { } match)
                pairs.Add(new(first, second, match));
        return pairs;
    }

    private static double StripAspect(MarkerTipObservation strip) => strip.Bar is { } bar
        ? MarkerTipGeometry.Distance(bar.End1, bar.End2) / MarkerTipGeometry.Distance(bar.Side1, bar.Side2) : 0;

    private static PairMatch? MatchPair(MarkerTipObservation first, MarkerTipObservation second, double? expectedSpacing)
    {
        if (first.Bar is not { } a || second.Bar is not { } b) return null;
        double al = MarkerTipGeometry.Distance(a.End1, a.End2), bl = MarkerTipGeometry.Distance(b.End1, b.End2);
        double aw = MarkerTipGeometry.Distance(a.Side1, a.Side2), bw = MarkerTipGeometry.Distance(b.Side1, b.Side2);
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
        // Rasterized edges can inflate the fitted thickness by a fraction of a pixel.
        // Keep that uncertainty bounded to half a raw camera pixel, independent of scale.
        if (along > length * .22 + 1 || across + .5 < Math.Max(aw, bw) * 1.35 ||
            spacing is < .12 or > .80 || expectedSpacing is { } expected &&
            Math.Abs(spacing - expected) > Math.Max(.04, expected * .27)) return null;

        var corners = Corners(a).Concat(Corners(b)).ToArray();
        double minU = corners.Min(p => p.X * ux + p.Y * uy), maxU = corners.Max(p => p.X * ux + p.Y * uy);
        double minV = corners.Min(p => p.X * vx + p.Y * vy), maxV = corners.Max(p => p.X * vx + p.Y * vy);
        double midU = (minU + maxU) * .5, midV = (minV + maxV) * .5;
        PixelPoint Point(double u, double v) => new(u * ux + v * vx, u * uy + v * vy);
        double area = first.AreaPixels + second.AreaPixels;
        var observation = new MarkerTipObservation(Point(midU, midV), Math.Sqrt(area / Math.PI), area,
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

    private sealed record ComponentShape(MarkerTipObservation Observation, double Aspect);

    private static ComponentShape? Component(Point[] contour, BlackImage image, BlackTipProfile profile,
        double? expectedArea, double valueAllowance = 0, bool useLuminance = false)
    {
        double area = Math.Abs(Cv2.ContourArea(contour));
        if (area < 12 || area > image.FrameWidth * (double)image.FrameHeight * .025) return null;
        double rawArea = area * image.ScaleX * image.ScaleY;
        // Reject tiny scenery before computing its rotated rectangle and convex hull,
        // especially when the darker recovery pass separates many small pebble seams.
        if (expectedArea is { } expected && (rawArea < expected * .45 || rawArea > expected * 2.2)) return null;
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
        Moments moments = Cv2.Moments(contour);
        if (moments.M00 <= 0) return null;
        double cx = moments.M10 / moments.M00, cy = moments.M01 / moments.M00;
        // Hole contours are children; this explicit dark-core check also rejects an enclosing
        // ring whose geometric centroid falls in its light interior.
        if (!image.DarkCore(cx, cy, profile, valueAllowance, useLuminance)) return null;
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
        bool firstIsLong = MarkerTipGeometry.Distance(acrossFirstEdges.First, acrossFirstEdges.Second) >=
            MarkerTipGeometry.Distance(acrossOtherEdges.First, acrossOtherEdges.Second);
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

        public double DarkCoreAllowance(BlackTipProfile profile) => -Math.Min(.10, MaximumValue(profile, 0) * .28);

        public double DeepCoreAllowance(BlackTipProfile profile) => -MaximumValue(profile, 0) * .50;

        public bool HasSeparatingGap(MarkerTipObservation first, MarkerTipObservation second, BlackTipProfile profile)
        {
            // A lighter stripe inside one black rectangle must not split it into a new
            // controller. Sample at the original threshold, allowing projected shade
            // across part of a real gap only when three clear samples span its length.
            var bar = first.Bar!;
            var other = second.Bar!;
            // Use the same shared axis as pairing, so strip order and opposite
            // endpoint directions cannot move the gap samples onto different pixels.
            double firstLength = MarkerTipGeometry.Distance(bar.End1, bar.End2);
            double secondLength = MarkerTipGeometry.Distance(other.End1, other.End2);
            double ax = (bar.End2.X - bar.End1.X) / firstLength, ay = (bar.End2.Y - bar.End1.Y) / firstLength;
            double bx = (other.End2.X - other.End1.X) / secondLength, by = (other.End2.Y - other.End1.Y) / secondLength;
            if (ax * bx + ay * by < 0) { bx = -bx; by = -by; }
            double ux = ax + bx, uy = ay + by;
            double norm = Math.Sqrt(ux * ux + uy * uy), meanLength = (firstLength + secondLength) * .5;
            double dx = ux / norm * meanLength, dy = uy / norm * meanLength;
            double midX = (first.Center.X + second.Center.X) * .5;
            double midY = (first.Center.Y + second.Center.Y) * .5;
            int clear = 0, firstClear = 4, lastClear = -4;
            for (int sample = -3; sample <= 3; sample++)
            {
                int x = (int)Math.Round(ToImageX(midX + dx * sample * .1));
                int y = (int)Math.Round(ToImageY(midY + dy * sample * .1));
                if (x >= 0 && x < Width && y >= 0 && y < Height && !IsDark(y * Width + x, profile))
                { clear++; firstClear = Math.Min(firstClear, sample); lastClear = sample; }
            }
            if (clear >= 3 && lastClear - firstClear >= 3) return true;

            // A dimmer part of the board can put the real surface below the learned
            // black threshold. It still separates the ink if it has substantial
            // contrast with both strips and resembles their darker outer surround.
            // A faint stripe inside one dark block lacks that surrounding contrast.
            double acrossX = second.Center.X - first.Center.X, acrossY = second.Center.Y - first.Center.Y;
            double distance = Math.Sqrt(acrossX * acrossX + acrossY * acrossY);
            if (distance <= 0) return false;
            acrossX /= distance; acrossY /= distance;
            double firstOutside = MarkerTipGeometry.Distance(bar.Side1, bar.Side2) * .75;
            double secondOutside = MarkerTipGeometry.Distance(second.Bar!.Side1, second.Bar.Side2) * .75;
            clear = 0; firstClear = 4; lastClear = -4;
            for (int sample = -3; sample <= 3; sample++)
            {
                double offsetX = dx * sample * .1, offsetY = dy * sample * .1;
                int gap = SampleValue(midX + offsetX, midY + offsetY);
                if (gap < 0) continue;
                if (gap / 255.0 <= MaximumValue(profile, 0))
                {
                    int firstInk = SampleValue(first.Center.X + offsetX, first.Center.Y + offsetY);
                    int secondInk = SampleValue(second.Center.X + offsetX, second.Center.Y + offsetY);
                    int firstFlank = SampleValue(first.Center.X + offsetX - acrossX * firstOutside,
                        first.Center.Y + offsetY - acrossY * firstOutside);
                    int secondFlank = SampleValue(second.Center.X + offsetX + acrossX * secondOutside,
                        second.Center.Y + offsetY + acrossY * secondOutside);
                    if (firstInk < 0 || secondInk < 0 || firstFlank < 0 || secondFlank < 0) continue;
                    int ink = Math.Max(firstInk, secondInk), outer = Math.Min(firstFlank, secondFlank);
                    if (outer - ink < 20 || gap - ink < Math.Max(20, (outer - ink) * .60)) continue;
                }
                clear++; firstClear = Math.Min(firstClear, sample); lastClear = sample;
            }
            return clear >= 3 && lastClear - firstClear >= 3;

            int SampleValue(double rawX, double rawY)
            {
                int x = (int)Math.Round(ToImageX(rawX)), y = (int)Math.Round(ToImageY(rawY));
                return x >= 0 && x < Width && y >= 0 && y < Height ? Value[y * Width + x] : -1;
            }
        }

        private bool IsDark(int pixel, BlackTipProfile profile, double valueAllowance = 0, bool useLuminance = false) =>
            (useLuminance ? Gray[pixel] : Value[pixel]) / 255.0 <= MaximumValue(profile, valueAllowance);

        public Mat Mask(BlackTipProfile profile, double valueAllowance = 0, Rect? region = null, bool useLuminance = false)
        {
            Rect bounds = region ?? new(0, 0, Width, Height);
            // The largest 8-bit value that IsDark accepts, so the vectorized threshold
            // selects exactly the same pixels as the per-pixel test.
            double maximum = MaximumValue(profile, valueAllowance);
            int threshold = -1;
            for (int value = 0; value < 256 && value / 255.0 <= maximum; value++) threshold = value;
            using var luminance = useLuminance ? Mat.FromPixelData(Height, Width, MatType.CV_8UC1, Gray) : null;
            using var source = new Mat(luminance ?? _value, bounds);
            Mat mask = new();
            Cv2.Threshold(source, mask, threshold, 255, ThresholdTypes.BinaryInv);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, CrossKernel);
            return mask;
        }

        public Rect SearchRegion(BlackTipSearchHint hint)
        {
            double radius = Math.Clamp(MarkerTipGeometry.Distance(hint.Marker.Bar!.End1, hint.Marker.Bar.End2) * 1.8, 48, 192);
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

        public IReadOnlyList<MarkerTipObservation>? FitEdges(MarkerTipObservation anchor,
            BlackTipProfile profile, double expectedArea)
        {
            var result = new List<MarkerTipObservation>();
            var bar = anchor.Bar!;
            double length = MarkerTipGeometry.Distance(bar.End1, bar.End2);
            double pixel = Math.Max(ScaleX, ScaleY);
            double thickness = Math.Sqrt(expectedArea / profile.AspectRatio);
            double edge = 2 * pixel;
            // A wider probe may confirm a transition softened by motion, but its
            // location and ranking still come from the original narrow gradient.
            double wideEdge = Math.Clamp(thickness * .25, edge, 4 * pixel);
            if (length / pixel is < 16 or > 192 || thickness / pixel < 4) return result;
            double angle = Math.Atan2(bar.End2.Y - bar.End1.Y, bar.End2.X - bar.End1.X);
            double cutoff = MaximumValue(profile, 0) * 255;
            int attempted = 0;
            foreach (int adjustment in new[] { 0, -5, 5 })
            {
                double direction = angle + adjustment * Math.PI / 180;
                double ux = Math.Cos(direction), uy = Math.Sin(direction), vx = -uy, vy = ux;
                PixelPoint Point(double u, double v) => new(anchor.Center.X + u * ux + v * vx,
                    anchor.Center.Y + u * uy + v * vy);
                // All tested transitions must be real pixels on both sides of the edge.
                // Search-window or image boundaries never become rectangle evidence.
                if (new[] { Point(-length * .72 - wideEdge, -length - wideEdge), Point(length * .72 + wideEdge, -length - wideEdge),
                    Point(-length * .72 - wideEdge, length + wideEdge), Point(length * .72 + wideEdge, length + wideEdge) }
                    .Any(point => ToImageX(point.X) < 1 || ToImageY(point.Y) < 1 ||
                        ToImageX(point.X) >= Width - 2 || ToImageY(point.Y) >= Height - 2)) continue;
                double Sample(double u, double v)
                {
                    double x = ToImageX(anchor.Center.X + u * ux + v * vx);
                    double y = ToImageY(anchor.Center.Y + u * uy + v * vy);
                    int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                    double fx = x - ix, fy = y - iy;
                    var data = Gray;
                    int i = iy * Width + ix;
                    return (data[i] * (1 - fx) + data[i + 1] * fx) * (1 - fy) +
                        (data[i + Width] * (1 - fx) + data[i + Width + 1] * fx) * fy;
                }
                List<(double Position, double Strength)>? Peaks(bool along, double from, double to,
                    double acrossFrom, double acrossTo, int samples, int sign)
                {
                    int count = (int)Math.Ceiling((to - from) / pixel) + 1;
                    Span<double> strength = stackalloc double[385];
                    Span<int> support = stackalloc int[385];
                    if (count > strength.Length) return null;
                    for (int i = 0; i < count; i++)
                    {
                        double position = from + i * pixel, sum = 0;
                        int supported = 0;
                        for (int j = 0; j < samples; j++)
                        {
                            double across = acrossFrom + (acrossTo - acrossFrom) * j / (samples - 1);
                            double delta = sign * (along ? Sample(position - edge, across) - Sample(position + edge, across)
                                : Sample(across, position - edge) - Sample(across, position + edge));
                            sum += delta;
                            if (delta > 10) supported++;
                        }
                        strength[i] = sum / samples;
                        support[i] = supported;
                    }
                    var peaks = new List<(double, double)>();
                    for (int i = 1; i < count - 1; i++)
                    {
                        if (support[i] < samples * .75 ||
                            strength[i - 1] > strength[i] || strength[i + 1] >= strength[i]) continue;
                        if (strength[i] < 25)
                        {
                            if (wideEdge <= edge) continue;
                            double position = from + i * pixel, sum = 0;
                            int supported = 0;
                            for (int j = 0; j < samples; j++)
                            {
                                double across = acrossFrom + (acrossTo - acrossFrom) * j / (samples - 1);
                                double delta = sign * (along
                                    ? Sample(position - wideEdge, across) - Sample(position + wideEdge, across)
                                    : Sample(across, position - wideEdge) - Sample(across, position + wideEdge));
                                sum += delta;
                                if (delta > 10) supported++;
                            }
                            if (sum / samples < 25 || supported < samples * .75) continue;
                        }
                        peaks.Add((from + i * pixel, strength[i]));
                        if (peaks.Count > 12) return null;
                    }
                    return peaks;
                }
                var tops = Peaks(false, -length * .95, length * .95, -length * .32, length * .32, 17, 1);
                var bottoms = Peaks(false, -length * .95, length * .95, -length * .32, length * .32, 17, -1);
                if (tops is null || bottoms is null) return null;
                foreach (var top in tops)
                foreach (var bottom in bottoms)
                {
                    double width = bottom.Position - top.Position, mid = (top.Position + bottom.Position) * .5;
                    if (width < thickness * .60 || width > thickness * 1.6 ||
                        Math.Min(Math.Abs(mid), Math.Abs(Math.Abs(mid) - length * profile.SpacingRatio)) > thickness * .50) continue;
                    if (++attempted > 32) return null;
                    var lefts = Peaks(true, -length * .7, length * .7,
                        top.Position + width * .25, bottom.Position - width * .25, 9, 1);
                    var rights = Peaks(true, -length * .7, length * .7,
                        top.Position + width * .25, bottom.Position - width * .25, 9, -1);
                    if (lefts is null || rights is null) return null;
                    foreach (var left in lefts)
                    foreach (var right in rights)
                    {
                        double measuredLength = right.Position - left.Position, area = measuredLength * width;
                        double aspect = measuredLength / width;
                        if (measuredLength < length * .75 || measuredLength > length * 1.25 ||
                            Math.Abs(left.Position + right.Position) > length * .30 ||
                            aspect < Math.Max(2.4, profile.AspectRatio / 1.8) || aspect > profile.AspectRatio * 1.8 ||
                            area < expectedArea * .45 || area > expectedArea * 2.2) continue;
                        int dark = 0, weakestRow = 13, topSupport = 0, bottomSupport = 0;
                        for (int row = 0; row < 5; row++)
                        {
                            int rowDark = 0;
                            double v = top.Position + width * (.20 + row * .15);
                            for (int column = 0; column < 13; column++)
                            {
                                double u = left.Position + measuredLength * (.12 + column * .76 / 12);
                                // A projected colour can brighten one RGB channel while
                                // the physical ink remains dark in luminance. This path
                                // already requires a fresh ordinary black-strip anchor.
                                if (Sample(u, v) <= cutoff) rowDark++;
                                if (row != 0) continue;
                                if (Sample(u, top.Position - edge) - Sample(u, top.Position + edge) > 10) topSupport++;
                                if (Sample(u, bottom.Position + edge) - Sample(u, bottom.Position - edge) > 10) bottomSupport++;
                            }
                            dark += rowDark;
                            weakestRow = Math.Min(weakestRow, rowDark);
                        }
                        if (dark < 46 || weakestRow < 7 || topSupport < 9 || bottomSupport < 9) continue;
                        double middleU = (left.Position + right.Position) * .5;
                        double contrast = Math.Min(Math.Min(top.Strength, bottom.Strength), Math.Min(left.Strength, right.Strength));
                        double score = .45 * Math.Exp(-Math.Abs(Math.Log(area / expectedArea))) +
                            .25 * Math.Exp(-Math.Abs(Math.Log(aspect / profile.AspectRatio))) + .30 * Math.Min(1, contrast / 80);
                        var measured = new MarkerTipObservation(Point(middleU, mid), Math.Sqrt(area / Math.PI), area, score)
                        { Bar = new(Point(left.Position, mid), Point(right.Position, mid),
                            Point(middleU, top.Position), Point(middleU, bottom.Position)) };
                        // Consolidate after all anchors: keeping these bounded measurements
                        // avoids losing a full outline to an early high-scoring subsection.
                        result.Add(measured);
                        if (result.Count > 128) return null;
                    }
                }
            }
            return result;
        }

        public bool DarkCore(double x, double y, BlackTipProfile profile, double valueAllowance = 0, bool useLuminance = false)
        {
            int cx = (int)Math.Round(x), cy = (int)Math.Round(y), count = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (IsDark((cy + dy) * Width + cx + dx, profile, valueAllowance, useLuminance)) count++;
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
            using var polygon = InputArray.Create(contour);
            for (int y = top; y <= bottom; y += step)
            for (int x = left; x <= right; x += step)
            {
                double distance = Cv2.PointPolygonTest(polygon, new Point2f(x, y), true);
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
