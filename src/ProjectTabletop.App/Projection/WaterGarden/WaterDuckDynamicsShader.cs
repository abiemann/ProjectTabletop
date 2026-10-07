using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal static class WaterGardenDucks
{
    public const int InitialCount = 10;
    public const int MaximumCount = 20;
    public const int Rows = 5;
    public const int StickWakeCount = 8;
}

// Each column describes one floating hull. Rows contain position/heading,
// velocity, buoyancy/scale, surface tilt, and airborne fall/impact state. All
// alpha channels stay opaque. Normal drift follows the actual water on the GPU;
// the host reads this tiny texture only while a newly added duck is airborne,
// to observe its first impact and drive the pond splash.
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
        else if (index == 5) { position = new Float2(.45f, .36f); yaw = -.3f; scale = .023f; }
        else if (index == 6) { position = new Float2(.62f, .46f); yaw = 1.6f; scale = .022f; }
        else if (index == 7) { position = new Float2(.27f, .55f); yaw = -2.2f; scale = .024f; }
        else if (index == 8) { position = new Float2(.49f, .72f); yaw = .4f; scale = .021f; }
        else if (index == 9) { position = new Float2(.75f, .51f); yaw = 2.8f; scale = .023f; }
        else if (index >= WaterGardenDucks.InitialCount)
        {
            position = new Float2(.5f, .5f);
            yaw = 0;
            scale = 0;
        }
        if (row == 0) return new Float4(position, yaw, 1);
        if (row == 2) return new Float4(0, 0, scale * Hlsl.Min(aspect, 1), 1);
        if (row == 4) return new Float4(0, 0, 1, 1);
        return new Float4(0, 0, 0, 1);
    }
}

