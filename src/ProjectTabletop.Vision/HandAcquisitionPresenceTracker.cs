namespace ProjectTabletop.Vision;

/// <summary>The exact rendered board and the calibrated native-camera to board-UV homography.</summary>
/// <param name="Bgra">Rendered BGRA pixels. Only opaque pixels are eligible reference samples;
/// transparent cutouts may contain live graphics that are absent from this static image.</param>
/// <param name="BoardSearchRegions">Optional normalized board-UV rectangles whose union contains
/// stable controls. Changes elsewhere in the rendered board cannot become foreground evidence.</param>
/// <param name="BoardReferenceRegions">Optional normalized board-UV rectangles used to fit camera
/// appearance independently of the candidate controls, such as unobscured static panels.</param>
/// <param name="BoardTriggerRegions">Optional normalized board-UV rectangles paired one-to-one
/// with BoardSearchRegions and contained inside them. Each control also requires fresh foreground
/// over at least 7% of its trigger region, such as its generated label.</param>
/// <param name="AllowsLocalForegroundContext">The generated image also describes the static
/// surroundings of controls. After a control qualifies, connected local foreground may
/// fit its temporary light; those extra pixels cannot qualify acquisition.</param>
public sealed record HandAcquisitionSceneImage(int Width, int Height, byte[] Bgra,
    IReadOnlyList<double> CameraToBoard, IReadOnlyList<HandTrackingBounds>? BoardSearchRegions = null,
    IReadOnlyList<HandTrackingBounds>? BoardReferenceRegions = null,
    IReadOnlyList<HandTrackingBounds>? BoardTriggerRegions = null,
    bool AllowsLocalForegroundContext = false);

/// <summary>Fresh generated-letter structure evidence, independent of panel palette fitting.
/// Coverage counts measured changed glyph/halo pixels against the complete paired rectangles.</summary>
/// <param name="CaptionReflectanceCoverage">Fresh chromatic residual measured against the whole control,
/// independent of structural letter corruption. Normally limited to registered glyph/halo support;
/// qualified thin captions also count actual changed control pixels after fresh opposite-margin fitting.</param>
/// <param name="CaptionReflectanceTriggerCoverage">The same residual measured against the caption region.</param>
/// <param name="CaptionReflectanceInkFraction">Fraction of sampled generated ink with fresh chromatic residual.</param>
/// <param name="CaptionReflectanceChanged">Readable letters have independently verified localized reflectance
/// evidence meeting both 7% area floors and the ink floor. This is not structural text corruption.</param>
public sealed record HandAcquisitionTextPatternResult(int ControlRegion, bool LabelIntact,
    double Correlation, double ControlCoverage, double ControlTriggerCoverage,
    bool ShapeCorrupted = false, int ConfirmationFrames = 0, double OpticalBlur = 0,
    int RegistrationX = 0, int RegistrationY = 0, double OpticalScaleX = 1, double OpticalScaleY = 1,
    IReadOnlyList<double>? SectorCorrelations = null, double LocalDamageCoverage = 0,
    double OpticalExposureGain = 1, double OpticalExposureBackground = 0,
    double CaptionReflectanceCoverage = 0, double CaptionReflectanceTriggerCoverage = 0,
    double CaptionReflectanceInkFraction = 0, bool CaptionReflectanceChanged = false, bool CaptionClipped = false);

/// <summary>Foreground evidence for acquisition, never proof of a hand or a selection.</summary>
/// <param name="ForegroundFraction">Foreground occupancy among eligible candidate samples.
/// When separate reference regions are supplied, their fit quality is checked independently.</param>
public sealed record HandAcquisitionPresenceResult(IReadOnlyList<HandAcquisitionHint> Hints,
    bool BaselineReady, bool? IlluminatedPresence, double ForegroundFraction, string Reason,
    IReadOnlyList<HandAcquisitionTextPatternResult>? TextPatterns = null,
    IReadOnlyList<HandAcquisitionLocalFitResult>? LocalFits = null, double? IlluminatedWhiteLuminance = null,
    bool? ProjectedWhiteClipped = null);

/// <summary>Current-frame opposite-margin fitting diagnostics for compact controls.</summary>
public sealed record HandAcquisitionLocalFitResult(int ControlRegion, int TrainingSamples,
    int WitnessSamples, double WitnessCoverage, double MedianError, string Reason);

/// <summary>
/// Compares the camera with a known rendered scene after robust photometric compensation.
/// A still foreground object remains foreground. Own search lighting is evaluated separately
/// inside its opaque white core and is never learned as the unlit board. Reset on a real scene
/// or calibration change, not when toggling the acquisition light.
/// </summary>
public sealed partial class HandAcquisitionPresenceTracker
{
    private const int Features = 7;
    // Dense betting boards need more than 32 independent captions. Keep all
    // three supplied region collections bounded before allocating templates.
    private const int MaximumSceneRegions = 64;
    // A live four-finger capture covered 7.2–9.0% of its control with residual
    // evidence; empty-table nuisance stayed below 0.36%. Normalize per control,
    // not by camera resolution or the number of buttons on a board.
    public const double MinimumControlCoverage = .07;
    // Camera frames arriving sooner after a search light starts still show the
    // unlit table; live logs found lit hands only from about 210 ms onward.
    public static readonly TimeSpan SearchLightSettling = TimeSpan.FromMilliseconds(220);
    // A night-exposed camera clipped the lit board to 254-255; lit fingers
    // clipped with it and the core read as empty white. Evening frames where
    // lit hands were found showed the lit board near 231 and fingers near 153.
    public const double SaturatedIlluminatedWhite = 245;
    private PixelPoint[] _polygon = [];
    private PixelPoint[] _locations = [];
    private bool[] _mask = [];
    private double[]? _baseline;
    private double[]? _expected;
    private bool[]? _templateEdges;
    private bool[]? _templateMask;
    private bool[]? _templateReferenceMask;
    private bool[]? _templateSampleMask;
    private double[]?[]? _edgeColors;
    private int[]? _controlRegions;
    private double[]? _sampleBoardAreas;
    private double[]? _controlBoardAreas;
    private int[]? _controlTriggerRegions;
    private double[]? _controlTriggerBoardAreas;
    private HandAcquisitionSceneImage? _scene;
    private PhotometricFit? _unobstructedFit;
    private Dictionary<int, double[]>? _unobstructedColorOffsets;
    private HandAcquisitionTextPatterns? _textPatterns;
    private readonly Dictionary<int, bool> _thinCaptionSupport = new();
    private int[]? _textCorruptionFrames;
    private DateTimeOffset[]? _textCorruptionTimes;
    private int _width, _height, _columns, _rows, _validCount;
    private double _left, _top, _scale;
    private DateTimeOffset _lastTime;
    internal int SampledCellCount { get; private set; }

    public HandAcquisitionPresenceResult Update(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> searchPolygon, HandAcquisitionSceneImage? expectedScene,
        DateTimeOffset frameTime, DateTimeOffset now, HandAcquisitionHint? illuminatedHint = null,
        DateTimeOffset? illuminationStartedAt = null) =>
        Update(width, height, stride, bgra, searchPolygon, frameTime, now, illuminatedHint,
            illuminationStartedAt, expectedScene);

    public HandAcquisitionPresenceResult Update(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> searchPolygon, DateTimeOffset frameTime, DateTimeOffset now,
        HandAcquisitionHint? illuminatedHint = null, DateTimeOffset? illuminationStartedAt = null,
        HandAcquisitionSceneImage? expectedScene = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(searchPolygon);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > TimeSpan.FromMilliseconds(300))
        {
            ResetTextConfirmation();
            return Empty("stale-camera-frame");
        }
        if (_lastTime != default && frameTime <= _lastTime)
        {
            if (frameTime < _lastTime) ResetTextConfirmation();
            return Empty("old-camera-frame");
        }
        bool geometryChanged = width != _width || height != _height || !_polygon.SequenceEqual(searchPolygon);
        if (geometryChanged || !ReferenceEquals(_scene, expectedScene))
        {
            int samplingAxis = SamplingAxis(searchPolygon, expectedScene, width, height);
            if (geometryChanged || Math.Abs(_scale - SamplingScale(searchPolygon, samplingAxis, width, height)) > .001)
                if (!Configure(width, height, searchPolygon, samplingAxis)) return Empty("invalid-search-polygon");
        }
        _lastTime = frameTime;
        SampledCellCount = 0;
        LocalContextSampledCellCount = 0;
        if (_validCount < 48) return Empty("insufficient-board-area");
        if (!ReferenceEquals(_scene, expectedScene)) ConfigureTemplate(expectedScene);
        // A supplied render is authoritative. Invalid geometry or an empty control
        // mask must not silently learn a hand already present as empty camera background.
        if (expectedScene is not null && _expected is null) return Empty("invalid-rendered-scene");

