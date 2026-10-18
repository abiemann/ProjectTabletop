using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

// Water Garden and Paint share one saved double-bar marker profile per camera.
internal static class DoubleBarProfileSelection
{
    public static BlackTipProfile? Usable(BlackTipProfile? saved) =>
        saved is { IsValid: true, BarCount: 2 } ? saved : null;
}
