using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Transfer the moving tip's momentum once per accepted camera stroke. The
// swept contact catches a hull between observations, independently of waves
// and the short-lived current left behind the stick.
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterDuckStickContactShader(float aspect,
    int activeDuckCount, Float2 start, Float2 end, float strength) : ID2D1PixelShader
{
    private Float4 State(int index, int row) => D2D.SampleInputAtPosition(0,
        new Float2(index + .5f, row + .5f));

    public Float4 Execute()
    {
        int index = (int)Hlsl.Floor(D2D.GetScenePosition().X);
        int row = (int)Hlsl.Floor(D2D.GetScenePosition().Y);
        Float4 existing = State(index, row);
        Float4 position = State(index, 0);
        Float4 motion = State(index, 1);
        float scale = State(index, 2).Z;
        float dropHeight = State(index, 4).X;
        Float2 metric = new Float2(aspect, 1);
        Float2 stroke = (end - start) * metric;
        float length = Hlsl.Length(stroke);
        Float2 direction = stroke / Hlsl.Max(length, .0001f);
        Float2 offset = (position.XY - start) * metric;
        float along = Hlsl.Dot(offset, direction);
        Float2 closest = offset - direction * Hlsl.Clamp(along, 0, length);
        float radius = scale * 1.14f + .010f * Hlsl.Min(aspect, 1);

        Float4 result = existing;
        if (row == 1 && index < activeDuckCount && scale > 0 &&
            dropHeight <= scale * 1.5f && length >= .0035f &&
            Hlsl.Dot(closest, closest) < radius * radius)
        {
            // Use the first point of contact, not the segment's midpoint.
            // Its normal naturally pushes a glancing hull sideways. A tip
            // starting inside a hull only pushes if it is moving into it;
            // pulling away must never attract the duck back toward the tip.
            Float2 perpendicular = offset - direction * along;
            float entry = Hlsl.Max(0, along - Hlsl.Sqrt(Hlsl.Max(0,
                radius * radius - Hlsl.Dot(perpendicular, perpendicular))));
            Float2 separation = offset - direction * entry;
            float separationLength = Hlsl.Length(separation);
            Float2 normal = separationLength > .0001f
                ? separation / separationLength : direction;
            float tipSpeed = (.12f + .14f * strength) * Hlsl.Min(aspect, 1);
            float incoming = Hlsl.Max(0, Hlsl.Dot(direction * tipSpeed, normal));
            if (incoming > 0)
            {
                Float2 velocity = motion.XY + normal * Hlsl.Max(0,
                    incoming - Hlsl.Dot(motion.XY, normal));
                // Repeated observations approach a bounded contact velocity
                // instead of adding the same full impulse on every frame.
                float maximumSpeed = WaterGardenDucks.MaximumSpeed * Hlsl.Min(aspect, 1);
                velocity *= Hlsl.Min(1, maximumSpeed / Hlsl.Max(Hlsl.Length(velocity), .000001f));
                result = new Float4(velocity, motion.Z, 1);
            }
        }
        return result;
    }
}
