using System.Text;

namespace ProjectTabletop.App;

/// <summary>One resumable local game, published only after a complete write.</summary>
internal static class MonopolySaveStore
{
    internal const int MaximumSaveBytes = 1024 * 1024;

    internal static async Task<string?> LoadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumSaveBytes)
            throw new InvalidDataException("The saved Crown & Deed game is too large.");
        return await File.ReadAllTextAsync(path, Encoding.UTF8);
    }

    internal static async Task SaveAsync(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumSaveBytes)
            throw new InvalidDataException("The Crown & Deed game is too large to save.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".crown-deed-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16384, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
