using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        UnhandledException += (_, args) => AppLog.Write("Unhandled UI exception", args.Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { AppLog.Write("App XAML initialization", ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { AppLog.Write("Main window launch", ex); throw; }
    }
}
