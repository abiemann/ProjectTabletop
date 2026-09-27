namespace ProjectTabletop.App;

internal static class AppLog
{
    internal static void Write(string context, Exception error)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("PROJECT_TABLETOP_DATA_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProjectTabletop");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "app-errors.log"),
                $"{DateTimeOffset.UtcNow:O} {context}: {error}\n");
        }
        catch { /* A logging failure must not hide the original app error. */ }
    }
}
