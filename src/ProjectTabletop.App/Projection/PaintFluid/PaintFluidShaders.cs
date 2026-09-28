using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.PaintFluid;

// The RK2 advection, pressure stencils and wet-surface BRDF are adapted from
// David Li's Fluid Paint (MIT, 2017). See THIRD_PARTY_NOTICES.md for attribution.
// Fields use alpha=1 throughout: their B channel is fluid height, never opacity.

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidDropShader(
    Float2 size, Float2 center, float radius, float amount, Float3 pigment,
    float aspect, float seed, int field) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 previous = D2D.SampleInputAtPosition(0, p);
        Float2 d = p / size - center;
        d.X *= aspect;
        float r = Hlsl.Length(d) / radius;
        // A deposited volume has a smooth mound, with a clean meniscus instead
        // of a pre-drawn silhouette or a noise-textured circular sticker.
        float mound = Hlsl.Pow(Hlsl.Saturate(1 - r * r), 1.25f) * amount;
        Float2 normal = d / Hlsl.Max(Hlsl.Length(d), 0.00001f);
        Float2 impactDirection = new Float2(Hlsl.Cos(seed * 2.39996323f), Hlsl.Sin(seed * 2.39996323f));
        mound *= 1 + Hlsl.Dot(normal, impactDirection) * Hlsl.Min(r, 1) * 0.12f;
        if (field == 0)
        {
            Float2 tangent = new Float2(-normal.Y, normal.X);
            float impulse = Hlsl.Sin(Hlsl.Min(r, 1) * 3.14159265f) * mound;
            Float2 speed = previous.XY + (normal * 35 + tangent * Hlsl.Sin(seed * 1.7f) * 19
                + impactDirection * 5) * impulse;
            speed *= Hlsl.Min(1, 8 / Hlsl.Max(Hlsl.Length(speed), 0.0001f));
            return new Float4(speed, Hlsl.Min(previous.Z + mound, 5), 1);
        }
        if (field == 1)
        {
            return new Float4(Hlsl.Min(previous.XYZ + pigment * mound, new Float3(20, 20, 20)), 1);
        }
        // Sparse gold particles are injected into the liquid, then advected
        // with it. Their locations are not recomputed in the final shading.
        Float2 cell = Hlsl.Floor(p / 1.7f);
        float random = Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(cell, new Float2(12.9898f, 78.233f)) + seed * 15.73f) * 43758.5453f);
        float metallic = Hlsl.Frac(seed * 0.6180339887f) > 0.64f ? 1 : 0;
        float flake = Hlsl.SmoothStep(0.970f, 0.995f, random) * mound * metallic;
        return new Float4(Hlsl.Min(previous.X + flake, 2), 0, 0, 1);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidVelocityShader(Float2 size, float dt) : ID2D1PixelShader
{
    private Float4 Sample(Float2 p) => D2D.SampleInputAtPosition(0, Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f));

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 center = Sample(p);
        Float2 half = p - center.XY * (0.5f * dt);
        Float4 value = Sample(p - Sample(half).XY * dt);
        Float4 left = Sample(p + new Float2(-1, 0));
        Float4 right = Sample(p + new Float2(1, 0));
        Float4 top = Sample(p + new Float2(0, -1));
        Float4 bottom = Sample(p + new Float2(0, 1));
        // Viscosity diffuses momentum; a clear underlying film provides finite
        // mobility even where pigment has not reached yet.
        Float2 viscosity = (left.XY + right.XY + top.XY + bottom.XY - center.XY * 4) * (7 * dt);
        Float2 velocity = (value.XY + viscosity) * Hlsl.Exp(-1.10f * dt);
        velocity *= Hlsl.Min(1, 8 / Hlsl.Max(Hlsl.Length(velocity), 0.0001f));
        if (p.X < 1.5f || p.X > size.X - 1.5f) velocity.X = 0;
        if (p.Y < 1.5f || p.Y > size.Y - 1.5f) velocity.Y = 0;
        return new Float4(velocity, center.Z, 1);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidDivergenceShader(Float2 size) : ID2D1PixelShader
{
    private Float4 Sample(Float2 p) => D2D.SampleInputAtPosition(0, Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f));

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        float divergence = (Sample(p + new Float2(1, 0)).X - Sample(p - new Float2(1, 0)).X
            + Sample(p + new Float2(0, 1)).Y - Sample(p - new Float2(0, 1)).Y) * 0.5f;
        return new Float4(divergence, 0, 0, 1);
    }
}

[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DInputDescription(1, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidPressureShader(Float2 size) : ID2D1PixelShader
{
    private float Sample(Float2 p) => D2D.SampleInputAtPosition(0, Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f)).X;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        float divergence = D2D.SampleInputAtPosition(1, p).X;
        float pressure = (Sample(p + new Float2(-1, 0)) + Sample(p + new Float2(1, 0))
            + Sample(p + new Float2(0, -1)) + Sample(p + new Float2(0, 1)) - divergence) * 0.25f;
        return new Float4(pressure, 0, 0, 1);
    }
}

