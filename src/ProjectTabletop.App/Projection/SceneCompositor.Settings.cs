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
        DrawSecondaryPrecisionButtonSurface(ds, rect, hovered);
        var center = new Vector2((float)rect.X + 58, (float)(rect.Y + rect.Height / 2));
        float aspect = (float)PaintBoardAspect();
        var previous = ds.Transform;
        // Physically round on the stretched board.
        ds.Transform = Matrix3x2.CreateScale(1 / aspect, 1, center) * previous;
        try
        {
            using var cog = SettingsCogGeometry(ds.Device, center, 30);
            // Cyan to steel blue: the violet accent is reserved for the "07 / BOARDS"
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

}
