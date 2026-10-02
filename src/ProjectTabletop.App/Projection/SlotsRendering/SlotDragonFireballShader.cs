using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// A compact burning head with a turbulent wake. The volume ends inside its
// transparent source padding; no rectangular edge fade shapes the silhouette.
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotDragonFireballShader(float time, float seed, float opacity, float trail) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 uv = D2D.GetScenePosition().XY / new Float2(256, 512);
        Float2 p = (uv - new Float2(0.5f, 0.25f)) * new Float2(4, 8);
        Float2 flow = p * new Float2(2.1f, 1.8f) + new Float2(seed * 3.7f, -time * 6.4f);
        Float2 warp = new Float2(Fractal(flow * 0.71f + new Float2(3.1f, 8.7f)),
            Fractal(flow * 0.83f + new Float2(11.3f, 2.9f))) - 0.5f;
        Float2 folded = flow + warp * 1.7f;
        float body = Fractal(folded);
        float detail = Fractal(folded * 2.7f + new Float2(17.4f, time * 0.8f));

        float headDistance = Hlsl.Length(p + warp * 0.16f);
        float head = 1 - Hlsl.SmoothStep(0.73f, 1.12f, headDistance + (body - 0.5f) * 0.18f);
        float reach = Hlsl.Max(0.4f, Hlsl.Saturate(trail) * 4.8f);
        float along = Hlsl.Saturate(p.Y / reach);
        float width = 0.73f * Hlsl.Pow(1 - along, 0.78f);
        float drift = warp.X * (0.20f + along * 0.65f);
        float edge = width - Hlsl.Abs(p.X + drift) + (detail - 0.5f) * along * 0.30f;
        float wake = Hlsl.SmoothStep(-0.08f, 0.09f, edge)
            * Hlsl.SmoothStep(-0.18f, 0.30f, p.Y)
            * (1 - Hlsl.SmoothStep(reach * (0.73f + body * 0.16f), reach, p.Y));
        wake *= Hlsl.SmoothStep(0.20f, 0.48f, body + detail * 0.28f) * Hlsl.Saturate(trail * 4);
        float fuel = 1 - (1 - head) * (1 - wake);
        float fold = 1 - Hlsl.SmoothStep(0.015f, 0.12f, Hlsl.Abs(detail - 0.49f + warp.Y * 0.14f));
        float heat = Hlsl.Saturate(body * 1.25f + fold * 0.25f + head * 0.16f);
        Float3 color = Hlsl.Lerp(new Float3(0.81f, 0.05f, 0.002f),
            new Float3(1, 0.32f, 0.012f), Hlsl.SmoothStep(0.18f, 0.49f, heat));
        color = Hlsl.Lerp(color, new Float3(1, 0.77f, 0.18f), Hlsl.SmoothStep(0.46f, 0.78f, heat));
        color = Hlsl.Lerp(color, new Float3(1, 0.98f, 0.73f), Hlsl.SmoothStep(0.82f, 1, heat) * head);
        float radiance = (1 - Hlsl.SmoothStep(0.9f, 1.65f, headDistance)) * 0.10f;
        float alpha = Hlsl.Saturate(fuel * (0.74f + detail * 0.24f) + radiance) * Hlsl.Saturate(opacity);
        return new Float4(color * alpha, alpha);
    }

    private static float Hash(Float2 p)
    {
        Float3 q = Hlsl.Frac(new Float3(p.X, p.Y, p.X) * 0.1031f);
        q += Hlsl.Dot(q, new Float3(q.Y, q.Z, q.X) + 33.33f);
        return Hlsl.Frac((q.X + q.Y) * q.Z);
    }

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }

    private static float Fractal(Float2 p) => Noise(p) * 0.53f + Noise(p * 2.03f + 9.2f) * 0.28f
        + Noise(p * 4.11f + 18.7f) * 0.13f + Noise(p * 8.23f + 27.4f) * 0.06f;
}