[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DInputDescription(1, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidProjectShader(Float2 size) : ID2D1PixelShader
{
    private Float2 Clamp(Float2 p) => Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f);

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 value = D2D.SampleInputAtPosition(0, p);
        float left = D2D.SampleInputAtPosition(1, Clamp(p + new Float2(-1, 0))).X;
        float right = D2D.SampleInputAtPosition(1, Clamp(p + new Float2(1, 0))).X;
        float top = D2D.SampleInputAtPosition(1, Clamp(p + new Float2(0, -1))).X;
        float bottom = D2D.SampleInputAtPosition(1, Clamp(p + new Float2(0, 1))).X;
        Float2 velocity = value.XY - new Float2(right - left, bottom - top) * 0.5f;
        // Pressure projection governs lateral mixing. Thin-film gravity adds a
        // small compressible downhill component: deposited mounds level and
        // neighbouring drops meet without a brush dragging them together.
        float hLeft = D2D.SampleInputAtPosition(0, Clamp(p + new Float2(-1, 0))).Z;
        float hRight = D2D.SampleInputAtPosition(0, Clamp(p + new Float2(1, 0))).Z;
        float hTop = D2D.SampleInputAtPosition(0, Clamp(p + new Float2(0, -1))).Z;
        float hBottom = D2D.SampleInputAtPosition(0, Clamp(p + new Float2(0, 1))).Z;
        velocity -= new Float2(hRight - hLeft, hBottom - hTop) * 14;
        velocity *= Hlsl.Min(1, 8 / Hlsl.Max(Hlsl.Length(velocity), 0.0001f));
        if (p.X < 1.5f || p.X > size.X - 1.5f) velocity.X = 0;
        if (p.Y < 1.5f || p.Y > size.Y - 1.5f) velocity.Y = 0;
        return new Float4(velocity, value.Z, 1);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidHeightShader(Float2 size, float dt) : ID2D1PixelShader
{
    private Float4 Sample(Float2 p) => D2D.SampleInputAtPosition(0, Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f));

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float4 center = Sample(p);
        Float4 left = Sample(p + new Float2(-1, 0));
        Float4 right = Sample(p + new Float2(1, 0));
        Float4 top = Sample(p + new Float2(0, -1));
        Float4 bottom = Sample(p + new Float2(0, 1));
        float east = p.X > size.X - 1.5f ? 0 : (center.X + right.X) * 0.5f;
        float west = p.X < 1.5f ? 0 : (left.X + center.X) * 0.5f;
        float south = p.Y > size.Y - 1.5f ? 0 : (center.Y + bottom.Y) * 0.5f;
        float north = p.Y < 1.5f ? 0 : (top.Y + center.Y) * 0.5f;
        float fluxEast = east * (east >= 0 ? center.Z : right.Z);
        float fluxWest = west * (west >= 0 ? left.Z : center.Z);
        float fluxSouth = south * (south >= 0 ? center.Z : bottom.Z);
        float fluxNorth = north * (north >= 0 ? top.Z : center.Z);
        // Shared face fluxes enter one cell and leave its neighbour by exactly
        // the same amount. This conserves deposited volume under compressible
        // leveling; passive RK2 concentration advection would create volume.
        float laplacian = left.Z + right.Z + top.Z + bottom.Z - center.Z * 4;
        float height = center.Z - dt * (fluxEast - fluxWest + fluxSouth - fluxNorth) + laplacian * (6 * dt);
        return new Float4(center.XY, Hlsl.Clamp(height, 0, 5), 1);
    }
}

[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidPigmentShader(Float2 size, float dt, float diffusion) : ID2D1PixelShader
{
    private Float2 Clamp(Float2 p) => Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f);
    private Float3 Sample(Float2 p) => D2D.SampleInputAtPosition(0, Clamp(p)).XYZ;
    private Float2 Velocity(Float2 p) => D2D.SampleInputAtPosition(1, Clamp(p)).XY;

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float3 center = Sample(p);
        Float3 left = Sample(p + new Float2(-1, 0));
        Float3 right = Sample(p + new Float2(1, 0));
        Float3 top = Sample(p + new Float2(0, -1));
        Float3 bottom = Sample(p + new Float2(0, 1));
        Float2 velocity = Velocity(p);
        float east = p.X > size.X - 1.5f ? 0 : (velocity.X + Velocity(p + new Float2(1, 0)).X) * 0.5f;
        float west = p.X < 1.5f ? 0 : (Velocity(p + new Float2(-1, 0)).X + velocity.X) * 0.5f;
        float south = p.Y > size.Y - 1.5f ? 0 : (velocity.Y + Velocity(p + new Float2(0, 1)).Y) * 0.5f;
        float north = p.Y < 1.5f ? 0 : (Velocity(p + new Float2(0, -1)).Y + velocity.Y) * 0.5f;
        Float3 fluxEast = east * (east >= 0 ? center : right);
        Float3 fluxWest = west * (west >= 0 ? left : center);
        Float3 fluxSouth = south * (south >= 0 ? center : bottom);
        Float3 fluxNorth = north * (north >= 0 ? top : center);
        Float3 laplacian = left + right + top + bottom - center * 4;
        Float3 density = center - dt * (fluxEast - fluxWest + fluxSouth - fluxNorth) + laplacian * (diffusion * dt);
        return new Float4(Hlsl.Clamp(density, new Float3(0, 0, 0), new Float3(20, 20, 20)), 1);
    }
}

