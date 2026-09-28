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
    private static readonly Color MonopolyGold = ThemeColor(222, 188, 112);
    private static readonly Color MonopolyIvory = ThemeColor(253, 246, 223);
    private static readonly Color MonopolyInk = ThemeColor(29, 47, 40);
    private static readonly Color MonopolyMuted = ThemeColor(175, 199, 178);
    private const float MonopolyEdge = 50, MonopolyCorner = 118;
    private const float MonopolyCellWidth = (900 - 2 * MonopolyCorner) / 9;

    // Correct the geometric board's physical aspect locally, including tiles
    // rotated through 90 degrees. Uniform art retains round tokens and square
    // dice while UI hit targets keep their established board UVs.
    private readonly struct MonopolyArtAspect : IDisposable
    {
        private readonly CanvasDrawingSession _drawing;
        private readonly Matrix3x2 _previous;

        public MonopolyArtAspect(CanvasDrawingSession drawing, Vector2 center, double aspect)
        {
            _drawing = drawing;
            _previous = drawing.Transform;
            aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
            float x = aspect >= 1 ? (float)(1 / aspect) : 1;
            float y = aspect >= 1 ? 1 : (float)aspect;
            if (Math.Abs(_previous.M12) > Math.Abs(_previous.M11)) (x, y) = (y, x);
            drawing.Transform = Matrix3x2.CreateScale(x, y, center) * _previous;
        }

        public void Dispose() => _drawing.Transform = _previous;
    }

    // The perimeter and every UI control use the same square board coordinates.
    // Fine material detail is part of the cached board texture, never a moving
    // effect over the camera's text-acquisition regions.
    private static void DrawMonopolyBoard(CanvasDrawingSession ds, MonopolySnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double boardAspect = 1, bool hideDiceDisplay = false,
        bool rolling = false, bool drawerOpen = false, float drawerProgress = 1)
    {
        DrawMonopolyFrame(ds);
        for (int index = 0; index < MonopolyGame.Spaces.Count; index++)
            DrawMonopolySpace(ds, MonopolyGame.Spaces[index], game, boardAspect);
        DrawMonopolyCenter(ds, game, boardAspect, hideDiceDisplay, drawerOpen);
        if (drawerOpen)
        {
            // Slide within the felt; the drawer never covers property tiles.
            using var clip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(180, 180, 640, 640));
            using var layer = ds.CreateLayer(1, clip);
            float slide = MonopolyDrawerSlide(drawerProgress);
            DrawMonopolyDrawer(ds, game, slide);
            DrawButtons(slide);
        }
        else DrawButtons(0);
        MonopolyText(ds, rolling ? "Rolling the dice…" :
            FingerSelectionCaption(selectionFeedback, "Bring fingers together. Aim, then separate index."),
            new Rect(145, 958, 710, 24), 14, MonopolyGold);

        void DrawButtons(float slide)
        {
            foreach (var button in buttons)
            {
                if (rolling && button.Id != "mp-exit") continue;
                var previous = ds.Transform;
                if (drawerOpen && button.Id != "mp-exit-cancel")
                    ds.Transform = Matrix3x2.CreateTranslation(0, slide) * previous;
                try
                {
                    DrawMonopolyButton(ds, button, hovered.Contains(button.Id), boardAspect);
                    DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, MonopolyGold);
                }
                finally { ds.Transform = previous; }
            }
        }
    }

    private static void DrawMonopolyFrame(CanvasDrawingSession ds)
    {
        ds.Clear(ThemeColor(12, 15, 12));
        using var wood = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(85, 43, 29) },
            new() { Position = .20f, Color = ThemeColor(39, 24, 19) },
            new() { Position = .52f, Color = ThemeColor(68, 36, 23) },
            new() { Position = .80f, Color = ThemeColor(33, 23, 19) },
            new() { Position = 1, Color = ThemeColor(102, 58, 33) }
        ]) { StartPoint = new(0, 0), EndPoint = new(110, 1000) };
        ds.FillRoundedRectangle(new Rect(7, 7, 986, 986), 31, 31, wood);
        ds.DrawRoundedRectangle(new Rect(9, 9, 982, 982), 29, 29, ThemeColor(179, 119, 67, 140), 2);
        ds.DrawRoundedRectangle(new Rect(18, 18, 964, 964), 22, 22, ThemeColor(6, 10, 7, 160), 3);
        for (float offset = 0; offset < 15; offset += 3)
        {
            ds.DrawLine(42, 27 + offset, 810, 27 + offset, ThemeColor(220, 159, 82, 14), .7f);
            ds.DrawLine(40, 965 + offset, 960, 965 + offset, ThemeColor(220, 159, 82, 13), .7f);
            ds.DrawLine(27 + offset, 45, 27 + offset, 957, ThemeColor(220, 159, 82, 14), .7f);
            ds.DrawLine(965 + offset, 55, 965 + offset, 957, ThemeColor(220, 159, 82, 14), .7f);
        }
        using var gold = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(247, 228, 165) },
            new() { Position = .30f, Color = ThemeColor(147, 107, 47) },
            new() { Position = .50f, Color = ThemeColor(230, 197, 121) },
            new() { Position = 1, Color = ThemeColor(156, 112, 49) }
        ]) { StartPoint = new(50, 42), EndPoint = new(50, 960) };
        ds.FillRoundedRectangle(new Rect(44, 44, 912, 912), 7, 7, gold);
        ds.FillRectangle(new Rect(49, 49, 902, 902), ThemeColor(11, 30, 22));
        using var felt = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(27, 89, 61) },
            new() { Position = .55f, Color = ThemeColor(16, 63, 43) },
            new() { Position = 1, Color = ThemeColor(9, 41, 30) }
        ]) { Center = new(500, 405), RadiusX = 510, RadiusY = 650 };
        ds.FillRectangle(new Rect(168, 168, 664, 664), felt);
        using (var clip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(168, 168, 664, 664)))
        using (ds.CreateLayer(1, clip))
        {
            for (int offset = -700; offset < 1400; offset += 28)
            {
                ds.DrawLine(offset, 168, offset + 664, 832, ThemeColor(204, 213, 164, 10), .7f);
                ds.DrawLine(offset, 168, offset - 664, 832, ThemeColor(204, 213, 164, 10), .7f);
            }
            for (int y = 173; y < 832; y += 5)
                ds.DrawLine(168, y, 832, y, ThemeColor(0, 18, 9, 7), .5f);
        }
        ds.DrawRectangle(new Rect(173, 173, 654, 654), ThemeColor(231, 204, 134, 165), 1.25f);
        ds.DrawRectangle(new Rect(179, 179, 642, 642), ThemeColor(231, 204, 134, 50), .75f);
        foreach (var center in new[] { new Vector2(189, 189), new Vector2(811, 189), new Vector2(189, 811), new Vector2(811, 811) })
            DrawMonopolyFiligree(ds, center, center.X > 500, center.Y > 500);
        MonopolyText(ds, "P R O J E C T   T A B L E T O P", new Rect(252, 14, 496, 24), 13, MonopolyGold);
    }

    private static void DrawMonopolyFiligree(CanvasDrawingSession ds, Vector2 center, bool flipX, bool flipY)
    {
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(flipX ? -1 : 1, flipY ? -1 : 1) *
            Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            ds.DrawLine(0, 36, 0, 0, MonopolyGold, 1.1f);
            ds.DrawLine(0, 0, 36, 0, MonopolyGold, 1.1f);
            using var path = new CanvasPathBuilder(ds.Device);
            path.BeginFigure(new Vector2(3, 27));
            path.AddCubicBezier(new(22, 19), new(9, 6), new(23, 6));
            path.AddCubicBezier(new(31, 6), new(28, 16), new(21, 12));
            path.EndFigure(CanvasFigureLoop.Open);
            using var curl = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(curl, ThemeColor(225, 193, 118, 145), 1.1f);
            ds.FillCircle(new Vector2(4, 4), 2, MonopolyGold);
            ds.FillCircle(new Vector2(31, 1), 1.6f, MonopolyGold);
            ds.FillCircle(new Vector2(1, 31), 1.6f, MonopolyGold);
        }
        finally { ds.Transform = previous; }
    }

    internal static Rect MonopolySpaceRectangle(int index)
    {
        if (index is < 0 or > 39) throw new ArgumentOutOfRangeException(nameof(index));
        float inside = MonopolyEdge + MonopolyCorner, far = 1000 - inside;
        return index switch
        {
            0 => new Rect(far, far, MonopolyCorner, MonopolyCorner),
            10 => new Rect(MonopolyEdge, far, MonopolyCorner, MonopolyCorner),
            20 => new Rect(MonopolyEdge, MonopolyEdge, MonopolyCorner, MonopolyCorner),
            30 => new Rect(far, MonopolyEdge, MonopolyCorner, MonopolyCorner),
            < 10 => new Rect(far - index * MonopolyCellWidth, far, MonopolyCellWidth, MonopolyCorner),
            < 20 => new Rect(MonopolyEdge, far - (index - 10) * MonopolyCellWidth, MonopolyCorner, MonopolyCellWidth),
            < 30 => new Rect(inside + (index - 21) * MonopolyCellWidth, MonopolyEdge, MonopolyCellWidth, MonopolyCorner),
            _ => new Rect(far, inside + (index - 31) * MonopolyCellWidth, MonopolyCorner, MonopolyCellWidth)
        };
    }

    private static Matrix3x2 MonopolySpaceTransform(int index)
    {
        var rect = MonopolySpaceRectangle(index);
        if (index % 10 == 0 || index < 10)
            return Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y);
        if (index < 20)
            return Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation((float)rect.Right, (float)rect.Y);
        if (index < 30)
            return Matrix3x2.CreateRotation(MathF.PI) * Matrix3x2.CreateTranslation((float)rect.Right, (float)rect.Bottom);
        return Matrix3x2.CreateRotation(-MathF.PI / 2) * Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Bottom);
    }

    private static Vector2 MonopolyLocalTokenCenter(int spaceIndex, int slot, int occupants)
    {
        bool corner = spaceIndex % 10 == 0;
        float width = corner ? MonopolyCorner : MonopolyCellWidth;
        float spacing = occupants > 3 && !corner ? 10 : 17;
        return new Vector2(width / 2 + (slot - (occupants - 1) / 2f) * spacing, corner ? 105 : 108);
    }

    internal static Vector2 MonopolyTokenCenter(MonopolySnapshot game, int playerId)
    {
        var player = game.Players.FirstOrDefault(item => item.Id == playerId && !item.Bankrupt)
            ?? throw new ArgumentOutOfRangeException(nameof(playerId));
        var occupants = game.Players.Where(item => !item.Bankrupt && item.Position == player.Position).ToArray();
        int slot = Array.FindIndex(occupants, item => item.Id == playerId);
        return Vector2.Transform(MonopolyLocalTokenCenter(player.Position, slot, occupants.Length), MonopolySpaceTransform(player.Position));
    }

    private static void DrawMonopolySpace(CanvasDrawingSession ds, MonopolySpace space, MonopolySnapshot game, double boardAspect)
    {
        var previous = ds.Transform;
        bool corner = space.Index % 10 == 0;
        ds.Transform = MonopolySpaceTransform(space.Index) * previous;
        try
        {
            float width = corner ? MonopolyCorner : MonopolyCellWidth;
            using var paper = new CanvasLinearGradientBrush(ds.Device, ThemeColor(254, 249, 232), ThemeColor(231, 222, 197))
            { StartPoint = new(0, 0), EndPoint = new(width, MonopolyCorner) };
            ds.FillRectangle(new Rect(0, 0, width, MonopolyCorner), paper);
            ds.DrawRectangle(new Rect(.5, .5, width - 1, MonopolyCorner - 1), ThemeColor(108, 111, 88), .8f);
            if (game.SelectedPropertyIndex == space.Index || game.PendingPropertyIndex == space.Index)
            {
                ds.FillRectangle(new Rect(2, 2, width - 4, MonopolyCorner - 4), ThemeColor(245, 212, 128, 35));
                ds.DrawRectangle(new Rect(2.5, 2.5, width - 5, MonopolyCorner - 5), ThemeColor(160, 110, 39), 2.2f);
            }
            var property = game.Properties.FirstOrDefault(item => item.SpaceIndex == space.Index);
            if (corner)
                DrawMonopolyCorner(ds, space, width, boardAspect);
            else
            {
                bool colored = space.Kind == MonopolySpaceKind.Property;
                if (colored)
                {
                    Color band = MonopolyGroupColor(space.Group);
                    ds.FillRectangle(new Rect(1, 1, width - 2, 20), band);
                    ds.DrawLine(1, 21, width - 1, 21, MonopolyInk, .8f);
                    ds.DrawLine(3, 3, width - 3, 3, ThemeColor(255, 255, 255, 95), .75f);
                    if (property is not null && property.Houses > 0)
                        DrawMonopolyBuildings(ds, width, property.Houses, boardAspect);
                }
                else DrawMonopolySpaceIcon(ds, space.Kind, new Vector2(width / 2, 24), 14, MonopolyGroupColor(space.Group), boardAspect,
                    space.Kind == MonopolySpaceKind.Utility && space.Name.Contains("Water", StringComparison.Ordinal));
                string caption = MonopolySpaceCaption(space);
                float captionSize = MonopolySpaceCaptionSize(ds.Device, caption, width - 10, colored ? 11.3f : 10.6f);
                MonopolyText(ds, caption, new Rect(5, colored ? 27 : 43, width - 10, colored ? 47 : 33),
                    captionSize, MonopolyInk, "Bahnschrift SemiCondensed", true);
                string price = space.Price > 0 ? MonopolyMoney(space.Price) : space.Kind == MonopolySpaceKind.Tax
                    ? "PAY TAX" : space.Kind is MonopolySpaceKind.Chance or MonopolySpaceKind.CommunityChest ? "DRAW A CARD" : "";
                MonopolyText(ds, property?.Mortgaged == true ? "MORTGAGED" : price, new Rect(2, 79, width - 4, 17),
                    property?.Mortgaged == true ? 8.5f : 10.5f, property?.Mortgaged == true ? ThemeColor(132, 48, 42) : MonopolyInk, "Bahnschrift", true);
                if (property?.OwnerId is { } ownerId)
                {
                    var owner = game.Players.FirstOrDefault(player => player.Id == ownerId);
                    Color ownerColor = MonopolyPlayerColor(owner?.ColorIndex ?? ownerId);
                    ds.FillRoundedRectangle(new Rect(5, 98, width - 10, 3), 1.5f, 1.5f, ownerColor);
                    ds.DrawRoundedRectangle(new Rect(5, 98, width - 10, 3), 1.5f, 1.5f, ThemeColor(24, 45, 36, 100), .5f);
                }
            }
            var occupants = game.Players.Where(player => !player.Bankrupt && player.Position == space.Index).ToArray();
            for (int index = 0; index < occupants.Length; index++)
            {
                float radius = occupants.Length > 3 && !corner ? 4.5f : 7.2f;
                DrawMonopolyToken(ds, MonopolyLocalTokenCenter(space.Index, index, occupants.Length), radius, occupants[index].ColorIndex,
                    occupants[index].Id == game.Players.ElementAtOrDefault(game.ActivePlayerIndex)?.Id, boardAspect);
            }
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawMonopolyCorner(CanvasDrawingSession ds, MonopolySpace space, float width, double boardAspect)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, new Vector2(width / 2, width / 2), boardAspect);
        Color accent = space.Kind == MonopolySpaceKind.Go ? ThemeColor(154, 46, 48) : MonopolyInk;
        switch (space.Kind)
        {
            case MonopolySpaceKind.Go:
                MonopolyText(ds, "GO", new Rect(10, 13, width - 20, 43), 36, accent, "Georgia", true);
                ds.DrawLine(24, 65, 88, 65, accent, 5);
                ds.DrawLine(24, 65, 38, 54, accent, 5);
                ds.DrawLine(24, 65, 38, 76, accent, 5);
                MonopolyText(ds, "COLLECT $200", new Rect(3, 80, width - 6, 17), 10.4f, MonopolyInk, "Bahnschrift", true);
                break;
            case MonopolySpaceKind.Jail:
                MonopolyText(ds, "IN JAIL", new Rect(7, 6, width - 14, 22), 14, MonopolyInk, "Georgia", true);
                ds.FillRoundedRectangle(new Rect(32, 34, 54, 42), 6, 6, ThemeColor(191, 128, 72));
                ds.FillCircle(new Vector2(59, 53), 9, MonopolyInk);
                for (float x = 39; x < 83; x += 11) ds.DrawLine(x, 34, x, 76, ThemeColor(248, 229, 190), 3);
                MonopolyText(ds, "JUST VISITING", new Rect(3, 79, width - 6, 19), 10.8f, MonopolyInk, "Bahnschrift", true);
                break;
            case MonopolySpaceKind.FreeParking:
                MonopolyText(ds, "FREE", new Rect(4, 6, width - 8, 23), 16, MonopolyInk, "Georgia", true);
                ds.FillRoundedRectangle(new Rect(34, 40, 50, 21), 6, 6, ThemeColor(140, 47, 49));
                ds.FillRoundedRectangle(new Rect(43, 31, 31, 22), 5, 5, ThemeColor(140, 47, 49));
                ds.FillRectangle(new Rect(48, 34, 21, 10), ThemeColor(236, 224, 190));
                ds.FillCircle(new Vector2(44, 62), 7, MonopolyInk); ds.FillCircle(new Vector2(74, 62), 7, MonopolyInk);
                ds.FillCircle(new Vector2(44, 62), 3, MonopolyGold); ds.FillCircle(new Vector2(74, 62), 3, MonopolyGold);
                MonopolyText(ds, "PARKING", new Rect(4, 75, width - 8, 24), 14, MonopolyInk, "Georgia", true);
                break;
            case MonopolySpaceKind.GoToJail:
                MonopolyText(ds, "GO TO", new Rect(4, 6, width - 8, 23), 16, MonopolyInk, "Georgia", true);
                DrawMonopolyShield(ds, new Vector2(59, 54), 21, ThemeColor(28, 67, 84));
                DrawMonopolyStar(ds, new Vector2(59, 53), 10, MonopolyGold);
                MonopolyText(ds, "JAIL", new Rect(4, 77, width - 8, 23), 16, MonopolyInk, "Georgia", true);
                break;
        }
    }

    private static string MonopolySpaceCaption(MonopolySpace space)
    {
        string name = space.Name.ToUpperInvariant();
        if (name.Contains("B. & O.", StringComparison.Ordinal)) return "B. & O.\nRAILROAD";
        return string.Join("\n", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static float MonopolySpaceCaptionSize(CanvasDevice device, string caption, float width, float preferred)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Bahnschrift SemiCondensed", FontSize = preferred, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var layout = new CanvasTextLayout(device, caption, format, 500, 100);
        double scale = Math.Min(1, (width - 1) / Math.Max(1, layout.DrawBounds.Width));
        int lines = caption.Count(character => character == '\n') + 1;
        if (lines >= 3) scale = Math.Min(scale, .91);
        return (float)(preferred * scale);
    }

    private static void DrawMonopolyCenter(CanvasDrawingSession ds, MonopolySnapshot game, double boardAspect,
        bool hideDiceDisplay = false, bool drawerOpen = false)
    {
        if (game.Phase == MonopolyPhase.Landing)
        {
            DrawMonopolyCrest(ds, new Vector2(545, 291), 1.1f, boardAspect);
            MonopolyText(ds, "THE PROPERTY TRADING GAME", new Rect(236, 354, 528, 21), 14, MonopolyGold);
            MonopolyText(ds, "MONOPOLY", new Rect(212, 382, 576, 68), 53, MonopolyIvory, "Georgia", true);
            DrawMonopolyRule(ds, 458, 135);
            MonopolyText(ds, "Build an empire. Make the table yours.", new Rect(250, 651, 500, 31), 19, MonopolyIvory);
            MonopolyText(ds, "Human players and AI opponents share one beautiful board.", new Rect(242, 687, 516, 42), 16, MonopolyMuted, wrap: true);
            return;
        }
        bool headerCaret = !drawerOpen;
        MonopolyText(ds, "MONOPOLY", headerCaret ? new Rect(465, 207, 340, 49) : new Rect(230, 207, 540, 49),
            36, MonopolyIvory, "Georgia", true);
        DrawMonopolyRule(ds, 266, 100, headerCaret ? 635 : 500);
        if (game.Phase == MonopolyPhase.Setup)
        {
            MonopolyText(ds, "YOUR TABLE, YOUR COMPANY", new Rect(226, 287, 548, 28), 18, MonopolyGold);
            MonopolyText(ds, "Choose 2 to 6 players. Pass the turn between human players.", new Rect(239, 323, 522, 43), 17, MonopolyIvory, wrap: true);
            MonopolyText(ds, "HUMAN PLAYERS", new Rect(307, 382, 386, 27), 17, MonopolyGold);
            MonopolyText(ds, game.HumanPlayers.ToString(CultureInfo.InvariantCulture), new Rect(443, 414, 114, 65), 38, MonopolyIvory, "Georgia", true);
            MonopolyText(ds, "AI OPPONENTS", new Rect(307, 492, 386, 27), 17, MonopolyGold);
            MonopolyText(ds, game.AiPlayers.ToString(CultureInfo.InvariantCulture), new Rect(443, 524, 114, 65), 38, MonopolyIvory, "Georgia", true);
            MonopolyText(ds, $"{game.HumanPlayers + game.AiPlayers} players  ·  $1,500 starting cash each", new Rect(259, 608, 482, 26), 16, MonopolyMuted);
            return;
        }
        if (game.Phase == MonopolyPhase.ManageProperties)
        {
            DrawMonopolyManagement(ds, game);
            return;
        }
        if (game.Phase == MonopolyPhase.Auction)
        {
            DrawMonopolyAuction(ds, game);
            return;
        }
        if (game.Phase == MonopolyPhase.GameOver)
        {
            var winner = game.Players.FirstOrDefault(player => player.Id == game.WinnerId);
            DrawMonopolyCrest(ds, new Vector2(500, 366), .8f, boardAspect);
            MonopolyText(ds, "AN EMPIRE IS BUILT", new Rect(231, 427, 538, 27), 18, MonopolyGold);
            MonopolyText(ds, winner is null ? "Game complete" : $"{winner.Name} wins", new Rect(238, 462, 524, 65), 32, MonopolyIvory, "Georgia", true, true);
            MonopolyText(ds, game.Status, new Rect(247, 534, 506, 54), 18, MonopolyMuted, wrap: true);
            return;
        }
        DrawMonopolyRoster(ds, game, boardAspect);
        if (drawerOpen) return;
        DrawMonopolyPlaque(ds, new Rect(224, 474, 552, 119));
        var active = game.Players.ElementAtOrDefault(game.ActivePlayerIndex);
        string phase = game.Phase switch
        {
            MonopolyPhase.AwaitingRoll => active?.InJail == true ? "IN JAIL" : "YOUR MOVE",
            MonopolyPhase.AwaitingPurchase => "PROPERTY AVAILABLE",
            MonopolyPhase.Debt => "SETTLE YOUR BALANCE",
            _ => "AT THE TABLE"
        };
        MonopolyText(ds, phase, new Rect(244, 482, 512, 23), 13, MonopolyGold, "Bahnschrift", true);
        MonopolyText(ds, game.Status, new Rect(243, 510, 514, 49), 18, MonopolyIvory, wrap: true);
        if (game.Dice is { First: > 0, Second: > 0 } dice)
        {
            if (!hideDiceDisplay)
            {
                DrawMonopolyDie(ds, new Rect(413, 563, 23, 23), dice.First, MonopolyIvory, MonopolyInk, boardAspect);
                DrawMonopolyDie(ds, new Rect(442, 563, 23, 23), dice.Second, MonopolyIvory, MonopolyInk, boardAspect);
            }
            MonopolyText(ds, dice.IsDouble ? $"{dice.Total} · DOUBLES" : $"ROLLED {dice.Total}", new Rect(479, 562, 176, 25), 13, MonopolyGold, "Bahnschrift", true);
        }
        else MonopolyText(ds, "Every empire begins with a roll.", new Rect(252, 563, 496, 24), 14, MonopolyMuted);
        if (!string.IsNullOrWhiteSpace(game.LastCard) && active?.InJail != true)
            MonopolyText(ds, game.LastCard, new Rect(230, 779, 540, 40), 12, MonopolyMuted, wrap: true);
    }

    private static void DrawMonopolyRoster(CanvasDrawingSession ds, MonopolySnapshot game, double boardAspect)
    {
        int rows = (game.Players.Count + 1) / 2;
        float start = rows <= 2 ? 309 : 306;
        float height = rows <= 2 ? 67 : 45;
        for (int index = 0; index < game.Players.Count; index++)
        {
            var player = game.Players[index];
            bool active = index == game.ActivePlayerIndex && !player.Bankrupt;
            var rect = new Rect(221 + index % 2 * 286, start + index / 2 * (height + 8), 272, height);
            DrawMonopolyPlaque(ds, rect, active);
            DrawMonopolyToken(ds, new Vector2((float)rect.X + 26, (float)(rect.Y + height / 2)), 10, player.ColorIndex, active, boardAspect);
            MonopolyText(ds, player.Name + (player.IsAi ? " · AI" : ""), new Rect(rect.X + 48, rect.Y + 6, 138, 22),
                14, player.Bankrupt ? ThemeColor(123, 139, 124) : MonopolyIvory, "Bahnschrift", true);
            MonopolyText(ds, player.Bankrupt ? "BANKRUPT" : MonopolyMoney(player.Money), new Rect(rect.X + 168, rect.Y + 6, 91, 25),
                player.Bankrupt ? 11 : 17, active ? MonopolyGold : MonopolyIvory, "Bahnschrift", true);
            string caption = player.Bankrupt ? "Out of the game" : player.InJail ? "In jail" : MonopolyGame.Spaces[player.Position].Name;
            MonopolyText(ds, caption, new Rect(rect.X + 48, rect.Y + 29, 210, height - 32), 11.5f, MonopolyMuted);
        }
        var current = game.Players.ElementAtOrDefault(game.ActivePlayerIndex);
        bool headerExit = game.Phase is not MonopolyPhase.ExitConfirmation and not MonopolyPhase.Saving;
        MonopolyText(ds, current is null ? "" : $"TURN {game.TurnNumber}  ·  {current.Name.ToUpperInvariant()}{(current.IsAi ? " · AI IS PLAYING" : "")}",
            headerExit ? new Rect(465, 278, 340, 24) : new Rect(234, 278, 532, 24),
            15, MonopolyGold, "Bahnschrift", true);
    }

    private static void DrawMonopolyManagement(CanvasDrawingSession ds, MonopolySnapshot game)
    {
        MonopolyText(ds, "YOUR PROPERTY PORTFOLIO", new Rect(235, 289, 530, 25), 17, MonopolyGold);
        var property = game.Properties.FirstOrDefault(item => item.SpaceIndex == game.SelectedPropertyIndex);
        var space = property is null ? null : MonopolyGame.Spaces[property.SpaceIndex];
        MonopolyText(ds, space?.Name ?? "No properties owned", new Rect(337, 346, 326, 90), 26, MonopolyIvory, "Georgia", true, true);
        if (space is not null && property is not null)
        {
            ds.FillRoundedRectangle(new Rect(367, 447, 266, 6), 3, 3, MonopolyGroupColor(space.Group));
            string building = space.Kind == MonopolySpaceKind.Railroad ? "RAILROAD" : space.Kind == MonopolySpaceKind.Utility ? "UTILITY" :
                property.Houses == 5 ? "HOTEL" : property.Houses == 0 ? "UNDEVELOPED" : $"{property.Houses} {(property.Houses == 1 ? "HOUSE" : "HOUSES")}";
            MonopolyText(ds, property.Mortgaged ? "MORTGAGED" : building, new Rect(277, 466, 446, 24), 15, MonopolyGold);
            string value = $"Property {MonopolyMoney(space.Price)}" + (space.HouseCost > 0 ? $"  ·  Building {MonopolyMoney(space.HouseCost)}" : "");
            MonopolyText(ds, value, new Rect(262, 493, 476, 23), 13, MonopolyMuted);
        }
        MonopolyText(ds, game.Status, new Rect(241, 791, 518, 30), 12, MonopolyMuted, wrap: true);
    }

    private static void DrawMonopolyAuction(CanvasDrawingSession ds, MonopolySnapshot game)
    {
        var auction = game.Auction;
        if (auction is null) return;
        var space = MonopolyGame.Spaces[auction.SpaceIndex];
        var bidder = game.Players.FirstOrDefault(player => player.Id == auction.CurrentBidderId);
        MonopolyText(ds, "AT AUCTION", new Rect(242, 294, 516, 24), 18, MonopolyGold);
        MonopolyText(ds, space.Name, new Rect(243, 337, 514, 77), 31, MonopolyIvory, "Georgia", true, true);
        DrawMonopolyPlaque(ds, new Rect(324, 434, 352, 122));
        MonopolyText(ds, "CURRENT BID", new Rect(335, 444, 330, 24), 13, MonopolyMuted);
        MonopolyText(ds, MonopolyMoney(auction.Bid), new Rect(335, 472, 330, 45), 36, MonopolyGold, "Georgia", true);
        MonopolyText(ds, bidder is null ? "" : $"{bidder.Name}{(bidder.IsAi ? " · AI" : "")}, your bid", new Rect(238, 567, 524, 30), 20, MonopolyIvory);
        MonopolyText(ds, game.Status, new Rect(245, 768, 510, 27), 13, MonopolyMuted);
    }

    private static void DrawMonopolyPlaque(CanvasDrawingSession ds, Rect rect, bool active = false)
    {
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 3, rect.Width, rect.Height), 13, 13, ThemeColor(2, 13, 8, 100));
        using var surface = new CanvasLinearGradientBrush(ds.Device, active ? ThemeColor(30, 66, 46) : ThemeColor(19, 49, 36), ThemeColor(11, 31, 23))
        { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, 13, 13, surface);
        ds.DrawRoundedRectangle(rect, 13, 13, active ? MonopolyGold : ThemeColor(188, 184, 122, 85), active ? 1.7f : .8f);
        ds.DrawLine((float)rect.X + 17, (float)rect.Y + 2, (float)rect.Right - 17, (float)rect.Y + 2, ThemeColor(235, 217, 159, 65), .8f);
    }

    private static Rect MonopolyButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        bool roll = button.Id == "mp-roll";
        return new Rect(bounds.X * BoardSurfaceSize + (roll ? 92 : 12), bounds.Y * BoardSurfaceSize + 4,
            bounds.Width * BoardSurfaceSize - (roll ? 104 : 24), bounds.Height * BoardSurfaceSize - 12);
    }

    private static CanvasTextFormat MonopolyButtonTextFormat(BoardButton button) => new()
    {
        FontFamily = "Bahnschrift", FontWeight = FontWeights.SemiBold,
        FontSize = button.Label is "^" or "v" ? 34 : button.Label is "+" or "−" or "-" ? 28 : button.Id == "mp-roll" ? 26 :
            button.Bounds.Height < .05 ? 17 : button.Id is "mp-start-game" or "mp-start" ? 26 :
            button.Bounds.Width < .1 ? 24 : button.Label.Length > 15 ? 18 : 22,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void DrawMonopolyButton(CanvasDrawingSession ds, BoardButton button, bool hovered, double boardAspect)
    {
        var b = button.Bounds;
        var rect = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize, b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
        bool caret = button.Label is "^" or "v";
        bool primary = button.Id is "mp-roll" or "mp-start-game" or "mp-start" or "mp-buy" or "mp-end-turn" or "mp-save-exit" or "mp-exit-game" or "mp-new-game";
        bool danger = button.Id is "mp-bankrupt" or "mp-exit-without-saving";
        bool enabled = button.Enabled;
        hovered &= enabled;
        float radius = button.Id == "mp-roll" || caret ? (float)rect.Height / 2 : (float)Math.Min(13, rect.Height / 3);
        Color top = !enabled ? ThemeColor(40, 56, 42) : primary ? ThemeColor(241, 216, 152) : danger ? ThemeColor(90, 42, 36) : hovered ? ThemeColor(47, 91, 65) : caret ? ThemeColor(22, 56, 39) : ThemeColor(31, 66, 47);
        Color bottom = !enabled ? ThemeColor(24, 39, 29) : primary ? ThemeColor(179, 139, 67) : danger ? ThemeColor(56, 24, 21) : ThemeColor(13, 36, 26);
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 4, rect.Width, rect.Height), radius, radius, ThemeColor(0, 12, 5, 140));
        using var surface = new CanvasLinearGradientBrush(ds.Device, top, bottom)
        { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, radius, radius, surface);
        ds.DrawRoundedRectangle(rect, radius, radius, hovered ? MonopolyIvory : enabled ? MonopolyGold : ThemeColor(100, 117, 88), hovered ? 2.6f : 1.2f);
        ds.DrawLine((float)rect.X + radius, (float)rect.Y + 2, (float)rect.Right - radius, (float)rect.Y + 2, ThemeColor(255, 245, 207, enabled ? (byte)90 : (byte)20), .7f);
        if (hovered)
            ds.DrawRoundedRectangle(new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6), radius + 3, radius + 3, ThemeColor(226, 198, 133, 50), 3);
        Color ink = !enabled ? ThemeColor(143, 158, 139) : primary ? ThemeColor(36, 39, 25) : MonopolyIvory;
        if (button.Id == "mp-roll")
        {
            float centerY = (float)(rect.Y + rect.Height / 2);
            DrawMonopolyDie(ds, new Rect(rect.X + 25, centerY - 14, 28, 28), 5, ink, primary ? ThemeColor(226, 197, 129) : MonopolyInk, boardAspect);
            DrawMonopolyDie(ds, new Rect(rect.X + 57, centerY - 14, 28, 28), 3, ink, primary ? ThemeColor(226, 197, 129) : MonopolyInk, boardAspect);
            ds.DrawLine((float)rect.X + 92, centerY - 17, (float)rect.X + 92, centerY + 17, ThemeColor(57, 56, 31, 60), .8f);
        }
        using var format = MonopolyButtonTextFormat(button);
        ds.DrawText(button.Label, MonopolyButtonTextRectangle(button), ink, format);
    }

    private static void DrawMonopolyDie(CanvasDrawingSession ds, Rect rect, int value, Color face, Color dots, double boardAspect = 1)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2)), boardAspect);
        float size = (float)Math.Min(rect.Width, rect.Height);
        var square = new Rect(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size);
        ds.FillRoundedRectangle(square, size * .17f, size * .17f, face);
        ds.DrawRoundedRectangle(square, size * .17f, size * .17f, ThemeColor(dots.R, dots.G, dots.B, 90), .6f);
        void Dot(float x, float y) => ds.FillCircle(new Vector2((float)square.X + size * x, (float)square.Y + size * y), size * .064f, dots);
        if (value is 1 or 3 or 5) Dot(.5f, .5f);
        if (value >= 2) { Dot(.26f, .26f); Dot(.74f, .74f); }
        if (value >= 4) { Dot(.74f, .26f); Dot(.26f, .74f); }
        if (value == 6) { Dot(.26f, .5f); Dot(.74f, .5f); }
    }

    private static void DrawMonopolyToken(CanvasDrawingSession ds, Vector2 center, float radius, int colorIndex, bool active, double boardAspect = 1)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, center, boardAspect);
        Color color = MonopolyPlayerColor(colorIndex);
        ds.FillEllipse(center + new Vector2(0, radius * .32f), radius + 1, radius * .74f, ThemeColor(20, 22, 16, 90));
        using var finish = new CanvasLinearGradientBrush(ds.Device, ThemeColor((byte)Math.Min(255, color.R + 34), (byte)Math.Min(255, color.G + 34), (byte)Math.Min(255, color.B + 34)), color)
        { StartPoint = center - new Vector2(0, radius), EndPoint = center + new Vector2(0, radius) };
        ds.FillCircle(center, radius, finish);
        ds.DrawCircle(center, radius, active ? MonopolyIvory : ThemeColor(33, 39, 24), active ? 1.6f : .8f);
        ds.DrawCircle(center, radius * .63f, ThemeColor(255, 245, 212, 140), .7f);
        DrawMonopolyStar(ds, center, radius * .38f, MonopolyIvory);
    }

    private static void DrawMonopolyBuildings(CanvasDrawingSession ds, float width, int houses, double boardAspect)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, new Vector2(width / 2, 11), boardAspect);
        if (houses == 5)
        {
            ds.FillRoundedRectangle(new Rect(width / 2 - 11, 7, 22, 11), 1, 1, ThemeColor(173, 52, 42));
            ds.DrawRectangle(new Rect(width / 2 - 11, 7, 22, 11), MonopolyIvory, .65f);
            for (float x = -7; x <= 7; x += 7) ds.FillRectangle(new Rect(width / 2 + x - 1, 10, 2, 3), MonopolyIvory);
            return;
        }
        for (int index = 0; index < houses; index++)
        {
            float x = width / 2 + (index - (houses - 1) / 2f) * 13;
            using var roof = CanvasGeometry.CreatePolygon(ds.Device, [new(x - 5, 10), new(x, 5), new(x + 5, 10)]);
            ds.FillGeometry(roof, MonopolyIvory);
            ds.FillRectangle(new Rect(x - 4, 10, 8, 7), ThemeColor(29, 114, 70));
            ds.DrawRectangle(new Rect(x - 4, 10, 8, 7), MonopolyIvory, .65f);
        }
    }

    private static Color MonopolyPlayerColor(int index) => ((index % 6 + 6) % 6) switch
    {
        0 => ThemeColor(192, 63, 60), 1 => ThemeColor(68, 117, 195), 2 => ThemeColor(233, 180, 64),
        3 => ThemeColor(148, 85, 182), 4 => ThemeColor(69, 163, 137), _ => ThemeColor(225, 132, 64)
    };

    private static Color MonopolyGroupColor(MonopolyGroup group) => group switch
    {
        MonopolyGroup.Brown => ThemeColor(123, 73, 49), MonopolyGroup.LightBlue => ThemeColor(116, 177, 196),
        MonopolyGroup.Pink => ThemeColor(176, 88, 140), MonopolyGroup.Orange => ThemeColor(218, 140, 56),
        MonopolyGroup.Red => ThemeColor(179, 54, 55), MonopolyGroup.Yellow => ThemeColor(226, 194, 73),
        MonopolyGroup.Green => ThemeColor(53, 135, 90), MonopolyGroup.DarkBlue => ThemeColor(38, 71, 134),
        _ => MonopolyInk
    };

    private static void DrawMonopolySpaceIcon(CanvasDrawingSession ds, MonopolySpaceKind kind, Vector2 center, float radius, Color color, double boardAspect,
        bool waterUtility = false)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, center, boardAspect);
        switch (kind)
        {
            case MonopolySpaceKind.Chance:
                MonopolyText(ds, "?", new Rect(center.X - radius, center.Y - radius - 7, radius * 2, radius * 2 + 12), radius * 2.5f, ThemeColor(173, 105, 44), "Georgia", true);
                break;
            case MonopolySpaceKind.CommunityChest:
                ds.FillRoundedRectangle(new Rect(center.X - radius, center.Y - radius * .55, radius * 2, radius * 1.25), 3, 3, ThemeColor(174, 130, 57));
                ds.DrawLine(center.X - radius, center.Y - 1, center.X + radius, center.Y - 1, MonopolyInk, 1);
                ds.FillRectangle(new Rect(center.X - 2, center.Y - 3, 4, 7), MonopolyIvory);
                break;
            case MonopolySpaceKind.Railroad:
                ds.FillRoundedRectangle(new Rect(center.X - 11, center.Y - 11, 22, 17), 3, 3, color);
                ds.FillRectangle(new Rect(center.X - 7, center.Y - 7, 14, 6), MonopolyIvory);
                ds.FillCircle(center + new Vector2(-7, 6), 3, color); ds.FillCircle(center + new Vector2(7, 6), 3, color);
                ds.DrawLine(center.X - 13, center.Y + 12, center.X + 13, center.Y + 12, color, 2);
                ds.DrawLine(center.X - 9, center.Y + 7, center.X - 14, center.Y + 16, color, 1.5f);
                ds.DrawLine(center.X + 9, center.Y + 7, center.X + 14, center.Y + 16, color, 1.5f);
                break;
            case MonopolySpaceKind.Utility:
                if (waterUtility)
                {
                    using var dropPath = new CanvasPathBuilder(ds.Device);
                    dropPath.BeginFigure(center + new Vector2(0, -radius));
                    dropPath.AddCubicBezier(center + new Vector2(-radius * .35f, -radius * .45f), center + new Vector2(-radius, radius * .05f), center + new Vector2(-radius * .6f, radius * .65f));
                    dropPath.AddCubicBezier(center + new Vector2(-radius * .3f, radius), center + new Vector2(radius * .3f, radius), center + new Vector2(radius * .6f, radius * .65f));
                    dropPath.AddCubicBezier(center + new Vector2(radius, radius * .05f), center + new Vector2(radius * .35f, -radius * .45f), center + new Vector2(0, -radius));
                    dropPath.EndFigure(CanvasFigureLoop.Closed);
                    using var drop = CanvasGeometry.CreatePath(dropPath);
                    ds.FillGeometry(drop, ThemeColor(67, 124, 148));
                    ds.DrawGeometry(drop, color, .8f);
                    ds.DrawLine(center.X - 4, center.Y + 1, center.X - 3, center.Y + 6, MonopolyIvory, 1.4f);
                    break;
                }
                ds.DrawCircle(center - new Vector2(0, 2), 10, color, 1.4f);
                ds.DrawLine(center.X - 5, center.Y + 8, center.X + 5, center.Y + 8, color, 2.5f);
                ds.DrawLine(center.X - 4, center.Y + 12, center.X + 4, center.Y + 12, color, 2.5f);
                for (int index = 0; index < 5; index++)
                {
                    float angle = MathF.PI + index * MathF.PI / 4;
                    var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    ds.DrawLine(center + direction * 15, center + direction * 18, color, 1.3f);
                }
                break;
            case MonopolySpaceKind.Tax:
                DrawMonopolyDiamond(ds, center, 12, ThemeColor(168, 127, 57));
                ds.DrawLine(center.X - 10, center.Y, center.X + 10, center.Y, MonopolyIvory, .8f);
                break;
        }
    }

    private static void DrawMonopolyCrest(CanvasDrawingSession ds, Vector2 center, float scale, double boardAspect)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, center, boardAspect);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            ds.DrawCircle(Vector2.Zero, 43, ThemeColor(227, 195, 121, 130), 1);
            ds.DrawCircle(Vector2.Zero, 38, ThemeColor(227, 195, 121, 45), .8f);
            DrawMonopolyShield(ds, new Vector2(0, 1), 31, ThemeColor(17, 60, 41));
            DrawMonopolyCrown(ds, new Vector2(0, -3), 22, MonopolyGold);
            for (int side = -1; side <= 1; side += 2)
            {
                var root = new Vector2(side * 14, 50);
                var firstControl = new Vector2(side * 66, 32);
                var secondControl = new Vector2(side * 76, -2);
                var tip = new Vector2(side * 52, -36);
                using var path = new CanvasPathBuilder(ds.Device);
                path.BeginFigure(root);
                path.AddCubicBezier(firstControl, secondControl, tip);
                path.EndFigure(CanvasFigureLoop.Open);
                using var stem = CanvasGeometry.CreatePath(path);
                ds.DrawGeometry(stem, ThemeColor(224, 192, 121, 160), 1.5f);
                for (int leaf = 0; leaf < 6; leaf++)
                {
                    // Share the branch's exact curve, so every leaf grows
                    // from the stem instead of floating beside the crest.
                    float t = .12f + leaf * .14f, u = 1 - t;
                    var attachment = u * u * u * root + 3 * u * u * t * firstControl +
                        3 * u * t * t * secondControl + t * t * t * tip;
                    var tangent = Vector2.Normalize(3 * u * u * (firstControl - root) +
                        6 * u * t * (secondControl - firstControl) + 3 * t * t * (tip - secondControl));
                    var outward = side * new Vector2(-tangent.Y, tangent.X);
                    var direction = Vector2.Normalize(.83f * tangent + .55f * outward);
                    DrawMonopolyLaurelLeaf(ds, attachment, direction, 18 - leaf * .7f);
                }
            }
            DrawMonopolyDiamond(ds, new Vector2(0, 51), 5, MonopolyGold);
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawMonopolyLaurelLeaf(CanvasDrawingSession ds, Vector2 attachment,
        Vector2 direction, float length)
    {
        var normal = new Vector2(-direction.Y, direction.X);
        var root = attachment + direction * 1.5f;
        var tip = attachment + direction * length;
        float width = length * .25f;
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(root);
        path.AddCubicBezier(root + direction * length * .24f + normal * width,
            tip - direction * length * .30f + normal * width, tip);
        path.AddCubicBezier(tip - direction * length * .30f - normal * width,
            root + direction * length * .24f - normal * width, root);
        path.EndFigure(CanvasFigureLoop.Closed);
        using var leaf = CanvasGeometry.CreatePath(path);
        ds.DrawLine(attachment, root + direction * 2, MonopolyGold, 1.3f);
        ds.FillGeometry(leaf, ThemeColor(224, 192, 121, 220));
        ds.DrawLine(root + direction, tip - direction * 2, ThemeColor(45, 76, 43, 180), .65f);
    }

    private static void DrawMonopolyCrown(CanvasDrawingSession ds, Vector2 center, float size, Color color, double boardAspect = 1)
    {
        using var aspectTransform = new MonopolyArtAspect(ds, center, boardAspect);
        using var crown = CanvasGeometry.CreatePolygon(ds.Device,
        [
            center + new Vector2(-size, -size * .42f), center + new Vector2(-size * .66f, size * .4f),
            center + new Vector2(size * .66f, size * .4f), center + new Vector2(size, -size * .42f),
            center + new Vector2(size * .42f, -size * .12f), center + new Vector2(0, -size * .75f),
            center + new Vector2(-size * .42f, -size * .12f)
        ]);
        ds.FillGeometry(crown, color);
        ds.DrawLine(center.X - size * .64f, center.Y + size * .62f, center.X + size * .64f, center.Y + size * .62f, color, size * .13f);
        ds.FillCircle(center + new Vector2(0, -size * .8f), size * .10f, color);
        ds.FillCircle(center + new Vector2(-size, -size * .47f), size * .09f, color);
        ds.FillCircle(center + new Vector2(size, -size * .47f), size * .09f, color);
    }

    private static void DrawMonopolyShield(CanvasDrawingSession ds, Vector2 center, float size, Color color)
    {
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(center + new Vector2(-size * .7f, -size * .85f));
        path.AddLine(center + new Vector2(size * .7f, -size * .85f));
        path.AddLine(center + new Vector2(size * .65f, size * .25f));
        path.AddCubicBezier(center + new Vector2(size * .5f, size * .65f), center + new Vector2(size * .2f, size * .9f), center + new Vector2(0, size));
        path.AddCubicBezier(center + new Vector2(-size * .2f, size * .9f), center + new Vector2(-size * .5f, size * .65f), center + new Vector2(-size * .65f, size * .25f));
        path.EndFigure(CanvasFigureLoop.Closed);
        using var shield = CanvasGeometry.CreatePath(path);
        ds.FillGeometry(shield, color); ds.DrawGeometry(shield, MonopolyGold, 1.1f);
    }

    private static void DrawMonopolyDiamond(CanvasDrawingSession ds, Vector2 center, float size, Color color)
    {
        using var diamond = CanvasGeometry.CreatePolygon(ds.Device,
            [center + new Vector2(0, -size), center + new Vector2(size, 0), center + new Vector2(0, size), center + new Vector2(-size, 0)]);
        ds.FillGeometry(diamond, color);
    }

    private static void DrawMonopolyStar(CanvasDrawingSession ds, Vector2 center, float size, Color color)
    {
        var vertices = new Vector2[10];
        for (int index = 0; index < vertices.Length; index++)
        {
            float angle = -MathF.PI / 2 + index * MathF.PI / 5;
            float radius = index % 2 == 0 ? size : size * .43f;
            vertices[index] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        using var star = CanvasGeometry.CreatePolygon(ds.Device, vertices);
        ds.FillGeometry(star, color);
    }

    private static void DrawMonopolyRule(CanvasDrawingSession ds, float y, float halfWidth, float centerX = 500)
    {
        ds.DrawLine(centerX - halfWidth, y, centerX - 19, y, ThemeColor(228, 197, 126, 125), 1);
        ds.DrawLine(centerX + 19, y, centerX + halfWidth, y, ThemeColor(228, 197, 126, 125), 1);
        DrawMonopolyDiamond(ds, new Vector2(centerX, y), 4, MonopolyGold);
    }

    private static string MonopolyMoney(decimal amount) => "$" + amount.ToString("#,0.##", CultureInfo.InvariantCulture);

    private static void MonopolyText(CanvasDrawingSession ds, string text, Rect rect, float size, Color color,
        string family = "Bahnschrift", bool bold = false, bool wrap = false)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = family, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = wrap ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap
        };
        ds.DrawText(text, rect, color, format);
    }
}
