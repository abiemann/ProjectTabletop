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
    private static readonly Color CrownDeedGold = ThemeColor(222, 188, 112);
    private static readonly Color CrownDeedIvory = ThemeColor(253, 246, 223);
    private static readonly Color CrownDeedInk = ThemeColor(29, 47, 40);
    private static readonly Color CrownDeedMuted = ThemeColor(175, 199, 178);
    private const float CrownDeedCorner = 90;
    private const float CrownDeedCellWidth = 60;
    private CanvasBitmap? _crownDeedCityBitmap;
    private CanvasDevice? _crownDeedCityDevice;

    internal readonly record struct CrownDeedParcel(Vector2 Center, float RotationRadians, float Width, float Depth,
        Matrix3x2 Transform);

    // Every district has the same footprint. The boulevard has no corner stops.
    internal static CrownDeedParcel CrownDeedParcelPose(int index)
    {
        if (index is < 0 or > 39) throw new ArgumentOutOfRangeException(nameof(index));
        float angle = MathF.PI / 2 + index * MathF.Tau / 40;
        var center = new Vector2(500 + 434 * MathF.Cos(angle), 500 + 408 * MathF.Sin(angle));
        var normal = Vector2.Normalize(new Vector2(MathF.Cos(angle) / 434, MathF.Sin(angle) / 408));
        float rotation = MathF.Atan2(-normal.X, normal.Y);
        var transform = Matrix3x2.CreateTranslation(-CrownDeedCellWidth / 2, -CrownDeedCorner / 2) *
            Matrix3x2.CreateRotation(rotation) * Matrix3x2.CreateTranslation(center);
        return new(center, rotation, CrownDeedCellWidth, CrownDeedCorner, transform);
    }

    // Correct the geometric board's physical aspect locally, including tiles
    // rotated through 90 degrees. Uniform art retains round tokens and square
    // dice while UI hit targets keep their established board UVs.
    private readonly struct CrownDeedArtAspect : IDisposable
    {
        private readonly CanvasDrawingSession _drawing;
        private readonly Matrix3x2 _previous;

        public CrownDeedArtAspect(CanvasDrawingSession drawing, Vector2 center, double aspect)
        {
            _drawing = drawing;
            _previous = drawing.Transform;
            aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
            float x = aspect >= 1 ? (float)(1 / aspect) : 1;
            float y = aspect >= 1 ? 1 : (float)aspect;
            // Conjugate the physical correction through an arbitrary parcel
            // rotation, retaining the outer raster scale. This also handles
            // intermediate oval angles, not only quarter turns.
            float rasterX = MathF.Sqrt(_previous.M11 * _previous.M11 + _previous.M21 * _previous.M21);
            float rasterY = MathF.Sqrt(_previous.M12 * _previous.M12 + _previous.M22 * _previous.M22);
            float angle = MathF.Atan2(_previous.M12 / Math.Max(.0001f, rasterY),
                _previous.M11 / Math.Max(.0001f, rasterX));
            var correction = Matrix3x2.CreateRotation(angle, center) *
                Matrix3x2.CreateScale(x, y, center) * Matrix3x2.CreateRotation(-angle, center);
            drawing.Transform = correction * _previous;
        }

        public void Dispose() => _drawing.Transform = _previous;
    }

    // Oval Boulevard and every UI control share the same logical coordinates.
    // Fine material detail is part of the cached board texture, never a moving
    // effect over the camera's text-acquisition regions.
    private void DrawCrownDeedBoard(CanvasDrawingSession ds, CrownDeedSnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double boardAspect = 1, bool hideDiceDisplay = false,
        bool rolling = false, bool drawerOpen = false, float drawerProgress = 1,
        CrownDeedEntranceFrame? entrance = null)
    {
        if (entrance is { Active: true } frame)
        {
            DrawCrownDeedEntrance(ds, game, buttons, hovered, selectionFeedback, boardAspect,
                hideDiceDisplay, rolling, drawerOpen, drawerProgress, frame);
            return;
        }
        DrawCrownDeedFrame(ds);
        var development = GetCrownDeedDevelopmentFrame(_crownDeedClock());
        for (int index = 0; index < CrownDeedGame.Spaces.Count; index++)
            DrawCrownDeedSpace(ds, CrownDeedGame.Spaces[index], game, boardAspect,
                development is { Active: true } && development.SpaceIndex == index ? development.Progress : 1);
        DrawCrownDeedCenterContents(ds, game, buttons, hovered, selectionFeedback, boardAspect,
            hideDiceDisplay, rolling, drawerOpen, drawerProgress, development);
        DrawCrownDeedRailCaptions(ds, rolling, selectionFeedback);
    }

    private void DrawCrownDeedCenterContents(CanvasDrawingSession ds, CrownDeedSnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double boardAspect,
        bool hideDiceDisplay, bool rolling, bool drawerOpen, float drawerProgress, CrownDeedDevelopmentFrame? development = null)
    {
        DrawCrownDeedCenter(ds, game, boardAspect, hideDiceDisplay, drawerOpen, development);
        if (drawerOpen)
        {
            // Slide within the felt; the drawer never covers property tiles.
            using var clip = CanvasGeometry.CreateEllipse(ds.Device, new Vector2(500, 500), 379, 351);
            using var layer = ds.CreateLayer(1, clip);
            float slide = CrownDeedDrawerSlide(drawerProgress);
            DrawCrownDeedDrawer(ds, game, slide);
            DrawButtons(slide);
        }
        else DrawButtons(0);
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
                    DrawCrownDeedButton(ds, button, hovered.Contains(button.Id), boardAspect);
                    DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, CrownDeedGold);
                }
                finally { ds.Transform = previous; }
            }
        }
    }

    private CanvasBitmap? EnsureCrownDeedCity(CanvasDevice device)
    {
        return PrepareCrownDeedResources(device) ? _crownDeedCityBitmap : null;
    }

    private void DrawCrownDeedFrame(CanvasDrawingSession ds)
    {
        ds.Clear(ThemeColor(10, 24, 25));
        if (EnsureCrownDeedCity(ds.Device) is { } city)
        {
            ds.DrawImage(city, new Rect(0, 0, 1000, 1000),
                new Rect(0, 0, city.SizeInPixels.Width, city.SizeInPixels.Height));
        }
        // A continuous granite boulevard connects the equal-sized deed plaques.
        ds.DrawEllipse(new Vector2(500, 500), 434, 408, ThemeColor(6, 19, 20, 225), 99);
        ds.DrawEllipse(new Vector2(500, 500), 479, 453, ThemeColor(155, 121, 63), 2.5f);
        ds.DrawEllipse(new Vector2(500, 500), 482, 456, ThemeColor(246, 218, 151, 170), .8f);
        ds.DrawEllipse(new Vector2(500, 500), 387, 361, ThemeColor(224, 190, 113, 190), 1.7f);
        ds.DrawEllipse(new Vector2(500, 500), 379, 353, ThemeColor(142, 110, 56, 155), .7f);
        for (int i = 0; i < 160; i++)
        {
            float a = MathF.PI / 2 + i * MathF.Tau / 160;
            var light = new Vector2(500 + 382 * MathF.Cos(a), 500 + 355 * MathF.Sin(a));
            ds.FillCircle(light, i % 4 == 0 ? 1.3f : .6f,
                i % 4 == 0 ? CrownDeedGold : ThemeColor(216, 185, 120, 80));
        }
        DrawCrownDeedCenterDecoration(ds);
    }

    private static void DrawCrownDeedCenterDecoration(CanvasDrawingSession ds)
    {
        // This opaque physical surface owns the central oval. The painted
        // city has a different plaza outline and compass, which must not show
        // through the felt or shift its trim near the navigation handle.
        using var stone = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(17, 49, 42) },
            new() { Position = 1, Color = ThemeColor(5, 27, 25) }
        ]) { Center = new(500, 475), RadiusX = 379, RadiusY = 353 };
        ds.FillEllipse(new Vector2(500, 500), 379, 353, stone);
        ds.DrawEllipse(new Vector2(500, 500), 372, 346, ThemeColor(218, 193, 123, 60), .8f);
    }

    private static void DrawCrownDeedRailCaptions(CanvasDrawingSession ds, bool rolling,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback) =>
        CrownDeedText(ds, rolling ? "Rolling the dice…" :
            FingerSelectionCaption(feedback, "Bring fingers together. Aim, then separate index."),
            new Rect(145, 958, 710, 24), 14, CrownDeedGold);

    private static void DrawCrownDeedFiligree(CanvasDrawingSession ds, Vector2 center, bool flipX, bool flipY)
    {
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(flipX ? -1 : 1, flipY ? -1 : 1) *
            Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            ds.DrawLine(0, 36, 0, 0, CrownDeedGold, 1.1f);
            ds.DrawLine(0, 0, 36, 0, CrownDeedGold, 1.1f);
            using var path = new CanvasPathBuilder(ds.Device);
            path.BeginFigure(new Vector2(3, 27));
            path.AddCubicBezier(new(22, 19), new(9, 6), new(23, 6));
            path.AddCubicBezier(new(31, 6), new(28, 16), new(21, 12));
            path.EndFigure(CanvasFigureLoop.Open);
            using var curl = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(curl, ThemeColor(225, 193, 118, 145), 1.1f);
            ds.FillCircle(new Vector2(4, 4), 2, CrownDeedGold);
            ds.FillCircle(new Vector2(31, 1), 1.6f, CrownDeedGold);
            ds.FillCircle(new Vector2(1, 31), 1.6f, CrownDeedGold);
        }
        finally { ds.Transform = previous; }
    }

    internal static Rect CrownDeedSpaceRectangle(int index)
    {
        var pose = CrownDeedParcelPose(index);
        var points = new[] { new Vector2(0, 0), new Vector2(pose.Width, 0),
            new Vector2(pose.Width, pose.Depth), new Vector2(0, pose.Depth) }
            .Select(point => Vector2.Transform(point, pose.Transform)).ToArray();
        float left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return new Rect(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
    }

    private static Matrix3x2 CrownDeedSpaceTransform(int index) => CrownDeedParcelPose(index).Transform;

    private static Vector2 CrownDeedLocalTokenCenter(int spaceIndex, int slot, int occupants) =>
        new(CrownDeedCellWidth / 2 + (slot - (occupants - 1) / 2f) * (occupants > 3 ? 10 : 18), 82);

    internal static Vector2 CrownDeedTokenCenter(CrownDeedSnapshot game, int playerId)
    {
        var player = game.Players.FirstOrDefault(item => item.Id == playerId && !item.Bankrupt)
            ?? throw new ArgumentOutOfRangeException(nameof(playerId));
        var occupants = game.Players.Where(item => !item.Bankrupt && item.Position == player.Position).ToArray();
        int slot = Array.FindIndex(occupants, item => item.Id == playerId);
        return Vector2.Transform(CrownDeedLocalTokenCenter(player.Position, slot, occupants.Length), CrownDeedSpaceTransform(player.Position));
    }

    private void DrawCrownDeedSpace(CanvasDrawingSession ds, CrownDeedSpace space, CrownDeedSnapshot game,
        double boardAspect, float developmentProgress = 1)
    {
        var previous = ds.Transform;
        ds.Transform = CrownDeedSpaceTransform(space.Index) * previous;
        try
        {
            const float width = CrownDeedCellWidth, depth = CrownDeedCorner;
            using var stone = new CanvasLinearGradientBrush(ds.Device, ThemeColor(29, 54, 53), ThemeColor(11, 29, 34))
            { StartPoint = new(0, 0), EndPoint = new(width, depth) };
            ds.FillRoundedRectangle(new Rect(0, 0, width, depth), 5, 5, stone);
            ds.DrawRoundedRectangle(new Rect(.5, .5, width - 1, depth - 1), 4.5f, 4.5f, ThemeColor(191, 157, 87), 1);
            ds.DrawRoundedRectangle(new Rect(2.5, 2.5, width - 5, depth - 5), 3, 3, ThemeColor(232, 208, 144, 70), .5f);
            if (game.SelectedPropertyIndex == space.Index || game.PendingPropertyIndex == space.Index)
                ds.DrawRoundedRectangle(new Rect(1.7, 1.7, width - 3.4, depth - 3.4), 4, 4, CrownDeedIvory, 1.8f);
            var property = game.Properties.FirstOrDefault(item => item.SpaceIndex == space.Index);
            if (space.Kind == CrownDeedSpaceKind.Property)
            {
                var band = CrownDeedGroupColor(space.Group);
                ds.FillRoundedRectangle(new Rect(5, 5, width - 10, 13), 3, 3, band);
                ds.DrawLine(8, 7, width - 8, 7, ThemeColor(255, 238, 191, 130), .7f);
                if (property is { Houses: > 0 })
                    DrawCrownDeedBuildings(ds, width, property.Houses, developmentProgress, band, boardAspect);
            }
            else DrawCrownDeedCivicIcon(ds, space.Kind, new(width / 2, 13), boardAspect);
            string caption = CrownDeedSpaceCaption(space);
            float captionSize = CrownDeedSpaceCaptionSize(ds.Device, caption, width - 8, 10.8f);
            CrownDeedText(ds, caption, new Rect(4, 24, width - 8, 32), captionSize, CrownDeedIvory,
                "Bahnschrift SemiCondensed", true);
            string price = space.Price > 0 ? CrownDeedMoney(space.Price) : space.Kind switch
            {
                CrownDeedSpaceKind.Go => "+ 200 CR",
                CrownDeedSpaceKind.Tax => "CITY DUES",
                CrownDeedSpaceKind.Chance or CrownDeedSpaceKind.CommunityChest => "EVENT",
                CrownDeedSpaceKind.Jail => "REVIEW",
                CrownDeedSpaceKind.GoToJail => "TO WATCH",
                _ => "REST"
            };
            CrownDeedText(ds, property?.Mortgaged == true ? "PLEDGED" : price, new Rect(3, 58, width - 6, 14),
                property?.Mortgaged == true ? 8.5f : 9.5f,
                property?.Mortgaged == true ? ThemeColor(245, 159, 120) : CrownDeedGold, "Bahnschrift", true);
            if (property?.OwnerId is { } ownerId)
            {
                var owner = game.Players.FirstOrDefault(player => player.Id == ownerId);
                ds.FillRoundedRectangle(new Rect(7, 72, width - 14, 2), 1, 1,
                    CrownDeedPlayerColor(owner?.ColorIndex ?? ownerId));
            }
        }
        finally { ds.Transform = previous; }
        // Place the metal pieces in world coordinates. Their forward axis aims
        // at the plaza, independently of the deed's elliptical tangent.
        var occupants = game.Players.Where(player => !player.Bankrupt && player.Position == space.Index).ToArray();
        for (int i = 0; i < occupants.Length; i++)
            DrawCrownDeedPiece(ds, CrownDeedTokenCenter(game, occupants[i].Id),
                occupants.Length > 3 ? 5 : 9, occupants[i].PieceIndex, occupants[i].ColorIndex,
                occupants[i].Id == game.Players.ElementAtOrDefault(game.ActivePlayerIndex)?.Id, boardAspect);
    }

    private static void DrawCrownDeedCivicIcon(CanvasDrawingSession ds, CrownDeedSpaceKind kind,
        Vector2 center, double aspect)
    {
        using var correction = new CrownDeedArtAspect(ds, center, aspect);
        if (kind is CrownDeedSpaceKind.Chance or CrownDeedSpaceKind.CommunityChest)
        {
            ds.FillRoundedRectangle(new Rect(center.X - 7, center.Y - 6, 14, 12), 2, 2, CrownDeedGold);
            for (int y = -3; y <= 3; y += 3)
                ds.DrawLine(center + new Vector2(-4, y), center + new Vector2(4, y), CrownDeedInk, .7f);
        }
        else if (kind == CrownDeedSpaceKind.Railroad)
        {
            using var hull = CanvasGeometry.CreatePolygon(ds.Device,
                [center + new Vector2(-11, 0), center + new Vector2(11, 0),
                 center + new Vector2(7, 5), center + new Vector2(-7, 5)]);
            ds.FillGeometry(hull, CrownDeedGold);
            ds.DrawLine(center + new Vector2(0, -8), center, CrownDeedIvory, 1);
            ds.DrawLine(center + new Vector2(-11, 8), center + new Vector2(11, 8), CrownDeedGold, .8f);
        }
        else if (kind is CrownDeedSpaceKind.Jail or CrownDeedSpaceKind.GoToJail)
        {
            DrawCrownDeedShield(ds, center, 8, CrownDeedGold);
            ds.DrawLine(center + new Vector2(-3, 0), center + new Vector2(-.5f, 3), CrownDeedInk, 1.1f);
            ds.DrawLine(center + new Vector2(-.5f, 3), center + new Vector2(4, -3), CrownDeedInk, 1.1f);
        }
        else if (kind == CrownDeedSpaceKind.FreeParking)
        {
            ds.DrawEllipse(center, 10, 5, CrownDeedGold, 1.3f);
            ds.DrawLine(center + new Vector2(0, -6), center + new Vector2(0, 2), CrownDeedIvory, 1.3f);
            ds.FillCircle(center + new Vector2(0, -7), 2, CrownDeedIvory);
        }
        else if (kind == CrownDeedSpaceKind.Utility)
        {
            ds.DrawCircle(center, 7, CrownDeedGold, 1);
            ds.DrawLine(center + new Vector2(-4, 4), center + new Vector2(0, -4), CrownDeedIvory, 1.2f);
            ds.DrawLine(center + new Vector2(0, -4), center + new Vector2(4, 4), CrownDeedIvory, 1.2f);
        }
        else if (kind == CrownDeedSpaceKind.Tax)
            DrawCrownDeedDiamond(ds, center, 7, CrownDeedGold);
        else
        {
            using var crown = CanvasGeometry.CreatePolygon(ds.Device,
                [center + new Vector2(-8, 5), center + new Vector2(-10, -4),
                 center + new Vector2(-4, 0), center + new Vector2(0, -7),
                 center + new Vector2(4, 0), center + new Vector2(10, -4), center + new Vector2(8, 5)]);
            ds.FillGeometry(crown, CrownDeedGold);
            ds.DrawLine(center + new Vector2(-7, 7), center + new Vector2(7, 7), CrownDeedIvory, .8f);
        }
    }

    private static string CrownDeedSpaceCaption(CrownDeedSpace space) =>
        string.Join("\n", space.Name.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static float CrownDeedSpaceCaptionSize(CanvasDevice device, string caption, float width, float preferred)
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

    private void DrawCrownDeedCenter(CanvasDrawingSession ds, CrownDeedSnapshot game, double boardAspect,
        bool hideDiceDisplay = false, bool drawerOpen = false, CrownDeedDevelopmentFrame? development = null)
    {
        if (game.Phase == CrownDeedPhase.Landing)
        {
            DrawCrownDeedCrest(ds, new Vector2(500, 291), 1.1f, boardAspect);
            CrownDeedText(ds, "WELCOME TO OVAL BOULEVARD", new Rect(236, 354, 528, 21), 14, CrownDeedGold);
            CrownDeedText(ds, "CROWN & DEED", new Rect(212, 382, 576, 68), 53, CrownDeedIvory, "Georgia", true);
            DrawCrownDeedRule(ds, 458, 135);
            CrownDeedText(ds, "Build your fortune. Shape the city.", new Rect(250, 679, 500, 31), 19, CrownDeedIvory);
            return;
        }
        CrownDeedText(ds, "CROWN & DEED", new Rect(280, 232, 440, 42),
            30, CrownDeedIvory, "Georgia", true);
        DrawCrownDeedRule(ds, 278, 100);
        if (game.Phase == CrownDeedPhase.Setup)
        {
            CrownDeedText(ds, "YOUR TABLE, YOUR COMPANY", new Rect(226, 287, 548, 28), 18, CrownDeedGold);
            CrownDeedText(ds, "Choose 2 to 6 players. Pass the turn between human players.", new Rect(239, 323, 522, 43), 17, CrownDeedIvory, wrap: true);
            CrownDeedText(ds, "HUMAN PLAYERS", new Rect(307, 365, 386, 24), 17, CrownDeedGold);
            CrownDeedText(ds, game.HumanPlayers.ToString(CultureInfo.InvariantCulture), new Rect(443, 395, 114, 48), 34, CrownDeedIvory, "Georgia", true);
            CrownDeedText(ds, "AI OPPONENTS", new Rect(307, 458, 386, 24), 17, CrownDeedGold);
            CrownDeedText(ds, game.AiPlayers.ToString(CultureInfo.InvariantCulture), new Rect(443, 490, 114, 48), 34, CrownDeedIvory, "Georgia", true);
            CrownDeedText(ds, $"{game.HumanPlayers + game.AiPlayers} players  ·  1,500 crowns each", new Rect(259, 558, 482, 24), 16, CrownDeedMuted);
            CrownDeedText(ds, "CHOOSE YOUR SILVER PIECE", new Rect(259, 590, 482, 22), 14, CrownDeedGold);
            return;
        }
        if (game.Phase == CrownDeedPhase.ManageProperties)
        {
            DrawCrownDeedManagement(ds, game, boardAspect, development);
            return;
        }
        if (game.Phase == CrownDeedPhase.Auction)
        {
            DrawCrownDeedAuction(ds, game);
            return;
        }
        if (game.Phase == CrownDeedPhase.GameOver)
        {
            var winner = game.Players.FirstOrDefault(player => player.Id == game.WinnerId);
            DrawCrownDeedCrest(ds, new Vector2(500, 366), .8f, boardAspect);
            CrownDeedText(ds, "AN EMPIRE IS BUILT", new Rect(231, 427, 538, 27), 18, CrownDeedGold);
            CrownDeedText(ds, winner is null ? "Game complete" : $"{winner.Name} wins", new Rect(238, 462, 524, 65), 32, CrownDeedIvory, "Georgia", true, true);
            CrownDeedText(ds, game.Status, new Rect(247, 534, 506, 54), 18, CrownDeedMuted, wrap: true);
            return;
        }
        DrawCrownDeedRoster(ds, game, boardAspect);
        if (drawerOpen) return;
        DrawCrownDeedPlaque(ds, new Rect(224, 474, 552, 119));
        var active = game.Players.ElementAtOrDefault(game.ActivePlayerIndex);
        string phase = game.Phase switch
        {
            CrownDeedPhase.AwaitingRoll => active?.InJail == true ? "CIVIC WATCH" : "YOUR MOVE",
            CrownDeedPhase.AwaitingPurchase => "PROPERTY AVAILABLE",
            CrownDeedPhase.Debt => "SETTLE YOUR BALANCE",
            _ => "AT THE TABLE"
        };
        CrownDeedText(ds, phase, new Rect(244, 482, 512, 23), 13, CrownDeedGold, "Bahnschrift", true);
        CrownDeedText(ds, game.Status, new Rect(243, 510, 514, 49), 18, CrownDeedIvory, wrap: true);
        if (game.Dice is { First: > 0, Second: > 0 } dice)
        {
            if (!hideDiceDisplay)
            {
                DrawCrownDeedDie(ds, new Rect(413, 563, 23, 23), dice.First, CrownDeedIvory, CrownDeedInk, boardAspect);
                DrawCrownDeedDie(ds, new Rect(442, 563, 23, 23), dice.Second, CrownDeedIvory, CrownDeedInk, boardAspect);
            }
            CrownDeedText(ds, dice.IsDouble ? $"{dice.Total} · DOUBLES" : $"ROLLED {dice.Total}", new Rect(479, 562, 176, 25), 13, CrownDeedGold, "Bahnschrift", true);
        }
        else CrownDeedText(ds, "Every empire begins with a roll.", new Rect(252, 563, 496, 24), 14, CrownDeedMuted);
        if (!string.IsNullOrWhiteSpace(game.LastCard) && active?.InJail != true)
            CrownDeedText(ds, game.LastCard, new Rect(320, 772, 360, 38), 12, CrownDeedMuted, wrap: true);
    }

    private void DrawCrownDeedRoster(CanvasDrawingSession ds, CrownDeedSnapshot game, double boardAspect)
    {
        int rows = (game.Players.Count + 1) / 2;
        float start = rows <= 2 ? 309 : 306;
        float height = rows <= 2 ? 67 : 45;
        for (int index = 0; index < game.Players.Count; index++)
        {
            var player = game.Players[index];
            bool active = index == game.ActivePlayerIndex && !player.Bankrupt;
            var rect = new Rect(221 + index % 2 * 286, start + index / 2 * (height + 8), 272, height);
            DrawCrownDeedPlaque(ds, rect, active);
            DrawCrownDeedPiece(ds, new Vector2((float)rect.X + 26, (float)(rect.Y + height / 2)),
                16, player.PieceIndex, player.ColorIndex, active, boardAspect, faceCenter: false);
            CrownDeedText(ds, player.Name + (player.IsAi ? " · AI" : ""), new Rect(rect.X + 48, rect.Y + 6, 138, 22),
                14, player.Bankrupt ? ThemeColor(123, 139, 124) : CrownDeedIvory, "Bahnschrift", true);
            CrownDeedText(ds, player.Bankrupt ? "BANKRUPT" : CrownDeedMoney(player.Money), new Rect(rect.X + 168, rect.Y + 6, 91, 25),
                player.Bankrupt ? 11 : 17, active ? CrownDeedGold : CrownDeedIvory, "Bahnschrift", true);
            string caption = player.Bankrupt ? "Out of the game" : player.InJail ? "Under review" : CrownDeedGame.Spaces[player.Position].Name;
            CrownDeedText(ds, caption, new Rect(rect.X + 48, rect.Y + 29, 210, height - 32), 11.5f, CrownDeedMuted);
        }
        var current = game.Players.ElementAtOrDefault(game.ActivePlayerIndex);
        bool headerExit = game.Phase is not CrownDeedPhase.ExitConfirmation and not CrownDeedPhase.Saving;
        CrownDeedText(ds, current is null ? "" : $"TURN {game.TurnNumber}  ·  {current.Name.ToUpperInvariant()}{(current.IsAi ? " · AI IS PLAYING" : "")}",
            headerExit ? new Rect(445, 284, 350, 24) : new Rect(234, 278, 532, 24),
            15, CrownDeedGold, "Bahnschrift", true);
    }

    private static void DrawCrownDeedManagement(CanvasDrawingSession ds, CrownDeedSnapshot game,
        double boardAspect, CrownDeedDevelopmentFrame? development)
    {
        CrownDeedText(ds, "YOUR PROPERTY PORTFOLIO", new Rect(235, 289, 530, 25), 17, CrownDeedGold);
        var property = game.Properties.FirstOrDefault(item => item.SpaceIndex == game.SelectedPropertyIndex);
        var space = property is null ? null : CrownDeedGame.Spaces[property.SpaceIndex];
        CrownDeedText(ds, space?.Name ?? "No properties owned", new Rect(337, 326, 326, 44), 26, CrownDeedIvory, "Georgia", true, true);
        if (space is not null && property is not null)
        {
            if (property.Houses > 0)
            {
                var previous = ds.Transform;
                ds.Transform = Matrix3x2.CreateScale(2.2f) * Matrix3x2.CreateTranslation(434, 415) * previous;
                try
                {
                    DrawCrownDeedBuildings(ds, 60, property.Houses,
                        development is { Active: true } && development.SpaceIndex == space.Index ? development.Progress : 1,
                        CrownDeedGroupColor(space.Group), boardAspect);
                }
                finally { ds.Transform = previous; }
            }
            else DrawCrownDeedRule(ds, 420, 90);
            string building = space.Kind == CrownDeedSpaceKind.Railroad ? "TRANSIT" : space.Kind == CrownDeedSpaceKind.Utility ? "INFRASTRUCTURE" :
                property.Houses == 5 ? "GRAND HALL" : property.Houses == 0 ? "UNDEVELOPED" : $"{property.Houses} {(property.Houses == 1 ? "SHOP" : "SHOPS")}";
            CrownDeedText(ds, property.Mortgaged ? "PLEDGED" : building, new Rect(277, 466, 446, 24), 15, CrownDeedGold);
            string value = $"Property {CrownDeedMoney(space.Price)}" + (space.HouseCost > 0 ? $"  ·  Building {CrownDeedMoney(space.HouseCost)}" : "");
            CrownDeedText(ds, value, new Rect(262, 493, 476, 23), 13, CrownDeedMuted);
        }
        CrownDeedText(ds, game.Status, new Rect(340, 807, 320, 27), 12, CrownDeedMuted, wrap: true);
    }

    private static void DrawCrownDeedAuction(CanvasDrawingSession ds, CrownDeedSnapshot game)
    {
        var auction = game.Auction;
        if (auction is null) return;
        var space = CrownDeedGame.Spaces[auction.SpaceIndex];
        var bidder = game.Players.FirstOrDefault(player => player.Id == auction.CurrentBidderId);
        CrownDeedText(ds, "AT AUCTION", new Rect(242, 294, 516, 24), 18, CrownDeedGold);
        CrownDeedText(ds, space.Name, new Rect(243, 337, 514, 77), 31, CrownDeedIvory, "Georgia", true, true);
        DrawCrownDeedPlaque(ds, new Rect(324, 434, 352, 122));
        CrownDeedText(ds, "CURRENT BID", new Rect(335, 444, 330, 24), 13, CrownDeedMuted);
        CrownDeedText(ds, CrownDeedMoney(auction.Bid), new Rect(335, 472, 330, 45), 36, CrownDeedGold, "Georgia", true);
        CrownDeedText(ds, bidder is null ? "" : $"{bidder.Name}{(bidder.IsAi ? " · AI" : "")}, your bid", new Rect(238, 567, 524, 30), 20, CrownDeedIvory);
        CrownDeedText(ds, game.Status, new Rect(320, 787, 360, 30), 13, CrownDeedMuted);
    }

    private static void DrawCrownDeedPlaque(CanvasDrawingSession ds, Rect rect, bool active = false)
    {
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 3, rect.Width, rect.Height), 13, 13, ThemeColor(2, 13, 8, 100));
        using var surface = new CanvasLinearGradientBrush(ds.Device, active ? ThemeColor(30, 66, 46) : ThemeColor(19, 49, 36), ThemeColor(11, 31, 23))
        { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, 13, 13, surface);
        ds.DrawRoundedRectangle(rect, 13, 13, active ? CrownDeedGold : ThemeColor(188, 184, 122, 85), active ? 1.7f : .8f);
        ds.DrawLine((float)rect.X + 17, (float)rect.Y + 2, (float)rect.Right - 17, (float)rect.Y + 2, ThemeColor(235, 217, 159, 65), .8f);
    }

    private static Rect CrownDeedButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        bool roll = button.Id == "mp-roll";
        bool piece = button.Id.StartsWith("mp-piece-next-", StringComparison.Ordinal);
        return new Rect(bounds.X * BoardSurfaceSize + (roll ? 92 : piece ? 40 : 12), bounds.Y * BoardSurfaceSize + 4,
            bounds.Width * BoardSurfaceSize - (roll ? 104 : piece ? 47 : 24), bounds.Height * BoardSurfaceSize - 12);
    }

    private static CanvasTextFormat CrownDeedButtonTextFormat(BoardButton button) => new()
    {
        FontFamily = "Bahnschrift", FontWeight = FontWeights.SemiBold,
        FontSize = button.Id.StartsWith("mp-piece-next-", StringComparison.Ordinal) ? 13 :
            button.Label is "+" or "−" or "-" ? 28 : button.Id == "mp-roll" ? 26 :
            button.Bounds.Height < .05 ? 17 : button.Id is "mp-start-game" or "mp-start" ? 26 :
            button.Bounds.Width < .1 ? 24 : button.Label.Length > 15 ? 18 : 22,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private void DrawCrownDeedButton(CanvasDrawingSession ds, BoardButton button, bool hovered, double boardAspect)
    {
        var b = button.Bounds;
        var rect = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize, b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
        bool caret = IsCrownDeedDrawerHandle(button);
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
        ds.DrawRoundedRectangle(rect, radius, radius, hovered ? CrownDeedIvory : enabled ? CrownDeedGold : ThemeColor(100, 117, 88), hovered ? 2.6f : 1.2f);
        ds.DrawLine((float)rect.X + radius, (float)rect.Y + 2, (float)rect.Right - radius, (float)rect.Y + 2, ThemeColor(255, 245, 207, enabled ? (byte)90 : (byte)20), .7f);
        if (hovered)
            ds.DrawRoundedRectangle(new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6), radius + 3, radius + 3, ThemeColor(226, 198, 133, 50), 3);
        Color ink = !enabled ? ThemeColor(143, 158, 139) : primary ? ThemeColor(36, 39, 25) : CrownDeedIvory;
        if (button.Id == "mp-roll")
        {
            float centerY = (float)(rect.Y + rect.Height / 2);
            DrawCrownDeedDie(ds, new Rect(rect.X + 25, centerY - 14, 28, 28), 5, ink, primary ? ThemeColor(226, 197, 129) : CrownDeedInk, boardAspect);
            DrawCrownDeedDie(ds, new Rect(rect.X + 57, centerY - 14, 28, 28), 3, ink, primary ? ThemeColor(226, 197, 129) : CrownDeedInk, boardAspect);
            ds.DrawLine((float)rect.X + 92, centerY - 17, (float)rect.X + 92, centerY + 17, ThemeColor(57, 56, 31, 60), .8f);
        }
        if (caret)
        {
            DrawCrownDeedDrawerArrow(ds, button, boardAspect, hovered);
            return;
        }
        if (button.Id.StartsWith("mp-piece-next-", StringComparison.Ordinal) &&
            int.TryParse(button.Id.AsSpan("mp-piece-next-".Length), out int slot) &&
            slot > 0 && slot <= _boardSession.CrownDeedState.SetupPieces.Count)
            DrawCrownDeedPiece(ds, new Vector2((float)rect.X + 23, (float)(rect.Y + rect.Height / 2)),
                14, _boardSession.CrownDeedState.SetupPieces[slot - 1], slot - 1, false, boardAspect, faceCenter: false);
        using var format = CrownDeedButtonTextFormat(button);
        ds.DrawText(button.Label, CrownDeedButtonTextRectangle(button), ink, format);
    }

    private static void DrawCrownDeedDie(CanvasDrawingSession ds, Rect rect, int value, Color face, Color dots, double boardAspect = 1)
    {
        using var aspectTransform = new CrownDeedArtAspect(ds, new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2)), boardAspect);
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

    private static Color CrownDeedPlayerColor(int index) => ((index % 6 + 6) % 6) switch
    {
        0 => ThemeColor(192, 63, 60), 1 => ThemeColor(68, 117, 195), 2 => ThemeColor(233, 180, 64),
        3 => ThemeColor(148, 85, 182), 4 => ThemeColor(69, 163, 137), _ => ThemeColor(225, 132, 64)
    };

    private static Color CrownDeedGroupColor(CrownDeedGroup group) => group switch
    {
        CrownDeedGroup.Brown => ThemeColor(126, 105, 75), CrownDeedGroup.LightBlue => ThemeColor(62, 104, 100),
        CrownDeedGroup.Pink => ThemeColor(132, 79, 101), CrownDeedGroup.Orange => ThemeColor(101, 138, 130),
        CrownDeedGroup.Red => ThemeColor(168, 112, 73), CrownDeedGroup.Yellow => ThemeColor(88, 97, 128),
        CrownDeedGroup.Green => ThemeColor(87, 90, 99), CrownDeedGroup.DarkBlue => ThemeColor(158, 125, 56),
        _ => CrownDeedInk
    };

    private static void DrawCrownDeedCrest(CanvasDrawingSession ds, Vector2 center, float scale, double boardAspect)
    {
        using var aspectTransform = new CrownDeedArtAspect(ds, center, boardAspect);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            ds.DrawCircle(Vector2.Zero, 43, ThemeColor(227, 195, 121, 130), 1);
            ds.DrawCircle(Vector2.Zero, 38, ThemeColor(227, 195, 121, 45), .8f);
            DrawCrownDeedShield(ds, new Vector2(0, 1), 31, ThemeColor(17, 60, 41));
            DrawCrownDeedCrown(ds, new Vector2(0, -3), 22, CrownDeedGold);
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
                    DrawCrownDeedLaurelLeaf(ds, attachment, direction, 18 - leaf * .7f);
                }
            }
            DrawCrownDeedDiamond(ds, new Vector2(0, 51), 5, CrownDeedGold);
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawCrownDeedLaurelLeaf(CanvasDrawingSession ds, Vector2 attachment,
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
        ds.DrawLine(attachment, root + direction * 2, CrownDeedGold, 1.3f);
        ds.FillGeometry(leaf, ThemeColor(224, 192, 121, 220));
        ds.DrawLine(root + direction, tip - direction * 2, ThemeColor(45, 76, 43, 180), .65f);
    }

    private static void DrawCrownDeedCrown(CanvasDrawingSession ds, Vector2 center, float size, Color color, double boardAspect = 1)
    {
        using var aspectTransform = new CrownDeedArtAspect(ds, center, boardAspect);
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

    private static void DrawCrownDeedShield(CanvasDrawingSession ds, Vector2 center, float size, Color color)
    {
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(center + new Vector2(-size * .7f, -size * .85f));
        path.AddLine(center + new Vector2(size * .7f, -size * .85f));
        path.AddLine(center + new Vector2(size * .65f, size * .25f));
        path.AddCubicBezier(center + new Vector2(size * .5f, size * .65f), center + new Vector2(size * .2f, size * .9f), center + new Vector2(0, size));
        path.AddCubicBezier(center + new Vector2(-size * .2f, size * .9f), center + new Vector2(-size * .5f, size * .65f), center + new Vector2(-size * .65f, size * .25f));
        path.EndFigure(CanvasFigureLoop.Closed);
        using var shield = CanvasGeometry.CreatePath(path);
        ds.FillGeometry(shield, color); ds.DrawGeometry(shield, CrownDeedGold, 1.1f);
    }

    private static void DrawCrownDeedDiamond(CanvasDrawingSession ds, Vector2 center, float size, Color color)
    {
        using var diamond = CanvasGeometry.CreatePolygon(ds.Device,
            [center + new Vector2(0, -size), center + new Vector2(size, 0), center + new Vector2(0, size), center + new Vector2(-size, 0)]);
        ds.FillGeometry(diamond, color);
    }

    private static void DrawCrownDeedStar(CanvasDrawingSession ds, Vector2 center, float size, Color color)
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

    private static void DrawCrownDeedRule(CanvasDrawingSession ds, float y, float halfWidth, float centerX = 500)
    {
        ds.DrawLine(centerX - halfWidth, y, centerX - 19, y, ThemeColor(228, 197, 126, 125), 1);
        ds.DrawLine(centerX + 19, y, centerX + halfWidth, y, ThemeColor(228, 197, 126, 125), 1);
        DrawCrownDeedDiamond(ds, new Vector2(centerX, y), 4, CrownDeedGold);
    }

    private static string CrownDeedMoney(decimal amount) => amount.ToString("#,0.##", CultureInfo.InvariantCulture) + " cr";

    private static void CrownDeedText(CanvasDrawingSession ds, string text, Rect rect, float size, Color color,
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
