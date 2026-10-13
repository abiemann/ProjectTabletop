using System.Numerics;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // This survives calibration clearing: camera orientation must not define
    // which physical side of the table the board faces.
    private double? _boardFacingDegrees;
    private double _appliedBoardFacingOffsetDegrees;
    private double BoardFacingOffsetDegrees => _boardSession.Screen == BoardScreen.StrykersRun ? 90 : 0;

    public double? GetBoardFacingDegrees()
    {
        lock (_gate) return _boardFacingDegrees;
    }

    public void SetBoardFacingDegrees(double? degrees)
    {
        if (degrees is { } value && !double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(degrees));
        double? normalized = degrees is { } angle ? (angle % 360 + 360) % 360 : null;
        lock (_gate)
        {
            if (_boardFacingDegrees == normalized) return;
            ApplyBoardFacing(normalized ?? 0);
            _boardFacingDegrees = normalized;
        }
    }

    private void SyncBoardOrientation()
    {
        if (_appliedBoardFacingOffsetDegrees != BoardFacingOffsetDegrees)
            ApplyBoardFacing(_boardFacingDegrees ?? 0);
    }

    private void ApplyBoardFacing(double baseFacing)
    {
        if (_boardMediaClip is null || _detectedBoardCorners is not { } corners ||
            _boardCameraMap is not { } cameraMap) return;
        // Stryker's Run faces the first player's short edge without changing
        // the saved facing shared by the menu and every other board.
        var ordered = BoardOrientation.Orient(corners, baseFacing + BoardFacingOffsetDegrees, _displayAspect);
        var grid = BoardGrid.Create(ordered.Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray());
        CancelBoardReveal();
        ApplyDetectedBoardGrid(grid, cameraMap);
    }
}
