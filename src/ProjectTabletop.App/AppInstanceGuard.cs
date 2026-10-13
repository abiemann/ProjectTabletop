using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProjectTabletop.App;

internal static class AppInstanceGuard
{
    internal const string LeaseName = @"Global\ProjectTabletop.App.Instance.v1";

    // Older builds do not acquire the named lease. Refuse to start alongside one;
    // process enumeration is an additional compatibility check, not the atomic guard.
    internal static bool TryActivateExisting()
    {
        var processes = Process.GetProcessesByName("ProjectTabletop.App");
        try
        {
            foreach (var process in processes)
            {
                if (process.Id == Environment.ProcessId || process.HasExited) continue;
                try
                {
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero)
                    {
                        ShowWindow(window, 9); // SW_RESTORE
                        SetForegroundWindow(window);
                    }
                }
                catch { /* The existing process still blocks startup if its window is inaccessible. */ }
                return true;
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal static void ShowStartupMessage(string message) =>
        MessageBoxW(IntPtr.Zero, message, "Project Tabletop", 0x00000040 | 0x00010000);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
