using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Projection;

namespace ProjectTabletop.App;

public sealed partial class ProjectionWindow : Window
{
    private readonly SceneCompositor _scene;
    private bool _fullScreen;

    public ProjectionWindow(SceneCompositor scene)
    {
        _scene = scene;
        InitializeComponent();
    }

    public bool IsFullScreen => _fullScreen;

    public void ShowOn(DisplayArea display)
    {
        Activate();
        if (_fullScreen) SetFullScreen(false);
        AppWindow.MoveAndResize(display.OuterBounds);
    }

    public void SetFullScreen(bool enabled)
    {
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        _fullScreen = enabled;
    }

    private void OutputCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        _scene.Draw(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height,
            preview: false, runningSlowly: args.Timing.IsRunningSlowly);
    }
}
