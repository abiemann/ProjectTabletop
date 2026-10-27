namespace ProjectTabletop.Interaction;

/// <summary>One numeric analysis window, never microphone samples or playable audio.</summary>
public readonly record struct AudioClickTraceSample(DateTimeOffset At, double Peak, double Rms, bool Candidate);

/// <summary>
/// Optional bounded diagnostic history. The owner serializes reads and writes.
/// The audio path allocates nothing; only an explicit CopySamples request allocates.
/// </summary>
public sealed class AudioClickTrace
{
    public const int Capacity = 256;
    private readonly AudioClickTraceSample[] _samples = new AudioClickTraceSample[Capacity];
    private int _next, _count;

    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Clear();
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _next = _count = 0;
    }

    public void Add(DateTimeOffset at, double peak, double rms, bool candidate)
    {
        if (!Enabled) return;
        _samples[_next] = new(at, peak, rms, candidate);
        _next = (_next + 1) % Capacity;
        _count = Math.Min(Capacity, _count + 1);
    }

    public AudioClickTraceSample[] CopySamples()
    {
        if (_count == 0) return [];
        var result = new AudioClickTraceSample[_count];
        int first = (_next - _count + Capacity) % Capacity;
        int leading = Math.Min(_count, Capacity - first);
        Array.Copy(_samples, first, result, 0, leading);
        if (leading < _count) Array.Copy(_samples, 0, result, leading, _count - leading);
        return result;
    }
}
