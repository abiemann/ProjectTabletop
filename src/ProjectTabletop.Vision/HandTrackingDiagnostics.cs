namespace ProjectTabletop.Vision;

/// <summary>A native camera-pixel rectangle for search hints and diagnostics.</summary>
public readonly record struct HandTrackingBounds(double X, double Y, double Width, double Height);

/// <summary>Gains applied only to the palm-search thumbnail, with bright-pixel evidence.</summary>
public sealed record HandTrackingColorCorrection(double RedGain, double GreenGain, double BlueGain,
    int ReferencePixelCount);

/// <summary>A palm-search view (full-frame, tile, motion-roi, or motion-roi-normalized) and the proposals
/// that passed the unchanged palm threshold/NMS. Bounds are in camera pixels.</summary>
public sealed record HandTrackingSearchDiagnostics(string Source, HandTrackingBounds ViewBounds, int ProposalCount,
    HandTrackingColorCorrection? ColorCorrection = null);

/// <summary>
/// One attempted landmark inference. Index identifies this attempt throughout
/// the snapshot. A returned hand rejected by temporal IoU retains its landmarks.
/// Search sources include motion-roi for an externally supplied square crop and
/// motion-roi-normalized for its fallback color-compensated palm-search input;
/// its palm coordinates and returned landmarks remain in the full camera frame.
/// NmsResult is not-eligible, selected, overlap, tracked-continuity, or selection-limit.
/// Tracked-continuity identifies a near-tied search fit suppressed by a reliable
/// current-frame tracked ROI despite the search fit's slightly higher model score.
/// </summary>
public sealed record HandTrackingCandidateDiagnostics(int Index, string Source, HandTrackingBounds? SearchViewBounds, int? PreviousHandIndex,
    HandTrackingBounds PalmBounds, double PalmScore, double? HandConfidence, string Result,
    double? PreviousBoundsIou, HandDetection? Hand, int? PreNmsIndex, string NmsResult,
    int? SuppressedByCandidateIndex);

/// <summary>
/// An immutable snapshot of one Detect call. Collections are read-only copies;
/// capturing them does not alter inference, thresholds, tracking or selection.
/// </summary>
public sealed record HandTrackingDiagnostics(bool FullSearch, string FullSearchReason, int PreviousHandCount,
    int TrackedRoiAttempts, IReadOnlyList<HandTrackingSearchDiagnostics> Searches,
    IReadOnlyList<HandTrackingCandidateDiagnostics> Candidates, IReadOnlyList<int> PreNmsCandidateIndices,
    IReadOnlyList<int> SelectedCandidateIndices);
