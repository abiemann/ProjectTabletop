using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

/// <summary>Exports the original object pixels as a straight-alpha PNG, without a rendering transform.</summary>
internal static class PhotoCopyImageStore
{
    public static string DefaultDirectory
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
                throw new DirectoryNotFoundException("Windows did not provide a Pictures folder.");
            return Path.Combine(pictures, "Project Tabletop");
        }
    }

    /// <summary>
    /// Returns the full final filename only after writing and publishing the complete PNG.
    /// Cancellation before publication leaves no final image. Publication itself is atomic.
    /// </summary>
    public static Task<string> SaveAsync(PhotoHandCutout cutout, string? directory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cutout);
        if (cutout.Width is <= 0 or > 16384 || cutout.Height is <= 0 or > 16384 ||
            cutout.BgraPixels is null || cutout.BgraPixels.LongLength != (long)cutout.Width * cutout.Height * 4)
            throw new ArgumentException("The object image must contain tightly packed BGRA pixels.", nameof(cutout));
        return SaveBgraAsync(cutout.Width, cutout.Height, cutout.Width * 4,
            cutout.BgraPixels, directory, cancellationToken);
    }

    // A stride-aware entry point also supports unresized camera-buffer callers.
    internal static async Task<string> SaveBgraAsync(int width, int height, int stride, byte[] bgra,
        string? directory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.LongLength < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA image dimensions, stride, or buffer length.");
        cancellationToken.ThrowIfCancellationRequested();
        string folder = Path.GetFullPath(directory ?? DefaultDirectory);
        int rowBytes = width * 4;
        var pixels = new byte[checked(rowBytes * height)];
        for (int row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Buffer.BlockCopy(bgra, row * stride, pixels, row * rowBytes, rowBytes);
        }

        // Snapshot the source before yielding. The PNG encoder consumes BGRA8
        // directly, preserving partial alpha and RGB even beneath transparent pixels.
        byte[] png = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = new Mat(height, width, MatType.CV_8UC4);
            Marshal.Copy(pixels, 0, image.Data, pixels.Length);
            if (!Cv2.ImEncode(".png", image, out byte[] encoded))
                throw new IOException("The object image could not be encoded as PNG.");
            return encoded;
        }, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        string name = $"photo-copy-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}Z-{Guid.NewGuid():N}.png";
        string finalPath = Path.Combine(folder, name);
        string temporaryPath = Path.Combine(folder, "." + name + ".tmp");
        bool published = false;
        bool ownsTemporaryFile = false;
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsTemporaryFile = true;
                await file.WriteAsync(png, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: false);
            published = true;
            return finalPath;
        }
        finally
        {
            if (!published && ownsTemporaryFile)
            {
                // Preserve the original save/cancellation exception if the filesystem
                // also refuses cleanup. Never remove or overwrite a published image.
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
