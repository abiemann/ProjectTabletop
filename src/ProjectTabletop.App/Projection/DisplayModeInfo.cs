using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace ProjectTabletop.App.Projection;

/// <summary>
/// Reads the Windows display mode in physical pixels. DisplayArea bounds are retained
/// separately because AppWindow placement and Win2D canvas sizes use layout units.
/// </summary>
internal sealed record DisplayModeInfo(string DeviceName, int Width, int Height, int RefreshHertz)
{
    private const uint MonitorDefaultToNull = 0;
    private const int CurrentSettings = -1;

    public static DisplayModeInfo? ForArea(DisplayArea area)
    {
        var bounds = area.OuterBounds;
        var midpoint = new NativePoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var monitor = MonitorFromPoint(midpoint, MonitorDefaultToNull);
        if (monitor == IntPtr.Zero) return null;

        var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.DeviceName)) return null;

        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(info.DeviceName, CurrentSettings, ref mode)) return null;
        return mode.Width > 0 && mode.Height > 0
            ? new DisplayModeInfo(info.DeviceName, (int)mode.Width, (int)mode.Height, (int)mode.RefreshHertz)
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    // DEVMODEW is 220 bytes. These offsets are stable parts of its public Win32 layout.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DevMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
        [FieldOffset(184)] public uint RefreshHertz;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);
}
