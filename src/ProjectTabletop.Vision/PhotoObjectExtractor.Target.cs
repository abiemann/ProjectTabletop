namespace ProjectTabletop.Vision;

public static partial class PhotoObjectExtractor
{
    /// <summary>
    /// Photograph current camera pixels through a previously acquired silhouette.
    /// Never resegments the projected white light. A moved, hidden or no longer
    /// contrasting object is refused, rather than copying stale background/hand.
    /// </summary>
    public static PhotoHandCutout? ExtractTarget(int width, int height, int stride, byte[] bgra,
        HandDetection shutter, IReadOnlyList<double> cameraToBoard, PhotoObjectTarget target,
        out string? failure, IReadOnlyList<HandDetection>? otherHands = null)
    {
        ArgumentNullException.ThrowIfNull(shutter);
        ArgumentNullException.ThrowIfNull(target);
        if (!PhotoObjectLocator.TryRectify(width, height, stride, bgra, cameraToBoard,
            out var photo, out var h, out failure)) return null;
        HandDetection[] hands = [shutter, .. otherHands ?? []];
        if (PhotoObjectLocator.ObserveRectified(photo, h, target, hands, out failure) != PhotoObjectTargetState.Present)
            return null;
        byte[] result = new byte[target.Width * target.Height * 4];
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                int destination = y * target.Width + x;
                byte alpha = target.Alpha[destination];
                if (alpha == 0) continue;
                int source = (target.Top + y) * PhotoHandCutout.BoardPixels + target.Left + x;
                Array.Copy(photo, source * 4, result, destination * 4, 3);
                result[destination * 4 + 3] = alpha;
            }
        failure = null;
        return new(target.Width, target.Height, result,
            new((target.Width - 1) / 2.0, (target.Height - 1) / 2.0), new(0, -1))
            { BoardOrigin = new(target.Left, target.Top) };
    }
}
