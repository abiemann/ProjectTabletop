using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

/// <summary>Original procedural river stones. Baked once, then viewed through the
/// refracting surface; no source-demo photograph or unlicensed texture is embedded.</summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct PebbleBedShader(Float2 size, float aspect) : ID2D1PixelShader
{
    private float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new Float2(127.1f, 311.7f))) * 43758.5453f);

    private float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        f = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), f.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), f.X), f.Y);
    }

    private Float4 Stones(Float2 world, float frequency, float seed)
    {
        Float2 p = world * frequency + new Float2(Noise(world * 9), Noise(world * 9 + 43)) * 1.1f + seed;
        Float2 cell = Hlsl.Floor(p);
        float nearest = 100, identity = 0, angle = 0;
        Float2 local = new Float2(0, 0);
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            Float2 tile = cell + new Float2(x, y);
            float id = Hash(tile);
            Float2 center = tile + .5f + new Float2(Hash(tile + 17), Hash(tile + 63)) * .88f - .44f;
            float turn = id * 6.2831853f;
            float sine = Hlsl.Sin(turn), cosine = Hlsl.Cos(turn);
            Float2 delta = p - center;
            Float2 q = new Float2(delta.X * cosine + delta.Y * sine, -delta.X * sine + delta.Y * cosine);
            q /= new Float2(.28f + Hash(tile + 9) * .43f, .24f + Hash(tile + 28) * .36f);
            float distance = Hlsl.Dot(q, q);
            // A slight stone-specific asymmetry keeps the silhouettes away from a regular bead grid.
            distance *= 1 + .21f * Hlsl.Sin(q.X * 3 + id * 7) * Hlsl.Sin(q.Y * 2.4f + id * 13);
            if (distance < nearest)
            {
                nearest = distance;
                local = q;
                identity = id;
                angle = turn;
            }
        }

        float grain = Noise(world * 640) * .55f + Noise(world * 1430 + 12) * .45f;
        float coverage = 1 - Hlsl.SmoothStep(.90f, 1.05f, nearest);
        float dome = Hlsl.Sqrt(Hlsl.Max(.04f, 1 - nearest));
        float cosineAngle = Hlsl.Cos(angle), sineAngle = Hlsl.Sin(angle);
        Float2 slope = new Float2(local.X * cosineAngle - local.Y * sineAngle,
            local.X * sineAngle + local.Y * cosineAngle);
        Float3 normal = Hlsl.Normalize(new Float3(slope * .70f, dome));
        Float3 light = Hlsl.Normalize(new Float3(-.40f, -.48f, .82f));
        float lighting = .64f + .42f * Hlsl.Saturate(Hlsl.Dot(normal, light));
        float shoulder = .70f + .30f * Hlsl.SmoothStep(0, .45f, dome);
        // Soft river-stone taupes, warm quartz and a little olive slate echo the
        // limestone basin and moss instead of forming a monochrome gravel bed.
        Float3 mineral = Hlsl.Lerp(new Float3(.34f, .35f, .29f), new Float3(.71f, .66f, .54f),
            Hlsl.SmoothStep(.05f, .75f, identity));
        if (identity > .80f) mineral = Hlsl.Lerp(new Float3(.66f, .63f, .53f),
            new Float3(.88f, .84f, .71f), (identity - .80f) / .20f);
        if (identity < .13f) mineral *= .63f;
        float mottling = Noise(local * 3.1f + identity * 71) * .6f + Noise(local * 10 + identity * 23) * .4f;
        mineral *= .84f + mottling * .30f;
        mineral *= .93f + grain * .14f;
        float vein = 1 - Hlsl.SmoothStep(.04f, .13f,
            Hlsl.Abs(Hlsl.Sin(local.X * 3.2f + local.Y * 1.5f + Noise(local * 2 + identity * 9) * 3.4f)));
        if (identity > .58f && identity < .78f) mineral = Hlsl.Lerp(mineral, new Float3(.80f, .80f, .75f), vein * .38f);
        Float3 halfVector = Hlsl.Normalize(light + new Float3(0, 0, 1));
        float wetLuster = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfVector)), 38) * .065f;
        Float3 stone = mineral * lighting * shoulder + new Float3(wetLuster, wetLuster, wetLuster);
        return new Float4(Hlsl.Saturate(stone), coverage);
    }

    public Float4 Execute()
    {
        Float2 world = D2D.GetScenePosition().XY / size * new Float2(aspect, 1);
        float grit = Noise(world * 1300);
        Float3 gaps = Hlsl.Lerp(new Float3(.08f, .10f, .10f), new Float3(.24f, .25f, .23f), grit);
        Float4 fine = Stones(world, 101, 71);
        Float4 pebbles = Stones(world, 43, 0);
        Float3 bed = Hlsl.Lerp(gaps, fine.XYZ * .86f, fine.W);
        bed = Hlsl.Lerp(bed, pebbles.XYZ, pebbles.W);
        return new Float4(bed, 1);
    }
}
