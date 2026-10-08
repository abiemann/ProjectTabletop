using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static bool IsVectorArrowHandle(BoardButton button) =>
        IsBoardDrawerHandle(button) || IsMenuScrollHandle(button) || IsCrownDeedDrawerHandle(button);

    private Rect BoardVectorArrowInk(BoardButton button) => IsCrownDeedDrawerHandle(button)
        ? CrownDeedDrawerArrowInk(button, PaintBoardAspect())
        : DrawerArrowInk(IsMenuScrollHandle(button) ? MenuArrowAppearance(button) : button, PaintBoardAspect());

    private static HandTrackingBounds BoardButtonPlateRegion(BoardButton button) =>
        new(button.Bounds.X + .012, button.Bounds.Y + .012,
            button.Bounds.Width - .024, button.Bounds.Height - .024);

    private HandTrackingBounds BoardButtonSearchRegion(BoardButton button)
    {
        if (_boardSession.Screen == BoardScreen.Roulette && button.Id.StartsWith("roulette-chip-", StringComparison.Ordinal))
            return RouletteChipSearchRegion(button);
        var plate = BoardButtonPlateRegion(button);
        if (!IsVectorArrowHandle(button)) return plate;
        var ink = BoardVectorArrowInk(button);
        // A compact chevron must be measured against its actual shape, rather
        // than the much larger pill. Twelve logical pixels retain the bounded
        // eight-pixel registration search plus its blur/ink margin. The full
        // opaque plate remains a separate lighting reference; its hit bounds,
        // rendering and seven-percent evidence floors are unchanged.
        const double margin = 12;
        double left = Math.Max(plate.X, (ink.X - margin) / BoardSurfaceSize);
        double top = Math.Max(plate.Y, (ink.Y - margin) / BoardSurfaceSize);
        double right = Math.Min(plate.X + plate.Width, (ink.Right + margin) / BoardSurfaceSize);
        double bottom = Math.Min(plate.Y + plate.Height, (ink.Bottom + margin) / BoardSurfaceSize);
        return new(left, top, right - left, bottom - top);
    }

    private HandTrackingBounds BoardButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        if (IsVectorArrowHandle(button)) return ButtonInkRegion(button, BoardVectorArrowInk(button), 0, 0);
        if (_boardSession.Screen == BoardScreen.Roulette) return RouletteButtonTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.Paint) return PaintButtonTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.WaterGarden) return WaterGardenButtonTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.Football) return FootballButtonTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.Slots) return SlotButtonTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.Menu && button.Id == "settings") return SettingsCogTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.Globe)
        {
            var rectangle = DrawerButtonTextRectangle(button);
            using var format = DrawerButtonTextFormat(button);
            using var layout = new CanvasTextLayout(device, button.Label, format,
                (float)rectangle.Width, (float)rectangle.Height);
            return ButtonInkRegion(button, layout.DrawBounds, rectangle.X, rectangle.Y);
        }
        if (_boardSession.Screen == BoardScreen.CrownDeed)
        {
            var rectangle = CrownDeedButtonTextRectangle(button);
            using var format = CrownDeedButtonTextFormat(button);
            using var layout = new CanvasTextLayout(device, button.Label, format,
                (float)rectangle.Width, (float)rectangle.Height);
            return ButtonInkRegion(button, layout.DrawBounds, rectangle.X, rectangle.Y);
        }
        if (_boardSession.Screen == BoardScreen.Blackjack)
        {
            var spec = CasinoButtonTextSpec(button);
            using var format = CasinoTextFormat(spec.Size, "Bahnschrift", true);
            using var layout = new CanvasTextLayout(device, spec.Caption, format,
                (float)spec.Bounds.Width, (float)spec.Bounds.Height);
            return ButtonInkRegion(button, layout.DrawBounds, spec.Bounds.X, spec.Bounds.Y);
        }
        if (_boardSession.Screen == BoardScreen.PhotoCopy)
        {
            var rectangle = DrawerButtonTextRectangle(button);
            using var format = DrawerButtonTextFormat(button);
            using var layout = new CanvasTextLayout(device, DrawerButtonCaption(button), format,
                (float)rectangle.Width, (float)rectangle.Height);
            return ButtonInkRegion(button, layout.DrawBounds, rectangle.X, rectangle.Y);
        }
        using var label = BoardButtonTextFormat();
        bool menu = _boardSession.Screen == BoardScreen.Menu ||
            _boardSession.Screen == BoardScreen.Settings && button.Destination == BoardScreen.HandTracking;
        double x = button.Bounds.X * BoardSurfaceSize + (menu ? 32 : 33);
        double y = button.Bounds.Y * BoardSurfaceSize + (menu ? 52 : 27);
        using var textLayout = new CanvasTextLayout(device, button.Label, label,
            (float)(button.Bounds.Width * BoardSurfaceSize), (float)(button.Bounds.Height * BoardSurfaceSize));
        return ButtonInkRegion(button, textLayout.DrawBounds, x, y);
    }

    private static HandTrackingBounds ButtonInkRegion(BoardButton button, Rect ink, double x, double y)
    {
        var b = button.Bounds;
        // Four logical pixels absorb bounded camera registration/raster blur.
        // Keep the actual label inside the same opaque control interior.
        double left = Math.Max(b.X + .012, (x + ink.X - 4) / BoardSurfaceSize);
        double top = Math.Max(b.Y + .012, (y + ink.Y - 4) / BoardSurfaceSize);
        double right = Math.Min(b.X + b.Width - .012, (x + ink.Right + 4) / BoardSurfaceSize);
        double bottom = Math.Min(b.Y + b.Height - .012, (y + ink.Bottom + 4) / BoardSurfaceSize);
        return new(left, top, right - left, bottom - top);
    }
}
