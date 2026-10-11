namespace ProjectTabletop.App.Camera;

public readonly record struct CameraVideoMode(int Width, int Height, double FramesPerSecond);

/// <summary>Camera mode ordering independent of Windows capture, shared by selection and capability reporting.</summary>
public static class CameraFrameRatePolicy
{
    public static bool IsRequestedRate(int value) => value is 30 or 60 or 100;

    public static bool MatchesRate(double actual, int requested) =>
        double.IsFinite(actual) && Math.Abs(actual - requested) <= requested * .01;

    public static bool IsUsable(CameraVideoMode mode) =>
        mode.Width is >= 1280 and <= 3840 && mode.Height is >= 720 and <= 2160 &&
        double.IsFinite(mode.FramesPerSecond) && mode.FramesPerSecond is >= 20 and <= 101;

    public static IReadOnlyList<T> Order<T>(IEnumerable<T> candidates, Func<T, CameraVideoMode> mode, int requested)
    {
        if (!IsRequestedRate(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        // Rate takes priority over resolution: a 720p60 stream beats a 1080p30 stream
        // when 60 fps was requested. Avoid the allocation cost of 4K when 1080p is available.
        return candidates.Where(candidate => IsUsable(mode(candidate)))
            .OrderBy(candidate => RateClass(mode(candidate).FramesPerSecond, requested))
            .ThenBy(candidate => MatchesRate(mode(candidate).FramesPerSecond, requested)
                ? 0 : Math.Abs(mode(candidate).FramesPerSecond - requested))
            .ThenBy(candidate => mode(candidate).Width <= 1920 && mode(candidate).Height <= 1080 ? 0 : 1)
            .ThenBy(candidate => Math.Abs((long)mode(candidate).Width * mode(candidate).Height - 1920L * 1080))
            .ThenBy(candidate => Math.Abs(mode(candidate).FramesPerSecond - requested))
            .ToArray();
    }

    public static IReadOnlyList<int> SupportedRequestedRates(IEnumerable<CameraVideoMode> modes)
    {
        var usable = modes.Where(IsUsable).ToArray();
        return Array.AsReadOnly(new[] { 30, 60, 100 }
            .Where(rate => usable.Any(mode => MatchesRate(mode.FramesPerSecond, rate))).ToArray());
    }

    public static double CopyRateLimit(int requested, double actual)
    {
        if (!IsRequestedRate(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        if (!double.IsFinite(actual) || actual <= 0) throw new ArgumentOutOfRangeException(nameof(actual));
        // The same small clock-jitter allowance as the previous 31.25 copies/s at 30 fps.
        return Math.Min(requested, actual) * (25d / 24);
    }

    private static int RateClass(double actual, int requested) =>
        MatchesRate(actual, requested) ? 0 : actual < requested ? 1 : 2;
}

/// <summary>A bounded two-frame token budget. Idle time never creates a stale catch-up queue.</summary>
public sealed class CameraCopyRateBudget(double copiesPerSecond)
{
    private double _allowance = 2;
    private double? _lastSeconds;

    public double CopiesPerSecond { get; } = double.IsFinite(copiesPerSecond) && copiesPerSecond > 0
        ? copiesPerSecond : throw new ArgumentOutOfRangeException(nameof(copiesPerSecond));

    public bool TryTake(double monotonicSeconds, bool captureEveryFrame = false)
    {
        if (!double.IsFinite(monotonicSeconds)) throw new ArgumentOutOfRangeException(nameof(monotonicSeconds));
        if (_lastSeconds is { } previous)
            _allowance = Math.Min(2, _allowance + Math.Max(0, monotonicSeconds - previous) * CopiesPerSecond);
        _lastSeconds = monotonicSeconds;
        if (!captureEveryFrame && _allowance < 1) return false;
        // Calibration bypass must not leave a negative debt when normal capture resumes.
        _allowance = Math.Max(0, _allowance - 1);
        return true;
    }
}
