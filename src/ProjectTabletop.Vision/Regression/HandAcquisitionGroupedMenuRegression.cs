using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionGroupedMenuRegression
{
    public static void Run()
    {
        // Reuse generated Win2D menu pixels; the obstruction is four rounded
        // strips with a common palm. No private camera image or model pose is
        // needed to prove that grouped fingers damage the caption directly.
        const string prefix = "menu-control-reflectance-fit";
        using var metadata = JsonDocument.Parse(File.ReadAllText(Fixture(prefix + ".json")));
        var root = metadata.RootElement;
        T Read<T>(JsonElement value, string name) => JsonSerializer.Deserialize<T>(value.GetProperty(name).GetRawText())!;
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
        const int control = 4;
        var caption = scene.BoardTriggerRegions![control];
        double[] map = scene.CameraToBoard.ToArray();
        int centerX = (int)Math.Round((caption.X + caption.Width * .5 - map[2]) / map[0]);
        int tipY = (int)Math.Round((caption.Y - caption.Height * .75 - map[5]) / map[4]);
        var epoch = new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);
        byte[] grouped = Fingers(20), spread = Fingers(36);
        foreach (bool warm in new[] { false, true })
        foreach (var (name, pixels) in new[] { ("grouped", grouped), ("grouped left", Fingers(20, -4)),
            ("grouped right", Fingers(20, 4)), ("spread", spread) })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Quiet(Feed(tracker, empty, 0), "Initial clear menu");
            var first = Feed(tracker, pixels, 100);
            var firstCaption = first.TextPatterns!.Single(pattern => pattern.ControlRegion == control);
            Require(first.Hints.Count == 0 && firstCaption.ShapeCorrupted && !firstCaption.LabelIntact &&
                firstCaption.ConfirmationFrames == 1, $"{name}: caption loss did not start one fresh confirmation: " + JsonSerializer.Serialize(firstCaption));
            var second = Feed(tracker, pixels, 200);
            var pattern = second.TextPatterns!.Single(pattern => pattern.ControlRegion == control);
            Require(pattern.ShapeCorrupted && !pattern.LabelIntact && pattern.ConfirmationFrames == 2 &&
                second.Hints.Count == 1 && second.Hints[0].ControlCoverage >= .07 &&
                second.Hints[0].ControlTriggerCoverage >= .07,
                $"{name}: caption corruption did not acquire through both independent area floors.");
            if (name == "grouped" && !warm)
                Require(pattern.Correlation is > .78 and < .82 && pattern.LocalDamageCoverage >= .07,
                    "The grouped fixture no longer exercises locally broken but globally recognizable lettering.");
            Require(second.TextPatterns!.Where(pattern => pattern.ControlRegion != control)
                .All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted),
                "Grouped caption loss corrupted an untouched neighbour.");
            Require(Feed(tracker, pixels, 200).Hints.Count == 0 && Feed(tracker, pixels, 300).Hints.Count == 1,
                "Repeated frames advanced confirmation or stationary grouped fingers were forgotten.");
            Quiet(Feed(tracker, empty, 400), "Removed grouped fingers");
        }

        foreach (var (name, gain, offsets) in new[]
            { ("dimmer", .72, new[]{8,8,8}), ("brighter", 1.12, new[]{12,12,12}),
              ("colour cast", .76, new[]{24,9,38}) })
        {
            byte[] intact = (byte[])empty.Clone();
            for (int pixel = 0; pixel < intact.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++)
                    intact[pixel + channel] = (byte)Math.Clamp(empty[pixel + channel] * gain + offsets[channel], 0, 255);
            var tracker = new HandAcquisitionPresenceTracker();
            Feed(tracker, empty, 0);
            Feed(tracker, grouped, 100);
            Quiet(Feed(tracker, intact, 200), "Intact " + name + " lettering");
            var restarted = Feed(tracker, grouped, 300);
            Require(restarted.Hints.Count == 0 && restarted.TextPatterns!.Single(pattern => pattern.ControlRegion == control)
                .ConfirmationFrames == 1, "Intact lettering retained partial obstruction progress.");
        }
        foreach (bool tint in new[] { false, true })
        {
            byte[] intact = (byte[])empty.Clone();
            var area = scene.BoardSearchRegions![control];
            for (int y = (int)Math.Ceiling((area.Y - map[5]) / map[4]); y < (area.Y + area.Height - map[5]) / map[4]; y++)
            for (int x = (int)Math.Ceiling((area.X - map[2]) / map[0]); x < (area.X + area.Width - map[2]) / map[0]; x++)
                for (int channel = 0; channel < 3; channel++)
                {
                    int index = (y * width + x) * 4 + channel;
                    intact[index] = (byte)(empty[index] * .75 + (tint ? new[] { 15, 4, 25 }[channel] : 4));
                }
            var tracker = new HandAcquisitionPresenceTracker();
            Feed(tracker, empty, 0);
            Feed(tracker, grouped, 100);
            var clear = Feed(tracker, intact, 200).TextPatterns!.Single(pattern => pattern.ControlRegion == control);
            // Acquisition may separately observe surface reflectance. It must
            // never call intact coloured letters corrupted or continue a hold.
            Require(clear.LabelIntact && !clear.ShapeCorrupted && clear.ConfirmationFrames == 0,
                "Local surface dimming or colour was mistaken for damaged caption shapes.");
            Require(Feed(tracker, grouped, 300).TextPatterns!.Single(pattern => pattern.ControlRegion == control)
                .ConfirmationFrames == 1, "Intact surface changes retained partial glyph-corruption progress.");
        }
        Console.WriteLine("Grouped menu caption acquisition passed: unchanged global match, shifted local glyph loss, " +
            "cold/warm grouped and spread fingers, two fresh observations, both 7% floors, stationary persistence, " +
            "clear/dim/bright/colour cancellation and intact neighbours.");

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var time = epoch.AddMilliseconds(milliseconds);
            return tracker.Update(width, height, width * 4, pixels, polygon, scene, time, time);
        }
        byte[] Fingers(int spacing, int offset = 0)
        {
            using var image = new Mat(height, width, MatType.CV_8UC4);
            Marshal.Copy(empty, 0, image.Data, empty.Length);
            var colour = new Scalar(75, 95, 185, 255);
            for (int finger = 0; finger < 4; finger++)
            {
                int x = centerX + offset + (finger - 1) * spacing, y = tipY + new[] { 8, 0, 7, 20 }[finger];
                Cv2.Rectangle(image, new Rect(x - 9, y + 9, 18, 86), colour, -1);
                Cv2.Circle(image, new Point(x, y + 9), 9, colour, -1);
            }
            Cv2.Ellipse(image, new Point(centerX + offset + 9, tipY + 117), new Size(39, 40), 0, 0, 360, colour, -1);
            byte[] pixels = new byte[empty.Length]; Marshal.Copy(image.Data, pixels, 0, pixels.Length); return pixels;
        }
        byte[] Pixels(string role)
        {
            using var image = Cv2.ImRead(Fixture(prefix + "-" + role + ".png"), ImreadModes.Unchanged);
            Require(image.Type() == MatType.CV_8UC4 && image.Width == width && image.Height == height,
                "The generated menu fixture geometry changed.");
            byte[] pixels = new byte[width * height * 4]; Marshal.Copy(image.Data, pixels, 0, pixels.Length); return pixels;
        }
    }
    private static void Quiet(HandAcquisitionPresenceResult result, string description) => Require(result.Hints.Count == 0 &&
        result.TextPatterns!.All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted && pattern.ConfirmationFrames == 0),
        description + " was mistaken for broken lettering: " + JsonSerializer.Serialize(result));
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
