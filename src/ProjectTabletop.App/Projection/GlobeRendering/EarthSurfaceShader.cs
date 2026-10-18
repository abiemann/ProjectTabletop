using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.GlobeRendering;

// Evaluate a sphere at the destination's native pixels. Equirectangular NASA
// imagery is sampled directly: there is no low-resolution globe render target
// and no planar stretching of the surface photograph.
[D2DInputCount(3)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputComplex(2)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(2, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct EarthSurfaceShader(
    Float2 daySize, Float2 cloudSize, Float2 starFieldSize, Float2 center, Float2 radii,
    float rotationRadians, float edgeWidth, float viewLatitudeRadians,
    Float2 shootingStarHead, Float2 shootingStarDirection, Float2 shootingStarScale,
    float shootingStarTailLength, float shootingStarOpacity) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float3 background = D2D.SampleInputAtPosition(2,
            Hlsl.Clamp(p / 1000 * starFieldSize, new Float2(.5f, .5f), starFieldSize - .5f)).XYZ;
        // Full-bright sky frames the dimmer centre. Preserve the lower swipe
        // strip and restore the top eighth, with a soft upper transition.
        if (p.Y > 125 && p.Y < 750)
        {
            float blend = 1 - Hlsl.SmoothStep(125, 250, p.Y)
                + Hlsl.SmoothStep(500, 750, p.Y);
            background *= .5f + .5f * blend;

            // Sub-8-bit, screen-stationary dither softens transition banding.
            // It vanishes at each transition's ends and leaves both bright
            // margins untouched.
            Float2 pixel = Hlsl.Floor(p / 1000 * starFieldSize);
            float noise = Hlsl.Frac(52.9829189f * Hlsl.Frac(
                Hlsl.Dot(pixel, new Float2(.06711056f, .00583715f)))) - .5f;
            background = Hlsl.Saturate(background
                + noise * (.75f / 255) * (4 * blend * (1 - blend)));
        }
        if (shootingStarOpacity > 0 && p.Y < 720)
        {
            // Physical short-edge coordinates keep the head round and the
            // tapered trail equally slim on landscape and portrait boards.
            Float2 offset = (p / 1000 - shootingStarHead) * shootingStarScale;
            float behind = -Hlsl.Dot(offset, shootingStarDirection);
            float across = Hlsl.Abs(offset.X * shootingStarDirection.Y
                - offset.Y * shootingStarDirection.X);
            Float3 meteor = new Float3(0, 0, 0);
            if (behind >= 0 && behind < shootingStarTailLength)
            {
                float along = behind / shootingStarTailLength;
                float taper = 1 - along;
                float width = .00022f + .00065f * taper;
                float core = 1 - Hlsl.SmoothStep(width * .35f, width, across);
                float halo = 1 - Hlsl.SmoothStep(width, .0014f + .0018f * taper, across);
                Float3 trailColor = Hlsl.Lerp(new Float3(1, .79f, .43f),
                    new Float3(.3f, .64f, 1), Hlsl.SmoothStep(.08f, .85f, along));
                meteor = (trailColor * core + new Float3(.22f, .48f, 1) * halo * .18f)
                    * taper * taper;
            }
            float headDistance = Hlsl.Length(offset);
            float head = 1 - Hlsl.SmoothStep(.0003f, .001f, headDistance);
            float glow = 1 - Hlsl.SmoothStep(.0006f, .0045f, headDistance);
            meteor += new Float3(1, .95f, .81f) * head * 1.2f
                + new Float3(.65f, .78f, 1) * glow * glow * .22f;
            // The sphere's existing coverage hides the meteor behind Earth;
            // no animated texture touches the bottom control/swipe strip.
            background += meteor * shootingStarOpacity;
        }
        Float2 disk = (p - center) / radii;
        float distance = Hlsl.Length(disk);

        // The thin blue scattering shell is tied to the planet, not to screen
        // size. It remains circular on non-square physical boards.
        float shell = Hlsl.Exp(-Hlsl.Max(distance - 1, 0) * 55);
        float sunSide = Hlsl.Saturate(0.6f - disk.X * 0.32f - disk.Y * 0.2f);
        Float3 atmosphere = new Float3(0.12f, 0.39f, 0.75f) * shell * sunSide * 0.34f;
        if (distance > 1 + edgeWidth)
            return new Float4(background + atmosphere, 1);

        Float3 normal = new Float3(disk.X, -disk.Y,
            Hlsl.Sqrt(Hlsl.Saturate(1 - Hlsl.Dot(disk, disk))));

        // Geographic north has Earth's familiar 23.4-degree axial inclination.
        // Positive rotation moves the surface eastward across the visible disk.
        float tiltCos = 0.9177546f, tiltSin = 0.3971479f;
        float geographicX = normal.X * tiltCos + normal.Y * tiltSin;
        float tiltedY = -normal.X * tiltSin + normal.Y * tiltCos;
        // Pitch about the inclined east-west axis so the home latitude faces
        // the viewer at the disk's centre. Screen-space lighting is unchanged.
        float pitchCos = Hlsl.Cos(viewLatitudeRadians), pitchSin = Hlsl.Sin(viewLatitudeRadians);
        float geographicY = tiltedY * pitchCos + normal.Z * pitchSin;
        float geographicZ = normal.Z * pitchCos - tiltedY * pitchSin;
        float longitude = Hlsl.Atan2(geographicX, geographicZ) - rotationRadians;
        float latitude = Hlsl.Asin(Hlsl.Clamp(geographicY, -1, 1));
        Float2 uv = new Float2(Hlsl.Frac(longitude / 6.283185307f + 0.5f),
            0.5f - latitude / 3.141592654f);
        Float3 surface = D2D.SampleInputAtPosition(0,
            Hlsl.Clamp(uv * daySize, new Float2(0.5f, 0.5f), daySize - 0.5f)).XYZ;
        Float3 linearSurface = new Float3(Hlsl.Pow(Hlsl.Abs(surface.X), 2.2f),
            Hlsl.Pow(Hlsl.Abs(surface.Y), 2.2f), Hlsl.Pow(Hlsl.Abs(surface.Z), 2.2f));

        Float3 sun = Hlsl.Normalize(new Float3(-0.48f, 0.31f, 0.82f));
        Float2 cloudUv = new Float2(Hlsl.Frac(uv.X + 0.004f), uv.Y);
        float cloud = D2D.SampleInputAtPosition(1,
            Hlsl.Clamp(cloudUv * cloudSize, new Float2(0.5f, 0.5f), cloudSize - 0.5f)).X;
        cloud = Hlsl.Saturate((cloud - 0.04f) * 1.18f);
        Float2 shadowUv = new Float2(Hlsl.Frac(cloudUv.X - 0.0018f),
            Hlsl.Clamp(cloudUv.Y + 0.0012f, 0, 1));
        float shadow = D2D.SampleInputAtPosition(1,
            Hlsl.Clamp(shadowUv * cloudSize, new Float2(0.5f, 0.5f), cloudSize - 0.5f)).X;
        linearSurface *= 1 - Hlsl.Saturate(shadow - cloud) * 0.45f;
        linearSurface = Hlsl.Lerp(linearSurface, new Float3(0.87f, 0.91f, 0.96f), cloud * 0.88f);

        // Lighting is applied to linear reflectance, then converted back for
        // the display. Night illumination stays soft rather than an ink-black
        // cutout, and the day/night terminator has a narrow twilight band.
        float incidence = Hlsl.Dot(normal, sun);
        float diffuse = 0.012f + Hlsl.Saturate(incidence) * 0.97f;
        Float3 lit = linearSurface * diffuse;

        // Ocean color supplies a conservative water mask for a small solar
        // reflection. Land and the imagery's cloud layer remain matte.
        float ocean = Hlsl.Saturate((surface.Z - surface.X * 1.15f) * 5)
            * Hlsl.Saturate((0.48f - surface.Y) * 6) * (1 - cloud);
        Float3 halfway = Hlsl.Normalize(sun + new Float3(0, 0, 1));
        float reflection = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 95)
            * ocean * Hlsl.SmoothStep(0, 0.15f, incidence) * 0.2f;
        lit += new Float3(0.66f, 0.80f, 1) * reflection;
        float rim = Hlsl.Pow(Hlsl.Abs(1 - normal.Z), 3.7f)
            * Hlsl.SmoothStep(-0.17f, 0.22f, incidence);
        lit += new Float3(0.016f, 0.075f, 0.18f) * rim;
        Float3 display = new Float3(Hlsl.Pow(Hlsl.Max(lit.X, 0), 1 / 2.2f),
            Hlsl.Pow(Hlsl.Max(lit.Y, 0), 1 / 2.2f), Hlsl.Pow(Hlsl.Max(lit.Z, 0), 1 / 2.2f));
        float coverage = 1 - Hlsl.SmoothStep(1 - edgeWidth, 1 + edgeWidth, distance);
        return new Float4(Hlsl.Lerp(background + atmosphere, Hlsl.Saturate(display), coverage), 1);
    }

}
