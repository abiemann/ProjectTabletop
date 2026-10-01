using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private bool DrawSlotChestArtwork(CanvasDrawingSession ds, Rect box, bool open, float aspect = 1)
    {
        EnsureSlotArtwork(ds.Device);
        if (_slotVaultChestArtwork is null) return false;
        // Registered equal frames keep the body, feet and chains in place when
        // a key releases the lid. Transparent gutters contain every contour.
        double sx = _slotVaultChestArtwork.Size.Width / 1536;
        double sy = _slotVaultChestArtwork.Size.Height / 1024;
        var source = new Rect((24 + (open ? 768 : 0)) * sx, 188 * sy, 728 * sx, 628 * sy);
        DrawSlotTreasureSprite(ds, _slotVaultChestArtwork, source, box, aspect);
        return true;
    }

    private bool DrawSlotKeyArtwork(CanvasDrawingSession ds, Rect box, float aspect = 1)
    {
        EnsureSlotArtwork(ds.Device);
        if (_slotVaultKeyArtwork is null) return false;
        double sx = _slotVaultKeyArtwork.Size.Width / 1024;
        double sy = _slotVaultKeyArtwork.Size.Height / 1536;
        DrawSlotTreasureSprite(ds, _slotVaultKeyArtwork,
            new Rect(70 * sx, 10 * sy, 918 * sx, 1508 * sy), box, aspect);
        return true;
    }

    private bool DrawSlotKeyChestArtwork(CanvasDrawingSession ds, Rect box, bool open, float aspect = 1)
    {
        EnsureSlotArtwork(ds.Device);
        if (_slotVaultChestFrontArtwork is null) return false;
        // A dedicated orthographic front retains level lid and chain details.
        // Crop the chain at the reel edges instead of fitting a small chest
        // inside the rail. Both states retain the same registered view.
        double sx = _slotVaultChestFrontArtwork.Size.Width / 1536;
        double sy = _slotVaultChestFrontArtwork.Size.Height / 1024;
        var crop = SlotKeyChestSource(box, open, aspect);
        var source = new Rect(crop.X * sx, crop.Y * sy, crop.Width * sx, crop.Height * sy);
        using var window = CanvasGeometry.CreateRoundedRectangle(ds.Device, box, 1 / aspect, 1);
        ds.FillGeometry(window, ThemeColor(21, 12, 8));
        using (ds.CreateLayer(1, window))
            ds.DrawImage(_slotVaultChestFrontArtwork, box, source, 1, CanvasImageInterpolation.HighQualityCubic);
        ds.DrawGeometry(window, ThemeColor(48, 29, 13), 1.2f);
        ds.DrawGeometry(window, ThemeColor(191, 145, 65, 190), .55f);
        return true;
    }

    private static Rect SlotKeyChestSource(Rect box, bool open, float aspect)
    {
        // Both padlocks are centered at master x768. A fixed vertical zoom
        // keeps its size across board shapes; narrower reels expose less of
        // the front instead of shrinking or stretching the lock and links.
        // This band also contains the released lock's tip and the gold seam.
        const double height = 400;
        double width = box.Width * aspect * height / box.Height;
        return new(768 - width / 2, 56 + (open ? 512 : 0), width, height);
    }

    private static void DrawSlotTreasureSprite(CanvasDrawingSession ds, CanvasBitmap bitmap,
        Rect source, Rect box, float aspect)
    {
        double fit = Math.Min(box.Width * aspect / source.Width, box.Height / source.Height);
        double width = source.Width * fit / aspect, height = source.Height * fit;
        ds.DrawImage(bitmap, new Rect(box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height),
            source, 1, CanvasImageInterpolation.HighQualityCubic);
    }

    private static Rect SlotKeyChestBox(SlotLayout layout, int reel)
    {
        double width = layout.CellWidth - 2 / layout.Aspect;
        const double height = 56;
        // Leave the gold reel frame clear and stop just above the win rail.
        return new(layout.ReelCenter(reel) - width / 2, SlotWinMessageBounds.Y - height - 2, width, height);
    }

    private static Rect SlotEarnedKeyBox(SlotLayout layout, int reel)
    {
        var chest = SlotKeyChestBox(layout, reel);
        double width = Math.Min(26, chest.Width * layout.Aspect * .22);
        double height = width * 40 / 26;
        return new(chest.Right - (width + 4) / layout.Aspect, chest.Y + (chest.Height - height) / 2,
            width / layout.Aspect, height);
    }

    private void DrawSlotKeyChestLight(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        for (int reel = 0; reel < SlotGame.Reels; reel++)
        {
            if (!game.Keys[reel]) continue;
            var box = SlotKeyChestBox(layout, reel);
            float breathe = .72f + .18f * MathF.Sin(_slotVfxTime * 1.8f + reel * 1.7f)
                + .1f * MathF.Sin(_slotVfxTime * 4.1f + reel);
            if (!DrawSlotKeyChestArtwork(ds, box, open: true, layout.Aspect)) continue;
            // Master (768,675) is the exposed gold seam in the ajar frame.
            // Map the light through the same camera window as its artwork.
            var source = SlotKeyChestSource(box, open: true, layout.Aspect);
            float fit = (float)(box.Height / source.Height);
            var origin = new Vector2((float)(box.X + (768 - source.X) * fit / layout.Aspect),
                (float)(box.Y + (675 - source.Y) * fit));
            using var window = CanvasGeometry.CreateRoundedRectangle(ds.Device,
                new Rect(box.X + 1 / layout.Aspect, box.Y + 1, box.Width - 2 / layout.Aspect, box.Height - 2),
                1 / layout.Aspect, 1);
            using (ds.CreateLayer(1, window))
            {
                float lightWidth = Math.Min(45, (float)box.Width * layout.Aspect * .4f);
                using var light = new CanvasRadialGradientBrush(ds.Device,
                    ThemeColor(255, 198, 57, (byte)(breathe * 36)), ThemeColor(255, 163, 17, 0))
                { Center = origin, RadiusX = lightWidth / layout.Aspect, RadiusY = 6 };
                ds.FillEllipse(origin, lightWidth / layout.Aspect, 6, light);
                float phase = (_slotVfxTime / 4.6f + reel * .211f) % 1;
                if (phase < .13f)
                    DrawSlotHoardGlint(ds, origin + new Vector2(-8 / layout.Aspect, -1), layout.Aspect,
                        MathF.Pow(MathF.Sin(phase / .13f * MathF.PI), 2) * .72f);
            }
            DrawSlotKeyArtwork(ds, SlotEarnedKeyBox(layout, reel), layout.Aspect);
        }
    }
}
