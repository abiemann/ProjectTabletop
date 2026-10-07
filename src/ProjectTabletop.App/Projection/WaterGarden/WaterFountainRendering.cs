using ComputeSharp;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Static cascade geometry and wet-stone lighting. Water is reconstructed from
// the simulated three-dimensional density field in WaterFountainVolumeRendering.
internal readonly partial struct WaterSurfaceShader
{
    private float FountainLayerDistance(Float3 p, Float2 center, Float2 halfSize, float bottom, float top, float phase)
    {
        Float2 local = p.XY - center;
        Float3 box = Hlsl.Abs(new Float3(local, p.Z - (bottom + top) * .5f)) -
            new Float3(halfSize + new Float2(.036f, .036f), (top - bottom) * .5f + .018f);
        float lowerBound = Hlsl.Length(Hlsl.Max(box, new Float3(0, 0, 0))) +
            Hlsl.Min(Hlsl.Max(box.X, Hlsl.Max(box.Y, box.Z)), 0);
        if (lowerBound > .020f) return lowerBound;
        float angle = .12f * Hlsl.Sin(phase), c = Hlsl.Cos(angle), s = Hlsl.Sin(angle);
        local = new Float2(c * local.X + s * local.Y, -s * local.X + c * local.Y);
        float k0 = Hlsl.Length(local / halfSize), k1 = Hlsl.Length(local / (halfSize * halfSize));
        float footprint = k0 < 1e-6f ? -Hlsl.Min(halfSize.X, halfSize.Y) : k0 * (k0 - 1) / k1;
        float front = Hlsl.Clamp((local.Y / halfSize.Y - .35f) / .65f, 0, 1);
        front = front * front * (3 - 2 * front);
        float irregular = .010f * Hlsl.Sin(local.X * 27 + phase) +
            .0055f * Hlsl.Sin(local.Y * 39 + phase * 1.4f) +
            .0028f * Hlsl.Sin((local.X + local.Y) * 110 + phase * 2);
        footprint += irregular * (1 - .72f * front);
        Float2 cut = Hlsl.Normalize(new Float2(.8f * Hlsl.Sin(phase), -.75f));
        footprint = Hlsl.Max(footprint, Hlsl.Dot(local, cut) - .86f * Hlsl.Length(halfSize * cut));
        cut = Hlsl.Normalize(new Float2(-.9f * Hlsl.Sin(phase) + .15f, -.35f));
        footprint = Hlsl.Max(footprint, Hlsl.Dot(local, cut) - .92f * Hlsl.Length(halfSize * cut));
        float uneven = .0048f * Hlsl.Sin(local.X * 24 + phase) +
            .0025f * Hlsl.Sin(local.Y * 31 + phase * 2) + .009f * local.X / halfSize.X * Hlsl.Sin(phase);
        uneven *= 1 - .78f * front;
        const float bevel = .0048f;
        Float2 section = new Float2(footprint + bevel,
            Hlsl.Abs(p.Z - uneven - (bottom + top) * .5f) - (top - bottom) * .5f + bevel);
        return Hlsl.Length(Hlsl.Max(section, new Float2(0, 0))) +
            Hlsl.Min(Hlsl.Max(section.X, section.Y), 0) - bevel;
    }

    private float FountainStoneDistance(Float3 p)
    {
        float scale = Hlsl.Min(aspect, 1);
        Float3 q = (p - new Float3(0, WaterFountainLayout.AnchorY, 0)) / scale;
        float distance = FountainLayerDistance(q, new Float2(-.019f, .078f), new Float2(.186f, .098f), -.045f, .055f, .8f);
        distance = Hlsl.Min(distance, FountainLayerDistance(q, new Float2(-.011f, .088f), new Float2(.205f, .110f), .045f, .083f, 2.1f));
        distance = Hlsl.Min(distance, FountainLayerDistance(q, new Float2(.027f, .034f), new Float2(.150f, .079f), .067f, .112f, 4.2f));
        distance = Hlsl.Min(distance, FountainLayerDistance(q, new Float2(.024f, .035f), new Float2(.175f, .100f), .108f, .137f, 5.6f));
        distance = Hlsl.Min(distance, FountainLayerDistance(q, new Float2(-.040f, .003f), new Float2(.112f, .066f), .117f, .165f, 7.3f));
        distance = Hlsl.Min(distance, FountainLayerDistance(q, new Float2(-.038f, .005f), new Float2(.132f, .078f), .160f, .180f, 9.1f));
        return distance * scale;
    }

    private Float3 FountainStoneNormal(Float3 p)
    {
        const float e = .00018f;
        Float3 a = new Float3(1, -1, -1), b = new Float3(-1, -1, 1);
        Float3 c = new Float3(-1, 1, -1), d = new Float3(1, 1, 1);
        return Hlsl.Normalize(a * FountainStoneDistance(p + a * e) + b * FountainStoneDistance(p + b * e) +
            c * FountainStoneDistance(p + c * e) + d * FountainStoneDistance(p + d * e));
    }

    private Float4 FountainStoneHit(Float3 origin, Float3 ray)
    {
        float scale = Hlsl.Min(aspect, 1);
        Float2 interval = StoneRayInterval(origin, ray,
            new Float3(-.255f * scale, WaterFountainLayout.AnchorY - .105f * scale, -.060f * scale),
            new Float3(.255f * scale, WaterFountainLayout.AnchorY + .235f * scale, .202f * scale));
        float distance = interval.X;
        Float4 hit = new Float4(0, 0, 1, 10000);
        if (interval.Y >= interval.X)
        {
            for (int step = 0; step < 40; step++)
            {
                Float3 p = origin + ray * distance;
                float advance = FountainStoneDistance(p);
                if (advance < .00013f) { hit = new Float4(FountainStoneNormal(p), distance); break; }
                distance += advance * .72f;
                if (distance > interval.Y) break;
            }
        }
        return hit;
    }

    private Float4 FountainSolidRay(Float3 origin, Float3 ray)
    {
        Float4 hit = FountainStoneHit(origin, ray);
        Float3 color = new Float3(0, 0, 0);
        if (hit.W < 1000)
        {
            Float3 p = origin + ray * hit.W, normal = hit.XYZ;
            Float3 stone = WetRockTexture(p, normal);
            Float3 light = Hlsl.Normalize(new Float3(-.42f, -.50f, 1));
            float diffuse = Hlsl.Saturate(Hlsl.Dot(normal, light)), sky = .5f + .5f * normal.Z;
            float recess = Hlsl.Saturate((.009f - FountainStoneDistance(p + normal * .009f)) * 24);
            color = stone * 1.13f * (.65f + .29f * diffuse + .16f * sky + .06f * Hlsl.Saturate(normal.Y)) * (1 - .24f * recess);
            Float3 halfway = Hlsl.Normalize(light - ray);
            float wetGloss = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 74) * .20f +
                Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 17) * .046f;
            color += new Float3(.82f, .89f, .87f) * wetGloss;
        }
        return new Float4(color, hit.W);
    }

    private Float3 FountainPool(Float3 eye, Float3 incoming, Float3 surface,
        Float3 reflectedRay, Float3 poolColor, bool inWater)
    {
        Float3 color = poolColor;
        if (inWater)
        {
            Float4 reflected = FountainSolidRay(surface + new Float3(0, 0, .001f), reflectedRay);
            if (reflected.W < 1000)
            {
                float facing = Hlsl.Saturate(Hlsl.Dot(new Float3(0, 0, 1), -incoming));
                color = Hlsl.Lerp(color, reflected.XYZ,
                    (.24f + .30f * Hlsl.Pow(1 - facing, 3)) * Hlsl.Exp(-reflected.W * .7f));
            }
            Float4 shadow = FountainStoneHit(surface + new Float3(0, 0, .002f),
                Hlsl.Normalize(new Float3(-.4f, -.55f, 1)));
            if (shadow.W > .0001f && shadow.W < 1000) color *= .86f;
        }
        return color;
    }
}
