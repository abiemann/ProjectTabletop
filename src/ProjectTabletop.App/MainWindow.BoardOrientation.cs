using System.Text.Json;
using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void RememberBoardFacing()
    {
        if (_boardFacingDegrees is null && _scene.GetBoardFacingDegrees() is { } learned)
        {
            _boardFacingDegrees = learned;
            SaveBoardFacing();
        }
        UpdateBoardFacingStatus();
    }

    private void RotateBoard_Click(object sender, RoutedEventArgs e)
    {
        if (_boardFacingDegrees is not { } facing) return;
        ApplyBoardFacing(facing + 90);
    }

    private void ApplyBoardFacing(double degrees)
    {
        if (SelectedDisplay is null) throw new InvalidOperationException("Select an output display first.");
        _scene.SetBoardFacingDegrees(degrees);
        _boardFacingDegrees = _scene.GetBoardFacingDegrees();
        ClearHandTracking();
        SaveBoardFacing();
        UpdateBoardFacingStatus();
        SetStatus("Board facing saved for this output. Webcam rotation will be corrected during board setup.");
    }

    private object SetBoardFacingForControl(JsonElement parameters)
    {
        ApplyBoardFacing(parameters.GetProperty("degrees").GetDouble());
        return new { boardFacingDegrees = _boardFacingDegrees, boardClipReady = _scene.HasBoardMediaClip };
    }

    private void SaveBoardFacing()
    {
        if (!_projectionSetupInitialized || _projectionProfileKey is not { } key) return;
        var entered = EnteredProjectionProfile();
        // An unfinished edit to optional dimensions must not stop facing from
        // saving, or overwrite the last valid optical/measurement settings.
        var profile = (entered.IsValid ? entered : _sessionProjectionProfiles.GetValueOrDefault(key) ??
            _projectionSetup.Profiles.GetValueOrDefault(key) ?? new()) with { BoardFacingDegrees = _boardFacingDegrees };
        _sessionProjectionProfiles[key] = profile;
        if (ProjectTabletop.App.Projection.ProjectionSizeProfile.PersistentKey(SelectedDisplay?.PhysicalMode) is { } persistentKey)
        {
            _projectionSetup.Profiles[persistentKey] = profile;
            SaveProjectionSettings();
        }
        UpdateBoardSizeEstimate();
    }

    private void UpdateBoardFacingStatus()
    {
        RotateBoardButton.IsEnabled = SelectedDisplay is not null && _boardFacingDegrees is not null;
        BoardFacingStatusText.Text = _boardFacingDegrees is null
            ? "Board setup will remember the board's facing direction."
            : "Facing saved. Webcam rotation is corrected during board setup.";
    }
}
