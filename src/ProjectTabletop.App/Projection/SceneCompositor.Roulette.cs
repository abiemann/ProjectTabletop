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
        var panel = new Rect(505, 149, 441, 297);
        ds.FillRoundedRectangle(panel, 16, 16, ThemeColor(5, 18, 24, 215));
        ds.DrawRoundedRectangle(panel, 16, 16, ThemeColor(122, 154, 143, 125), .9f);
        RouletteText(ds, spinning ? "NO MORE BETS" : "PLACE YOUR BETS", new Rect(525, 164, 400, 25),
            18, spinning ? RoulettePink : RouletteGold, aspect);
        ds.DrawLine(527, 202, 924, 202, ThemeColor(155, 133, 97, 140), .8f);
        RouletteText(ds, "CREDITS", new Rect(526, 214, 193, 17), 11, RouletteGold, aspect);
        RouletteText(ds, "ON THE TABLE", new Rect(732, 214, 190, 17), 11, RouletteGold, aspect);
        RouletteText(ds, game.Balance.ToString("N0"), new Rect(526, 237, 193, 44), 33, RouletteCream, aspect, "Georgia");
        RouletteText(ds, game.TotalBet.ToString("N0"), new Rect(732, 237, 190, 44), 33, RouletteCream, aspect, "Georgia");
        ds.DrawLine(724, 216, 724, 280, ThemeColor(155, 133, 97, 100), .8f);
        RouletteText(ds, game.Status, new Rect(526, 291, 397, 42), 14, RouletteCream, aspect);
        RouletteText(ds, "RECENT NUMBERS", new Rect(527, 345, 397, 17), 10, RouletteGold, aspect);
        int index = 0;
        foreach (var result in game.History.Take(8))
        {
            int number = result.Number;
            var box = new Rect(534 + index * 49, 374, 41, 42);
            Color color = number == 0 ? ThemeColor(17, 103, 77) : RouletteGame.IsRed(number)
                ? ThemeColor(135, 26, 57) : ThemeColor(19, 30, 37);
            ds.FillRoundedRectangle(box, 6, 6, color);
            ds.DrawRoundedRectangle(box, 6, 6, WithAlpha(RouletteGold, index == 0 ? (byte)230 : (byte)80), 1);
            RouletteText(ds, number.ToString(), box, 18, RouletteCream, aspect);
            index++;
        }
        if (index == 0) RouletteText(ds, "Your evening starts here", new Rect(532, 372, 392, 38), 15,
            ThemeColor(159, 178, 175), aspect, "Georgia");
    }

    private void DrawRouletteControls(CanvasDrawingSession ds, RouletteSnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, double aspect)
    {
        foreach (var button in buttons)
        {
            DrawRouletteButton(ds, button, game, hovered.Contains(button.Id), aspect);
            if (!button.IsHold) DrawButtonFingerSelectionFeedback(ds, button, feedback, RouletteGold);
        }
    }

    private static void DrawRouletteButton(CanvasDrawingSession ds, BoardButton button,
        RouletteSnapshot game, bool hovered, double aspect)
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
            using var sheen = new CanvasLinearGradientBrush(ds.Device,
                [new() { Position = 0, Color = primary && button.Enabled ? RouletteCream : ThemeColor(49, 67, 70) },
                 new() { Position = .35f, Color = fill }, new() { Position = 1, Color = fill }])
                { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
            ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 5, rect.Width, rect.Height), 19, 19, ThemeColor(0, 5, 9));
            ds.FillRoundedRectangle(rect, 19, 19, sheen);
            ds.DrawRoundedRectangle(rect, 19, 19, edge, 1.5f);
            RouletteText(ds, button.Label.ToUpperInvariant(), RouletteButtonTextRectangle(button), 23,
                !button.Enabled ? ThemeColor(134, 142, 139) : primary ? RouletteInk : RouletteCream, aspect);
            return;
        }
        if (chip)
        {
            decimal value = decimal.Parse(button.Id["roulette-chip-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
            DrawRouletteChip(ds, center, (float)rect.Height * .46f, value, aspect, selected: game.Chip == value);
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

    private HandTrackingBounds RouletteChipSearchRegion(BoardButton button)
    {
        // Keep the comfortable gesture hit target, but judge optical coverage
        // against the visible chip face rather than its empty horizontal padding.
        // This square sits inside the circular face and excludes the moving rim.
        var bounds = button.Bounds;
        double halfHeight = bounds.Height * .46 / Math.Sqrt(2);
        double halfWidth = halfHeight / PaintBoardAspect();
        return new(bounds.X + bounds.Width / 2 - halfWidth,
            bounds.Y + bounds.Height / 2 - halfHeight, halfWidth * 2, halfHeight * 2);
    }

    private HandTrackingBounds RouletteButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        var rect = RouletteButtonTextRectangle(button);
        float size = button.IsHold ? 23 : button.Id.StartsWith("roulette-number-") ? 22 : 13;
        if (button.Id.StartsWith("roulette-chip-", StringComparison.Ordinal))
        {
            var bounds = Pixels(button.Bounds, 0);
            double radius = bounds.Height * .46, aspect = PaintBoardAspect();
            rect = new(bounds.X + bounds.Width / 2 - radius / aspect,
                bounds.Y + bounds.Height / 2 - radius, radius * 2 / aspect, radius * 2);
            size = 14;
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
        double aspect, string family = "Bahnschrift", CanvasHorizontalAlignment alignment = CanvasHorizontalAlignment.Center)
    {
        float a = (float)Math.Clamp(aspect, .4, 3);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / a, 1) * Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y) * previous;
        try
        {
            using var format = RouletteTextFormat(size, family, alignment);
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
            using var format = RouletteTextFormat(stack ? 8 : 14);
            ds.DrawText(amount.ToString("0"), new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2),
                amount < 10 ? RouletteInk : RouletteCream, format);
        }
        finally { ds.Transform = previous; }
    }
}
