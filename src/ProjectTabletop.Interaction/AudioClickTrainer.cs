namespace ProjectTabletop.Interaction;

public enum AudioClickLearningStage
{
    Idle, QuietBaseline, LearningClicks, ValidationClicks, QuietValidation, Complete, Failed, Cancelled
}

public readonly record struct AudioClickLearningSnapshot(AudioClickLearningStage Stage, string Message,
    int CollectedClicks, int ValidatedClicks, double Level, double Noise, bool CanSave, double Progress0To1)
{
    public int RequiredClicks => AudioClickTrainer.RequiredClicks;
    public int RequiredValidation => AudioClickTrainer.RequiredValidation;
    public int CompletedValidationAttempts { get; init; }
    public int ValidationAttempt { get; init; }
    public bool Collecting { get; init; }
    public bool CanRetryValidation { get; init; }
}

public readonly record struct AudioClickTrainingDiagnostics(AudioClickDetectionDiagnostics Detection,
    long WeakRejected, long ClippedRejected, long InconsistentRejected, long ValidationRejected,
    AudioClickTransient? LastAcceptedExample);

/// <summary>
/// Guided, device-specific click learning. Keeps only ten numerical feature sets,
/// never PCM. The host serializes Process and controls, and cancels on device changes.
/// Held-out clicks and a quiet negative check are required before Profile is exposed.
/// </summary>
public sealed class AudioClickTrainer
{
    public const int RequiredClicks = 10, RequiredValidation = 5;
    private const double QuietSeconds = 3;
    private const double ValidationReadySeconds = 1, ValidationClickSeconds = 3, ValidationConfirmationSeconds = .15;
    private static readonly string[] AimDirections = ["upper left", "upper right", "centre", "lower left", "lower right"];
    private readonly AudioClickDetector _detector = new(learning: true);
    private readonly AudioClickFeatures[] _clicks = new AudioClickFeatures[RequiredClicks];
    private AudioClickLearningStage _stage;
    private string _message = "Choose a microphone, then start click learning.";
    private AudioClickProfile? _profile;
    private int _collected, _validated, _rejected, _sampleRate, _channels;
    private double _stageSeconds, _noise, _loudSeconds;
    private DateTimeOffset _lastFrameEnd;
    private DateTimeOffset _meaningfulFeedbackThrough;
    private long _weakRejected, _clippedRejected, _inconsistentRejected, _validationRejected;
    private AudioClickTransient? _lastAcceptedExample;
    private AudioClickDetectionDiagnostics _stoppedDetection;
    private bool _hasStoppedDiagnostics;
    private int _validationAttempts;
    private long _observedTimeouts;
    private bool _validationCollecting, _canRetryValidation;
    private DateTimeOffset _validationWindowStart;
    private string _lastValidationFailure = "";

    public string? DeviceId { get; private set; }
    public bool TraceEnabled => _detector.TraceEnabled;
    public void SetTraceEnabled(bool enabled) => _detector.SetTraceEnabled(enabled);
    public AudioClickTraceSample[] CopyTrace() => _detector.CopyTrace();
    public AudioClickProfile? Profile => _stage == AudioClickLearningStage.Complete ? _profile : null;
    public AudioClickTrainingDiagnostics Diagnostics => new(
        _hasStoppedDiagnostics ? _stoppedDetection : _detector.Analysis,
        _weakRejected, _clippedRejected, _inconsistentRejected, _validationRejected, _lastAcceptedExample);
    public AudioClickLearningSnapshot Snapshot => new(_stage, _message, _collected, _validated,
        _detector.Diagnostics.Peak, _noise, _stage == AudioClickLearningStage.Complete,
        _stage switch
        {
            AudioClickLearningStage.QuietBaseline or AudioClickLearningStage.QuietValidation => Math.Clamp(_stageSeconds / QuietSeconds, 0, 1),
            AudioClickLearningStage.LearningClicks => (double)_collected / RequiredClicks,
            AudioClickLearningStage.ValidationClicks => Math.Clamp(_stageSeconds /
                (_validationCollecting ? ValidationClickSeconds : ValidationReadySeconds), 0, 1),
            AudioClickLearningStage.Complete => 1,
            _ => 0
        })
    {
        CompletedValidationAttempts = _validationAttempts,
        ValidationAttempt = Math.Min(_validationAttempts + 1, RequiredValidation),
        Collecting = _stage == AudioClickLearningStage.LearningClicks ||
            _stage == AudioClickLearningStage.ValidationClicks && _validationCollecting && _stageSeconds < ValidationClickSeconds,
        CanRetryValidation = _stage == AudioClickLearningStage.Failed && _canRetryValidation
    };

