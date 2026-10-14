using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly partial struct WaterSurfaceShader
{
    private Float3 WetRockSample(Float2 position)
    {
        // One natural stratification cycle spans the compact waterfall; the
        // supplied photograph inspires its mineral colour, not its pixels.
        Float2 uv = Hlsl.Frac(position / .44f + new Float2(.41f, .23f));
        return D2D.SampleInputAtPosition(3, uv * (size - 1) + .5f).XYZ;
    }

    private Float3 WetRockTexture(Float3 p, Float3 normal)
    {
        Float3 weight = Hlsl.Abs(normal);
        weight *= weight;
        weight *= weight;
        weight /= Hlsl.Max(.000001f, weight.X + weight.Y + weight.Z);
        Float3 side = WetRockSample(new Float2(p.Y, p.Z));
        Float3 front = WetRockSample(new Float2(p.X, p.Z));
        Float3 top = WetRockSample(p.XY);
        return side * weight.X + front * weight.Y + top * weight.Z;
    }
}
