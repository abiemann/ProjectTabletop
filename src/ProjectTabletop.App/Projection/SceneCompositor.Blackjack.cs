using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly Color CasinoGold = ThemeColor(224, 194, 124);
    private static readonly Color CasinoIvory = ThemeColor(248, 243, 224);
    private static readonly Color CasinoMuted = ThemeColor(157, 188, 168);

    /// <summary>One perspective-correct table; card and button positions use the same board coordinates.</summary>
    private static void DrawBlackjackTable(CanvasDrawingSession ds, BlackjackSnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        IReadOnlyDictionary<int, IReadOnlySet<int>>? flyingPlayerCards = null)
    {
        DrawCasinoFelt(ds);

        CasinoText(ds, "T A B L E T O P", new Rect(310, 53, 380, 24), 18, CasinoGold);
        CasinoText(ds, "BLACKJACK", new Rect(310, 80, 380, 51), 40, CasinoIvory,
            "Bahnschrift SemiCondensed", true);
        DrawCasinoDiamond(ds, new Vector2(326, 142), 4, CasinoGold);
        ds.DrawLine(339, 142, 465, 142, ThemeColor(224, 194, 124, 110), 1);
        ds.DrawLine(535, 142, 661, 142, ThemeColor(224, 194, 124, 110), 1);
        DrawCasinoSuit(ds, BlackjackSuit.Spades, new Vector2(500, 141), 10, CasinoGold);
        DrawCasinoDiamond(ds, new Vector2(674, 142), 4, CasinoGold);

        DrawCasinoPlaque(ds, new Rect(742, 55, 198, 90));
        CasinoText(ds, "YOUR CHIPS", new Rect(752, 65, 178, 23), 15, CasinoGold);
        CasinoText(ds, CasinoAmount(game.Bankroll), new Rect(752, 88, 178, 43), 31, CasinoIvory,
            "Bahnschrift", true);

        bool dealt = game.DealerCards.Count != 0;
        var dealerLabel = !dealt ? "DEALER" : game.DealerHoleCardHidden
            ? $"DEALER  ·  {game.DealerTotal} SHOWING"
            : $"DEALER  ·  {game.DealerTotal}{(game.DealerIsSoft ? " SOFT" : "")}";
        CasinoText(ds, dealerLabel, new Rect(260, 181, 480, 29), 19, CasinoMuted,
            "Bahnschrift", true);
        if (dealt)
            DrawCasinoCards(ds, game.DealerCards, new Rect(215, 224, 570, 159));
        else
        {
            DrawCasinoCardWell(ds, new Rect(377, 224, 112, 154));
            DrawCasinoCardWell(ds, new Rect(509, 224, 112, 154));
        }

        // The dealer's traditional pay-line remains legible between card areas.
        ds.DrawLine(122, 414, 329, 414, ThemeColor(224, 194, 124, 105), 1);
        ds.DrawLine(671, 414, 878, 414, ThemeColor(224, 194, 124, 105), 1);
        CasinoText(ds, "BLACKJACK PAYS 3 : 2", new Rect(322, 399, 356, 29), 20, CasinoGold,
            "Bahnschrift", true);

        Color statusColor = game.Phase == BlackjackPhase.RoundOver ? CasinoGold : CasinoIvory;
        using (var statusFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 24, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap
        })
            ds.DrawText(game.Status, new Rect(112, 441, 776, 62), statusColor, statusFormat);

        if (game.Hands.Count == 0)
        {
            DrawCasinoCardWell(ds, new Rect(377, 526, 112, 154));
            DrawCasinoCardWell(ds, new Rect(509, 526, 112, 154));
            DrawCasinoChipStack(ds, new Vector2(725, 617), 34, game.SelectedBet);
            CasinoText(ds, "YOUR HAND", new Rect(300, 706, 400, 27), 18, CasinoMuted,
                "Bahnschrift", true);
        }
        else
        {
            bool split = game.Hands.Count > 1;
            for (int index = 0; index < game.Hands.Count; index++)
            {
                var hand = game.Hands[index];
                var lane = CasinoPlayerLane(index, game.Hands.Count);
                if (hand.IsActive)
                {
                    ds.FillRoundedRectangle(new Rect(lane.X - 6, 516, lane.Width + 12, 226),
                        20, 20, ThemeColor(110, 192, 139, 10));
                    ds.DrawRoundedRectangle(new Rect(lane.X - 6, 516, lane.Width + 12, 226),
                        20, 20, ThemeColor(224, 194, 124, 110), 1.5f);
                    ds.FillCircle(new Vector2((float)lane.X + 18, 722), 3.5f, CasinoGold);
                }
                var hidden = flyingPlayerCards is not null && flyingPlayerCards.TryGetValue(index, out var indices)
                    ? indices : null;
                DrawCasinoCards(ds, hand.Cards.Select(card => (BlackjackCard?)card).ToArray(), lane, hidden);
                string total = hand.IsBust ? $"BUST · {hand.Total}"
                    : hand.IsNatural ? "BLACKJACK" : $"{hand.Total}{(hand.IsSoft ? " SOFT" : "")}";
                string handName = split ? $"HAND {index + 1}" : "YOU";
                string caption = game.Phase == BlackjackPhase.RoundOver && !string.IsNullOrWhiteSpace(hand.Result)
                    ? $"{(hand.IsNatural ? handName : total)}  ·  {hand.Result.ToUpperInvariant()}"
                    : $"{handName}  ·  {total}  ·  BET {CasinoAmount(hand.Bet)}";
                CasinoText(ds, caption, new Rect(lane.X + 18, 704, lane.Width - 36, 31),
                    split ? 17 : 20, hand.IsBust ? ThemeColor(246, 161, 147) : CasinoIvory,
                    "Bahnschrift", true);
            }
        }

        bool betting = game.Phase is BlackjackPhase.Betting or BlackjackPhase.RoundOver;
        CasinoText(ds, FingerSelectionCaption(selectionFeedback, betting
                ? $"BET {CasinoAmount(game.SelectedBet)} · Bring fingers together" : "Bring fingers together"),
            new Rect(80, 742, 840, 24), 15, CasinoGold, "Bahnschrift", true);
        foreach (var button in buttons)
        {
            DrawCasinoButton(ds, button, game.AvailableActions.Contains(button.Id) || button.Id == "menu",
                hovered.Contains(button.Id), game.SelectedBet);
            DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, CasinoGold);
        }

        CasinoText(ds, "PRACTICE TABLE  ·  NO REAL MONEY", new Rect(80, 887, 840, 23), 15, CasinoGold,
            "Bahnschrift", true);
        CasinoText(ds, "Dealer stands on soft 17  ·  One split  ·  No insurance or surrender",
            new Rect(65, 911, 870, 24), 16, CasinoMuted);
        CasinoText(ds, "Four fingers together. Aim with middle; move index sideways. Or pinch.",
            new Rect(75, 934, 850, 19), 14, CasinoMuted);
    }

    private static string CasinoAmount(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);

    private static void DrawCasinoFelt(CanvasDrawingSession ds)
    {
        ds.Clear(ThemeColor(10, 14, 13));
        using var rail = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(77, 49, 35) },
            new() { Position = .15f, Color = ThemeColor(33, 24, 23) },
            new() { Position = .52f, Color = ThemeColor(21, 20, 19) },
            new() { Position = .94f, Color = ThemeColor(59, 39, 30) },
            new() { Position = 1, Color = ThemeColor(102, 67, 44) }
        ]) { StartPoint = new(0, 0), EndPoint = new(180, 1000) };
        ds.FillRoundedRectangle(new Rect(14, 14, 972, 972), 88, 88, rail);
        ds.DrawRoundedRectangle(new Rect(21, 21, 958, 958), 83, 83, ThemeColor(212, 180, 118, 90), 1);
        using var felt = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(27, 104, 76) },
            new() { Position = .55f, Color = ThemeColor(17, 75, 56) },
            new() { Position = 1, Color = ThemeColor(8, 43, 34) }
        ]) { Center = new(500, 385), RadiusX = 670, RadiusY = 830 };
        var inner = new Rect(39, 39, 922, 938);
        ds.FillRoundedRectangle(inner, 60, 60, ThemeColor(1, 16, 12));
        ds.FillRoundedRectangle(new Rect(44, 44, 912, 928), 56, 56, felt);
        using var feltClip = CanvasGeometry.CreateRoundedRectangle(ds.Device,
            new Rect(44, 44, 912, 928), 56, 56);
        using (ds.CreateLayer(1, feltClip))
        {
            // Fine woven felt, cached with the board image rather than animated noise.
            for (float y = 48; y < 974; y += 5)
                ds.DrawLine(44, y, 956, y, ThemeColor(165, 201, 168, 5), .55f);
            for (float x = 47; x < 957; x += 5)
                ds.DrawLine(x, 44, x, 972, ThemeColor(0, 14, 9, 10), .55f);
            for (int y = 52; y < 970; y += 14)
                for (int x = 49 + (y % 3) * 3; x < 956; x += 19)
                    ds.DrawLine(x, y, x + 2, y + 1, ThemeColor(188, 214, 174, 6), .7f);
        }
        ds.DrawRoundedRectangle(new Rect(47, 47, 906, 922), 53, 53, ThemeColor(214, 181, 108, 160), 1.2f);
        ds.DrawRoundedRectangle(new Rect(52, 52, 896, 912), 49, 49, ThemeColor(224, 194, 124, 45), .75f);
        foreach (float x in new[] { 26f, 974f })
        foreach (float y in new[] { 184f, 374f, 564f, 754f })
            DrawCasinoDiamond(ds, new Vector2(x, y), 3.5f, ThemeColor(225, 196, 133, 120));
        // Printed arc recalls a casino's curved dealer line while leaving room for cards.
        using var arcPath = new CanvasPathBuilder(ds.Device);
        arcPath.BeginFigure(new Vector2(103, 320));
        arcPath.AddCubicBezier(new Vector2(105, 486), new Vector2(895, 486), new Vector2(897, 320));
        arcPath.EndFigure(CanvasFigureLoop.Open);
        using var arc = CanvasGeometry.CreatePath(arcPath);
        ds.DrawGeometry(arc, ThemeColor(222, 192, 120, 65), 2);
    }

    private static void DrawCasinoPlaque(CanvasDrawingSession ds, Rect rect)
    {
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 4, rect.Width, rect.Height),
            12, 12, ThemeColor(0, 12, 9, 115));
        ds.FillRoundedRectangle(rect, 12, 12, ThemeColor(13, 36, 29));
        ds.DrawRoundedRectangle(rect, 12, 12, ThemeColor(224, 194, 124, 100), 1);
        ds.DrawLine((float)rect.X + 18, (float)rect.Y + 2, (float)rect.Right - 18, (float)rect.Y + 2,
            ThemeColor(247, 229, 187, 75), 1);
    }

    private static void DrawCasinoButton(CanvasDrawingSession ds, BoardButton button, bool enabled,
        bool hovered, decimal selectedBet)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * 1000, bounds.Y * 1000, bounds.Width * 1000, bounds.Height * 1000);
        bool bet = button.Id.StartsWith("bj-bet-", StringComparison.Ordinal);
        bool selected = bet && decimal.TryParse(button.Id[7..], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var amount) && amount == selectedBet;
        bool primary = button.Id == "bj-deal";
        bool isMenu = button.Id == "menu";
        bool reset = button.Id == "bj-reset";
        hovered &= enabled;
        Color top = !enabled ? ThemeColor(30, 52, 43) : primary ? ThemeColor(235, 208, 148)
            : hovered ? ThemeColor(51, 99, 77) : ThemeColor(28, 61, 48);
        Color bottom = !enabled ? ThemeColor(21, 40, 32) : primary ? ThemeColor(179, 140, 74)
            : hovered ? ThemeColor(28, 70, 53) : ThemeColor(14, 37, 29);
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 5, rect.Width, rect.Height),
            13, 13, ThemeColor(0, 12, 7, 120));
        using var finish = new CanvasLinearGradientBrush(ds.Device, top, bottom)
        { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, 13, 13, finish);
        Color edge = hovered || selected ? CasinoGold : enabled ? ThemeColor(136, 155, 114) : ThemeColor(62, 81, 61);
        ds.DrawRoundedRectangle(rect, 13, 13, edge, hovered || selected ? 2.5f : 1);
        if (hovered)
            ds.DrawRoundedRectangle(new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6),
                16, 16, ThemeColor(225, 196, 133, 55), 3);
        Color ink = !enabled ? ThemeColor(125, 146, 132) : primary ? ThemeColor(33, 30, 17) : CasinoIvory;
        if (bet)
        {
            Color chipColor = button.Id switch
            {
                "bj-bet-10" => ThemeColor(37, 93, 153),
                "bj-bet-25" => ThemeColor(51, 117, 76),
                "bj-bet-50" => ThemeColor(147, 60, 70),
                _ => ThemeColor(47, 47, 54)
            };
            DrawCasinoChip(ds, new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2)),
                32, chipColor, button.Id[7..], !enabled);
            if (selected)
                ds.FillCircle(new Vector2((float)rect.Right - 11, (float)rect.Y + 12), 3.5f, CasinoGold);
        }
        else
        {
            string caption = button.Id switch
            {
                "bj-deal" => "DEAL", "bj-hit" => "HIT", "bj-stand" => "STAND",
                "bj-double" => "DOUBLE", "bj-split" => "SPLIT", "bj-reset" => "Reset chips",
                "menu" => "‹  Back to menu", _ => button.Label
            };
            CasinoText(ds, caption, new Rect(rect.X + 8, rect.Y + (primary ? -7 : 0), rect.Width - 16, rect.Height),
                isMenu ? 21 : reset ? 18 : 27, ink, "Bahnschrift", true);
            if (primary)
                CasinoText(ds, "NEW HAND", new Rect(rect.X + 8, rect.Bottom - 33, rect.Width - 16, 22), 13,
                    ThemeColor(63, 55, 29), "Bahnschrift", true);
        }
    }

    private static void DrawCasinoCardWell(CanvasDrawingSession ds, Rect rect)
    {
        ds.FillRoundedRectangle(rect, 9, 9, ThemeColor(3, 37, 26, 40));
        ds.DrawRoundedRectangle(rect, 9, 9, ThemeColor(208, 190, 139, 55), 1.5f);
        DrawCasinoSuit(ds, BlackjackSuit.Spades,
            new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2)),
            20, ThemeColor(214, 190, 130, 26));
    }

    private static Rect CasinoPlayerLane(int handIndex, int handCount) => handCount > 1
        ? new Rect(handIndex == 0 ? 83 : 518, 526, 399, 166)
        : new Rect(150, 526, 700, 166);

    // Resting cards and their flying overlays share exactly the same destination
    // rectangles, including the long-hand row transition and split-hand centering.
    private static IReadOnlyList<Rect> CasinoCardLayout(int count, Rect lane)
    {
        if (count <= 0) return Array.Empty<Rect>();
        var layout = new Rect[count];
        // Very long hands use two overlapping rows: top-left indices stay exposed
        // instead of compressing nineteen cards into unreadable one-pixel slivers.
        int rows = count > (lane.Width < 450 ? 8 : 11) ? 2 : 1;
        int rowCount = (count + rows - 1) / rows;
        float width = rows == 2 ? 88 : 112;
        float height = width * 154 / 112;
        for (int row = 0; row < rows; row++)
        {
            int start = row * rowCount;
            int rowSize = Math.Min(rowCount, count - start);
            if (rowSize <= 0) continue;
            float step = rowSize == 1 ? 0 : Math.Min(width + 14, ((float)lane.Width - width) / (rowSize - 1));
            float occupied = width + (rowSize - 1) * step;
            float left = (float)(lane.X + (lane.Width - occupied) / 2);
            for (int index = 0; index < rowSize; index++)
                layout[start + index] = new Rect(left + index * step,
                    lane.Y + row * 43, width, height);
        }
        return Array.AsReadOnly(layout);
    }

    private static void DrawCasinoCards(CanvasDrawingSession ds, IReadOnlyList<BlackjackCard?> cards, Rect lane,
        IReadOnlySet<int>? hiddenIndices = null)
    {
        var layout = CasinoCardLayout(cards.Count, lane);
        for (int index = 0; index < cards.Count; index++)
            if (hiddenIndices?.Contains(index) != true)
                DrawCasinoCard(ds, cards[index], layout[index]);
    }

    private static void DrawCasinoCard(CanvasDrawingSession ds, BlackjackCard? card, Rect rect)
    {
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)rect.Width / 112) *
            Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y) * previous;
        try
        {
            ds.FillRoundedRectangle(new Rect(1, 5, 112, 154), 9, 9, ThemeColor(0, 12, 6, 125));
            using var paper = new CanvasLinearGradientBrush(ds.Device, ThemeColor(255, 253, 240), ThemeColor(235, 232, 216))
            { StartPoint = new(0, 0), EndPoint = new(110, 154) };
            ds.FillRoundedRectangle(new Rect(0, 0, 112, 154), 9, 9, paper);
            ds.DrawRoundedRectangle(new Rect(.6, .6, 110.8, 152.8), 8.5f, 8.5f, ThemeColor(224, 218, 194), 1.2f);
            if (card is null)
            {
                DrawCasinoCardBack(ds);
                return;
            }
            Color ink = card.IsRed ? ThemeColor(176, 37, 48) : ThemeColor(26, 38, 39);
            DrawCasinoCardCorner(ds, card, ink);
            ds.Transform = Matrix3x2.CreateRotation(MathF.PI, new Vector2(56, 77)) * ds.Transform;
            DrawCasinoCardCorner(ds, card, ink);
            ds.Transform = Matrix3x2.CreateScale((float)rect.Width / 112) *
                Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y) * previous;
            if (card.Rank == 1)
                DrawCasinoSuit(ds, card.Suit, new Vector2(56, 77), 30, ink);
            else if (card.Rank >= 11)
                DrawCasinoCourtCard(ds, card, ink);
            else
                DrawCasinoPips(ds, card.Rank, card.Suit, ink);
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawCasinoCardCorner(CanvasDrawingSession ds, BlackjackCard card, Color ink)
    {
        CasinoText(ds, card.DisplayRank, new Rect(4, 3, 27, 27), 24, ink, "Georgia", true);
        DrawCasinoSuit(ds, card.Suit, new Vector2(17.5f, 40), 8, ink);
    }

    private static void DrawCasinoCardBack(CanvasDrawingSession ds)
    {
        var inside = new Rect(6, 6, 100, 142);
        ds.FillRoundedRectangle(inside, 5, 5, ThemeColor(18, 39, 65));
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds.Device, inside, 5, 5);
        using (ds.CreateLayer(1, clip))
        {
            for (int x = -140; x < 240; x += 12)
            {
                ds.DrawLine(x, 6, x + 142, 148, ThemeColor(219, 185, 115, 50), .75f);
                ds.DrawLine(x, 6, x - 142, 148, ThemeColor(219, 185, 115, 50), .75f);
            }
        }
        ds.DrawRoundedRectangle(new Rect(10, 10, 92, 134), 3, 3, ThemeColor(219, 185, 115, 145), 1);
        ds.DrawRoundedRectangle(new Rect(14, 14, 84, 126), 2, 2, ThemeColor(219, 185, 115, 70), 1);
        using var lozenge = CanvasGeometry.CreatePolygon(ds.Device,
            [new(56, 41), new(85, 77), new(56, 113), new(27, 77)]);
        ds.FillGeometry(lozenge, ThemeColor(18, 39, 65));
        ds.DrawGeometry(lozenge, CasinoGold, 1.2f);
        DrawCasinoSuit(ds, BlackjackSuit.Spades, new Vector2(56, 77), 16, CasinoGold);
    }

    private static void DrawCasinoCourtCard(CanvasDrawingSession ds, BlackjackCard card, Color ink)
    {
        ds.DrawRectangle(new Rect(32, 23, 48, 108), ThemeColor(199, 173, 104), 1.2f);
        using var shield = CanvasGeometry.CreatePolygon(ds.Device,
            [new(56, 37), new(75, 65), new(56, 115), new(37, 65)]);
        ds.FillGeometry(shield, ThemeColor(222, 202, 143, 120));
        ds.DrawGeometry(shield, ThemeColor(175, 143, 72), 1);
        using var crown = CanvasGeometry.CreatePolygon(ds.Device,
            [new(40, 53), new(38, 37), new(48, 45), new(56, 30), new(64, 45), new(74, 37), new(72, 53)]);
        ds.FillGeometry(crown, ThemeColor(189, 150, 65));
        ds.DrawLine(40, 56, 72, 56, ink, 1.5f);
        CasinoText(ds, card.DisplayRank, new Rect(35, 60, 42, 41), 31, ink, "Georgia", true);
        DrawCasinoSuit(ds, card.Suit, new Vector2(56, 118), 9, ink);
    }

    private static void DrawCasinoPips(CanvasDrawingSession ds, int rank, BlackjackSuit suit, Color ink)
    {
        List<Vector2> points = [];
        if (rank is 2 or 3)
        {
            points.Add(new(56, 38)); points.Add(new(56, 116));
            if (rank == 3) points.Add(new(56, 77));
        }
        else
        {
            points.AddRange([new(39, 38), new(73, 38), new(39, 116), new(73, 116)]);
            if (rank == 5) points.Add(new(56, 77));
            if (rank is 6 or 7 or 8) points.AddRange([new(39, 77), new(73, 77)]);
            if (rank is 7 or 8) points.Add(new(56, 57));
            if (rank == 8) points.Add(new(56, 97));
            if (rank is 9 or 10)
            {
                points.AddRange([new(39, 64), new(73, 64), new(39, 90), new(73, 90)]);
                if (rank == 9) points.Add(new(56, 77));
                else points.AddRange([new(56, 51), new(56, 103)]);
            }
        }
        foreach (var point in points) DrawCasinoSuit(ds, suit, point, rank >= 9 ? 8 : 9, ink);
    }

    private static void DrawCasinoSuit(CanvasDrawingSession ds, BlackjackSuit suit, Vector2 center, float size, Color color)
    {
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(size) * Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            if (suit == BlackjackSuit.Diamonds)
            {
                using var diamond = CanvasGeometry.CreatePolygon(ds.Device,
                    [new(0, -1.15f), new(.75f, 0), new(0, 1.15f), new(-.75f, 0)]);
                ds.FillGeometry(diamond, color);
            }
            else if (suit == BlackjackSuit.Clubs)
            {
                ds.FillCircle(new Vector2(0, -.48f), .52f, color);
                ds.FillCircle(new Vector2(-.46f, .12f), .52f, color);
                ds.FillCircle(new Vector2(.46f, .12f), .52f, color);
                using var stem = CanvasGeometry.CreatePolygon(ds.Device,
                    [new(-.15f, .1f), new(.15f, .1f), new(.18f, .65f), new(.43f, 1), new(-.43f, 1), new(-.18f, .65f)]);
                ds.FillGeometry(stem, color);
            }
            else
            {
                bool spade = suit == BlackjackSuit.Spades;
                using var path = new CanvasPathBuilder(ds.Device);
                if (spade)
                {
                    path.BeginFigure(new Vector2(0, -1.12f));
                    path.AddCubicBezier(new(-.32f, -.67f), new(-1.03f, -.3f), new(-.86f, .31f));
                    path.AddCubicBezier(new(-.7f, .8f), new(-.2f, .62f), new(0, .3f));
                    path.AddCubicBezier(new(.2f, .62f), new(.7f, .8f), new(.86f, .31f));
                    path.AddCubicBezier(new(1.03f, -.3f), new(.32f, -.67f), new(0, -1.12f));
                }
                else
                {
                    path.BeginFigure(new Vector2(0, 1.05f));
                    path.AddCubicBezier(new(-.37f, .54f), new(-1.07f, -.01f), new(-.85f, -.65f));
                    path.AddCubicBezier(new(-.69f, -1.08f), new(-.2f, -1.12f), new(0, -.65f));
                    path.AddCubicBezier(new(.2f, -1.12f), new(.69f, -1.08f), new(.85f, -.65f));
                    path.AddCubicBezier(new(1.07f, -.01f), new(.37f, .54f), new(0, 1.05f));
                }
                path.EndFigure(CanvasFigureLoop.Closed);
                using var shape = CanvasGeometry.CreatePath(path);
                ds.FillGeometry(shape, color);
                if (spade)
                {
                    using var stem = CanvasGeometry.CreatePolygon(ds.Device,
                        [new(-.13f, .22f), new(.13f, .22f), new(.19f, .69f), new(.41f, 1.04f), new(-.41f, 1.04f), new(-.19f, .69f)]);
                    ds.FillGeometry(stem, color);
                }
            }
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawCasinoDiamond(CanvasDrawingSession ds, Vector2 center, float size, Color color) =>
        DrawCasinoSuit(ds, BlackjackSuit.Diamonds, center, size, color);

    private static void DrawCasinoChipStack(CanvasDrawingSession ds, Vector2 center, float radius, decimal value)
    {
        for (int index = 3; index >= 1; index--)
        {
            ds.FillEllipse(new Vector2(center.X, center.Y + index * 5), radius, radius,
                ThemeColor(28, 53, 75));
            ds.DrawEllipse(new Vector2(center.X, center.Y + index * 5), radius, radius,
                ThemeColor(166, 172, 156), 1);
        }
        DrawCasinoChip(ds, center, radius, ThemeColor(37, 93, 153), CasinoAmount(value), false);
    }

    private static void DrawCasinoChip(CanvasDrawingSession ds, Vector2 center, float radius,
        Color body, string value, bool dimmed)
    {
        ds.FillCircle(new Vector2(center.X, center.Y + 3), radius, ThemeColor(0, 10, 8, 100));
        if (dimmed) body = ThemeColor((byte)(body.R * .65), (byte)(body.G * .65), (byte)(body.B * .65));
        ds.FillCircle(center, radius, body);
        var stripe = dimmed ? ThemeColor(152, 165, 149) : CasinoIvory;
        for (int index = 0; index < 8; index++)
        {
            float angle = index * MathF.PI / 4;
            var inner = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radius - 8);
            var outer = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radius - 1);
            ds.DrawLine(inner, outer, stripe, 7);
        }
        ds.DrawCircle(center, radius - .5f, ThemeColor(239, 229, 195, dimmed ? (byte)70 : (byte)180), 1);
        ds.DrawCircle(center, radius - 10, stripe, 1);
        ds.DrawCircle(center, radius - 13, ThemeColor(225, 235, 220, 90), .8f);
        CasinoText(ds, value, new Rect(center.X - radius + 8, center.Y - 18, radius * 2 - 16, 36),
            value.Length >= 3 ? 19 : 23, stripe, "Bahnschrift", true);
    }

    private static void CasinoText(CanvasDrawingSession ds, string text, Rect rect, float size, Color color,
        string family = "Segoe UI", bool bold = false)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = family, FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        ds.DrawText(text, rect, color, format);
    }
}
