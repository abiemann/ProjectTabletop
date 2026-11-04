namespace ProjectTabletop.Vision;

public sealed record BoardColorCalibrationResult(bool IsUsable, int ValidEdges, double WhiteLevel,
    double MidGrayLevel, double BlackLevel, double ClippedFraction, double NeutralRedBlueError,
    double NeutralGreenError, double ColorSeparation, double ColorError, double ExposureScore,
    int ExposureDirection, int WhiteBalanceDirection)
{
    public double WhiteBalanceScore => Math.Abs(NeutralRedBlueError) + Math.Abs(NeutralGreenError);
}

/// <summary>
/// Sparse measurements of the settled test-card strips. Recommendations never alter a camera or detector.
/// Use only after registration, wait for fresh settled frames after changes, and keep the best measured trial.
/// </summary>
public static class BoardColorCalibration
{
    private readonly record struct Sample(double R, double G, double B, double Clipped)
    {
        public double Level => (R + G + B) / 3;
        public double Maximum => Math.Max(R, Math.Max(G, B));
        public double Minimum => Math.Min(R, Math.Min(G, B));
    }

    private readonly record struct Edge(double White, double Middle, double Black, double Clipped,
        double RedBlue, double Green, bool NeutralUsable, double Separation, double ColorError);

    public static BoardColorCalibrationResult Analyze(int width, int height, int stride, byte[] bgra,
        Func<PixelPoint, PixelPoint> boardToCamera)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(boardToCamera);
        if (width <= 0 || height <= 0 || stride < (long)width * 4 ||
            bgra.Length < (long)(height - 1) * stride + (long)width * 4)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");

        Span<Edge> edges = stackalloc Edge[4];
        int count = 0;
        for (int edge = 0; edge < 4; edge++)
            if (MeasureEdge(edge, width, height, stride, bgra, boardToCamera) is { } measured)
                edges[count++] = measured;
        // One obscured edge is tolerated. A fragment, missing chart or stale projection cannot steer controls.
        if (count < 3) return new(false, count, 0, 0, 0, 0, 0, 0, 0, 0, double.PositiveInfinity, 0, 0);

