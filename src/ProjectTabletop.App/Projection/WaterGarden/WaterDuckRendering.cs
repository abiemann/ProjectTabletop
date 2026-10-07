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

    private Float4 DuckRay(Float3 origin, Float3 ray, Float4 position, Float4 buoyancy,
        Float4 tilt, Float4 drop)
    {
        float scale = buoyancy.Z;
        Float3 center = new Float3((position.X - .5f) * aspect, position.Y - .5f,
            buoyancy.X + Hlsl.Max(0, drop.X));
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

    private Float4 DuckSplashRay(Float3 eye, Float3 incoming, Float3 surface,
        Float3 behind, float splashAge)
    {
        Float4 nearest = new Float4(0, 0, 0, 10000);
        if (splashAge < 0 || splashAge >= .38f) return nearest;

        float scale = Hlsl.Min(aspect, 1);
        // The water impact stays where the hull first touched down, even when
        // a collision or the outgoing ripple carries that hull away.
        Float2 centre = (duckSplashPosition - .5f) * new Float2(aspect, 1);
        // The droplets only occupy a small volume around the actual touchdown.
        // Culling by the water-plane intersection avoids tracing their geometry
        // for the rest of the projected board.
        if (Hlsl.Length(surface.XY - centre) > .15f * scale) return nearest;

        float progress = splashAge / .38f;
        float daylight = 1 - Hlsl.SmoothStep(.21f, .38f, splashAge);
        float clarity = 0;
        for (int petal = 0; petal < 8; petal++)
        {
            float angle = petal * .78539816f + .19f * Hlsl.Sin(petal * 4.1f);
            Float2 direction = new Float2(Hlsl.Cos(angle), Hlsl.Sin(angle));

            // A few low, broken beads of water cling to the impact rim before
            // settling. They are much shorter than the rising droplets and
            // avoid a rigid picket-fence crown around the toy.
            if (splashAge < .13f && petal != 2 && petal != 5)
            {
                float crownLife = 1 - splashAge / .13f;
                Float2 crownPlace = centre + direction *
                    (.030f + .012f * splashAge + .003f * Hlsl.Sin(petal * 2.2f)) * scale;
                Float4 crown = DuckEllipsoid(eye, incoming,
                    new Float3(crownPlace, (.005f + .004f * crownLife) * scale),
                    new Float3(.0046f, .0046f, .004f * crownLife + .0015f) * scale);
                if (crown.W < nearest.W) { nearest = crown; clarity = .37f; }
            }

            // A handful of small, translucent droplets follow independent
            // outward ballistic arcs. Unequal phases break up the circular
            // symmetry without shifting the physical splash origin.
            float phase = Hlsl.Saturate(progress * (.91f + .13f * Hlsl.Sin(petal * 2.7f)) +
                .026f * Hlsl.Sin(petal * 3.9f));
            float rise = 4 * phase * (1 - phase);
            Float2 place = centre + direction * (.028f + .053f * phase) * scale;
            float height = (.006f + (.032f + .012f * Hlsl.Sin(petal * 1.9f)) * rise) * scale;
            Float4 drop = DuckEllipsoid(eye, incoming,
                new Float3(place, height), new Float3(.0036f, .0036f, .0045f) * scale);
            if (drop.W < nearest.W) { nearest = drop; clarity = .63f; }
        }

        if (nearest.W < 1000)
        {
            Float3 normal = Hlsl.Normalize(nearest.XYZ);
            Float3 light = Hlsl.Normalize(new Float3(-.35f, -.30f, 1));
            Float3 halfway = Hlsl.Normalize(light - incoming);
            float glint = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 52);
            float edge = Hlsl.Pow(1 - Hlsl.Saturate(Hlsl.Dot(normal, -incoming)), 2);
            Float3 glass = new Float3(.74f, .78f, .75f) *
                (.57f + .43f * Hlsl.Saturate(Hlsl.Dot(normal, light))) +
                new Float3(.97f, .99f, .96f) * (glint * .60f + edge * .11f);
            // Water remains partly transparent; its short fade reveals the
            // actual water or hull behind it instead of darkening to black.
            return new Float4(Hlsl.Lerp(behind, Hlsl.Saturate(glass), clarity * daylight), nearest.W);
        }
        return nearest;
    }

    private Float4 GardenDuckRays(Float3 eye, Float3 incoming, Float3 surface,
        Float3 reflection, Float3 waterColor, bool inWater)
    {
        Float4 nearest = new Float4(waterColor, 10000);
        Float4 reflected = new Float4(0, 0, 0, 10000);
        float shadow = 1;
        for (int index = 0; index < activeDuckCount; index++)
        {
            Float4 position = DuckState(index, 0);
            Float4 buoyancy = DuckState(index, 2);
            Float4 tilt = DuckState(index, 3);
            Float4 drop = DuckState(index, 4);
            Float4 duck = DuckRay(eye, incoming, position, buoyancy, tilt, drop);
            // Clip the toy at the waterline. The submerged underside does not
            // paste an opaque yellow shape over the refracting surface.
            if (duck.W < nearest.W && eye.Z + incoming.Z * duck.W >= surface.Z - .001f)
                nearest = duck;
            if (inWater)
            {
                Float4 reflectionHit = DuckRay(surface + new Float3(0, 0, .001f), reflection,
                    position, buoyancy, tilt, drop);
                if (reflectionHit.W < reflected.W) reflected = reflectionHit;
                // Shallow-water contact shading is tied to the body's footprint,
                // not to a rectangular sprite or a screen-space opacity fade.
                float elevation = Hlsl.Max(0, drop.X);
                Float2 toDuck = (surface.XY - (position.XY - .5f) * new Float2(aspect, 1)) /
                    (buoyancy.Z * (1 + elevation * 2.4f) * new Float2(1.25f, 1.05f));
                float shadowStrength = Hlsl.Lerp(.18f, .045f, Hlsl.Saturate(elevation / .55f));
                shadow *= 1 - shadowStrength * Hlsl.Exp(-Hlsl.Dot(toDuck, toDuck) * 1.5f);
            }
            Float4 splash = DuckSplashRay(eye, incoming, surface, nearest.XYZ, drop.Z);
            if (splash.W < nearest.W && eye.Z + incoming.Z * splash.W >= surface.Z - .001f)
                nearest = splash;
        }
        Float3 color = waterColor * shadow;
        if (reflected.W < 1000)
            color = Hlsl.Lerp(color, reflected.XYZ, .32f * Hlsl.Exp(-reflected.W * 8));
        if (nearest.W < 1000) color = nearest.XYZ;
        return new Float4(color, nearest.W);
    }
}
