using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly TimeSpan BoardRevealDuration = TimeSpan.FromMilliseconds(1300);
    private readonly Func<DateTimeOffset> _boardRevealClock;
    private DateTimeOffset? _boardRevealStartedAt;
    private Vector2 _boardRevealCenter;
    private bool _boardRevealInputPending;

    public sealed record BoardRevealDiagnostics(bool Active, double Progress, double DurationMilliseconds,
        double[] Center, double FlairMilliseconds = 0);

    public bool BoardRevealActive
    {
        get { lock (_gate) return IsBoardRevealActive; }
    }

    private double BoardRevealElapsedMilliseconds => _boardRevealStartedAt is { } started
        ? (_boardRevealClock() - started).TotalMilliseconds : double.PositiveInfinity;

    // The corner flair plays first; the reveal proper then runs for its own duration.
    private double BoardRevealProgress => _boardRevealStartedAt is not null
        ? Math.Clamp((BoardRevealElapsedMilliseconds - CurrentFlairDuration.TotalMilliseconds) /
            BoardRevealDuration.TotalMilliseconds, 0, 1)
        : 1;

    private bool IsBoardRevealActive => _boardRevealStartedAt is not null && BoardRevealProgress < 1;

    public BoardRevealDiagnostics GetBoardRevealDiagnostics()
    {
        lock (_gate) return new(IsBoardRevealActive, BoardRevealProgress,
            BoardRevealDuration.TotalMilliseconds, [_boardRevealCenter.X, _boardRevealCenter.Y],
            CurrentFlairDuration.TotalMilliseconds);
    }

    /// <summary>Commit a validated final center measurement and reveal the selected scene atomically.</summary>
    public float CompleteBoardSetup(IReadOnlyList<Vector2> projectorCorners, Homography cameraMap)
    {
        lock (_gate)
        {
            if (!_boardSetup || _boardCalibrationSpot != BoardCalibrationSpotCount - 1)
                throw new InvalidOperationException("Complete the center registration spot before revealing the board.");
            var center = BoardCalibrationSpotPosition(BoardCalibrationSpotCount - 1);
            var inset = SetDetectedBoardGrid(projectorCorners, cameraMap);
            _revealFlairAnimated = _setupFlairCorners is null;
            SetBoardSetup(false);
            _boardRevealCenter = center;
            _boardRevealStartedAt = _boardRevealClock();
            _boardRevealInputPending = true;
            if (_boardSession.Screen == BoardScreen.CrownDeed)
                StartCrownDeedEntrance(_crownDeedClock() + CurrentFlairDuration + BoardRevealDuration);
            return inset;
        }
    }

    private void CancelBoardReveal()
    {
        if (_boardRevealStartedAt is null) return;
        _boardRevealStartedAt = null;
        _boardRevealInputPending = false;
        ClearHandTips();
    }

    // Do not arm buttons at their final hitboxes while their images are moving.
    // The first post-reveal observation establishes a fresh input barrier, so a
    // gesture begun during the animation cannot select when it finishes.
    private bool BlockBoardRevealInput()
    {
        if (IsBoardRevealActive) return true;
        if (_boardRevealInputPending)
        {
            _boardRevealInputPending = false;
            ClearHandTipsCore(resetInput: true, cancelCrownDeedEntrance: false);
        }
        return false;
    }

    private bool TryDrawBoardReveal(CanvasDrawingSession ds, Rect output, bool preview)
    {
        if (!IsBoardRevealActive || _boardMediaClip is not { } clip) return false;
        double milliseconds = BoardRevealProgress * BoardRevealDuration.TotalMilliseconds;
        static float Ease(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return (float)(t * t * (3 - 2 * t));
        }
        var center = new Vector2((float)(output.X + _boardRevealCenter.X * output.Width),
            (float)(output.Y + _boardRevealCenter.Y * output.Height));
        float startRadius = (float)Math.Min(output.Width, output.Height) * .035f;
        double farX = Math.Max(center.X - output.X, output.Right - center.X);
        double farY = Math.Max(center.Y - output.Y, output.Bottom - center.Y);
        float endRadius = (float)Math.Sqrt(farX * farX + farY * farY) + 2;
        float radius = startRadius + (endRadius - startRadius) * Ease(milliseconds / 850);

        // Continue the exact final calibration image, then cover its white
        // field with black. Keep preview letterboxing outside this field black.
        using var outputLayer = ds.CreateLayer(1, output);
        ds.FillRectangle(output, Colors.White);
        ds.FillCircle(center, radius, Colors.Black);
        // The corner arrows and test strips fade as the reveal begins.
        DrawBoardFlair(ds, output, BoardRevealElapsedMilliseconds, (float)Math.Clamp(1 - milliseconds / 450, 0, 1));

        float scale = Ease((milliseconds - 100) / 1200);
        if (scale <= 0) return true;
        using var iris = CanvasGeometry.CreateCircle(ds.Device, center, radius);
        using var irisLayer = ds.CreateLayer(1, iris);
        // The expanding UI cannot escape the final physical board boundary.
        var corners = clip.Corners.Select(point => new Vector2(
            (float)(output.X + point.X * output.Width),
            (float)(output.Y + point.Y * output.Height))).ToArray();
        using var boardGeometry = CanvasGeometry.CreatePolygon(ds.Device, corners);
        using var boardLayer = ds.CreateLayer(1, boardGeometry);
        var previous = ds.Transform;
        try
        {
            // Uniform scale in output space preserves the calibrated image's
            // proportions. Both preview and projector use the same clock.
            ds.Transform = Matrix3x2.CreateScale(scale, center) * previous;
            DrawBoardScene(ds, output, preview);
        }
        finally { ds.Transform = previous; }
        return true;
    }
}
