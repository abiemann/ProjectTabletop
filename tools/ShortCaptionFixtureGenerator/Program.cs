using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ShortCaptionFixtureGenerator;

// Bootstrap the isolated Win2D runtime before JIT-compiling any renderer calls.
if (args.Length != 1)
    throw new ArgumentException("Usage: ShortCaptionFixtureGenerator.exe <fixture-output-directory>");
string output = Path.GetFullPath(args[0]);
string directory = AppContext.BaseDirectory;
Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", directory);
Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID", Environment.ProcessId.ToString());
Environment.CurrentDirectory = directory;
if (!Native.SetDllDirectory(directory)) throw new System.ComponentModel.Win32Exception();
Marshal.ThrowExceptionForHR(Native.RoInitialize(1));
Marshal.ThrowExceptionForHR(Native.WindowsAppRuntime_EnsureIsLoaded());
string application = Path.Combine(directory, "ProjectTabletop.App.dll");
var resolver = new AssemblyDependencyResolver(application);
AssemblyLoadContext.Default.Resolving += (_, name) =>
    AssemblyLoadContext.Default.LoadFromAssemblyPath(resolver.ResolveAssemblyToPath(name)
        ?? Path.Combine(directory, name.Name + ".dll"));
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
    resolver.ResolveUnmanagedDllToPath(name) is { } path ? NativeLibrary.Load(path) : 0;
await FixtureRecipe.GenerateAsync(Assembly.LoadFrom(application), output);

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string path);
    [DllImport("combase.dll")]
    internal static extern int RoInitialize(uint type);
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    internal static extern int WindowsAppRuntime_EnsureIsLoaded();
}
