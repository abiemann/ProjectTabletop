using System.Text.Json;

namespace ProjectTabletop.Calibration;

/// <summary>Reads and atomically replaces a versioned local JSON calibration file.</summary>
public static class CalibrationSessionStore
{
    private const int CurrentVersion = 1;
    private const long MaximumFileBytes = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task SaveAsync(string path, CalibrationSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(session);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var document = new SessionDocument
        {
            Version = CurrentVersion,
            CameraId = session.CameraId,
            CameraWidth = session.CameraWidth,
            CameraHeight = session.CameraHeight,
            ProjectorDisplayId = session.ProjectorDisplayId,
            ProjectorWidth = session.ProjectorWidth,
            ProjectorHeight = session.ProjectorHeight,
            PieceTopHeightMillimeters = session.PieceTopHeightMillimeters,
            CalibratedAt = session.CalibratedAt,
            Board = PlaneDocument.From(session.BoardPlane),
            PieceTop = PlaneDocument.From(session.PieceTopPlane)
        };
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<CalibrationSession> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("Calibration file exceeds the supported size.");
        SessionDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<SessionDocument>(stream, JsonOptions,
                cancellationToken) ?? throw new InvalidDataException("Calibration file is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Calibration file is not valid JSON.", error);
        }
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported calibration version {document.Version}.");
        if (document.Board is null || document.PieceTop is null)
            throw new InvalidDataException("Both board and piece-top measurements are required.");
        try
        {
            return new CalibrationSession(document.CameraId!, document.CameraWidth, document.CameraHeight,
                document.ProjectorDisplayId!, document.ProjectorWidth, document.ProjectorHeight,
                document.Board.ToCalibration(), document.PieceTop.ToCalibration(),
                document.PieceTopHeightMillimeters, document.CalibratedAt);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Calibration measurements are invalid or incomplete.", error);
        }
    }

    private sealed class SessionDocument
    {
        public int Version { get; set; }
        public string? CameraId { get; set; }
        public int CameraWidth { get; set; }
        public int CameraHeight { get; set; }
        public string? ProjectorDisplayId { get; set; }
        public int ProjectorWidth { get; set; }
        public int ProjectorHeight { get; set; }
        public double PieceTopHeightMillimeters { get; set; }
        public DateTimeOffset CalibratedAt { get; set; }
        public PlaneDocument? Board { get; set; }
        public PlaneDocument? PieceTop { get; set; }
    }

    private sealed class PlaneDocument
    {
        public Point2[]? CameraPoints { get; set; }
        public Point2[]? ProjectorPoints { get; set; }

        public static PlaneDocument From(PlaneCalibration calibration) => new()
        {
            CameraPoints = calibration.CameraPoints.ToArray(),
            ProjectorPoints = calibration.ProjectorPoints.ToArray()
        };

        public PlaneCalibration ToCalibration() => new(CameraPoints!, ProjectorPoints!);
    }
}