[D2DInputCount(3)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputComplex(2)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DInputDescription(2, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidSurfaceShader(Float2 size, float aspect) : ID2D1PixelShader
{
    private float Height(Float2 p) => D2D.SampleInputAtPosition(0, Hlsl.Clamp(p, new Float2(0.5f, 0.5f), size - 0.5f)).Z;

    private float Specular(Float3 normal, Float3 light, float roughness)
    {
        Float3 eye = new Float3(0, 0, 1);
        Float3 half = Hlsl.Normalize(light + eye);
        float nDotH = Hlsl.Saturate(Hlsl.Dot(normal, half));
        float nDotL = Hlsl.Saturate(Hlsl.Dot(normal, light));
        float nDotV = Hlsl.Max(0.01f, Hlsl.Dot(normal, eye));
        float lDotH = Hlsl.Saturate(Hlsl.Dot(light, half));
        float a2 = roughness * roughness;
        float denominator = nDotH * nDotH * (a2 - 1) + 1;
        float distribution = a2 / Hlsl.Max(3.14159265f * denominator * denominator, 0.0001f);
        float gl = nDotL + Hlsl.Sqrt(a2 + (1 - a2) * nDotL * nDotL);
        float gv = nDotV + Hlsl.Sqrt(a2 + (1 - a2) * nDotV * nDotV);
        float fresnel = 0.045f + 0.955f * Hlsl.Pow(1 - lDotH, 5);
        return distribution * fresnel * nDotL / Hlsl.Max(gl * gv, 0.0001f);
    }

    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 uv = p / size;
        float height = Height(p);
        float tl = Height(p + new Float2(-1, -1));
        float top = Height(p + new Float2(0, -1));
        float tr = Height(p + new Float2(1, -1));
        float left = Height(p + new Float2(-1, 0));
        float right = Height(p + new Float2(1, 0));
        float bl = Height(p + new Float2(-1, 1));
        float bottom = Height(p + new Float2(0, 1));
        float br = Height(p + new Float2(1, 1));
        Float2 gradient = new Float2(tl + 2 * left + bl - tr - 2 * right - br,
            tl + 2 * top + tr - bl - 2 * bottom - br);
        Float3 normal = Hlsl.Normalize(new Float3(gradient * 32, 1));
        Float3 density = D2D.SampleInputAtPosition(1, p).XYZ;
        float coverage = Hlsl.SmoothStep(0.006f, 0.065f, height);
        Float3 pigment = Hlsl.Exp(-density / Hlsl.Max(height, 0.003f));
        // A dark, reflective clear carrier keeps uncovered areas clean. Colour
        // is a transported concentration, independent of the final opacity.
        Float3 underlay = new Float3(0.018f, 0.028f, 0.036f);
        float vignette = Hlsl.Saturate(1 - Hlsl.Length((uv - 0.5f) * new Float2(aspect, 1)) * 0.28f);
        underlay *= vignette;
        Float3 light = Hlsl.Normalize(new Float3(-0.28f, -0.20f, 1));
        float diffuse = 0.64f + 0.36f * Hlsl.Saturate(Hlsl.Dot(normal, light));
        float specular = Specular(normal, light, 0.11f);
        Float3 color = Hlsl.Lerp(underlay, pigment * diffuse, coverage);
        float wetness = 0.14f + 0.86f * Hlsl.SmoothStep(0.005f, 0.04f, height);
        color += new Float3(1.0f, 0.96f, 0.87f) * Hlsl.Min(specular * 3.5f, 0.80f) * wetness;
        // Broad studio softbox reflected in the local height-derived normal.
        float strip = Hlsl.Exp(-Hlsl.Pow((normal.X + 0.20f) * 13, 2))
            * Hlsl.Exp(-Hlsl.Pow((normal.Y + 0.16f) * 5, 2));
        color += new Float3(0.29f, 0.32f, 0.35f) * strip * wetness;
        float metal = D2D.SampleInputAtPosition(2, p).X;
        float sparkle = Hlsl.Saturate(metal * 4) * wetness;
        color = Hlsl.Lerp(color, new Float3(0.88f, 0.67f, 0.24f) * (0.7f + specular * 3), sparkle);
        return new Float4(Hlsl.Saturate(color), 1);
    }
}
