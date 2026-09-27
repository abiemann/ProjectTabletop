using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>Bounded color compensation for a palm-search thumbnail, never camera or pose pixels.</summary>
internal static class PalmProjectionColorCorrection
{
    public static bool TryCreate(Mat rgb, out Mat? corrected, out HandTrackingColorCorrection? evidence)
    {
        corrected = null;
        evidence = null;
        int width = rgb.Width, height = rgb.Height;
        if (rgb.Type() != MatType.CV_8UC3 || !rgb.IsContinuous() || width <= 0 || height <= 0) return false;
        var pixels = new byte[width * height * 3];
        Marshal.Copy(rgb.Data, pixels, 0, pixels.Length);
        double red = 0, green = 0, blue = 0;
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 3)
        {
            int r = pixels[index], g = pixels[index + 1], b = pixels[index + 2];
            // White projector light can look pink to the camera. Estimate its
            // cast from bright, modest-chroma pixels, excluding skin/dark felt.
            if (r + g + b < 180 * 3 || Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 90) continue;
            red += r; green += g; blue += b; count++;
        }
        if (count < Math.Max(64, width * height / 50)) return false;
        double mean = (red + green + blue) / 3;
        double redGain = Math.Clamp(mean / red, .75, 1.35);
        double greenGain = Math.Clamp(mean / green, .75, 1.35);
        double blueGain = Math.Clamp(mean / blue, .75, 1.35);
        if (Math.Max(Math.Abs(redGain - 1), Math.Max(Math.Abs(greenGain - 1), Math.Abs(blueGain - 1))) < .06)
            return false;
        for (int index = 0; index < pixels.Length; index += 3)
        {
            pixels[index] = Scale(pixels[index], redGain);
            pixels[index + 1] = Scale(pixels[index + 1], greenGain);
            pixels[index + 2] = Scale(pixels[index + 2], blueGain);
        }
        corrected = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(pixels, 0, corrected.Data, pixels.Length);
        evidence = new(redGain, greenGain, blueGain, count);
        return true;
    }

    private static byte Scale(byte value, double gain) => (byte)Math.Clamp((int)Math.Round(value * gain), 0, 255);
}
