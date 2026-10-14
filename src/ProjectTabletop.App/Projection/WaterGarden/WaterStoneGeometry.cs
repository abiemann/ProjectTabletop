using ComputeSharp;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly partial struct WaterSurfaceShader
{
    private float RoundedRectangleDistance(Float2 p, Float2 halfSize, float radius)
    {
        Float2 q = Hlsl.Abs(p) - halfSize + radius;
        return Hlsl.Length(Hlsl.Max(q, new Float2(0, 0))) + Hlsl.Min(Hlsl.Max(q.X, q.Y), 0) - radius;
    }

    private float RoundedExtrusionDistance(Float3 p, Float2 halfSize, float cornerRadius,
        float halfHeight, float edgeRadius)
    {
        Float2 q = new Float2(RoundedRectangleDistance(p.XY, halfSize, cornerRadius) + edgeRadius,
            Hlsl.Abs(p.Z) - halfHeight + edgeRadius);
        return Hlsl.Length(Hlsl.Max(q, new Float2(0, 0))) + Hlsl.Min(Hlsl.Max(q.X, q.Y), 0) - edgeRadius;
    }

    private float StoneSmoothUnion(float a, float b, float radius)
    {
        float h = Hlsl.Max(radius - Hlsl.Abs(a - b), 0) / radius;
        return Hlsl.Min(a, b) - h * h * radius * .25f;
    }

    private float StoneSmoothDifference(float outer, float cut, float radius)
    {
        float h = Hlsl.Max(radius - Hlsl.Abs(outer + cut), 0) / radius;
        return Hlsl.Max(outer, -cut) + h * h * radius * .25f;
    }

    private float StoneCapsuleDistance(Float3 p, Float3 start, Float3 end, float radius)
    {
        Float3 axis = end - start, delta = p - start;
        float t = Hlsl.Saturate(Hlsl.Dot(delta, axis) / Hlsl.Max(.000001f, Hlsl.Dot(axis, axis)));
        return Hlsl.Length(delta - t * axis) - radius;
    }

    private Float2 StoneRayInterval(Float3 origin, Float3 ray, Float3 low, Float3 high)
    {
        Float3 inverse = new Float3(
            (ray.X >= 0 ? 1 : -1) / Hlsl.Max(.000001f, Hlsl.Abs(ray.X)),
            (ray.Y >= 0 ? 1 : -1) / Hlsl.Max(.000001f, Hlsl.Abs(ray.Y)),
            (ray.Z >= 0 ? 1 : -1) / Hlsl.Max(.000001f, Hlsl.Abs(ray.Z)));
        Float3 a = (low - origin) * inverse, b = (high - origin) * inverse;
        Float3 enter = Hlsl.Min(a, b), leave = Hlsl.Max(a, b);
        return new Float2(Hlsl.Max(0, Hlsl.Max(enter.X, Hlsl.Max(enter.Y, enter.Z))),
            Hlsl.Min(leave.X, Hlsl.Min(leave.Y, leave.Z)));
    }

    private bool InPond(Float2 world) =>
        RoundedRectangleDistance(world, new Float2(aspect * .5f, .5f), WaterGardenView.PondCornerRadius * Hlsl.Min(aspect, 1)) <= 0;
}
