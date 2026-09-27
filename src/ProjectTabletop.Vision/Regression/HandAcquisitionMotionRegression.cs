using ProjectTabletop.Vision;

internal static class HandAcquisitionMotionRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private const int Width = 640, Height = 360;
    private static readonly PixelPoint[] Region = [new(80, 100), new(560, 100), new(580, 330), new(60, 330)];

    public static void Run()
    {
        LocalMotionAndExpiry();
        ExposureNoiseAndProjectionChanges();
        NativeGeometryAndTwoRegions();
        TimesGeometryAndReset();
        Console.WriteLine("Hand acquisition motion regression: local disturbance search, square native-camera crops, " +
            "two-region bound, calibrated polygon containment, aspect/resolution invariance, noise and exposure rejection, " +
            "scene quiet period, stationary hint expiry, and timestamp/reset barriers passed.");
    }

    private static void LocalMotionAndExpiry()
    {
        var tracker = new HandAcquisitionMotionTracker();
        byte[] blank = Frame(), hand = Frame();
        Paint(hand, 250, 180, 55, 70, 175);
        Require(Feed(tracker, blank, 0).Count == 0, "A first frame invented motion.");
        Require(Feed(tracker, blank, 200).Count == 0, "A stationary board invented motion.");
        var hints = Feed(tracker, hand, 300);
        Require(hints.Count == 1, "An arriving hand-sized disturbance was not localized.");
        var hint = hints[0];
        Require(Math.Abs(hint.Center.X - 277.5) < 5 && Math.Abs(hint.Center.Y - 215) < 5,
            "The disturbance center moved out of native camera coordinates.");
        Require(hint.ObservedAt == Epoch.AddMilliseconds(300) && hint.MotionFraction is > .01 and < .1,
            "A hint did not retain its true source time or bounded motion evidence.");
        CheckCrop(hint, Width, Height);
        Require(Feed(tracker, hand, 400).Single().ObservedAt == hint.ObservedAt,
            "A stationary object renewed its motion evidence.");
        Require(Feed(tracker, hand, 600).Count == 1, "A brief acquisition pause discarded the motion crop too early.");
        Require(Feed(tracker, hand, 760).Count == 0, "A stationary scene kept a stale acquisition hint alive.");
    }

    private static void ExposureNoiseAndProjectionChanges()
    {
        var tracker = new HandAcquisitionMotionTracker();
        byte[] blank = Frame(), noise = Frame();
        Feed(tracker, blank, 0); Feed(tracker, blank, 200);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            byte value = (byte)(90 + ((x * 17 + y * 31) % 23) - 11);
            Paint(noise, x, y, 1, 1, value);
        }
        Require(Feed(tracker, noise, 300).Count == 0, "Low amplitude camera noise created a motion crop.");
        Require(Feed(tracker, Frame(100), 400).Count == 0, "A small global exposure step created a motion crop.");
        Require(Feed(tracker, Frame(145), 500).Count == 0, "A strong global exposure step created a motion crop.");
        byte[] local = Frame(145);
        Paint(local, 220, 170, 70, 80, 210);
        Require(Feed(tracker, local, 550).Count == 0, "A scene/exposure change did not allow its quiet period.");
        Feed(tracker, Frame(145), 750);
        Require(Feed(tracker, local, 850).Count == 1, "Local motion failed after exposure settled.");
        Require(Feed(tracker, Frame(220), 950).Count == 0, "A global flash retained an old spotlight hint.");

        tracker.Reset();
        // The caller resets before its own card/spotlight projection changes. Those changes
        // become the new baseline, so projected content cannot keep acquiring itself.
        Require(Feed(tracker, local, 1000).Count == 0 && Feed(tracker, local, 1250).Count == 0,
            "A reset treated the newly projected scene as physical motion.");
        byte[] outside = (byte[])local.Clone();
        Paint(outside, 0, 0, 80, 70, 255);
        Require(Feed(tracker, outside, 1350).Count == 0, "Motion outside the calibrated polygon triggered acquisition.");
        byte[] specks = (byte[])outside.Clone();
        for (int x = 100; x < 540; x += 45) Paint(specks, x, 140, 1, 1, 0);
        Require(Feed(tracker, specks, 1450).Count == 0, "Isolated bright/dark sensor specks formed a hand candidate.");
    }

    private static void NativeGeometryAndTwoRegions()
    {
        var normal = new HandAcquisitionMotionTracker();
        var doubled = new HandAcquisitionMotionTracker();
        byte[] first = Frame(), second = Frame(scale: 2);
        PixelPoint[] largerRegion = Region.Select(point => new PixelPoint(point.X * 2, point.Y * 2)).ToArray();
        Feed(normal, first, 0); Feed(normal, first, 200);
        Feed(doubled, second, 0, scale: 2, polygon: largerRegion);
        Feed(doubled, second, 200, scale: 2, polygon: largerRegion);
        Paint(first, 180, 180, 50, 60, 190);
        Paint(second, 360, 360, 100, 120, 190, scale: 2);
        HandAcquisitionHint a = Feed(normal, first, 300).Single();
        HandAcquisitionHint b = Feed(doubled, second, 300, scale: 2, polygon: largerRegion).Single();
        Require(Math.Abs(a.Center.X - b.Center.X / 2) < 2 && Math.Abs(a.Center.Y - b.Center.Y / 2) < 2 &&
            Math.Abs(a.SearchBounds.Width - b.SearchBounds.Width / 2) < 2 && Math.Abs(a.RadiusPixels - b.RadiusPixels / 2) < 2,
            "Camera resolution changed the normalized acquisition geometry or aspect ratio.");
        CheckCrop(b, Width * 2, Height * 2);

        normal.Reset();
        Feed(normal, Frame(), 0); Feed(normal, Frame(), 200);
        byte[] multiple = Frame();
        Paint(multiple, 90, 160, 45, 55, 190);
        Paint(multiple, 270, 170, 45, 55, 190);
        Paint(multiple, 480, 180, 45, 55, 190);
        var hints = Feed(normal, multiple, 300);
        Require(hints.Count == 2, "Motion search was not bounded to the two strongest distinct regions.");
        foreach (HandAcquisitionHint hint in hints) CheckCrop(hint, Width, Height);

        normal.Reset();
        PixelPoint[] edgeRegion = [new(0, 0), new(300, 0), new(300, 300), new(0, 300)];
        Feed(normal, Frame(), 0, polygon: edgeRegion); Feed(normal, Frame(), 200, polygon: edgeRegion);
        byte[] edge = Frame(); Paint(edge, 1, 1, 32, 48, 190);
        HandAcquisitionHint nearEdge = Feed(normal, edge, 300, polygon: edgeRegion).Single();
        CheckCrop(nearEdge, Width, Height);
        Require(nearEdge.SearchBounds.X == 0 && nearEdge.SearchBounds.Y == 0,
            "Edge acquisition cropped away useful context or ran outside the image.");
    }

    private static void TimesGeometryAndReset()
    {
        var tracker = new HandAcquisitionMotionTracker();
        byte[] blank = Frame(), hand = Frame(); Paint(hand, 220, 180, 50, 70, 190);
        Feed(tracker, blank, 0); Feed(tracker, blank, 200);
        Require(Feed(tracker, hand, 300).Count == 1, "Timestamp fixture did not acquire.");
        Require(Feed(tracker, blank, 250).Count == 0 && Feed(tracker, blank, 300).Count == 0,
            "An old or duplicate camera frame generated new acquisition evidence.");
        Require(Feed(tracker, hand, 400).Single().ObservedAt == Epoch.AddMilliseconds(300),
            "A rejected old frame poisoned the current baseline.");
        Require(Feed(tracker, hand, 500, nowMilliseconds: 900).Count == 0,
            "Stale camera data retained motion hints.");
        Require(Feed(tracker, blank, 950).Count == 0, "Stale data did not reset the baseline.");
        Require(Feed(tracker, hand, 1000, nowMilliseconds: 900).Count == 0,
            "Future camera data created a motion hint.");
        Require(Feed(tracker, hand, 1100).Count == 0, "A future-time reset kept old motion state.");
        Feed(tracker, hand, 1300);
        Require(Feed(tracker, blank, 1900).Count == 0, "A camera interruption was interpreted as hand motion.");
        PixelPoint[] movedRegion = Region.Select(point => new PixelPoint(point.X + 5, point.Y)).ToArray();
        Require(Feed(tracker, hand, 2100, polygon: movedRegion).Count == 0,
            "A recalibration compared unrelated camera polygons.");
        Require(Feed(tracker, hand, 2300, polygon: [new(double.NaN, 0), new(1, 0), new(1, 1)]).Count == 0,
            "Invalid calibration yielded a search crop.");
        tracker.Reset();
        Require(Feed(tracker, hand, 2500).Count == 0, "An explicit reset retained an old candidate.");

        // Extra camera row padding must not become pixels or alter native coordinates.
        int stride = Width * 4 + 16;
        byte[] padded = new byte[stride * Height];
        for (int row = 0; row < Height; row++) Array.Copy(blank, row * Width * 4, padded, row * stride, Width * 4);
        tracker.Reset();
        Require(tracker.Update(Width, Height, stride, padded, Region, Epoch, Epoch).Count == 0,
            "A valid padded camera stride failed.");
    }

    private static IReadOnlyList<HandAcquisitionHint> Feed(HandAcquisitionMotionTracker tracker, byte[] frame,
        int milliseconds, int? nowMilliseconds = null, int scale = 1, IReadOnlyList<PixelPoint>? polygon = null) =>
        tracker.Update(Width * scale, Height * scale, Width * scale * 4, frame, polygon ?? Region,
            Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(nowMilliseconds ?? milliseconds));

    private static byte[] Frame(byte value = 90, int scale = 1)
    {
        byte[] pixels = new byte[Width * Height * scale * scale * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static void Paint(byte[] pixels, int x, int y, int width, int height, byte value, int scale = 1)
    {
        for (int py = y; py < y + height; py++)
        for (int px = x; px < x + width; px++)
        {
            int offset = (py * Width * scale + px) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
        }
    }

    private static void CheckCrop(HandAcquisitionHint hint, int width, int height)
    {
        HandTrackingBounds bounds = hint.SearchBounds;
        Require(bounds.Width == bounds.Height && bounds.Width > 0 && bounds.X >= 0 && bounds.Y >= 0 &&
            bounds.X + bounds.Width <= width && bounds.Y + bounds.Height <= height,
            "A search crop is stretched, empty, or outside the native frame.");
        Require(hint.Center.X >= bounds.X && hint.Center.X <= bounds.X + bounds.Width &&
            hint.Center.Y >= bounds.Y && hint.Center.Y <= bounds.Y + bounds.Height,
            "Clamping a square crop lost the actual disturbance.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
