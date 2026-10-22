namespace ProjectTabletop.Interaction;

public enum AudioClickLearningStage
{
    Idle, QuietBaseline, LearningClicks, ValidationClicks, QuietValidation, Complete, Failed, Cancelled
}

public readonly record struct AudioClickLearningSnapshot(AudioClickLearningStage Stage, string Message,
    int CollectedClicks, int ValidatedClicks, double Level, double Noise, bool CanSave, double Progress0To1);

public readonly record struct AudioClickTrainingDiagnostics(AudioClickDetectionDiagnostics Detection,
    long WeakRejected, long ClippedRejected, long InconsistentRejected, long ValidationRejected,
    AudioClickTransient? LastAcceptedExample);

/// <summary>
/// Guided, device-specific click learning. Keeps only six numerical feature sets,
/// never PCM. The host serializes Process and controls, and cancels on device changes.
/// Held-out clicks and a quiet negative check are required before Profile is exposed.
/// </summary>
public sealed class AudioClickTrainer
{
    private const int RequiredClicks = 6, RequiredValidation = 3;
    private const double QuietSeconds = 3;
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

    public string? DeviceId { get; private set; }
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
            AudioClickLearningStage.ValidationClicks => (double)_validated / RequiredValidation,
            AudioClickLearningStage.Complete => 1,
            _ => 0
        });

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
        SetStage(AudioClickLearningStage.QuietBaseline, "Keep quiet while the room noise is measured.");
    }

    public void Cancel()
    {
        StopAnalysis();
        _profile = null;
        SetStage(AudioClickLearningStage.Cancelled, "Click learning cancelled. Your saved sound is unchanged.");
    }

    public void DeviceUnavailable() => Fail("Microphone changed or disconnected. Reconnect it and choose Retry.");

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
                    SetStage(AudioClickLearningStage.LearningClicks, "Make six clear clicks, one at a time, with a short pause between them.");
                break;

            case AudioClickLearningStage.LearningClicks:
                if (transient is { } learning) Collect(learning);
                break;

            case AudioClickLearningStage.ValidationClicks:
                if (transient is { } validation)
                {
                    if (_profile!.Matches(validation.Features))
                    {
                        _validated++;
                        _rejected = 0;
                        _message = "Click recognized. Repeat the same sound with a short pause.";
                        if (_validated == RequiredValidation)
                            SetStage(AudioClickLearningStage.QuietValidation, "Now keep quiet for three seconds to check for accidental shots.");
                    }
                    else
                    {
                        _validationRejected++;
                        // The permissive capture pass also hears small follow-up sounds.
                        // Runtime rejects these below both learned loudness bounds, so
                        // they must not undo a correctly recognized validation click.
                        if (validation.Features.Peak < _profile.MinimumPeak &&
                            validation.Features.Rms < _profile.MinimumRms) break;
                        _validated = 0;
                        _message = "That sound did not match. Try three more clicks from the same position.";
                        if (++_rejected >= 3)
                            Fail("The clicks are not consistent enough. Keep the same position and choose Retry.");
                    }
                }
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
        if (_collected > 0 && !Consistent(_clicks[0], features))
        {
            _inconsistentRejected++;
            _message = "Use the same click sound and position. That different sound was ignored.";
            _meaningfulFeedbackThrough = _lastFrameEnd.AddSeconds(1.5);
            if (++_rejected >= 3)
                Fail("The clicks vary too much. Choose one clear click sound and Retry.");
            return;
        }
        _clicks[_collected++] = features;
        _lastAcceptedExample = transient;
        _rejected = 0;
        _message = "Click captured. Repeat the same sound with a short pause.";
        _meaningfulFeedbackThrough = _lastFrameEnd.AddSeconds(1.5);
        if (_collected != RequiredClicks) return;
        _profile = BuildProfile();
        _profile.Validate();
        SetStage(AudioClickLearningStage.ValidationClicks, "Make three more clicks to test the learned sound.");
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
        double middlePeak = (peak[2] + peak[3]) / 2, middleRms = (rms[2] + rms[3]) / 2;
        double middleDuration = (duration[2] + duration[3]) / 2, middleHigh = (high[2] + high[3]) / 2;
        return new AudioClickProfile
        {
            TrainingNoiseRms = _noise,
            MinimumPeak = Math.Max(.004, Math.Min(peak[0] * .7, middlePeak * .5)),
            MaximumPeak = Math.Min(1, Math.Max(peak[5] * 1.35, middlePeak * 1.8)),
            MinimumRms = Math.Max(.001, Math.Min(rms[0] * .7, middleRms * .5)),
            MaximumRms = Math.Min(1, Math.Max(rms[5] * 1.35, middleRms * 1.8)),
            MinimumDurationMs = Math.Max(1, Math.Min(duration[0] - 5, middleDuration * .5)),
            MaximumDurationMs = Math.Min(120, Math.Max(duration[5] + 10, middleDuration * 1.8)),
            MinimumHighFrequencyRatio = Math.Max(0, Math.Min(high[0] - .08, middleHigh - .12)),
            MaximumHighFrequencyRatio = Math.Min(1, Math.Max(high[5] + .08, middleHigh + .12))
        };
    }

    private static bool Consistent(AudioClickFeatures first, AudioClickFeatures next) =>
        next.Peak / first.Peak is >= .35 and <= 2.8 &&
        next.Rms / first.Rms is >= .35 and <= 2.8 &&
        Math.Abs(next.DurationMs - first.DurationMs) <= Math.Max(10, first.DurationMs * .9) &&
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
        SetStage(AudioClickLearningStage.Failed, message);
    }

    private void StopAnalysis()
    {
        if (!_hasStoppedDiagnostics) _stoppedDetection = _detector.Analysis;
        _hasStoppedDiagnostics = true;
        _detector.Reset();
    }
}
