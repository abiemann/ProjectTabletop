using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionReflectanceFitRegression
{
    private const string Prefix = "menu-control-reflectance-fit";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 5, 20, 30, 0, TimeSpan.Zero);

    public static void Run()
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Fixture(Prefix + ".json")));
        var root = metadata.RootElement;
        T Read<T>(JsonElement element, string name) => JsonSerializer.Deserialize<T>(element.GetProperty(name).GetRawText())!;
        int width = Read<int>(root, "width"), height = Read<int>(root, "height");
        var expected = root.GetProperty("expected");
        var references = Read<HandTrackingBounds[]>(expected, "BoardReferenceRegions");
        var scene = new HandAcquisitionSceneImage(Read<int>(expected, "Width"), Read<int>(expected, "Height"),
            Pixels("expected"), Read<double[]>(expected, "CameraToBoard"),
            Read<HandTrackingBounds[]>(expected, "BoardSearchRegions"), references,
            Read<HandTrackingBounds[]>(expected, "BoardTriggerRegions"), AllowsLocalForegroundContext: true,
            BoardControlReferenceRegions: references);
        var polygon = Read<PixelPoint[]>(root, "SearchPolygon");
        byte[] empty = Pixels("camera");
        var changed = (byte[])empty.Clone();
        var edge = Read<HandTrackingBounds>(root, "edge");
        double[] matrix = scene.CameraToBoard.ToArray();
        double CameraX(double u) => (u - matrix[2]) / matrix[0];
        double CameraY(double v) => (v - matrix[5]) / matrix[4];
        for (int y = (int)CameraY(edge.Y); y < (int)CameraY(edge.Y) + (int)(CameraY(edge.Y + edge.Height) - CameraY(edge.Y)); y++)
        for (int x = (int)CameraX(edge.X); x < (int)CameraX(edge.X) + (int)(CameraX(edge.X + edge.Width) - CameraX(edge.X)); x++)
        {
            int pixel = (y * width + x) * 4;
            changed[pixel] = 75; changed[pixel + 1] = 95; changed[pixel + 2] = 185;
        }
        var arrow = scene.BoardSearchRegions![7];
        for (int y = (int)Math.Floor(CameraY(arrow.Y)); y <= CameraY(arrow.Y + arrow.Height); y++)
        for (int x = (int)Math.Floor(CameraX(arrow.X)); x <= CameraX(arrow.X + arrow.Width); x++)
        {
            int pixel = (y * width + x) * 4;
            Require(changed.AsSpan(pixel, 4).SequenceEqual(empty.AsSpan(pixel, 4)),
                "The offside fixture changed the actual arrow pixels.");
        }
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm)
                for (int frame = 0; frame < 3; frame++) Quiet(Feed(empty, frame * 100), "Unchanged generated menu");
            for (int frame = 0; frame < 8; frame++) Quiet(Feed(changed, 500 + frame * 100), "Unrelated Blackjack thumbnail edge");
            Quiet(Feed(empty, 1400), "Removed unrelated thumbnail edge");
            HandAcquisitionPresenceResult Feed(byte[] pixels, int time) => tracker.Update(width, height, width * 4,
                pixels, polygon, scene, Epoch.AddMilliseconds(time), Epoch.AddMilliseconds(time));
        }
        Console.WriteLine("Native generated menu reflectance passed: untouched arrow pixels, cold/warm independent card-edge " +
            "change, eight stationary frames, all captions intact and no false hand/search hint, then removal.");

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
    private static void Quiet(HandAcquisitionPresenceResult result, string description) =>
        Require(result.Hints.Count == 0 && result.TextPatterns is { Count: 8 } &&
            result.TextPatterns.All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted &&
                pattern.ConfirmationFrames == 0 && !pattern.CaptionReflectanceChanged),
            description + " manufactured caption reflectance: " + JsonSerializer.Serialize(result));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
