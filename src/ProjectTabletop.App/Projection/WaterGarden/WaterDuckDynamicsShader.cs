using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal static class WaterGardenDucks
{
    public const int Count = 5;
    public const int Rows = 4;
}

// Each column describes one floating hull. Rows contain position/heading,
// velocity, buoyancy/scale, and surface tilt. All alpha channels stay opaque.
// Keeping this small signed texture on the GPU lets the ducks follow the actual
// water without per-frame readbacks.
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterDuckInitializeShader(float aspect) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        int index = (int)Hlsl.Floor(D2D.GetScenePosition().X);
        int row = (int)Hlsl.Floor(D2D.GetScenePosition().Y);
        Float2 position = new Float2(.28f, .32f);
        float yaw = -1.3f, scale = .022f;
        if (index == 1) { position = new Float2(.72f, .35f); yaw = 2.4f; scale = .024f; }
        else if (index == 2) { position = new Float2(.42f, .51f); yaw = .8f; scale = .023f; }
        else if (index == 3) { position = new Float2(.67f, .64f); yaw = -.5f; scale = .021f; }
        else if (index == 4) { position = new Float2(.32f, .74f); yaw = 2.1f; scale = .025f; }
        if (row == 0) return new Float4(position, yaw, 1);
        if (row == 2) return new Float4(0, 0, scale * Hlsl.Min(aspect, 1), 1);
        return new Float4(0, 0, 0, 1);
    }
}

[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterDuckDynamicsShader(Float2 fieldSize, float aspect,
    float dt, float time, int ambientEnabled) : ID2D1PixelShader
{
    private Float4 State(int index, int row) => D2D.SampleInputAtPosition(0,
        new Float2(index + .5f, row + .5f));

    private float Height(Float2 uv)
    {
        Float2 sampleUv = Hlsl.Clamp(uv, .5f / fieldSize, (fieldSize - .5f) / fieldSize);
        float height = D2D.SampleInputAtPosition(1, sampleUv * fieldSize).X;
        Float2 world = uv * new Float2(aspect, 1);
        // Follow the same small optical ripples as the visible water. These do
        // not inject energy into the wave solver, and can be disabled in checks.
        float amount = ambientEnabled * Hlsl.SmoothStep(2, 5, time);
        return height + amount * (.00040f * Hlsl.Sin(world.X * 23 + world.Y * 13 - time * .9f) +
            .00030f * Hlsl.Sin(world.X * -17 + world.Y * 29 + time * .72f) +
            .00016f * Hlsl.Sin(world.X * 43 - world.Y * 21 - time * 1.12f));
    }

    public Float4 Execute()
    {
        int index = (int)Hlsl.Floor(D2D.GetScenePosition().X);
        int row = (int)Hlsl.Floor(D2D.GetScenePosition().Y);
        Float4 horizontal = State(index, 0);
        Float4 motion = State(index, 1);
        Float4 vertical = State(index, 2);
        Float2 oldSlope = State(index, 3).XY;
        Float2 position = horizontal.XY, velocity = motion.XY;
        Float2 metric = new Float2(aspect, 1);
        float footprint = .018f * Hlsl.Min(aspect, 1);
        Float2 dx = new Float2(footprint / aspect, 0), dy = new Float2(0, footprint);
        float centre = Height(position);
        float left = Height(position - dx), right = Height(position + dx);
        float back = Height(position - dy), front = Height(position + dy);
        float targetHeight = (centre * 2 + left + right + back + front) / 6;
        Float2 gradient = new Float2(right - left, front - back) / (2 * footprint);
        gradient = Hlsl.Clamp(gradient, new Float2(-.65f, -.65f), new Float2(.65f, .65f));

        // A damped buoyancy spring avoids gluing the hull to individual texels.
        // The footprint averages short waves while retaining larger ripples.
        float verticalVelocity = Hlsl.Clamp(vertical.Y +
            ((targetHeight - vertical.X) * 324 - vertical.Y * 18) * dt, -.30f, .30f);
        float heave = Hlsl.Clamp(vertical.X + verticalVelocity * dt, -.05f, .05f);
        Float2 slope = Hlsl.Lerp(oldSlope, gradient, 1 - Hlsl.Exp(-9 * dt));
        Float2 acceleration = -gradient;
        Float2 world = position * metric;
        float ambient = ambientEnabled * Hlsl.SmoothStep(2, 5, time);
        acceleration += new Float2(Hlsl.Sin(world.Y * 8 + time * .17f),
            Hlsl.Cos(world.X * 5 - time * .13f)) * (.0016f * ambient);

        // Soft hull separation keeps a moving group from collecting into one
        // yellow blob. Forces and travel are bounded, including narrow boards.
        for (int other = 0; other < WaterGardenDucks.Count; other++)
        {
            if (other != index)
            {
                Float2 delta = (position - State(other, 0).XY) * metric;
                float distance = Hlsl.Length(delta);
                float separation = (vertical.Z + State(other, 2).Z) * 2.4f;
                if (distance < separation)
                {
                    Float2 away = distance > .00001f ? delta / distance :
                        new Float2(index < other ? -1 : 1, 0);
                    acceleration += away * (.09f * (1 - distance / separation));
                }
            }
        }

        Float2 low = new Float2(.16f, .22f), high = new Float2(.84f, .82f);
        Float2 edgeDistance = Hlsl.Min(position - low, high - position) * metric;
        Float2 edgeDirection = new Float2(position.X < .5f ? 1 : -1, position.Y < .52f ? 1 : -1);
        acceleration += edgeDirection * (1 - Hlsl.Saturate(edgeDistance / (.05f * Hlsl.Min(aspect, 1)))) * .016f;
        acceleration = Hlsl.Clamp(acceleration, new Float2(-.12f, -.12f), new Float2(.12f, .12f));
        velocity = (velocity + acceleration * dt) * Hlsl.Exp(-1.35f * dt);
        float speed = Hlsl.Length(velocity);
        float maximumSpeed = .05f * Hlsl.Min(aspect, 1);
        velocity *= Hlsl.Min(1, maximumSpeed / Hlsl.Max(speed, .000001f));
        position += velocity * dt / metric;
        if (position.X < low.X || position.X > high.X) velocity.X *= -.25f;
        if (position.Y < low.Y || position.Y > high.Y) velocity.Y *= -.25f;
        position = Hlsl.Clamp(position, low, high);

        // A floating toy slowly turns with its drift instead of instantly
        // adopting the wave normal or snapping its heading at a wrap boundary.
        float yaw = horizontal.Z, yawVelocity = motion.Z;
        float targetYaw = Hlsl.Length(velocity) > .000001f ? Hlsl.Atan2(velocity.Y, velocity.X) : yaw;
        float angle = Hlsl.Atan2(Hlsl.Sin(targetYaw - yaw), Hlsl.Cos(targetYaw - yaw));
        float turn = Hlsl.SmoothStep(.00025f, .002f, Hlsl.Length(velocity));
        yawVelocity = Hlsl.Clamp((yawVelocity + angle * turn * .9f * dt) * Hlsl.Exp(-3.3f * dt), -.25f, .25f);
        yaw += yawVelocity * dt;
        if (yaw < -3.14159265f || yaw > 3.14159265f)
            yaw = Hlsl.Atan2(Hlsl.Sin(yaw), Hlsl.Cos(yaw));

        if (row == 0) return new Float4(position, yaw, 1);
        if (row == 1) return new Float4(velocity, yawVelocity, 1);
        if (row == 2) return new Float4(heave, verticalVelocity, vertical.Z, 1);
        return new Float4(slope, 0, 1);
    }
}
