using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// A long, sticky molten strand in registered painting coordinates. Its weighted
// bead pinches off while the remaining tether retracts back toward the lip.
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotDragonLavaShader(float age, float seed) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY;
        Float2 lip = new Float2(24, 17);
        Float4 result = new Float4(0, 0, 0, 0);
        float fade = Hlsl.SmoothStep(0, .45f, age) * (1 - Hlsl.SmoothStep(4.2f, 6, age));
        // A little residue inside the mouth fades and cools after the drops.
        float residue = Segment(p, new Float2(19, 10), lip) - .55f;
        result = Material(p, residue, 1, Hlsl.Saturate((age - 3.5f) / 2.5f), fade * .65f);

        for (int index = 0; index < 3; index++)
        {
            float variation = Hash(new Float2(seed * 13 + index, 7.1f));
            float release = index == 0 ? 2.1f + seed * .2f
                : index == 1 ? 2.85f + variation * .18f : 3.62f + variation * .23f;
            float previous = index == 0 ? 0 : index == 1 ? 2.1f + seed * .2f
                : 2.85f + Hash(new Float2(seed * 13 + 1, 7.1f)) * .18f;
            float radius = index == 0 ? 3.4f : index == 1 ? 2.45f : 1.6f;
            radius *= .94f + variation * .12f;
            float releaseLength = index == 0 ? 64 : index == 1 ? 40 : 26;
            float fall = age - release;
            if (age >= previous && fall < 1.05f)
            {
                float grow = Hlsl.SmoothStep(previous, release, age);
                Float2 center;
                float distance;
                float opacity;
                float cooling;
                if (fall < 0)
                {
                    float pinch = Hlsl.SmoothStep(release - .18f, release, age);
                    float length = 3 + grow * (releaseLength - 3);
                    center = StrandPoint(1, length, age);
                    float r = radius * (.35f + .65f * grow);
                    Float2 q = p - center;
                    float blob = (Hlsl.Length(q / new Float2(r, r * (1.18f + .25f * grow))) - 1) * r;
                    float along = Hlsl.Saturate((p.Y - lip.Y) / length);
                    Float2 neckPoint = StrandPoint(along, length, age);
                    // A swollen lip attachment and an uneven middle retain
                    // the weight of saliva. Only the waist above the bead
                    // thins under tension, rather than the entire strand.
                    float neckWidth = (.7f + .65f * (1 - along) * (1 - along)
                        + .25f * Hlsl.Sin(along * 3.14159265f)) * (.6f + .4f * grow);
                    float waist = 1 - Hlsl.SmoothStep(.08f, .22f, Hlsl.Abs(along - .84f));
                    neckWidth *= 1 - pinch * waist * .94f;
                    float neck = Hlsl.Length(p - neckPoint) - neckWidth;
                    distance = Hlsl.Min(blob, neck);
                    opacity = Hlsl.SmoothStep(previous, previous + .18f, age);
                    cooling = 0;
                }
                else
                {
                    // Unequal, rounded drops accelerate under gravity. Their
                    // tiny sideways drift does not turn into a repeated stream.
                    // Continue from the same bent endpoint without a jump.
                    center = StrandPoint(1, releaseLength, release)
                        + new Float2((variation - .5f) * fall * 2.5f, fall * 14 + fall * fall * 110);
                    Float2 q = p - center;
                    float roundness = Hlsl.Lerp(1.43f, 1.12f + .05f * Hlsl.Sin(fall * 12 + index),
                        Hlsl.SmoothStep(0, .18f, fall));
                    distance = (Hlsl.Length(q / new Float2(radius, radius * roundness)) - 1) * radius;
                    opacity = 1 - Hlsl.SmoothStep(.62f, 1.05f, fall);
                    cooling = Hlsl.Saturate(fall / 1.2f);
                    if (fall < .34f)
                    {
                        // The long upper tether does not disappear in one
                        // frame when its bead releases. It recoils and cools.
                        float retract = Hlsl.SmoothStep(0, .34f, fall);
                        float length = Hlsl.Max(.2f, releaseLength * .84f * (1 - retract));
                        float along = Hlsl.Saturate((p.Y - lip.Y) / length);
                        float strandAlong = along * length / releaseLength;
                        Float2 neckPoint = StrandPoint(strandAlong, releaseLength, age);
                        float neckWidth = (.7f + .65f * (1 - strandAlong) * (1 - strandAlong)
                            + .25f * Hlsl.Sin(strandAlong * 3.14159265f)) * (1 - retract * .65f);
                        float waist = 1 - Hlsl.SmoothStep(.08f, .22f, Hlsl.Abs(strandAlong - .84f));
                        neckWidth *= 1 - waist * .94f;
                        float neck = Hlsl.Length(p - neckPoint) - neckWidth;
                        Float4 remnant = Material(p - lip, neck, radius, retract * .5f,
                            1 - Hlsl.SmoothStep(.12f, .34f, fall));
                        result = remnant + result * (1 - remnant.W);
                    }
                }
                Float4 drop = Material(p - center, distance, radius, cooling, opacity);
                result = drop + result * (1 - drop.W);
            }
        }
        return result;
    }

    private Float2 StrandPoint(float along, float length, float moment)
    {
        float stretch = Hlsl.Saturate(length / 64);
        float bow = Hlsl.Sin(along * 3.14159265f);
        float sway = Hlsl.Sin(moment * 1.2f + seed * 6) * along * along * 1.5f;
        float bend = bow * (1.1f + stretch * 3.5f) * Hlsl.Sin(moment * .9f + seed * 6.7f);
        float curl = bow * Hlsl.Sin(along * 7 + moment * 1.5f + seed * 5) * (.35f + stretch * .7f);
        return new Float2(24 + sway + bend + curl, 17 + along * length);
    }

    private Float4 Material(Float2 p, float distance, float radius, float cooling, float opacity)
    {
        float grain = Noise(p * 1.7f + new Float2(seed * 19, age * -.55f));
        distance += (grain - .5f) * .18f;
        float body = 1 - Hlsl.SmoothStep(-.15f, .45f, distance);
        float core = 1 - Hlsl.SmoothStep(-radius * .85f, -radius * .1f, distance);
        float heat = Hlsl.Saturate(.16f + core * .66f + (grain - .5f) * .24f - cooling * .37f);
        Float3 color = Hlsl.Lerp(new Float3(.38f, .019f, .004f), new Float3(1, .19f, .008f),
            Hlsl.Saturate(heat * 2));
        color = Hlsl.Lerp(color, new Float3(1, .73f, .13f), Hlsl.Saturate((heat - .38f) * 2.5f));
        // Sparse dark cooling crust and a small hot patch, no white reflection
        // stripe or translucent blue specular material associated with water.
        float crust = Hlsl.SmoothStep(.68f, .86f, grain) * (.35f + cooling * .35f);
        color *= 1 - crust;
        float hot = Hlsl.Exp(-Hlsl.Dot(p - new Float2(-.45f, -.5f), p - new Float2(-.45f, -.5f)) * 2.8f);
        color = Hlsl.Lerp(color, new Float3(1, .89f, .37f), hot * (1 - cooling) * .45f);
        float glow = Hlsl.Exp(-Hlsl.Max(0, distance) * 1.5f) * .12f * (1 - body) * (1 - cooling);
        float alpha = (body + glow) * opacity;
        color = Hlsl.Lerp(color, new Float3(.95f, .19f, .015f), Hlsl.Saturate(glow * 8));
        return new Float4(color * alpha, alpha);
    }

    private static float Segment(Float2 p, Float2 a, Float2 b)
    {
        Float2 v = b - a;
        return Hlsl.Length(p - a - v * Hlsl.Saturate(Hlsl.Dot(p - a, v) / Hlsl.Dot(v, v)));
    }

    private static float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new Float2(127.1f, 311.7f))) * 43758.5453f);

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }
}
