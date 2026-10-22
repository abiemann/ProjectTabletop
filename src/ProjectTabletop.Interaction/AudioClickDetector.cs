namespace ProjectTabletop.Interaction;

public readonly record struct AudioClickDetectionDiagnostics(long CompletedTransients, long TimedOutCandidates,
    long RejectedByProfile, AudioClickTransient? LastCompletedTransient, AudioClickTransient? LastTimedOutCandidate);

/// <summary>
/// Detects short, abrupt microphone transients. No samples are retained, and a loud
/// sound must fall away promptly before its original onset is reported as a click.
/// An optional learned profile also matches the transient's loudness, duration and
/// frequency character. Without a profile the original transient rules are used.
/// </summary>
public sealed class AudioClickDetector
{
    private const double WindowSeconds = .005;
    private const double WarmupSeconds = .6;
    private int _sampleRate, _channels, _windowFrames, _frames;
    private double _energy, _peak, _elapsed, _noiseRms, _noisePeak, _previousPeak;
    private double _candidateRms, _quietSeconds, _candidateFallSeconds;
    private double _highEnergy, _highPassAlpha;
    private double _candidatePeak, _candidateEnergy, _candidateHighEnergy, _candidateFrames, _candidateDuration;
    private readonly double[] _previousSamples = new double[32], _highPassSamples = new double[32];
    private readonly bool _learning;
    private AudioClickProfile? _profile;
    private long _completedTransients, _timedOutCandidates, _rejectedByProfile;
    private AudioClickTransient? _lastCompletedTransient, _lastTimedOutCandidate;
    private DateTimeOffset _windowStart, _lastFrameEnd, _candidateAt, _lastClickAt;
    private bool _candidate, _armed;

    public readonly record struct Levels(double Peak, double Rms, double NoiseRms,
        double PeakThreshold, bool Ready, bool Candidate);

    public Levels Diagnostics { get; private set; }
    /// <summary>The completed transient, including one rejected by a learned profile. Cleared each Process call.</summary>
    public AudioClickTransient? LastTransient { get; private set; }
    public AudioClickDetectionDiagnostics Analysis => new(_completedTransients, _timedOutCandidates,
        _rejectedByProfile, _lastCompletedTransient, _lastTimedOutCandidate);

    public AudioClickDetector() { }
    internal AudioClickDetector(bool learning) => _learning = learning;

    public void SetProfile(AudioClickProfile? profile)
    {
        profile?.Validate();
        _profile = profile;
        Reset();
    }

    public void Reset()
    {
        _sampleRate = _channels = _windowFrames = _frames = 0;
        _energy = _peak = _elapsed = _noiseRms = _noisePeak = _previousPeak = 0;
        _candidateRms = _quietSeconds = _candidateFallSeconds = 0;
        _highEnergy = _highPassAlpha = 0;
        _candidatePeak = _candidateEnergy = _candidateHighEnergy = _candidateFrames = _candidateDuration = 0;
        Array.Clear(_previousSamples);
        Array.Clear(_highPassSamples);
        _windowStart = _lastFrameEnd = _candidateAt = _lastClickAt = default;
        _candidate = _armed = false;
        Diagnostics = default;
        LastTransient = null;
        _completedTransients = _timedOutCandidates = _rejectedByProfile = 0;
        _lastCompletedTransient = _lastTimedOutCandidate = null;
    }