        bool activeLight = IsValidLight(illuminatedHint);
        var textObservations = _textPatterns?.Observe(width, height, stride, bgra,
            activeLight ? illuminatedHint : null) ?? [];
        double[] current = Sample(stride, bgra, activeLight ? illuminatedHint : null);
        bool[] allowed = (bool[])(_templateMask ?? _mask).Clone();
        // Compact labeled controls can consist almost entirely of raster edges.
        // Keep those pixels as candidates, comparing nearby expected colors, but
        // never use ambiguous edge pixels to train the photometric fit.
        bool compareControlEdges = _templateMask is not null;
        bool[] fitAllowed = _templateReferenceMask is not null ? (bool[])_templateReferenceMask.Clone()
            : compareControlEdges ? (bool[])allowed.Clone() : allowed;
        if (activeLight)
            for (int index = 0; index < allowed.Length; index++)
                if (Distance(_locations[index], illuminatedHint!.IlluminationCenter) < illuminatedHint.IlluminationRadiusPixels * 1.28)
                    allowed[index] = fitAllowed[index] = false;

        // The immutable rendered image supports acquisition even when a hand is present in
        // the very first camera frame. A camera reference is only a fallback for callers
        // without a usable rendered template; it cannot identify objects already in it.
        double[]? reference = _expected ?? _baseline;
        bool usingTemplate = _expected is not null;
        if (reference is null)
        {
            if (!activeLight) _baseline = (double[])current.Clone();
            return Empty(activeLight ? "waiting-for-unlit-reference" : "camera-reference-initialized");
        }
        if (usingTemplate)
            for (int index = 0; index < allowed.Length; index++)
            {
                if (!compareControlEdges) allowed[index] &= !_templateEdges![index];
                fitAllowed[index] &= !_templateEdges![index];
            }

        // A compact control may occupy half of all available reference pixels.
        // Its obstruction must not teach the fit that a darker surface is the
        // expected camera response. Keep an established unobstructed appearance
        // and fit from the other reference patches when just a control changes.
        ExcludeObstructedReferenceControls(reference, current, fitAllowed);
        ExcludeObstructedTextControls(textObservations, fitAllowed);
        PhotometricFit? fit = Fit(reference, current, fitAllowed);
        var colorOffsets = fit is null ? null : ColorResiduals(reference, current, fitAllowed, fit.Coefficients);
        bool[] foreground = new bool[_mask.Length];
        double threshold = fit is null ? double.PositiveInfinity : Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
        int foregroundCount = 0, eligibleCount = 0;
        if (fit is not null)
            for (int index = 0; index < foreground.Length; index++)
            {
                if (!allowed[index]) continue;
                eligibleCount++;
                colorOffsets!.TryGetValue(ColorKey(reference, index), out var colorOffset);
                double error = Error(fit.Coefficients, reference, current, index, colorOffset);
                if (error > threshold && _edgeColors?[index] is { } alternatives)
                    error = EdgeError(fit.Coefficients, alternatives, current, index, colorOffsets, error, threshold);
                if (error <= threshold) continue;
                foreground[index] = true;
                foregroundCount++;
            }
        double fraction = foregroundCount / (double)Math.Max(1, eligibleCount);
        // An explicit reference patch may be much smaller than a covered Back
        // button. Its own fit residuals determine whether camera compensation is
        // trustworthy; the amount of foreground on a separate candidate cannot
        // invalidate a clean reference. Report candidate occupancy consistently.
        double referenceFraction = fraction;
        if (fit is not null && _templateReferenceMask is not null)
        {
            int referenceForeground = 0, referenceEligible = 0;
            for (int index = 0; index < fitAllowed.Length; index++)
            {
                if (!fitAllowed[index]) continue;
                referenceEligible++;
                colorOffsets!.TryGetValue(ColorKey(reference, index), out var colorOffset);
                if (Error(fit.Coefficients, reference, current, index, colorOffset) > threshold)
                    referenceForeground++;
            }
            referenceFraction = referenceForeground / (double)Math.Max(1, referenceEligible);
        }
        bool modelReliable = fit is not null && fit.MedianError <= 18 && referenceFraction < .40;
        if (!modelReliable) Array.Clear(foreground);
        var captionReflectance = MeasureCaptionReflectance(reference, current, foreground, fit, colorOffsets);
        var reflectedControls = ValidateCaptionReflectance(reference, current, captionReflectance,
            textObservations, fitAllowed);
        var localFits = new List<HandAcquisitionLocalFitResult>();
        var localizedControls = !activeLight ? FitLocalizedControls(reference, current, allowed, fitAllowed,
            foreground, textObservations, captionReflectance, reflectedControls, localFits) : [];
        foreach (var localized in localizedControls)
        {
            if (localized.Reflectance is { } evidence)
            {
                reflectedControls = reflectedControls.Where(other => other.Region != evidence.Region).Append(evidence).ToArray();
                captionReflectance = captionReflectance.Where(other => other.Region != evidence.Region).Append(evidence).ToArray();
            }
        }
        // Readable letters usually disprove a panel palette/exposure mismatch.
        // The projector can also print the same readable letters onto fingers:
        // retain only independently verified new chroma on their generated ink
        // and halo, never a broad panel tint or luminance-only optical mismatch.
        foreach (var observation in textObservations.Where(observation => !observation.StrongCorruption))
        {
            var evidence = reflectedControls.FirstOrDefault(candidate => candidate.Region == observation.Region);
            for (int index = 0; index < foreground.Length; index++)
                if (_controlRegions?[index] == observation.Region)
                    foreground[index] = evidence?.Mask[index] ?? false;
        }
        if (modelReliable)
        {
            foregroundCount = foreground.Count(value => value);
            fraction = foregroundCount / (double)Math.Max(1, eligibleCount);
        }

        bool? illuminatedPresence = null;
        double? illuminatedWhite = null;
        double? illuminatedControlCoverage = null;
        double? illuminatedControlTriggerCoverage = null;
        string reason = modelReliable ? usingTemplate ? "rendered-scene-foreground" : "camera-reference-foreground"
            : "photometric-reference-uncertain";
        if (activeLight)
        {
            if (illuminationStartedAt is null || frameTime - illuminationStartedAt < SearchLightSettling)
                reason = "search-light-settling";
            else
            {
                illuminatedPresence = CheckIlluminatedCore(current, illuminatedHint!, fit, out string lightReason,
                    out illuminatedControlCoverage, out illuminatedControlTriggerCoverage, out illuminatedWhite);
                reason = lightReason;
            }
        }

        var hints = modelReliable ? Components(foreground, frameTime,
            searchMask: _templateMask, evidenceArea: _templateMask is null ? null : eligibleCount) : new List<HandAcquisitionHint>();
        foreach (var localized in localizedControls)
            foreach (var hint in Components(localized.Mask, frameTime, searchMask: _templateMask,
                evidenceArea: eligibleCount, requiredControlRegion: localized.Region))
                if (!hints.Any(other => ControlRegionAt(other.Center) == localized.Region))
                {
                    hints.Add(hint);
                    fraction = Math.Max(fraction, hint.MotionFraction);
                    reason = "localized-control-foreground";
                }
        if (!activeLight && reflectedControls.Count > 0 && hints.Count > 0)
            reason = "caption-reflectance-foreground";
        foreach (var observation in textObservations.Where(observation => observation.StrongCorruption))
            if (TextHint(observation, frameTime) is { } textHint &&
                !hints.Any(hint => ControlRegionAt(hint.Center) == observation.Region))
            {
                hints.Add(textHint);
                fraction = Math.Max(fraction, textHint.MotionFraction);
                if (!activeLight) reason = "text-pattern-foreground";
            }
        // Freshly qualifying controls awaiting their second frame can still
        // describe a reaching palm's position; they never start a light.
        var awaitingConfirmation = new List<HandAcquisitionHint>();
        foreach (var observation in textObservations)
        {
            bool qualifying = (observation.StrongCorruption || reflectedControls.Any(evidence => evidence.Region == observation.Region)) && hints.Any(hint =>
                ControlRegionAt(hint.Center) == observation.Region && hint.ControlCoverage >= MinimumControlCoverage &&
                hint.ControlTriggerCoverage >= MinimumControlCoverage);
            bool consecutive = frameTime - _textCorruptionTimes![observation.Region] <= TimeSpan.FromMilliseconds(350);
            int confirmations = qualifying ? Math.Min(2, consecutive ? _textCorruptionFrames![observation.Region] + 1 : 1) : 0;
            _textCorruptionFrames![observation.Region] = confirmations;
            _textCorruptionTimes[observation.Region] = qualifying ? frameTime : default;
            if (confirmations < 2)
            {
                if (qualifying)
                    awaitingConfirmation.AddRange(hints.Where(hint => ControlRegionAt(hint.Center) == observation.Region));
                hints.RemoveAll(hint => ControlRegionAt(hint.Center) == observation.Region);
                if (qualifying && !activeLight) reason = observation.StrongCorruption
                    ? "text-corruption-confirming" : "caption-reflectance-confirming";
            }
        }
        // A hand reaches in from the viewer's edge, the rendered board's bottom.
        // Across several disturbed controls its fingertips are on the one
        // farthest from that edge: keep that target first, the palm's next.
        if (!activeLight && hints.Count > 1 && _scene is not null)
            hints = hints.OrderBy(hint => BoardPosition(_scene, hint.Center, out _, out double v) ? v : double.PositiveInfinity)
                .ToList();
        if (illuminatedPresence == true)
        {
            hints.RemoveAll(hint => Distance(hint.Center, illuminatedHint!.Center) < illuminatedHint.RadiusPixels);
            hints.Insert(0, illuminatedHint! with
            {
                ObservedAt = frameTime,
                ControlCoverage = illuminatedControlCoverage,
                ControlTriggerCoverage = illuminatedControlTriggerCoverage
            });
        }
        if (hints.Count > 2) hints.RemoveRange(2, hints.Count - 2);
        if (!activeLight && modelReliable && fit is not null && colorOffsets is not null &&
            _scene?.AllowsLocalForegroundContext == true)
            for (int index = 0; index < hints.Count; index++)
                hints[index] = AttachLocalForegroundCandidate(hints[index], bgra, stride, fit, colorOffsets);
        // The nearest disturbed control behind the target, toward the viewer,
        // holds the palm. Its evidence may still be awaiting its second frame.
        if (!activeLight && hints.Count > 0 && hints[0].ValidatedCandidateBounds is null && _scene is not null)
        {
            var target = hints[0];
            if (hints.Skip(1).Concat(awaitingConfirmation)
                    .OrderBy(hint => BoardPosition(_scene, hint.Center, out _, out double v) ? v : double.PositiveInfinity)
                    .Select(behind => ReachingHandSpan(target, behind)).FirstOrDefault(span => span is not null) is { } reaching)
                hints[0] = reaching;
        }

