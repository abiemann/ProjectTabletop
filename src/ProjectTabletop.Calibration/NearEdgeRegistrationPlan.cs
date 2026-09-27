namespace ProjectTabletop.Calibration;

/// <summary>A projected dark spot and its predicted camera position on the board.</summary>
public readonly record struct NearEdgeRegistrationTarget(Point2 CameraEstimate,
                                                          Point2 ProjectorPosition);

/// <summary>
/// Places the second registration pass near the physical board corners. The first
/// homography determines projector targets; the detected board quadrilateral keeps
/// each disk safely inside the cardboard while improving the final edge fit.
/// </summary>
public static class NearEdgeRegistrationPlan
{
    public const double BoardInset = 0.15;
    public const double ProjectorSafetyMargin = 0.05;

    public static NearEdgeRegistrationTarget[] Create(IReadOnlyList<Point2> boardCameraCorners,
                                                       Homography provisionalCameraToProjector)
    {
        ArgumentNullException.ThrowIfNull(provisionalCameraToProjector);
        var boardToCamera = Homography.FromFourPoints(
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], boardCameraCorners);
        Point2[] boardPositions = [
            new(BoardInset, BoardInset),
            new(1 - BoardInset, BoardInset),
            new(1 - BoardInset, 1 - BoardInset),
            new(BoardInset, 1 - BoardInset)
        ];
        var targets = boardPositions.Select(boardPosition =>
        {
            var cameraEstimate = boardToCamera.Transform(boardPosition);
            var projectorPosition = provisionalCameraToProjector.Transform(cameraEstimate);
            if (projectorPosition.X < ProjectorSafetyMargin ||
                projectorPosition.X > 1 - ProjectorSafetyMargin ||
                projectorPosition.Y < ProjectorSafetyMargin ||
                projectorPosition.Y > 1 - ProjectorSafetyMargin)
                throw new InvalidOperationException(
                    "A near-edge registration spot would leave the safe projector area.");
            return new NearEdgeRegistrationTarget(cameraEstimate, projectorPosition);
        }).ToArray();

        // A new homography must be possible from these targets. This also catches
        // a provisional fit that folds the board or collapses one of its sides.
        _ = Homography.FromFourPoints(targets.Select(target => target.CameraEstimate).ToArray(),
                                     targets.Select(target => target.ProjectorPosition).ToArray());
        return targets;
    }
}
