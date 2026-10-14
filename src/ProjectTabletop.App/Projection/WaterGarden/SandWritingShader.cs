using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// A shallow groove, lit from the upper left. Its translucent shading preserves
// the actual ground grains below it; there is no opaque text face or backdrop.
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SandWritingShader(float pixelScale) : ID2D1PixelShader
{
    private float Groove(Float2 p) => D2D.SampleInputAtPosition(0, p).W;
    private float Grain(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p,
        new Float2(127.1f, 311.7f))) * 43758.5453f);

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 sand = p / pixelScale;
        float fine = Grain(Hlsl.Floor(sand * 3.1f));
        float coarse = Grain(Hlsl.Floor(sand * 1.3f));
        // Sub-grain irregularity belongs to the trench, not a rectangular fade.
        p += new Float2(fine - .5f, coarse - .5f) * (.42f * pixelScale);
        float depth = Groove(p);
        float step = .7f * pixelScale;
        Float2 gradient = new Float2(
            Groove(p + new Float2(step, 0)) - Groove(p - new Float2(step, 0)),
            Groove(p + new Float2(0, step)) - Groove(p - new Float2(0, step)));
        // Negative height makes the upper-left inner bank shaded and the
        // opposite bank lit. The floor stays sandy rather than ink-filled.
        float light = Hlsl.Dot(gradient, new Float2(-.65f, -.76f)) * 1.15f;
        float floor = depth * (.24f + fine * .11f);
        float lip = (1 - Hlsl.SmoothStep(.05f, .22f, depth)) *
            Hlsl.SmoothStep(.008f, .055f, depth);
        float tone = light - floor + lip * (.10f + coarse * .10f);
        float coverage = Hlsl.SmoothStep(.008f, .10f, depth);
        float alpha = Hlsl.Min(.82f, Hlsl.Abs(tone)) * coverage;
        Float3 tint = tone > 0 ? new Float3(1, .91f, .73f) : new Float3(.19f, .105f, .035f);
        return new Float4(tint * alpha, alpha);
    }
}
