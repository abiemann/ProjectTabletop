using System.Numerics;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // This survives calibration clearing: camera orientation must not define
    // which physical side of the table the board faces.
    private double? _boardFacingDegrees;

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
            if (normalized is { } facing && _boardMediaClip is not null &&
                _detectedBoardCorners is { } corners && _boardCameraMap is { } cameraMap)
            {
                var ordered = BoardOrientation.Orient(corners, facing, _displayAspect);
                var grid = BoardGrid.Create(ordered.Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray());
                CancelBoardReveal();
                ApplyDetectedBoardGrid(grid, cameraMap);
            }
            _boardFacingDegrees = normalized;
        }
    }
}
