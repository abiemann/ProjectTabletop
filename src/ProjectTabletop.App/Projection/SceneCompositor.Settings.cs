using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // The menu's upper-right cog opens Settings, home of the Hand-Tracking tester.
    private const float SettingsCaptionSize = 28;

    private void DrawSettingsCogButton(CanvasDrawingSession ds, BoardButton button, bool hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        var rect = new Rect(button.Bounds.X * BoardSurfaceSize, button.Bounds.Y * BoardSurfaceSize,
            button.Bounds.Width * BoardSurfaceSize, button.Bounds.Height * BoardSurfaceSize);
        // A quiet secondary control: no status bar, bevel, sheen or shadow.
        ds.DrawRoundedRectangle(rect, 19, 19,
            hovered ? AppPalette.IndicatorOn : AppPalette.MetalEdge, 1.25f);
        var center = new Vector2((float)rect.X + 58, (float)(rect.Y + rect.Height / 2));
        float aspect = (float)PaintBoardAspect();
        var previous = ds.Transform;
        // Physically round on the stretched board.
        ds.Transform = Matrix3x2.CreateScale(1 / aspect, 1, center) * previous;
        try
        {
            using var cog = SettingsCogGeometry(ds.Device, center, 30);
            // Cyan to steel blue: the violet accent is reserved for the "06 / BOARDS"
            // caption, which verification uses as the menu's registration marker.
            using var metal = new CanvasLinearGradientBrush(ds.Device, AppPalette.IndicatorOn, ThemeColor(38, 104, 160))
            { StartPoint = center - new Vector2(30, 30), EndPoint = center + new Vector2(30, 30) };
            ds.FillGeometry(cog, metal);
            ds.DrawGeometry(cog, AppPalette.ButtonText, 1.2f);
            ds.FillCircle(center, 10, ThemeColor(20, 28, 40));
            ds.DrawCircle(center, 10, AppPalette.ButtonText, 1.2f);
        }
        finally { ds.Transform = previous; }
        using var format = SettingsCaptionFormat();
        ds.DrawText(button.Label, SettingsCaptionRect(button), AppPalette.ButtonText, format);
        DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, AppPalette.IndicatorOn, showCaption: false);
    }

    private static CanvasGeometry SettingsCogGeometry(ICanvasResourceCreator device, Vector2 center, float radius)
    {
        using var builder = new CanvasPathBuilder(device);
        const int teeth = 8;
        for (int index = 0; index < teeth * 4; index++)
        {
            // Each tooth: rise, top, fall, valley.
            float angle = index / (teeth * 4f) * MathF.Tau;
            float r = index % 4 is 1 or 2 ? radius : radius * .74f;
            var point = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            if (index == 0) builder.BeginFigure(point);
            else builder.AddLine(point);
        }
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static Rect SettingsCaptionRect(BoardButton button) => new(button.Bounds.X * BoardSurfaceSize + 96,
        button.Bounds.Y * BoardSurfaceSize, button.Bounds.Width * BoardSurfaceSize - 104, button.Bounds.Height * BoardSurfaceSize);

    private static CanvasTextFormat SettingsCaptionFormat() => new()
    {
        FontFamily = "Segoe UI", FontSize = SettingsCaptionSize, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static HandTrackingBounds SettingsCogTextRegion(CanvasDevice device, BoardButton button)
    {
        var rect = SettingsCaptionRect(button);
        using var format = SettingsCaptionFormat();
        using var layout = new CanvasTextLayout(device, button.Label, format, (float)rect.Width, (float)rect.Height);
        return ButtonInkRegion(button, layout.DrawBounds, rect.X, rect.Y);
    }

    // A transparent dragon fills the tile height with its head on the right,
    // breathing left into the existing diagonal fade beneath the caption.
    private void DrawSlotsMenuPreview(CanvasDrawingSession ds, float span)
    {
        EnsureSlotArtwork(ds.Device);
        ds.Clear(Microsoft.UI.Colors.Transparent);
        if (_slotMenuDragonArtwork is not { } dragon)
        {
            DrawSlotsMenuFallback(ds, span);
            return;
        }
        // Menu previews already use physically square units at native raster
        // density. Fit by height without stretching or reflecting the artwork;
        // the shared tile clip trims only the fading flame on narrow boards.
        const float height = MenuPreviewUnits - 6;
        double width = height * dragon.Size.Width / dragon.Size.Height;
        var destination = new Rect(span - width - 4, 3, width, height);
        ds.DrawImage(dragon, destination, new Rect(0, 0, dragon.Size.Width, dragon.Size.Height),
            1, CanvasImageInterpolation.HighQualityCubic);
    }

    // Retain the established illustrated reel preview if the optional menu
    // asset cannot load; the tile still identifies its destination clearly.
    private void DrawSlotsMenuFallback(CanvasDrawingSession ds, float span)
    {
        using var cave = new CanvasRadialGradientBrush(ds.Device, ThemeColor(96, 26, 30), ThemeColor(14, 6, 12))
        { Center = new(span - 90, 80), RadiusX = 220, RadiusY = 170 };
        ds.FillRectangle(new Rect(0, 0, span, MenuPreviewUnits), cave);
        var window = new Rect(span - 186, 26, 168, 108);
        ds.FillRoundedRectangle(new Rect(window.X - 7, window.Y - 7, window.Width + 14, window.Height + 14), 9, 9, SlotDeepGold);
        ds.DrawRoundedRectangle(new Rect(window.X - 7, window.Y - 7, window.Width + 14, window.Height + 14), 9, 9, SlotGold, 2);
        ds.FillRectangle(window, ThemeColor(26, 20, 46));
        SlotSymbol[,] symbols =
        {
            { SlotSymbol.Crown, SlotSymbol.Coin }, { SlotSymbol.Wild, SlotSymbol.EggRed }, { SlotSymbol.Coin, SlotSymbol.Crown }
        };
        for (int reel = 0; reel < 3; reel++)
        {
            if (reel > 0) ds.DrawLine((float)window.X + reel * 56, (float)window.Y, (float)window.X + reel * 56, (float)window.Bottom,
                ThemeColor(240, 201, 116, 120), 1);
            for (int row = 0; row < 2; row++)
            {
                var previous = ds.Transform;
                ds.Transform = Matrix3x2.CreateScale(.5f) *
                    Matrix3x2.CreateTranslation((float)window.X + 3 + reel * 56, (float)window.Y + 4 + row * 52) * previous;
                try { DrawSlotArt(ds, symbols[reel, row], new Rect(0, 0, 100, 100), 1); }
                finally { ds.Transform = previous; }
            }
        }
    }
}
