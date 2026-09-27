using System.Runtime.InteropServices;

namespace ProjectTabletop.App.Projection;

/// <summary>
/// Resolves a GDI display source to its currently connected physical target.
/// All calls are read-only. Missing driver metadata must not prevent output.
/// </summary>
internal sealed record NativeDisplayMetadata(string? FriendlyName, string? MonitorDevicePath,
    int? PreferredPixelWidth, int? PreferredPixelHeight)
{
    private const uint OnlyActivePaths = 0x2;
    private const int InsufficientBuffer = 122;

    public static NativeDisplayMetadata? ForSource(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || !OperatingSystem.IsWindows()) return null;
        // A display can connect/disconnect between size and query calls. Retry
        // that documented race, but do not block startup on continual changes.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0 ||
                pathCount is 0 or > 128 || modeCount is 0 or > 1024) return null;
            var paths = new DisplayConfigPath[pathCount];
            var modes = new DisplayConfigMode[modeCount];
            int status = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == InsufficientBuffer) continue;
            if (status != 0) return null;

            DisplayConfigTarget? selected = null;
            for (int index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var source = new SourceDeviceName
                {
                    Header = Header<SourceDeviceName>(1, path.Source.AdapterId, path.Source.Id)
                };
                if (GetSourceDeviceName(ref source) != 0 ||
                    !string.Equals(source.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                // One GDI source can feed multiple cloned monitors. Its bounds
                // cannot distinguish them, so never choose whichever came first.
                if (selected is not null) return null;
                selected = path.Target;
            }
            if (selected is not { } target) return null;

            var name = new TargetDeviceName
            {
                Header = Header<TargetDeviceName>(2, target.AdapterId, target.Id)
            };
            bool hasName = GetTargetDeviceName(ref name) == 0;
            var preferred = new TargetPreferredMode
            {
                Header = Header<TargetPreferredMode>(3, target.AdapterId, target.Id)
            };
            bool hasPreferred = GetTargetPreferredMode(ref preferred) == 0 &&
                preferred.Width is > 0 and <= int.MaxValue && preferred.Height is > 0 and <= int.MaxValue;
            return new(hasName ? NonEmpty(name.FriendlyName) : null,
                hasName ? NonEmpty(name.MonitorDevicePath) : null,
                hasPreferred ? (int)preferred.Width : null, hasPreferred ? (int)preferred.Height : null);
        }
        return null;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DeviceInfoHeader Header<T>(uint type, Luid adapterId, uint id) where T : struct =>
        new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapterId, Id = id };

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Region
    {
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSource
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTarget
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPath
    {
        public DisplayConfigSource Source;
        public DisplayConfigTarget Target;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO has a 16-byte header and a 48-byte mode union.
    // The union is unused here; the API still needs a correctly sized buffer.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigMode
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public ulong PixelRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufacturerId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HorizontalSync;
        public Rational VerticalSync;
        public Region ActiveSize;
        public Region TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetPreferredMode
    {
        public DeviceInfoHeader Header;
        public uint Width;
        public uint Height;
        public VideoSignalInfo Signal;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayConfigPath[] paths, ref uint modeCount, [Out] DisplayConfigMode[] modes, IntPtr topologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int GetSourceDeviceName(ref SourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int GetTargetDeviceName(ref TargetDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int GetTargetPreferredMode(ref TargetPreferredMode request);
}
