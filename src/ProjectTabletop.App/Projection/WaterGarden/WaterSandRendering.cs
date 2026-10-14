using ComputeSharp;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly partial struct WaterSurfaceShader
{
    private const float MaximumSandRidgeHeight = .025f;

    private float SandRidgeHeight(Float2 ground)
    {
        float radius = WaterGardenView.PondCornerRadius * Hlsl.Min(aspect, 1);
        float distance = RoundedRectangleDistance(ground, new Float2(aspect * .5f, .5f), radius);
        // Pushed-up sand hugs only the outside of the near edge, tapering
        // around the front corners before reaching the straight side edges.
        float near = Hlsl.SmoothStep(.02f, .70f, (ground.Y - (.5f - radius)) / radius);
        if (distance < -.0002f || distance > .065f || near <= 0) return 0;
        float lumps = Noise(ground * new Float2(37, 23) + 9.3f);
        float width = .033f + .016f * Noise(ground * new Float2(18, 31) + 3.7f);
        float skirt = Hlsl.Saturate(1 - Hlsl.Max(0, distance) / width);
        // A rounded shoulder against the container and a gentle granular toe;
        // all variation is fixed in world space, including during the entrance.
        float height = .015f + .007f * lumps;
        return near * height * skirt * skirt * (3 - 2 * skirt);
    }

    private Float4 SandGroundRay(Float3 origin, Float3 ray, float occluderDistance)
    {
        const float groundZ = -WaterGardenView.GroundDepth;
        float flatDistance = (groundZ - origin.Z) / ray.Z;
        float visibleDistance = flatDistance;
        float illumination = 1;
        // Stop at the water side: the ridge is outside the container, so a
        // ray must never skip its front surface and hit sand behind the glass.
        float far = Hlsl.Min(flatDistance, occluderDistance - .0001f);
        float near = Hlsl.Max(0, (groundZ + MaximumSandRidgeHeight - origin.Z) / ray.Z);
        Float3 end = origin + ray * far;
        float endHeight = SandRidgeHeight(end.XY);
        if (endHeight <= 0 && InPond(end.XY))
        {
            // A ray just below the water floor can cross the mound and end
            // inside the footprint. Bracket it at the same rounded boundary,
            // extended down to the sand, instead of skipping the raised edge.
            Float4 boundary = PondColumnHit(origin, ray, groundZ);
            far = Hlsl.Min(far, boundary.W - .0001f);
            end = origin + ray * far;
            endHeight = SandRidgeHeight(end.XY);
        }
        if (near < far && endHeight > 0 && end.Z - groundZ <= endHeight)
        {
            // This low relief is bracketed by two planes; fixed bisection needs
            // no texture reads and cannot step through its rounded silhouette.
            for (int step = 0; step < 12; step++)
            {
                float middle = (near + far) * .5f;
                Float3 point = origin + ray * middle;
                if (point.Z - groundZ > SandRidgeHeight(point.XY)) near = middle;
                else far = middle;
            }
            visibleDistance = (near + far) * .5f;
            Float2 ground = (origin + ray * visibleDistance).XY;
            const float epsilon = .001f;
            float dx = SandRidgeHeight(ground + new Float2(epsilon, 0)) -
                SandRidgeHeight(ground - new Float2(epsilon, 0));
            float dy = SandRidgeHeight(ground + new Float2(0, epsilon)) -
                SandRidgeHeight(ground - new Float2(0, epsilon));
            Float3 normal = Hlsl.Normalize(new Float3(-dx, -dy, epsilon * 2));
            Float3 light = Hlsl.Normalize(new Float3(-.35f, -.30f, 1));
            illumination = .68f + .36f * Hlsl.Saturate(Hlsl.Dot(normal, light));
        }
        // The raised ridge replaces the flat plane. Sample the photographic
        // grains once, after geometry has selected the visible ground point.
        Float3 color = SandGround((origin + ray * visibleDistance).XY) * illumination;
        return new Float4(color, visibleDistance);
    }
}
