using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.StrykersRun;

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct StrykersRunCityShader(Float2 textureSize, float distance) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 screen = D2D.GetScenePosition().XY / textureSize;
        // Painted ruins form a vertically repeating distant plane. Their travel
        // is 22% of the unchanged road shader's .0125 texture units per distance unit.
        float sourceY = Hlsl.Frac(screen.Y - distance * (.0125f * .22f));
        Float2 sample = Hlsl.Clamp(new Float2(screen.X, sourceY) * textureSize,
            new Float2(.5f, .5f), textureSize - .5f);
        Float4 color = D2D.SampleInputAtPosition(0, sample);
        // Recede behind the brighter road without dimming actors or effects.
        return new Float4(color.XYZ * .65f, color.W);
    }
}
