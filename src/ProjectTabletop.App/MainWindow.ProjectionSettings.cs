using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private ProjectionSetupSettings _projectionSetup = new();
    private bool _projectionSetupInitialized, _loadingProjectionProfile;
    private string? _projectionProfileKey;
    private readonly Dictionary<string, ProjectionSizeProfile> _sessionProjectionProfiles = new();
    private long _lastProjectionModeCheck;
    private string ProjectionSetupPath => Path.Combine(_appDataDirectory, "projection-setup.json");

    private sealed record ProjectionSetupSettings
    {
        public int Version { get; init; } = 2;
        public Dictionary<string, ProjectionSizeProfile> Profiles { get; init; } = new();
    }

    private void InitializeProjectionSettings()
    {
        try
        {
            if (File.Exists(ProjectionSetupPath))
            {
                _projectionSetup = ParseProjectionSettings(File.ReadAllText(ProjectionSetupPath),
                    SelectedDisplay?.Id, SelectedDisplay?.PhysicalMode);
                foreach (var pair in _projectionSetup.Profiles.Where(pair => pair.Key.StartsWith("session:")).ToArray())
                {
                    _sessionProjectionProfiles[pair.Key] = pair.Value;
                    _projectionSetup.Profiles.Remove(pair.Key);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            ProjectionSettingsStatusText.Text = "Could not load output settings: " + error.Message;
            ProjectionSettingsStatusText.Visibility = Visibility.Visible;
        }
        _projectionSetupInitialized = true;
        ProjectionSettingsDisplayChanged();
    }

    private static ProjectionSetupSettings ParseProjectionSettings(string json, string? displayId, DisplayModeInfo? mode)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("Version").GetInt32() == 1)
        {
            // Preserve the user's entered height, never the old model/ratio assumption.
            var result = new ProjectionSetupSettings();
            var old = document.RootElement;
            if (displayId is not null && old.TryGetProperty("DisplayId", out var id) &&
                id.GetString() == displayId &&
                old.TryGetProperty("LensHeightCentimeters", out var height) && height.TryGetDouble(out var value) &&
                ProjectionSizeProfile.ValidLength(value))
                result.Profiles[ProjectionSizeProfile.PersistentKey(mode) ?? ProjectionSizeProfile.SessionKey(displayId, mode)] =
                    new() { LensHeightCentimeters = value };
            return result;
        }
        var saved = JsonSerializer.Deserialize<ProjectionSetupSettings>(json);
        if (saved is not { Version: 2, Profiles: not null } ||
            saved.Profiles.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || !pair.Value.IsValid))
            throw new InvalidDataException("Saved output settings are invalid or unsupported.");
        return saved;
    }

    private object SetProjectionSize(JsonElement parameters)
    {
        if (SelectedDisplay is null) throw new InvalidOperationException("Select an output display first.");
        var profile = EnteredProjectionProfile();
        if (parameters.TryGetProperty("lensHeightCentimeters", out var height))
            profile = profile with { LensHeightCentimeters = height.ValueKind == JsonValueKind.Null ? null : height.GetDouble() };
        if (parameters.TryGetProperty("throwRatio", out var ratio))
            profile = profile with { ThrowRatio = ratio.ValueKind == JsonValueKind.Null ? null : ratio.GetDouble() };
        if (parameters.TryGetProperty("measuredBoardShortSideCentimeters", out var shortSide))
            profile = profile with { MeasuredBoardShortSideCentimeters = shortSide.ValueKind == JsonValueKind.Null ? null : shortSide.GetDouble() };
        if (parameters.TryGetProperty("measuredBoardLongSideCentimeters", out var longSide))
            profile = profile with { MeasuredBoardLongSideCentimeters = longSide.ValueKind == JsonValueKind.Null ? null : longSide.GetDouble() };
        if (!profile.IsValid) throw new ArgumentException("Invalid height, throw ratio, or measured board dimensions. The short side cannot exceed the long side.");
        _loadingProjectionProfile = true;
        ProjectorLensHeightNumberBox.Value = profile.LensHeightCentimeters ?? double.NaN;
        ProjectorThrowRatioNumberBox.Value = profile.ThrowRatio ?? double.NaN;
        MeasuredBoardShortSideNumberBox.Value = profile.MeasuredBoardShortSideCentimeters ?? double.NaN;
        MeasuredBoardLongSideNumberBox.Value = profile.MeasuredBoardLongSideCentimeters ?? double.NaN;
        MeasuredBoardSizeExpander.IsExpanded = profile.MeasuredBoardShortSideCentimeters is not null ||
            profile.MeasuredBoardLongSideCentimeters is not null;
        _loadingProjectionProfile = false;
        ProjectorSize_ValueChanged(ProjectorLensHeightNumberBox, null!);
        return new { profile.LensHeightCentimeters, profile.ThrowRatio,
            profile.MeasuredBoardShortSideCentimeters, profile.MeasuredBoardLongSideCentimeters,
            boardSizeEstimate = CurrentBoardSizeEstimate(), opticalBoardSizeEstimate = CurrentOpticalBoardSizeEstimate(),
            estimateSource = BoardSizeSource };
    }

    private ProjectionSizeProfile EnteredProjectionProfile() => new()
    {
        LensHeightCentimeters = OptionalNumber(ProjectorLensHeightNumberBox.Value),
        ThrowRatio = OptionalNumber(ProjectorThrowRatioNumberBox.Value),
        MeasuredBoardShortSideCentimeters = OptionalNumber(MeasuredBoardShortSideNumberBox.Value),
        MeasuredBoardLongSideCentimeters = OptionalNumber(MeasuredBoardLongSideNumberBox.Value)
    };

    private static double? OptionalNumber(double value) => double.IsNaN(value) ? null : value;

    private void ProjectorSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_projectionSetupInitialized || _loadingProjectionProfile) return;
        var profile = EnteredProjectionProfile();
        if (profile.IsValid && _projectionProfileKey is { } key)
        {
            _sessionProjectionProfiles[key] = profile;
            if (ProjectionSizeProfile.PersistentKey(SelectedDisplay?.PhysicalMode) is { } persistentKey)
            {
                _projectionSetup.Profiles[persistentKey] = profile;
                SaveProjectionSettings();
            }
        }
        UpdateBoardSizeEstimate();
    }

    private void SaveProjectionSettings()
    {
        try
        {
            Directory.CreateDirectory(_appDataDirectory);
            string temporary = ProjectionSetupPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_projectionSetup,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ProjectionSetupPath, overwrite: true);
            ProjectionSettingsStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProjectionSettingsStatusText.Text = "Could not save output settings: " + error.Message;
            ProjectionSettingsStatusText.Visibility = Visibility.Visible;
        }
    }

    private void ProjectionSettingsDisplayChanged()
    {
        if (!_projectionSetupInitialized) return;
        string? persistentKey = ProjectionSizeProfile.PersistentKey(SelectedDisplay?.PhysicalMode);
        _projectionProfileKey = persistentKey ?? (SelectedDisplay is { } selected
            ? ProjectionSizeProfile.SessionKey(selected.Id, selected.PhysicalMode) : null);
        var profile = new ProjectionSizeProfile();
        if (_projectionProfileKey is { } key)
            profile = _sessionProjectionProfiles.GetValueOrDefault(key) ??
                _projectionSetup.Profiles.GetValueOrDefault(key) ?? profile;
        _loadingProjectionProfile = true;
        ProjectorLensHeightNumberBox.Value = profile.LensHeightCentimeters ?? double.NaN;
        ProjectorThrowRatioNumberBox.Value = profile.ThrowRatio ?? double.NaN;
        MeasuredBoardShortSideNumberBox.Value = profile.MeasuredBoardShortSideCentimeters ?? double.NaN;
        MeasuredBoardLongSideNumberBox.Value = profile.MeasuredBoardLongSideCentimeters ?? double.NaN;
        MeasuredBoardSizeExpander.IsExpanded = profile.MeasuredBoardShortSideCentimeters is not null ||
            profile.MeasuredBoardLongSideCentimeters is not null;
        _loadingProjectionProfile = false;
        UpdateBoardSizeEstimate();
    }

    private string BoardSizeSource => EnteredProjectionProfile().MeasuredBoardSize is not null
        ? "User-supplied board dimensions" : "User-supplied lens height and throw ratio; optical estimate";

    private BoardSizeEstimate? CurrentBoardSizeEstimate() => _scene.GetDetectedBoardCorners() is null
        ? null : EnteredProjectionProfile().MeasuredBoardSize ?? CurrentOpticalBoardSizeEstimate();

    private BoardSizeEstimate? CurrentOpticalBoardSizeEstimate()
    {
        if (EnteredProjectionProfile().ImageSize(SelectedDisplay?.PhysicalMode, out _) is not { } image ||
            _scene.GetDetectedBoardCorners() is not { } corners) return null;
        return BoardSizeEstimate.FromImageDimensions(corners, image.Width, image.Height);
    }

    private void UpdateBoardSizeEstimate()
    {
        if (!_projectionSetupInitialized) return;
        // A Windows resolution/rotation change can leave layout bounds unchanged.
        long now = Environment.TickCount64;
        if (now - _lastProjectionModeCheck > 3000 && SelectedDisplay is { } selected)
        {
            _lastProjectionModeCheck = now;
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromDisplayId(selected.DisplayId);
            var currentMode = area is not null ? DisplayModeInfo.ForArea(area) : null;
            if (ProjectionModeChanged(selected.PhysicalMode, currentMode))
            {
                RefreshDisplays();
                return;
            }
        }
        var mode = SelectedDisplay?.PhysicalMode;
        ProjectionPropertiesText.Text = mode is null ? "Output properties unavailable. Refresh displays." :
            $"{mode.FriendlyName ?? "Output display"} · {mode.Width} × {mode.Height} · {mode.RefreshHertz} Hz · " +
            $"{AspectLabel(mode.Width, mode.Height)}";
        var profile = EnteredProjectionProfile();
        var image = profile.ImageSize(mode, out string status);
        var estimate = CurrentBoardSizeEstimate();
        _scene.SetEstimatedBoardSize(estimate, measured: profile.MeasuredBoardSize is not null);
        if (profile.MeasuredBoardSize is { } measured)
        {
            BoardSizeEstimateText.Text = $"Measured board: {measured.ShortSideCentimeters:F1} × {measured.LongSideCentimeters:F1} cm. " +
                (estimate is not null ? "Shown on Hand-Tracking." : "Run board setup to use this reference.");
            if (CurrentOpticalBoardSizeEstimate() is { } optical)
                BoardSizeEstimateText.Text += $" Optical estimate: {optical.ShortSideCentimeters:F1} × {optical.LongSideCentimeters:F1} cm.";
        }
        else if (estimate is { } size)
            BoardSizeEstimateText.Text = $"Estimated board: {size.ShortSideCentimeters:F1} × {size.LongSideCentimeters:F1} cm. " +
                "Shown on Hand-Tracking.";
        else if (image is { } field)
            BoardSizeEstimateText.Text = $"Estimated full image: {field.Width:F1} × {field.Height:F1} cm. " +
                "Run board setup to estimate the board itself.";
        else
            BoardSizeEstimateText.Text = status;
        if (!ProjectionSizeProfile.ValidLength(profile.MeasuredBoardShortSideCentimeters) ||
            !ProjectionSizeProfile.ValidLength(profile.MeasuredBoardLongSideCentimeters) ||
            profile.MeasuredBoardShortSideCentimeters > profile.MeasuredBoardLongSideCentimeters)
            BoardSizeEstimateText.Text += " Measured sides must be 0.1–10,000 cm, with the short side no longer than the long side.";
        else if ((profile.MeasuredBoardShortSideCentimeters is null) != (profile.MeasuredBoardLongSideCentimeters is null))
            BoardSizeEstimateText.Text += " Enter both measured sides to use them as the reference.";
        if (mode is not null && ProjectionSizeProfile.PersistentKey(mode) is null)
            BoardSizeEstimateText.Text += " Display identity unavailable; entries apply only to this session.";
    }

    private static string AspectLabel(int width, int height)
    {
        int a = width, b = height;
        while (b != 0) (a, b) = (b, a % b);
        return a > 0 ? $"{width / a}:{height / a}" : "aspect unavailable";
    }

    private static bool ProjectionModeChanged(DisplayModeInfo? previous, DisplayModeInfo? current) =>
        current is not null && (previous is null || current.Width != previous.Width || current.Height != previous.Height ||
            current.RotationDegrees is not null && previous.RotationDegrees is not null && current.RotationDegrees != previous.RotationDegrees ||
            current.MonitorDevicePath is not null && previous.MonitorDevicePath is not null &&
                !string.Equals(current.MonitorDevicePath, previous.MonitorDevicePath, StringComparison.OrdinalIgnoreCase) ||
            current.PreferredPixelWidth is not null && previous.PreferredPixelWidth is not null && current.PreferredPixelWidth != previous.PreferredPixelWidth ||
            current.PreferredPixelHeight is not null && previous.PreferredPixelHeight is not null && current.PreferredPixelHeight != previous.PreferredPixelHeight);
}
