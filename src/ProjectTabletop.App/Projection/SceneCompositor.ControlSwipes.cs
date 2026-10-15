using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly record struct ControlSwipeSceneKey(long Navigation, long Controls,
        Homography CameraMap, Homography SurfaceMap, DateTimeOffset InputReset, double Brightness);
    private ControlSwipeSceneKey? _controlSwipeSceneKey;
    private ControlSwipeContext? _controlSwipeContext;
    private long _controlSwipeRevision;
    private DateTimeOffset _controlSwipeNotBefore, _controlSwipeRearmAt;
    private DateTimeOffset _controlSwipeTraversalFrame;
    private DateTimeOffset _controlSwipeHoldNotBefore;
    private bool _controlSwipeTraversal;

    public bool BoardButtonsVisible { get { lock (_gate) return _boardSession.ButtonsVisible; } }

    /// <summary>The player's control row, independent of visible buttons.</summary>
    public sealed record ControlSwipeContext(long Revision, IReadOnlyList<PixelPoint> SearchPolygon,
        double Top, double Bottom, DateTimeOffset NotBefore);

    public ControlSwipeContext? GetControlSwipeContext(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            if (!MappedBoardInputReady || !_boardSession.ControlSwipesEnabled ||
                HasBoardControlDrawerAnimation() || CrownDeedEntranceActive ||
                _boardSession.Screen == BoardScreen.Football && !_boardSession.FootballControlsVisible ||
                _boardSession.Screen == BoardScreen.CrownDeed && HasCrownDeedDrawerAnimation(_crownDeedClock()) ||
                _boardSession.Screen == BoardScreen.WaterGarden && WaterGardenEntranceProgress(_waterClock()) < 1 ||
                HoldControlsSettling(frameTime))
            {
                InvalidateControlSwipeContext(now);
                return null;
            }
            var key = new ControlSwipeSceneKey(_boardSession.NavigationRevision, _boardSession.Revision,
                _boardCameraMap!, _boardSurfaceMap!, _handVisualResetThrough, _holdControlBrightness);
            if (_controlSwipeSceneKey != key || _controlSwipeContext is null)
            {
                InvalidateControlSwipeContext(now);
                _controlSwipeSceneKey = key;
                // A command changes the projected pixels. Preserve the rearm
                // interval even though the image baseline must start over.
                var notBefore = _controlSwipeRearmAt > now ? _controlSwipeRearmAt : now;
                PixelPoint Map(double u, double v)
                {
                    var point = _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(new(u, v)));
                    return new(point.X, point.Y);
                }
                var (top, bottom) = _boardSession.Screen switch
                {
                    BoardScreen.Settings or BoardScreen.HandTracking or BoardScreen.Media => (.04, .20),
                    BoardScreen.CrownDeed => (.60, .83),
                    BoardScreen.Blackjack => (.75, .90),
                    _ => (.82, 1d)
                };
                _controlSwipeContext = new(++_controlSwipeRevision,
                    [Map(0, top), Map(1, top), Map(1, bottom), Map(0, bottom)], top, bottom, notBefore);
            }
            return frameTime <= now && now - frameTime <= TimeSpan.FromMilliseconds(250) &&
                frameTime > _controlSwipeContext.NotBefore && frameTime > _handVisualResetThrough
                ? _controlSwipeContext : null;
        }
    }

    /// <summary>Publishes fresh motion only; this path never acquires or lights a hand.</summary>
    public bool CompleteControlSwipe(ControlSwipeContext context, BoardControlSwipe? swipe,
        bool traversalInProgress, DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            if (!ReferenceEquals(context, GetControlSwipeContext(frameTime)) ||
                frameTime <= _controlSwipeNotBefore || frameTime <= _controlSwipeTraversalFrame) return false;
            bool wasTraversing = ControlSwipeTraversalActive(now);
            _controlSwipeTraversalFrame = frameTime;
            _controlSwipeTraversal = traversalInProgress;
            if (traversalInProgress || wasTraversing) _controlSwipeHoldNotBefore = frameTime;
            if (traversalInProgress && !wasTraversing)
            {
                // Crossing a caption must not complete a hold. A stationary
                // hand left behind must uncover the caption before rearming.
                _boardSession.ResetHoldCaptionEvidence();
                _holdRevision++;
            }
            if (swipe is not { } command || now < _controlSwipeRearmAt ||
                !_boardSession.SetButtonsVisible(command == BoardControlSwipe.Show, now)) return false;
            _controlSwipeRearmAt = now + TimeSpan.FromMilliseconds(600);
            InvalidateControlSwipeContext(now);
            // Reject results captured before a hide/show round trip even when
            // restored controls have identical labels and geometry.
            _holdSceneKey = null;
            _holdExpectedScene = null;
            _holdRevision++;
            _holdNavigationStartedAt = now;
            _renderedBoardState = null;
            return true;
        }
    }

    private bool ControlSwipeTraversalActive(DateTimeOffset now) => _controlSwipeTraversal &&
        _controlSwipeTraversalFrame <= now && now - _controlSwipeTraversalFrame <= TimeSpan.FromMilliseconds(250);

    private void InvalidateControlSwipeContext(DateTimeOffset now)
    {
        _controlSwipeContext = null;
        _controlSwipeSceneKey = null;
        _controlSwipeNotBefore = now;
        _controlSwipeTraversal = false;
        _controlSwipeTraversalFrame = default;
    }
}
