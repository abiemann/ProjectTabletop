using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// A small registered patch of the original painting. Closing lids sample its
// own brow/cheek texture; the surrounding scales never become a flat overlay.
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotDragonEyeShader(
    Float2 imageScale, Float2 gaze, float closing, float attention, float cropTop, float cropHeight) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY / imageScale;
        Float2 center = new Float2(353.4f, 171.2f);
        Float2 d = p - center;
        Float2 axis = new Float2(0.6f, 0.8f), across = new Float2(-0.8f, 0.6f);
        float along = Hlsl.Dot(d, axis), side = Hlsl.Dot(d, across);
        float edge = Hlsl.Length(new Float2(along / 12, side / 7.8f));
        float alpha = 1 - Hlsl.SmoothStep(0.84f, 1, edge);
        if (alpha <= 0) return new Float4(0, 0, 0, 0);

        // Shift the painted iris itself, not just a dot sliding over a still
        // eye. The deformation falls away inside the unchanged socket rim.
        float iris = 1 - Hlsl.SmoothStep(0.46f, 0.89f, edge);
        Float3 color = Sample(p - gaze * iris);
        Float2 pupil = p - center - gaze;
        float radius = 1.9f + attention * 0.1f;
        float dot = 1 - Hlsl.SmoothStep(radius - 0.25f, radius + 0.3f, Hlsl.Length(pupil));
        color = Hlsl.Lerp(color, new Float3(0.012f, 0.007f, 0.004f), dot);
        Float2 highlight = (pupil - new Float2(-1.2f, -2.8f)) / new Float2(0.65f, 0.8f);
        color = Hlsl.Lerp(color, new Float3(1, 0.96f, 0.73f),
            Hlsl.Exp(-Hlsl.Dot(highlight, highlight) * 2) * 0.48f);

        // Upper lid supplies most of a blink. Its curved lip meets the smaller
        // lower lid at an off-centre seam, then opens slightly more slowly.
        float height = 7.8f * Hlsl.Sqrt(Hlsl.Saturate(1 - along * along / 144));
        float top = -height + closing * height * 1.75f;
        float bottom = height - closing * height * 0.25f;
        float enabled = Hlsl.SmoothStep(0, 0.045f, closing);
        float upper = (1 - Hlsl.SmoothStep(top - 0.5f, top + 0.5f, side)) * enabled;
        float lower = Hlsl.SmoothStep(bottom - 0.5f, bottom + 0.5f, side) * enabled;
        Float2 upperPoint = center + axis * along + across * (-8.5f - (side + 7.8f) * 0.17f);
        Float2 lowerPoint = center + axis * along + across * (8.6f + (side - 7.8f) * 0.2f);
        Float3 upperSkin = Sample(upperPoint) * 0.88f;
        Float3 lowerSkin = Sample(lowerPoint) * 0.87f;
        color = Hlsl.Lerp(color, upperSkin, upper);
        color = Hlsl.Lerp(color, lowerSkin, lower);
        float seam = Hlsl.Exp(-Hlsl.Abs(side - top) * 2.2f) * enabled;
        color *= 1 - seam * 0.32f;

        // Match the cabinet's existing smoked-glass wash precisely so the
        // feathered patch blends into the cached background at every crop.
        float y = Hlsl.Saturate((p.Y - cropTop) / cropHeight);
        float shade = y < 0.6f ? Hlsl.Lerp(15 / 255f, 45 / 255f, y / 0.6f)
            : y < 0.82f ? Hlsl.Lerp(45 / 255f, 224 / 255f, (y - 0.6f) / 0.22f)
            : Hlsl.Lerp(224 / 255f, 248 / 255f, (y - 0.82f) / 0.18f);
        color = Hlsl.Lerp(color, new Float3(7 / 255f, 8 / 255f, 14 / 255f), shade);
        return new Float4(color * alpha, alpha);
    }

    private Float3 Sample(Float2 p) => D2D.SampleInputAtPosition(0, p * imageScale).XYZ;
}
