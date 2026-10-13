using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.StrykersRun;

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct StrykersRunRoadShader(Float2 textureSize, float distance) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 screen = D2D.GetScenePosition().XY / textureSize;
        float scale = (.34f + .66f * Hlsl.Saturate(screen.Y)) / (.34f + .66f * .78f);
        float worldX = .5f + (screen.X - .5f) / scale;
        if (worldX < 0 || worldX > 1) return new Float4(0, 0, 0, 0);
        // Mirrored tile travel is continuous at both ends of the painted road.
        float travel = screen.Y - distance * .0125f;
        float sourceY = 1 - Hlsl.Abs(Hlsl.Frac(travel * .5f) * 2 - 1);
        Float2 sample = Hlsl.Clamp(new Float2(worldX, sourceY) * textureSize,
            new Float2(.5f, .5f), textureSize - .5f);
        Float4 road = D2D.SampleInputAtPosition(0, sample);
        float fog = Hlsl.Pow(1 - Hlsl.Saturate(screen.Y), 3) * .35f;
        return new Float4(Hlsl.Lerp(road.XYZ, new Float3(.075f, .11f, .15f), fog), 1);
    }
}
