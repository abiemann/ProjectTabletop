using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const float SlotDragonMorphResolution = 8;
    private readonly record struct SlotDragonMorphGeometry(float Top, Vector2 Eyes, Vector2 Mouth, float Bottom);
    private readonly record struct SlotDragonMorphFace(CanvasBitmap Bitmap, Rect Source, Rect Local,
        SlotDragonMorphGeometry Geometry);

    // Coarse eye-line centers in the green master row. Rows are ordered from
    // crown through eyes and muzzle to the lower neck edge; no jaw/neck contour
    // correspondence can fold the painting or cut holes through its mouth.
    private static readonly Vector2[] SlotDragonGreenEyeCenters =
    [
        new(122, 179), new(444.5f, 180.5f), new(809.5f, 183),
        new(1176.5f, 182), new(1503, 179)
    ];

    private void DrawSlotDragonMorphedHead(CanvasDrawingSession ds, int index, SlotDragonPose pose)
    {
        var frames = SlotDragonYawFrames(pose.Yaw);
        var calm = SlotDragonCalmMorphFace(index);
        var left = SlotDragonOpenMorphFace(index, frames.Left);
        var right = SlotDragonOpenMorphFace(index, frames.Right);
        var target = SlotDragonMorphInterpolate(calm.Geometry,
            SlotDragonMorphInterpolate(left.Geometry, right.Geometry, frames.Blend), pose.Roar);

        // Brief movement blends soften authored pose handoffs. By full aim the
        // head resolves to one solid painting. Its final source is chosen from
        // the fixed target, so crossing a midpoint during a turn cannot change
        // which endpoint the blend is settling toward.
        float settle = SlotDragonPoseEase((pose.AimWeight - .8f) / .2f);
        float endpoint = pose.TargetFrame <= frames.Left ? 0 : 1;
        float yawMix = frames.Left == frames.Right ? 0 : float.Lerp(frames.Blend, endpoint, settle);
        using var calmImage = pose.Roar < .999f ? DrawSlotDragonMorphSource(ds.Device, calm, target) : null;
        using var leftImage = yawMix < .999f ? DrawSlotDragonMorphSource(ds.Device, left, target) : null;
        using var rightImage = yawMix > .001f ? DrawSlotDragonMorphSource(ds.Device, right, target) : null;
        using var yawBlend = leftImage is not null && rightImage is not null
            ? new CrossFadeEffect { Source1 = leftImage, Source2 = rightImage, CrossFade = yawMix } : null;
        ICanvasImage openImage = (ICanvasImage?)yawBlend ?? leftImage ?? rightImage!;
        using var jawBlend = calmImage is not null
            ? new CrossFadeEffect { Source1 = calmImage, Source2 = openImage, CrossFade = pose.Roar } : null;
        ICanvasImage result = (ICanvasImage?)jawBlend ?? openImage;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / SlotDragonMorphResolution) * previous;
        try
        {
            // Effect inputs retain authored detail at eight times local rig
            // resolution, rather than rasterizing a face into a 50-pixel image.
            var bounds = new Rect(-64 * SlotDragonMorphResolution, -64 * SlotDragonMorphResolution,
                128 * SlotDragonMorphResolution, 96 * SlotDragonMorphResolution);
            ds.DrawImage(result, bounds, bounds, 1, CanvasImageInterpolation.HighQualityCubic);
        }
        finally { ds.Transform = previous; }
    }

    private SlotDragonMorphFace SlotDragonCalmMorphFace(int index)
    {
        var box = SlotDragonHeadRect;
        box.X -= (1 - index) * 32 * box.Width / 512;
        float cellWidth = (float)_slotDragonHeadArtwork!.Size.Width / 3;
        float cellHeight = (float)_slotDragonHeadArtwork.Size.Height / 2;
        var source = new Rect(index * cellWidth, 0, cellWidth, cellHeight);
        var eyes = new Vector2((float)(box.X + (285 - index * 32) * box.Width / 512),
            (float)(box.Y + 300 * box.Height / 512));
        return new(_slotDragonHeadArtwork, source, box,
            new((float)box.Y, eyes, SlotDragonHeadMouth, (float)box.Bottom));
    }

    private SlotDragonMorphFace SlotDragonOpenMorphFace(int index, int frame)
    {
        var registration = SlotDragonSwivelRegistrations[index * 5 + frame];
        var green = SlotDragonSwivelRegistrations[frame];
        var box = SlotDragonSwivelBox(index, frame);
        float crown = index == 0 ? 29 : index == 1 ? 337 : 641;
        float vertical = (registration.Neck.Y - crown) / (green.Neck.Y - 29);
        var greenEyes = SlotDragonGreenEyeCenters[frame];
        var eyes = new Vector2(greenEyes.X - green.Neck.X,
            (greenEyes.Y - green.Neck.Y) * vertical) * registration.Scale;
        float sx = (float)_slotDragonSwivelArtwork!.Size.Width / 1619;
        float sy = (float)_slotDragonSwivelArtwork.Size.Height / 971;
        var source = registration.Source;
        return new(_slotDragonSwivelArtwork,
            new(source.X * sx, source.Y * sy, source.Width * sx, source.Height * sy), box,
            new((float)box.Y, eyes, (registration.Mouth - registration.Neck) * registration.Scale, (float)box.Bottom));
    }

    private static SlotDragonMorphGeometry SlotDragonMorphInterpolate(SlotDragonMorphGeometry a,
        SlotDragonMorphGeometry b, float amount) => new(
            float.Lerp(a.Top, b.Top, amount), Vector2.Lerp(a.Eyes, b.Eyes, amount),
            Vector2.Lerp(a.Mouth, b.Mouth, amount), float.Lerp(a.Bottom, b.Bottom, amount));

    private static CanvasCommandList DrawSlotDragonMorphSource(CanvasDevice device, SlotDragonMorphFace face,
        SlotDragonMorphGeometry target)
    {
        var image = new CanvasCommandList(device);
        try
        {
            using var drawing = image.CreateDrawingSession();
            drawing.Transform = Matrix3x2.CreateScale(SlotDragonMorphResolution);
            // Adjacent strips share exact edges; antialiasing them separately
            // would attenuate the face along those internal boundaries.
            drawing.Antialiasing = CanvasAntialiasing.Aliased;
            var shape = face.Geometry;
            float eyeShift = target.Eyes.X - shape.Eyes.X;
            float mouthShift = target.Mouth.X - shape.Mouth.X;
            DrawBand(shape.Top, shape.Eyes.Y, target.Top, target.Eyes.Y, eyeShift, eyeShift, 1);
            DrawBand(shape.Eyes.Y, shape.Mouth.Y, target.Eyes.Y, target.Mouth.Y, eyeShift, mouthShift, 8);
            DrawBand(shape.Mouth.Y, shape.Bottom, target.Mouth.Y, target.Bottom, mouthShift, 0, 12);
            return image;

            void DrawBand(float fromY, float toY, float targetFromY, float targetToY,
                float fromShift, float toShift, int strips)
            {
                // All authored and interpolated row heights are strictly
                // ordered. x'=x+shift(y), y'=f(y) therefore has positive
                // Jacobian f'(y), irrespective of the amount of neck bend.
                for (int strip = 0; strip < strips; strip++)
                {
                    float u = (float)strip / strips, v = (float)(strip + 1) / strips;
                    float y0 = float.Lerp(fromY, toY, u), y1 = float.Lerp(fromY, toY, v);
                    float top = float.Lerp(targetFromY, targetToY, u), bottom = float.Lerp(targetFromY, targetToY, v);
                    float shift0 = float.Lerp(fromShift, toShift, SlotDragonPoseEase(u));
                    float shift1 = float.Lerp(fromShift, toShift, SlotDragonPoseEase(v));
                    float sourceY0 = (float)(face.Source.Y + (y0 - face.Local.Y) * face.Source.Height / face.Local.Height);
                    float sourceY1 = (float)(face.Source.Y + (y1 - face.Local.Y) * face.Source.Height / face.Local.Height);
                    float scaleX = (float)(face.Local.Width / face.Source.Width);
                    float scaleY = (bottom - top) / (sourceY1 - sourceY0);
                    float shear = (shift1 - shift0) / (sourceY1 - sourceY0);
                    var map = new Matrix3x2(scaleX, 0, shear, scaleY,
                        (float)face.Local.X + shift0 - (float)face.Source.X * scaleX - sourceY0 * shear,
                        top - sourceY0 * scaleY);
                    using var brush = new CanvasImageBrush(device, face.Bitmap)
                    {
                        Interpolation = CanvasImageInterpolation.HighQualityCubic,
                        Transform = map
                    };
                    using var geometry = CanvasGeometry.CreatePolygon(device,
                    [
                        new((float)face.Local.X + shift0, top), new((float)face.Local.Right + shift0, top),
                        new((float)face.Local.Right + shift1, bottom), new((float)face.Local.X + shift1, bottom)
                    ]);
                    drawing.FillGeometry(geometry, brush);
                }
            }
        }
        catch { image.Dispose(); throw; }
    }
}
