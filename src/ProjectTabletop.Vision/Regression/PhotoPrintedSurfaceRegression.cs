using ProjectTabletop.Vision;

internal static class PhotoPrintedSurfaceRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    private const int Left = 250, Top = 400, Width = 284, Height = 204;
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, 0, 0, 0, 1];

    public static void Run()
    {
        foreach (bool hole in new[] { false, true })
        foreach (var pale in new[] { (B: (byte)245, G: (byte)245, R: (byte)245),
            (B: (byte)242, G: (byte)246, R: (byte)249) })
        {
            var target = Target(hole);
            var lit = Photo(hole, pale);
            Require(Observe(lit, target) == PhotoObjectTargetState.Present,
                "A pale printed surface lost presence when its blank face matched the white light.");

            // This is the reported failure mode: treating the filled pale surface
            // itself as required contrast rejects it, despite visible ink and edges.
            var ordinary = new PhotoObjectTarget(Left, Top, Width, Height,
                target.Alpha.ToArray(), target.ForegroundArea);
            Require(Observe(lit, ordinary) == PhotoObjectTargetState.MissingOrMoved,
                "The fixture no longer distinguishes pale-surface verification from whole-body contrast.");

            var cutout = PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, lit, null,
                BoardMap, target, out var failure);
            Require(cutout is not null && failure is null, "A verified pale object could not be photographed: " + failure);
            Require(cutout!.Width == Width && cutout.Height == Height && cutout.BoardOrigin == new PixelPoint(Left, Top),
                "Pale-surface capture changed its locked outline or crop origin.");
            int palePixels = 0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int index = y * Width + x, output = index * 4;
                Require(cutout.BgraPixels[output + 3] == target.Alpha[index],
                    "Photographing pale printing replaced the complete silhouette with only its ink.");
                if (target.Alpha[index] == 0)
                {
                    Require(cutout.BgraPixels[output] == 0 && cutout.BgraPixels[output + 1] == 0 &&
                            cutout.BgraPixels[output + 2] == 0,
                        "Transparent borders or a real opening retained projected background pixels.");
                    continue;
                }
                int source = ((Top + y) * Size + Left + x) * 4;
                Require(cutout.BgraPixels[output] == lit[source] &&
                        cutout.BgraPixels[output + 1] == lit[source + 1] &&
                        cutout.BgraPixels[output + 2] == lit[source + 2],
                    "Surface recovery painted or recolored the actual photograph.");
                if (!Ink(x, y))
                {
                    palePixels++;
                    Require(cutout.BgraPixels[output] == pale.B && cutout.BgraPixels[output + 1] == pale.G &&
                            cutout.BgraPixels[output + 2] == pale.R && cutout.BgraPixels[output + 3] == 255,
                        "The object's blank pale face was not retained as opaque photographed RGB.");
                }
            }
            Require(palePixels > target.ForegroundArea * .70,
                "The fixture stopped exercising a mostly pale surface with sparse printed features.");
            Require(cutout.BgraPixels[3] == 0 &&
                    (!hole || cutout.BgraPixels[(140 * Width + 204) * 4 + 3] == 0),
                "Pale recovery filled an exterior pixel or the fixture's real opening.");

            Reject(Photo(hole, pale, removed: true), target,
                "The white projected halo alone proved a removed pale object was still present.");
            Reject(Photo(hole, pale, printOffset: 55), target,
                "Moving the printed features away from their locked positions was accepted.");
            Reject(Photo(hole, pale, objectOffset: 42), target,
                "A moved pale object was photographed through its old outline.");

            var overlap = OverlappingHand();
            Require(Observe(lit, target, [overlap]) == PhotoObjectTargetState.Occluded,
                "A detected hand over the recovered pale surface was not treated as occlusion.");
            Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, lit, overlap,
                        BoardMap, target, out failure) is null && failure is not null &&
                    PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, lit, null,
                        BoardMap, target, out failure, [overlap]) is null && failure is not null,
                "A selecting or remaining hand overlapping the pale object bypassed capture rejection.");
        }
        Console.WriteLine("Printed pale-surface regression: current opaque RGB, complete alpha and real holes, " +
            "white-halo removal, shifted printing/object and overlapping-hand capture rejection passed.");
    }

    private static PhotoObjectTarget Target(bool hole)
    {
        var alpha = new byte[Width * Height];
        var evidence = new byte[alpha.Length];
        int area = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Surface(x, y, hole)) continue;
            alpha[y * Width + x] = 255;
            area++;
            if (Ink(x, y)) evidence[y * Width + x] = 255;
        }
        return new(Left, Top, Width, Height, alpha, area, evidence);
    }

    private static byte[] Photo(bool hole, (byte B, byte G, byte R) pale,
        bool removed = false, int printOffset = 0, int objectOffset = 0)
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int index = (y * Size + x) * 4;
            byte background = x >= Left - 35 && x < Left + Width + 35 &&
                y >= Top - 35 && y < Top + Height + 35 ? (byte)245 : (byte)100;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = background;
            pixels[index + 3] = 255;
            int localX = x - Left - objectOffset, localY = y - Top;
            if (removed || !Surface(localX, localY, hole)) continue;
            bool ink = Perimeter(localX, localY) || PrintedFeature(localX - printOffset, localY);
            pixels[index] = ink ? (byte)18 : pale.B;
            pixels[index + 1] = ink ? (byte)27 : pale.G;
            pixels[index + 2] = ink ? (byte)35 : pale.R;
        }
        return pixels;
    }

    private static bool Surface(int x, int y, bool hole) => x >= 2 && x < Width - 2 &&
        y >= 2 && y < Height - 2 && !(hole && x >= 190 && x < 218 && y >= 128 && y < 154);
    private static bool Perimeter(int x, int y) => x < 5 || x >= Width - 5 || y < 5 || y >= Height - 5;
    private static bool Ink(int x, int y) => Perimeter(x, y) || PrintedFeature(x, y);
    private static bool PrintedFeature(int x, int y) =>
        In(x, y, 25, 30, 32, 24) || In(x, y, 95, 30, 44, 20) || In(x, y, 205, 35, 36, 25) ||
        In(x, y, 36, 105, 42, 30) || In(x, y, 105, 145, 48, 25);
    private static bool In(int x, int y, int left, int top, int width, int height) =>
        x >= left && x < left + width && y >= top && y < top + height;
    private static PhotoObjectTargetState Observe(byte[] pixels, PhotoObjectTarget target,
        IReadOnlyList<HandDetection>? hands = null) =>
        PhotoObjectLocator.ObserveTarget(Size, Size, Size * 4, pixels, BoardMap, target, out _, hands);
    private static void Reject(byte[] pixels, PhotoObjectTarget target, string message)
    {
        Require(Observe(pixels, target) == PhotoObjectTargetState.MissingOrMoved, message);
        Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, pixels, null,
                BoardMap, target, out var failure) is null && failure is not null,
            "A failed pale-surface presence check still returned a photograph.");
    }
    private static HandDetection OverlappingHand()
    {
        PixelPoint[] points = [new(417, 537), new(402, 528), new(390, 518), new(379, 508), new(400, 470),
            new(402, 504), new(400, 483), new(399, 470), new(400, 470), new(417, 499), new(417, 476),
            new(417, 459), new(417, 446), new(430, 502), new(432, 483), new(433, 469), new(433, 458),
            new(438, 508), new(445, 495), new(448, 484), new(450, 474)];
        return new(points, .99, .5);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
