using System.Numerics;

namespace ProjectTabletop.App.Projection.WaterGarden;

/// <summary>Shared rear rock-cascade geometry and source timing.</summary>
internal static class WaterFountainLayout
{
    public const float AnchorY = -.50f;
    public const float Gravity = 1.8f;

    public static float FlowAmount(double seconds)
    {
        float t = (float)Math.Clamp(seconds - 1, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static bool OccludesSurface(Vector2 screen, float aspect)
    {
        if (!float.IsFinite(aspect) || aspect <= 0) return false;
        var uv = WaterGardenView.ScreenToSurface(screen);
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) return false;
        var point = new Vector3((uv.X - .5f) * aspect, uv.Y - .5f, 0);
        var eye = new Vector3(0, WaterGardenView.Distance * WaterGardenView.SinTilt,
            WaterGardenView.Distance * WaterGardenView.CosTilt);
        float planeDistance = Vector3.Distance(eye, point);
        var direction = Vector3.Normalize(point - eye);
        float scale = Math.Min(aspect, 1);
        var low = new Vector3(-.255f * scale, AnchorY - .105f * scale, -.060f * scale);
        var high = new Vector3(.255f * scale, AnchorY + .235f * scale, .202f * scale);
        float near = 0, far = planeDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = eye[axis], ray = direction[axis];
            if (Math.Abs(ray) < 1e-6f)
            {
                if (origin < low[axis] || origin > high[axis]) return false;
                continue;
            }
            float a = (low[axis] - origin) / ray, b = (high[axis] - origin) / ray;
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
            if (near > far) return false;
        }
        // The CPU follows the same six irregular stones as the renderer, leaving
        // the empty corners and channels of their bounding box interactive.
        for (int step = 0; step < 40 && near <= far; step++)
        {
            float advance = StoneDistance(eye + direction * near, scale);
            if (advance < .00013f) return near < planeDistance;
            near += advance * .72f;
        }
        return false;
    }

    // The six stones never change shape; reuse their constant geometry in every
    // collision query without changing the point-dependent distance arithmetic.
    private static readonly StoneLayer Layer0 = new(new(-.019f, .078f), new(.186f, .098f), -.045f, .055f, .8f);
    private static readonly StoneLayer Layer1 = new(new(-.011f, .088f), new(.205f, .110f), .045f, .083f, 2.1f);
    private static readonly StoneLayer Layer2 = new(new(.027f, .034f), new(.150f, .079f), .067f, .112f, 4.2f);
    private static readonly StoneLayer Layer3 = new(new(.024f, .035f), new(.175f, .100f), .108f, .137f, 5.6f);
    private static readonly StoneLayer Layer4 = new(new(-.040f, .003f), new(.112f, .066f), .117f, .165f, 7.3f);
    private static readonly StoneLayer Layer5 = new(new(-.038f, .005f), new(.132f, .078f), .160f, .180f, 9.1f);

    internal static float StoneDistance(Vector3 p, float scale)
    {
        Vector3 q = (p - new Vector3(0, AnchorY, 0)) / scale;
        float distance = Layer(q, in Layer0);
        distance = Math.Min(distance, Layer(q, in Layer1));
        distance = Math.Min(distance, Layer(q, in Layer2));
        distance = Math.Min(distance, Layer(q, in Layer3));
        distance = Math.Min(distance, Layer(q, in Layer4));
        distance = Math.Min(distance, Layer(q, in Layer5));
        return distance * scale;
    }

    private readonly struct StoneLayer
    {
        internal readonly Vector2 Center, HalfSize, HalfSizeSquared, Cut0, Cut1;
        internal readonly Vector3 BoxHalfSize;
        internal readonly float Middle, HalfHeight, Phase, PhaseSin, C, S, CutLimit0, CutLimit1;

        internal StoneLayer(Vector2 center, Vector2 halfSize, float bottom, float top, float phase)
        {
            Center = center;
            HalfSize = halfSize;
            HalfSizeSquared = halfSize * halfSize;
            Middle = (bottom + top) * .5f;
            HalfHeight = (top - bottom) * .5f;
            BoxHalfSize = new(halfSize + new Vector2(.036f), HalfHeight + .018f);
            Phase = phase;
            PhaseSin = MathF.Sin(phase);
            float angle = .12f * PhaseSin;
            C = MathF.Cos(angle);
            S = MathF.Sin(angle);
            Cut0 = Vector2.Normalize(new(.8f * PhaseSin, -.75f));
            CutLimit0 = .86f * (halfSize * Cut0).Length();
            Cut1 = Vector2.Normalize(new(-.9f * PhaseSin + .15f, -.35f));
            CutLimit1 = .92f * (halfSize * Cut1).Length();
        }
    }

    private static float Layer(Vector3 p, in StoneLayer layer)
    {
        Vector2 halfSize = layer.HalfSize;
        float phase = layer.Phase;
        Vector2 local = new Vector2(p.X, p.Y) - layer.Center;
        Vector3 box = Vector3.Abs(new(local, p.Z - layer.Middle)) - layer.BoxHalfSize;
        float lowerBound = Vector3.Max(box, Vector3.Zero).Length() +
            Math.Min(Math.Max(box.X, Math.Max(box.Y, box.Z)), 0);
        if (lowerBound > .020f) return lowerBound;
        float c = layer.C, s = layer.S;
        local = new(c * local.X + s * local.Y, -s * local.X + c * local.Y);
        float k0 = (local / halfSize).Length(), k1 = (local / layer.HalfSizeSquared).Length();
        float footprint = k0 < 1e-6f ? -Math.Min(halfSize.X, halfSize.Y) : k0 * (k0 - 1) / k1;
        float front = Math.Clamp((local.Y / halfSize.Y - .35f) / .65f, 0, 1);
        front = front * front * (3 - 2 * front);
        float irregular = .010f * MathF.Sin(local.X * 27 + phase) +
            .0055f * MathF.Sin(local.Y * 39 + phase * 1.4f) +
            .0028f * MathF.Sin((local.X + local.Y) * 110 + phase * 2);
        footprint += irregular * (1 - .72f * front);
        footprint = Math.Max(footprint, Vector2.Dot(local, layer.Cut0) - layer.CutLimit0);
        footprint = Math.Max(footprint, Vector2.Dot(local, layer.Cut1) - layer.CutLimit1);
        float uneven = .0048f * MathF.Sin(local.X * 24 + phase) +
            .0025f * MathF.Sin(local.Y * 31 + phase * 2) + .009f * local.X / halfSize.X * layer.PhaseSin;
        uneven *= 1 - .78f * front;
        const float bevel = .0048f;
        Vector2 section = new(footprint + bevel,
            Math.Abs(p.Z - uneven - layer.Middle) - layer.HalfHeight + bevel);
        return Vector2.Max(section, Vector2.Zero).Length() +
            Math.Min(Math.Max(section.X, section.Y), 0) - bevel;
    }
}
