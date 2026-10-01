using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// Moving molten detail inside the painted banks. Rocks and the painted mean
// heat remain fixed; the upper vent breathes light through its own warm texture.
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotCavernLavaShader(
    Float2 imageScale, float time, float cropTop, float cropHeight) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 p = D2D.GetScenePosition().XY / imageScale;
        Float3 original = D2D.SampleInputAtPosition(0, p * imageScale).XYZ;
        float warm = Hlsl.SmoothStep(.06f, .32f, original.X - original.Z)
            * Hlsl.SmoothStep(.09f, .38f, original.X);
        Float4 result = new Float4(0, 0, 0, 0);
        if (p.X < 600) result = LeftLava(p, original, warm);
        else result = RightLava(p, original, warm);
        return result;
    }

    private Float4 RightLava(Float2 p, Float3 original, float warm)
    {
        float center = 1315 + Hlsl.SmoothStep(470, 554, p.Y) * 6;
        float footBulge = (p.Y - 553) / 10;
        float width = 1.2f + Hlsl.SmoothStep(445, 490, p.Y) * 3.1f
            + Hlsl.Exp(-footBulge * footBulge) * 1.5f;
        float bank = Hlsl.Abs(p.X - center) - width;
        float ribbon = (1 - Hlsl.SmoothStep(-.8f, .8f, bank))
            * Hlsl.SmoothStep(439, 449, p.Y) * (1 - Hlsl.SmoothStep(557, 566, p.Y));
        Float2 foot = (p - new Float2(1319, 559)) / new Float2(25, 5.5f);
        float pool = 1 - Hlsl.SmoothStep(.65f, 1, Hlsl.Length(foot));
        Float2 top = (p - new Float2(1298, 235)) / new Float2(17, 31);
        float vent = 1 - Hlsl.SmoothStep(.64f, 1, Hlsl.Length(top));
        float alpha = Hlsl.Max(Hlsl.Max(ribbon, pool), vent) * warm;
        if (alpha <= .001f) return new Float4(0, 0, 0, 0);

        // Irregular elongated folds/crust travel DOWN at 7.2 source pixels/sec.
        // Two scales share that advection, so detail cannot turn into flicker
        // or a stack of parallel scrolling bands. No displacement of the banks.
        float travel = p.Y - time * 7.2f;
        float folds = Noise(new Float2((p.X - center) * .38f, travel * .065f));
        float grain = Noise(new Float2((p.X - center) * 1.37f + 9.4f, travel * .31f));
        float molten = folds * .85f + grain * .15f;
        float crust = 1 - Hlsl.SmoothStep(.28f, .49f, molten);
        float hot = Hlsl.SmoothStep(.49f, .70f, molten);
        float flowGain = .94f - crust * .38f + hot * .30f;
        // Broken heat patches spread sideways through the impact pool. This
        // remains viscous lava: no circular water ripples or white spray.
        float spreading = Noise(new Float2((Hlsl.Abs(p.X - 1319) - time * 2) * .28f,
            (p.Y - 559) * .48f + 15.7f));
        float poolGain = .89f + spreading * .22f;
        float glowGain = 1 + Hlsl.Sin(time * 1.31f) * .12f
            + Hlsl.Sin(time * 2.17f + 1.4f) * .05f;
        // The last few pixels feed into a steadily hot impact, rather than
        // letting a dark crust patch extinguish the bright painted foot.
        float gain = Hlsl.Lerp(1, flowGain, ribbon * (1 - Hlsl.SmoothStep(550, 558, p.Y)));
        gain = Hlsl.Lerp(gain, poolGain, pool * .72f);
        gain = Hlsl.Lerp(gain, glowGain, vent);
        Float3 color = Hlsl.Saturate(original * gain);

        // Same smoked-glass wash as the cached backdrop: the patch blends
        // without a rectangle, halo over the claw, or newly painted rock.
        float y = Hlsl.Saturate((p.Y - cropTop) / cropHeight);
        float shade = y < .6f ? Hlsl.Lerp(15 / 255f, 45 / 255f, y / .6f)
            : y < .82f ? Hlsl.Lerp(45 / 255f, 224 / 255f, (y - .6f) / .22f)
            : Hlsl.Lerp(224 / 255f, 248 / 255f, (y - .82f) / .18f);
        color = Hlsl.Lerp(color, new Float3(7 / 255f, 8 / 255f, 14 / 255f), shade);
        return new Float4(color * alpha, alpha);
    }

    private Float4 LeftLava(Float2 p, Float3 original, float warm)
    {
        // Profiles follow the bends and widening of each painted fall. Their
        // separate masks retain the foreground shelves and dragon coil gaps.
        Float2 rear = RearProfile(p.Y), front = FrontProfile(p.Y);
        float distant = Ribbon(p, rear, 337, 480);
        float sheet = Ribbon(p, front, 560, 691);
        float curtain = Ribbon(p, new Float2(208, 24), 675, 749);
        Float2 crack = new Float2(299 + Hlsl.SmoothStep(790, 828, p.Y) * 2,
            1.3f + Hlsl.SmoothStep(742, 825, p.Y) * 3);
        float fissure = Ribbon(p, crack, 713, 829);
        float rearFoot = Pocket(p, new Float2(184, 476.5f), new Float2(13, 4));
        float frontFoot = Pocket(p, new Float2(171, 684), new Float2(10, 4));
        float lowerFoot = Pocket(p, new Float2(210, 743), new Float2(21, 4));
        float step = Pocket(p, new Float2(208, 488), new Float2(18, 6));
        float farStep = Pocket(p, new Float2(239, 503), new Float2(25, 7));
        float coverage = Hlsl.Max(Hlsl.Max(distant, sheet), Hlsl.Max(curtain, fissure));
        coverage = Hlsl.Max(coverage, Hlsl.Max(Hlsl.Max(rearFoot, frontFoot), lowerFoot));
        coverage = Hlsl.Max(coverage, Hlsl.Max(step, farStep));
        float alpha = coverage * warm;
        if (alpha <= .001f) return new Float4(0, 0, 0, 0);

        float gain = Hlsl.Lerp(1, FlowGain(p, rear.X, 6.1f, new Float2(.42f, .070f), 8.7f),
            distant * .8f * (1 - Hlsl.SmoothStep(469, 477, p.Y)));
        gain = Hlsl.Lerp(gain, FlowGain(p, front.X, 5.3f, new Float2(.24f, .052f), 31.2f),
            sheet * (1 - Hlsl.SmoothStep(678, 686, p.Y)));
        gain = Hlsl.Lerp(gain, FlowGain(p, 216, 4.7f, new Float2(.34f, .075f), 56.4f),
            curtain * (1 - Hlsl.SmoothStep(735, 743, p.Y)));
        gain = Hlsl.Lerp(gain, FlowGain(p, crack.X, 4.3f, new Float2(.46f, .085f), 78.1f),
            fissure * .75f * (1 - Hlsl.SmoothStep(819, 827, p.Y)));
        gain = Hlsl.Lerp(gain, PoolGain(p, 184, 476.5f, 9.3f), rearFoot * .7f);
        gain = Hlsl.Lerp(gain, PoolGain(p, 171, 684, 38.5f), frontFoot * .7f);
        gain = Hlsl.Lerp(gain, PoolGain(p, 210, 743, 65.1f), lowerFoot * .7f);
        gain = Hlsl.Lerp(gain, 1 + Hlsl.Sin(time * 1.17f + 2.4f) * .075f, step);
        gain = Hlsl.Lerp(gain, 1 + Hlsl.Sin(time * .93f + 4.1f) * .065f, farStep);
        Float3 color = Hlsl.Saturate(original * gain);
        float y = Hlsl.Saturate((p.Y - cropTop) / cropHeight);
        float shade = y < .6f ? Hlsl.Lerp(15 / 255f, 45 / 255f, y / .6f)
            : y < .82f ? Hlsl.Lerp(45 / 255f, 224 / 255f, (y - .6f) / .22f)
            : Hlsl.Lerp(224 / 255f, 248 / 255f, (y - .82f) / .18f);
        color = Hlsl.Lerp(color, new Float3(7 / 255f, 8 / 255f, 14 / 255f), shade);
        return new Float4(color * alpha, alpha);
    }

    private float FlowGain(Float2 p, float center, float speed, Float2 frequency, float seed)
    {
        float travel = p.Y - time * speed;
        float folds = Noise(new Float2((p.X - center) * frequency.X + seed, travel * frequency.Y));
        float grain = Noise(new Float2((p.X - center) * 1.37f + seed * 1.7f, travel * .31f + seed));
        float molten = folds * .85f + grain * .15f;
        float crust = 1 - Hlsl.SmoothStep(.28f, .49f, molten);
        float hot = Hlsl.SmoothStep(.49f, .70f, molten);
        return .94f - crust * .38f + hot * .30f;
    }

    private float PoolGain(Float2 p, float center, float bottom, float seed) => .89f + .22f
        * Noise(new Float2((Hlsl.Abs(p.X - center) - time * 1.6f) * .28f + seed,
            (p.Y - bottom) * .48f + seed));

    private static float Ribbon(Float2 p, Float2 profile, float top, float bottom) =>
        (1 - Hlsl.SmoothStep(-.8f, .8f, Hlsl.Abs(p.X - profile.X) - profile.Y))
        * Hlsl.SmoothStep(top, top + 5, p.Y) * (1 - Hlsl.SmoothStep(bottom - 2, bottom + 1, p.Y));

    private static float Pocket(Float2 p, Float2 center, Float2 radii) =>
        1 - Hlsl.SmoothStep(.6f, 1, Hlsl.Length((p - center) / radii));

    private static Float2 RearProfile(float y) => y < 356
        ? Hlsl.Lerp(new Float2(183, 3), new Float2(189, 4), Hlsl.SmoothStep(341, 356, y))
        : y < 366 ? Hlsl.Lerp(new Float2(189, 4), new Float2(194, 3), Hlsl.SmoothStep(356, 366, y))
        : y < 386 ? Hlsl.Lerp(new Float2(194, 3), new Float2(186, 3), Hlsl.SmoothStep(366, 386, y))
        : y < 405 ? Hlsl.Lerp(new Float2(186, 3), new Float2(177, 3), Hlsl.SmoothStep(386, 405, y))
        : y < 425 ? Hlsl.Lerp(new Float2(177, 3), new Float2(181, 4), Hlsl.SmoothStep(405, 425, y))
        : y < 449 ? Hlsl.Lerp(new Float2(181, 4), new Float2(182, 4), Hlsl.SmoothStep(425, 449, y))
        : y < 472 ? Hlsl.Lerp(new Float2(182, 4), new Float2(184, 5), Hlsl.SmoothStep(449, 472, y))
        : Hlsl.Lerp(new Float2(184, 5), new Float2(185, 7), Hlsl.SmoothStep(472, 478, y));

    private static Float2 FrontProfile(float y) => y < 577
        ? Hlsl.Lerp(new Float2(166, 1.8f), new Float2(170, 3.2f), Hlsl.SmoothStep(563, 577, y))
        : y < 599 ? Hlsl.Lerp(new Float2(170, 3.2f), new Float2(167, 4), Hlsl.SmoothStep(577, 599, y))
        : y < 620 ? Hlsl.Lerp(new Float2(167, 4), new Float2(169, 4), Hlsl.SmoothStep(599, 620, y))
        : y < 643 ? Hlsl.Lerp(new Float2(169, 4), new Float2(170, 5), Hlsl.SmoothStep(620, 643, y))
        : y < 663 ? Hlsl.Lerp(new Float2(170, 5), new Float2(173, 5), Hlsl.SmoothStep(643, 663, y))
        : Hlsl.Lerp(new Float2(173, 5), new Float2(172, 7), Hlsl.SmoothStep(663, 683, y));

    private static float Hash(Float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new Float2(127.1f, 311.7f))) * 43758.5453f);

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }
}
