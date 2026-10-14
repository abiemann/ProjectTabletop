using System.Numerics;
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
        BoardScreen.CrownDeed => CrownDeedResourcesReady,
        BoardScreen.WaterGarden => WaterGardenArtworkReady,
        BoardScreen.Football => FootballArtworkReady,
        BoardScreen.StrykersRun => StrykersRunArtworkReady,
        _ => true
    };

    internal Task EnsureBoardArtworkResourcesAsync(CanvasDevice device, BoardScreen screen) => screen switch
    {
        BoardScreen.Slots => EnsureSlotResourcesAsync(device),
        BoardScreen.Roulette => EnsureRouletteResourcesAsync(device),
        BoardScreen.CrownDeed => EnsureCrownDeedResourcesAsync(device),
        BoardScreen.WaterGarden => EnsureWaterGardenResourcesAsync(device),
        BoardScreen.Football => EnsureFootballResourcesAsync(device),
        BoardScreen.StrykersRun => EnsureStrykersRunResourcesAsync(device),
        _ => Task.CompletedTask
    };

    private bool PrepareBoardArtwork(CanvasDevice device) => _boardSession.Screen switch
    {
        BoardScreen.Slots => PrepareSlotResources(device),
        BoardScreen.Roulette => PrepareRouletteResources(device),
        BoardScreen.CrownDeed => PrepareCrownDeedResources(device),
        BoardScreen.WaterGarden => PrepareWaterGardenResources(device),
        BoardScreen.Football => PrepareFootballResources(device),
        BoardScreen.StrykersRun => PrepareStrykersRunResources(device),
        _ => true
    };

    private void InvalidateBoardArtworkSurface()
    {
        _renderedBoardState = null;
        _acquisitionScene = null;
        _acquisitionExpectedScene = null;
        _holdSceneKey = null;
        _holdExpectedScene = null;
        _blackjackPreviewRevision = -1;
        _slotsPreviewKey = null;
        _roulettePreviewKey = null;
        _crownDeedPreviewState = null;
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
                ClearHandTipsCore(resetInput: true, cancelCrownDeedEntrance: false);
        }
        return false;
    }

    private void DrawArtworkLoading(CanvasDrawingSession drawing, Rect bounds, bool rotate180 = false)
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
            BoardScreen.CrownDeed => CrownDeedResourcesError,
            BoardScreen.WaterGarden => WaterGardenArtworkError,
            BoardScreen.Football => _footballRenderer?.PitchError,
            BoardScreen.StrykersRun => _strykersRunRenderer?.Error,
            _ => null
        };
        var previous = drawing.Transform;
        if (rotate180)
            drawing.Transform = Matrix3x2.CreateScale(-1, -1,
                new Vector2((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2))) * previous;
        try
        {
            drawing.DrawText(error is null ? "Loading…" : "Could not load board",
                bounds, AppPalette.MutedText, format);
        }
        finally { drawing.Transform = previous; }
    }
}
