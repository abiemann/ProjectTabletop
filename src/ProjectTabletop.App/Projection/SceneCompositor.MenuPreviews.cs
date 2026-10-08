using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private MenuThumbnailImages? _menuPreviewImages;
    private bool _menuPreviewReady;

    internal sealed record MenuPreviewDiagnostics(bool Ready, int LoadedCount, string? Error);

    internal MenuPreviewDiagnostics GetMenuPreviewDiagnostics()
    {
        lock (_gate) return new(_menuPreviewImages?.IsReady == true,
            _menuPreviewImages?.LoadedCount ?? 0, _menuPreviewImages?.Error);
    }

    internal Task EnsureMenuPreviewResourcesAsync(CanvasDevice device)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetMenuPreviewImages(device).EnsureReadyAsync();
        }
    }

    private MenuThumbnailImages GetMenuPreviewImages(CanvasDevice device)
    {
        if (_menuPreviewImages is null || _menuPreviewImages.Device != device)
        {
            _menuPreviewImages?.Dispose();
            _menuPreviewImages = new(device);
            _menuPreviewReady = false;
            InvalidateMenuPreviewSurface();
        }
        return _menuPreviewImages;
    }

    private void PrepareMenuPreviews(CanvasDevice device)
    {
        var images = GetMenuPreviewImages(device);
        bool ready = images.IsReady;
        if (ready == _menuPreviewReady) return;
        _menuPreviewReady = ready;
        if (ready && images.Error is { } error)
            AppLog.Write("Menu thumbnails", new InvalidDataException(error));
        InvalidateMenuPreviewSurface();
    }

    private void InvalidateMenuPreviewSurface()
    {
        // Thumbnail completion changes only screens that display thumbnails.
        // A late background load must not reset another board's camera reference.
        if (_boardSession.Screen is not (BoardScreen.Menu or BoardScreen.Settings)) return;
        _renderedBoardState = null;
        _acquisitionScene = null;
        _acquisitionExpectedScene = null;
    }

    private void DrawMenuPreview(CanvasDrawingSession ds, Rect rect, Rect inside, float radius, BoardScreen screen)
    {
        if (!_menuPreviewReady) return;
        var image = screen == BoardScreen.Football ? FootballMenuThumbnail(ds.Device) : _menuPreviewImages?.Image(screen);
        if (image is null) return;
        // Keep the original diagonal sheen and rounded panel clip. Only the
        // destination artwork is baked; captions and hold rims remain live.
        var bounds = new Rect(rect.X + rect.Width * .28, rect.Y, rect.Width * .72, rect.Height);
        var along = Vector2.Normalize(new((float)(-rect.Width * .24), (float)rect.Height));
        var normal = new Vector2(-along.Y, along.X) * -1;
        float startFraction = screen == BoardScreen.Slots ? .50f : .40f;
        float fadeWidth = screen == BoardScreen.Slots ? .32f : .24f;
        var start = new Vector2((float)(rect.X + rect.Width * startFraction), (float)(rect.Y + rect.Height / 2));
        float distance = (float)(rect.Width * fadeWidth) * normal.X;
        using var fade = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = Color.FromArgb(0, 255, 255, 255) },
            new() { Position = .22f, Color = Color.FromArgb(14, 255, 255, 255) },
            new() { Position = .48f, Color = Color.FromArgb(82, 255, 255, 255) },
            new() { Position = .74f, Color = Color.FromArgb(190, 255, 255, 255) },
            new() { Position = 1, Color = Color.FromArgb(255, 255, 255, 255) }
        ]) { StartPoint = start, EndPoint = start + normal * distance };
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds.Device, inside, radius, radius);
        using (ds.CreateLayer(fade, clip)) DrawMenuThumbnail(ds, image, bounds);
    }

    private static void DrawMenuThumbnail(CanvasDrawingSession ds, CanvasBitmap image, Rect bounds)
    {
        // Physical board pixels are square even when logical board units are
        // not. Scale uniformly by physical height, and crop from the right,
        // preserving round Earth, chips, dice, and the dragon's proportions.
        var transform = ds.Transform;
        double xScale = new Vector2(transform.M11, transform.M12).Length();
        double yScale = new Vector2(transform.M21, transform.M22).Length();
        if (xScale <= 0 || yScale <= 0 || bounds.Height <= 0) return;
        double sourceWidth = bounds.Width * xScale / (bounds.Height * yScale) * image.Size.Height;
        if (sourceWidth <= image.Size.Width)
        {
            ds.DrawImage(image, bounds,
                new Rect(image.Size.Width - sourceWidth, 0, sourceWidth, image.Size.Height),
                1, CanvasImageInterpolation.HighQualityCubic);
            return;
        }
        // Extremely wide boards extend only the image's quiet left background;
        // the focal artwork retains the same physical scale and right anchor.
        double width = bounds.Width * image.Size.Width / sourceWidth;
        var art = new Rect(bounds.Right - width, bounds.Y, width, bounds.Height);
        ds.DrawImage(image, new Rect(bounds.X, bounds.Y, art.X - bounds.X, bounds.Height),
            new Rect(0, 0, 1, image.Size.Height));
        ds.DrawImage(image, art, new Rect(0, 0, image.Size.Width, image.Size.Height),
            1, CanvasImageInterpolation.HighQualityCubic);
    }

    private void DisposeMenuPreviews()
    {
        _menuPreviewImages?.Dispose();
        _menuPreviewImages = null;
        _menuPreviewReady = false;
    }
}
