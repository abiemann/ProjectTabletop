using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private bool _projectionPreviewEnabled = true;
    private volatile bool _projectionPreviewDrawingEnabled;
    private volatile bool _boardGamePreviewDrawingEnabled;

    private void ProjectionPreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closing) return;
        SetProjectionPreviewEnabled(((ToggleSwitch)sender).IsOn);
    }

    private void SetProjectionPreviewEnabled(bool enabled)
    {
        if (_projectionPreviewEnabled == enabled) return;
        _projectionPreviewEnabled = enabled;
        ProjectionPreviewToggle.IsOn = enabled;
        BoardGamePreviewToggle.IsOn = enabled;
        ApplyProjectionPreviewState();
    }

    private void ApplyProjectionPreviewState()
    {
        bool enabled = _projectionPreviewEnabled && !_closing;
        // Draw runs on Win2D threads. Publish guards before pausing or hiding
        // the controls so queued draws never need to read UI-thread properties.
        _projectionPreviewDrawingEnabled = enabled && ProjectionPreviewPanel.Visibility == Visibility.Visible;
        _boardGamePreviewDrawingEnabled = enabled && BlackjackPreviewPanel.Visibility == Visibility.Visible;
        ProjectionPreview.Paused = !_projectionPreviewDrawingEnabled;
        BlackjackPreview.Paused = !_boardGamePreviewDrawingEnabled;
        ProjectionPreview.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        BlackjackPreview.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ProjectionPreviewOffText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        BoardGamePreviewOffText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }
}
