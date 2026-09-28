using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionGoldDealRegression
{
    public static void Run()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "blackjack-idle-controls.json")));
        var root = json.RootElement;
        T Read<T>(string name) => JsonSerializer.Deserialize<T>(root.GetProperty(name).GetRawText())!;
        int width = Read<int>("cameraWidth"), height = Read<int>("cameraHeight"), stride = Read<int>("Stride");
        var scene = new HandAcquisitionSceneImage(Read<int>("Width"), Read<int>("Height"),
            Pixels("blackjack-idle-controls-expected.png"), Read<double[]>("CameraToBoard"),
            Read<HandTrackingBounds[]>("BoardSearchRegions"), Read<HandTrackingBounds[]>("BoardReferenceRegions"),
            Read<HandTrackingBounds[]>("BoardTriggerRegions"), Read<bool>("AllowsLocalForegroundContext"));
        byte[] camera = Pixels("blackjack-idle-controls-camera.png");
        var tracker = new HandAcquisitionPresenceTracker();
        bool aboveFloor = false;
        for (int frame = 0; frame < 8; frame++)
        {
            var time = new DateTimeOffset(2026, 9, 28, 19, 25, 34, TimeSpan.Zero).AddMilliseconds(frame * 100);
            var result = tracker.Update(width, height, stride, camera,
                Read<PixelPoint[]>("SearchPolygon"), scene, time, time);
            var deal = result.TextPatterns!.Single(pattern => pattern.ControlRegion == 6);
            aboveFloor |= deal.CaptionReflectanceCoverage >= .07 && deal.CaptionReflectanceTriggerCoverage >= .07;
            if (result.Hints.Count != 0 || !deal.LabelIntact || deal.ShapeCorrupted || deal.CaptionReflectanceChanged)
                throw new InvalidOperationException("Unobstructed native gold DEAL artwork became hand-acquisition evidence: " +
                    JsonSerializer.Serialize(result));
        }
        if (!aboveFloor)
            throw new InvalidOperationException("Native gold DEAL fixture no longer exercises the measured 7% colour-response ambiguity.");
        Console.WriteLine("Native gold DEAL regression passed: intact empty generated controls, actual optical colour response " +
            "above both 7% floors, eight fresh stationary frames and no foreground acquisition.");

        byte[] fingers = Tint(false);
        foreach (bool cold in new[] { true, false })
        {
            var occupied = new HandAcquisitionPresenceTracker();
            if (!cold)
                for (int frame = 0; frame < 3; frame++) Feed(occupied, camera, frame * 100);
            for (int frame = 0; frame < 8; frame++)
            {
                var result = Feed(occupied, fingers, 400 + frame * 100);
                var deal = result.TextPatterns!.Single(pattern => pattern.ControlRegion == 6);
                if (!deal.LabelIntact || deal.ShapeCorrupted || !deal.CaptionReflectanceChanged ||
                    (frame == 0 ? result.Hints.Count != 0 : result.Hints.Count != 1) ||
                    frame > 0 && (result.Hints[0].ControlCoverage is not >= .07 || result.Hints[0].ControlTriggerCoverage is not >= .07))
                    throw new InvalidOperationException("Partial readable fingers over the native gold DEAL did not qualify: " +
                        JsonSerializer.Serialize(new { cold, frame, result }));
            }
            if (Feed(occupied, camera, 1200).Hints.Count != 0)
                throw new InvalidOperationException("Removing native gold DEAL fingers left a foreground hint.");
        }
        var whole = new HandAcquisitionPresenceTracker();
        byte[] panelTint = Tint(true);
        for (int frame = 0; frame < 4; frame++)
            if (Feed(whole, panelTint, frame * 100).Hints.Count != 0)
                throw new InvalidOperationException("A whole gold panel colour shift became finger evidence.");
        var weak = new HandAcquisitionPresenceTracker();
        byte[] weakTint = Tint(false, weakContrast: true);
        for (int frame = 0; frame < 4; frame++)
            if (Feed(weak, weakTint, frame * 100).Hints.Count != 0)
                throw new InvalidOperationException("Weak local colour change borrowed the gold panel's response error to meet coverage.");
        Console.WriteLine("Native gold DEAL obstruction regression passed: partial readable four fingers at startup " +
            "and after idle, both 7% floors, two fresh frames, stationary persistence, removal and whole-panel tint rejection.");

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker target, byte[] pixels, int milliseconds)
        {
            var time = new DateTimeOffset(2026, 9, 28, 19, 25, 34, TimeSpan.Zero).AddMilliseconds(milliseconds);
            return target.Update(width, height, stride, pixels, Read<PixelPoint[]>("SearchPolygon"), scene, time, time);
        }

        byte[] Tint(bool wholePanel, bool weakContrast = false)
        {
            byte[] pixels = (byte[])camera.Clone();
            var h = scene.CameraToBoard;
            var rect = scene.BoardSearchRegions![6];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double divisor = h[6] * x + h[7] * y + h[8];
                double u = (h[0] * x + h[1] * y + h[2]) / divisor;
                double v = (h[3] * x + h[4] * y + h[5]) / divisor;
                if (u < rect.X || u > rect.X + rect.Width || v < rect.Y || v > rect.Y + rect.Height) continue;
                bool selected = wholePanel || new[] { .773, .791, .809, .827 }.Any(center =>
                    Math.Pow((u - center) / .009, 2) + Math.Pow((v - .84) / .055, 2) <= 1);
                if (!selected) continue;
                int offset = y * stride + x * 4;
                // Red is already near clipping on this projected gold plate.
                // The weaker tint cannot borrow that plate's optical mismatch
                // to exceed the independent per-pixel foreground error floor.
                pixels[offset] = (byte)Math.Clamp(pixels[offset] - (weakContrast ? 40 : 70), 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(pixels[offset + 1] - (weakContrast ? 5 : 20), 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(pixels[offset + 2] + 25, 0, 255);
            }
            return pixels;
        }

        byte[] Pixels(string name)
        {
            using var image = Cv2.ImRead(Path.Combine(fixtures, name), ImreadModes.Color);
            using var bgra = new Mat();
            Cv2.CvtColor(image, bgra, ColorConversionCodes.BGR2BGRA);
            byte[] pixels = new byte[bgra.Rows * bgra.Cols * 4];
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
            return pixels;
        }
    }
}