        Edge[] edgeArray = edges[..count].ToArray();
        double Median(Func<Edge, double> selector)
        {
            // This function deliberately uses the small array below, outside the pixel loop.
            double[] selected = new double[count];
            for (int i = 0; i < count; i++) selected[i] = selector(edgeArray[i]);
            Array.Sort(selected);
            return selected[count / 2];
        }
        double white = edgeArray.Max(edge => edge.White), middle = Median(edge => edge.Middle);
        double black = Median(edge => edge.Black), clipped = edgeArray.Max(edge => edge.Clipped);
        double redBlue = Median(edge => edge.RedBlue), green = Median(edge => edge.Green);
        double separation = Median(edge => edge.Separation), colorError = Median(edge => edge.ColorError);
        double contrast = white - black;
        // Headroom beats a prettier but clipped picture. The low-contrast penalty prevents a bright wash
        // from being mistaken for a useful exposure; no brightness/contrast control is guessed from it.
        // The brightest ramp step is 244/244/248: full projected white can already clip before this
        // patch reaches 255. Penalize that narrow headroom as well as directly observed clipping.
        double score = Math.Abs(white - 212) + Math.Max(0, white - 235) * 8 +
            clipped * 400 + Math.Max(0, 90 - contrast) * 2;
        int exposure = clipped >= .08 || white >= 240 ? -1 : white < 145 && middle < 100 ? 1 : 0;
        bool neutralUsable = clipped < .02 && edgeArray.Count(edge => edge.NeutralUsable) >= 3;
        // Typical Kelvin controls warm as their value increases. A red cast suggests a lower value;
        // callers must verify the actual driver's response and restore any trial that makes it worse.
        int whiteBalance = !neutralUsable || exposure != 0 || Math.Abs(redBlue) < .06 ? 0 : redBlue > 0 ? -1 : 1;
        return new(true, count, white, middle, black, clipped, redBlue, green, separation, colorError,
            score, exposure, whiteBalance);
    }

    private static Edge? MeasureEdge(int edge, int width, int height, int stride, byte[] bgra,
        Func<PixelPoint, PixelPoint> map)
    {
        Span<Sample> gray = stackalloc Sample[5];
        for (int i = 0; i < 5; i++)
        {
            var segment = BoardCalibrationTestCard.GraySteps[i];
            if (SamplePatch(edge, segment.Start, segment.End, width, height, stride, bgra, map) is not { } sample)
                return null;
            gray[i] = sample;
        }
        if (gray[4].Level - gray[0].Level < 30 || gray[1].Level - gray[0].Level < 8 ||
            gray[2].Level - gray[1].Level < 8 || gray[3].Level < gray[2].Level - 6 ||
            gray[4].Level < gray[3].Level - 6) return null;

        int matchingColors = 0;
        double separation = 0, colorError = 0;
        for (int i = 0; i < 6; i++)
        {
            var expected = BoardCalibrationTestCard.ColorBars[i];
            if (SamplePatch(edge, expected.Start, expected.End, width, height, stride, bgra, map) is not { } sample)
                return null;
            double r = Math.Max(0, sample.R - gray[0].R), g = Math.Max(0, sample.G - gray[0].G);
            double b = Math.Max(0, sample.B - gray[0].B), total = Math.Max(1, r + g + b);
            double er = expected.Red - 20, eg = expected.Green - 20, eb = expected.Blue - 24;
            double expectedTotal = er + eg + eb;
            colorError += (Math.Abs(r / total - er / expectedTotal) + Math.Abs(g / total - eg / expectedTotal) +
                Math.Abs(b / total - eb / expectedTotal)) / 3;
            separation += (sample.Maximum - sample.Minimum) / Math.Max(1, sample.Maximum);
            bool match = (expected.Red - expected.Green) * (sample.R - sample.G) >= 0 &&
                (expected.Red - expected.Blue) * (sample.R - sample.B) >= 0 &&
                (expected.Green - expected.Blue) * (sample.G - sample.B) >= 0;
            if (match && sample.Maximum - sample.Minimum >= 10) matchingColors++;
        }
        if (matchingColors < 4) return null;

        double redBlue = 0, green = 0;
        int neutrals = 0;
        for (int i = 1; i <= 3; i++)
        {
            if (gray[i].Maximum >= 240 || gray[i].Minimum < 25 || gray[i].Clipped > .02) continue;
            // The printed grey ramp has B=R+4. Subtracting its black step removes that tint and ambient offset.
            double r = gray[i].R - gray[0].R, g = gray[i].G - gray[0].G, b = gray[i].B - gray[0].B;
            double level = (r + g + b) / 3;
            if (level < 20) continue;
            redBlue += (r - b) / level;
            green += (g - (r + b) * .5) / level;
            neutrals++;
        }
        return new(gray[4].Maximum, gray[2].Level, gray[0].Level, Math.Max(gray[3].Clipped, gray[4].Clipped),
            neutrals > 0 ? redBlue / neutrals : 0, neutrals > 0 ? green / neutrals : 0,
            neutrals >= 2, separation / 6, colorError / 6);
    }

    private static Sample? SamplePatch(int edge, double start, double end, int width, int height, int stride,
        byte[] bgra, Func<PixelPoint, PixelPoint> map)
    {
        PixelPoint first = map(BoardCalibrationTestCard.Point(edge, start + (end - start) * .25, .25));
        PixelPoint last = map(BoardCalibrationTestCard.Point(edge, end - (end - start) * .25, .75));
        if (Math.Abs(first.X - last.X) + Math.Abs(first.Y - last.Y) < 4) return null;
        Span<byte> red = stackalloc byte[25], green = stackalloc byte[25], blue = stackalloc byte[25];
        int clipped = 0;
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 5; x++)
        {
            var point = map(BoardCalibrationTestCard.Point(edge, start + (end - start) * (.25 + x * .125), .25 + y * .125));
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 ||
                point.X > width - 1 || point.Y > height - 1) return null;
            int offset = (int)Math.Round(point.Y) * stride + (int)Math.Round(point.X) * 4, index = y * 5 + x;
            blue[index] = bgra[offset]; green[index] = bgra[offset + 1]; red[index] = bgra[offset + 2];
            if (Math.Max(red[index], Math.Max(green[index], blue[index])) >= 250) clipped++;
        }
        red.Sort(); green.Sort(); blue.Sort();
        return new(red[12], green[12], blue[12], clipped / 25d);
    }
}
