using ProjectTabletop.Vision;

internal static class PhotoConnectedPaleSurfaceRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    private const int Left = 330, Top = 450, Width = 250, Height = 240;
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, 0, 0, 0, 1];

    public static void Run()
    {
        // The complete pale face contrasts with the grey acquisition field, so
        // the rim and printing belong to the same connected object. When its
        // light turns on, only those darker features remain distinguishable.
        // This deliberately bypasses multi-component pale-surface recovery.
        foreach (bool hole in new[] { false, true })
        {
            byte[] acquisition = Photo(hole, new(115, 115, 115), new(177, 181, 185), new(24, 31, 39));
            PhotoObjectTarget target = Locate(acquisition);
            Require(Alpha(target, 120, 185) == 255 && Alpha(target, -8, 100) == 0,
                "A connected pale face lost its original silhouette.");
            Require(!hole || Alpha(target, 207, 187) == 0,
                "Acquiring contrast evidence filled a real opening in the connected face.");
            Require(target.ContrastEvidence is not null &&
                    target.ContrastEvidence.Count(value => value >= 200) < target.ForegroundArea * .5,
                "A connected pale face still requires its whole blank area to retain contrast under white light.");

            foreach (var exposure in new[]
                     {
                         (Background: new Color(211, 214, 217), Face: new Color(216, 219, 222), Ink: new Color(53, 65, 80)),
                         (Background: new Color(235, 235, 235), Face: new Color(235, 235, 235), Ink: new Color(65, 73, 81)),
                         (Background: new Color(245, 245, 245), Face: new Color(242, 246, 249), Ink: new Color(70, 85, 99))
                     })
            {
                byte[] illuminated = Photo(hole, exposure.Background, exposure.Face, exposure.Ink);
                Require(Observe(illuminated, target) == PhotoObjectTargetState.Present,
                    "Turning on the light lost a stationary connected pale object with visible acquired printing.");

                // The old full-silhouette check must fail this scene, otherwise
                // the fixture no longer reproduces the spotlight feedback loop.
                var wholeBody = new PhotoObjectTarget(target.Left, target.Top, target.Width, target.Height,
                    target.Alpha.ToArray(), target.ForegroundArea);
                Require(Observe(illuminated, wholeBody) == PhotoObjectTargetState.MissingOrMoved,
                    "The fixture stopped exercising the connected pale-face contrast failure.");
                CheckCapture(illuminated, target, hole);

                Reject(Photo(hole, exposure.Background, exposure.Face, exposure.Ink, removed: true), target,
                    "An empty white spotlight kept the removed connected object locked.");
                Reject(Photo(hole, exposure.Background, exposure.Face, exposure.Ink, objectOffset: 45), target,
                    "Moving the connected object left its old silhouette eligible for capture.");
                Reject(Photo(hole, exposure.Background, exposure.Face, exposure.Ink, printOffset: 70), target,
                    "Displaced printing was accepted from an old connected-object lock.");
                Reject(Photo(hole, exposure.Background, exposure.Background, exposure.Background), target,
                    "A completely washed-out face with no remaining ink or edge evidence was accepted.");
            }
        }

        // A uniformly light or dark item has no measured darker printed region
        // to substitute for its acquired body. Keep its existing presence rule.
        foreach (Color solid in new[] { new Color(25, 25, 25), new Color(220, 220, 220) })
        {
            PhotoObjectTarget target = Locate(Photo(false, new(115, 115, 115), solid, solid));
            Require(target.ContrastEvidence is null,
                "A uniform solid item acquired an unsupported printed-feature fallback.");
            Reject(Photo(false, new(245, 245, 245), new(245, 245, 245), new(245, 245, 245)), target,
                "A uniform item matching the white light was accepted without visible evidence.");
        }
        Console.WriteLine("Connected pale-surface regression: acquisition through one silhouette, exposure and white-light " +
            "continuity, complete current RGB and real holes; removed, moved, shifted-print, washed-out and uniform-solid safeguards passed.");
    }

    private static void CheckCapture(byte[] illuminated, PhotoObjectTarget target, bool hole)
    {
        PhotoHandCutout? cutout = PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, illuminated, null,
            BoardMap, target, out var failure);
        Require(cutout is not null && failure is null, "A verified connected pale object could not be copied: " + failure);
        Require(cutout!.Width == target.Width && cutout.Height == target.Height &&
                cutout.BoardOrigin == new PixelPoint(target.Left, target.Top),
            "Evidence selection resized or recentered the acquired photograph.");
        int paleCount = 0;
        for (int y = 0; y < target.Height; y++)
        for (int x = 0; x < target.Width; x++)
        {
            int index = y * target.Width + x, destination = index * 4;
            Require(cutout.BgraPixels[destination + 3] == target.Alpha[index],
                "Evidence selection changed the saved silhouette alpha.");
            if (target.Alpha[index] == 0)
            {
                Require(cutout.BgraPixels[destination] == 0 && cutout.BgraPixels[destination + 1] == 0 &&
                        cutout.BgraPixels[destination + 2] == 0,
                    "A transparent exterior or opening retained background RGB.");
                continue;
            }
            int source = ((target.Top + y) * Size + target.Left + x) * 4;
            for (int channel = 0; channel < 3; channel++)
                Require(cutout.BgraPixels[destination + channel] == illuminated[source + channel],
                    "The copied object reused acquisition color or recolored its pale face.");
            if (target.ContrastEvidence![index] == 0 && target.Alpha[index] == 255) paleCount++;
        }
        Require(paleCount > target.ForegroundArea * .5,
            "The test no longer retains a majority blank face independently of contrast evidence.");
        Require(!hole || Alpha(target, 207, 187) == 0,
            "White illumination or copying filled the object's real hole.");
    }

    private static byte[] Photo(bool hole, Color background, Color face, Color ink,
        bool removed = false, int objectOffset = 0, int printOffset = 0)
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int localX = x - Left - objectOffset, localY = y - Top;
            Color color = background;
            if (!removed && Surface(localX, localY, hole))
                color = Rim(localX, localY) || Printing(localX - printOffset, localY) ? ink : face;
            int index = (y * Size + x) * 4;
            pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static bool Surface(int x, int y, bool hole) => x >= 0 && x < Width && y >= 0 && y < Height &&
        !(hole && In(x, y, 196, 174, 22, 26));
    private static bool Rim(int x, int y) => x < 8 || x >= Width - 2 || y < 2 || y >= Height - 9;
    private static bool Printing(int x, int y) => In(x, y, 25, 28, 80, 23) ||
        In(x, y, 120, 35, 84, 28) || In(x, y, 34, 89, 72, 42) ||
        In(x, y, 145, 106, 58, 32) || In(x, y, 43, 167, 55, 28);
    private static bool In(int x, int y, int left, int top, int width, int height) =>
        x >= left && x < left + width && y >= top && y < top + height;
    private static byte Alpha(PhotoObjectTarget target, int localX, int localY)
    {
        int x = Left + localX - target.Left, y = Top + localY - target.Top;
        return x < 0 || x >= target.Width || y < 0 || y >= target.Height ? (byte)0 : target.Alpha[y * target.Width + x];
    }
    private static PhotoObjectTarget Locate(byte[] photo) =>
        PhotoObjectLocator.Locate(Size, Size, Size * 4, photo, BoardMap, out var failure) ??
        throw new InvalidOperationException("Connected object acquisition failed: " + failure);
    private static PhotoObjectTargetState Observe(byte[] photo, PhotoObjectTarget target) =>
        PhotoObjectLocator.ObserveTarget(Size, Size, Size * 4, photo, BoardMap, target, out _);
    private static void Reject(byte[] photo, PhotoObjectTarget target, string message)
    {
        Require(Observe(photo, target) == PhotoObjectTargetState.MissingOrMoved, message);
        Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, photo, null, BoardMap, target,
                out var failure) is null && failure is not null,
            "Failed connected-object verification still produced a photograph.");
    }
    private readonly record struct Color(byte B, byte G, byte R);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
