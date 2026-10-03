using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection.PaintFluid;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Each menu tile previews its destination with that board's own artwork.
    // The preview is sharp on the right and fades into the glass across the
    // panel's diagonal sheen band. Images render once at native board density.
    private const float MenuPreviewUnits = 160;
    private readonly Dictionary<BoardScreen, CanvasRenderTarget> _menuPreviews = [];
    private static readonly Lazy<MonopolySnapshot> MenuPreviewMonopoly = new(() => new MonopolyGame(1).Snapshot);
    private bool _menuGlobePreviewDeferred;

    private void PrepareMenuPreviews(CanvasDevice device)
    {
        bool menu = _boardSession.Screen == BoardScreen.Menu;
        // Board setup precedes the menu, leaving time for the shared Earth
        // textures to load before the Globe tile's first preview.
        if (_boardSetup || menu) GetGlobeRenderer(device);
        // A menu first drawn without Earth keeps that tile until the menu is
        // left or rescanned, so the camera's rendered reference stays valid.
        if (_boardSetup || !menu) _menuGlobePreviewDeferred = false;
    }

    private void DrawMenuPreview(CanvasDrawingSession ds, Rect rect, Rect inside, float radius, BoardScreen screen)
    {
        // Matches DrawGlassPanel's sheen: .52–.76 of the width at the top and
        // .28–.52 at the bottom. The fade runs perpendicular to that band.
        var bounds = new Rect(rect.X + rect.Width * .28, rect.Y, rect.Width * .72, rect.Height);
        var image = MenuPreviewImage(ds, screen, bounds);
        if (image is null) return;
        var along = Vector2.Normalize(new((float)(-rect.Width * .24), (float)rect.Height));
        var normal = new Vector2(-along.Y, along.X) * -1;
        // The isolated dragon starts farther right than an opaque board scene.
        // Carry the same diagonal falloff across its plume rather than through
        // empty alpha, keeping the head and neck clear at the right edge.
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
        using (ds.CreateLayer(fade, clip))
            ds.DrawImage(image, bounds, new Rect(0, 0, image.Size.Width, image.Size.Height));
    }

    private CanvasRenderTarget? MenuPreviewImage(CanvasDrawingSession ds, BoardScreen screen, Rect bounds)
    {
        // Physical pixels are square in the board raster, so rendering at the
        // destination's native size keeps chips, dice and Earth round.
        var transform = ds.Transform;
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width * new Vector2(transform.M11, transform.M12).Length()), 16, 2048);
        int height = Math.Clamp((int)Math.Ceiling(bounds.Height * new Vector2(transform.M21, transform.M22).Length()), 16, 2048);
        if (_menuPreviews.TryGetValue(screen, out var cached))
        {
            if (cached.Device == ds.Device && cached.SizeInPixels.Width == width && cached.SizeInPixels.Height == height)
                return cached;
            cached.Dispose();
            _menuPreviews.Remove(screen);
        }
        if (screen == BoardScreen.Globe && (_menuGlobePreviewDeferred || !GetGlobeRenderer(ds.Device).IsReady))
        {
            _menuGlobePreviewDeferred = true;
            return null;
        }
        if (screen is not (BoardScreen.HandTracking or BoardScreen.PhotoCopy or BoardScreen.Blackjack or
            BoardScreen.Paint or BoardScreen.Monopoly or BoardScreen.Globe or BoardScreen.Slots or BoardScreen.Roulette)) return null;
        var image = new CanvasRenderTarget(ds.Device, width, height, 96);
        PaintFluidSimulation? fluid = null;
        try
        {
            using (var drawing = image.CreateDrawingSession())
            {
                float unit = height / MenuPreviewUnits;
                drawing.Transform = Matrix3x2.CreateScale(unit);
                float span = width / unit;
                switch (screen)
                {
                    case BoardScreen.HandTracking: DrawHandTrackingPreview(drawing, span); break;
                    case BoardScreen.PhotoCopy: DrawPhotoCopyPreview(drawing, span); break;
                    case BoardScreen.Blackjack: DrawBlackjackPreview(drawing, span); break;
                    case BoardScreen.Paint: fluid = DrawPaintPreview(drawing, span); break;
                    case BoardScreen.Monopoly: DrawMonopolyPreview(drawing, span); break;
                    case BoardScreen.Globe: DrawGlobePreview(drawing, span); break;
                    case BoardScreen.Slots: DrawSlotsMenuPreview(drawing, span); break;
                    case BoardScreen.Roulette: DrawRouletteMenuPreview(drawing, span); break;
                }
            }
        }
        catch (NotSupportedException)
        {
            // An adapter without floating-point fields cannot run Paint either.
            image.Dispose();
            return null;
        }
        finally { fluid?.Dispose(); }
        _menuPreviews[screen] = image;
        return image;
    }

    private void DisposeMenuPreviews()
    {
        foreach (var image in _menuPreviews.Values) image.Dispose();
        _menuPreviews.Clear();
    }

    // Preview scenes use a local space 160 units high; `span` is its width.
    // Focal content sits inside the fully sharp right-hand region.
    private static void DrawHandTrackingPreview(CanvasDrawingSession ds, float span)
    {
        ds.Clear(AppPalette.Background);
        var center = new Vector2(span - 82, 84);
        for (float x = center.X % 32; x < span; x += 32)
            ds.DrawLine(x, 0, x, MenuPreviewUnits, AppPalette.GridLine, Math.Abs(x - center.X) < 1 ? 2 : 1);
        for (float y = 84 % 32; y < MenuPreviewUnits; y += 32)
            ds.DrawLine(0, y, span, y, AppPalette.GridLine, Math.Abs(y - 84) < 1 ? 2 : 1);
        using var light = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(236, 241, 246) },
            new() { Position = .62f, Color = ThemeColor(214, 223, 232) },
            new() { Position = 1, Color = ThemeColor(214, 223, 232, 0) }
        ]) { Center = center, RadiusX = 70, RadiusY = 70 };
        ds.FillCircle(center, 70, light);
        // A palm-down hand with its four fingers grouped, as boards expect.
        (Vector2 Base, Vector2 Tip)[] fingers =
        [
            (new(-17, 12), new(-21, -28)), (new(-5, 10), new(-7, -38)),
            (new(7, 10), new(7, -35)), (new(18, 13), new(20, -22))
        ];
        (Vector2 Base, Vector2 Tip) thumb = (new(-24, 32), new(-44, 8));
        using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        DrawHand(new Vector2(3, 4), null, ThemeColor(40, 52, 68, 70));
        using var skin = new CanvasLinearGradientBrush(ds.Device, ThemeColor(226, 184, 156), ThemeColor(191, 142, 114))
        { StartPoint = center + new Vector2(0, -40), EndPoint = center + new Vector2(0, 60) };
        DrawHand(Vector2.Zero, skin, default);
        foreach (var finger in fingers)
        {
            var direction = Vector2.Normalize(finger.Tip - finger.Base);
            var nail = center + finger.Tip - direction * 4.5f;
            ds.FillEllipse(nail, 3.6f, 4.4f, ThemeColor(241, 214, 198));
            ds.DrawLine(center + finger.Base + direction * 12 - new Vector2(3, 0),
                center + finger.Base + direction * 12 + new Vector2(3, 0), ThemeColor(168, 120, 96, 150), .8f);
        }
        for (int index = 0; index < fingers.Length; index++)
        {
            var marker = center + fingers[index].Tip;
            var ring = index == 1 ? ThemeColor(233, 190, 83) : AppPalette.IndicatorOn;
            ds.DrawCircle(marker, 7.5f, ring, 2.2f);
            if (index == 1) ds.FillCircle(marker, 2.4f, ring);
        }

        void DrawHand(Vector2 offset, ICanvasBrush? brush, Color color)
        {
            var origin = center + offset;
            using var palm = CanvasGeometry.CreateRoundedRectangle(ds.Device,
                new Rect(origin.X - 26, origin.Y + 8, 52, 70), 20, 20);
            if (brush is null) ds.FillGeometry(palm, color); else ds.FillGeometry(palm, brush);
            foreach (var (from, to) in fingers.Append(thumb))
                if (brush is null) ds.DrawLine(origin + from, origin + to, color, from == thumb.Base ? 13 : 11.6f, round);
                else ds.DrawLine(origin + from, origin + to, brush, from == thumb.Base ? 13 : 11.6f, round);
        }
    }

    private static void DrawPhotoCopyPreview(CanvasDrawingSession ds, float span)
    {
        ds.Clear(AppPalette.PhotoCopyBackground);
        var center = new Vector2(span - 86, 80);
        // A patch of Swirl's square spiral: copies turn their tops inward.
        for (int row = -2; row <= 2; row++)
        for (int column = -4; column <= 2; column++)
        {
            if (row == 0 && column == 0) continue;
            var position = center + new Vector2(column * 46, row * 42);
            if (position.X < span - 210) continue;
            var inward = center - position;
            DrawCopy(position, MathF.Atan2(inward.X, -inward.Y), 1.02f);
        }
        // The grey surface falls into shadow beneath the tile's captions.
        using var shade = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = .30f, Color = ThemeColor(18, 20, 24, 0) },
            new() { Position = 1, Color = ThemeColor(18, 20, 24, 170) }
        ]) { Center = center, RadiusX = 250, RadiusY = 170 };
        ds.FillRectangle(new Rect(0, 0, span, MenuPreviewUnits), shade);
        using var glow = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(255, 255, 255, 170), ThemeColor(255, 255, 255, 0))
        { Center = center, RadiusX = 44, RadiusY = 44 };
        ds.FillCircle(center, 44, glow);
        DrawCopy(center, -.16f, 1.3f);

        void DrawCopy(Vector2 position, float angle, float scale)
        {
            var previous = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(angle) *
                Matrix3x2.CreateTranslation(position) * previous;
            try
            {
                ds.FillRoundedRectangle(new Rect(-10, -13, 23, 31), 3, 3, ThemeColor(20, 20, 20, 70));
                using var cover = new CanvasLinearGradientBrush(ds.Device, ThemeColor(52, 128, 84), ThemeColor(24, 84, 54))
                { StartPoint = new(-12, -16), EndPoint = new(12, 16) };
                ds.FillRoundedRectangle(new Rect(-12, -16, 24, 32), 3, 3, cover);
                ds.FillRectangle(new Rect(-12, -14, 3.5, 28), ThemeColor(18, 62, 40));
                ds.FillRoundedRectangle(new Rect(-5, -11, 12, 7), 1.2f, 1.2f, ThemeColor(236, 229, 205));
                ds.DrawLine(7.5f, -16, 7.5f, 16, ThemeColor(24, 24, 24), 2);
            }
            finally { ds.Transform = previous; }
        }
    }

    private static void DrawBlackjackPreview(CanvasDrawingSession ds, float span)
    {
        // The table's own felt, viewed inside its rail around the dealer arc.
        float scale = MenuPreviewUnits / 330;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-(940 - span / scale), -430) * Matrix3x2.CreateScale(scale) * previous;
        DrawCasinoFelt(ds);
        ds.Transform = previous;

        DrawChip(new Vector2(span - 176, 124), ThemeColor(147, 60, 70), "50");
        DrawChip(new Vector2(span - 150, 134), ThemeColor(37, 93, 153), "10");
        DrawCard(new BlackjackCard(1, BlackjackSuit.Spades), new Vector2(span - 104, 82), -11);
        DrawCard(new BlackjackCard(13, BlackjackSuit.Hearts), new Vector2(span - 58, 78), 9);

        void DrawCard(BlackjackCard card, Vector2 center, float degrees)
        {
            var saved = ds.Transform;
            ds.Transform = Matrix3x2.CreateRotation(degrees * MathF.PI / 180, center) * saved;
            try { DrawCasinoCard(ds, card, new Rect(center.X - 31, center.Y - 42.5f, 62, 85)); }
            finally { ds.Transform = saved; }
        }
        void DrawChip(Vector2 center, Color body, string value)
        {
            var saved = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(.56f, center) * saved;
            try { DrawCasinoChip(ds, center, 32, body, value, false); }
            finally { ds.Transform = saved; }
        }
    }

    // A short run of the real fluid simulation. The caller disposes it after
    // the preview's drawing session has finished using its fields.
    private static PaintFluidSimulation DrawPaintPreview(CanvasDrawingSession ds, float span)
    {
        int fieldHeight = 96, fieldWidth = Math.Clamp((int)MathF.Round(fieldHeight * span / MenuPreviewUnits), 32, 512);
        var fluid = new PaintFluidSimulation(ds.Device, fieldWidth, fieldHeight, fieldWidth / (double)fieldHeight);
        // Positions are units from the right edge; radii are fractions of height.
        (float FromRight, float Y, float Radius, int Pigment)[] drops =
        [
            (30, .20f, .10f, 0), (70, .34f, .09f, 1), (112, .18f, .08f, 2), (52, .62f, .10f, 3),
            (96, .72f, .09f, 6), (140, .50f, .08f, 5), (22, .88f, .08f, 4), (16, .48f, .07f, 7),
            (84, .04f, .07f, 6), (126, .92f, .07f, 0), (170, .30f, .07f, 1), (60, .96f, .06f, 2),
            (180, .78f, .06f, 3), (44, .42f, .06f, 5)
        ];
        for (int index = 0; index < drops.Length; index++)
            fluid.AddDrop(new(1 - drops[index].FromRight / span, drops[index].Y), drops[index].Radius,
                PaintPigments[drops[index].Pigment], 1, 101 + index * 37);
        // Four seconds lets the mounds relax into overlapping coats.
        for (int step = 0; step < 60; step++) fluid.Advance(1 / 15.0);
        fluid.Draw(ds, new Rect(0, 0, span, MenuPreviewUnits));
        return fluid;
    }

    private static void DrawMonopolyPreview(CanvasDrawingSession ds, float span)
    {
        // The GO corner of the real board: rail, gold trim, tiles and felt.
        // Only the tiles nearest GO are laid, so the dark rail bed rather than
        // bright paper runs beneath the tile's captions.
        float scale = MenuPreviewUnits / 270;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-(990 - span / scale), -720) * Matrix3x2.CreateScale(scale) * previous;
        try
        {
            var game = MenuPreviewMonopoly.Value;
            DrawMonopolyFrame(ds);
            foreach (int index in new[] { 0, 1, 38, 39 })
                DrawMonopolySpace(ds, MonopolyGame.Spaces[index], game, 1);
            for (int slot = 0; slot < 2; slot++)
                DrawMonopolyToken(ds, Vector2.Transform(MonopolyLocalTokenCenter(0, slot, 2), MonopolySpaceTransform(0)),
                    7.2f, slot * 2, false);
            DrawMonopolyDie(ds, new Rect(690, 752, 42, 42), 5, MonopolyIvory, MonopolyInk);
            DrawMonopolyDie(ds, new Rect(746, 766, 42, 42), 2, MonopolyIvory, MonopolyInk);
        }
        finally { ds.Transform = previous; }
    }

    private void DrawGlobePreview(CanvasDrawingSession ds, float span)
    {
        ds.Clear(ThemeColor(2, 5, 11));
        // The Globe shader draws its sphere and sky in a 1000-unit frame.
        const float zoom = .3f, radius = 66;
        float scale = radius / (335 * zoom);
        var center = new Vector2(span - 80, 80);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-500, -500) * Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            GetGlobeRenderer(ds.Device).Draw(ds, zoom, (float)_boardSession.GlobeHomeRotationDegrees, 1,
                (float)_boardSession.GlobeHomeLatitudeDegrees);
        }
        finally { ds.Transform = previous; }
    }
}
