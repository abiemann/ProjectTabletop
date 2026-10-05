using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.CrownDeedRendering;

// Input0 is the unchanged city. Input1 shares its exact pixel registration:
// R=glass coverage, G/B=window ID low/high bytes, A=255. ID bytes must extend
// through a small guard around their aperture before R is painted separately;
// antialiasing must never multiply categorical IDs by coverage. All panes of
// one architectural window share an ID. Nonselected IDs always remain lit.
[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct CrownDeedWindowsShader(
    Float2 imageScale, float time, float strength) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 logical = D2D.GetScenePosition().XY / imageScale;
        Float2 source = logical * imageScale;
        Float4 metadata = D2D.SampleInputAtPosition(1, source);
        float coverage = Hlsl.Saturate(metadata.X);
        if (coverage <= .0001f) return new Float4(0, 0, 0, 0);

        // Coverage stays linearly filtered at native resolution; categorical
        // ID bytes are read at their source texel centre instead of interpolated.
        Float4 identity = D2D.SampleInputAtPosition(1, Hlsl.Floor(source) + .5f);
        int windowId = (int)(identity.Y * 255 + .5f) + (int)(identity.Z * 255 + .5f) * 256;
        Float4 original = D2D.SampleInputAtPosition(0, source);
        if (original.W <= 0) return new Float4(0, 0, 0, 0);
        Float3 painted = original.XYZ / Hlsl.Max(original.W, .0001f);
        float light = LightLevel(windowId, Hlsl.Max(0, time));

        // Moonlit glass retains the painting's tiny reflections and texture.
        // Only emitted light disappears: mullions, masonry and aperture edges
        // are left to the exact mask and unchanged foreground painting.
        float luminance = Hlsl.Dot(painted, new Float3(.2126f, .7152f, .0722f));
        Float3 darkGlass = new Float3(.42f, .75f, .82f) * (.015f + luminance * .055f)
            + painted * .018f;
        float darkness = (1 - light) * coverage * Hlsl.Saturate(strength);

        // Opaque source pixels support the antialiased holes in the cached
        // foreground. Outside R the pass is clear; strength0 restores the city.
        return new Float4(Hlsl.Lerp(painted, darkGlass, darkness), 1);
    }

    private static float LightLevel(int windowId, float elapsedSeconds)
    {
        if (windowId <= 0 || windowId > 65535 || Hash(windowId, 0) >= 39321) return 1;
        float initialOn = 6 + Unit(windowId, 11) * 29;
        float elapsed = elapsedSeconds - initialOn;
        if (elapsed <= 0) return 1;
        float period = 40 + Unit(windowId, 17) * 4;
        int cycle = (int)Hlsl.Floor(elapsed / period);
        float local = elapsed - cycle * period;
        float jitter = cycle == 0 ? 0 : Unit(windowId, CycleSalt(cycle, 1)) * 3;
        float fadeOut = .8f + Unit(windowId, CycleSalt(cycle, 2)) * 1.2f;
        float offDwell = 12 + Unit(windowId, CycleSalt(cycle, 3)) * 12;
        float fadeIn = .8f + Unit(windowId, CycleSalt(cycle, 4)) * 1.2f;
        float phase = local - jitter;
        if (phase <= 0) return 1;
        if (phase < fadeOut) return 1 - Ease(phase / fadeOut);
        phase -= fadeOut;
        if (phase < offDwell) return 0;
        phase -= offDwell;
        if (phase < fadeIn) return Ease(phase / fadeIn);
        return 1;
    }

    private static int CycleSalt(int cycle, int channel) =>
        (int)(((uint)cycle % 65521u * 83u + (uint)channel * 997u) % 65521u);
    private static float Unit(int windowId, int salt) => Hash(windowId, salt) / 65535f;
    private static int Hash(int windowId, int salt)
    {
        int h = (windowId ^ (salt * 131)) & 65535;
        h = ((h * 251 + 13849) ^ (h >> 7)) & 65535;
        h = ((h * 173 + 24023) ^ (h >> 5)) & 65535;
        return h;
    }
    private static float Ease(float t) => t * t * (3 - 2 * t);
}
