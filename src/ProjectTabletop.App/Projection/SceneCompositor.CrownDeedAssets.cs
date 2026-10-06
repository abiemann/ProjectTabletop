using Microsoft.Graphics.Canvas;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private BitmapAssetSet? _crownDeedAssetSet;
    private bool _crownDeedResourcesPublished;
    private bool _crownDeedResourceFailureLogged;
    private bool CrownDeedResourcesReady => _crownDeedResourcesPublished;
    private string? CrownDeedResourcesError => _crownDeedAssetSet?.Error;

    internal async Task EnsureCrownDeedResourcesAsync(CanvasDevice device)
    {
        BitmapAssetSet resources;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            resources = GetCrownDeedResources(device);
        }
        await resources.EnsureLoadedAsync().ConfigureAwait(false);
        lock (_gate)
        {
            // A replacement device or disposal owns a different set. Never
            // publish late images back into that scene's stationary reference.
            if (!_disposed && ReferenceEquals(resources, _crownDeedAssetSet))
                PrepareCrownDeedResources(device);
        }
    }

    private BitmapAssetSet GetCrownDeedResources(CanvasDevice device)
    {
        if (_crownDeedAssetSet?.Device != device)
        {
            DisposeCrownDeedResources();
            string[] files = ["crown-deed-city.png", .. CrownDeedPieceSources.Pieces.Select(piece => piece.File)];
            _crownDeedAssetSet = new(device,
                Path.Combine(AppContext.BaseDirectory, "Assets", "CrownDeed"), files);
            InvalidateBoardArtworkSurface();
        }
        return _crownDeedAssetSet!;
    }

    private bool PrepareCrownDeedResources(CanvasDevice device)
    {
        var resources = GetCrownDeedResources(device);
        if (_crownDeedResourcesPublished) return true;
        if (!resources.IsLoaded)
        {
            if (resources.Error is { } error && !_crownDeedResourceFailureLogged)
            {
                AppLog.Write("Crown & Deed artwork loading", new IOException(error));
                _crownDeedResourceFailureLogged = true;
            }
            return false;
        }

        // Borrow the whole completed set on the scene thread. The set owns all
        // bitmap disposal; frames never decode files or inspect bitmap pixels.
        _crownDeedCityBitmap = resources.Image("crown-deed-city.png");
        _crownDeedCityDevice = device;
        _crownDeedPieceBitmaps = new CanvasBitmap?[CrownDeedPieceSources.Pieces.Length];
        _crownDeedPieceSourceBounds = CrownDeedPieceSources.Pieces.Select(piece => piece.Bounds).ToArray();
        for (int index = 0; index < CrownDeedPieceSources.Pieces.Length; index++)
        {
            var source = CrownDeedPieceSources.Pieces[index];
            var bitmap = resources.Image(source.File);
            if (bitmap is not null && (bitmap.SizeInPixels.Width != source.Width || bitmap.SizeInPixels.Height != source.Height))
            {
                AppLog.Write("Crown & Deed piece metadata", new InvalidDataException(
                    source.File + " has changed dimensions; regenerate its offline source bounds."));
                bitmap = null;
            }
            _crownDeedPieceBitmaps[index] = bitmap;
        }
        _crownDeedPieceDevice = device;
        foreach (var error in resources.Errors)
            AppLog.Write("Crown & Deed artwork: " + error.Key, new IOException(error.Value));
        _crownDeedResourcesPublished = true;
        InvalidateBoardArtworkSurface();
        return true;
    }

    private void DisposeCrownDeedResources()
    {
        DisposeMonopolyEntranceLayers();
        DisposeCrownDeedPieces();
        _crownDeedCityBitmap = null;
        _crownDeedCityDevice = null;
        _crownDeedAssetSet?.Dispose();
        _crownDeedAssetSet = null;
        _crownDeedResourcesPublished = false;
        _crownDeedResourceFailureLogged = false;
    }
}