// Addition is rare, so this pass copies the tiny existing state texture and
// initializes only one new column. The GPU copy preserves every moving duck
// while the new one begins its fall above the center of the water.
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float32)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct WaterDuckSpawnShader(int spawnIndex, Float2 spawnPosition,
    float yaw, float scale, float dropHeight) : ID2D1PixelShader
{
    public Float4 Execute()
    {
        Float2 scene = D2D.GetScenePosition().XY;
        int index = (int)Hlsl.Floor(scene.X);
        int row = (int)Hlsl.Floor(scene.Y);
        Float4 existing = D2D.SampleInputAtPosition(0,
            new Float2(index + .5f, row + .5f));
        if (index != spawnIndex) return existing;
        if (row == 0) return new Float4(spawnPosition, yaw, 1);
        if (row == 2) return new Float4(0, 0, scale, 1);
        if (row == 4) return new Float4(dropHeight, 0, -1, 1);
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
    float dt, float time, int ambientEnabled, int activeDuckCount,
    Float4 duckSplash,
    Float4 landing0, Float4 landing1, Float4 landing2, Float4 landing3,
    Float4 stick0, Float4 stick1, Float4 stick2, Float4 stick3,
    Float4 stick4, Float4 stick5, Float4 stick6, Float4 stick7) : ID2D1PixelShader
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

    private Float2 LandingCurrent(Float2 position, Float2 metric, Float4 landing)
    {
        // A parcel transfers momentum to the pond at its measured landing point.
        // That flux spreads as a surface current; the rear cascade also gives the
        // outward flow a downstream bias toward the front of the garden.
        Float2 delta = (position - landing.XY) * metric;
        float distance = Hlsl.Length(delta);
        float radius = .12f * Hlsl.Min(aspect, 1);
        float falloff = Hlsl.Exp(-.5f * distance * distance / (radius * radius));
        Float2 radial = delta / Hlsl.Max(distance, .0001f);
        Float2 flow = new Float2(radial.X * .35f, Hlsl.Max(.55f, radial.Y + .8f));
        flow /= Hlsl.Max(Hlsl.Length(flow), .0001f);
        return flow * landing.Z * falloff;
    }

    private Float2 StickCurrent(Float2 position, Float2 metric, Float4 wake)
    {
        Float2 delta = (position - wake.XY) * metric;
        float distance = Hlsl.Length(delta);
        float radius = .060f * Hlsl.Min(aspect, 1);
        float falloff = Hlsl.Exp(-.5f * distance * distance / (radius * radius));
        Float2 radial = delta / Hlsl.Max(distance, .0001f);
        Float2 stroke = new Float2(Hlsl.Cos(wake.W), Hlsl.Sin(wake.W));
        Float2 flow = radial * .55f + stroke * .75f;
        flow /= Hlsl.Max(Hlsl.Length(flow), .0001f);
        return flow * wake.Z * falloff;
    }

    private Float2 SplashCurrent(Float2 position, Float2 metric)
    {
        Float2 delta = (position - duckSplash.XY) * metric;
        float distance = Hlsl.Length(delta);
        float radius = .105f * Hlsl.Min(aspect, 1);
        float falloff = Hlsl.Exp(-.5f * distance * distance / (radius * radius));
        return delta / Hlsl.Max(distance, .0001f) * duckSplash.Z * falloff;
    }

    private Float4 DuckContact(int index, int other, Float4 horizontal,
        Float4 motion, Float4 vertical, Float4 drop, Float2 metric)
    {
        // This helper is called for each fixed slot, outside a shader loop.
        // D2D input sampling requires gradients unavailable in a varying loop.
        Float4 otherPosition = State(other, 0);
        Float4 otherMotion = State(other, 1);
        Float4 otherVertical = State(other, 2);
        Float4 otherDrop = State(other, 4);
        if (other >= activeDuckCount || other == index) return new Float4(0, 0, 0, 0);

        float combinedRadius = (vertical.Z + otherVertical.Z) * 1.14f;
        Float2 separation = (horizontal.XY - otherPosition.XY) * metric;
        float dz = vertical.X + drop.X - otherVertical.X - otherDrop.X;
        float distance = Hlsl.Sqrt(Hlsl.Dot(separation, separation) + dz * dz);
        if (combinedRadius <= 0 || distance >= combinedRadius) return new Float4(0, 0, 0, 0);

        float planarDistance = Hlsl.Length(separation);
        Float2 lateral = planarDistance > .0001f
            ? separation / planarDistance
            : new Float2(index < other ? -1 : 1, 0);
        float verticalBias = Hlsl.Saturate(Hlsl.Abs(dz) /
            Hlsl.Max(.25f * combinedRadius, .0001f));
        Float2 contactXY = planarDistance > .0001f
            ? separation
            : lateral * (.17f * combinedRadius);
        Float3 normal = Hlsl.Normalize(new Float3(
            contactXY + lateral * (.17f * combinedRadius * verticalBias), dz));
        float approaching = Hlsl.Dot(new Float3(motion.XY - otherMotion.XY,
            drop.Y - otherDrop.Y), normal);
        float overlap = combinedRadius - distance;
        float impact = Hlsl.Max(0, -(1.0f + .38f) * approaching * .5f);
        float separationSpeed = Hlsl.Min(.06f, overlap * 3f);
        return new Float4(normal * (impact + separationSpeed), overlap);
    }

    public Float4 Execute()
    {
        int index = (int)Hlsl.Floor(D2D.GetScenePosition().X);
        int row = (int)Hlsl.Floor(D2D.GetScenePosition().Y);
        Float4 previous = State(index, row);
        Float4 horizontal = State(index, 0);
        Float4 motion = State(index, 1);
        Float4 vertical = State(index, 2);
        Float4 drop = State(index, 4);
        Float2 oldSlope = State(index, 3).XY;
        Float2 position = horizontal.XY, velocity = motion.XY;
        float dropHeight = drop.X, fallVelocity = drop.Y, splashAge = drop.Z;
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
        acceleration += LandingCurrent(position, metric, landing0) +
            LandingCurrent(position, metric, landing1) +
            LandingCurrent(position, metric, landing2) +
            LandingCurrent(position, metric, landing3) +
            StickCurrent(position, metric, stick0) +
            StickCurrent(position, metric, stick1) +
            StickCurrent(position, metric, stick2) +
            StickCurrent(position, metric, stick3) +
            StickCurrent(position, metric, stick4) +
            StickCurrent(position, metric, stick5) +
            StickCurrent(position, metric, stick6) +
            StickCurrent(position, metric, stick7) +
            SplashCurrent(position, metric);

        Float2 low = new Float2(.16f, .22f), high = new Float2(.84f, .82f);
        Float2 edgeDistance = Hlsl.Min(position - low, high - position) * metric;
        Float2 edgeDirection = new Float2(position.X < .5f ? 1 : -1, position.Y < .52f ? 1 : -1);
        acceleration += edgeDirection * (1 - Hlsl.Saturate(edgeDistance / (.05f * Hlsl.Min(aspect, 1)))) * .016f;
        acceleration = Hlsl.Clamp(acceleration, new Float2(-.12f, -.12f), new Float2(.12f, .12f));
        velocity = (velocity + acceleration * dt) * Hlsl.Exp(-1.35f * dt);
        float speed = Hlsl.Length(velocity);
        // Preserve a real sideways rebound from duck contact. Ambient, stick,
        // and fountain acceleration remain bounded above; the larger speed cap
        // matters chiefly after a collision impulse.
        float maximumSpeed = .16f * Hlsl.Min(aspect, 1);
        velocity *= Hlsl.Min(1, maximumSpeed / Hlsl.Max(speed, .000001f));

        // Integrate the new toy above the same buoyant water plane as the
        // settled flock. A low-restitution rebound gives the little hull a
        // visible bob before the buoyancy spring takes over. Only the very
        // first crossing exposes splashAge=0 to the host.
        if (splashAge >= 0 && splashAge < 1)
            splashAge = Hlsl.Min(1, splashAge + dt);
        if (dropHeight > 0 || fallVelocity > 0)
        {
            fallVelocity -= 2.8f * dt;
            dropHeight += fallVelocity * dt;
            if (dropHeight <= 0)
            {
                dropHeight = 0;
                if (splashAge < 0) splashAge = 0;
                fallVelocity = -fallVelocity > .08f ? -fallVelocity * .12f : 0;
            }
        }
        // Every column reads the same previous frame, so each pair receives
        // equal and opposite contact impulses. Fixed, explicit slot calls keep
        // D2D gradient sampling out of a varying loop on FXC. The normal has
        // a small lateral component for a near-vertical landing collision.
        Float4 contact =
            DuckContact(index, 0, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 1, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 2, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 3, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 4, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 5, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 6, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 7, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 8, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 9, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 10, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 11, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 12, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 13, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 14, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 15, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 16, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 17, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 18, horizontal, motion, vertical, drop, metric) +
            DuckContact(index, 19, horizontal, motion, vertical, drop, metric);
        velocity += contact.XY;
        // Several hulls can crowd one landing. Bound their combined impulse so
        // a toy cannot be launched unrealistically high while leaving the
        // normal free-fall speed and small water rebound intact.
        fallVelocity = Hlsl.Clamp(fallVelocity + contact.Z, -2.6f, .45f);
        float contactLength = Hlsl.Length(contact.XYZ);
        if (contactLength > .00001f)
        {
            Float3 correction = contact.XYZ / contactLength *
                (Hlsl.Min(contact.W, .025f) * .42f);
            position += correction.XY / metric;
            dropHeight = Hlsl.Max(0, dropHeight + correction.Z);
        }

        // The pool supports a settled hull from below. A downward impulse is
        // absorbed as a brief bob rather than remaining as stale fall velocity.
        if (dropHeight <= 0 && fallVelocity < 0)
        {
            if (splashAge < 0) splashAge = 0;
            heave = Hlsl.Max(-.05f, heave + fallVelocity * dt * .2f);
            fallVelocity = 0;
        }

        speed = Hlsl.Length(velocity);
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

        Float4 next = new Float4(slope, 0, 1);
        if (row == 0) next = new Float4(position, yaw, 1);
        else if (row == 1) next = new Float4(velocity, yawVelocity, 1);
        else if (row == 2) next = new Float4(heave, verticalVelocity, vertical.Z, 1);
        else if (row == 4) next = new Float4(dropHeight, fallVelocity, splashAge, 1);
        // Inactive slots retain their untouched state until DUCK+ initializes
        // them. This also keeps every shader output path explicitly assigned.
        if (index >= activeDuckCount) next = previous;
        return next;
    }
}
