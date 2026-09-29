using System.Text.Json.Serialization;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App.Projection;

/// <param name="CameraToProjector">Row-major 3 × 3 map from camera pixels to normalized projector coordinates.</param>
internal sealed record CameraProjectorAlignment(string CameraDeviceId, int Width, int Height, double[] CameraToProjector)
{
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrEmpty(CameraDeviceId) && Width > 0 && Height > 0 &&
        CameraToProjector is { Length: 9 } matrix && matrix.All(double.IsFinite);

    public (double X, double Y)? Transform(double x, double y)
    {
        var m = CameraToProjector;
        double divisor = m[6] * x + m[7] * y + m[8];
        if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-12) return null;
        double u = (m[0] * x + m[1] * y + m[2]) / divisor, v = (m[3] * x + m[4] * y + m[5]) / divisor;
        return double.IsFinite(u) && double.IsFinite(v) ? (u, v) : null;
    }
}

// Values belong to one output device and mode, never to a projector brand.
internal sealed record ProjectionSizeProfile
{
    public double? LensHeightCentimeters { get; init; }
    public double? ThrowRatio { get; init; }
    public double? MeasuredBoardShortSideCentimeters { get; init; }
    public double? MeasuredBoardLongSideCentimeters { get; init; }
    public bool EnableDisplayAudio { get; init; } = true;
    public double? BoardFacingDegrees { get; init; }
    // The last registration's camera-to-projector map on this output. It only
    // places the next scan's corner flair before that scan's own dots measure.
    public CameraProjectorAlignment? LastAlignment { get; init; }

    public bool IsValid => ValidOptics && ValidMeasuredInputs && (BoardFacingDegrees is null ||
        double.IsFinite(BoardFacingDegrees.Value) && BoardFacingDegrees is >= 0 and < 360) &&
        (LastAlignment is null || LastAlignment.IsValid);

    // A supplied board reference does not depend on an optical estimate. Keep
    // only its two editable inputs in the persisted profile, not derived state.
    [JsonIgnore]
    public BoardSizeEstimate? MeasuredBoardSize => ValidMeasuredInputs &&
        MeasuredBoardShortSideCentimeters is { } shortSide && MeasuredBoardLongSideCentimeters is { } longSide
            ? new(shortSide, longSide) : null;

    private bool ValidOptics => ValidLength(LensHeightCentimeters) && (ThrowRatio is null ||
            double.IsFinite(ThrowRatio.Value) && ThrowRatio is >= .01 and <= 100);

    private bool ValidMeasuredInputs => ValidLength(MeasuredBoardShortSideCentimeters) &&
        ValidLength(MeasuredBoardLongSideCentimeters) &&
        (MeasuredBoardShortSideCentimeters is null || MeasuredBoardLongSideCentimeters is null ||
            MeasuredBoardShortSideCentimeters <= MeasuredBoardLongSideCentimeters);

    public static bool ValidLength(double? value) => value is null ||
        double.IsFinite(value.Value) && value is >= .1 and <= 10000;

    public static string? PersistentKey(DisplayModeInfo? mode) =>
        mode is { Width: > 0, Height: > 0, MonitorDevicePath: { Length: > 0 } path }
            ? $"{path.ToUpperInvariant()}|{mode.Width}x{mode.Height}|{mode.RotationDegrees}|" +
              $"{mode.PreferredPixelWidth}x{mode.PreferredPixelHeight}"
            : null;

    public static string SessionKey(string displayId, DisplayModeInfo? mode) =>
        $"session:{displayId}|{mode?.Width}x{mode?.Height}|{mode?.RotationDegrees}|{mode?.PreferredPixelWidth}x{mode?.PreferredPixelHeight}";

    public (double Width, double Height)? ImageSize(DisplayModeInfo? mode, out string status)
    {
        status = "";
        if (!ValidOptics)
            status = "Use lengths from 0.1 to 10,000 cm and a throw ratio from 0.01 to 100.";
        else if (mode is not { Width: > 0, Height: > 0 })
            status = "Refresh displays to read the output resolution before estimating size.";
        else if (LensHeightCentimeters is not { } lensHeight || ThrowRatio is not { } ratio)
            status = "Optional: enter both height and throw ratio to show estimated dimensions on Hand-Tracking.";
        else
        {
            bool quarterTurn = mode.RotationDegrees is 90 or 270;
            double opticalAspect = quarterTurn ? (double)mode.Height / mode.Width : (double)mode.Width / mode.Height;
            if (mode.PreferredPixelWidth is > 0 && mode.PreferredPixelHeight is > 0 &&
                Math.Abs(opticalAspect / ((double)mode.PreferredPixelWidth.Value / mode.PreferredPixelHeight.Value) - 1) > .01)
                status = "Use the display's preferred aspect ratio for a size estimate; this mode may add borders or crop the image.";
            else
            {
                double width = lensHeight / ratio, height = width / opticalAspect;
                return quarterTurn ? (height, width) : (width, height);
            }
        }
        return null;
    }
}
