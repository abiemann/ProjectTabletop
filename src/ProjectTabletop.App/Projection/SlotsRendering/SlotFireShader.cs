using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// An advected, domain-warped density field rather than a stack of glow disks.
// The source is an explicitly bounded square; alpha is premultiplied for D2D.
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotFireShader(float time, float seed, float opacity) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 uv = D2D.GetScenePosition().XY / 256;
        float x = (uv.X - 0.5f) * 2;
        float height = (0.92f - uv.Y) / 0.88f;
        float clock = time + seed * 7.31f;
        Float2 drift = new Float2(seed * 3.7f, -clock * 1.55f);
        Float2 p = new Float2(x * 3.6f, height * 3.1f) + drift;
        Float2 warp = new Float2(Fractal(p + new Float2(2.7f, 0.4f)),
            Fractal(p + new Float2(6.1f, 4.8f))) - 0.5f;
        float turbulence = Fractal(p * new Float2(1.4f, 1.9f) + warp * 2.8f);
        float filaments = Fractal(p * new Float2(3.7f, 2.3f) + warp * 4.2f);

        // Independently changing columns and rising turbulence tear the edge
        // into tapered tongues with open dark gaps between the flames.
        float column = Fractal(new Float2(x * 5.1f + warp.X * 0.9f, clock * 0.52f + seed));
        float tip = 0.68f + column * 0.32f;
        float width = 0.62f * Hlsl.Pow(Hlsl.Saturate(1 - height / tip), 0.63f);
        float sway = Hlsl.Sin(height * 7.8f - clock * 2.3f + seed) * height * 0.075f;
        float edge = width - Hlsl.Abs(x + sway + warp.X * height * 0.24f);
        float flame = Hlsl.Saturate(edge * 8.5f + (turbulence - 0.48f) * 2.1f);
        flame *= Hlsl.SmoothStep(0, 0.13f, height) * (1 - Hlsl.SmoothStep(tip - 0.14f, tip, height));

        Float2 corePoint = (uv - new Float2(0.5f, 0.68f)) / new Float2(0.29f, 0.255f);
        float coreDistance = Hlsl.Length(corePoint);
        float core = 1 - Hlsl.SmoothStep(0.53f, 1.03f, coreDistance + (turbulence - 0.5f) * 0.2f);
        float heat = Hlsl.Saturate(flame * 0.7f + core * 0.81f + (filaments - 0.45f) * 0.34f);
        float density = Hlsl.Saturate(flame * 1.18f + core * 0.9f);
        Float3 color = Hlsl.Lerp(new Float3(0.56f, 0.028f, 0.002f),
            new Float3(1, 0.22f, 0.007f), Hlsl.Saturate(heat * 2.2f));
        color = Hlsl.Lerp(color, new Float3(1, 0.69f, 0.12f), Hlsl.Saturate((heat - 0.36f) * 2.1f));
        color = Hlsl.Lerp(color, new Float3(1, 0.96f, 0.66f), Hlsl.Saturate((heat - 0.77f) * 4));

        // Fine rising sparks are stretched along the flow, not circular blobs.
        float embers = 0;
        for (int index = 0; index < 7; index++)
        {
            float random = Hash(new Float2(index * 5.17f, seed + 1.4f));
            float age = Hlsl.Frac(clock * (0.29f + random * 0.18f) + random * 8.3f);
            float ex = 0.5f + (random - 0.5f) * 0.55f + Hlsl.Sin(age * 7 + random * 15) * 0.035f;
            float ey = 0.67f - age * 0.62f;
            Float2 delta = (uv - new Float2(ex, ey)) / new Float2(0.0035f, 0.014f);
            embers += Hlsl.Exp(-Hlsl.Dot(delta, delta) * 1.8f) * Hlsl.Sin(age * 3.14159265f);
        }
        float alpha = Hlsl.Saturate(density + embers) * opacity;
        color = Hlsl.Lerp(color, new Float3(1, 0.79f, 0.31f), Hlsl.Saturate(embers));
        float boundary = Hlsl.SmoothStep(0, 0.025f, uv.X) * (1 - Hlsl.SmoothStep(0.975f, 1, uv.X))
            * Hlsl.SmoothStep(0, 0.025f, uv.Y) * (1 - Hlsl.SmoothStep(0.96f, 1, uv.Y));
        alpha *= boundary;
        return new Float4(color * alpha, alpha);
    }

    private static float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new Float2(127.1f, 311.7f))) * 43758.5453f);

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }

    private static float Fractal(Float2 p) => Noise(p) * 0.54f + Noise(p * 2.03f + 9.2f) * 0.27f
        + Noise(p * 4.11f + 18.7f) * 0.13f + Noise(p * 8.23f + 27.4f) * 0.06f;
}
