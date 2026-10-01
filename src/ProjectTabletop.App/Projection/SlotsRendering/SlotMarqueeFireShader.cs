using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// A continuous, bottom-fed flame sheet in physical cabinet coordinates.
// The same upward flow carries its broad curls, fine folds and torn tips.
// No spherical core or repeated sprite silhouette is used for the marquee.
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotMarqueeFireShader(float time, Float2 size) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 pixel = D2D.GetScenePosition().XY;
        float x = pixel.X - size.X * 0.5f;
        float height = size.Y - 6 - pixel.Y;
        float rise = height - time * 32;

        // Large eddies bend the flame sheets; smaller eddies fold and tear
        // them. All scales advect upward instead of boiling in place.
        Float2 flow = new Float2(x * 0.039f + 13.7f, rise * 0.033f + 31.4f);
        Float2 warp = new Float2(Fractal(flow * 0.73f + new Float2(7.3f, 2.1f)),
            Fractal(flow * 0.81f + new Float2(1.9f, 11.6f))) - 0.5f;
        float curl = Hlsl.Saturate(height / 65);
        Float2 folded = flow + warp * (0.6f + curl * 2.2f);
        float body = Fractal(folded);
        float fine = Fractal(folded * new Float2(2.6f, 1.7f) + warp * 2.1f);
        float fuel = Noise(new Float2(x * 0.052f + 71.3f, time * 0.21f + 8.9f));
        // A short, uneven fuel shoulder grows into the main fire beside the
        // lettering. Shape the rising sheets themselves rather than fading
        // a full-height column into a rectangular wall of blurred orange.
        float sideDistance = Hlsl.Min(pixel.X, size.X - pixel.X);
        float inward = sideDistance + warp.X * 8 + (fuel - 0.5f) * 5;
        float edgeBuild = Hlsl.SmoothStep(2, 34, inward);
        float localRise = Hlsl.Lerp(8, 80, edgeBuild);
        float density = 1.14f + (fuel - 0.5f) * 0.23f - height * 0.0088f
            - body * 1.55f + (fine - 0.5f) * 0.10f
            - Hlsl.Max(height, 0) * 0.0088f * (80 / localRise - 1);

        // Thin luminous folds sit inside translucent orange sheets. Dark
        // openings and narrow tips survive rather than filling with white.
        float flame = Hlsl.SmoothStep(0.01f, 0.105f, density);
        float fold = 1 - Hlsl.SmoothStep(0.025f, 0.12f,
            Hlsl.Abs(fine - 0.47f + warp.Y * 0.15f));
        float inside = Hlsl.SmoothStep(0.07f, 0.22f, density);
        float heat = Hlsl.Saturate(density * 1.40f + fold * inside * 0.22f);
        Float3 color = Hlsl.Lerp(new Float3(0.66f, 0.039f, 0.003f),
            new Float3(1, 0.29f, 0.012f), Hlsl.SmoothStep(0, 0.28f, heat));
        color = Hlsl.Lerp(color, new Float3(1, 0.72f, 0.18f),
            Hlsl.SmoothStep(0.23f, 0.67f, heat));
        color = Hlsl.Lerp(color, new Float3(1, 0.94f, 0.63f),
            Hlsl.SmoothStep(0.72f, 1, heat) * 0.65f);

        // A weak local radiance softens the transparent outer edge without
        // creating a broad glow disk. The charcoal remains visible below.
        float radiance = Hlsl.SmoothStep(-0.10f, 0.035f, density) * 0.095f
            * Hlsl.Lerp(0.45f, 1, edgeBuild);
        float alpha = Hlsl.Saturate(flame * (0.67f + inside * 0.24f) + radiance);
        // Only narrow coverage remains at the source boundary. The visible
        // silhouette has already tapered into low, turbulent flame tongues.
        float coverage = Hlsl.SmoothStep(0, 1.25f, sideDistance);
        // Irregular glowing roots follow the charcoal instead of leaving a
        // straight luminous underline at the bottom of the source rectangle.
        float bedLift = 0.75f + Noise(new Float2(x * 0.17f + 41.9f, time * 0.35f + 9.4f)) * 4.5f;
        float baseFade = Hlsl.SmoothStep(-0.6f, 1.4f, height - bedLift);
        alpha *= coverage * baseFade;
        return new Float4(color * alpha, alpha);
    }

    private static float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p,
        new Float2(127.1f, 311.7f))) * 43758.5453f);

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }

    private static float Fractal(Float2 p) => Noise(p) * 0.56f
        + Noise(p * 2.03f + 9.2f) * 0.27f
        + Noise(p * 4.11f + 18.7f) * 0.12f
        + Noise(p * 8.23f + 27.4f) * 0.05f;
}
