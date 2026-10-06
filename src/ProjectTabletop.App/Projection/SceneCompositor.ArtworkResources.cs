using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private bool _artworkInputPending;
    private BoardScreen _artworkInputPendingScreen;
    private bool CurrentBoardArtworkReady => _boardSession.Screen switch
    {
        BoardScreen.Slots => _slotArtworkAttempted,
        BoardScreen.Roulette => _rouletteArtworkPublished,
        BoardScreen.Monopoly => CrownDeedResourcesReady,
        _ => true
    };

    internal Task EnsureBoardArtworkResourcesAsync(CanvasDevice device, BoardScreen screen) => screen switch
    {
        BoardScreen.Slots => EnsureSlotResourcesAsync(device),
        BoardScreen.Roulette => EnsureRouletteResourcesAsync(device),
        BoardScreen.Monopoly => EnsureCrownDeedResourcesAsync(device),
        _ => Task.CompletedTask
    };

    private bool PrepareBoardArtwork(CanvasDevice device) => _boardSession.Screen switch
    {
        BoardScreen.Slots => PrepareSlotResources(device),
        BoardScreen.Roulette => PrepareRouletteResources(device),
        BoardScreen.Monopoly => PrepareCrownDeedResources(device),
        _ => true
    };

    private void InvalidateBoardArtworkSurface()
    {
        _renderedBoardState = null;
        _acquisitionScene = null;
        _acquisitionExpectedScene = null;
        _holdSceneKey = null;
        _holdExpectedScene = null;
        _slotsPreviewKey = null;
        _roulettePreviewKey = null;
        _monopolyPreviewState = null;
    }

    private bool BlockBoardArtworkInput()
    {
        if (!CurrentBoardArtworkReady)
        {
            _artworkInputPending = true;
            _artworkInputPendingScreen = _boardSession.Screen;
            return true;
        }
        if (_artworkInputPending)
        {
            _artworkInputPending = false;
            if (_artworkInputPendingScreen == _boardSession.Screen)
                ClearHandTipsCore(resetInput: true, cancelMonopolyEntrance: false);
        }
        return false;
    }

    private void DrawArtworkLoading(CanvasDrawingSession drawing, Rect bounds)
    {
        drawing.FillRectangle(bounds, AppPalette.Background);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 24,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        string? error = _boardSession.Screen switch
        {
            BoardScreen.Slots => _slotImages?.Error,
            BoardScreen.Roulette => _rouletteImages?.Error,
            BoardScreen.Monopoly => CrownDeedResourcesError,
            _ => null
        };
        drawing.DrawText(error is null ? "Loading…" : "Could not load board",
            bounds, AppPalette.MutedText, format);
    }
}
