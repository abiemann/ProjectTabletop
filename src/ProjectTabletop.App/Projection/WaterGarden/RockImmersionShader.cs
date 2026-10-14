using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Shade the rock's submerged foot without moving its silhouette. The original
// grain remains visible, with live water showing through its deepest edge.
[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct RockImmersionShader(Float2 imageSize, float submergedPixels) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 rock = D2D.SampleInputAtPosition(0, p);
        Float4 encoded = D2D.SampleInputAtPosition(1,
            new Float2(Hlsl.Clamp(p.X, .5f, imageSize.X - .5f), imageSize.Y * .5f));
        // Point sampling preserves the two-byte row value across byte carries.
        float foot = encoded.X * 65280 + encoded.Y * 255;
        if (foot <= 0 || submergedPixels <= 0 || rock.W <= 0) return rock;

        float belowLine = p.Y - (foot - submergedPixels);
        float depth = Hlsl.Saturate(belowLine / Hlsl.Max(1, submergedPixels));
        float wet = Hlsl.SmoothStep(-.5f, .5f, belowLine);
        // A restrained wet tint keeps the stone detail instead of painting a
        // uniform blue band. Only the contour-defined foot receives this shade.
        Float3 underwater = Hlsl.Lerp(rock.XYZ * .82f,
            new Float3(.075f, .34f, .30f) * rock.W, .12f + .08f * depth);
        float transmission = 1 - .26f * Hlsl.SmoothStep(.35f, 1, depth);
        Float4 result = Hlsl.Lerp(rock,
            new Float4(underwater * transmission, rock.W * transmission), wet);

        // A thin contact sheen follows the same irregular waterline. It never
        // extends beyond the source alpha or displaces the hit-test silhouette.
        float lineWidth = Hlsl.Max(1.25f, submergedPixels * .025f);
        float sheen = (1 - Hlsl.SmoothStep(.25f * lineWidth, lineWidth,
            Hlsl.Abs(belowLine))) * .18f;
        Float3 color = Hlsl.Lerp(result.XYZ,
            new Float3(.48f, .66f, .60f) * result.W, sheen);
        return new Float4(color, result.W);
    }
}
