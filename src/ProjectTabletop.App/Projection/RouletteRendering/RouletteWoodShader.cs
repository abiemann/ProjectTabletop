using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.RouletteRendering;

// One physical surface under the stationary wheel camera. The photograph is
// diffuse wood only: object coordinates move its grain, not its illumination.
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct RouletteWoodShader(
    Float2 woodSize, Float2 rotation, int cone, int nearSideOnly, float edgeWidth) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 screen = D2D.GetScenePosition().XY - new Float2(200, 200);
        // These bounds only avoid work outside the entire projected surface.
        // The actual alpha silhouette below follows its physical radial edges.
        if (cone != 0 && (Hlsl.Abs(screen.X) > 120 || screen.Y < -90 || screen.Y > 110))
            return new Float4(0, 0, 0, 0);
        float denominator = .79f + screen.Y * .00045f;
        float height = cone != 0 ? 4 : 8;
        float y = (screen.Y + .64f * height) / denominator;
        float x = screen.X * (1 - .00045f * y);
        float radius = Hlsl.Sqrt(x * x + y * y);
        if (cone != 0)
        {
            // Invert y_screen=(.79*y-.64*h(r))/(1-.00045*y).
            // A bounded Newton solve retains the original raised-cone profile;
            // rotating a flattened ellipse would make the grain slide across it.
            for (int iteration = 0; iteration < 5; iteration++)
            {
                x = screen.X * (1 - .00045f * y);
                radius = Hlsl.Sqrt(x * x + y * y);
                height = ConeHeight(radius);
                float radialDerivative = (y - .00045f * screen.X * x) / Hlsl.Max(radius, .01f);
                float slope = ConeSlope(radius);
                float derivative = denominator + .64f * slope * radialDerivative;
                float error = denominator * y - screen.Y - .64f * height;
                y -= Hlsl.Clamp(error / Hlsl.Max(derivative, .25f), -48, 48);
            }
            x = screen.X * (1 - .00045f * y);
            radius = Hlsl.Sqrt(x * x + y * y);
            height = ConeHeight(radius);
        }

        float edge = Hlsl.Max(edgeWidth, .035f);
        float coverage = cone != 0 ? 1 - Hlsl.SmoothStep(112 - edge, 112 + edge, radius) :
            Hlsl.SmoothStep(162 - edge, 162 + edge, radius) *
            (1 - Hlsl.SmoothStep(186 - edge, 186 + edge, radius));
        if (nearSideOnly != 0) coverage *= Hlsl.SmoothStep(-edge, edge, y);
        if (coverage <= 0) return new Float4(0, 0, 0, 0);

        Float2 world = new Float2(x, y);
        Float2 material = cone != 0 ? new Float2(
            rotation.X * x + rotation.Y * y, -rotation.Y * x + rotation.X * y) : world;
        // Each part uses one continuous slab. Neither wraps or tiles at a seam.
        float slabWidth = cone != 0 ? 255 : 410;
        Float2 uv = material / slabWidth + .5f;
        Float2 samplePosition = Hlsl.Clamp(uv * woodSize, new Float2(.5f, .5f), woodSize - .5f);
        Float3 wood = D2D.SampleInputAtPosition(0, samplePosition).XYZ;
        Float3 albedo = wood * wood;

        float slopeNormal = cone != 0 ? ConeSlope(radius) : 0;
        Float3 normal = Hlsl.Normalize(new Float3(world / Hlsl.Max(radius, 1) * slopeNormal, 1));
        Float3 light = Hlsl.Normalize(new Float3(-.55f, -.72f, 1.45f));
        // A fixed, distant camera supplies the view-dependent lacquer lobe.
        // Surface height and position change the reflection; wheel angle does not.
        Float3 view = Hlsl.Normalize(new Float3(-x, -900 - y, 1110 - height));
        float diffuse = .30f + .75f * Hlsl.Saturate(Hlsl.Dot(normal, light));
        Float3 lit = albedo * new Float3(1.04f, .98f, .91f) * diffuse;
        if (cone != 0)
        {
            // Dense polished burl retains its dark pores under the clear coat.
            // The pocket seat supplies a little ambient occlusion at the foot,
            // not an image-space vignette or a moving texture shadow.
            float seat = 1 - .16f * Hlsl.SmoothStep(94, 112, radius);
            lit *= new Float3(.89f, .86f, .83f) * seat;
        }

        Float3 reflected = 2 * Hlsl.Dot(normal, view) * normal - view;
        Float2 reflectedSlope = reflected.XY / Hlsl.Max(reflected.Z, .12f);
        Float2 source = reflectedSlope - new Float2(-.24f, .77f);
        // A narrow cream softbox and a broad weak room reflection describe a
        // clear polished finish. They bend over the cone instead of orbiting it.
        float across = source.Y + source.X * .14f;
        float strip = (1 - Hlsl.SmoothStep(.20f, .29f, Hlsl.Abs(source.X))) *
            (1 - Hlsl.SmoothStep(.025f, .066f, Hlsl.Abs(across)));
        float surround = Hlsl.Exp(-source.X * source.X / .45f - across * across / .045f);
        float visible = Hlsl.SmoothStep(.12f, .28f, reflected.Z);
        float roughness = .84f + .16f * Hlsl.Saturate(Hlsl.Dot(wood, new Float3(.3f, .5f, .2f)));
        lit += new Float3(1, .94f, .79f) * (strip * .31f + surround * .024f) * visible * roughness;

        if (cone != 0)
        {
            // A finite overhead diffuser reflects in the sloping clear coat.
            // Intersect the reflected ray with its fixed room-height plane:
            // this bends and tapers the reflection over the actual cone rather
            // than painting a rectangle or rotating a highlight with the grain.
            Float2 ceiling = world + reflectedSlope * (340 - height);
            Float2 offset = ceiling - new Float2(-224, 79);
            float along = offset.X * .60f - offset.Y * .80f;
            float acrossCoat = offset.X * .80f + offset.Y * .60f;
            float panel = along * along / (108 * 108) + acrossCoat * acrossCoat / (15 * 15);
            float clearCoat = 1 - Hlsl.SmoothStep(.30f, 1, panel);
            float bloom = Hlsl.Exp(-along * along / (132 * 132) - acrossCoat * acrossCoat / (32 * 32));
            float grazing = Hlsl.Pow(Hlsl.Saturate(1 - Hlsl.Dot(normal, view)), 5);
            // A distinct reflected light, with enough transmission to read the
            // figuring beneath it. The weak broad lobe is the polished room.
            lit += new Float3(1, .96f, .84f) * (clearCoat * .39f + bloom * .032f) *
                visible * (.93f + grazing * 2.2f) * roughness;
        }

        // A gently rounded lacquer edge catches a small fixed light without
        // changing the agreed top plane or the foreground casing's occlusion.
        if (cone == 0)
        {
            float arc = Hlsl.Saturate((-x * .58f - y * .82f) / Hlsl.Max(radius, 1));
            float band = (radius - 182.6f) / 1.25f;
            float edgeGlint = Hlsl.Exp(-band * band) * Hlsl.Pow(Hlsl.Saturate(arc), 9);
            lit += new Float3(1, .89f, .66f) * edgeGlint * .19f;
        }
        Float3 display = Hlsl.Sqrt(Hlsl.Saturate(lit));
        return new Float4(display * coverage, coverage);
    }

    private static float ConeHeight(float radius) => 24 - 36 * Hlsl.Pow(Hlsl.Abs(radius / 112), .85f);
    private static float ConeSlope(float radius) => (36 * .85f / 112) *
        Hlsl.Pow(Hlsl.Abs(Hlsl.Max(radius, 1) / 112), -.15f);
}
