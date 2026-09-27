namespace ProjectTabletop.App.Projection;

internal static class PhotoCopyBitmapPixels
{
    /// <summary>Convert straight BGRA from the extractor to Win2D's bitmap format.</summary>
    internal static byte[] Premultiply(byte[] straight)
    {
        ArgumentNullException.ThrowIfNull(straight);
        if (straight.Length % 4 != 0) throw new ArgumentException("Provide complete BGRA pixels.", nameof(straight));
        var pixels = new byte[straight.Length];
        for (var index = 0; index < straight.Length; index += 4)
        {
            var alpha = straight[index + 3];
            // Rounded integer multiplication preserves opaque colors, makes
            // transparent RGB zero, and keeps every color channel <= alpha.
            pixels[index] = (byte)((straight[index] * alpha + 127) / 255);
            pixels[index + 1] = (byte)((straight[index + 1] * alpha + 127) / 255);
            pixels[index + 2] = (byte)((straight[index + 2] * alpha + 127) / 255);
            pixels[index + 3] = alpha;
        }
        return pixels;
    }
}