    /// <param name="samples">Interleaved, normalized floating-point PCM.</param>
    /// <param name="frameStartUtc">Estimated capture time of the first sample.</param>
    /// <returns>The onset of at most one validated click in this frame.</returns>
    public DateTimeOffset? Process(ReadOnlySpan<float> samples, int sampleRate, int channels,
        DateTimeOffset frameStartUtc)
    {
        LastTransient = null;
        if (sampleRate < 8000 || channels is < 1 or > 32)
            throw new ArgumentOutOfRangeException(sampleRate < 8000 ? nameof(sampleRate) : nameof(channels));
        if (samples.Length % channels != 0)
            throw new ArgumentException("PCM must contain complete interleaved sample frames.", nameof(samples));
        if (samples.IsEmpty) return null;
        if (_sampleRate != sampleRate || _channels != channels ||
            (_lastFrameEnd != default &&
             (frameStartUtc - _lastFrameEnd).Duration() > TimeSpan.FromMilliseconds(150)))
        {
            Reset();
            _sampleRate = sampleRate;
            _channels = channels;
            _windowFrames = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
            _highPassAlpha = Math.Exp(-2 * Math.PI * 2000 / sampleRate);
        }

        DateTimeOffset? click = null;
        int frameCount = samples.Length / channels;
        for (int frame = 0; frame < frameCount; frame++)
        {
            if (_frames == 0) _windowStart = frameStartUtc.AddSeconds((double)frame / sampleRate);
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = samples[frame * channels + channel];
                double value = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
                double magnitude = Math.Abs(value);
                _peak = Math.Max(_peak, magnitude);
                _energy += magnitude * magnitude;
                double highPass = _highPassAlpha * (_highPassSamples[channel] + value - _previousSamples[channel]);
                _previousSamples[channel] = value;
                _highPassSamples[channel] = highPass;
                _highEnergy += highPass * highPass;
            }
            if (++_frames < _windowFrames) continue;
            double rms = Math.Sqrt(_energy / (_frames * channels));
            DateTimeOffset? detected = ProcessWindow(_peak, rms, _energy, _highEnergy, _frames * channels, _windowStart,
                (double)_frames / sampleRate);
            click ??= detected;
            _frames = 0;
            _energy = _peak = _highEnergy = 0;
        }
        _lastFrameEnd = frameStartUtc.AddSeconds((double)frameCount / sampleRate);
        return click;
    }

    private DateTimeOffset? ProcessWindow(double peak, double rms, double energy, double highEnergy,
        int sampleCount, DateTimeOffset at, double seconds)
    {
        _elapsed += seconds;
        bool warming = _elapsed < WarmupSeconds;
        bool adapted = _learning || _profile is not null;
        // Learning and trained runtime must segment the same complete sound. Raising
        // the onset floor from its learned peak would lose quieter leading portions
        // and change the duration/frequency features of an otherwise identical click.
        double threshold = Math.Max(adapted ? .004 : .12, _noisePeak * 3);
        DateTimeOffset? click = null;
        bool completed = false;

        if (warming)
        {
            _noiseRms += (rms - _noiseRms) * .08;
            _noisePeak += (peak - _noisePeak) * .08;
            _armed = false;
            _quietSeconds = 0;
        }
        else
        {
            bool quiet = rms < Math.Max(adapted ? .002 : .02, _noiseRms * 2) &&
                peak < Math.Max(adapted ? .006 : .07, _noisePeak * 2);
            _quietSeconds = quiet ? _quietSeconds + seconds : 0;
            if (_candidate)
            {
                _candidateRms = Math.Max(_candidateRms, rms);
                double age = (at - _candidateAt).TotalSeconds;
                // A sharp click can leave a much quieter room/microphone tail above
                // ambient. For learning and trained playback, a sustained relative
                // fall from a clearly dominant peak can end the click promptly.
                // Use the same segmentation in both paths; do not alter the default detector.
                bool relativeFall = adapted &&
                    _candidatePeak >= Math.Max(.02, _noisePeak * 8) &&
                    _candidateRms >= Math.Max(.004, _noiseRms * 8) &&
                    peak <= _candidatePeak * .2 && rms <= _candidateRms * .18;
                _candidateFallSeconds = quiet || relativeFall ? _candidateFallSeconds + seconds : 0;
                if (!quiet && !relativeFall) AccumulateCandidate(peak, energy, highEnergy, sampleCount, age + seconds);
                if (age > .12)
                {
                    // Speech, music and a sustained loud onset do not become a shot.
                    _timedOutCandidates++;
                    // This is the observed age when analysis stopped, not a claim
                    // that the sound ended here. Only numerical summaries survive.
                    _lastTimedOutCandidate = new(_candidateAt, CandidateFeatures((age + seconds) * 1000));
                    _candidate = false;
                }
                else if ((adapted ? _candidateFallSeconds : _quietSeconds) >= .01 &&
                    rms < Math.Max(adapted ? .001 : .008, _candidateRms * .3))
                {
                    var features = CandidateFeatures(Math.Min(120, _candidateDuration * 1000));
                    LastTransient = new(_candidateAt, features);
                    _lastCompletedTransient = LastTransient;
                    _completedTransients++;
                    completed = true;
                    if (_profile is null || _profile.Matches(features)) click = _candidateAt;
                    else _rejectedByProfile++;
                    // Even a rejected transient must rearm, avoiding its echo becoming another candidate.
                    _lastClickAt = _candidateAt;
                    _candidate = false;
                }
            }
            else if (_armed && peak >= threshold && peak > _previousPeak * 1.8 &&
                rms >= Math.Max(adapted ? .001 : .012, _noiseRms * 3))
            {
                _candidate = true;
                _armed = false;
                _candidateAt = at;
                _candidateRms = rms;
                _candidateFallSeconds = 0;
                _candidatePeak = _candidateEnergy = _candidateHighEnergy = _candidateFrames = _candidateDuration = 0;
                AccumulateCandidate(peak, energy, highEnergy, sampleCount, seconds);
                _quietSeconds = 0;
            }
            else if (!_armed && _quietSeconds >= .06 &&
                (_lastClickAt == default || at - _lastClickAt >= TimeSpan.FromMilliseconds(250)))
            {
                _armed = true;
            }

            // Do not teach the noise floor that the click itself is ambient noise.
            if (!_candidate && !completed)
            {
                _noiseRms += (rms - _noiseRms) * .005;
                _noisePeak += (peak - _noisePeak) * .005;
            }
        }
        _previousPeak = peak;
        Diagnostics = new Levels(peak, rms, _noiseRms, threshold, !warming, _candidate);
        return click;
    }

    private void AccumulateCandidate(double peak, double energy, double highEnergy, int samples, double duration)
    {
        _candidatePeak = Math.Max(_candidatePeak, peak);
        _candidateEnergy += energy;
        _candidateHighEnergy += highEnergy;
        _candidateFrames += samples;
        _candidateDuration = duration;
    }

    private AudioClickFeatures CandidateFeatures(double durationMs) => new(_candidatePeak,
        Math.Sqrt(_candidateEnergy / Math.Max(1, _candidateFrames)), durationMs,
        Math.Clamp(_candidateHighEnergy / Math.Max(double.Epsilon, _candidateEnergy), 0, 1));
}
