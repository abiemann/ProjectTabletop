using System.Numerics;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly record struct WaterGardenCamera(float SinTilt, float CosTilt,
    float Distance, float Zoom, float CentreX, float CentreY);

/// <summary>The garden camera shared by rendering and tracked-stick input.
/// Screen coordinates are normalized board coordinates; surface coordinates are
/// normalized simulation coordinates. The world plane is aspect by one at z=0.</summary>
internal static class WaterGardenView
{
    public const float EntranceDurationSeconds = 3f;
    public const float TiltRadians = MathF.PI / 4;
    public const float SinTilt = .70710678f;
    public const float CosTilt = .70710678f;
    // The settled view retains the familiar 45-degree angle. A slightly longer
    // lens enlarges the far edge without losing the rounded near rim.
    public const float Distance = 3.3f;
    public const float Zoom = .891f;
    public const float CentreX = .5f;
    public const float CentreY = .493f;
    public const float RimHeight = .04f;
    public const float PondCornerRadius = .18f;
    public const float SurfaceEdgeMargin = .004f;
    private static readonly WaterGardenCamera FinalCamera =
        new(SinTilt, CosTilt, Distance, Zoom, CentreX, CentreY);

    /// <summary>A short dolly-in and downward pan, ending at the camera used for
    /// calibrated stick projection. Progress is normalized from zero to one.</summary>
    public static WaterGardenCamera CameraAt(float progress)
    {
        if (progress >= 1) return FinalCamera;
        // A small establishing hold makes the movement intentional. Ease both
        // ends so the image never jumps when the scene begins or settles.
        float t = Math.Clamp((progress - .06f) / .94f, 0, 1);
        t = t * t * (3 - 2 * t);
        const float initialTilt = 39f * MathF.PI / 180;
        float tilt = initialTilt + (TiltRadians - initialTilt) * t;
        return new(MathF.Sin(tilt), MathF.Cos(tilt),
            3.85f + (Distance - 3.85f) * t,
            .68f + (Zoom - .68f) * t,
            CentreX,
            .45f + (CentreY - .45f) * t);
    }

    public static Vector2 ScreenToSurface(Vector2 screen)
        => ScreenToSurface(screen, FinalCamera);

    public static Vector2 ScreenToSurface(Vector2 screen, WaterGardenCamera camera)
    {
        if (!IsFinite(screen)) return new(float.NaN, float.NaN);
        float screenY = screen.Y - camera.CentreY;
        float focalLength = camera.Zoom * camera.Distance;
        float denominator = focalLength * camera.CosTilt + screenY * camera.SinTilt;
        if (!float.IsFinite(denominator) || denominator <= 1e-6f)
            return new(float.NaN, float.NaN);
        float worldY = screenY * camera.Distance / denominator;
        // With no camera roll, the board aspect cancels in this inverse x mapping.
        float surfaceX = (screen.X - camera.CentreX) *
            (camera.Distance - worldY * camera.SinTilt) / focalLength + .5f;
        return new(surfaceX, worldY + .5f);
    }

    public static Vector2 SurfaceToScreen(Vector2 surface) =>
        Project(new(surface.X - .5f, surface.Y - .5f, 0), 1);

    public static Vector2 Project(Vector3 world, float aspect)
        => Project(world, aspect, FinalCamera);

    public static Vector2 Project(Vector3 world, float aspect, WaterGardenCamera camera)
    {
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z) ||
            !float.IsFinite(aspect) || aspect <= 0)
            return new(float.NaN, float.NaN);
        float denominator = camera.Distance - world.Y * camera.SinTilt - world.Z * camera.CosTilt;
        if (!float.IsFinite(denominator) || denominator <= 1e-6f)
            return new(float.NaN, float.NaN);
        float focalLength = camera.Zoom * camera.Distance;
        return new(focalLength * world.X / denominator / aspect + camera.CentreX,
            focalLength * (world.Y * camera.CosTilt - world.Z * camera.SinTilt) / denominator + camera.CentreY);
    }

    public static bool ContainsWater(Vector2 screen, float aspect = 1)
    {
        var surface = ScreenToSurface(screen);
        if (!IsFinite(surface) ||
            surface.X < SurfaceEdgeMargin || surface.X > 1 - SurfaceEdgeMargin ||
            surface.Y < SurfaceEdgeMargin || surface.Y > 1 - SurfaceEdgeMargin)
            return false;
        float radius = PondCornerRadius * Math.Min(aspect, 1);
        var q = Vector2.Abs(new((surface.X - .5f) * aspect, surface.Y - .5f)) -
            new Vector2(aspect * .5f - radius, .5f - radius);
        float roundedDistance = Vector2.Max(q, Vector2.Zero).Length() + Math.Min(Math.Max(q.X, q.Y), 0) - radius;
        if (roundedDistance > -SurfaceEdgeMargin) return false;
        // The raised near rim hides a narrow strip of the water plane. Follow
        // the sight line toward the camera to reject pixels landing on that rim.
        float planeY = surface.Y - .5f;
        float atRimY = planeY + (Distance * SinTilt - planeY) * RimHeight / (Distance * CosTilt);
        return atRimY < .5f - SurfaceEdgeMargin;
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