    public void Start(string actualDeviceId)
    {
        if (string.IsNullOrWhiteSpace(actualDeviceId))
            throw new ArgumentException("Select an available microphone before learning.", nameof(actualDeviceId));
        DeviceId = actualDeviceId;
        _detector.Reset();
        Array.Clear(_clicks);
        _profile = null;
        _collected = _validated = _rejected = _sampleRate = _channels = 0;
        _stageSeconds = _noise = _loudSeconds = 0;
        _lastFrameEnd = _meaningfulFeedbackThrough = default;
        _weakRejected = _clippedRejected = _inconsistentRejected = _validationRejected = 0;
        _lastAcceptedExample = null;
        _stoppedDetection = default;
        _hasStoppedDiagnostics = false;
        _validationAttempts = 0;
        _observedTimeouts = 0;
        _validationCollecting = _canRetryValidation = false;
        _validationWindowStart = default;
        _lastValidationFailure = "";
        SetStage(AudioClickLearningStage.QuietBaseline, "Keep quiet while the room noise is measured.");
    }

    public void Cancel()
    {
        StopAnalysis();
        _profile = null;
        _canRetryValidation = false;
        SetStage(AudioClickLearningStage.Cancelled, "Click learning cancelled. Your saved sound is unchanged.");
    }

    public void DeviceUnavailable() => Fail("Microphone changed or disconnected. Reconnect it and choose Retry.");

    public bool RetryValidation()
    {
        if (!_canRetryValidation || _stage != AudioClickLearningStage.Failed || _profile is null) return false;
        _detector.Reset();
        _hasStoppedDiagnostics = false;
        _stoppedDetection = default;
        _observedTimeouts = 0;
        _lastFrameEnd = default;
        BeginValidation();
        return true;
    }

    public void Process(ReadOnlySpan<float> samples, int sampleRate, int channels, DateTimeOffset frameStartUtc)
    {
        if (_stage is AudioClickLearningStage.Idle or AudioClickLearningStage.Complete or
            AudioClickLearningStage.Failed or AudioClickLearningStage.Cancelled) return;
        if (sampleRate < 8000 || channels is < 1 or > 32 || samples.Length % channels != 0)
            throw new ArgumentException("Learning requires complete, normalized PCM sample frames.");
        if (samples.IsEmpty) return;
        double seconds = (double)samples.Length / (sampleRate * channels);
        if (seconds > .1 || (_lastFrameEnd != default &&
            ((frameStartUtc - _lastFrameEnd).Duration() > TimeSpan.FromMilliseconds(150) ||
             sampleRate != _sampleRate || channels != _channels)))
        {
            Fail("Microphone audio was interrupted. Keep the microphone connected and choose Retry.");
            return;
        }
        _sampleRate = sampleRate;
        _channels = channels;
        _lastFrameEnd = frameStartUtc.AddSeconds(seconds);
        _detector.Process(samples, sampleRate, channels, frameStartUtc);
        var levels = _detector.Diagnostics;
        var transient = _detector.LastTransient;
        var analysis = _detector.Analysis;
        AudioClickTransient? timeout = analysis.TimedOutCandidates > _observedTimeouts ? analysis.LastTimedOutCandidate : null;
        _observedTimeouts = analysis.TimedOutCandidates;

        switch (_stage)
        {
            case AudioClickLearningStage.QuietBaseline:
                _noise = levels.NoiseRms;
                if (levels.Ready && _noise > .08)
                {
                    Fail("The room is too loud. Reduce background sound or move closer to the microphone, then Retry.");
                    return;
                }
                if (levels.Candidate || transient is not null)
                {
                    _stageSeconds = 0;
                    _message = "Keep quiet for three seconds before making your clicks.";
                }
                else _stageSeconds += seconds;
                if (_stageSeconds >= QuietSeconds && levels.Ready)
                    SetStage(AudioClickLearningStage.LearningClicks, LearningInstruction());
                break;

            case AudioClickLearningStage.LearningClicks:
                if (transient is { } learning) Collect(learning);
                else if (timeout is not null)
                    _message = "That sound lasted too long to recognize. Pause, then try the same click again.";
                break;

            case AudioClickLearningStage.ValidationClicks:
                ProcessValidation(seconds, transient, timeout);
                break;

            case AudioClickLearningStage.QuietValidation:
                if (transient is { } quiet && _profile!.Matches(quiet.Features))
                {
                    Fail("A click was detected during the quiet check. Remove background clicking and choose Retry.");
                    return;
                }
                _loudSeconds = levels.Rms > Math.Max(.025, _noise * 2.5) ? _loudSeconds + seconds : 0;
                if (_loudSeconds >= .2)
                {
                    Fail("The room sound changed during the quiet check. Keep the room quiet and choose Retry.");
                    return;
                }
                // A pending sound must finish before the quiet check can pass.
                _stageSeconds += seconds;
                if (_stageSeconds >= QuietSeconds && !levels.Candidate && _loudSeconds == 0)
                    SetStage(AudioClickLearningStage.Complete, "Checks passed. Save this click sound for the selected microphone.");
                break;
        }
    }

