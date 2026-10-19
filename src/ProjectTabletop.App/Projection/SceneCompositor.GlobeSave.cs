using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal sealed record GlobeMemoryImage(long SaveId, long Navigation, int Width, int Height, byte[] BgraPixels);
    private sealed record GlobeSaveRequest(long Navigation, GlobeSnapshot State, double Aspect);
    private GlobeSaveRequest? _globeSaveRequest;
    private long _globeSaveId, _globeSaveStatusNavigation = -1;
    private bool _globeSaving;
    private string? _globeSaveError;
    private DateTimeOffset _globeSavedUntil;

    private void SyncGlobeSaveState()
    {
        if (_globeSaveRequest is { } request &&
            (_boardSession.Screen != BoardScreen.Globe || request.Navigation != _boardSession.NavigationRevision))
        {
            _globeSaveRequest = null;
            _globeSaving = false;
        }
        _boardSession.GlobeSaveEnabled = !_disposed && !_globeSaving &&
            _boardSession.Screen == BoardScreen.Globe && _globeRenderer?.IsReady == true &&
            !_globeRenderer.IsLoading && _globeRenderer.LoadedModel == _boardSession.SelectedGlobeModel;
    }

    // Record the exact presentation state on acceptance, without GPU readback
    // on the caption worker. The UI consumes it once, then encodes off-thread.
    private void QueueGlobeSaveRequest()
    {
        SyncGlobeSaveState();
        if (!_boardSession.GlobeSaveEnabled) return;
        _globeSaveRequest = new(_boardSession.NavigationRevision,
            _boardSession.GetGlobeSnapshot(_globeClock()), PaintBoardAspect());
        _globeSaving = true;
        _globeSaveId++;
        _globeSaveStatusNavigation = _boardSession.NavigationRevision;
        _globeSaveError = null;
        _globeSavedUntil = default;
        SyncGlobeSaveState();
    }

    internal bool TryTakeGlobeSaveRequest(out GlobeMemoryImage image)
    {
        lock (_gate)
        {
            image = null!;
            SyncGlobeSaveState();
            if (_globeSaveRequest is not { } request) return false;
            _globeSaveRequest = null;
            try
            {
                if (_disposed || _globeRenderer?.IsReady != true || _globeRenderer.IsLoading ||
                    _globeRenderer.LoadedModel != request.State.Model ||
                    _boardSession.SelectedGlobeModel != request.State.Model)
                    throw new InvalidOperationException("The selected Earth image is not ready to save.");
                // Preserve the board's aspect at 4K on its longer edge. This is
                // an upright artwork image, without projector perspective.
                int width = (int)Math.Round(3840 * Math.Min(1, request.Aspect));
                int height = (int)Math.Round(3840 * Math.Min(1, 1 / request.Aspect));
                using var target = new CanvasRenderTarget(_globeRenderer.Device, width, height, 96);
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Black);
                    drawing.Transform = BoardRasterTransform(target);
                    var state = request.State;
                    if (!_globeRenderer.Draw(drawing, (float)state.Zoom, (float)state.RotationDegrees,
                        request.Aspect, (float)state.ViewLatitudeDegrees, state.ElapsedSeconds, state.Model))
                        throw new InvalidOperationException("The selected Earth image could not be rendered.");
                }
                image = new(_globeSaveId, request.Navigation, width, height, target.GetPixelBytes());
                return true;
            }
            catch
            {
                _globeSaving = false;
                _globeSaveError = "Save failed - retry";
                SyncGlobeSaveState();
                throw;
            }
        }
    }

    internal void CompleteGlobeSave(GlobeMemoryImage image, bool success)
    {
        lock (_gate)
        {
            if (image.SaveId != _globeSaveId) return;
            _globeSaving = false;
            _globeSaveError = success ? null : "Save failed - retry";
            _globeSavedUntil = success ? _globeClock().AddSeconds(3) : default;
            SyncGlobeSaveState();
        }
    }

    internal string? GetGlobeSaveStatus(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Globe ||
                _globeSaveStatusNavigation != _boardSession.NavigationRevision) return null;
            return _globeSaving ? "Saving image…" : now < _globeSavedUntil ? "Saved to Pictures folder" : _globeSaveError;
        }
    }
}