        // Keep a fixed reference instead of gradually absorbing a stationary hand. A new
        // rendered scene explicitly resets it; exposure drift is fitted on every fresh frame.
        if (_baseline is null && !activeLight && foregroundCount == 0 && modelReliable)
            _baseline = (double[])current.Clone();
        if (!activeLight && foregroundCount == 0 && modelReliable &&
            textObservations.All(observation => observation.Clean))
        {
            _unobstructedFit = fit;
            _unobstructedColorOffsets = colorOffsets;
        }
        var textDiagnostics = textObservations.Select(observation => new HandAcquisitionTextPatternResult(
            observation.Region, observation.Clean, observation.Correlation,
            observation.ChangedBoardPixels.Count / 1_000_000.0 / _controlBoardAreas![observation.Region],
            observation.ChangedBoardPixels.Count / 1_000_000.0 / _controlTriggerBoardAreas![observation.Region],
            observation.StrongCorruption, _textCorruptionFrames![observation.Region], observation.OpticalBlur,
            observation.OffsetX, observation.OffsetY, observation.ScaleX, observation.ScaleY,
            observation.SectorCorrelations, observation.LocalDamageCoverage,
            observation.ExposureGain, observation.ExposureBackground,
            captionReflectance.FirstOrDefault(evidence => evidence.Region == observation.Region)?.Coverage ?? 0,
            captionReflectance.FirstOrDefault(evidence => evidence.Region == observation.Region)?.TriggerCoverage ?? 0,
            captionReflectance.FirstOrDefault(evidence => evidence.Region == observation.Region)?.InkFraction ?? 0,
            reflectedControls.Any(evidence => evidence.Region == observation.Region), observation.Clipped)).ToArray();
        return new(hints, _baseline is not null || _expected is not null, illuminatedPresence, fraction, reason,
            textDiagnostics.Length == 0 ? null : textDiagnostics, localFits.Count == 0 ? null : localFits, illuminatedWhite,
            // Intact white captions already clipping means a white search light
            // will clip too; night exposure raised the unlit board by about 38%.
            textObservations.Any(observation => observation.Clean)
                ? textObservations.Any(observation => observation.Clean && observation.Clipped) : null);
    }

    public void Reset()
    {
        _polygon = []; _locations = []; _mask = [];
        _baseline = _expected = null; _templateEdges = _templateMask = _templateReferenceMask = _templateSampleMask = null;
        _edgeColors = null; _controlRegions = null; _sampleBoardAreas = _controlBoardAreas = null; _scene = null;
        _controlTriggerRegions = null; _controlTriggerBoardAreas = null;
        _unobstructedFit = null; _unobstructedColorOffsets = null;
        _textPatterns = null;
        _thinCaptionSupport.Clear();
        _textCorruptionFrames = null;
        _textCorruptionTimes = null;
        _width = _height = _columns = _rows = _validCount = 0;
        _lastTime = default;
        SampledCellCount = 0;
    }

    private HandAcquisitionPresenceResult Empty(string reason) =>
        new([], _baseline is not null || _expected is not null, null, 0, reason);

    private void ResetTextConfirmation()
    {
        if (_textCorruptionFrames is not null) Array.Clear(_textCorruptionFrames);
        if (_textCorruptionTimes is not null) Array.Clear(_textCorruptionTimes);
    }

    private static double SamplingScale(IReadOnlyList<PixelPoint> polygon, int axis, int width, int height) => polygon.Count == 0 ? 1 :
        Math.Max(1, Math.Max(Math.Clamp(polygon.Max(point => point.X), 0, width) - Math.Clamp(polygon.Min(point => point.X), 0, width),
            Math.Clamp(polygon.Max(point => point.Y), 0, height) - Math.Clamp(polygon.Min(point => point.Y), 0, height)) / axis);

    private static int SamplingAxis(IReadOnlyList<PixelPoint> polygon, HandAcquisitionSceneImage? scene, int cameraWidth, int cameraHeight)
    {
        var references = scene?.BoardReferenceRegions ?? scene?.BoardSearchRegions;
        if (polygon.Count < 3 || references is not { Count: > 0 }) return 192;
        // A short minus can fall entirely between coarse grid rows. Refine to
        // the existing bounded density if its actual caption cannot contain a
        // full native sample. Never expand the caption or count partial samples.
        // This decision is reused until the scene reference or geometry changes.
        if (scene is { CameraToBoard.Count: 9, BoardTriggerRegions.Count: > 0 } &&
            ValidRegions(scene.BoardTriggerRegions) && scene.BoardTriggerRegions.Any(trigger =>
                !CaptionContainsSample(scene, polygon, trigger, SamplingScale(polygon, 192, cameraWidth, cameraHeight),
                    cameraWidth, cameraHeight))) return 384;
        if (scene?.BoardSearchRegions is not { Count: 1 }) return 192;
        var control = scene.BoardSearchRegions[0];
        if (references.Any(region => region.X < control.X || region.Y < control.Y ||
            region.X + region.Width > control.X + control.Width + 1e-12 ||
            region.Y + region.Height > control.Y + control.Height + 1e-12)) return 192;
        double width = Math.Clamp(polygon.Max(point => point.X), 0, cameraWidth) - Math.Clamp(polygon.Min(point => point.X), 0, cameraWidth);
        double height = Math.Clamp(polygon.Max(point => point.Y), 0, cameraHeight) - Math.Clamp(polygon.Min(point => point.Y), 0, cameraHeight);
        double scale = SamplingScale(polygon, 192, cameraWidth, cameraHeight);
        // A compact self-reference needs enough independent native samples on
        // its two glass margins. Increase only the bounded POI grid density;
        // sampling is still restricted to controls/references and never duplicates
        // native pixels when the camera already resolves fewer than 384 cells.
        double referenceSamples = width * height / (scale * scale) *
            references.Sum(region => region.Width * region.Height);
        return referenceSamples * .20 < 100 ? 384 : 192;
    }

    private static bool CaptionContainsSample(HandAcquisitionSceneImage scene, IReadOnlyList<PixelPoint> polygon,
        HandTrackingBounds trigger, double scale, int cameraWidth, int cameraHeight)
    {
        var m = scene.CameraToBoard;
        double u = trigger.X + trigger.Width / 2, v = trigger.Y + trigger.Height / 2;
        // Solve the projective map at the caption centre without constructing
        // a second image or scanning the board. Nine nearby grid cells suffice
        // for the bounded resolution probe; uncertain geometry refines safely.
        double a = m[0] - u * m[6], b = m[1] - u * m[7], c = u * m[8] - m[2];
        double d = m[3] - v * m[6], e = m[4] - v * m[7], f = v * m[8] - m[5];
        double determinant = a * e - b * d;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-16) return false;
        double x = (c * e - b * f) / determinant, y = (a * f - c * d) / determinant;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        double left = Math.Clamp(polygon.Min(point => point.X), 0, cameraWidth), top = Math.Clamp(polygon.Min(point => point.Y), 0, cameraHeight);
        double column = Math.Round((x - left) / scale - .5), row = Math.Round((y - top) / scale - .5);
        double radius = scale * .25 + 1; // Same four-pixel footprint as WithinSampleRegions.
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            var center = new PixelPoint(left + (column + dx + .5) * scale, top + (row + dy + .5) * scale);
            bool contained = true;
            for (int corner = 0; corner < 4; corner++)
                if (!BoardPosition(scene, new(center.X + (corner % 2 == 0 ? -radius : radius),
                    center.Y + (corner < 2 ? -radius : radius)), out double cu, out double cv) ||
                    cu < trigger.X || cu > trigger.X + trigger.Width || cv < trigger.Y || cv > trigger.Y + trigger.Height)
                { contained = false; break; }
            if (contained) return true;
        }
        return false;
    }

    private bool Configure(int width, int height, IReadOnlyList<PixelPoint> polygon, int samplingAxis = 192)
    {
        Reset();
        if (polygon.Count is < 3 or > 16 || polygon.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        _left = Math.Clamp(polygon.Min(point => point.X), 0, width);
        _top = Math.Clamp(polygon.Min(point => point.Y), 0, height);
        double right = Math.Clamp(polygon.Max(point => point.X), 0, width);
        double bottom = Math.Clamp(polygon.Max(point => point.Y), 0, height);
        if (right - _left < 8 || bottom - _top < 8) return false;
        _width = width; _height = height; _polygon = polygon.ToArray();
        _scale = Math.Max(1, Math.Max(right - _left, bottom - _top) / samplingAxis);
        _columns = (int)Math.Ceiling((right - _left) / _scale);
        _rows = (int)Math.Ceiling((bottom - _top) / _scale);
        _mask = new bool[_columns * _rows];
        _locations = new PixelPoint[_mask.Length];
        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < _columns; x++)
        {
            int index = y * _columns + x;
            _locations[index] = new(_left + (x + .5) * _scale, _top + (y + .5) * _scale);
            _mask[index] = Inside(_locations[index], _polygon);
            if (_mask[index]) _validCount++;
        }
        return true;
    }

    private double[] Sample(int stride, byte[] bgra, HandAcquisitionHint? illuminatedHint)
    {
        double[] result = new double[_mask.Length * 3];
        SampledCellCount = 0;
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) continue;
            PixelPoint point = _locations[index];
            // Generated controls are the points of interest. Independent static
            // reference patches compensate camera exposure; they cannot acquire
            // a hand. Only an active light's white core needs additional pixels.
            if (_templateSampleMask is not null && !_templateSampleMask[index] &&
                (illuminatedHint is null || Distance(point, illuminatedHint.IlluminationCenter) >= illuminatedHint.IlluminationRadiusPixels * .64))
                continue;
            SampledCellCount++;
            for (int dy = -1; dy <= 1; dy += 2)
            for (int dx = -1; dx <= 1; dx += 2)
            {
                int x = Math.Clamp((int)(point.X + dx * _scale * .25), 0, _width - 1);
                int y = Math.Clamp((int)(point.Y + dy * _scale * .25), 0, _height - 1);
                int offset = y * stride + x * 4;
                for (int channel = 0; channel < 3; channel++) result[index * 3 + channel] += bgra[offset + channel] * .25;
            }
        }
        return result;
    }

    private void ConfigureTemplate(HandAcquisitionSceneImage? scene)
    {
        _scene = scene; _baseline = _expected = null;
        _unobstructedFit = null; _unobstructedColorOffsets = null;
        _textPatterns = null;
        _thinCaptionSupport.Clear();
        _textCorruptionFrames = null;
        _textCorruptionTimes = null;
        _templateEdges = _templateMask = _templateReferenceMask = _templateSampleMask = null;
        _edgeColors = null;
        _controlRegions = null; _sampleBoardAreas = _controlBoardAreas = null;
        _controlTriggerRegions = null; _controlTriggerBoardAreas = null;
        if (scene is null || scene.Width is <= 1 or > 16384 || scene.Height is <= 1 or > 16384 ||
            scene.Bgra is null || scene.Bgra.Length < scene.Width * (long)scene.Height * 4 ||
            scene.CameraToBoard is not { Count: 9 } || scene.CameraToBoard.Any(value => !double.IsFinite(value))) return;
        var regions = scene.BoardSearchRegions;
        var referenceRegions = scene.BoardReferenceRegions;
        var triggerRegions = scene.BoardTriggerRegions;
        if (!ValidRegions(regions) || !ValidRegions(referenceRegions)) return;
        if (triggerRegions is not null && (!ValidRegions(triggerRegions) || regions is null ||
            triggerRegions.Count != regions.Count || Enumerable.Range(0, regions.Count).Any(index =>
                triggerRegions[index].X < regions[index].X || triggerRegions[index].Y < regions[index].Y ||
                triggerRegions[index].X + triggerRegions[index].Width > regions[index].X + regions[index].Width + 1e-12 ||
                triggerRegions[index].Y + triggerRegions[index].Height > regions[index].Y + regions[index].Height + 1e-12))) return;
        var expected = new double[_mask.Length * 3];
        var edges = new bool[_mask.Length];
        var templateMask = new bool[_mask.Length];
        var referenceMask = new bool[_mask.Length];
        int mapped = 0, selected = 0, referenceSelected = 0;
        double[] neighbor = new double[3];
        PixelPoint[] offsets = [new(-_scale * 2.5, 0), new(_scale * 2.5, 0),
            new(0, -_scale * 2.5), new(0, _scale * 2.5)];
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) { edges[index] = true; continue; }
            PixelPoint point = _locations[index];
            if (!TemplateColor(scene, point, expected.AsSpan(index * 3, 3))) { edges[index] = true; continue; }
            mapped++;
            templateMask[index] = WithinSampleRegions(scene, point, regions);
            referenceMask[index] = referenceRegions is null ? templateMask[index] : WithinSampleRegions(scene, point, referenceRegions);
            if (templateMask[index]) selected++;
            if (referenceMask[index]) referenceSelected++;
            if (!templateMask[index] && !referenceMask[index])
            { edges[index] = true; continue; }
            foreach (var offset in offsets)
            {
                if (!TemplateColor(scene, new(point.X + offset.X, point.Y + offset.Y), neighbor)) { edges[index] = true; break; }
                if (Enumerable.Range(0, 3).Any(channel => Math.Abs(expected[index * 3 + channel] - neighbor[channel]) > 32))
                { edges[index] = true; break; }
            }
        }
        if (mapped < _validCount * .75 || selected < 80 || referenceSelected < 80) return;
        _expected = expected; _templateEdges = edges; _templateMask = regions is null ? null : templateMask;
        _templateReferenceMask = referenceRegions is null ? null : referenceMask;
        if (triggerRegions is not null)
        {
            _textPatterns = new(scene);
            _textCorruptionFrames = new int[triggerRegions.Count];
            _textCorruptionTimes = new DateTimeOffset[triggerRegions.Count];
        }
        if (regions is not null)
        {
            _templateSampleMask = Enumerable.Range(0, _mask.Length)
                .Select(index => templateMask[index] || referenceMask[index]).ToArray();
            _edgeColors = new double[]?[_mask.Length];
            _controlRegions = Enumerable.Repeat(-1, _mask.Length).ToArray();
            _sampleBoardAreas = new double[_mask.Length];
            _controlBoardAreas = regions.Select(region => region.Width * region.Height).ToArray();
            if (triggerRegions is not null)
            {
                _controlTriggerRegions = Enumerable.Repeat(-1, _mask.Length).ToArray();
                _controlTriggerBoardAreas = triggerRegions.Select(region => region.Width * region.Height).ToArray();
            }
            double[] color = new double[3];
            for (int index = 0; index < _mask.Length; index++)
            {
                if (!templateMask[index]) continue;
                var colors = new List<double>(75);
                var point = _locations[index];
                BoardPosition(scene, point, out double u, out double v);
                for (int region = 0; region < regions.Count; region++)
                {
                    var bounds = regions[region];
                    if (u < bounds.X || u > bounds.X + bounds.Width || v < bounds.Y || v > bounds.Y + bounds.Height) continue;
                    _controlRegions[index] = region;
                    _sampleBoardAreas[index] = BoardSampleArea(scene, point);
                    if (triggerRegions is not null && WithinSampleRegions(scene, point, [triggerRegions[region]]))
                        _controlTriggerRegions![index] = region;
                    break;
                }
                // One sampling cell plus native rounding tolerates small calibration and raster shifts.
                // Bilinear samples include antialiased text/chip transitions; a new
                // foreground color must differ from this entire local neighborhood.
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    if (TemplateColor(scene, new(point.X + dx * (_scale + 1) * .5,
                        point.Y + dy * (_scale + 1) * .5), color)) colors.AddRange(color);
                _edgeColors[index] = colors.ToArray();
            }
        }
    }

    private static bool ValidRegions(IReadOnlyList<HandTrackingBounds>? regions) => regions is null ||
        regions.Count is >= 1 and <= MaximumSceneRegions && regions.All(region =>
            double.IsFinite(region.X) && double.IsFinite(region.Y) &&
            double.IsFinite(region.Width) && double.IsFinite(region.Height) &&
            region.X >= 0 && region.Y >= 0 && region.Width > 0 && region.Height > 0 &&
            region.X + region.Width <= 1 && region.Y + region.Height <= 1);

    private static bool WithinRegions(IReadOnlyList<HandTrackingBounds>? regions, double u, double v) => regions is null ||
        regions.Any(region => u >= region.X && u <= region.X + region.Width &&
            v >= region.Y && v <= region.Y + region.Height);

    private bool WithinSampleRegions(HandAcquisitionSceneImage scene, PixelPoint point,
        IReadOnlyList<HandTrackingBounds>? regions)
    {
        if (regions is null) return true;
        // Sample() averages four native pixels. All of that footprint must lie
        // inside the stable region, otherwise animation just beyond its boundary
        // can leak into a candidate. One pixel covers integer sample rounding.
        double radius = _scale * .25 + 1;
        for (int dy = -1; dy <= 1; dy += 2)
        for (int dx = -1; dx <= 1; dx += 2)
            if (!BoardPosition(scene, new(point.X + dx * radius, point.Y + dy * radius), out double u, out double v) ||
                !WithinRegions(regions, u, v)) return false;
        return true;
    }

    private double BoardSampleArea(HandAcquisitionSceneImage scene, PixelPoint point)
    {
        Span<double> u = stackalloc double[4], v = stackalloc double[4];
        for (int corner = 0; corner < 4; corner++)
        {
            double dx = corner is 0 or 3 ? -.5 : .5, dy = corner < 2 ? -.5 : .5;
            if (!BoardPosition(scene, new(point.X + dx * _scale, point.Y + dy * _scale), out u[corner], out v[corner])) return 0;
        }
        double area = 0;
        for (int corner = 0; corner < 4; corner++) area += u[corner] * v[(corner + 1) % 4] - u[(corner + 1) % 4] * v[corner];
        return Math.Abs(area) / 2;
    }

    private static bool TemplateColor(HandAcquisitionSceneImage scene, PixelPoint point, Span<double> result)
    {
        if (!BoardPosition(scene, point, out double u, out double v)) return false;
        double x = u * (scene.Width - 1), y = v * (scene.Height - 1);
        int ix = (int)x, iy = (int)y, rx = Math.Min(scene.Width - 1, ix + 1), by = Math.Min(scene.Height - 1, iy + 1);
        double fx = x - ix, fy = y - iy;
        int topLeft = (iy * scene.Width + ix) * 4, topRight = (iy * scene.Width + rx) * 4;
        int bottomLeft = (by * scene.Width + ix) * 4, bottomRight = (by * scene.Width + rx) * 4;
        double a = (1 - fx) * (1 - fy), b = fx * (1 - fy), c = (1 - fx) * fy, d = fx * fy;
        // A transparent hole is absent reference data, not an opaque black
        // surface. Reject every contributing nonopaque texel so antialiased
        // animation boundaries cannot enter the photometric fit either.
        if ((a > 0 && scene.Bgra[topLeft + 3] < 254) || (b > 0 && scene.Bgra[topRight + 3] < 254) ||
            (c > 0 && scene.Bgra[bottomLeft + 3] < 254) || (d > 0 && scene.Bgra[bottomRight + 3] < 254)) return false;
        for (int channel = 0; channel < 3; channel++)
            result[channel] = scene.Bgra[topLeft + channel] * a + scene.Bgra[topRight + channel] * b +
                scene.Bgra[bottomLeft + channel] * c + scene.Bgra[bottomRight + channel] * d;
        return true;
    }

    private static bool BoardPosition(HandAcquisitionSceneImage scene, PixelPoint point, out double u, out double v)
    {
        IReadOnlyList<double> matrix = scene.CameraToBoard;
        u = v = double.NaN;
        double divisor = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-10) return false;
        u = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / divisor;
        v = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / divisor;
        return double.IsFinite(u) && double.IsFinite(v) && u >= 0 && v >= 0 && u <= 1 && v <= 1;
    }

    private sealed record PhotometricFit(double[][] Coefficients, double MedianError);

    private sealed record CaptionReflectanceEvidence(int Region, bool[] Mask, double Coverage,
        double TriggerCoverage, double InkFraction);

    private IReadOnlyList<CaptionReflectanceEvidence> MeasureCaptionReflectance(double[] reference,
        double[] current, bool[] foreground, PhotometricFit? fit, Dictionary<int, double[]>? offsets,
        int? selectedRegion = null)
    {
        if (fit is null || offsets is null || _textPatterns is null || _scene is null ||
            _controlRegions is null || _controlBoardAreas is null || _controlTriggerRegions is null ||
            _controlTriggerBoardAreas is null) return [];
        var result = new List<CaptionReflectanceEvidence>();
        Span<double> residual = stackalloc double[3];
        for (int region = 0; region < _controlBoardAreas.Length; region++)
        {
            if (selectedRegion is not null && selectedRegion != region) continue;
            bool[] mask = new bool[foreground.Length];
            double area = 0, triggerArea = 0, inkArea = 0, changedInkArea = 0;
            for (int index = 0; index < foreground.Length; index++)
            {
                if (_controlRegions[index] != region ||
                    !BoardPosition(_scene, _locations[index], out double u, out double v) ||
                    !_textPatterns.IsGeneratedCaptionSupport(region, u, v, out bool ink)) continue;
                double cellArea = _sampleBoardAreas![index];
                if (ink) inkArea += cellArea;
                if (!foreground[index]) continue;
                offsets.TryGetValue(ColorKey(reference, index), out var offset);
                for (int channel = 0; channel < 3; channel++)
                    residual[channel] = current[index * 3 + channel] -
                        Predict(fit.Coefficients[channel], reference[index * 3], reference[index * 3 + 1],
                            reference[index * 3 + 2], index, channel) - (offset?[channel] ?? 0);
                double luminance = residual[0] * .114 + residual[1] * .587 + residual[2] * .299;
                double chroma = Math.Sqrt((Math.Pow(residual[0] - luminance, 2) +
                    Math.Pow(residual[1] - luminance, 2) + Math.Pow(residual[2] - luminance, 2)) / 3);
                if (chroma < 18) continue;
                mask[index] = true;
                area += cellArea;
                if (_controlTriggerRegions[index] == region) triggerArea += cellArea;
                if (ink) changedInkArea += cellArea;
            }
            result.Add(new(region, mask, area / _controlBoardAreas[region],
                triggerArea / _controlTriggerBoardAreas[region], changedInkArea / Math.Max(1e-12, inkArea)));
        }
        return result;
    }

    private IReadOnlyList<CaptionReflectanceEvidence> ValidateCaptionReflectance(double[] reference,
        double[] current, IReadOnlyList<CaptionReflectanceEvidence> measured,
        IReadOnlyList<HandAcquisitionTextPatterns.Observation> observations, bool[] fitAllowed)
    {
        var result = new List<CaptionReflectanceEvidence>();
        if (_controlRegions is null || _templateReferenceMask is null) return result;
        foreach (var candidate in measured.Where(evidence =>
            evidence.Coverage >= MinimumControlCoverage && evidence.TriggerCoverage >= MinimumControlCoverage &&
            evidence.InkFraction >= .5 && observations.Any(observation => observation.Region == evidence.Region &&
                observation.Clean && !observation.StrongCorruption)))
        {
            var independentLabels = observations.Where(observation => observation.Region != candidate.Region &&
                observation.Clean && observation.Correlation >= .82 &&
                measured.All(other => other.Region != observation.Region || other.Coverage < MinimumControlCoverage))
                .Select(observation => observation.Region).ToHashSet();
            if (independentLabels.Count == 0) continue;
            bool[] independent = Enumerable.Range(0, fitAllowed.Length).Select(index =>
                fitAllowed[index] && _templateReferenceMask[index] && _controlRegions[index] != candidate.Region &&
                (_controlRegions[index] < 0 || independentLabels.Contains(_controlRegions[index]))).ToArray();
            var fit = Fit(reference, current, independent);
            if (fit is null || fit.MedianError > 18) continue;
            var offsets = ColorResiduals(reference, current, independent, fit.Coefficients);
            double threshold = Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
            int checkedReference = 0, uncertainReference = 0;
            bool[] foreground = new bool[fitAllowed.Length];
            for (int index = 0; index < foreground.Length; index++)
            {
                if (!independent[index] && _controlRegions[index] != candidate.Region) continue;
                offsets.TryGetValue(ColorKey(reference, index), out var offset);
                double error = Error(fit.Coefficients, reference, current, index, offset);
                if (error > threshold && _edgeColors?[index] is { } alternatives)
                    error = EdgeError(fit.Coefficients, alternatives, current, index, offsets, error, threshold);
                if (independent[index])
                {
                    checkedReference++;
                    if (error > threshold) uncertainReference++;
                }
                else foreground[index] = error > threshold;
            }
            if (checkedReference < 80 || uncertainReference / (double)checkedReference >= .40) continue;
            var verified = MeasureCaptionReflectance(reference, current, foreground, fit, offsets, candidate.Region).Single();
            if (verified.Coverage >= MinimumControlCoverage && verified.TriggerCoverage >= MinimumControlCoverage &&
                verified.InkFraction >= .5 && LocalizedCaptionReflectance(reference, current, candidate.Region,
                    foreground, fit, offsets, threshold) is { } localized)
                result.Add(localized);
        }
        return result;
    }

    private void ExcludeObstructedTextControls(IReadOnlyList<HandAcquisitionTextPatterns.Observation> observations,
        bool[] fitAllowed)
    {
        if (_controlRegions is null) return;
        // A short caption can lose its shape while supplying little glyph area.
        // Keep that panel out of camera-colour training so a broad obstruction
        // cannot teach its new colour as the expected board. This creates no
        // foreground area: illumination still needs measured control and label
        // coverage plus two fresh confirming frames below.
        var obstructed = observations.Where(observation => observation.StrongCorruption)
            .Select(observation => observation.Region).ToHashSet();
        if (obstructed.Count == 0) return;
        bool[] independent = Enumerable.Range(0, fitAllowed.Length)
            .Select(index => fitAllowed[index] && !obstructed.Contains(_controlRegions[index])).ToArray();
        if (independent.Count(value => value) >= 80) independent.CopyTo(fitAllowed, 0);
    }

    private HandAcquisitionHint? TextHint(HandAcquisitionTextPatterns.Observation observation, DateTimeOffset observedAt)
    {
        if (_textPatterns is null || _controlBoardAreas is null || _controlTriggerBoardAreas is null ||
            observation.ChangedBoardPixels.Count == 0) return null;
        // Every counted logical pixel is fresh glyph/halo evidence. No NCC score
        // is converted into an invented hand area, and no dilation contributes.
        double area = observation.ChangedBoardPixels.Count / 1_000_000.0;
        double coverage = area / _controlBoardAreas[observation.Region];
        double triggerCoverage = area / _controlTriggerBoardAreas[observation.Region];
        if (coverage < MinimumControlCoverage || triggerCoverage < MinimumControlCoverage) return null;
        var points = observation.ChangedBoardPixels.Select(point =>
            _textPatterns.CameraPoint(point.X * 1000, point.Y * 1000)).ToArray();
        double left = points.Min(point => point.X), right = points.Max(point => point.X);
        double top = points.Min(point => point.Y), bottom = points.Max(point => point.Y);
        var center = new PixelPoint((left + right) / 2, (top + bottom) / 2);
        if (!Inside(center, _polygon)) return null;
        double extent = Math.Max(right - left + 1, bottom - top + 1);
        double shortSide = Math.Min(_width, _height), maxSide = shortSide * .60;
        int side = (int)Math.Ceiling(Math.Clamp(extent * 1.7 + shortSide * .10,
            Math.Min(maxSide, Math.Max(48, shortSide * .26)), maxSide));
        int cropX = Math.Clamp((int)Math.Round(center.X - side / 2.0), 0, _width - side);
        int cropY = Math.Clamp((int)Math.Round(center.Y - side / 2.0), 0, _height - side);
        double radius = Math.Clamp(extent * .55 + shortSide * .025, shortSide * .055, shortSide * .13);
        return new(new(cropX, cropY, side, side), center, radius, observedAt,
            area / Math.Max(1e-12, _controlBoardAreas.Sum()), coverage, triggerCoverage);
    }

    private void ExcludeObstructedReferenceControls(double[] reference, double[] current, bool[] fitAllowed)
    {
        if (_unobstructedFit is null || _unobstructedColorOffsets is null || _templateReferenceMask is null ||
            _controlRegions is null || _controlBoardAreas is null) return;
        var obstructed = new HashSet<int>();
        double threshold = Math.Clamp(_unobstructedFit.MedianError * 3.5 + 10, 24, 52);
        for (int region = 0; region < _controlBoardAreas.Length; region++)
        {
            double area = 0;
            for (int index = 0; index < fitAllowed.Length; index++)
            {
                if (!fitAllowed[index] || _controlRegions[index] != region) continue;
                _unobstructedColorOffsets.TryGetValue(ColorKey(reference, index), out var offset);
                double error = Error(_unobstructedFit.Coefficients, reference, current, index, offset);
                if (error > threshold && _edgeColors?[index] is { } alternatives)
                    error = EdgeError(_unobstructedFit.Coefficients, alternatives, current, index,
                        _unobstructedColorOffsets, error, threshold);
                if (error > threshold) area += _sampleBoardAreas![index];
            }
            if (area / _controlBoardAreas[region] >= MinimumControlCoverage) obstructed.Add(region);
        }
        // A global exposure change affects all compact references; let the
        // robust fit compensate it. A surviving independent patch is required
        // before excluding a candidate, so this never creates a blind fit.
        if (obstructed.Count == 0) return;
        bool[] independent = Enumerable.Range(0, fitAllowed.Length)
            .Select(index => fitAllowed[index] && !obstructed.Contains(_controlRegions[index])).ToArray();
        if (independent.Count(value => value) < 80) return;
        independent.CopyTo(fitAllowed, 0);
    }

    private PhotometricFit? Fit(double[] reference, double[] current, bool[] allowed)
    {
        int[] training = Enumerable.Range(0, allowed.Length).Where(index => allowed[index]).ToArray();
        if (training.Length < 80) return null;
        // Tiny controls alone cannot distinguish spatial exposure from an
        // arriving object on one panel. In that underconstrained geometry a
        // free x/y gradient can explain away the occupied panel and invent
        // foreground on an untouched one. Independent reference panels retain
        // the full model; compact self-references fit color/exposure only.
        bool spatialCompensation = _templateReferenceMask is null || _controlRegions is null ||
            _controlBoardAreas is null || _controlBoardAreas.Sum() >= .08 ||
            training.Count(index => _controlRegions[index] < 0) >= 80;
        int[] retained = training;
        double[][] coefficients = new double[3][];
        double median = 0;
        double[] features = new double[Features];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double[,] normal = new double[Features, Features];
                double[] values = new double[Features];
                foreach (int index in retained)
                {
                    FeatureVector(reference, index, channel, features);
                    if (!spatialCompensation) features[5] = features[6] = 0;
                    double value = current[index * 3 + channel];
                    for (int row = 0; row < Features; row++)
                    {
                        values[row] += features[row] * value;
                        for (int column = 0; column < Features; column++) normal[row, column] += features[row] * features[column];
                    }
                }
                // Regularize degenerate flat-color scenes and prefer a linear response when
                // the template lacks enough brightness range to estimate a gamma curve.
                for (int diagonal = 0; diagonal < Features; diagonal++) normal[diagonal, diagonal] += retained.Length * (diagonal == 4 ? .0003 : .00001);
                coefficients[channel] = Solve(normal, values);
                if (coefficients[channel].Any(value => !double.IsFinite(value))) return null;
            }
            var errors = training.Select(index => (Index: index, Error: Error(coefficients, reference, current, index)))
                .OrderBy(item => item.Error).ToArray();
            median = errors[errors.Length / 2].Error;
            // Keep the fit representative of dark controls as well as the much larger felt
            // area. A global trim can discard every button pixel and then classify those
            // very buttons as foreground; trim outliers within known rendered-color groups.
            retained = errors.GroupBy(item => ColorKey(reference, item.Index))
                .SelectMany(group => group.Take(Math.Max(1, (int)(group.Count() * .70))))
                .Select(item => item.Index).ToArray();
        }
        return new(coefficients, median);
    }

    private void FeatureVector(double[] reference, int index, int channel, double[] features)
    {
        double normalized = reference[index * 3 + channel] / 255;
        features[0] = 1;
        for (int color = 0; color < 3; color++) features[color + 1] = reference[index * 3 + color] / 255;
        features[4] = normalized * normalized;
        features[5] = (index % _columns + .5) / _columns - .5;
        features[6] = (index / _columns + .5) / _rows - .5;
    }

    private double Predict(double[] coefficients, double blue, double green, double red, int index, int channel)
    {
        double normalized = (channel == 0 ? blue : channel == 1 ? green : red) / 255;
        return Math.Clamp(coefficients[0] + coefficients[1] * blue / 255 + coefficients[2] * green / 255 +
            coefficients[3] * red / 255 + coefficients[4] * normalized * normalized +
            coefficients[5] * ((index % _columns + .5) / _columns - .5) +
            coefficients[6] * ((index / _columns + .5) / _rows - .5), 0, 255);
    }

    private double Error(double[][] coefficients, double[] reference, double[] current, int index, double[]? colorOffset = null)
        => ColorError(coefficients, reference, index, current, index, colorOffset);

    private double ColorError(double[][] coefficients, double[] colors, int colorIndex,
        double[] current, int index, double[]? colorOffset)
    {
        double squared = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double prediction = Predict(coefficients[channel], colors[colorIndex * 3], colors[colorIndex * 3 + 1],
                colors[colorIndex * 3 + 2], index, channel);
            squared += Math.Pow(current[index * 3 + channel] - prediction - (colorOffset?[channel] ?? 0), 2);
        }
        return Math.Sqrt(squared / 3);
    }

    private double EdgeError(double[][] coefficients, double[] colors, double[] current, int index,
        Dictionary<int, double[]> offsets, double best, double threshold)
    {
        Span<double> darkest = stackalloc double[3], brightest = stackalloc double[3], prediction = stackalloc double[3];
        double minimumBrightness = double.PositiveInfinity, maximumBrightness = double.NegativeInfinity;
        for (int color = 0; color < colors.Length / 3; color++)
        {
            offsets.TryGetValue(ColorKey(colors, color), out var offset);
            double squared = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                prediction[channel] = Predict(coefficients[channel], colors[color * 3], colors[color * 3 + 1],
                    colors[color * 3 + 2], index, channel) + (offset?[channel] ?? 0);
                squared += Math.Pow(current[index * 3 + channel] - prediction[channel], 2);
            }
            best = Math.Min(best, Math.Sqrt(squared / 3));
            if (best <= threshold) break;
            double brightness = prediction[0] * .114 + prediction[1] * .587 + prediction[2] * .299;
            if (brightness < minimumBrightness) { minimumBrightness = brightness; prediction.CopyTo(darkest); }
            if (brightness > maximumBrightness) { maximumBrightness = brightness; prediction.CopyTo(brightest); }
        }
        if (best <= threshold || !double.IsFinite(minimumBrightness)) return best;
        // Camera pixels and Sample() mix adjacent projected glyph/background
        // colors. Accept mixtures along their local RGB segment, not arbitrary
        // new colors inside a broad per-channel range.
        double numerator = 0, denominator = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double delta = brightest[channel] - darkest[channel];
            numerator += (current[index * 3 + channel] - darkest[channel]) * delta;
            denominator += delta * delta;
        }
        double amount = denominator > 1e-10 ? Math.Clamp(numerator / denominator, 0, 1) : 0;
        double mixtureError = 0;
        for (int channel = 0; channel < 3; channel++)
            mixtureError += Math.Pow(current[index * 3 + channel] -
                (darkest[channel] + amount * (brightest[channel] - darkest[channel])), 2);
        best = Math.Min(best, Math.Sqrt(mixtureError / 3));
        return best;
    }

    private static int ColorKey(double[] reference, int index) => (int)reference[index * 3] / 16 +
        ((int)reference[index * 3 + 1] / 16) * 16 + ((int)reference[index * 3 + 2] / 16) * 256;

    private Dictionary<int, double[]> ColorResiduals(double[] reference, double[] current, bool[] allowed, double[][] coefficients)
    {
        var offsets = new Dictionary<int, double[]>();
        foreach (var group in Enumerable.Range(0, allowed.Length).Where(index => allowed[index]).GroupBy(index => ColorKey(reference, index)))
        {
            // A single uniquely colored control might be covered by the arriving hand.
            // Its pixels cannot establish their own expected appearance: require matching
            // rendered colors at separated board locations before applying a correction.
            if (!Distributed(group)) continue;
            double[] correction = new double[3];
            for (int channel = 0; channel < 3; channel++)
                correction[channel] = Median(group.Select(index => current[index * 3 + channel] -
                    Predict(coefficients[channel], reference[index * 3], reference[index * 3 + 1], reference[index * 3 + 2], index, channel)));
            offsets[group.Key] = correction;
        }
        return offsets;
    }

    private bool Distributed(IEnumerable<int> group) => group.Count() >= 40 &&
        (group.Max(index => index % _columns) - group.Min(index => index % _columns) >= _columns * .25 ||
         group.Max(index => index / _columns) - group.Min(index => index / _columns) >= _rows * .50);

    private static double[] Solve(double[,] matrix, double[] values)
    {
        for (int pivot = 0; pivot < Features; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < Features; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-12) return Enumerable.Repeat(double.NaN, Features).ToArray();
            if (best != pivot)
            {
                for (int column = 0; column < Features; column++)
                    (matrix[best, column], matrix[pivot, column]) = (matrix[pivot, column], matrix[best, column]);
                (values[best], values[pivot]) = (values[pivot], values[best]);
            }
            double divisor = matrix[pivot, pivot];
            for (int column = pivot; column < Features; column++) matrix[pivot, column] /= divisor;
            values[pivot] /= divisor;
            for (int row = 0; row < Features; row++)
            {
                if (row == pivot) continue;
                double amount = matrix[row, pivot];
                for (int column = pivot; column < Features; column++) matrix[row, column] -= amount * matrix[pivot, column];
                values[row] -= amount * values[pivot];
            }
        }
        return values;
    }

    private bool? CheckIlluminatedCore(double[] current, HandAcquisitionHint hint, PhotometricFit? fit,
        out string reason, out double? controlCoverage, out double? controlTriggerCoverage, out double? whiteLuminance)
    {
        controlCoverage = null;
        controlTriggerCoverage = null;
        whiteLuminance = null;
        int[] core = Enumerable.Range(0, _mask.Length).Where(index => _mask[index] &&
            Distance(_locations[index], hint.Center) < hint.RadiusPixels * .64).ToArray();
        if (core.Length < 24) { reason = "insufficient-white-core"; return null; }
        int[] bright = core.OrderBy(index => Luminance(current, index)).Skip((int)(core.Length * .72)).ToArray();
        double[] reference = Enumerable.Range(0, 3).Select(channel => Median(bright.Select(index => current[index * 3 + channel]))).ToArray();
        double referenceLuminance = reference[0] * .114 + reference[1] * .587 + reference[2] * .299;
        whiteLuminance = referenceLuminance;
        var whiteField = FitPeripheralIlluminatedWhite(current, core, bright, hint);
        bool[] foreground = new bool[_mask.Length];
        int changed = 0;
        foreach (int index in core)
        {
            double expectedBlue = WhiteAt(0), expectedGreen = WhiteAt(1), expectedRed = WhiteAt(2);
            double expectedLuminance = expectedBlue * .114 + expectedGreen * .587 + expectedRed * .299;
            double brightnessDrop = expectedLuminance - Luminance(current, index);
            double colorError = Math.Sqrt(Enumerable.Range(0, 3).Sum(channel =>
                Math.Pow(current[index * 3 + channel] - WhiteAt(channel) + brightnessDrop, 2)) / 3);
            if (brightnessDrop <= 20 && colorError <= 22) continue;
            foreground[index] = true; changed++;

            double WhiteAt(int channel) => whiteField is null ? reference[channel] :
                IlluminatedWhiteAt(whiteField[channel], _locations[index], hint);
        }
        bool controlled = _templateMask is not null && _controlBoardAreas is not null;
        // Renewal needs the same measured obstruction as unlit acquisition.
        // The disk's own shading or a few dark pixels cannot retain a light by
        // inheriting the coverage that started it. Components counts only fresh
        // residual cells, and uses their mapped board area before dilation.
        if (changed >= Math.Max(8, core.Length * .035))
        {
            var components = Components(foreground, _lastTime, Math.Max(6, (int)(core.Length * .025)),
                searchMask: controlled ? _templateMask : null,
                evidenceArea: controlled ? _templateMask!.Count(value => value) : null,
                requiredControlRegion: _controlTriggerBoardAreas is null ? null : ControlRegionAt(hint.Center));
            if (components.Count > 0)
            {
                var renewal = components.OrderByDescending(component => component.ControlCoverage).First();
                controlCoverage = controlled ? renewal.ControlCoverage : null;
                controlTriggerCoverage = controlled ? renewal.ControlTriggerCoverage : null;
                reason = "foreground-under-search-light";
                return true;
            }
        }

        // A uniformly dark core might be entirely occluded. Do not call it empty unless
        // the visible color is a plausible lit board, or agrees with the fitted white response.
        bool plausibleWhite = referenceLuminance >= 115 && reference.Max() - reference.Min() <= 48;
        if (!plausibleWhite && fit is not null)
        {
            int centerIndex = core.OrderBy(index => Distance(_locations[index], hint.Center)).First();
            double colorError = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                double prediction = Predict(fit.Coefficients[channel], 255, 255, 255, centerIndex, channel);
                colorError += Math.Pow(reference[channel] - prediction, 2);
            }
            plausibleWhite = Math.Sqrt(colorError / 3) <= 32 && referenceLuminance >= 75;
        }
        // Clipped white hides pale skin as well as the board. Report it so the
        // light can be dimmed; it is no evidence that the hand has left.
        if (plausibleWhite && referenceLuminance >= SaturatedIlluminatedWhite)
        {
            reason = "search-light-saturated";
            return null;
        }
        reason = plausibleWhite ? "empty-search-light" : "white-core-appearance-uncertain";
        return plausibleWhite ? false : null;
    }

    private List<HandAcquisitionHint> Components(bool[] foreground, DateTimeOffset observedAt, int? minimumEvidence = null,
        bool[]? searchMask = null, int? evidenceArea = null, int? requiredControlRegion = null)
    {
        var mask = searchMask ?? _mask;
        int area = evidenceArea ?? _validCount;
        bool controlled = searchMask is not null && ReferenceEquals(searchMask, _templateMask) && _controlBoardAreas is not null;
        bool[] joined = new bool[foreground.Length];
        for (int index = 0; index < foreground.Length; index++)
        {
            if (!foreground[index]) continue;
            int x = index % _columns, y = index / _columns;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < _columns && ny < _rows && mask[ny * _columns + nx]) joined[ny * _columns + nx] = true;
            }
        }
        // Control-specific area rejects minor changes without imposing a minimum
        // width proportional to the entire webcam on short on-board buttons.
        int minimum = minimumEvidence ?? (controlled ? 12 : Math.Max(12, (int)(area * .006)));
        var candidates = new List<(HandAcquisitionHint Hint, int Count)>();
        int[] queue = new int[joined.Length];
        for (int start = 0; start < joined.Length; start++)
        {
            if (!joined[start]) continue;
            joined[start] = false;
            int count = 1, read = 0, evidence = 0, left = _columns, top = _rows, right = 0, bottom = 0;
            double[]? controlEvidence = controlled ? new double[_controlBoardAreas!.Length] : null;
            double[]? triggerEvidence = controlled && _controlTriggerBoardAreas is not null
                ? new double[_controlTriggerBoardAreas.Length] : null;
            queue[0] = start;
            while (read < count)
            {
                int index = queue[read++], x = index % _columns, y = index / _columns;
                if (foreground[index])
                {
                    evidence++; left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    if (controlled && _controlRegions![index] is var region && region >= 0)
                        controlEvidence![region] += _sampleBoardAreas![index];
                    if (triggerEvidence is not null && _controlTriggerRegions![index] is var triggerRegion && triggerRegion >= 0)
                        triggerEvidence[triggerRegion] += _sampleBoardAreas![index];
                }
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= _columns || ny >= _rows) continue;
                    int adjacent = ny * _columns + nx;
                    if (!joined[adjacent]) continue;
                    joined[adjacent] = false; queue[count++] = adjacent;
                }
            }
            double? controlCoverage = null, controlTriggerCoverage = null;
            if (controlled)
            {
                var qualified = Enumerable.Range(0, controlEvidence!.Length).Where(region =>
                    (requiredControlRegion is null || region == requiredControlRegion) &&
                    controlEvidence[region] / _controlBoardAreas![region] >= MinimumControlCoverage &&
                    (triggerEvidence is null || triggerEvidence[region] / _controlTriggerBoardAreas![region] >= MinimumControlCoverage))
                    .OrderByDescending(region => controlEvidence[region] / _controlBoardAreas![region]).ToArray();
                if (qualified.Length == 0) continue;
                int selected = qualified[0];
                controlCoverage = controlEvidence[selected] / _controlBoardAreas![selected];
                controlTriggerCoverage = triggerEvidence is null ? null : triggerEvidence[selected] / _controlTriggerBoardAreas![selected];
            }
            if (controlCoverage < MinimumControlCoverage) continue;
            int occupiedCell = controlled ? 1 : 0;
            double minimumSide = controlled ? 3 * _scale : Math.Max(3 * _scale, Math.Min(_width, _height) * .03);
            if (evidence < minimum || Math.Min(right - left + occupiedCell, bottom - top + occupiedCell) * _scale < minimumSide) continue;
            var center = new PixelPoint(_left + (left + right + 1) * _scale / 2, _top + (top + bottom + 1) * _scale / 2);
            if (!Inside(center, _polygon)) continue;
            double extent = Math.Max(right - left + 1, bottom - top + 1) * _scale;
            double shortSide = Math.Min(_width, _height), maxSide = shortSide * .60;
            int side = (int)Math.Ceiling(Math.Clamp(extent * 1.7 + shortSide * .10, Math.Min(maxSide, Math.Max(48, shortSide * .26)), maxSide));
            int cropX = Math.Clamp((int)Math.Round(center.X - side / 2.0), 0, _width - side);
            int cropY = Math.Clamp((int)Math.Round(center.Y - side / 2.0), 0, _height - side);
            double radius = Math.Clamp(extent * .55 + shortSide * .025, shortSide * .055, shortSide * .13);
            candidates.Add((new(new(cropX, cropY, side, side), center, radius, observedAt,
                evidence / (double)Math.Max(1, area), controlCoverage, controlTriggerCoverage), evidence));
        }
        var result = new List<HandAcquisitionHint>();
        foreach (var item in candidates.OrderByDescending(item => item.Count))
        {
            if (result.Any(existing => Distance(existing.Center, item.Hint.Center) <
                Math.Min(existing.SearchBounds.Width, item.Hint.SearchBounds.Width) * .45)) continue;
            result.Add(item.Hint);
            if (result.Count == 2) break;
        }
        return result;
    }

    private int ControlRegionAt(PixelPoint point)
    {
        if (_scene?.BoardSearchRegions is not { } regions || !BoardPosition(_scene, point, out double u, out double v)) return -1;
        for (int region = 0; region < regions.Count; region++)
            if (WithinRegions([regions[region]], u, v)) return region;
        return -1;
    }

    // An arm reaching past a nearer control overflows the far control's own
    // connected-foreground window, leaving assistance on the fingertips only.
    // When the second disturbed control lies wholly on the viewer's side of
    // the target, span both measured caption cores so the palm is lit too.
    // Geometry only: coverage, confirmation and gestures are unchanged.
    private HandAcquisitionHint? ReachingHandSpan(HandAcquisitionHint target, HandAcquisitionHint behind)
    {
        if (_scene?.BoardSearchRegions is not { } regions ||
            target.ControlCoverage is not >= MinimumControlCoverage || target.ControlTriggerCoverage is not >= MinimumControlCoverage ||
            behind.ControlCoverage is not >= MinimumControlCoverage || behind.ControlTriggerCoverage is not >= MinimumControlCoverage)
            return null;
        int targetRegion = ControlRegionAt(target.Center), behindRegion = ControlRegionAt(behind.Center);
        if (targetRegion < 0 || behindRegion < 0 || targetRegion == behindRegion ||
            !BoardPosition(_scene, behind.Center, out _, out double behindV) ||
            behindV < regions[targetRegion].Y + regions[targetRegion].Height) return null;
        double targetCore = target.RadiusPixels * .64, behindCore = behind.RadiusPixels * .64;
        double left = Math.Min(target.Center.X - targetCore, behind.Center.X - behindCore);
        double top = Math.Min(target.Center.Y - targetCore, behind.Center.Y - behindCore);
        double right = Math.Max(target.Center.X + targetCore, behind.Center.X + behindCore);
        double bottom = Math.Max(target.Center.Y + targetCore, behind.Center.Y + behindCore);
        left = Math.Max(0, left); top = Math.Max(0, top);
        right = Math.Min(_width, right); bottom = Math.Min(_height, bottom);
        if (right <= left || bottom <= top) return null;
        // The hint's own size and frame caps reject controls too far apart.
        var spanned = (target with { CandidateBounds = new(left, top, right - left, bottom - top) })
            .ConstrainToFrame(_width, _height);
        return spanned.ValidatedCandidateBounds is null ? null : spanned;
    }

    private static bool IsValidLight(HandAcquisitionHint? hint) => hint is not null &&
        double.IsFinite(hint.Center.X) && double.IsFinite(hint.Center.Y) && double.IsFinite(hint.RadiusPixels) && hint.RadiusPixels > 0;
    private static double Luminance(double[] pixels, int index) =>
        pixels[index * 3] * .114 + pixels[index * 3 + 1] * .587 + pixels[index * 3 + 2] * .299;
    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    private static bool Inside(PixelPoint point, IReadOnlyList<PixelPoint> polygon)
    {
        bool inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            PixelPoint a = polygon[current], b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
