using ComputeSharp;
using ComputeSharp.D2D1;

namespace ProjectTabletop.App.Projection.WaterGarden;

// The visible water is the isosurface of the particle solver's density volume.
// Positions, connected streams, pooling and detached drops all come from that
// state. There are no predetermined stream paths or time-animated water masks.
internal readonly partial struct WaterSurfaceShader
{
    private Float4 FountainFluidSample(Float3 world)
    {
        Float3 uvw = (world - fountainVolumeMin) / fountainVolumeSize;
        Float4 value = new Float4(0, 0, 0, 0);
        if (uvw.X >= 0 && uvw.X <= 1 && uvw.Y >= 0 && uvw.Y <= 1 && uvw.Z >= 0 && uvw.Z <= 1)
        {
            Float3 cell = Hlsl.Clamp(uvw * fountainGridSize - .5f,
                new Float3(0, 0, 0), fountainGridSize - 1);
            float slice0 = Hlsl.Floor(cell.Z), slice1 = Hlsl.Min(slice0 + 1, fountainGridSize.Z - 1);
            float row0 = Hlsl.Floor(slice0 / fountainAtlasTiles.X), row1 = Hlsl.Floor(slice1 / fountainAtlasTiles.X);
            Float2 tile0 = new Float2(slice0 - row0 * fountainAtlasTiles.X, row0);
            Float2 tile1 = new Float2(slice1 - row1 * fountainAtlasTiles.X, row1);
            Float2 atlasSize = fountainGridSize.XY * fountainAtlasTiles;
            Float2 pixel0 = tile0 * fountainGridSize.XY + cell.XY + .5f;
            Float2 pixel1 = tile1 * fountainGridSize.XY + cell.XY + .5f;
            // Each XY sample stays within the centers of one slice tile.
            // Explicit interpolation in Z reconstructs the full 3D density.
            Float4 lower = D2D.SampleInputAtPosition(5, pixel0 / atlasSize * size);
            Float4 upper = D2D.SampleInputAtPosition(5, pixel1 / atlasSize * size);
            value = Hlsl.Lerp(lower, upper, cell.Z - slice0);
        }
        return value;
    }

    private Float4 FountainFluidSurface(Float3 point, Float3 fallback)
    {
        Float3 cell = fountainVolumeSize / fountainGridSize;
        Float3 dx = new Float3(cell.X, 0, 0), dy = new Float3(0, cell.Y, 0), dz = new Float3(0, 0, cell.Z);
        Float4 xp = FountainFluidSample(point + dx), xm = FountainFluidSample(point - dx);
        Float4 yp = FountainFluidSample(point + dy), ym = FountainFluidSample(point - dy);
        Float4 zp = FountainFluidSample(point + dz), zm = FountainFluidSample(point - dz);
        Float3 gradient = new Float3(
            xp.X - xm.X, yp.X - ym.X, zp.X - zm.X) / cell;
        Float3 normal = Hlsl.Dot(gradient, gradient) > .00000001f ? -Hlsl.Normalize(gradient) : fallback;

        // Scattering belongs only to curved, shearing parts of the actual
        // reconstructed liquid. Reuse the normal samples rather than adding
        // decorative foam masks or time-driven sparkle.
        Float4 center = FountainFluidSample(point);
        float surrounding = xp.X + xm.X + yp.X + ym.X + zp.X + zm.X;
        float curvature = Hlsl.Saturate((6 * center.X - surrounding) / Hlsl.Max(.25f, 6 * center.X));
        float scale = Hlsl.Min(aspect, 1);
        Float2 velocity = center.YZ / scale;
        float variation = (
            Hlsl.Length(xp.YZ / scale - velocity) * xp.X + Hlsl.Length(xm.YZ / scale - velocity) * xm.X +
            Hlsl.Length(yp.YZ / scale - velocity) * yp.X + Hlsl.Length(ym.YZ / scale - velocity) * ym.X +
            Hlsl.Length(zp.YZ / scale - velocity) * zp.X + Hlsl.Length(zm.YZ / scale - velocity) * zm.X) /
            Hlsl.Max(.15f, surrounding);
        float scattering = Hlsl.SmoothStep(.025f, .24f, curvature) * Hlsl.SmoothStep(.025f, .18f, variation) *
            Hlsl.SmoothStep(.10f, .30f, Hlsl.Length(velocity));
        return new Float4(normal, scattering);
    }

    private Float3 FountainFluidNormal(Float3 point, Float3 fallback) => FountainFluidSurface(point, fallback).XYZ;

    private Float3 FountainFluidTraceStep(Float3 origin, Float3 ray, Float3 state, Float3 limits)
    {
        // state = current distance, previous distance, first hit.
        // limits = opaque/volume end, near-water step, empty-space step.
        if (state.Z >= 1000 && state.X <= limits.X)
        {
            float density = FountainFluidSample(origin + ray * state.X).X;
            if (density >= .16f) state.Z = state.X;
            else
            {
                state.Y = state.X;
                state.X += density > .035f ? limits.Y : limits.Z;
            }
        }
        return state;
    }

    private Float2 FountainFluidRefine(Float3 origin, Float3 ray, Float2 bracket)
    {
        float middle = (bracket.X + bracket.Y) * .5f;
        if (FountainFluidSample(origin + ray * middle).X >= .16f) bracket.Y = middle;
        else bracket.X = middle;
        return bracket;
    }

    private float FountainFluidTrace(Float3 origin, Float3 ray, float opaqueDistance)
    {
        Float2 interval = StoneRayInterval(origin, ray, fountainVolumeMin,
            fountainVolumeMin + fountainVolumeSize);
        float end = Hlsl.Min(interval.Y, opaqueDistance);
        Float3 cell = fountainVolumeSize / fountainGridSize;
        float smallStep = Hlsl.Min(cell.X, Hlsl.Min(cell.Y, cell.Z)) * .90f;
        // Stay below the diameter of the thinnest reconstructed droplet so an
        // empty-space sample cannot leap over real water between two voxels.
        float emptyStep = Hlsl.Max(cell.X, Hlsl.Max(cell.Y, cell.Z)) * .90f;
        float hit = 10000;
        if (end >= interval.X)
        {
            Float3 state = new Float3(interval.X, interval.X, 10000);
            Float3 limits = new Float3(end, smallStep, emptyStep);
            // Deliberately expand these calls in source. Direct2D's image
            // sampler uses implicit derivatives, and legacy FXC rejects even
            // fixed-count loops whose sampling positions vary by pixel.
            // Each call is inactive after a hit or the nearest solid/box exit.
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 8
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 16
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 24
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 32
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 40
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 48
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits);
            state = FountainFluidTraceStep(origin, ray, state, limits); // 56
            if (state.Z < 1000)
            {
                Float2 bracket = new Float2(state.Y, state.Z);
                bracket = FountainFluidRefine(origin, ray, bracket);
                bracket = FountainFluidRefine(origin, ray, bracket);
                bracket = FountainFluidRefine(origin, ray, bracket);
                bracket = FountainFluidRefine(origin, ray, bracket);
                hit = bracket.Y;
            }
        }
        return hit;
    }

    private Float3 FountainFluidThicknessStep(Float3 point, Float3 insideRay, Float3 state, float sampleStep)
    {
        // state = traveled distance, integrated optical depth, still inside.
        if (state.Z > 0)
        {
            float density = FountainFluidSample(point + insideRay * state.X).X;
            if (density < .12f) state.Z = 0;
            else
            {
                state.Y += Hlsl.Saturate(density) * sampleStep;
                state.X += sampleStep;
            }
        }
        return state;
    }

    private Float3 FountainFluidBehind(Float3 origin, Float3 ray, Float3 fallback)
    {
        Float4 rock = FountainSolidRay(origin, ray);
        Float4 basin = BasinHit(origin, ray);
        float pondDistance = ray.Z < -.00001f ? -origin.Z / ray.Z : 10000;
        Float3 color = fallback;
        if (rock.W >= 0 && rock.W < Hlsl.Min(pondDistance, basin.W)) color = rock.XYZ;
        else if (basin.W >= 0 && basin.W < Hlsl.Min(pondDistance, rock.W))
            color = Granite(origin + ray * basin.W, basin.XYZ);
        else if (pondDistance > 0 && pondDistance < 1000)
        {
            Float3 pond = origin + ray * pondDistance;
            if (InPond(pond.XY))
            {
                // Refract through the receiving pool as well, sampling its real
                // height field and gravel at the displaced three-dimensional ray.
                Float2 uv = pond.XY / new Float2(aspect, 1) + .5f;
                Float2 texel = 1 / fieldSize, metric = new Float2(aspect, 1) / fieldSize;
                Float2 slope = new Float2(
                    Height(uv + new Float2(texel.X, 0)) - Height(uv - new Float2(texel.X, 0)),
                    Height(uv + new Float2(0, texel.Y)) - Height(uv - new Float2(0, texel.Y))) / (2 * metric) * 2.4f;
                slope = Hlsl.Clamp(slope, new Float2(-.85f, -.85f), new Float2(.85f, .85f));
                Float3 pondNormal = Hlsl.Normalize(new Float3(-slope, 1));
                Float3 throughPool = Hlsl.Refract(ray, pondNormal, 1 / 1.333f);
                float depth = .042f + .008f * Noise(uv * new Float2(aspect, 1) * 3);
                Float2 gravel = uv + throughPool.XY / Hlsl.Max(.15f, -throughPool.Z) * depth / new Float2(aspect, 1);
                Float3 bed = D2D.SampleInputAtPosition(1,
                    Hlsl.Clamp(gravel * size, new Float2(.5f, .5f), size - .5f)).XYZ;
                Float4 sky = Environment(Hlsl.Reflect(ray, pondNormal), pond.XY);
                float facing = Hlsl.Saturate(Hlsl.Dot(pondNormal, -ray));
                float reflectedAmount = .035f + .965f * Hlsl.Pow(1 - facing, 5) + .23f * sky.W * sky.W;
                color = Hlsl.Lerp(bed * new Float3(.976f, .986f, .989f), sky.XYZ, Hlsl.Saturate(reflectedAmount));
            }
        }
        else if (ray.Z > 0) color = Environment(ray, origin.XY).XYZ;
        return color;
    }

    private Float4 FountainWaterRay(Float3 origin, Float3 ray, Float3 background, float opaqueDistance)
    {
        Float4 result = new Float4(background, 10000);
        // Host sets this from actual particle occupancy, not a cosmetic timer.
        if (fountainFlow <= 0) return result;
        float hit = FountainFluidTrace(origin, ray, opaqueDistance);
        if (hit < 1000)
        {
            Float3 cell = fountainVolumeSize / fountainGridSize;
            float sampleStep = Hlsl.Min(cell.X, Hlsl.Min(cell.Y, cell.Z)) * .8f;
            Float3 point = origin + ray * hit;
            Float4 surface = FountainFluidSurface(point, -ray);
            Float3 normal = surface.XYZ;
            if (Hlsl.Dot(normal, ray) > 0) normal = -normal;
            Float3 insideRay = Hlsl.Refract(ray, normal, 1 / 1.333f);
            Float3 thicknessState = new Float3(sampleStep, 0, 1);
            // Follow the transmitted ray through the reconstructed body.
            // Like the surface trace, texture-bearing steps are source-unrolled.
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep); // 8
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            thicknessState = FountainFluidThicknessStep(point, insideRay, thicknessState, sampleStep);
            float thickness = thicknessState.X, opticalDepth = thicknessState.Y;
            Float3 exitPoint = point + insideRay * thickness;
            Float3 exitNormal = FountainFluidNormal(exitPoint, insideRay);
            if (Hlsl.Dot(exitNormal, insideRay) < 0) exitNormal = -exitNormal;
            Float3 transmittedRay = Hlsl.Refract(insideRay, -exitNormal, 1.333f);
            if (Hlsl.Dot(transmittedRay, transmittedRay) < .00001f)
                transmittedRay = Hlsl.Reflect(insideRay, exitNormal);
            Float3 transmitted = FountainFluidBehind(exitPoint + transmittedRay * sampleStep * .25f,
                transmittedRay, background);
            Float3 absorption = Hlsl.Exp(-Hlsl.Max(opticalDepth, thickness * .35f) * new Float3(.85f, .32f, .16f));
            transmitted = transmitted * absorption + new Float3(.28f, .43f, .42f) * (1 - absorption);

            Float3 reflectedRay = Hlsl.Reflect(ray, normal);
            Float3 reflected = Environment(reflectedRay, point.XY).XYZ;
            Float4 reflectedStone = FountainSolidRay(point + normal * sampleStep * .35f, reflectedRay);
            if (reflectedStone.W > .00001f && reflectedStone.W < 1000) reflected = reflectedStone.XYZ;
            float facing = Hlsl.Saturate(Hlsl.Dot(normal, -ray));
            float fresnel = .0204f + .9796f * Hlsl.Pow(1 - facing, 5);
            Float3 color = Hlsl.Lerp(transmitted, reflected, fresnel);
            // A broad light on the viewer's side catches vertical cascades as
            // well as horizontal pools, so clear water reads against wet slate.
            Float3 light = Hlsl.Normalize(new Float3(-.38f, .62f, .82f));
            Float3 halfway = Hlsl.Normalize(light - ray);
            float highlight = Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 68) * .64f +
                Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, halfway)), 12) * .085f;
            Float3 fillHalfway = Hlsl.Normalize(Hlsl.Normalize(new Float3(.72f, .28f, .68f)) - ray);
            highlight += Hlsl.Pow(Hlsl.Saturate(Hlsl.Dot(normal, fillHalfway)), 90) * .20f;
            color += new Float3(1, .99f, .94f) * highlight;
            Float3 whiteWater = new Float3(.88f, .94f, .93f) *
                (.65f + .35f * Hlsl.Saturate(Hlsl.Dot(normal, light)));
            color = Hlsl.Lerp(color, whiteWater, surface.W * .24f);
            result = new Float4(Hlsl.Saturate(color), hit);
        }
        return result;
    }
}
