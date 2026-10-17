using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

internal static class WandMarkerProfileSelection
{
    public static BlackTipProfile? BarProfile(string? activeMode, BlackTipProfile? savedBar) =>
        activeMode == "bar" ? savedBar : null;

    public static BlackTipProfile? PaintBarProfile(BlackTipProfile? savedBar) =>
        savedBar is { IsValid: true, BarCount: 2 } ? savedBar : null;

    public static bool HasUsableProfile(string? activeMode, BlackTipProfile? savedBar, bool hasEyeProfile) =>
        BarProfile(activeMode, savedBar) is { IsValid: true } || hasEyeProfile;
}
