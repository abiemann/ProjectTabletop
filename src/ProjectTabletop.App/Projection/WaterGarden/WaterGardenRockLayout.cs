using System.Numerics;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal static class WaterGardenRockLayout
{
    // Board rendering, silhouette hit testing and offline thumbnails use the
    // same layout. Fit the complete image inside the water's narrowing outline,
    // including space for its small contact shadow and the raised front wall.
    public static (Rect First, Rect Second) GetPlacements(double aspect, double imageAspect)
    {
        double scale = Math.Min(1, aspect);
        double margin = .02 / aspect;
        double farHeight = .18 * scale, farWidth = farHeight * imageAspect / aspect;
        double nearHeight = .29 * scale, nearWidth = nearHeight * imageAspect / aspect;
        const double farTop = .205, nearBottom = .745;
        double nearTop = nearBottom - nearHeight;
        return (new(LeftWaterEdge(farTop) + margin, farTop, farWidth, farHeight),
            new(1 - LeftWaterEdge(nearTop) - margin - nearWidth, nearTop, nearWidth, nearHeight));
    }

    private static double LeftWaterEdge(double screenY)
    {
        var surface = WaterGardenView.ScreenToSurface(new Vector2(.5f, (float)screenY));
        return WaterGardenView.SurfaceToScreen(new Vector2(0, surface.Y)).X;
    }
}