    private void Collect(AudioClickTransient transient)
    {
        var features = transient.Features;
        if (features.Peak >= .999 || features.Rms < Math.Max(.0025, _noise * 5))
        {
            if (features.Peak >= .999) _clippedRejected++;
            else _weakRejected++;
            if (features.Peak < .999 && _lastFrameEnd <= _meaningfulFeedbackThrough) return;
            _message = features.Peak >= .999
                ? "That click clipped. Move a little farther from the microphone and try again."
                : "That click was too close to the room noise. Move closer or reduce background sound.";
            if (features.Peak >= .999) _meaningfulFeedbackThrough = _lastFrameEnd.AddSeconds(1.5);
            return;
        }
        if (_collected > 0 && !ConsistentWithExamples(features))
        {
            _inconsistentRejected++;
            _message = "That sound was different from the collected clicks. Use the same click device while aiming naturally.";
            _meaningfulFeedbackThrough = _lastFrameEnd.AddSeconds(1.5);
            if (++_rejected >= 3)
                Fail("The clicks vary too much. Choose one clear click sound and Retry.");
            return;
        }
        _clicks[_collected++] = features;
        _lastAcceptedExample = transient;
        _rejected = 0;
        _message = "Click captured. " + LearningInstruction();
        _meaningfulFeedbackThrough = _lastFrameEnd.AddSeconds(1.5);
        if (_collected != RequiredClicks) return;
        _profile = BuildProfile();
        _profile.Validate();
        BeginValidation();
    }

    private string LearningInstruction() => $"Aim toward the {AimDirections[Math.Min(_collected / 2, AimDirections.Length - 1)]}. " +
        "Make one click at a time, pausing briefly. Use your normal playing position and vary your aim as prompted.";

    private void BeginValidation()
    {
        _validated = _validationAttempts = 0;
        _validationCollecting = _canRetryValidation = false;
        _lastValidationFailure = "";
        SetStage(AudioClickLearningStage.ValidationClicks,
            $"Learning captured. Wait for green, then make exactly one click per test. There are {RequiredValidation} tests.");
    }

    private void ProcessValidation(double seconds, AudioClickTransient? transient, AudioClickTransient? timeout)
    {
        _stageSeconds += seconds;
        if (!_validationCollecting)
        {
            if (_stageSeconds < ValidationReadySeconds) return;
            _validationCollecting = true;
            _stageSeconds = 0;
            _validationWindowStart = _lastFrameEnd;
            _message = $"Aim toward the {AimDirections[_validationAttempts]}. Make ONE click now, then wait for the next green prompt.";
            return;
        }

        // Each prompted attempt has one result. A later recognized sound must not
        // conceal an earlier miss, timeout or mismatched sound in the same test.
        // Small room sounds below both runtime loudness floors are not the
        // prompted click. They cannot consume the window, nor count as success;
        // a window containing only those sounds still expires as a missed test.
        AudioClickTransient? qualified = transient is { } detected && InWindow(detected.At) &&
            !_profile!.IsBelowBothMinimumLevels(detected.Features) ? detected : null;
        AudioClickTransient? expired = timeout is { } timedOut && InWindow(timedOut.At) &&
            !_profile!.IsBelowBothMinimumLevels(timedOut.Features) ? timedOut : null;
        // A permitted larger PCM packet can contain a timeout and a later
        // completed sound. Preserve the first eligible event in capture order.
        if (expired is { } failure && (qualified is null || failure.At <= qualified.Value.At))
            FinishValidationAttempt(false, "The sound lasted too long to recognize as a click.");
        else if (qualified is { } sample)
        {
            bool matched = _profile!.Matches(sample.Features);
            FinishValidationAttempt(matched, matched ? "Click recognized." : "The sound did not match the learned click.");
        }
        else if (_stageSeconds >= ValidationClickSeconds + ValidationConfirmationSeconds)
            FinishValidationAttempt(false, "No click was recognized during the green prompt.");

        bool InWindow(DateTimeOffset onset)
        {
            // A PCM boundary converted through seconds can differ by one 100 ns
            // tick. Ignore only sub-microsecond conversion roundoff at the prompt.
            double elapsedMs = Math.Round((onset - _validationWindowStart).TotalMilliseconds, 3);
            return elapsedMs >= 0 && elapsedMs < ValidationClickSeconds * 1000;
        }
    }

