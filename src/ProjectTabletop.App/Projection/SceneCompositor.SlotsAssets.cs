using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasBitmap? _slotArtwork;
    private CanvasBitmap? _slotBackdrop;
    private CanvasBitmap? _slotTreasureArtwork;
    private CanvasBitmap? _slotDragonArtwork;
    private CanvasBitmap? _slotWildPortraits;
    private CanvasBitmap? _slotWildColossus;
    private CanvasBitmap? _slotMenuDragonArtwork;
    private CanvasBitmap? _slotCoinPileArtwork;
    private CanvasBitmap? _slotVaultChestArtwork;
    private CanvasBitmap? _slotVaultKeyArtwork;
    private CanvasDevice? _slotArtworkDevice;
    private bool _slotArtworkAttempted;
    private string? _slotArtworkError;
    private string? _slotMenuArtworkError;
    private string? _slotCabinetArtworkError;
    private readonly Dictionary<SlotSymbol, Rect> _slotArtworkBounds = [];

    // Original illustrated art in twelve nominal grid cells. Typography is
    // rendered separately, keeping payouts and WILD sharp and readable at 4K.
    private static readonly SlotSymbol[] SlotArtworkOrder =
    [
        SlotSymbol.Dagger, SlotSymbol.Goblet, SlotSymbol.Chest, SlotSymbol.Crown,
        SlotSymbol.Wild, SlotSymbol.Coin, SlotSymbol.EggGreen, SlotSymbol.EggRed,
        SlotSymbol.EggBlue, SlotSymbol.EggRainbow, SlotSymbol.Elixir, SlotSymbol.Key
    ];
    // Authored crop map: the dagger and bottle deliberately cross the nominal
    // atlas rows by a few pixels. Explicit gutters retain their complete edges
    // without sampling a neighbour. Coordinates are for the 1448 x 1086 master.
    private static readonly Rect[] SlotArtworkRegions =
    [
        new(24, 24, 352, 342), new(425, 38, 248, 325), new(728, 38, 350, 326), new(1082, 38, 358, 325),
        new(16, 372, 348, 345), new(375, 374, 350, 341), new(745, 381, 306, 329), new(1116, 380, 297, 334),
        new(39, 717, 309, 344), new(394, 717, 313, 344), new(774, 710, 253, 357), new(1095, 716, 346, 342)
    ];

    private static readonly Rect[] SlotTreasureRegions =
    [
        new(40, 75, 612, 522), new(680, 70, 537, 530),
        new(50, 672, 557, 541), new(660, 669, 556, 544)
    ];

    internal bool SlotsArtworkReady => _slotArtwork is not null && _slotBackdrop is not null
        && _slotTreasureArtwork is not null && _slotDragonArtwork is not null
        && _slotWildPortraits is not null && _slotWildColossus is not null && SlotsCabinetArtworkReady;
    internal string? SlotsArtworkError => _slotArtworkError ?? _slotCabinetArtworkError;
    internal bool SlotsCabinetArtworkReady => _slotCoinPileArtwork is not null
        && _slotVaultChestArtwork is not null && _slotVaultKeyArtwork is not null;
    internal bool SlotsMenuArtworkReady => _slotMenuDragonArtwork is not null;
    internal string? SlotsMenuArtworkError => _slotMenuArtworkError;

    private void EnsureSlotArtwork(CanvasDevice device)
    {
        if (_slotArtworkDevice != device)
        {
            DisposeSlotArtwork();
            _slotArtworkDevice = device;
        }
        if (_slotArtworkAttempted) return;
        _slotArtworkAttempted = true;
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "SlotsRendering", "Assets");
            // One decode at first use, reused by subsequent frames. Loading the
            // complete atlas before caching a sprite avoids late image swaps in
            // the main menu's camera reference.
            _slotArtwork = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-symbols.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotBackdrop = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "dragon-sanctum.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotTreasureArtwork = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-treasures.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotDragonArtwork = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-dragons.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotWildPortraits = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-wild-portraits.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotWildColossus = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-wild-colossus.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            SetSlotArtworkBounds();
        }
        catch (Exception error) when (!device.IsDeviceLost(error.HResult))
        {
            _slotArtworkError = error.Message;
            _slotArtwork?.Dispose();
            _slotBackdrop?.Dispose();
            _slotTreasureArtwork?.Dispose();
            _slotDragonArtwork?.Dispose();
            _slotWildPortraits?.Dispose(); _slotWildColossus?.Dispose();
            _slotArtwork = _slotBackdrop = null;
            _slotTreasureArtwork = null;
            _slotDragonArtwork = null;
            _slotWildPortraits = _slotWildColossus = null;
            _slotArtworkBounds.Clear();
            AppLog.Write("Dragon Slots artwork", error);
        }
        // The menu illustration is optional and independent of game artwork:
        // a missing thumbnail must not discard the reels' successfully loaded
        // atlases. Resolve it before caching the tile, never midway through an
        // unchanged menu's camera reference.
        try
        {
            _slotMenuDragonArtwork = CanvasBitmap.LoadAsync(device,
                Path.Combine(AppContext.BaseDirectory, "SlotsRendering", "Assets", "slot-menu-dragon.png"), 96)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (!device.IsDeviceLost(error.HResult))
        {
            _slotMenuArtworkError = error.Message;
            AppLog.Write("Dragon Slots menu artwork", error);
        }
        // These sprites have their own load boundary: a missing new cabinet
        // asset must not discard the already decoded dragons and reel atlases.
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "SlotsRendering", "Assets");
            _slotCoinPileArtwork = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-coin-pile.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotVaultChestArtwork = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-vault-chests.png"), 96)
                .AsTask().GetAwaiter().GetResult();
            _slotVaultKeyArtwork = CanvasBitmap.LoadAsync(device, Path.Combine(directory, "slot-vault-key.png"), 96)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (!device.IsDeviceLost(error.HResult))
        {
            _slotCabinetArtworkError = error.Message;
            AppLog.Write("Dragon Slots treasure artwork", error);
        }
    }

    private void SetSlotArtworkBounds()
    {
        var atlas = _slotArtwork!;
        double xScale = atlas.SizeInPixels.Width / 1448.0, yScale = atlas.SizeInPixels.Height / 1086.0;
        for (int index = 0; index < SlotArtworkOrder.Length; index++)
        {
            var source = SlotArtworkRegions[index];
            _slotArtworkBounds[SlotArtworkOrder[index]] = new Rect(source.X * xScale, source.Y * yScale,
                source.Width * xScale, source.Height * yScale);
        }
    }

    private bool DrawIllustratedSlotSymbol(CanvasDrawingSession ds, SlotSymbol symbol)
    {
        EnsureSlotArtwork(ds.Device);
        if (symbol == SlotSymbol.Wild && _slotWildPortraits is not null)
        {
            DrawSlotWildTile(ds, new Rect(3, 3, 94, 94), 1, -1, 1);
            return true;
        }
        if (symbol == SlotSymbol.Chest && DrawSlotChestArtwork(ds, new Rect(4, 4, 92, 92), open: true)) return true;
        if (symbol == SlotSymbol.Key && DrawSlotKeyArtwork(ds, new Rect(4, 4, 92, 92))) return true;
        if (symbol == SlotSymbol.Chest && DrawSlotTreasure(ds, 0, new Rect(4, 4, 92, 92))) return true;
        if (_slotArtwork is null || !_slotArtworkBounds.TryGetValue(symbol, out var source)) return false;
        double scale = 92 / Math.Max(source.Width, source.Height);
        double width = source.Width * scale, height = source.Height * scale;
        ds.DrawImage(_slotArtwork, new Rect((100 - width) / 2, (100 - height) / 2, width, height), source,
            1, CanvasImageInterpolation.HighQualityCubic);
        if (symbol == SlotSymbol.Wild)
        {
            // The generated art contains no lettering. This label remains crisp
            // and recognisable when the dragon eye lands on a small reel.
            var plate = new Rect(17, 74, 66, 20);
            ds.FillRoundedRectangle(plate, 4, 4, ThemeColor(14, 6, 5, 235));
            ds.DrawRoundedRectangle(plate, 4, 4, SlotDeepGold, 1);
            SlotText(ds, "WILD", plate, 17, SlotGold, 1, "Georgia", true, fire: true);
        }
        return true;
    }

    private bool DrawSlotTreasure(CanvasDrawingSession ds, int index, Rect destination)
    {
        EnsureSlotArtwork(ds.Device);
        if (_slotTreasureArtwork is null) return false;
        var crop = SlotTreasureRegions[index];
        double scaleX = _slotTreasureArtwork.Size.Width / 1254, scaleY = _slotTreasureArtwork.Size.Height / 1254;
        var source = new Rect(crop.X * scaleX, crop.Y * scaleY, crop.Width * scaleX, crop.Height * scaleY);
        double fit = Math.Min(destination.Width / source.Width, destination.Height / source.Height);
        double width = source.Width * fit, height = source.Height * fit;
        ds.DrawImage(_slotTreasureArtwork,
            new Rect(destination.X + (destination.Width - width) / 2, destination.Y + (destination.Height - height) / 2, width, height),
            source, 1, CanvasImageInterpolation.HighQualityCubic);
        return true;
    }

    private void DrawSlotOpenChest(CanvasDrawingSession ds, Rect box)
    {
        EnsureSlotArtwork(ds.Device);
        if (DrawSlotChestArtwork(ds, box, open: true, (float)PaintBoardAspect())) return;
        if (_slotArtwork is null || !_slotArtworkBounds.TryGetValue(SlotSymbol.Chest, out var source))
        {
            DrawSlotArt(ds, SlotSymbol.Chest, box, 1);
            return;
        }
        // Preserve the same square physical coordinate system as reel sprites.
        double width = source.Width / Math.Max(source.Width, source.Height) * box.Width * .92;
        double height = source.Height / Math.Max(source.Width, source.Height) * box.Height * .92;
        ds.DrawImage(_slotArtwork, new Rect(box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height),
            source, 1, CanvasImageInterpolation.HighQualityCubic);
    }

    private bool DrawSlotBackdrop(CanvasDrawingSession ds)
    {
        EnsureSlotArtwork(ds.Device);
        if (_slotBackdrop is null) return false;
        // Cover without distortion at the physical board aspect. The cavern's
        // quiet center leaves the machine's contrast and typography intact.
        var source = SlotBackdropSource(PaintBoardAspect());
        ds.DrawImage(_slotBackdrop, new Rect(0, 0, 1000, 1000),
            source,
            1, CanvasImageInterpolation.HighQualityCubic);
        return true;
    }

    private Rect SlotBackdropSource(double aspect)
    {
        double width = _slotBackdrop!.Size.Width, height = _slotBackdrop.Size.Height;
        double cropWidth = Math.Min(width, height * aspect), cropHeight = cropWidth / aspect;
        return new((width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight);
    }

    private void DisposeSlotArtwork()
    {
        _slotArtwork?.Dispose(); _slotBackdrop?.Dispose(); _slotTreasureArtwork?.Dispose(); _slotDragonArtwork?.Dispose();
        _slotWildPortraits?.Dispose(); _slotWildColossus?.Dispose();
        _slotMenuDragonArtwork?.Dispose();
        _slotCoinPileArtwork?.Dispose(); _slotVaultChestArtwork?.Dispose(); _slotVaultKeyArtwork?.Dispose();
        _slotArtwork = _slotBackdrop = null;
        _slotTreasureArtwork = null;
        _slotDragonArtwork = null;
        _slotWildPortraits = _slotWildColossus = null;
        _slotMenuDragonArtwork = null;
        _slotCoinPileArtwork = _slotVaultChestArtwork = _slotVaultKeyArtwork = null;
        _slotArtworkDevice = null;
        _slotArtworkAttempted = false;
        _slotArtworkError = null;
        _slotMenuArtworkError = null;
        _slotCabinetArtworkError = null;
        _slotArtworkBounds.Clear();
    }
}
