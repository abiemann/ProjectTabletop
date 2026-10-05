using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using MenuPreviewGenerator;

// Native bootstrap intentionally runs before any method that references Win2D
// is JIT compiled. This tool creates neither a WinUI window nor a camera/pipe.
if (args.Length != 3)
    throw new ArgumentException("Usage: MenuPreviewGenerator.exe <repository-root> <asset-output-directory> <comparison-output-directory>");
string directory = AppContext.BaseDirectory;
Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", directory);
Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID", Environment.ProcessId.ToString());
Environment.SetEnvironmentVariable("PROJECT_TABLETOP_DATA_DIR", Path.Combine(args[2], "isolated-data"));
Environment.CurrentDirectory = directory;
if (!Native.SetDllDirectory(directory)) throw new System.ComponentModel.Win32Exception();
Marshal.ThrowExceptionForHR(Native.RoInitialize(1));
Marshal.ThrowExceptionForHR(Native.WindowsAppRuntime_EnsureIsLoaded());
string application = Path.Combine(directory, "ProjectTabletop.App.dll");
var resolver = new AssemblyDependencyResolver(application);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string? resolved = resolver.ResolveAssemblyToPath(name);
    string adjacent = Path.Combine(directory, name.Name + ".dll");
    return AssemblyLoadContext.Default.LoadFromAssemblyPath(resolved ?? adjacent);
};
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
{
    string? resolved = resolver.ResolveUnmanagedDllToPath(name);
    return resolved is null ? 0 : NativeLibrary.Load(resolved);
};
var app = Assembly.LoadFrom(application);
await PreviewRecipes.GenerateAsync(app, Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string path);
    [DllImport("combase.dll")]
    internal static extern int RoInitialize(uint type);
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    internal static extern int WindowsAppRuntime_EnsureIsLoaded();
}
