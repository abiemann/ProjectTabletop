namespace ProjectTabletop.Vision;

public readonly record struct BoardTestCardSegment(double Start, double End, double Near, double Far,
    byte Red, byte Green, byte Blue);

/// <summary>The projected edge strips and camera samples share this single board-normalized layout.</summary>
public static class BoardCalibrationTestCard
{
    public static IReadOnlyList<BoardTestCardSegment> Segments { get; } = Array.AsReadOnly(CreateSegments());
    public static IReadOnlyList<BoardTestCardSegment> ColorBars { get; } = Array.AsReadOnly(Segments.Take(6).ToArray());
    public static IReadOnlyList<BoardTestCardSegment> GraySteps { get; } = Array.AsReadOnly(Segments.Skip(20).Take(5).ToArray());

    // Edges run clockwise from each board corner: top, right, bottom, left.
    public static PixelPoint Point(int edge, double along, double across)
    {
        double s = .14 + .72 * along, depth = .012 + .045 * across;
        return edge switch
        {
            0 => new(s, depth),
            1 => new(1 - depth, s),
            2 => new(1 - s, 1 - depth),
            3 => new(depth, 1 - s),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    private static BoardTestCardSegment[] CreateSegments()
    {
        var segments = new List<BoardTestCardSegment>();
        (byte R, byte G, byte B)[] bars =
            [(250, 230, 60), (70, 200, 235), (90, 215, 90), (225, 80, 200), (230, 70, 50), (50, 80, 225)];
        (byte R, byte G, byte B) black = (18, 18, 22), white = (245, 245, 245);
        void Run(double from, double to, int count, Func<int, (byte R, byte G, byte B)> color, bool twoRows = false)
        {
            for (int index = 0; index < count; index++)
            {
                double start = from + (to - from) * index / count, end = from + (to - from) * (index + 1) / count;
                var first = color(index);
                segments.Add(new(start, end, 0, twoRows ? .5 : 1, first.R, first.G, first.B));
                if (twoRows)
                {
                    var second = color(index + 1);
                    segments.Add(new(start, end, .5, 1, second.R, second.G, second.B));
                }
            }
        }
        Run(0, .30, bars.Length, index => bars[index]);
        Run(.30, .42, 14, index => index % 2 == 0 ? black : white);
        Run(.42, .66, 5, index => ((byte)(20 + index * 56), (byte)(20 + index * 56), (byte)(24 + index * 56)));
        Run(.66, .84, 6, index => index % 2 == 0 ? black : white, twoRows: true);
        Run(.84, 1, 6, index => index % 2 == 0 ? black : white);
        return segments.ToArray();
    }
}
