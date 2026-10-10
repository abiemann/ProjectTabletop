using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionExposureSearchRegression
{
    private const string Prefix = "menu-control-reflectance-fit";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 16, 30, 0, TimeSpan.Zero);
    private static readonly int[] Unmatched = [0, 4, 6];

    public static void Run()
    {
        // The generated eight-caption menu, overexposed until its bright glyphs
        // clip, with three captions that never match their generated shape (as
        // when the projector edge cuts a label). Rebuilding every exposure model
        // for those labels on every frame cost about 330 ms and 160 MB per frame,
        // so the menu's hand results arrived too stale to light a spotlight.
        using var metadata = JsonDocument.Parse(File.ReadAllText(Fixture(Prefix + ".json")));
        var root = metadata.RootElement;
        T Read<T>(JsonElement element, string name) => JsonSerializer.Deserialize<T>(element.GetProperty(name).GetRawText())!;
        int width = Read<int>(root, "width"), height = Read<int>(root, "height");
        var expected = root.GetProperty("expected");
        var references = Read<HandTrackingBounds[]>(expected, "BoardReferenceRegions");
        var triggers = Read<HandTrackingBounds[]>(expected, "BoardTriggerRegions");
        var scene = new HandAcquisitionSceneImage(Read<int>(expected, "Width"), Read<int>(expected, "Height"),
            Pixels("expected"), Read<double[]>(expected, "CameraToBoard"),
            Read<HandTrackingBounds[]>(expected, "BoardSearchRegions"), references, triggers,
            AllowsLocalForegroundContext: true, BoardControlReferenceRegions: references);
        var polygon = Read<PixelPoint[]>(root, "SearchPolygon");
        double[] matrix = scene.CameraToBoard.ToArray();
        int CameraX(double u) => (int)((u - matrix[2]) / matrix[0]);
        int CameraY(double v) => (int)((v - matrix[5]) / matrix[4]);

        byte[] source = Pixels("camera");
        byte[] camera = (byte[])source.Clone();
        foreach (int region in Unmatched)
        {
            // Cover the right part of the caption with the plate color above it.
            var trigger = triggers[region];
            int left = CameraX(trigger.X + trigger.Width * .55), right = CameraX(trigger.X + trigger.Width) + 4;
            int top = CameraY(trigger.Y) - 4, bottom = CameraY(trigger.Y + trigger.Height) + 4;
            int plate = ((top - 8) * width + left) * 4;
            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                Array.Copy(source, plate, camera, (y * width + x) * 4, 4);
        }
        for (int i = 0; i < camera.Length; i += 4)
            for (int channel = 0; channel < 3; channel++)
                camera[i + channel] = (byte)Math.Clamp(camera[i + channel] * 1.7 + 10, 0, 255);

        var tracker = new HandAcquisitionPresenceTracker();
        const int warmup = 4, frames = 24;
        long allocated = 0;
        HandAcquisitionPresenceResult? result = null;
        for (int frame = 0; frame < frames; frame++)
        {
            if (frame == warmup) allocated = GC.GetAllocatedBytesForCurrentThread();
            var time = Epoch.AddMilliseconds(frame * 100);
            result = tracker.Update(width, height, width * 4, camera, polygon, scene, time, time);
            Require(result.Hints.Count == 0 && result.TextPatterns is { Count: 8 } patterns &&
                patterns.All(pattern => pattern.LabelIntact != Unmatched.Contains(pattern.ControlRegion)) &&
                patterns.Count(pattern => pattern.CaptionClipped) >= 7,
                $"Frame {frame} changed the overexposed menu's caption verdicts: " + JsonSerializer.Serialize(result));
        }
        double perFrame = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)(frames - warmup) / 1e6;
        Require(perFrame < 60, $"Overexposed unmatched captions allocated {perFrame:F1} MB per frame; " +
            "their exposure models are being rebuilt instead of reused.");
        Console.WriteLine($"Overexposed menu exposure search passed: 7 clipped captions, 3 never matching, verdicts " +
            $"stable over {frames} frames without hints, {perFrame:F1} MB allocated per frame.");

        byte[] Pixels(string role)
        {
            using var image = Cv2.ImRead(Fixture(Prefix + "-" + role + ".png"), ImreadModes.Unchanged);
            Require(image.Type() == MatType.CV_8UC4 && image.Width == width && image.Height == height,
                "The generated menu fixture geometry or alpha format changed.");
            var bytes = new byte[image.Total() * image.ElemSize()];
            Marshal.Copy(image.Data, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
