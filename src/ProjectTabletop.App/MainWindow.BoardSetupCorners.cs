using System.Diagnostics;
using System.Numerics;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Orange corners land before the registration dots. The dots' white
    // reference is taken only after they settle, so the static corners and
    // test strips are part of it and cannot be mistaken for a dot.
    private static readonly TimeSpan SetupCornersSettle = TimeSpan.FromMilliseconds(1800);
    private CameraProjectorAlignment? _cameraAlignment;

    private bool TryShowSetupCorners(CameraFrame frame, BoardDetection field, BoardDetection board)
    {
        if (_cameraAlignment is not { IsValid: true } alignment || _camera.ActiveDeviceId != alignment.CameraDeviceId ||
            alignment.Width != frame.Width || alignment.Height != frame.Height ||
            PredictSetupCorners(alignment, field.Corners, board.Corners) is not { } corners) return false;
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _scene.ShowSetupCorners(corners);
        Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.ShowCorners);
        BoardSetupStatusText.Text = "Cardboard corners found. Marking them before alignment. Keep the cardboard still.";
        return true;
    }

    /// <summary>
    /// Approximate projector positions of the cardboard corners before this scan's dots.
    /// The white field's fresh camera outline gives position and scale; the previous
    /// registration only decides which field corner is which projector corner.
    /// </summary>
    internal static Vector2[]? PredictSetupCorners(CameraProjectorAlignment alignment,
        IReadOnlyList<PixelPoint> field, IReadOnlyList<PixelPoint> board)
    {
        if (!alignment.IsValid || field.Count != 4 || board.Count != 4) return null;
        Point2[] canvas = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var order = new int[4];
        for (int index = 0; index < 4; index++)
        {
            if (alignment.Transform(field[index].X, field[index].Y) is not { } previous) return null;
            order[index] = Enumerable.Range(0, 4).MinBy(corner =>
                Math.Pow(previous.X - canvas[corner].X, 2) + Math.Pow(previous.Y - canvas[corner].Y, 2));
        }
        if (order.Distinct().Count() != 4) return null;
        Homography fieldMap;
        try
        {
            fieldMap = Homography.FromFourPoints(field.Select(point => new Point2(point.X, point.Y)).ToArray(),
                order.Select(corner => canvas[corner]).ToArray());
        }
        catch (ArgumentException) { return null; }
        var corners = board.Select(point => fieldMap.Transform(new Point2(point.X, point.Y)))
            .Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray();
        return corners.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.X is > -.05f and < 1.05f && point.Y is > -.05f and < 1.05f) ? corners : null;
    }

    private void ProcessSetupCornersShown(CameraFrame frame, long generation)
    {
        if (_closing || generation != Interlocked.Read(ref _boardSetupGeneration) ||
            (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) != BoardSetupPhase.ShowCorners) return;
        BeginRegistrationSpots(frame);
    }

    private void SaveCameraAlignment(CameraFrame frame, Homography cameraMap)
    {
        if (_camera.ActiveDeviceId is not { } cameraId) return;
        _cameraAlignment = new(cameraId, frame.Width, frame.Height, cameraMap.ToMatrix());
        if (!_projectionSetupInitialized || _projectionProfileKey is not { } key) return;
        var entered = EnteredProjectionProfile();
        var profile = (entered.IsValid ? entered : _sessionProjectionProfiles.GetValueOrDefault(key) ??
            _projectionSetup.Profiles.GetValueOrDefault(key) ?? new()) with { LastAlignment = _cameraAlignment };
        _sessionProjectionProfiles[key] = profile;
        if (ProjectionSizeProfile.PersistentKey(SelectedDisplay?.PhysicalMode) is { } persistentKey)
        {
            _projectionSetup.Profiles[persistentKey] = profile;
            SaveProjectionSettings();
        }
    }
}
