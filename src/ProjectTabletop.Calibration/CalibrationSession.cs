namespace ProjectTabletop.Calibration;

public enum CalibrationPlane
{
    Board,
    PieceTop
}

/// <summary>
/// Four measured raw-camera-pixel to projector-pixel correspondences for one physical plane.
/// Points must have the same convex perimeter order in both images. A raised piece top needs
/// its own correspondences, measured at the piece's actual top height.
/// </summary>
public sealed class PlaneCalibration
{
    private readonly Point2[] _cameraPoints;
    private readonly Point2[] _projectorPoints;

    public PlaneCalibration(IReadOnlyList<Point2> cameraPoints, IReadOnlyList<Point2> projectorPoints)
    {
        CameraToProjector = Homography.FromFourPoints(cameraPoints, projectorPoints);
        _cameraPoints = cameraPoints.ToArray();
        _projectorPoints = projectorPoints.ToArray();
    }

    public IReadOnlyList<Point2> CameraPoints => Array.AsReadOnly(_cameraPoints);
    public IReadOnlyList<Point2> ProjectorPoints => Array.AsReadOnly(_projectorPoints);
    public Homography CameraToProjector { get; }

    public Point2 MapCameraToProjector(Point2 cameraPixel) => CameraToProjector.Transform(cameraPixel);
    public Point2 MapProjectorToCamera(Point2 projectorPixel) => CameraToProjector.InverseTransform(projectorPixel);
}

/// <summary>
/// A pair of calibrations tied to one camera, projector output and piece-top height. Coordinates
/// are raw camera pixels and projector client-area pixels; callers must account for any display
/// scaling or lens correction before using them. Recalibrate when either device or the board moves.
/// </summary>
public sealed class CalibrationSession
{
    public CalibrationSession(string cameraId, int cameraWidth, int cameraHeight,
        string projectorDisplayId, int projectorWidth, int projectorHeight,
        PlaneCalibration boardPlane, PlaneCalibration pieceTopPlane, double pieceTopHeightMillimeters,
        DateTimeOffset? calibratedAt = null)
    {
        CameraId = ValidateId(cameraId, nameof(cameraId));
        ProjectorDisplayId = ValidateId(projectorDisplayId, nameof(projectorDisplayId));
        ValidateDimensions(cameraWidth, cameraHeight, nameof(cameraWidth));
        ValidateDimensions(projectorWidth, projectorHeight, nameof(projectorWidth));
        if (!double.IsFinite(pieceTopHeightMillimeters) || pieceTopHeightMillimeters <= 0 ||
            pieceTopHeightMillimeters > 1000)
            throw new ArgumentOutOfRangeException(nameof(pieceTopHeightMillimeters),
                "Measure the piece top above the board in millimeters.");
        ArgumentNullException.ThrowIfNull(boardPlane);
        ArgumentNullException.ThrowIfNull(pieceTopPlane);
        ValidatePointsInBounds(boardPlane, cameraWidth, cameraHeight, projectorWidth, projectorHeight,
            nameof(boardPlane));
        ValidatePointsInBounds(pieceTopPlane, cameraWidth, cameraHeight, projectorWidth, projectorHeight,
            nameof(pieceTopPlane));

        CameraWidth = cameraWidth;
        CameraHeight = cameraHeight;
        ProjectorWidth = projectorWidth;
        ProjectorHeight = projectorHeight;
        BoardPlane = boardPlane;
        PieceTopPlane = pieceTopPlane;
        PieceTopHeightMillimeters = pieceTopHeightMillimeters;
        CalibratedAt = calibratedAt ?? DateTimeOffset.UtcNow;
        if (CalibratedAt == default)
            throw new ArgumentOutOfRangeException(nameof(calibratedAt));
    }

    public string CameraId { get; }
    public int CameraWidth { get; }
    public int CameraHeight { get; }
    public string ProjectorDisplayId { get; }
    public int ProjectorWidth { get; }
    public int ProjectorHeight { get; }
    public PlaneCalibration BoardPlane { get; }
    public PlaneCalibration PieceTopPlane { get; }
    public double PieceTopHeightMillimeters { get; }
    public DateTimeOffset CalibratedAt { get; }

    /// <summary>Maps a detected camera point to the selected physical plane in projector pixels.</summary>
    public Point2 MapCameraToProjector(Point2 cameraPixel, CalibrationPlane plane) =>
        SelectPlane(plane).MapCameraToProjector(cameraPixel);

    /// <summary>Maps a projector pixel back into the camera image on the selected plane.</summary>
    public Point2 MapProjectorToCamera(Point2 projectorPixel, CalibrationPlane plane) =>
        SelectPlane(plane).MapProjectorToCamera(projectorPixel);

    /// <summary>Checks the identifiers and negotiated pixel dimensions, but cannot detect a moved mount.</summary>
    public bool MatchesHardware(string cameraId, int cameraWidth, int cameraHeight,
        string projectorDisplayId, int projectorWidth, int projectorHeight) =>
        CameraId == cameraId && CameraWidth == cameraWidth && CameraHeight == cameraHeight &&
        ProjectorDisplayId == projectorDisplayId && ProjectorWidth == projectorWidth &&
        ProjectorHeight == projectorHeight;

    private PlaneCalibration SelectPlane(CalibrationPlane plane) => plane switch
    {
        CalibrationPlane.Board => BoardPlane,
        CalibrationPlane.PieceTop => PieceTopPlane,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    private static string ValidateId(string? id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 4096)
            throw new ArgumentException("A nonempty device or display identifier is required.", parameter);
        return id;
    }

    private static void ValidateDimensions(int width, int height, string parameter)
    {
        if (width < 16 || height < 16 || width > 16384 || height > 16384)
            throw new ArgumentOutOfRangeException(parameter, "Image dimensions must be between 16 and 16384 pixels.");
    }

    private static void ValidatePointsInBounds(PlaneCalibration calibration,
        int cameraWidth, int cameraHeight, int projectorWidth, int projectorHeight, string parameter)
    {
        foreach (var point in calibration.CameraPoints)
            if (point.X < 0 || point.Y < 0 || point.X >= cameraWidth || point.Y >= cameraHeight)
                throw new ArgumentException("A camera calibration point is outside the captured image.", parameter);
        foreach (var point in calibration.ProjectorPoints)
            if (point.X < 0 || point.Y < 0 || point.X >= projectorWidth || point.Y >= projectorHeight)
                throw new ArgumentException("A projector calibration point is outside the output image.", parameter);
    }
}
