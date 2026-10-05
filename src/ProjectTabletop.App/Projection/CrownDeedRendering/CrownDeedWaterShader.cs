using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.CrownDeedRendering;

// Both inputs share the city image's pixel coordinates. Metadata is opaque:
// R is exact water coverage, G is a registered warm reflection footprint,
// B is reserved, and A must be one (the data channels are not premultiplied).
// Draw beneath the cached foreground's matching water cutouts. Mask coverage
// tapers the movement, while opaque source-water beneath its antialiased edge
// prevents a translucent seam. Outside the mask the result is exactly clear.
[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct CrownDeedWaterShader(
    Float2 imageScale, float time, float boardAspect, float strength) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY / imageScale;
        Float4 metadata = Metadata(p);
        float coverage = Hlsl.Saturate(metadata.X);
        if (coverage <= .0001f) return new Float4(0, 0, 0, 0);
        float effectStrength = coverage * Hlsl.Saturate(strength);

        Float4 original = City(p);
        if (original.W <= 0) return new Float4(0, 0, 0, 0);
        float aspect = Hlsl.Clamp(boardAspect, .2f, 5);
        Float2 q = new Float2(p.X * aspect, p.Y);

        // Broad swells and a finer current advect slowly on different axes.
        // A shared physical field spans every canal patch without seams,
        // tiled phase resets, random sparkle frames or scrolling texture bars.
        float swell = Noise(q * new Float2(.034f, .042f) + new Float2(-time * .038f, time * .021f));
        float detail = Noise(q * new Float2(.112f, .088f) + new Float2(time * .067f, -time * .049f));
        float phaseA = Hlsl.Dot(q, new Float2(.110f, .180f)) - time * .45f + (swell - .5f) * 2.4f;
        float phaseB = Hlsl.Dot(q, new Float2(-.210f, .100f)) + time * .63f + (detail - .5f) * 1.7f;
        float phaseC = Hlsl.Dot(q, new Float2(.037f, .510f)) - time * 1.12f + swell;
        float waveA = Hlsl.Sin(phaseA), waveB = Hlsl.Sin(phaseB), waveC = Hlsl.Sin(phaseC);
        Float2 normal = new Float2(waveA * .60f + waveB * .26f + waveC * .14f,
            Hlsl.Cos(phaseA) * .34f - Hlsl.Cos(phaseB) * .31f + Hlsl.Cos(phaseC) * .15f);

        // Refraction is subpixel at a logical 1000-unit board. Shore samples
        // lock distortion to water; a narrow bridge or moored boat cannot be
        // dragged into the current by a neighbouring displaced tap.
        Float2 shoreX = new Float2(1.25f / aspect, 0), shoreY = new Float2(0, 1.25f);
        float shore = Hlsl.Min(Hlsl.Min(Metadata(p - shoreX).X, Metadata(p + shoreX).X),
            Hlsl.Min(Metadata(p - shoreY).X, Metadata(p + shoreY).X));
        float interior = Hlsl.SmoothStep(.75f, .995f, Hlsl.Min(metadata.X, shore)) * effectStrength;
        Float2 displaced = p + normal * new Float2(.62f / aspect, .38f) * interior;
        float displacedCoverage = Metadata(displaced).X;
        Float4 sampled = original;
        if (displacedCoverage >= .995f) sampled = City(displaced);
        Float3 color = sampled.XYZ / Hlsl.Max(sampled.W, .0001f);
        Float3 painted = original.XYZ / Hlsl.Max(original.W, .0001f);

        // Preserve the painted dark teal. Small changes in the surface normal
        // reveal soft, cool ridges instead of repainting the canal bright blue.
        float normalLight = .98f + waveA * .032f + waveB * .019f;
        color *= normalLight;
        float coolRidge = Hlsl.SmoothStep(.43f, .82f, normal.Y * .5f + .5f)
            * (.35f + detail * .65f);
        color += new Float3(.035f, .130f, .135f) * coolRidge * .13f;

        // Lamps remain fixed on their banks. Their elongated metadata lobes
        // locate reflections; moving water breaks each into scattered facets
        // with warm cores and dark spaces between.
        float paintedWarm = Hlsl.SmoothStep(.025f, .20f, painted.X - painted.Z)
            * Hlsl.SmoothStep(.10f, .44f, painted.X);
        float lamp = Hlsl.Saturate(Hlsl.Max(metadata.Y, paintedWarm * .55f));
        float fragments = Noise(new Float2(q.X * .20f - time * .21f, q.Y * .51f + time * .18f));
        float band = .5f + .5f * Hlsl.Sin(q.Y * 1.53f + normal.X * .90f
            + time * .62f + (detail - .5f) * 2.1f);
        float crest = Hlsl.SmoothStep(.76f, .985f, band);
        float breakup = Hlsl.SmoothStep(.28f, .75f, fragments);
        float reflection = lamp * (.13f + crest * .72f) * (.42f + breakup * .58f);
        color *= 1 - paintedWarm * lamp * (.045f + .085f * (1 - crest));
        color += new Float3(.97f, .59f, .21f) * reflection * .24f;
        color += new Float3(.70f, .63f, .41f) * (crest * crest * crest) * breakup * lamp * .08f;

        return new Float4(Hlsl.Lerp(painted, Hlsl.Saturate(color), effectStrength), 1);
    }

    private Float4 City(Float2 logical) => D2D.SampleInputAtPosition(0, logical * imageScale);
    private Float4 Metadata(Float2 logical) => D2D.SampleInputAtPosition(1, logical * imageScale);

    private static float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p,
        new Float2(127.1f, 311.7f))) * 43758.5453f);

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }
}
