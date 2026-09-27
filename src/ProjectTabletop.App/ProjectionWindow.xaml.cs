using System.Globalization;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Projection;
using Windows.Graphics;
using Windows.System;

namespace ProjectTabletop.App;

public sealed partial class ProjectionWindow : Window
{
    private readonly SceneCompositor _scene;
    private Microsoft.UI.DisplayId? _targetDisplayId;
    private WindowId? _controlWindowId;

    public ProjectionWindow(SceneCompositor scene)
    {
        _scene = scene;
        InitializeComponent();
        Content.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            Close();
        };
    }

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public string? ActualDisplayId =>
        DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None)?.DisplayId.Value
            .ToString(CultureInfo.InvariantCulture);

    public void ShowOn(DisplayArea display, WindowId controlWindowId)
    {
        AppWindow.Hide();
        _targetDisplayId = display.DisplayId;
        _controlWindowId = controlWindowId;
        SetFullScreen(true);
    }

    public void SetFullScreen(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                // Restoring the windowed presenter can restore an earlier position.
                // Keep it hidden until the placement has been checked as well.
                AppWindow.Hide();
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                var windowedTarget = ResolveTargetDisplay();
                PositionOn(windowedTarget);
                VerifyPlacement();
                AppWindow.Show(false);
                VerifyPlacement();
                return;
            }

            AppWindow.Hide();
            var target = ResolveTargetDisplay();
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            PositionOn(target);
            VerifyPlacement();
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            VerifyPlacement();
            // Showing without activation preserves keyboard focus on the controls.
            // The XAML content is already attached by InitializeComponent.
            AppWindow.Show(false);
            VerifyPlacement();
        }
        catch
        {
            AppWindow.Hide();
            throw;
        }
    }

    public void VerifyTargetDisplay() => VerifyPlacement();

    private DisplayArea ResolveTargetDisplay()
    {
        if (_targetDisplayId is not { } targetId || _controlWindowId is not { } controlId)
            throw new InvalidOperationException("Choose a projection display before opening output.");
        var target = DisplayArea.GetFromDisplayId(targetId) ??
            throw new InvalidOperationException("The selected projection display is no longer connected. Refresh displays.");
        var bounds = target.OuterBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The selected projection display has no usable desktop bounds. Refresh displays and check Windows display settings.");
        var controls = DisplayArea.GetFromWindowId(controlId, DisplayAreaFallback.None) ??
            throw new InvalidOperationException("The control window's display could not be verified. Move the controls onto the laptop display.");
        if (controls.DisplayId.Value == target.DisplayId.Value)
            throw new InvalidOperationException("Choose a projector display separate from the control window. Use Extend in Windows display settings.");
        return target;
    }

    private void PositionOn(DisplayArea target)
    {
        var bounds = target.OuterBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The selected projection display no longer has usable desktop bounds.");
        AppWindow.MoveAndResize(new RectInt32(0, 0, bounds.Width, bounds.Height), target);
    }

    private void VerifyPlacement()
    {
        var target = ResolveTargetDisplay();
        var actual = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None);
        if (actual is null || actual.DisplayId.Value != target.DisplayId.Value)
            throw new InvalidOperationException("Windows could not place the output on the selected projector display. Output was hidden to keep the controls visible.");
    }

    private void OutputCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        _scene.Draw(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height,
            preview: false, runningSlowly: args.Timing.IsRunningSlowly);
    }
}
