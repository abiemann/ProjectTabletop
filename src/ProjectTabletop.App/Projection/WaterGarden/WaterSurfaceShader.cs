using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Height-normal reflection/refraction adapts the optical approach in Evan Wallace's
// MIT WebGL Water (2011); see THIRD_PARTY_NOTICES.md. The pebble bed and environment
// are original. Caustic light is a bounded local Jacobian approximation, not a port
// of the demo's displaced-mesh/raster-derivative caustics stage.
[D2DInputCount(6)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputComplex(2)]
[D2DInputComplex(3)]
[D2DInputComplex(4)]
[D2DInputComplex(5)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(2, D2D1Filter.MinMagMipPoint)]
[D2DInputDescription(3, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(4, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(5, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterSurfaceShader(Float2 fieldSize, Float2 size,
    float aspect, float time, int ambientEnabled, Float4 camera, Float2 centre, float rimHeight,
    Float2 duckStateSize, float fountainFlow,
    Float3 fountainVolumeMin, Float3 fountainVolumeSize, Float3 fountainGridSize,
    Float2 fountainAtlasTiles) : ID2D1PixelShader
{
    // camera = (sin tilt, cos tilt, distance, zoom). The host supplies the same
    // camera used to unproject tracked stick observations into the wave field.
    private Float2 SurfacePoint(Float2 screen)
    {
        Float2 p = screen - centre;
        float focal = camera.Z * camera.W;
        float y = p.Y * camera.Z / (focal * camera.Y + p.Y * camera.X);
        float x = p.X * (camera.Z - y * camera.X) / focal;
        return new Float2(x + .5f, y + .5f);
    }

    private Float4 BoxHit(Float3 origin, Float3 direction, Float3 low, Float3 high)
    {
        Float3 inverse = new Float3(
            (direction.X >= 0 ? 1 : -1) / Hlsl.Max(.00001f, Hlsl.Abs(direction.X)),
            (direction.Y >= 0 ? 1 : -1) / Hlsl.Max(.00001f, Hlsl.Abs(direction.Y)),
            (direction.Z >= 0 ? 1 : -1) / Hlsl.Max(.00001f, Hlsl.Abs(direction.Z)));
        Float3 a = (low - origin) * inverse, b = (high - origin) * inverse;
        Float3 enter = Hlsl.Min(a, b), leave = Hlsl.Max(a, b);
        float near = Hlsl.Max(enter.X, Hlsl.Max(enter.Y, enter.Z));
        float far = Hlsl.Min(leave.X, Hlsl.Min(leave.Y, leave.Z));
        Float3 normal = new Float3(0, 0, -Hlsl.Sign(direction.Z));
        if (enter.X >= enter.Y && enter.X >= enter.Z) normal = new Float3(-Hlsl.Sign(direction.X), 0, 0);
        else if (enter.Y >= enter.Z) normal = new Float3(0, -Hlsl.Sign(direction.Y), 0);
        return new Float4(normal, far >= Hlsl.Max(near, 0) ? near : 10000);
    }

    private float Height(Float2 uv)
    {
        // Input zero is scaled to the bed's bounds by the host. Keep its clamp at
        // actual field-cell centers while sampling in the shared image coordinates.
        Float2 fieldUv = Hlsl.Clamp(uv, .5f / fieldSize, (fieldSize - .5f) / fieldSize);
        float height = D2D.SampleInputAtPosition(0, fieldUv * size).X;
        Float2 world = uv * new Float2(aspect, 1);
        // Ambient ripples are optical only: they cannot accumulate mass or disturb
        // deterministic simulation tests. Calm Water stays flat before they fade in.
        float amount = ambientEnabled * Hlsl.SmoothStep(2, 5, time);
        return height + amount * (.00040f * Hlsl.Sin(world.X * 23 + world.Y * 13 - time * .9f) +
            .00030f * Hlsl.Sin(world.X * -17 + world.Y * 29 + time * .72f) +
            .00016f * Hlsl.Sin(world.X * 43 - world.Y * 21 - time * 1.12f));
    }

    private float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new Float2(127.1f, 311.7f))) * 43758.5453f);

    private float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        f = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), f.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), f.X), f.Y);
    }

    private Float4 Environment(Float3 ray, Float2 world)
    {
        float horizon = Hlsl.Saturate(ray.Z);
        Float3 sky = Hlsl.Lerp(new Float3(.32f, .38f, .36f), new Float3(.65f, .70f, .70f), horizon);
        Float2 position = ray.XY * 9 + world * 1.9f + new Float2(4.7f, 9.3f);
        float clouds = Noise(position) * .65f + Noise(position * 2.1f + 17) * .35f;
        float openSky = Hlsl.SmoothStep(.35f, .72f, clouds);
        sky = Hlsl.Lerp(sky, new Float3(.98f, .99f, .96f), openSky);
        float foliage = (1 - Hlsl.SmoothStep(.28f, .63f, ray.Z)) * Hlsl.SmoothStep(.30f, .60f, Noise(position * 1.8f));
        return new Float4(Hlsl.Lerp(sky, new Float3(.12f, .19f, .13f), foliage), openSky);
    }

    public Float4 Execute()
    {
        Float2 screen = D2D.GetScenePosition().XY / size;
        Float2 uv = SurfacePoint(screen);
        Float2 world = (uv - .5f) * new Float2(aspect, 1);
        Float3 eye = new Float3(0, camera.Z * camera.X, camera.Z * camera.Y);
        Float3 incoming = Hlsl.Normalize(new Float3(world, 0) - eye);
        float planeDistance = Hlsl.Length(new Float3(world, 0) - eye);
        bool inWater = InPond(world);
        Float2 texel = 1 / fieldSize;
        Float2 cell = new Float2(aspect, 1) / fieldSize;
        float height = Height(uv);
        float left = Height(uv - new Float2(texel.X, 0));
        float right = Height(uv + new Float2(texel.X, 0));
        float top = Height(uv - new Float2(0, texel.Y));
        float bottom = Height(uv + new Float2(0, texel.Y));
        // Small physical displacements need a modest optical normal enhancement to remain
        // legible in an overhead projection. The actual simulation amplitude is unchanged.
        Float2 slope = new Float2((right - left) / (2 * cell.X), (bottom - top) / (2 * cell.Y)) * 2.4f;
        slope = Hlsl.Clamp(slope, new Float2(-.85f, -.85f), new Float2(.85f, .85f));
        Float3 normal = Hlsl.Normalize(new Float3(-slope, 1));
        float eta = 1 / 1.333f;
        Float3 refraction = Hlsl.Refract(incoming, normal, eta);
        // Keep the pebble floor above the bottom of the thinner basin walls so
        // oblique refracted rays cannot escape beneath their rounded profile.
        float depth = .042f + .008f * Noise(uv * new Float2(aspect, 1) * 3);
        // Keep resting parallax: a slanted ray travels through the water before
        // reaching the gravel, rather than treating the bed as a flat photograph.
        Float2 offset = refraction.XY / Hlsl.Max(.15f, -refraction.Z) * (depth + height);
        Float2 bedUv = uv + offset / new Float2(aspect, 1);
        Float3 bed = D2D.SampleInputAtPosition(1,
            Hlsl.Clamp(bedUv * size, new Float2(.5f, .5f), size - .5f)).XYZ;
        // Refracted rays can meet the inside wall before they reach the bottom.
        if (!InPond((bedUv - .5f) * new Float2(aspect, 1)))
        {
            Float3 surfaceOrigin = new Float3(world, height);
            Float4 submergedWall = BasinHit(surfaceOrigin, refraction);
            if (submergedWall.W > 0 && submergedWall.W < 1000)
                bed = Granite(surfaceOrigin + refraction * submergedWall.W, submergedWall.XYZ);
        }

        // Curvature controls the local concentration of refracted light. This gather-only
        // approximation is deliberately bounded around ray folds, so a steep disturbance
        // cannot flash the projector white or produce infinities.
        float xx = (left + right - 2 * height) / (cell.X * cell.X);
        float yy = (top + bottom - 2 * height) / (cell.Y * cell.Y);
        float tl = Height(uv - texel), br = Height(uv + texel);
        float tr = Height(uv + new Float2(texel.X, -texel.Y));
        float bl = Height(uv + new Float2(-texel.X, texel.Y));
        float xy = (tl + br - tr - bl) / (4 * cell.X * cell.Y);
        float focusDistance = depth * (1 - eta);
        float determinant = (1 - focusDistance * xx) * (1 - focusDistance * yy) -
            focusDistance * focusDistance * xy * xy;
        float focusing = Hlsl.Clamp(1 / Hlsl.Max(.36f, Hlsl.Abs(determinant)), .65f, 2.3f);
        bed *= .85f + .15f * focusing;
        Float3 transmission = Hlsl.Exp(-depth * new Float3(.40f, .23f, .18f));
        bed = bed * transmission + new Float3(.28f, .33f, .31f) * (1 - transmission);

        Float3 reflection = Hlsl.Reflect(incoming, normal);
        float facing = Hlsl.Saturate(Hlsl.Dot(normal, -incoming));
        Float4 environment = Environment(reflection, uv * new Float2(aspect, 1));
        float fresnel = .035f + .965f * Hlsl.Pow(1 - facing, 5);
        // Broad, softly broken silver sky patches are more useful than hard blue gloss
        // for this shallow garden. Their coordinates bend with the same surface normal.
        float softReflection = fresnel + .23f * environment.W * environment.W;
        Float3 color = Hlsl.Lerp(bed, environment.XYZ, Hlsl.Saturate(softReflection));
        Float3 light = Hlsl.Normalize(new Float3(-.35f, -.30f, 1));
        Float3 halfVector = Hlsl.Normalize(light - incoming);
        float sun = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfVector)), 110) * .29f;
        float broadSky = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfVector)), 24) * .026f;
        color += new Float3(1, .98f, .90f) * (sun + broadSky);
        color = FountainPool(eye, incoming, new Float3(world, height), reflection, color, inWater);
        Float4 ducks = GardenDuckRays(eye, incoming, new Float3(world, height), reflection, color, inWater);
        color = ducks.XYZ;
        // Resolve solid geometry after the texture gathers. This keeps the D2D
        // multi-input sampling path uniform through the legacy shader compiler.
        Float4 basin = BasinHit(eye, incoming);
        float visibleDistance = inWater ? Hlsl.Min(planeDistance, ducks.W) : 10000;
        if (!inWater)
        {
            float paper = Noise(screen * size * .34f);
            color = new Float3(.86f, .87f, .82f) + (paper - .5f) * .008f;
        }
        if (basin.W < 1000 && basin.W < ducks.W && (!inWater || basin.W < planeDistance))
        {
            color = Granite(eye + incoming * basin.W, basin.XYZ);
            visibleDistance = basin.W;
        }
        Float4 fountain = FountainSolidRay(eye, incoming);
        if (fountain.W < visibleDistance)
        {
            color = fountain.XYZ;
            visibleDistance = fountain.W;
        }
        Float4 fountainWater = FountainWaterRay(eye, incoming, color, visibleDistance);
        if (fountainWater.W < visibleDistance) color = fountainWater.XYZ;
        return new Float4(Hlsl.Saturate(color), 1);
    }
}
