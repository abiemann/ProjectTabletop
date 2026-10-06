using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Wave height/velocity ping-pong updates adapt Evan Wallace's WebGL Water
// (Copyright 2011 Evan Wallace, MIT). See THIRD_PARTY_NOTICES.md. Unlike the
// frame-count-based demo, this version uses a fixed physical timestep and grid spacing.

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterDisturbanceShader(Float2 size, float aspect,
    Float2 center, float radius, float strength) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 field = D2D.SampleInputAtPosition(0, p);
        Float2 delta = (p / size - center) * new Float2(aspect, 1);
        float r2 = Hlsl.Dot(delta, delta) / (radius * radius);
        // A depression pushes an equal-volume rim outward. Zero-integral radial
        // displacement avoids accumulating permanent mounds from repeated strokes.
        float impulse = r2 < 9 ? (1 - 3 * r2) * Hlsl.Exp(-3 * r2) : 0;
        float height = Hlsl.Clamp(field.X - strength * impulse, -.04f, .04f);
        return new Float4(height, field.Y, 0, 1);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterStepShader(Float2 size, float aspect, float dt,
    float waveSpeed) : ID2D1PixelShader
{
    private float Height(Float2 p) => D2D.SampleInputAtPosition(0,
        Hlsl.Clamp(p, new Float2(.5f, .5f), size - .5f)).X;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 field = D2D.SampleInputAtPosition(0, p);
        Float2 cell = new Float2(aspect, 1) / size;
        float laplacian = (Height(p + new Float2(-1, 0)) + Height(p + new Float2(1, 0)) - 2 * field.X) /
            (cell.X * cell.X) + (Height(p + new Float2(0, -1)) + Height(p + new Float2(0, 1)) - 2 * field.X) /
            (cell.Y * cell.Y);
        Float2 uv = p / size;
        float edge = Hlsl.Min(Hlsl.Min(uv.X, 1 - uv.X) * aspect, Hlsl.Min(uv.Y, 1 - uv.Y));
        float drag = .65f + 1.4f * (1 - Hlsl.SmoothStep(0, .035f, edge));
        float velocity = (field.Y + laplacian * waveSpeed * waveSpeed * dt) * Hlsl.Exp(-drag * dt);
        velocity = Hlsl.Clamp(velocity, -.30f, .30f);
        float height = Hlsl.Clamp(field.X + velocity * dt, -.04f, .04f);
        return new Float4(height, velocity, 0, 1);
    }
}
