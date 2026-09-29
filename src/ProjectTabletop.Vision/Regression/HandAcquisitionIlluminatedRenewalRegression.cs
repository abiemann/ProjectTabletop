using System.Text.Json;
using ProjectTabletop.Vision;

internal static class HandAcquisitionIlluminatedRenewalRegression
{
    private const int Width = 640, Height = 360;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(0, 0), new(Width, 0), new(Width, Height), new(0, Height)];
    private static readonly HandTrackingBounds Control = new(.28, .51, .36, .29);
    private static readonly HandTrackingBounds Caption = new(.435, .585, .10, .10);
    private static readonly HandAcquisitionHint Light = new(new(205, 140, 190, 190), new(300, 235), 92,
        Epoch, .04, ControlCoverage: .92, ControlTriggerCoverage: .93);

    public static void Run()
    {
        EmptyGradientsCannotRenew();
        StationaryGroupedFingersAndRemoval();
        BroadPeripheralOcclusionCannotTrainWhite();
        PairedAreaFloorsAndFreshFrames();
        ClippedLightIsNotEmpty();
        Console.WriteLine("Illuminated renewal passed: empty horizontal/diagonal/chromatic shading, prolonged false-light " +
            "rejection, localized stationary grouped fingers, peripheral occlusion, removal, paired 7% areas, source-time " +
            "barriers and clipped night-exposure white.");
    }

    private static void EmptyGradientsCannotRenew()
    {
        var scene = Scene();
        foreach (int profile in new[] { 0, 1, 2 })
        {
            byte[] lit = ShadedLight(scene.Bgra, profile);
            var tracker = new HandAcquisitionPresenceTracker();
            Require(Feed(tracker, scene.Bgra, scene, 0).Hints.Count == 0,
                "The generated unlit fixture was already disturbed");
            Require(Feed(tracker, lit, scene, 100, 100).IlluminatedPresence is null,
                "A search-light transition was classified before camera settling");
            foreach (int time in new[] { 350, 450, 550, 750, 1200, 5000, 300000 })
            {
                var result = Feed(tracker, lit, scene, time, 100);
                Require(result.IlluminatedPresence == false && result.Hints.Count == 0,
                    $"Empty smooth white profile {profile} retained a false light at {time} ms", result);
            }
            Require(Feed(tracker, scene.Bgra, scene, 300100).Hints.Count == 0,
                "An expired false light became foreground when projection returned to the board");
        }
    }

    private static void StationaryGroupedFingersAndRemoval()
    {
        var scene = Scene();
        foreach (int profile in new[] { 0, 1, 2 })
        {
            byte[] lit = ShadedLight(scene.Bgra, profile), fingers = (byte[])lit.Clone();
            for (int finger = 0; finger < 4; finger++)
                Patch(fingers, 278 + finger * 13, 210, 12, 60, 70, 105, 150);
            var tracker = new HandAcquisitionPresenceTracker();
            Feed(tracker, scene.Bgra, scene, 0);
            for (int time = 350; time <= 1750; time += 100)
            {
                var result = Feed(tracker, fingers, scene, time, 100);
                // Four 12 × 60 strips occupy about 12% of the 24,000-pixel
                // control. Allow a small native sampling margin; the surrounding
                // white gradient must not inflate their measured evidence.
                Require(result.IlluminatedPresence == true && result.Hints.Count == 1 &&
                    result.Hints[0].ControlCoverage is >= .07 and <= .16 &&
                    result.Hints[0].ControlTriggerCoverage is >= .07 and < .93 &&
                    result.Hints[0].ObservedAt == Epoch.AddMilliseconds(time),
                    $"Localized grouped fingers were absorbed or lost fresh paired areas under profile {profile}", result);
            }
            var removed = Feed(tracker, lit, scene, 1850, 100);
            Require(removed.IlluminatedPresence == false && removed.Hints.Count == 0,
                "Removed fingers inherited their old evidence over a smoothly shaded white disk", removed);
            Require(Feed(tracker, scene.Bgra, scene, 1950).Hints.Count == 0,
                "Turning off the cleared light left a stationary foreground ghost");
        }
    }

    private static void BroadPeripheralOcclusionCannotTrainWhite()
    {
        var scene = Scene();
        byte[] lit = ShadedLight(scene.Bgra, 1);
        foreach (var surface in new (byte Blue, byte Green, byte Red)[] { (105, 115, 125), (85, 115, 165) })
        {
            byte[] occupied = (byte[])lit.Clone();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                double distanceSquared = Math.Pow(x - Light.Center.X, 2) + Math.Pow(y - Light.Center.Y, 2);
                // All peripheral white-model sectors are obscured, while a
                // central patch of actual bright paper remains visible. A fit
                // to the broad dark surface cannot redefine it as empty white.
                if (distanceSquared > Math.Pow(Light.RadiusPixels * .70, 2) || distanceSquared <= 27 * 27) continue;
                int index = (y * Width + x) * 4;
                occupied[index] = surface.Blue; occupied[index + 1] = surface.Green; occupied[index + 2] = surface.Red;
            }
            var tracker = new HandAcquisitionPresenceTracker();
            Feed(tracker, scene.Bgra, scene, 0);
            foreach (int time in new[] { 350, 450, 650 })
            {
                var result = Feed(tracker, occupied, scene, time, 100);
                Require(result.IlluminatedPresence == true && result.Hints.Count == 1 &&
                    result.Hints[0].ControlCoverage is >= .18 and < .80 &&
                    result.Hints[0].ControlTriggerCoverage is >= .07 and < .93 &&
                    result.Hints[0].ObservedAt == Epoch.AddMilliseconds(time),
                    "A broad peripheral occlusion trained the white model or counted only its visible paper patch", result);
            }
            var removed = Feed(tracker, lit, scene, 750, 100);
            Require(removed.IlluminatedPresence == false && removed.Hints.Count == 0,
                "Removing a broad peripheral obstruction did not restore empty-light rejection", removed);
        }
    }

    private static void PairedAreaFloorsAndFreshFrames()
    {
        var scene = Scene();
        byte[] lit = ShadedLight(scene.Bgra, 1), fingers = (byte[])lit.Clone();
        for (int finger = 0; finger < 4; finger++) Patch(fingers, 278 + finger * 13, 210, 12, 60, 70, 105, 150);
        var tracker = new HandAcquisitionPresenceTracker();
        Feed(tracker, scene.Bgra, scene, 0);
        Require(Feed(tracker, fingers, scene, 350, 100).IlluminatedPresence == true,
            "The freshness fixture did not establish a real localized obstruction");
        var duplicate = Feed(tracker, fingers, scene, 350, 100);
        Require(duplicate.Reason == "old-camera-frame" && duplicate.Hints.Count == 0,
            "A duplicated camera frame renewed the spotlight");
        var stale = Feed(tracker, fingers, scene, 400, 100, now: 750);
        Require(stale.Reason == "stale-camera-frame" && stale.Hints.Count == 0,
            "A stale obstruction published a fresh renewal timestamp");
        var fresh = Feed(tracker, fingers, scene, 800, 100);
        Require(fresh.IlluminatedPresence == true && fresh.Hints[0].ObservedAt == Epoch.AddMilliseconds(800),
            "Rejecting stale frames prevented a genuinely fresh stationary-hand observation");

        byte[] tiny = (byte[])lit.Clone();
        Patch(tiny, 294, 220, 10, 15, 70, 105, 150);
        var subFloor = Feed(tracker, tiny, scene, 900, 100);
        Require(subFloor.IlluminatedPresence == false && subFloor.Hints.Count == 0,
            "Smooth shading plus a sub-7% fragment reused old control coverage", subFloor);

        byte[] outsideCaption = (byte[])lit.Clone();
        Patch(outsideCaption, 238, 205, 38, 70, 70, 105, 150);
        var nonCaption = Feed(tracker, outsideCaption, scene, 1000, 100);
        Require(nonCaption.IlluminatedPresence == false && nonCaption.Hints.Count == 0,
            "A non-caption obstruction renewed the light through inherited trigger evidence", nonCaption);
        var empty = Feed(tracker, lit, scene, 1100, 100);
        Require(empty.IlluminatedPresence == false && empty.Hints.Count == 0,
            "Fresh unoccluded shading did not clear the previous obstruction");
    }

    // A night-exposed camera clips the lit board and pale lit fingers to the
    // same white. That frame cannot show the hand left; it must stay unknown
    // and report its white level so the light can be dimmed.
    private static void ClippedLightIsNotEmpty()
    {
        var scene = Scene();
        byte[] clipped = (byte[])scene.Bgra.Clone();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double dx = x - Light.Center.X, dy = y - Light.Center.Y;
            if (dx * dx + dy * dy <= Light.RadiusPixels * Light.RadiusPixels) Patch(clipped, x, y, 1, 1, 254, 255, 255);
        }
        var tracker = new HandAcquisitionPresenceTracker();
        Feed(tracker, scene.Bgra, scene, 0);
        var result = Feed(tracker, clipped, scene, 350, 100);
        Require(result.IlluminatedPresence is null && result.Reason == "search-light-saturated" &&
            result.IlluminatedWhiteLuminance >= HandAcquisitionPresenceTracker.SaturatedIlluminatedWhite,
            "A clipped white core was reported as an empty search light", result);
        byte[] fingers = (byte[])clipped.Clone();
        for (int finger = 0; finger < 4; finger++)
            Patch(fingers, 278 + finger * 13, 210, 12, 60, 70, 105, 150);
        var fingerTracker = new HandAcquisitionPresenceTracker();
        Feed(fingerTracker, scene.Bgra, scene, 0);
        var visible = Feed(fingerTracker, fingers, scene, 350, 100);
        Require(visible.IlluminatedPresence == true && visible.Hints.Count == 1,
            "Fingers still visible in a clipped light were not kept", visible);
        var shaded = ShadedLight(scene.Bgra, 0);
        var normal = Feed(tracker, shaded, scene, 450, 100);
        Require(normal.IlluminatedWhiteLuminance is > 150 and < HandAcquisitionPresenceTracker.SaturatedIlluminatedWhite &&
            normal.IlluminatedPresence == false, "An unclipped empty light did not report its white level", normal);
    }

    private static HandAcquisitionSceneImage Scene()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int index = (y * Width + x) * 4;
            pixels[index] = (byte)(25 + x % 97);
            pixels[index + 1] = (byte)(45 + y % 79);
            pixels[index + 2] = (byte)(35 + (x + y) % 83);
            pixels[index + 3] = 255;
            if (Within(Control, x, y))
            { pixels[index] = 30; pixels[index + 1] = 45; pixels[index + 2] = 60; }
        }
        return new(Width, Height, pixels, [1.0 / (Width - 1), 0, 0, 0, 1.0 / (Height - 1), 0, 0, 0, 1],
            [Control], [new(.02, .03, .95, .36)], [Caption]);
    }

    private static byte[] ShadedLight(byte[] empty, int profile)
    {
        var pixels = (byte[])empty.Clone();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double dx = x - Light.Center.X, dy = y - Light.Center.Y;
            if (dx * dx + dy * dy > Light.RadiusPixels * Light.RadiusPixels) continue;
            int index = (y * Width + x) * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                double shade = profile switch
                {
                    0 => 182 + .75 * dx,
                    1 => 182 + .55 * dx + .45 * dy,
                    _ => 182 + .60 * dx + (channel == 0 ? .75 : channel == 1 ? -.10 : .30) * dy
                };
                pixels[index + channel] = (byte)Math.Clamp(Math.Round(shade), 0, 255);
            }
        }
        return pixels;
    }

    private static bool Within(HandTrackingBounds region, double x, double y) =>
        x / (Width - 1) >= region.X && x / (Width - 1) <= region.X + region.Width &&
        y / (Height - 1) >= region.Y && y / (Height - 1) <= region.Y + region.Height;
    private static void Patch(byte[] pixels, int x, int y, int width, int height, byte blue, byte green, byte red)
    {
        for (int py = y; py < y + height; py++)
        for (int px = x; px < x + width; px++)
        {
            int index = (py * Width + px) * 4;
            pixels[index] = blue; pixels[index + 1] = green; pixels[index + 2] = red;
        }
    }
    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage scene, int at, int? lightStarted = null, int? now = null) =>
        tracker.Update(Width, Height, Width * 4, pixels, Polygon, scene,
            Epoch.AddMilliseconds(at), Epoch.AddMilliseconds(now ?? at),
            lightStarted.HasValue ? Light : null, lightStarted.HasValue ? Epoch.AddMilliseconds(lightStarted.Value) : null);
    private static void Require(bool condition, string message, object? details = null)
    {
        if (!condition) throw new InvalidOperationException(message +
            (details is null ? "." : ": " + JsonSerializer.Serialize(details)));
    }
}
