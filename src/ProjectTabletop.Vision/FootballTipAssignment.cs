namespace ProjectTabletop.Vision;

public enum FootballTipAction { Publish, Hold, Clear }

/// <summary>Tip is in raw camera pixels. FieldPoint is the normalized pitch location
/// supplied by the caller's mapping, present only for Publish.</summary>
public sealed record FootballTipDecision(FootballTipAction Action, ColorTipObservation? Tip,
    PixelPoint? FieldPoint, string Reason)
{
    public string Source { get; init; } = "none";
}

/// <summary>
/// Assigns black markers (single bars or learned pairs) by calibrated pitch half before temporal
/// association: player 1 owns the left half, player 2 the right. Two markers in one half are
/// ambiguous and clear that player. A brief miss or reconfirmation holds instead, so one blurred
/// frame does not pause play; the caller's freshness timeout clears a bar that stays away.
/// </summary>
public static class FootballTipAssignment
{
    /// <param name="toField">Maps a raw camera point to normalized pitch coordinates, or null
    /// when it lies outside the calibrated grass surface. Grass behind a goal line can
    /// produce an X outside 0–1; its sign still identifies the owning half.</param>
    public static FootballTipDecision Update(ColorTipTracker tracker, int player, ColorTipDetectionResult detection,
        Func<PixelPoint, PixelPoint?> toField, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(toField);
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        if (detection.Reason.StartsWith("waiting-for-projected-", StringComparison.Ordinal))
        {
            tracker.Reset();
            return new(FootballTipAction.Clear, null, null, "Waiting for fresh camera and projection images.");
        }
        var inHalf = detection.Candidates.Select(tip => (Tip: tip, Field: toField(tip.Center)))
            .Where(item => item.Field is { } field && (field.X < .5 ? 0 : 1) == player).ToArray();
        // Never pick a nearest winner: identical bars have no identity beyond their half.
        if (inHalf.Length > 1)
        {
            tracker.Reset();
            return new(FootballTipAction.Clear, null, null, "Show only one black marker in this player's half.");
        }
        var track = tracker.Update(new(inHalf.Select(item => item.Tip).ToArray(), detection.Reason), frameTime, now);
        if (track.Confirmed && track.Observation is { } tip)
            return new(FootballTipAction.Publish, tip, inHalf[0].Field, "Black marker tracked.");
        return new(FootballTipAction.Hold, null, null,
            $"Looking for a black marker in the {(player == 0 ? "left" : "right")} half.");
    }
}
