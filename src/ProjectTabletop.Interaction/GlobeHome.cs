namespace ProjectTabletop.Interaction;

/// <summary>The Globe's fixed opening view. No location service or network is used.</summary>
public static class GlobeHome
{
    // Captured from the user's settled Globe view on 2026-10-05. The automatic
    // spin continues during the entrance, so reach this rotation at full size.
    public const double DefaultRestingRotationDegrees = 58.29723737777772;
    public const double DefaultViewLatitudeDegrees = 34.05222222222222;

    public static GlobeState CreateDefault() => new(
        360 - DefaultRestingRotationDegrees +
            GlobeState.EntranceDuration.TotalSeconds * GlobeState.RotationDegreesPerSecond,
        DefaultViewLatitudeDegrees);
}