    private void FinishValidationAttempt(bool matched, string reason)
    {
        _validationAttempts++;
        if (matched) _validated++;
        else
        {
            _validationRejected++;
            _lastValidationFailure = reason;
        }
        _validationCollecting = false;
        if (_validationAttempts < RequiredValidation)
            SetStage(AudioClickLearningStage.ValidationClicks,
                $"Test {_validationAttempts}: {reason} {_validated}/{_validationAttempts} recognized. Wait for green before clicking again.");
        else if (_validated == RequiredValidation)
            SetStage(AudioClickLearningStage.QuietValidation, "All five test clicks recognized. Now keep quiet for three seconds.");
        else
        {
            StopAnalysis();
            _canRetryValidation = true;
            SetStage(AudioClickLearningStage.Failed,
                $"{_validated}/{RequiredValidation} test clicks recognized. {_lastValidationFailure} " +
                "Retry the five tests, or Cancel and start learning again if misses continue. Your saved profile is unchanged.");
        }
    }

    private AudioClickProfile BuildProfile()
    {
        Span<double> peak = stackalloc double[RequiredClicks], rms = stackalloc double[RequiredClicks];
        Span<double> duration = stackalloc double[RequiredClicks], high = stackalloc double[RequiredClicks];
        for (int i = 0; i < RequiredClicks; i++)
        {
            peak[i] = _clicks[i].Peak;
            rms[i] = _clicks[i].Rms;
            duration[i] = _clicks[i].DurationMs;
            high[i] = _clicks[i].HighFrequencyRatio;
        }
        peak.Sort(); rms.Sort(); duration.Sort(); high.Sort();
        double middlePeak = Median(peak), middleRms = Median(rms);
        double middleDuration = Median(duration), middleHigh = Median(high);
        return new AudioClickProfile
        {
            Version = 2,
            TrainingNoiseRms = _noise,
            MinimumPeak = Math.Max(.004, Math.Min(peak[0] * .7, middlePeak * .5)),
            MaximumPeak = Math.Min(1, Math.Max(peak[^1] * 1.35, middlePeak * 1.8)),
            MinimumRms = Math.Max(.001, Math.Min(rms[0] * .7, middleRms * .5)),
            MaximumRms = Math.Min(1, Math.Max(rms[^1] * 1.35, middleRms * 1.8)),
            MinimumDurationMs = Math.Max(1, Math.Min(duration[0] - 5, middleDuration * .5)),
            MaximumDurationMs = Math.Min(AudioClickProfile.MaximumLearnedDurationMs, Math.Max(duration[^1] + 10, middleDuration * 1.8)),
            MinimumHighFrequencyRatio = Math.Max(0, Math.Min(high[0] - .08, middleHigh - .12)),
            MaximumHighFrequencyRatio = Math.Min(1, Math.Max(high[^1] + .08, middleHigh + .12))
        };
    }

    private static double Median(ReadOnlySpan<double> sorted) =>
        (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;

    private bool ConsistentWithExamples(AudioClickFeatures next)
    {
        int matches = 0;
        for (int i = 0; i < _collected; i++) if (Consistent(_clicks[i], next)) matches++;
        // A majority prevents an unusual first sample or progressive drift from
        // defining the whole profile. Level variation is expected as aim changes.
        return matches > _collected / 2;
    }

    private static bool Consistent(AudioClickFeatures first, AudioClickFeatures next) =>
        next.Peak / first.Peak is >= .35 and <= 2.8 &&
        next.Rms / first.Rms is >= .35 and <= 2.8 &&
        Math.Abs(next.DurationMs - first.DurationMs) <= Math.Max(15, Math.Max(first.DurationMs, next.DurationMs) * .65) &&
        Math.Abs(next.HighFrequencyRatio - first.HighFrequencyRatio) <= .22;

    private void SetStage(AudioClickLearningStage stage, string message)
    {
        _stage = stage;
        _message = message;
        _stageSeconds = _loudSeconds = 0;
    }

    private void Fail(string message)
    {
        StopAnalysis();
        _profile = null;
        _canRetryValidation = false;
        SetStage(AudioClickLearningStage.Failed, message);
    }

    private void StopAnalysis()
    {
        if (!_hasStoppedDiagnostics) _stoppedDetection = _detector.Analysis;
        _hasStoppedDiagnostics = true;
        _detector.Reset();
    }
}
