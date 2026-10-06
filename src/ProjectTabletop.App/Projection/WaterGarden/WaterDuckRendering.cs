using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Original analytic geometry, evaluated at the destination pixels. These are
// small three-dimensional rubber toys, not camera-facing sprites. Their pose and
// position come from the buoyancy state advanced alongside the real wave field.
internal readonly partial struct WaterSurfaceShader
{
    private Float4 DuckState(int index, int row) => D2D.SampleInputAtPosition(2,
        (new Float2(index + .5f, row + .5f) / duckStateSize) * size);

    private Float4 DuckEllipsoid(Float3 origin, Float3 ray, Float3 center, Float3 radius)
    {
        Float3 q = (origin - center) / radius, d = ray / radius;
        float a = Hlsl.Dot(d, d), b = Hlsl.Dot(q, d), c = Hlsl.Dot(q, q) - 1;
        float discriminant = b * b - a * c;
        float distance = (-b - Hlsl.Sqrt(Hlsl.Max(0, discriminant))) / a;
        Float3 point = origin + ray * distance;
        Float3 normal = Hlsl.Normalize((point - center) / (radius * radius));
        return new Float4(normal, discriminant >= 0 && distance > .0001f ? distance : 10000);
    }

    private Float4 DuckRay(Float3 origin, Float3 ray, Float4 position, Float4 buoyancy, Float4 tilt)
    {
        float scale = buoyancy.Z;
        Float3 center = new Float3((position.X - .5f) * aspect, position.Y - .5f, buoyancy.X);
        // Match the water's optical normal scale so rocking remains legible on
        // these tiny toys. Bound the roll while the buoyant height stays physical.
        Float2 visualSlope = Hlsl.Clamp(tilt.XY * 2.4f, new Float2(-.4f, -.4f), new Float2(.4f, .4f));
        Float3 up = Hlsl.Normalize(new Float3(-visualSlope, 1));
        Float3 heading = new Float3(Hlsl.Cos(position.Z), Hlsl.Sin(position.Z), 0);
        Float3 forward = Hlsl.Normalize(heading - up * Hlsl.Dot(heading, up));
        Float3 side = Hlsl.Cross(up, forward);
        Float3 delta = (origin - center) / scale;
        Float3 localOrigin = new Float3(Hlsl.Dot(delta, forward), Hlsl.Dot(delta, side), Hlsl.Dot(delta, up));
        Float3 localRay = new Float3(Hlsl.Dot(ray, forward), Hlsl.Dot(ray, side), Hlsl.Dot(ray, up));
        Float3 bound = localOrigin - new Float3(0, 0, .55f);
        float approach = -Hlsl.Dot(bound, localRay);
        float miss = Hlsl.Dot(bound, bound) - approach * approach;
        Float4 result = new Float4(0, 0, 0, 10000);
        // Almost all pixels miss this small sphere. Only the toy's immediate
        // footprint evaluates its rounded body, neck, head, wings and details.
        if (approach > 0 && miss < 3.25f)
        {
            float material = 0;
            Float4 hit = DuckEllipsoid(localOrigin, localRay, new Float3(-.12f, 0, .23f),
                new Float3(1.02f, .69f, .59f));
            Float4 next = DuckEllipsoid(localOrigin, localRay, new Float3(.41f, 0, .64f),
                new Float3(.38f, .36f, .56f));
            if (next.W < hit.W) hit = next;
            next = DuckEllipsoid(localOrigin, localRay, new Float3(.57f, 0, 1.12f),
                new Float3(.47f, .43f, .47f));
            if (next.W < hit.W) hit = next;
            next = DuckEllipsoid(localOrigin, localRay, new Float3(-1.00f, 0, .53f),
                new Float3(.39f, .29f, .26f));
            if (next.W < hit.W) hit = next;
            next = DuckEllipsoid(localOrigin, localRay, new Float3(-.18f, -.59f, .39f),
                new Float3(.63f, .16f, .30f));
            if (next.W < hit.W) { hit = next; material = 1; }
            next = DuckEllipsoid(localOrigin, localRay, new Float3(-.18f, .59f, .39f),
                new Float3(.63f, .16f, .30f));
            if (next.W < hit.W) { hit = next; material = 1; }
            next = DuckEllipsoid(localOrigin, localRay, new Float3(1.03f, 0, 1.02f),
                new Float3(.38f, .27f, .13f));
            if (next.W < hit.W) { hit = next; material = 2; }
            // A fine lower lip gives the bill a recognisable flattened silhouette.
            next = DuckEllipsoid(localOrigin, localRay, new Float3(1.03f, 0, .956f),
                new Float3(.35f, .25f, .052f));
            if (next.W < hit.W) { hit = next; material = 3; }
            next = DuckEllipsoid(localOrigin, localRay, new Float3(.74f, -.378f, 1.225f),
                new Float3(.070f, .052f, .073f));
            if (next.W < hit.W) { hit = next; material = 4; }
            next = DuckEllipsoid(localOrigin, localRay, new Float3(.74f, .378f, 1.225f),
                new Float3(.070f, .052f, .073f));
            if (next.W < hit.W) { hit = next; material = 4; }

            if (hit.W < 1000)
            {
                Float3 normal = forward * hit.X + side * hit.Y + up * hit.Z;
                Float3 light = Hlsl.Normalize(new Float3(-.38f, -.48f, 1));
                float diffuse = .48f + .52f * Hlsl.Saturate(Hlsl.Dot(normal, light));
                Float3 rubber = new Float3(1.0f, .77f, .018f);
                if (material == 1) rubber = new Float3(.98f, .70f, .012f);
                if (material == 2) rubber = new Float3(1.0f, .31f, .018f);
                if (material == 3) rubber = new Float3(.79f, .17f, .008f);
                if (material == 4) rubber = new Float3(.012f, .014f, .010f);
                Float3 halfway = Hlsl.Normalize(light - ray);
                float gloss = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), material == 4 ? 96 : 65);
                float softLight = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 10) * .045f;
                float edgeLight = Hlsl.Pow(1 - Hlsl.Saturate(Hlsl.Dot(normal, -ray)), 4) * .09f;
                Float3 color = rubber * diffuse + new Float3(1, .99f, .83f) *
                    (gloss * .43f + softLight + edgeLight);
                float localHeight = localOrigin.Z + localRay.Z * hit.W;
                color *= .85f + .15f * Hlsl.SmoothStep(-.15f, .4f, localHeight);
                result = new Float4(Hlsl.Saturate(color), hit.W * scale);
            }
        }
        return result;
    }

    private Float4 GardenDuckRays(Float3 eye, Float3 incoming, Float3 surface,
        Float3 reflection, Float3 waterColor, bool inWater)
    {
        Float4 nearest = new Float4(waterColor, 10000);
        Float4 reflected = new Float4(0, 0, 0, 10000);
        float shadow = 1;
        for (int index = 0; index < (int)duckStateSize.X; index++)
        {
            Float4 position = DuckState(index, 0);
            Float4 buoyancy = DuckState(index, 2);
            Float4 tilt = DuckState(index, 3);
            Float4 duck = DuckRay(eye, incoming, position, buoyancy, tilt);
            // Clip the toy at the waterline. The submerged underside does not
            // paste an opaque yellow shape over the refracting surface.
            if (duck.W < nearest.W && eye.Z + incoming.Z * duck.W >= surface.Z - .001f)
                nearest = duck;
            if (inWater)
            {
                Float4 reflectionHit = DuckRay(surface + new Float3(0, 0, .001f), reflection,
                    position, buoyancy, tilt);
                if (reflectionHit.W < reflected.W) reflected = reflectionHit;
                // Shallow-water contact shading is tied to the body's footprint,
                // not to a rectangular sprite or a screen-space opacity fade.
                Float2 toDuck = (surface.XY - (position.XY - .5f) * new Float2(aspect, 1)) /
                    (buoyancy.Z * new Float2(1.25f, 1.05f));
                shadow *= 1 - .18f * Hlsl.Exp(-Hlsl.Dot(toDuck, toDuck) * 1.5f);
            }
        }
        Float3 color = waterColor * shadow;
        if (reflected.W < 1000)
            color = Hlsl.Lerp(color, reflected.XYZ, .32f * Hlsl.Exp(-reflected.W * 8));
        if (nearest.W < 1000) color = nearest.XYZ;
        return new Float4(color, nearest.W);
    }
}
