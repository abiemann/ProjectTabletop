using System.Numerics;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal static class WaterGardenRockLayout
{
    // Board rendering and silhouette hit testing use the same layout.
    // Fit the complete image inside the water's narrowing outline,
    // including space for its small contact shadow and the raised front wall.
    public static (Rect First, Rect Second) GetPlacements(double aspect, double imageAspect)
    {
        double scale = Math.Min(1, aspect);
        double margin = .02 / aspect;
        double farHeight = .18 * scale, farWidth = farHeight * imageAspect / aspect;
        double nearHeight = .29 * scale, nearWidth = nearHeight * imageAspect / aspect;
        const double farTop = .24, nearBottom = .745;
        double nearTop = nearBottom - nearHeight;
        double farEdge = Math.Max(LeftWaterEdge(farTop, aspect), LeftWaterEdge(farTop + farHeight, aspect));
        double nearEdge = Math.Max(LeftWaterEdge(nearTop, aspect), LeftWaterEdge(nearBottom, aspect));
        return (new(farEdge + margin, farTop, farWidth, farHeight),
            new(1 - nearEdge - margin - nearWidth, nearTop, nearWidth, nearHeight));
    }

    private static double LeftWaterEdge(double screenY, double aspect)
    {
        var surface = WaterGardenView.ScreenToSurface(new Vector2(.5f, (float)screenY));
        double radius = WaterGardenView.PondCornerRadius * Math.Min(aspect, 1);
        double endDistance = Math.Min(surface.Y, 1 - surface.Y);
        double inset = endDistance >= radius ? 0 : radius -
            Math.Sqrt(Math.Max(0, radius * radius - Math.Pow(radius - endDistance, 2)));
        return WaterGardenView.SurfaceToScreen(new Vector2((float)(inset / aspect), surface.Y)).X;
    }
}
