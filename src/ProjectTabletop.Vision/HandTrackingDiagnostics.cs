namespace ProjectTabletop.Vision;

/// <summary>A camera-pixel rectangle; diagnostic data only.</summary>
public readonly record struct HandTrackingBounds(double X, double Y, double Width, double Height);

/// <summary>A palm-search view and the proposals that passed its existing palm threshold/NMS.</summary>
public sealed record HandTrackingSearchDiagnostics(string Source, HandTrackingBounds ViewBounds, int ProposalCount);

/// <summary>
/// One attempted landmark inference. Index identifies this attempt throughout
/// the snapshot. A returned hand rejected by temporal IoU retains its landmarks.
/// NmsResult is not-eligible, selected, overlap, or selection-limit.
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
