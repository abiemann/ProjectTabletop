using System.Numerics;

namespace ProjectTabletop.App.Projection.WaterGarden;

/// <summary>The fixed garden camera shared by rendering and tracked-stick input.
/// Screen coordinates are normalized board coordinates; surface coordinates are
/// normalized simulation coordinates. The world plane is aspect by one at z=0.</summary>
internal static class WaterGardenView
{
    public const float TiltRadians = MathF.PI / 4;
    public const float SinTilt = .70710678f;
    public const float CosTilt = .70710678f;
    public const float Distance = 2.6f;
    public const float Zoom = .87f;
    public const float CentreX = .5f;
    public const float CentreY = .45f;
    public const float RimHeight = .065f;
    public const float SurfaceEdgeMargin = .004f;
    private const float FocalLength = Zoom * Distance;

    public static Vector2 ScreenToSurface(Vector2 screen)
    {
        if (!IsFinite(screen)) return new(float.NaN, float.NaN);
        float screenY = screen.Y - CentreY;
        float denominator = FocalLength * CosTilt + screenY * SinTilt;
        if (!float.IsFinite(denominator) || denominator <= 1e-6f)
            return new(float.NaN, float.NaN);
        float worldY = screenY * Distance / denominator;
        // With no camera roll, the board aspect cancels in this inverse x mapping.
        float surfaceX = (screen.X - CentreX) * (Distance - worldY * SinTilt) / FocalLength + .5f;
        return new(surfaceX, worldY + .5f);
    }

    public static Vector2 SurfaceToScreen(Vector2 surface) =>
        Project(new(surface.X - .5f, surface.Y - .5f, 0), 1);

    public static Vector2 Project(Vector3 world, float aspect)
    {
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z) ||
            !float.IsFinite(aspect) || aspect <= 0)
            return new(float.NaN, float.NaN);
        float denominator = Distance - world.Y * SinTilt - world.Z * CosTilt;
        if (!float.IsFinite(denominator) || denominator <= 1e-6f)
            return new(float.NaN, float.NaN);
        return new(FocalLength * world.X / denominator / aspect + CentreX,
            FocalLength * (world.Y * CosTilt - world.Z * SinTilt) / denominator + CentreY);
    }

    public static bool ContainsWater(Vector2 screen)
    {
        var surface = ScreenToSurface(screen);
        if (!IsFinite(surface) ||
            surface.X < SurfaceEdgeMargin || surface.X > 1 - SurfaceEdgeMargin ||
            surface.Y < SurfaceEdgeMargin || surface.Y > 1 - SurfaceEdgeMargin)
            return false;
        // The raised near rim hides a narrow strip of the water plane. Follow
        // the sight line toward the camera to reject pixels landing on that rim.
        float planeY = surface.Y - .5f;
        float atRimY = planeY + (Distance * SinTilt - planeY) * RimHeight / (Distance * CosTilt);
        return atRimY < .5f - SurfaceEdgeMargin;
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
