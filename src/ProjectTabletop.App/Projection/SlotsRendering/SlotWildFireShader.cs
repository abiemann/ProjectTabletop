using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.SlotsRendering;

// One physical fire field is revealed by growing fuel volumes. Its heat and
// upward advection never change when separate mouths become a joined wall.
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct SlotWildFireShader(
    float time, float seed, float opacity, float physicalRatio, int heads,
    float swelling, float joining, float mouthY, float reveal, float mouthStride,
    float edgeDecay, Float4 coreBounds) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 uv = D2D.GetScenePosition().XY / new Float2(384, 1024);
        // A genuine transparent guard band, including the outer texel centres.
        if (uv.X <= 0.003f || uv.X >= 0.997f || uv.Y <= 0.001f || uv.Y >= 0.999f)
            return new Float4(0, 0, 0, 0);

        float clock = time * 0.90f;
        float grow = Hlsl.Saturate(swelling);
        float join = Hlsl.Saturate(joining);
        float decay = Hlsl.Saturate(edgeDecay);
        float cellHeight = physicalRatio * mouthStride;
        Float2 core = (uv - new Float2(coreBounds.X, coreBounds.Y)) / new Float2(coreBounds.Z, coreBounds.W);
        Float2 physical = new Float2(core.X - 0.5f, core.Y * physicalRatio);

        // Small domain perturbations keep this a soft turbulent volume. There
        // are no ridged noise contours or broad sinuous marble-like bands.
        Float2 flow = physical * new Float2(5.8f, heads == 1 ? 3.4f : Hlsl.Lerp(4.9f, 3.4f, join))
            + new Float2(seed * 3.71f, clock * 3.9f + seed * 1.93f);
        Float2 drift = new Float2(
            Noise(flow * 0.57f + new Float2(clock * 0.19f, 4.2f)),
            Noise(flow * 0.61f + new Float2(8.3f, -clock * 0.16f))) - 0.5f;
        Float2 smoke = flow + drift * 0.56f;
        float clouds = Fractal(smoke, clock);
        float detail = Detail(smoke * 2.9f + new Float2(17.8f, clock * 0.24f), clock);
        float fringe = Noise(smoke * 3.7f + new Float2(4.1f, -clock * 0.33f));
        float edgeRoughness = (clouds - 0.5f) * 0.070f + (fringe - 0.5f) * 0.034f;

        float fuel = 0;
        for (int index = 0; index < 3; index++)
        {
            if (index < heads)
            {
                float mouth = (index + mouthY) * cellHeight;
                float down = physical.Y - mouth;
                // A single tall guardian breathes from about .22 of the run
                // toward its feet; stacked mouths use one cell of reach.
                float length = cellHeight * (heads == 1 ? 0.76f : 0.49f + grow * 0.17f);
                float along = Hlsl.Saturate(down / Hlsl.Max(length, 0.01f));
                // A connected narrow pressure jet feeds every growing puff.
                // Its geometric direction is down; the turbulent material is
                // always the same upward-moving field as the merged volume.
                float halfWidth = 0.045f + along * (0.145f + grow * 0.115f);
                float jetX = physical.X + drift.X * along * 0.027f;
                if (heads == 1 && join < 0.001f)
                {
                    // Keep the pressure at the jaw centred, then let rolling
                    // eddies bend and pinch the finishing breath downstream.
                    // Independent advected scales avoid a rigid sine-shaped
                    // hose; neither the joined wall nor its side wisps change.
                    float loose = Hlsl.SmoothStep(0.03f, 0.65f, along);
                    float roll = Noise(new Float2(along * 4.1f + clock * 2.35f + seed * 0.37f, seed + 5.8f)) - 0.5f;
                    float curl = Noise(new Float2(along * 11.7f + clock * 4.9f, seed + 18.9f)) - 0.5f;
                    jetX -= (roll * 0.30f + curl * 0.10f) * loose;
                    float lobes = Noise(new Float2(along * 8.8f + clock * 3.1f, seed + 31.4f)) - 0.5f;
                    halfWidth *= Hlsl.Clamp(1 + lobes * 0.56f * loose, 0.70f, 1.28f);
                }
                float jetEdge = Hlsl.Abs(jetX) - halfWidth + edgeRoughness * along;
                float jet = (1 - Hlsl.SmoothStep(-0.017f, 0.025f, jetEdge))
                    * Hlsl.SmoothStep(-0.020f, 0.008f, down)
                    * (1 - Hlsl.SmoothStep(length * 0.76f, length, down));

                // The puff begins just below the jaw and expands physically
                // round. Joining extends the same volume both up and down;
                // neighbouring fronts meet without replacing its texture.
                float puffCenter = mouth + cellHeight * (0.16f + grow * 0.045f);
                float puffRadius = 0.13f + grow * 0.395f;
                puffRadius = Hlsl.Lerp(puffRadius, 0.57f, join);
                float front = join * cellHeight * 0.94f;
                float vertical = Hlsl.Max(Hlsl.Abs(physical.Y - puffCenter) - front, 0);
                float volumeDistance = Hlsl.Length(new Float2(physical.X + drift.X * 0.025f, vertical));
                float puffEdge = volumeDistance - puffRadius + edgeRoughness;
                float puff = (1 - Hlsl.SmoothStep(-0.052f, 0.043f, puffEdge))
                    * Hlsl.Saturate(grow * 3.3f + join * 2);
                float localFuel = 1 - (1 - jet) * (1 - puff);
                // Union rather than adding opacity: overlapping puffs do not
                // create rectangular bright bands at the old cell seams.
                fuel = 1 - (1 - fuel) * (1 - localFuel);
            }
        }

        // Preserve the original fierce wall until the reveal opens it. The
        // remaining side fire then comes from thin independent contours, not
        // from the opaque wall being left standing behind those contours.
        float open = Hlsl.Saturate(reveal);
        float release = Hlsl.SmoothStep(0.16f, 0.96f, open);
        float side = Hlsl.Abs(physical.X);
        float railCenter = 0.474f + (clouds - 0.5f) * 0.060f + (fringe - 0.5f) * 0.024f;
        float railWidth = (0.054f + clouds * 0.075f + detail * 0.022f) * (1 - decay * 0.72f);
        float railDistance = Hlsl.Abs(side - railCenter) - railWidth + (fringe - 0.5f) * 0.025f;
        float broadRail = 1 - Hlsl.SmoothStep(-0.010f, 0.019f, railDistance);
        float crown = -0.028f - clouds * 0.045f + (fringe - 0.5f) * 0.018f;
        float risingCrown = Hlsl.SmoothStep(crown - 0.013f, crown + 0.028f, core.Y);
        float railTexture = Hlsl.SmoothStep(0.29f, 0.53f, clouds + detail * 0.20f);
        broadRail *= risingCrown * railTexture;

        // Shoulder-to-foot wisps wander laterally, vary in width and contain
        // softly moving gaps. Their field still rises with the original heat;
        // there are no straight, opaque posts reaching above the horns.
        float wander = Noise(new Float2(physical.Y * 5.3f + clock * 5.8f,
            physical.X * 4.7f + seed * 2.1f));
        float wispCenter = 0.457f + (wander - 0.5f) * 0.125f + (clouds - 0.5f) * 0.047f;
        float wispWidth = (0.032f + detail * 0.036f + clouds * 0.008f)
            * (0.60f + Hlsl.SmoothStep(0.20f, 0.66f, core.Y) * 0.40f) * (1 - decay * 0.72f);
        float wispDistance = Hlsl.Abs(side - wispCenter) - wispWidth + (fringe - 0.5f) * 0.038f;
        float wisp = 1 - Hlsl.SmoothStep(-0.007f, 0.017f, wispDistance);
        float shoulder = 0.14f + clouds * 0.10f + (fringe - 0.5f) * 0.030f;
        wisp *= Hlsl.SmoothStep(shoulder - 0.030f, shoulder + 0.040f, core.Y)
            * Hlsl.SmoothStep(0.22f, 0.73f, clouds * 0.35f + fringe * 0.65f);
        float rail = Hlsl.Lerp(broadRail, wisp, release) * Hlsl.SmoothStep(0.18f, 0.76f, join);

        // At full reveal the old wall contributes no outer-side opacity, so
        // the thin ragged rail actually controls the surviving silhouette.
        fuel *= 1 - release * Hlsl.SmoothStep(0.10f, 0.27f, side);
        fuel = 1 - (1 - fuel) * (1 - rail);

        // The crown itself also has a ragged living silhouette within the new
        // padding; no fire is simply cut off along the expanded rectangle.
        float crownEnvelope = Hlsl.SmoothStep(crown - 0.020f, crown + 0.022f, core.Y);
        fuel *= crownEnvelope;

        // Dense warm volume with small, changing yellow islands. Dark holes
        // and hard white cores cannot erase the volume or flatten its detail.
        float heat = Hlsl.Saturate((clouds - 0.12f) * 1.67f + (detail - 0.5f) * 0.36f);
        Float3 color = Hlsl.Lerp(new Float3(0.94f, 0.105f, 0.004f),
            new Float3(1, 0.34f, 0.012f), Hlsl.SmoothStep(0.16f, 0.47f, heat));
        color = Hlsl.Lerp(color, new Float3(1, 0.73f, 0.080f), Hlsl.SmoothStep(0.43f, 0.75f, heat));
        color = Hlsl.Lerp(color, new Float3(1, 0.94f, 0.30f), Hlsl.SmoothStep(0.72f, 0.94f, heat));
        float hottest = Hlsl.SmoothStep(0.90f, 1, heat) * Hlsl.SmoothStep(0.55f, 0.79f, detail);
        color = Hlsl.Lerp(color, new Float3(1, 0.985f, 0.69f), hottest * 0.65f);
        float density = Hlsl.Lerp(0.91f + detail * 0.09f, 1, join);

        // Open the middle into a bowl, leaving tall fire rising at both sides.
        // There is no shared horizontal line sweeping down the entire column.
        float middle = 1 - Hlsl.SmoothStep(0.15f, 0.47f, side);
        float bowlFront = open * 1.30f * middle - 0.14f;
        float clearing = Hlsl.Saturate(open * 6);
        bowlFront += ((clouds - 0.5f) * 0.16f + (fringe - 0.5f) * 0.05f) * clearing * middle;
        float retained = Hlsl.SmoothStep(bowlFront - 0.042f, bowlFront + 0.072f, core.Y);

        // Later the surviving rails narrow, detach and burn upward from their
        // roots. Fine advected gaps dissolve them without a downward curtain.
        float burnFront = 1.18f - decay * 1.42f + (clouds - 0.5f) * 0.20f;
        float risingRemnant = 1 - Hlsl.SmoothStep(burnFront - 0.08f, burnFront + 0.09f, core.Y);
        float dissolving = Hlsl.SmoothStep(decay * 0.81f - 0.12f, decay * 0.81f + 0.09f,
            detail * 0.70f + clouds * 0.30f);
        float emberLife = Hlsl.Lerp(1, risingRemnant * dissolving * (1 - decay), Hlsl.Saturate(decay * 8));
        float boundary = Hlsl.SmoothStep(0.003f, 0.017f, uv.X) * (1 - Hlsl.SmoothStep(0.983f, 0.997f, uv.X))
            * Hlsl.SmoothStep(0.001f, 0.008f, uv.Y) * (1 - Hlsl.SmoothStep(0.982f, 0.999f, uv.Y));
        float alpha = Hlsl.Saturate(fuel) * density * retained * emberLife * boundary * Hlsl.Saturate(opacity);
        return new Float4(color * alpha, alpha);
    }

    private static float Hash(Float2 p)
    {
        Float3 q = Hlsl.Frac(new Float3(p.X, p.Y, p.X) * 0.1031f);
        q += Hlsl.Dot(q, new Float3(q.Y, q.Z, q.X) + 33.33f);
        return Hlsl.Frac((q.X + q.Y) * q.Z);
    }

    private static float Noise(Float2 p)
    {
        Float2 cell = Hlsl.Floor(p), f = Hlsl.Frac(p);
        Float2 smooth = f * f * (3 - 2 * f);
        return Hlsl.Lerp(Hlsl.Lerp(Hash(cell), Hash(cell + new Float2(1, 0)), smooth.X),
            Hlsl.Lerp(Hash(cell + new Float2(0, 1)), Hash(cell + new Float2(1, 1)), smooth.X), smooth.Y);
    }

    private static float Fractal(Float2 p, float t) => Noise(p) * 0.42f
        + Noise(p * 2.03f + new Float2(7.8f + t * 0.13f, 3.1f - t * 0.12f)) * 0.26f
        + Noise(p * 4.09f + new Float2(19.3f - t * 0.16f, 11.7f)) * 0.17f
        + Noise(p * 8.17f + new Float2(2.1f, 23.9f + t * 0.21f)) * 0.095f
        + Noise(p * 16.31f + new Float2(31.7f + t * 0.24f, 5.4f)) * 0.055f;

    private static float Detail(Float2 p, float t) => Noise(p) * 0.54f
        + Noise(p * 2.07f + new Float2(4.7f, 13.9f + t * 0.14f)) * 0.30f
        + Noise(p * 4.13f + new Float2(18.4f - t * 0.21f, 3.7f)) * 0.16f;
}
