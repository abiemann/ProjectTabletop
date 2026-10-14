using System.Numerics;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly record struct WaterGardenRockPlacement(Rect Bounds, double SubmergedHeight);

internal static class WaterGardenRockLayout
{
    // Visual two-centimetre immersion on the garden's nominal metre-long plane.
    private const float SubmersionDepth = .02f;

    // Board rendering and silhouette hit testing use the same layout.
    // Fit the complete image inside the water's narrowing outline,
    // including a water margin above the exposed vertical sides.
    public static (WaterGardenRockPlacement First, WaterGardenRockPlacement Second) GetPlacements(
        double aspect, double imageAspect, double opaqueFoot, float entranceProgress = 1)
    {
        double scale = Math.Min(1, aspect);
        double margin = .02 / aspect;
        double farHeight = .155 * scale, farWidth = farHeight * imageAspect / aspect;
        double nearHeight = .250 * scale, nearWidth = nearHeight * imageAspect / aspect;
        double farTop = WaterGardenView.SurfaceToScreen(new(.5f, .06f)).Y;
        double nearBottom = WaterGardenView.SurfaceToScreen(new(.5f, .87f)).Y;
        double nearTop = nearBottom - nearHeight;
        double farEdge = Math.Max(LeftWaterEdge(farTop, aspect), LeftWaterEdge(farTop + farHeight, aspect));
        double nearEdge = Math.Max(LeftWaterEdge(nearTop, aspect), LeftWaterEdge(nearBottom, aspect));
        return (Immerse(new(farEdge + margin, farTop, farWidth, farHeight)),
            Immerse(new(1 - nearEdge - margin - nearWidth, nearTop, nearWidth, nearHeight)));

        WaterGardenRockPlacement Immerse(Rect bounds)
        {
            // Anchor at the actual opaque foot, not the PNG's transparent padding.
            // The same camera and depth place the artwork and its input silhouette.
            var foot = new Vector2((float)(bounds.X + bounds.Width / 2),
                (float)(bounds.Y + bounds.Height * opaqueFoot));
            var surface = WaterGardenView.ScreenToSurface(foot);
            float worldY = surface.Y - .5f;
            var camera = WaterGardenView.CameraAt(entranceProgress);
            var final = WaterGardenView.CameraAt(1);
            var basePoint = new Vector3(surface.X - .5f, worldY, -SubmersionDepth);
            var submerged = WaterGardenView.Project(basePoint, 1, camera);
            var waterline = WaterGardenView.Project(basePoint with { Z = 0 }, 1, camera);
            float apparentScale = (camera.Zoom * camera.Distance /
                    (camera.Distance - worldY * camera.SinTilt + SubmersionDepth * camera.CosTilt)) /
                (final.Zoom * final.Distance / (final.Distance - worldY * final.SinTilt));
            double width = bounds.Width * apparentScale, height = bounds.Height * apparentScale;
            return new(new Rect(submerged.X - width / 2, submerged.Y - height * opaqueFoot,
                width, height), submerged.Y - waterline.Y);
        }
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
