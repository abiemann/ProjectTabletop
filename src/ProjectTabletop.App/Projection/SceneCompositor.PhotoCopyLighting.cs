using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PhotoObjectTarget? _photoCopyObjectTarget;
    private DateTimeOffset _photoCopyObjectShownAt;

    public object PhotoCopyLightingStatus
    {
        get
        {
            lock (_gate)
            {
                SyncPhotoCopySession();
                var target = _photoCopyObjectTarget;
                var light = target?.Spotlight;
                return new { objectLocked = target is not null, revision = _photoCopyRevision,
                    center = target?.Center, radius = target?.SpotlightRadius,
                    spotlight = light is null ? null : new { shape = light.Shape.ToString(), light.Center,
                        light.Width, light.Height, light.RotationRadians, light.CornerRadius },
                    shownAt = _photoCopyObjectShownAt, foregroundArea = target?.ForegroundArea };
            }
        }
    }

    // Called on actual projector draws, independently of the cached UI surface.
    private void MarkPhotoCopySurfacePresented(DateTimeOffset now)
    {
        if (_photoCopySurfaceShownAt == DateTimeOffset.MinValue) _photoCopySurfaceShownAt = now;
        if (_photoCopyObjectTarget is not null && _photoCopyObjectShownAt == DateTimeOffset.MinValue)
            _photoCopyObjectShownAt = now;
    }

    public bool SetPhotoCopyObject(PhotoObjectTarget target, long revision)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            SyncPhotoCopySession();
            if (!PhotoCopyCaptureAllowed || revision != _photoCopyRevision ||
                _photoCopyCutout is not null || _photoCopyObjectTarget is not null) return false;
            _photoCopyObjectTarget = target;
            _photoCopyObjectShownAt = DateTimeOffset.MinValue;
            _photoCopyStatus = "Object locked and lit. Keep it still; pinch beside it to copy.";
            _renderedBoardState = null;
            return true;
        }
    }

    public bool ClearPhotoCopyObject(PhotoObjectTarget expected, long revision)
    {
        lock (_gate)
        {
            SyncPhotoCopySession();
            if (!PhotoCopyCaptureAllowed || revision != _photoCopyRevision ||
                !ReferenceEquals(expected, _photoCopyObjectTarget) || _photoCopyCutout is not null) return false;
            _photoCopyObjectTarget = null;
            _photoCopyObjectShownAt = DateTimeOffset.MinValue;
            // Wait for the grey surface to be presented and settle before a new
            // search; the departing light must not become a new object mask.
            _photoCopySurfaceShownAt = DateTimeOffset.MinValue;
            _photoCopyStatus = "Object moved or removed. Leave one object on grey to lock its light.";
            _renderedBoardState = null;
            return true;
        }
    }

    private void DrawPhotoCopyObjectSpotlight(CanvasDrawingSession surface)
    {
        if (!PhotoCopyCaptureAllowed || _photoCopyCutout is not null ||
            _photoCopyObjectTarget is not { } target) return;
        using var clip = surface.CreateLayer(1, new Rect(PhotoObjectTarget.CaptureLeft, PhotoObjectTarget.CaptureTop,
            PhotoObjectTarget.CaptureRight - PhotoObjectTarget.CaptureLeft,
            PhotoObjectTarget.CaptureBottom - PhotoObjectTarget.CaptureTop));
        var shape = target.Spotlight;
        if (shape.Shape == PhotoObjectSpotlightShape.RoundedRectangle)
        {
            var previousTransform = surface.Transform;
            surface.Transform = Matrix3x2.CreateRotation((float)shape.RotationRadians) *
                Matrix3x2.CreateTranslation((float)shape.Center.X, (float)shape.Center.Y) * previousTransform;
            try
            {
                var bounds = new Rect(-shape.Width / 2, -shape.Height / 2, shape.Width, shape.Height);
                var corner = (float)shape.CornerRadius;
                using var silhouette = new CanvasCommandList(surface.Device);
                using (var drawing = silhouette.CreateDrawingSession())
                    drawing.FillRoundedRectangle(bounds, corner, corner, Colors.White);
                using var edge = new GaussianBlurEffect { Source = silhouette, BlurAmount = 3,
                    BorderMode = EffectBorderMode.Soft };
                surface.DrawImage(edge);
                // Preserve the solid core needed by object-presence checks; only
                // the exterior rim is feathered into the grey capture field.
                surface.FillRoundedRectangle(bounds, corner, corner, Colors.White);
            }
            finally { surface.Transform = previousTransform; }
            return;
        }
        using var light = new CanvasRadialGradientBrush(surface.Device,
        [
            new CanvasGradientStop { Position = 0, Color = Colors.White },
            new CanvasGradientStop { Position = SpotlightCoreFraction, Color = Colors.White },
            new CanvasGradientStop { Position = 1, Color = Color.FromArgb(0, 255, 255, 255) }
        ]);
        float radius = (float)(target.SpotlightRadius / SpotlightCoreFraction);
        var center = new Vector2((float)shape.Center.X, (float)shape.Center.Y);
        light.Center = center;
        light.RadiusX = light.RadiusY = radius;
        surface.FillCircle(center, radius, light);
    }

    // Both light sources may illuminate the subject field, but the glass
    // navigation controls remain legible above it.
    private CanvasActiveLayer? ClipPhotoCopyHandLighting(CanvasDrawingSession drawing, Rect output)
    {
        if (_boardSession.Screen != BoardScreen.PhotoCopy || _boardSurfaceMap is null) return null;
        const double scale = 1.0 / PhotoHandCutout.BoardPixels;
        var points = new[] {
            new Point2(PhotoObjectTarget.CaptureLeft * scale, PhotoObjectTarget.CaptureTop * scale),
            new Point2(PhotoObjectTarget.CaptureRight * scale, PhotoObjectTarget.CaptureTop * scale),
            new Point2(PhotoObjectTarget.CaptureRight * scale, PhotoObjectTarget.CaptureBottom * scale),
            new Point2(PhotoObjectTarget.CaptureLeft * scale, PhotoObjectTarget.CaptureBottom * scale) }
            .Select(point => _boardSurfaceMap.Transform(point))
            .Select(point => new Vector2((float)(output.X + point.X * output.Width),
                (float)(output.Y + point.Y * output.Height))).ToArray();
        using var geometry = CanvasGeometry.CreatePolygon(drawing.Device, points);
        return drawing.CreateLayer(1, geometry);
    }
}
