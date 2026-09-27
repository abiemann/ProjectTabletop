namespace ProjectTabletop.Vision;

/// <summary>
/// Chooses among fresh inferences of this frame. A reliable tracked crop wins
/// a near-tie with an overlapping search crop; no previous landmarks are used.
/// </summary>
internal static class HandCandidateSelector
{
    private const double MinimumTrackedConfidence = 0.90;
    private const double MinimumPreviousBoundsIou = 0.65;
    private const double MaximumSearchAdvantage = 0.01;
    private const double DuplicateBoundsIou = 0.30;

    public static IReadOnlyList<HandDetection> Select(IReadOnlyList<HandDetection> hands,
        IReadOnlyDictionary<HandDetection, double> trackedPreviousBoundsIou,
        Action<HandDetection, string, HandDetection?>? record = null)
    {
        var selected = new List<HandDetection>(2);
        foreach (HandDetection hand in hands.OrderByDescending(RankingScore))
        {
            HandDetection? suppressor = selected.FirstOrDefault(other => Overlap(hand, other) > DuplicateBoundsIou);
            if (suppressor is not null)
            {
                bool continuityDecided = hand.Confidence > suppressor.Confidence && PreferTracked(suppressor, hand);
                record?.Invoke(hand, continuityDecided ? "tracked-continuity" : "overlap", suppressor);
                continue;
            }
            selected.Add(hand);
            record?.Invoke(hand, "selected", null);
            if (selected.Count == 2) break;
        }
        return selected;

        double RankingScore(HandDetection hand)
        {
            if (trackedPreviousBoundsIou.ContainsKey(hand)) return hand.Confidence;
            // Keep unrelated candidates' true scores, including arriving hands.
            // Lower only a competing search fit just below its best eligible ROI.
            // A clearly stronger search fit still outranks and replaces that ROI.
            double preferredConfidence = hands.Where(tracked => PreferTracked(tracked, hand))
                .Select(tracked => tracked.Confidence).DefaultIfEmpty(double.NegativeInfinity).Max();
            return double.IsFinite(preferredConfidence)
                ? Math.Min(hand.Confidence, Math.BitDecrement(preferredConfidence)) : hand.Confidence;
        }

        bool PreferTracked(HandDetection tracked, HandDetection search) =>
            !trackedPreviousBoundsIou.ContainsKey(search) &&
            trackedPreviousBoundsIou.TryGetValue(tracked, out double previousIou) &&
            tracked.Confidence >= MinimumTrackedConfidence && previousIou >= MinimumPreviousBoundsIou &&
            search.Confidence <= tracked.Confidence + MaximumSearchAdvantage &&
            Overlap(tracked, search) > DuplicateBoundsIou;
    }

    private static double Overlap(HandDetection first, HandDetection second)
    {
        var a = Bounds(first);
        var b = Bounds(second);
        double width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double intersection = width * height;
        return intersection / ((a.Right - a.Left) * (a.Bottom - a.Top) +
            (b.Right - b.Left) * (b.Bottom - b.Top) - intersection);
    }

    private static (double Left, double Top, double Right, double Bottom) Bounds(HandDetection hand) =>
        (hand.Landmarks.Min(point => point.X), hand.Landmarks.Min(point => point.Y),
            hand.Landmarks.Max(point => point.X), hand.Landmarks.Max(point => point.Y));
}
