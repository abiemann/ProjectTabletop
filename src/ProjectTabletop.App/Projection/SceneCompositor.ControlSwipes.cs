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
    // A tracked double-bar marker paints or stirs; its strokes along the control
    // row are never commands. Swipes stay disengaged while any marker is seen and
    // briefly afterwards, so a dropout cannot let the end of a stroke toggle.
    internal static readonly TimeSpan ControlSwipeMarkerQuietPeriod = TimeSpan.FromSeconds(1);
    private DateTimeOffset _controlSwipeMarkerSeenAt;
    private DateTimeOffset _controlSwipeMarkerCheckedThrough, _controlSwipeMarkerCheckReset;
    private long _controlSwipeMarkerCheckNavigation = -1;
    private PendingControlSwipe? _pendingControlSwipe;
    private sealed record PendingControlSwipe(ControlSwipeContext Context, DateTimeOffset FrameTime,
        DateTimeOffset WaitingSince);
    private long _controlSwipeMarkerWaitCount, _controlSwipeMarkerReleasedCount;
    private double _controlSwipeLastMarkerWaitMilliseconds, _controlSwipeMaximumMarkerWaitMilliseconds;

    public object GetControlSwipeMarkerWaitDiagnostics()
    {
        lock (_gate)
        {
            ExpirePendingControlSwipe(_blackjackClock());
            return new
            {
                pending = _pendingControlSwipe is not null,
                waits = _controlSwipeMarkerWaitCount, released = _controlSwipeMarkerReleasedCount,
                lastWaitMilliseconds = _controlSwipeLastMarkerWaitMilliseconds,
                maximumWaitMilliseconds = _controlSwipeMaximumMarkerWaitMilliseconds
            };
        }
    }

    public bool BoardButtonsVisible { get { lock (_gate) return _boardSession.ButtonsVisible; } }

    /// <summary>The player's control row, independent of visible buttons.</summary>
    public sealed record ControlSwipeContext(long Revision, IReadOnlyList<PixelPoint> SearchPolygon,
        double Top, double Bottom, DateTimeOffset NotBefore);

    public ControlSwipeContext? GetControlSwipeContext(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            ExpirePendingControlSwipe(now);
            if (!MappedBoardInputReady || !_boardSession.ControlSwipesEnabled ||
                HasBoardControlDrawerAnimation() || CrownDeedEntranceActive ||
                _boardSession.Screen == BoardScreen.Football && !_boardSession.FootballControlsVisible ||
                _boardSession.Screen == BoardScreen.CrownDeed && HasCrownDeedDrawerAnimation(_crownDeedClock()) ||
                _boardSession.Screen == BoardScreen.WaterGarden && WaterGardenEntranceProgress(_waterClock()) < 1 ||
                ControlSwipesQuietedByMarker(frameTime) || HoldControlsSettling(frameTime))
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
        bool traversalInProgress, DateTimeOffset frameTime, bool markerCheckRequired = false)
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
            // A deliberate control sweep is user activity before it reaches the
            // opposite edge. Keep idle paint from redrawing its camera baseline.
            if (traversalInProgress && _boardSession.Screen == BoardScreen.Paint) NotePaintUserActivity();
            if (traversalInProgress && !wasTraversing)
            {
                // Only captions in the swipe band need to uncover again. A
                // reach across the footer must not disarm choices above it.
                // Use the same lettering bounds as the hold detector.
                var crossed = _boardSession.Buttons.Where(button => button.IsHold && InSwipeBand(button))
                    .Select(button => button.Id).ToArray();
                _boardSession.ResetHoldCaptionEvidence(crossed, frameTime);
                _holdRevision++;

                bool InSwipeBand(BoardButton button)
                {
                    double center = button.Bounds.Y + button.Bounds.Height / 2;
                    if (_acquisitionReferenceTarget is { } reference)
                    {
                        var caption = BoardButtonTextRegion(reference.Device, button);
                        center = caption.Y + caption.Height / 2;
                    }
                    return center >= context.Top && center <= context.Bottom;
                }
            }
            if (swipe != BoardControlSwipe.Toggle || now < _controlSwipeRearmAt) return false;
            if (markerCheckRequired && !ControlSwipeMarkerCheckReached(frameTime))
            {
                // Independent workers can finish in either order. Keep only this
                // command, not its image, until marker detection covers its time.
                if (_pendingControlSwipe is null)
                {
                    _pendingControlSwipe = new(context, frameTime, now);
                    _controlSwipeMarkerWaitCount++;
                }
                return false;
            }
            return ApplyControlSwipeToggle(now);
        }
    }

    private bool ControlSwipeMarkerCheckReached(DateTimeOffset frameTime) =>
        _controlSwipeMarkerCheckNavigation == _boardSession.NavigationRevision &&
        _controlSwipeMarkerCheckReset == _handVisualResetThrough &&
        _controlSwipeMarkerCheckedThrough >= frameTime;

    private void ExpirePendingControlSwipe(DateTimeOffset now)
    {
        if (_pendingControlSwipe is { } pending && now - pending.FrameTime > TimeSpan.FromMilliseconds(250))
            _pendingControlSwipe = null;
    }

    // Call once after all selected marker profiles have actually been checked.
    // A busy worker, discarded frame or unavailable reference is not a clear result.
    public bool CompleteControlSwipeMarkerCheck(DateTimeOffset frameTime, bool markerSeen)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            ExpirePendingControlSwipe(now);
            if (frameTime > now || now - frameTime > TimeSpan.FromMilliseconds(250) ||
                frameTime <= _handVisualResetThrough) return false;
            if (markerSeen)
            {
                NoteMarkerOnBoard(frameTime);
                return false;
            }
            if (_controlSwipeMarkerCheckNavigation != _boardSession.NavigationRevision ||
                _controlSwipeMarkerCheckReset != _handVisualResetThrough)
                _controlSwipeMarkerCheckedThrough = default;
            _controlSwipeMarkerCheckNavigation = _boardSession.NavigationRevision;
            _controlSwipeMarkerCheckReset = _handVisualResetThrough;
            if (frameTime > _controlSwipeMarkerCheckedThrough) _controlSwipeMarkerCheckedThrough = frameTime;
            if (_pendingControlSwipe is not { } pending || !ControlSwipeMarkerCheckReached(pending.FrameTime)) return false;
            _pendingControlSwipe = null;
            if (!ReferenceEquals(pending.Context, GetControlSwipeContext(pending.FrameTime)) ||
                pending.FrameTime <= _controlSwipeNotBefore || now < _controlSwipeRearmAt) return false;
            if (!ApplyControlSwipeToggle(now)) return false;
            _controlSwipeMarkerReleasedCount++;
            _controlSwipeLastMarkerWaitMilliseconds = (now - pending.WaitingSince).TotalMilliseconds;
            _controlSwipeMaximumMarkerWaitMilliseconds = Math.Max(_controlSwipeMaximumMarkerWaitMilliseconds,
                _controlSwipeLastMarkerWaitMilliseconds);
            return true;
        }
    }

    public void ResetControlSwipeMarkerChecks()
    {
        lock (_gate)
        {
            _pendingControlSwipe = null;
            _controlSwipeMarkerCheckedThrough = default;
            _controlSwipeMarkerCheckNavigation = -1;
        }
    }

    private bool ApplyControlSwipeToggle(DateTimeOffset now)
    {
        if (!_boardSession.SetButtonsVisible(!_boardSession.ButtonsVisible, now)) return false;
        if (_boardSession.Screen == BoardScreen.Paint) NotePaintUserActivity();
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

    /// <summary>Records a fresh camera frame in which a marker detector measured a marker on the board.</summary>
    public void NoteMarkerOnBoard(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            if (frameTime <= _controlSwipeMarkerSeenAt) return;
            _controlSwipeMarkerSeenAt = frameTime;
            // Cancel a sweep already in progress. The motion baseline restarts
            // only after the quiet period, from frames without the marker.
            InvalidateControlSwipeContext(_blackjackClock());
        }
    }

    // Camera frames on either side of the marker's latest frame share one window,
    // because the marker and swipe detectors finish in independent order.
    private bool ControlSwipesQuietedByMarker(DateTimeOffset frameTime) =>
        (_boardSession.Screen is BoardScreen.WaterGarden or BoardScreen.Paint or BoardScreen.Football) &&
        _controlSwipeMarkerSeenAt != default && frameTime - _controlSwipeMarkerSeenAt <= ControlSwipeMarkerQuietPeriod;

    private bool ControlSwipeTraversalActive(DateTimeOffset now) => _controlSwipeTraversal &&
        _controlSwipeTraversalFrame <= now && now - _controlSwipeTraversalFrame <= TimeSpan.FromMilliseconds(250);

    private void InvalidateControlSwipeContext(DateTimeOffset now)
    {
        _pendingControlSwipe = null;
        _controlSwipeContext = null;
        _controlSwipeSceneKey = null;
        _controlSwipeNotBefore = now;
        _controlSwipeTraversal = false;
        _controlSwipeTraversalFrame = default;
    }
}
