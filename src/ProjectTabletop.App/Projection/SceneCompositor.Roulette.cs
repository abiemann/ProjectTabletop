using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly Color RouletteGold = ThemeColor(223, 191, 126);
    private static readonly Color RouletteCream = ThemeColor(255, 241, 213);
    private static readonly Color RoulettePink = ThemeColor(239, 120, 160);
    private static readonly Color RouletteInk = ThemeColor(8, 22, 28);
    private static float RouletteChipCaptionSize(double radius) => (float)Math.Min(22, radius * .78);
    private CanvasBitmap? _rouletteBackdrop;

    private void DrawRouletteBoard(CanvasDrawingSession ds, RouletteSnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, double aspect)
    {
        ds.Clear(RouletteInk);
        DrawRouletteBackdrop(ds);
        ds.FillRoundedRectangle(new Rect(49, 462, 903, 306), 18, 18, ThemeColor(6, 35, 35, 240));
        ds.DrawRoundedRectangle(new Rect(49, 462, 903, 306), 18, 18, ThemeColor(207, 181, 127, 160), 1.4f);
        ds.DrawRoundedRectangle(new Rect(11, 11, 978, 978), 19, 19, RouletteGold, 1.6f);
        ds.DrawRoundedRectangle(new Rect(17, 17, 966, 966), 15, 15, ThemeColor(153, 120, 78, 130), .8f);
        var titleBounds = new Rect(45, 29, 555, 65);
        using var titleFormat = RouletteTextFormat(53, "Georgia", CanvasHorizontalAlignment.Left);
        using var titleLayout = new CanvasTextLayout(ds.Device, "VICE ROYALE", titleFormat, 2000, 65);
        float titleSize = (float)(53 * Math.Min(1, (titleBounds.Width * aspect - 4) / titleLayout.DrawBounds.Width));
        RouletteText(ds, "VICE ROYALE", titleBounds, titleSize, RouletteCream,
            aspect, "Georgia", CanvasHorizontalAlignment.Left);
        RouletteText(ds, "R O U L E T T E", new Rect(49, 91, 390, 22), 14, RoulettePink,
            aspect, alignment: CanvasHorizontalAlignment.Left);
        RouletteText(ds, "EUROPEAN  /  SINGLE ZERO", new Rect(630, 53, 320, 20), 12, RouletteGold, aspect);
        DrawRouletteSummary(ds, game, aspect);
        DrawRouletteControls(ds, game, buttons, hovered, feedback, aspect);
    }

    private void DrawRouletteBackdrop(CanvasDrawingSession ds)
    {
        PrepareRouletteResources(ds.Device);
        if (_rouletteBackdrop is { } art)
        {
            double side = Math.Min(art.Size.Width, art.Size.Height);
            ds.DrawImage(art, new Rect(0, 0, 1000, 1000),
                new Rect((art.Size.Width - side) / 2, (art.Size.Height - side) / 2, side, side),
                1, CanvasImageInterpolation.HighQualityCubic);
        }
        // These are physical smoked-glass plates, not fades over the silhouette
        // of the artwork. Stationary captions always have opaque surfaces.
        ds.FillRoundedRectangle(new Rect(26, 23, 948, 91), 16, 16, ThemeColor(4, 16, 24, 225));
        ds.FillRoundedRectangle(new Rect(20, 848, 960, 131), 18, 18, ThemeColor(5, 17, 23));
    }

    private static void DrawRouletteSummary(CanvasDrawingSession ds, RouletteSnapshot game, double aspect)
    {
        bool spinning = game.Phase == RoulettePhase.Spinning;
        var panel = new Rect(400, 149, 250, 297);
        ds.FillRoundedRectangle(panel, 16, 16, ThemeColor(5, 18, 24, 215));
        ds.DrawRoundedRectangle(panel, 16, 16, ThemeColor(122, 154, 143, 125), .9f);
        RouletteText(ds, spinning ? "NO MORE BETS" : "PLACE YOUR BETS", new Rect(410, 164, 230, 25),
            16, spinning ? RoulettePink : RouletteGold, aspect, fitWidth: true);
        ds.DrawLine(417, 202, 633, 202, ThemeColor(155, 133, 97, 140), .8f);
        RouletteText(ds, "CREDITS", new Rect(410, 211, 230, 17), 11, RouletteGold, aspect);
        RouletteText(ds, game.Balance.ToString("N0"), new Rect(410, 230, 230, 39), 31, RouletteCream, aspect, "Georgia");
        RouletteText(ds, "ON THE TABLE", new Rect(410, 278, 230, 17), 11, RouletteGold, aspect);
        RouletteText(ds, game.TotalBet.ToString("N0"), new Rect(410, 297, 230, 39), 31, RouletteCream, aspect, "Georgia");
        RouletteText(ds, "RECENT NUMBERS", new Rect(410, 348, 230, 17), 10, RouletteGold, aspect);
        int index = 0;
        foreach (var result in game.History.Take(8))
        {
            int number = result.Number;
            var box = new Rect(416 + index % 4 * 55, 375 + index / 4 * 32, 52, 28);
            Color color = number == 0 ? ThemeColor(17, 103, 77) : RouletteGame.IsRed(number)
                ? ThemeColor(135, 26, 57) : ThemeColor(19, 30, 37);
            ds.FillRoundedRectangle(box, 6, 6, color);
            ds.DrawRoundedRectangle(box, 6, 6, WithAlpha(RouletteGold, index == 0 ? (byte)230 : (byte)80), 1);
            RouletteText(ds, number.ToString(), box, 16, RouletteCream, aspect);
            index++;
        }
        if (index == 0) RouletteText(ds, "Your evening starts here", new Rect(410, 375, 230, 38), 11,
            ThemeColor(159, 178, 175), aspect, "Georgia", fitWidth: true);

        var chips = new Rect(680, 149, 290, 297);
        ds.FillRoundedRectangle(chips, 16, 16, ThemeColor(5, 18, 24, 215));
        ds.DrawRoundedRectangle(chips, 16, 16, ThemeColor(122, 154, 143, 125), .9f);
        RouletteText(ds, "CHIP VALUE", new Rect(695, 157, 260, 20), 11, RouletteGold, aspect);
        ds.FillRoundedRectangle(new Rect(49, 780, 903, 52), 12, 12, ThemeColor(5, 18, 24, 215));
        RouletteText(ds, game.Status, new Rect(69, 785, 863, 42), 14, RouletteCream, aspect);
    }

    private void DrawRouletteControls(CanvasDrawingSession ds, RouletteSnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, double aspect)
    {
        if (buttons.Count == 0) return;
        foreach (var button in buttons)
        {
            DrawRouletteButton(ds, button, game, hovered.Contains(button.Id), aspect, _holdControlBrightness);
            if (!button.IsHold) DrawButtonFingerSelectionFeedback(ds, button, feedback, RouletteGold);
        }
    }

    private static void DrawRouletteButton(CanvasDrawingSession ds, BoardButton button,
        RouletteSnapshot game, bool hovered, double aspect, double holdExposure = 1)
    {
        var rect = Pixels(button.Bounds, 0);
        Color fill = ThemeColor(13, 65, 62), edge = ThemeColor(158, 166, 127, 180);
        bool chip = button.Id.StartsWith("roulette-chip-", StringComparison.Ordinal);
        bool number = button.Id.StartsWith("roulette-number-", StringComparison.Ordinal);
        if (button.Id == "roulette-red" || number && int.TryParse(button.Label, out int n) && RouletteGame.IsRed(n))
            fill = ThemeColor(134, 27, 54);
        else if (button.Id == "roulette-black" || number && button.Label != "0") fill = ThemeColor(13, 25, 30);
        if (button.IsHold)
        {
            bool primary = button.Id is "roulette-spin" or "roulette-refill";
            fill = primary ? ThemeColor(222, 189, 124) : ThemeColor(17, 37, 42);
            edge = primary ? RouletteCream : RouletteGold;
            if (!button.Enabled) { fill = ThemeColor(29, 37, 39); edge = ThemeColor(80, 87, 81); }
            // Exposure protection dims the light caption element: the plate on
            // primary actions, otherwise the cream lettering on the dark plate.
            double plate = primary ? holdExposure : 1, lettering = primary ? 1 : holdExposure;
            fill = HoldExposure(fill, plate);
            using var sheen = new CanvasLinearGradientBrush(ds.Device,
                [new() { Position = 0, Color = HoldExposure(primary && button.Enabled ? RouletteCream : ThemeColor(49, 67, 70), plate) },
                 new() { Position = .35f, Color = fill }, new() { Position = 1, Color = fill }])
                { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
            ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 5, rect.Width, rect.Height), 19, 19, ThemeColor(0, 5, 9));
            ds.FillRoundedRectangle(rect, 19, 19, sheen);
            ds.DrawRoundedRectangle(rect, 19, 19, edge, 1.5f);
            DrawHoldButtonCaption(ds, button.Label, RouletteButtonTextRectangle(button),
                HoldExposure(!button.Enabled ? ThemeColor(134, 142, 139) : primary ? RouletteInk : RouletteCream, lettering), aspect);
            return;
        }
        if (chip)
        {
            decimal value = decimal.Parse(button.Id["roulette-chip-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
            DrawRouletteChip(ds, center, (float)(RouletteChipRadius(button, aspect) * BoardSurfaceSize),
                value, aspect, selected: game.Chip == value);
            if (hovered) ds.DrawRoundedRectangle(rect, 8, 8, RouletteCream, 2);
            return;
        }
        ds.FillRoundedRectangle(rect, 4, 4, fill);
        ds.DrawRoundedRectangle(rect, 4, 4, hovered ? RouletteCream : edge, hovered ? 2.4f : .8f);
        RouletteText(ds, button.Label.ToUpperInvariant(), RouletteButtonTextRectangle(button), number ? 22 : 13,
            button.Enabled ? RouletteCream : ThemeColor(137, 159, 153), aspect);
        if (game.Bets.FirstOrDefault(bet => bet.Id == button.Id) is { Amount: > 0 } bet)
        {
            // Stakes sit away from the number's centre so it stays readable.
            var center = new Vector2((float)(rect.Right - 10 / aspect), (float)(rect.Bottom - 10));
            DrawRouletteChip(ds, center, 8.5f, bet.Amount, aspect, selected: false, stack: true);
        }
        if (game.Phase != RoulettePhase.Spinning && game.Outcome is int result && number && button.Label == result.ToString())
            ds.DrawRoundedRectangle(new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), 3, 3, RouletteGold, 2.5f);
    }

    private static Rect RouletteButtonTextRectangle(BoardButton button)
    {
        var rect = Pixels(button.Bounds, 0);
        return new(rect.X + 5, rect.Y + 3, rect.Width - 10, rect.Height - 6);
    }

    // Keep the physical chip inside its gesture target on wide and narrow boards.
    // Drawing, caption bounds and optical coverage all use this same face size.
    private static double RouletteChipRadius(BoardButton button, double aspect) =>
        Math.Min(button.Bounds.Height, button.Bounds.Width * aspect) * .46;

    private HandTrackingBounds RouletteChipFaceRegion(BoardButton button)
    {
        // The caption supplies acquisition evidence; this separate colour
        // reference stays inside the circular face and excludes its rim.
        var bounds = button.Bounds;
        double halfHeight = RouletteChipRadius(button, PaintBoardAspect()) / Math.Sqrt(2);
        double halfWidth = halfHeight / PaintBoardAspect();
        return new(bounds.X + bounds.Width / 2 - halfWidth,
            bounds.Y + bounds.Height / 2 - halfHeight, halfWidth * 2, halfHeight * 2);
    }

    private HandTrackingBounds RouletteButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        var rect = RouletteButtonTextRectangle(button);
        if (button.IsHold) return HoldButtonCaptionRegion(device, button, rect);
        float size = button.Id.StartsWith("roulette-number-") ? 22 : 13;
        if (button.Id.StartsWith("roulette-chip-", StringComparison.Ordinal))
        {
            var bounds = Pixels(button.Bounds, 0);
            double aspect = PaintBoardAspect(), radius = RouletteChipRadius(button, aspect) * BoardSurfaceSize;
            rect = new(bounds.X + bounds.Width / 2 - radius / aspect,
                bounds.Y + bounds.Height / 2 - radius, radius * 2 / aspect, radius * 2);
            size = RouletteChipCaptionSize(radius);
        }
        using var format = RouletteTextFormat(size);
        using var layout = new CanvasTextLayout(device, button.Label.ToUpperInvariant(), format,
            (float)(rect.Width * PaintBoardAspect()), (float)rect.Height);
        var ink = layout.DrawBounds;
        ink.X /= PaintBoardAspect(); ink.Width /= PaintBoardAspect();
        return ButtonInkRegion(button, ink, rect.X, rect.Y);
    }

    private static CanvasTextFormat RouletteTextFormat(float size, string family = "Bahnschrift",
        CanvasHorizontalAlignment alignment = CanvasHorizontalAlignment.Center) => new()
    {
        FontFamily = family, FontSize = size, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = alignment, VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void RouletteText(CanvasDrawingSession ds, string text, Rect rect, float size, Color color,
        double aspect, string family = "Bahnschrift", CanvasHorizontalAlignment alignment = CanvasHorizontalAlignment.Center,
        bool fitWidth = false)
    {
        float a = (float)Math.Clamp(aspect, .4, 3);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / a, 1) * Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y) * previous;
        try
        {
            using var format = RouletteTextFormat(size, family, alignment);
            if (fitWidth)
            {
                using var layout = new CanvasTextLayout(ds.Device, text, format, 2000, (float)rect.Height);
                format.FontSize *= (float)Math.Min(1, Math.Max(1, rect.Width * a - 4) / Math.Max(1, layout.DrawBounds.Width));
            }
            ds.DrawText(text, new Rect(0, 0, rect.Width * a, rect.Height), color, format);
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawRouletteChip(CanvasDrawingSession ds, Vector2 center, float radius, decimal amount,
        double aspect, bool selected, bool stack = false)
    {
        Color face = amount < 10 ? ThemeColor(232, 232, 213) : amount < 50 ? ThemeColor(181, 46, 86)
            : amount < 100 ? ThemeColor(22, 132, 121) : ThemeColor(53, 40, 76);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)(1 / aspect), 1, center) * previous;
        try
        {
            ds.FillCircle(center + new Vector2(0, stack ? 3 : 4), radius + 1, ThemeColor(1, 6, 10, 195));
            ds.FillCircle(center, radius, face);
            for (int i = 0; i < 10; i++)
            {
                float angle = i * MathF.Tau / 10;
                var radial = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                ds.DrawLine(center + radial * radius * .79f, center + radial * radius * .97f, RouletteCream, radius * .16f);
            }
            ds.DrawCircle(center, radius * .67f, ThemeColor(255, 243, 205, 160), .7f);
            if (selected) ds.DrawCircle(center, radius + 4, RouletteGold, 1.8f);
            using var format = RouletteTextFormat(stack ? 8 : RouletteChipCaptionSize(radius));
            ds.DrawText(amount.ToString("0"), new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2),
                amount < 10 ? RouletteInk : RouletteCream, format);
        }
        finally { ds.Transform = previous; }
    }
}
