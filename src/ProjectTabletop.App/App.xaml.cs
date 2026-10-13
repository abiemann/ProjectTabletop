using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public partial class App : Application
{
    private MainWindow? _window;
    // Keep this rooted until process exit. MainWindow.Closed performs asynchronous
    // camera cleanup, so releasing the lease in that event would be too early.
    private ExclusiveProcessLease? _instanceLease;

    public App()
    {
        UnhandledException += (_, args) => AppLog.Write("Unhandled UI exception", args.Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { AppLog.Write("App XAML initialization", ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }
        try
        {
            _instanceLease ??= ExclusiveProcessLease.TryAcquire(AppInstanceGuard.LeaseName);
            bool existingProcess = AppInstanceGuard.TryActivateExisting();
            if (_instanceLease is null || existingProcess)
            {
                AppInstanceGuard.ShowStartupMessage("Project Tabletop is already running. " +
                    "Use the existing window, or close it and wait for it to exit before starting another copy. " +
                    "This copy will close without opening a webcam.");
                Exit();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Exclusive app startup guard", ex);
            AppInstanceGuard.ShowStartupMessage("Project Tabletop could not verify that it is the only running copy. " +
                "To protect the webcam, this copy will close without opening it.\n\n" + ex.Message);
            Exit();
            return;
        }
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { AppLog.Write("Main window launch", ex); throw; }
    }
}
