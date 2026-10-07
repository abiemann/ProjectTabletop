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

    internal static float StoneDistance(Vector3 p, float scale)
    {
        Vector3 q = (p - new Vector3(0, AnchorY, 0)) / scale;
        float distance = Layer(q, new(-.019f, .078f), new(.186f, .098f), -.045f, .055f, .8f);
        distance = Math.Min(distance, Layer(q, new(-.011f, .088f), new(.205f, .110f), .045f, .083f, 2.1f));
        distance = Math.Min(distance, Layer(q, new(.027f, .034f), new(.150f, .079f), .067f, .112f, 4.2f));
        distance = Math.Min(distance, Layer(q, new(.024f, .035f), new(.175f, .100f), .108f, .137f, 5.6f));
        distance = Math.Min(distance, Layer(q, new(-.040f, .003f), new(.112f, .066f), .117f, .165f, 7.3f));
        distance = Math.Min(distance, Layer(q, new(-.038f, .005f), new(.132f, .078f), .160f, .180f, 9.1f));
        return distance * scale;
    }

    private static float Layer(Vector3 p, Vector2 center, Vector2 halfSize, float bottom, float top, float phase)
    {
        Vector2 local = new Vector2(p.X, p.Y) - center;
        Vector3 box = Vector3.Abs(new(local, p.Z - (bottom + top) * .5f)) -
            new Vector3(halfSize + new Vector2(.036f), (top - bottom) * .5f + .018f);
        float lowerBound = Vector3.Max(box, Vector3.Zero).Length() +
            Math.Min(Math.Max(box.X, Math.Max(box.Y, box.Z)), 0);
        if (lowerBound > .020f) return lowerBound;
        float angle = .12f * MathF.Sin(phase), c = MathF.Cos(angle), s = MathF.Sin(angle);
        local = new(c * local.X + s * local.Y, -s * local.X + c * local.Y);
        float k0 = (local / halfSize).Length(), k1 = (local / (halfSize * halfSize)).Length();
        float footprint = k0 < 1e-6f ? -Math.Min(halfSize.X, halfSize.Y) : k0 * (k0 - 1) / k1;
        float front = Math.Clamp((local.Y / halfSize.Y - .35f) / .65f, 0, 1);
        front = front * front * (3 - 2 * front);
        float irregular = .010f * MathF.Sin(local.X * 27 + phase) +
            .0055f * MathF.Sin(local.Y * 39 + phase * 1.4f) +
            .0028f * MathF.Sin((local.X + local.Y) * 110 + phase * 2);
        footprint += irregular * (1 - .72f * front);
        var cut = Vector2.Normalize(new(.8f * MathF.Sin(phase), -.75f));
        footprint = Math.Max(footprint, Vector2.Dot(local, cut) - .86f * (halfSize * cut).Length());
        cut = Vector2.Normalize(new(-.9f * MathF.Sin(phase) + .15f, -.35f));
        footprint = Math.Max(footprint, Vector2.Dot(local, cut) - .92f * (halfSize * cut).Length());
        float uneven = .0048f * MathF.Sin(local.X * 24 + phase) +
            .0025f * MathF.Sin(local.Y * 31 + phase * 2) + .009f * local.X / halfSize.X * MathF.Sin(phase);
        uneven *= 1 - .78f * front;
        const float bevel = .0048f;
        Vector2 section = new(footprint + bevel,
            Math.Abs(p.Z - uneven - (bottom + top) * .5f) - (top - bottom) * .5f + bevel);
        return Vector2.Max(section, Vector2.Zero).Length() +
            Math.Min(Math.Max(section.X, section.Y), 0) - bevel;
    }
}
