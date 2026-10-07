using ComputeSharp;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly partial struct WaterSurfaceShader
{
    private float RoundedRectangleDistance(Float2 p, Float2 halfSize, float radius)
    {
        Float2 q = Hlsl.Abs(p) - halfSize + radius;
        return Hlsl.Length(Hlsl.Max(q, new Float2(0, 0))) + Hlsl.Min(Hlsl.Max(q.X, q.Y), 0) - radius;
    }

    private Float2 RoundedRectangleNormal(Float2 p, Float2 halfSize, float radius)
    {
        Float2 q = Hlsl.Abs(p) - halfSize + radius;
        Float2 outside = Hlsl.Max(q, new Float2(0, 0));
        if (Hlsl.Dot(outside, outside) > .0000000001f)
            return Hlsl.Normalize(outside) * Hlsl.Sign(p);
        return q.X > q.Y ? new Float2(Hlsl.Sign(p.X), 0) : new Float2(0, Hlsl.Sign(p.Y));
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

    private float PondStoneDistance(Float3 p)
    {
        const float thickness = .021f, bottom = -.06f, bevel = .007f;
        float boundary = RoundedRectangleDistance(p.XY, new Float2(aspect * .5f, .5f),
            WaterGardenView.PondCornerRadius * Hlsl.Min(aspect, 1));
        Float2 q = Hlsl.Abs(new Float2(boundary - thickness * .5f, p.Z - (rimHeight + bottom) * .5f)) -
            new Float2(thickness * .5f - bevel, (rimHeight - bottom) * .5f - bevel);
        return Hlsl.Length(Hlsl.Max(q, new Float2(0, 0))) + Hlsl.Min(Hlsl.Max(q.X, q.Y), 0) - bevel;
    }

    private Float3 PondStoneNormal(Float3 p)
    {
        const float thickness = .021f, bottom = -.06f, bevel = .007f;
        float radius = WaterGardenView.PondCornerRadius * Hlsl.Min(aspect, 1);
        Float2 halfSize = new Float2(aspect * .5f, .5f);
        float boundary = RoundedRectangleDistance(p.XY, halfSize, radius);
        Float2 section = new Float2(boundary - thickness * .5f, p.Z - (rimHeight + bottom) * .5f);
        Float2 q = Hlsl.Abs(section) - new Float2(thickness * .5f - bevel, (rimHeight - bottom) * .5f - bevel);
        Float2 outside = Hlsl.Max(q, new Float2(0, 0));
        Float2 sectionNormal = q.X > q.Y ? new Float2(Hlsl.Sign(section.X), 0) : new Float2(0, Hlsl.Sign(section.Y));
        if (Hlsl.Dot(outside, outside) > .0000000001f)
            sectionNormal = Hlsl.Normalize(outside) * Hlsl.Sign(section);
        Float2 horizontal = RoundedRectangleNormal(p.XY, halfSize, radius) * sectionNormal.X;
        return new Float3(horizontal, sectionNormal.Y);
    }

    private Float4 BasinHit(Float3 origin, Float3 direction)
    {
        // Rays only traverse the shallow z interval of the stone. Most pool
        // pixels skip it in one distance step; the rounded border converges in
        // a handful. The cap prevents pathological grazing-ray GPU work.
        Float2 interval = StoneRayInterval(origin, direction,
            new Float3(-aspect * .5f - .022f, -.522f, -.061f),
            new Float3(aspect * .5f + .022f, .522f, rimHeight + .001f));
        float distance = interval.X;
        Float4 result = new Float4(0, 0, 1, 10000);
        if (interval.Y >= interval.X)
        {
            for (int step = 0; step < 24; step++)
            {
                Float3 p = origin + direction * distance;
                float advance = PondStoneDistance(p);
                if (advance < .00012f)
                {
                    result = new Float4(PondStoneNormal(p), distance);
                    break;
                }
                distance += advance;
                if (distance > interval.Y) break;
            }
        }
        return result;
    }

    private Float3 Granite(Float3 p, Float3 normal)
    {
        // Albedo comes from the photographic limestone tile. Soft directional
        // shading reveals rolled lips and recessed faces without grain noise.
        Float3 stone = StoneTexture(p, normal);
        Float3 light = Hlsl.Normalize(new Float3(-.42f, -.50f, 1));
        float diffuse = Hlsl.Saturate(Hlsl.Dot(normal, light));
        float sky = .5f + .5f * normal.Z;
        float lighting = .61f + .25f * diffuse + .14f * sky;
        float damp = 1 - Hlsl.SmoothStep(-.015f, .021f, p.Z);
        return stone * lighting * Hlsl.Lerp(new Float3(1, 1, 1), new Float3(.87f, .89f, .85f), damp * .42f);
    }
}
