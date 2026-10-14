using ComputeSharp;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly partial struct WaterSurfaceShader
{
    private Float3 WaterTransmission(Float3 origin, Float3 ray)
    {
        float distance = Hlsl.Max(0, origin.Z + WaterGardenView.WaterDepth) /
            Hlsl.Max(.15f, -ray.Z);
        Float3 bedPoint = origin + ray * distance;
        Float3 background;
        if (!InPond(bedPoint.XY))
        {
            // Find the exit through the invisible side before the floor. The
            // bounded search contains only geometry, never texture readbacks.
            float low = 0, high = distance;
            for (int step = 0; step < 10; step++)
            {
                float middle = (low + high) * .5f;
                if (InPond((origin + ray * middle).XY)) low = middle;
                else high = middle;
            }
            distance = (low + high) * .5f;
            background = SandGroundRay(origin, ray, 10000).XYZ;
        }
        else background = PebbleBed(bedPoint.XY / new Float2(aspect, 1) + .5f);
        // Longer paths still lose red first, with less colour separation so
        // gravel retains its natural warmth beneath the transparent depth.
        // Share these optics across the top, exposed sides and waterfall.
        Float3 transmission = Hlsl.Exp(-distance * new Float3(2.25f, 1.1f, .85f));
        return background * transmission + new Float3(.17f, .46f, .39f) * (1 - transmission);
    }

    private Float4 PondCornerHit(Float3 origin, Float3 ray, Float2 center, float radius, float bottom)
    {
        Float2 delta = origin.XY - center;
        float a = Hlsl.Dot(ray.XY, ray.XY), b = Hlsl.Dot(delta, ray.XY);
        // This equivalent discriminant avoids subtracting large near-equal
        // values, preserving almost-tangent hits with shader float precision.
        float cross = delta.X * ray.Y - delta.Y * ray.X;
        float discriminant = a * radius * radius - cross * cross;
        float root = Hlsl.Sqrt(Hlsl.Max(0, discriminant));
        float near = (-b - root) / Hlsl.Max(.000001f, a);
        float far = (-b + root) / Hlsl.Max(.000001f, a);
        float inverseZ = (ray.Z >= 0 ? 1 : -1) / Hlsl.Max(.000001f, Hlsl.Abs(ray.Z));
        float cap0 = (bottom - origin.Z) * inverseZ, cap1 = -origin.Z * inverseZ;
        float capNear = Hlsl.Min(cap0, cap1), capFar = Hlsl.Max(cap0, cap1);
        float distance = Hlsl.Max(near, capNear);
        Float3 point = origin + ray * distance;
        Float3 normal = new Float3((point.XY - center) / radius, 0);
        if (capNear > near) normal = new Float3(0, 0, -Hlsl.Sign(ray.Z));
        bool hit = discriminant >= 0 && a > .000001f && Hlsl.Min(far, capFar) >= Hlsl.Max(0, distance);
        return new Float4(normal, hit ? distance : 10000);
    }

    private Float4 PondColumnHit(Float3 origin, Float3 ray, float bottom)
    {
        float radius = WaterGardenView.PondCornerRadius * Hlsl.Min(aspect, 1);
        Float2 halfSize = new Float2(aspect * .5f, .5f);
        // A rounded rectangle is exactly the union of two strips and four
        // circles. Analytic intersections keep tangent corners closed without
        // a variable number of sphere-tracing steps or silhouette pinholes.
        Float4 hit = BoxHit(origin, ray,
            new Float3(-halfSize.X + radius, -halfSize.Y, bottom),
            new Float3(halfSize.X - radius, halfSize.Y, 0));
        Float4 next = BoxHit(origin, ray,
            new Float3(-halfSize.X, -halfSize.Y + radius, bottom),
            new Float3(halfSize.X, halfSize.Y - radius, 0));
        if (next.W < hit.W) hit = next;
        for (int y = -1; y <= 1; y += 2)
        for (int x = -1; x <= 1; x += 2)
        {
            Float2 center = new Float2(x, y) * (halfSize - radius);
            next = PondCornerHit(origin, ray, center, radius, bottom);
            if (next.W < hit.W) hit = next;
        }
        return hit;
    }

    private Float3 WaterColumnColor(Float3 eye, Float3 incoming, Float4 hit)
    {
        Float3 point = eye + incoming * hit.W;
        Float3 normal = hit.XYZ;
        Float3 refraction = Hlsl.Refract(incoming, normal, 1 / 1.333f);
        Float3 transmitted = WaterTransmission(point - normal * .00015f, refraction);
        Float3 reflection = Hlsl.Reflect(incoming, normal);
        Float4 environment = Environment(reflection, point.XY);
        float facing = Hlsl.Saturate(Hlsl.Dot(normal, -incoming));
        float fresnel = .025f + .975f * Hlsl.Pow(1 - facing, 5);
        return Hlsl.Lerp(transmitted, environment.XYZ, fresnel);
    }
}
