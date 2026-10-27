using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickTrainerTests
{
    [Fact]
    public void TrainingRequiresTenClicksFivePromptedHeldOutAndQuietBeforeExposingProfile()
    {
        var stream = new TrainingStream();
        Assert.Null(stream.Trainer.Profile);
        stream.Quiet(3.1);
        Assert.Equal(AudioClickLearningStage.LearningClicks, stream.Trainer.Snapshot.Stage);
        for (int i = 0; i < AudioClickTrainer.RequiredClicks - 1; i++) stream.Click();
        Assert.Equal(9, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Null(stream.Trainer.Profile);
        stream.Click();
        Assert.Equal(AudioClickLearningStage.ValidationClicks, stream.Trainer.Snapshot.Stage);
        Assert.False(stream.Trainer.Snapshot.Collecting);
        for (int i = 0; i < AudioClickTrainer.RequiredValidation - 1; i++) ValidateClick(stream);
        Assert.Equal(4, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Equal(4, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.False(stream.Trainer.Snapshot.CanSave);
        ValidateClick(stream);
        Assert.Equal(AudioClickLearningStage.QuietValidation, stream.Trainer.Snapshot.Stage);
        Assert.Null(stream.Trainer.Profile);
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.Equal("physical-microphone-id", stream.Trainer.DeviceId);
        Assert.Equal(AudioClickLearningStage.Complete, stream.Trainer.Snapshot.Stage);
        stream.Trainer.Profile!.Validate();
        Assert.Equal(2, stream.Trainer.Profile.Version);
    }

    [Fact]
    public void LearnedQuietClickWorksAtRuntimeButWrongFrequencyAndDurationDoNot()
    {
        var stream = CompleteTraining();
        var detector = new AudioClickDetector();
        detector.SetProfile(stream.Trainer.Profile);
        var runtime = new DetectorStream(detector);
        runtime.Quiet(1);
        runtime.Click(.04, 5000);
        Assert.Equal(1, runtime.Shots);
        runtime.Click(.04, 180);
        Assert.Equal(1, runtime.Shots);
        runtime.Click(.04, 5000, .095);
        Assert.Equal(1, runtime.Shots);
        runtime.Click(.04, 5000);
        Assert.Equal(2, runtime.Shots);
        detector.Reset();
        runtime.Quiet(1);
        runtime.Click(.04, 180);
        Assert.Equal(2, runtime.Shots);

        // A learned mismatch never falls back to the untrained loud-click rule.
        runtime.Click(.5, 180);
        Assert.Equal(2, runtime.Shots);
        Assert.Equal(2, detector.Analysis.RejectedByProfile);
        Assert.Equal(2, detector.Analysis.CompletedTransients);
    }

    [Fact]
    public void HeldOutAndRuntimeSegmentTheSameSoftLeadingClick()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        LearnAndValidate(stream, stream.SoftLeadingClick);
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.InRange(stream.Trainer.Profile!.MinimumDurationMs, 9, 11);
        var detector = new AudioClickDetector();
        detector.SetProfile(stream.Trainer.Profile);
        var runtime = new DetectorStream(detector);
        runtime.Quiet(1);
        runtime.SoftLeadingClick();
        Assert.Equal(1, runtime.Shots);
    }

    [Fact]
    public void StrongClickWithALongQuieterTailLearnsAndMatchesPromptly()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        LearnAndValidate(stream, stream.DecayingClick);
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.Equal(0, stream.Trainer.Diagnostics.Detection.TimedOutCandidates);

        var detector = new AudioClickDetector();
        detector.SetProfile(stream.Trainer.Profile);
        var runtime = new DetectorStream(detector);
        runtime.Quiet(1);
        var onset = runtime.Now;
        runtime.Sound(.01, .8, 5000);
        runtime.Sound(.015, .08, 5000);
        Assert.Equal(1, runtime.Shots);
        Assert.Equal(onset, detector.Analysis.LastCompletedTransient!.Value.At);
        Assert.InRange(detector.Analysis.LastCompletedTransient.Value.Features.DurationMs, 9, 11);
        runtime.Sound(.2, .06, 5000);
        runtime.Quiet(.4);
        Assert.Equal(1, runtime.Shots);

        // The established untrained detector is deliberately untouched.
        var original = new AudioClickDetector();
        var untrained = new DetectorStream(original);
        untrained.Quiet(1);
        untrained.DecayingClick();
        Assert.Equal(0, untrained.Shots);
        Assert.Equal(1, original.Analysis.TimedOutCandidates);
    }

    [Fact]
    public void RelativeFallDoesNotTurnSustainedSpeechOrClippedClicksIntoTrainingExamples()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        stream.Sound(.3, .25, 5000);
        stream.Quiet(.5);
        // Speech-like syllable: gradual attack, sustained vowel and slower decay.
        for (int i = 1; i <= 10; i++) stream.Sound(.005, .02 * i, 350);
        stream.Sound(.15, .2, 350);
        for (int i = 20; i >= 1; i--) stream.Sound(.005, .01 * i, 350);
        stream.Quiet(.5);
        stream.Click(amplitude: 1.1);
        Assert.Equal(0, stream.Trainer.Snapshot.CollectedClicks);
        Assert.True(stream.Trainer.Diagnostics.Detection.TimedOutCandidates >= 1);
        Assert.Equal(1, stream.Trainer.Diagnostics.ClippedRejected);
        Assert.False(stream.Trainer.Snapshot.CanSave);
    }

    [Fact]
    public void QuietCheckRejectsAFalsePositiveAndNeverMakesProfileSaveable()
    {
        var stream = TrainingToValidation();
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream);
        Assert.Equal(AudioClickLearningStage.QuietValidation, stream.Trainer.Snapshot.Stage);
        stream.Click();
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        stream.Quiet(4);
        Assert.False(stream.Trainer.Snapshot.CanSave);
        Assert.Null(stream.Trainer.Profile);
    }

    [Fact]
    public void InconsistentExamplesAndHeldOutFailuresCannotBeSaved()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        stream.Click();
        for (int i = 0; i < 3; i++) stream.Click(frequency: 180);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        Assert.Equal(1, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Null(stream.Trainer.Profile);

        stream = TrainingToValidation();
        ValidateClick(stream);
        Assert.Equal(1, stream.Trainer.Snapshot.ValidatedClicks);
        for (int i = 1; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream, frequency: 180);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        Assert.Equal(1, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Equal(5, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.True(stream.Trainer.Snapshot.CanRetryValidation);
        Assert.Null(stream.Trainer.Profile);
    }

    [Fact]
    public void SteadyNoiseSpeechLikeBurstsAndClippingDoNotTeachClicks()
    {
        var loud = new TrainingStream();
        loud.Sound(4, .4, 500);
        Assert.Equal(AudioClickLearningStage.Failed, loud.Trainer.Snapshot.Stage);

        var stream = new TrainingStream();
        stream.Quiet(3.1);
        for (int i = 0; i < 3; i++)
        {
            stream.Sound(.35, .08, 500);
            stream.Quiet(.4);
        }
        stream.Click(amplitude: 1.1);
        Assert.Equal(0, stream.Trainer.Snapshot.CollectedClicks);
        Assert.False(stream.Trainer.Snapshot.CanSave);
        Assert.Null(stream.Trainer.Profile);
    }

    [Fact]
    public void DisconnectStreamGapAndFormatChangeRequireRestart()
    {
        var stream = TrainingToValidation();
        stream.Trainer.DeviceUnavailable();
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        Assert.Null(stream.Trainer.Profile);
        stream.Trainer.Start("replacement-device");
        Assert.Equal("replacement-device", stream.Trainer.DeviceId);
        Assert.Equal(0, stream.Trainer.Snapshot.CollectedClicks);
        stream.Quiet(.2);
        stream.Skip(.2);
        stream.Quiet(.005);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);

        stream = new TrainingStream();
        stream.Quiet(.1);
        stream.Trainer.Process(new float[80], 16000, 1, stream.Now);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
    }

    [Fact]
    public void CancelDiscardsPendingTrainingAndRestartDoesNotReuseExamples()
    {
        var stream = TrainingToValidation();
        stream.Trainer.Cancel();
        stream.Quiet(4);
        Assert.Equal(AudioClickLearningStage.Cancelled, stream.Trainer.Snapshot.Stage);
        Assert.Null(stream.Trainer.Profile);
        stream.Trainer.Start("physical-microphone-id");
        stream.Quiet(3.1);
        Assert.Equal(0, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Equal(AudioClickLearningStage.LearningClicks, stream.Trainer.Snapshot.Stage);
    }

    [Fact]
    public void RepeatedModerateRoomNoiseStillAllowsLearnedClicks()
    {
        var stream = new TrainingStream(background: .004);
        stream.Quiet(3.1);
        LearnAndValidate(stream, () => stream.Click(amplitude: .07));
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.InRange(stream.Trainer.Profile!.TrainingNoiseRms, .002, .004);
    }

    [Fact]
    public void QuietCheckWaitsForSoundWhichStartsAtTheEndOfItsWindow()
    {
        var stream = TrainingToValidation();
        for (int i = 0; i < AudioClickTrainer.RequiredValidation - 1; i++) ValidateClick(stream);
        ValidateClick(stream, pause: .015);
        Assert.Equal(AudioClickLearningStage.QuietValidation, stream.Trainer.Snapshot.Stage);
        stream.Quiet(2.98);
        stream.Sound(.02, .04, 5000);
        Assert.False(stream.Trainer.Snapshot.CanSave);
        stream.Quiet(.05);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
    }

    [Fact]
    public void StreamingAndSnapshotReadsAllocateNoSteadyStateMemory()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            stream.Quiet(.005);
            _ = stream.Trainer.Snapshot;
            _ = stream.Trainer.Diagnostics;
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void WaitingWithinAGreenTestAllocatesNoSteadyStateMemory()
    {
        var stream = TrainingToValidation();
        WaitForGreen(stream);
        _ = stream.Trainer.Snapshot;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            stream.Quiet(.005);
            _ = stream.Trainer.Snapshot;
            _ = stream.Trainer.Diagnostics;
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(0, stream.Trainer.Snapshot.CompletedValidationAttempts);
    }

    [Fact]
    public void DiagnosticsDistinguishWeakNoiseClippingInconsistencyAndLongSounds()
    {
        var stream = new TrainingStream(background: .002);
        stream.Quiet(3.1);
        stream.Click();
        var accepted = stream.Trainer.Diagnostics.LastAcceptedExample;
        Assert.NotNull(accepted);
        stream.Click(amplitude: .008);
        Assert.Equal(1, stream.Trainer.Diagnostics.WeakRejected);
        Assert.Equal(accepted, stream.Trainer.Diagnostics.LastAcceptedExample);
        Assert.Equal(1, stream.Trainer.Snapshot.CollectedClicks);
        stream.Click(amplitude: 1.1);
        Assert.Equal(1, stream.Trainer.Diagnostics.ClippedRejected);
        stream.Click(frequency: 180);
        Assert.Equal(1, stream.Trainer.Diagnostics.InconsistentRejected);
        stream.Sound(.2, .04, 5000);
        stream.Quiet(.3);
        Assert.Equal(1, stream.Trainer.Diagnostics.Detection.TimedOutCandidates);
        Assert.Equal(4, stream.Trainer.Diagnostics.Detection.CompletedTransients);
        var before = stream.Trainer.Diagnostics;
        stream.Trainer.DeviceUnavailable();
        Assert.Equal(before, stream.Trainer.Diagnostics);
        stream.Trainer.Cancel();
        Assert.Equal(before, stream.Trainer.Diagnostics);
        stream.Trainer.Start("physical-microphone-id");
        Assert.Equal(default, stream.Trainer.Diagnostics);
    }

    [Fact]
    public void WeakIncidentalNoiseDoesNotImmediatelyEraseAcceptedFeedback()
    {
        var stream = new TrainingStream(background: .002);
        stream.Quiet(3.1);
        stream.Click();
        string acceptedMessage = stream.Trainer.Snapshot.Message;
        stream.Click(amplitude: .008);
        Assert.Equal(acceptedMessage, stream.Trainer.Snapshot.Message);
        Assert.Equal(1, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Equal(1, stream.Trainer.Diagnostics.WeakRejected);
        stream.Quiet(1.6);
        stream.Click(amplitude: .008);
        Assert.Contains("too close", stream.Trainer.Snapshot.Message);
        Assert.Equal(2, stream.Trainer.Diagnostics.WeakRejected);
    }

    [Fact]
    public void RejectedAttemptCannotBeHiddenByRepeatingUntilAClickMatches()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        for (int i = 0; i < AudioClickTrainer.RequiredClicks; i++) stream.Click(amplitude: .8);
        ValidateClick(stream, amplitude: .8, frequency: 180);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        // A second click during red must not replace the first test's failed result.
        stream.Click(amplitude: .8);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        for (int i = 1; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream, amplitude: .8);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        Assert.Equal(4, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Equal(1, stream.Trainer.Diagnostics.ValidationRejected);
        Assert.False(stream.Trainer.Snapshot.CanSave);
    }

    [Fact]
    public void QuietIncidentalSoundsCannotStealAPromptedClickWindow()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        for (int i = 0; i < AudioClickTrainer.RequiredClicks; i++) stream.Click(amplitude: .8);
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++)
        {
            WaitForGreen(stream);
            stream.Click(amplitude: .1);
            Assert.Equal(i, stream.Trainer.Snapshot.CompletedValidationAttempts);
            stream.Click(amplitude: .8);
            Assert.Equal(i + 1, stream.Trainer.Snapshot.CompletedValidationAttempts);
            Assert.Equal(i + 1, stream.Trainer.Snapshot.ValidatedClicks);
        }
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.Equal(0, stream.Trainer.Diagnostics.ValidationRejected);
    }

    [Fact]
    public void WindowWithOnlyQuietIncidentalSoundsStillCountsAsAMissedTest()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        for (int i = 0; i < AudioClickTrainer.RequiredClicks; i++) stream.Click(amplitude: .8);
        WaitForGreen(stream);
        stream.Click(amplitude: .1);
        stream.Sound(.2, .1, 180);
        stream.Quiet(.35);
        Assert.Equal(0, stream.Trainer.Snapshot.CompletedValidationAttempts);
        stream.Quiet(2.3);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Equal(1, stream.Trainer.Diagnostics.ValidationRejected);
        Assert.False(stream.Trainer.Snapshot.CanSave);
    }

    [Fact]
    public void MissingOnsetsConsumeFivePromptedAttemptsInsteadOfWaitingForever()
    {
        var stream = TrainingToValidation();
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++)
        {
            WaitForGreen(stream);
            Assert.Equal(i + 1, stream.Trainer.Snapshot.ValidationAttempt);
            stream.Quiet(3.16);
            Assert.Equal(i + 1, stream.Trainer.Snapshot.CompletedValidationAttempts);
            Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        }
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        Assert.Contains("0/5", stream.Trainer.Snapshot.Message);
        Assert.Contains("No click", stream.Trainer.Snapshot.Message);
        Assert.True(stream.Trainer.Snapshot.CanRetryValidation);
        Assert.False(stream.Trainer.Snapshot.CanSave);
        Assert.Null(stream.Trainer.Profile);
    }

    [Fact]
    public void TimeoutConsumesOneTestAndPreservesItsFailureThroughLaterSuccesses()
    {
        var stream = TrainingToValidation();
        WaitForGreen(stream);
        stream.Sound(.2, .04, 5000);
        stream.Quiet(.35);
        Assert.Equal(1, stream.Trainer.Diagnostics.Detection.TimedOutCandidates);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        for (int i = 1; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream);
        Assert.Equal(AudioClickLearningStage.Failed, stream.Trainer.Snapshot.Stage);
        Assert.Equal(4, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Contains("lasted too long", stream.Trainer.Snapshot.Message);
        Assert.Equal(1, stream.Trainer.Diagnostics.ValidationRejected);
    }

    [Fact]
    public void EarlierTimeoutCannotBeHiddenByALaterClickInTheSameAudioPacket()
    {
        var stream = TrainingToValidation();
        WaitForGreen(stream);
        stream.Sound(.12, .04, 5000);
        stream.TimeoutThenClickPacket();
        Assert.Equal(1, stream.Trainer.Diagnostics.Detection.TimedOutCandidates);
        Assert.Equal(AudioClickTrainer.RequiredClicks + 1, stream.Trainer.Diagnostics.Detection.CompletedTransients);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Contains("lasted too long", stream.Trainer.Snapshot.Message);
    }

    [Fact]
    public void ValidationRetryReusesExamplesButRequiresFiveNewTestsAndQuiet()
    {
        var stream = TrainingToValidation();
        Assert.False(stream.Trainer.RetryValidation());
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream, frequency: 180);
        Assert.True(stream.Trainer.RetryValidation());
        Assert.Equal(AudioClickTrainer.RequiredClicks, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Equal(0, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Null(stream.Trainer.Profile);
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream);
        Assert.Null(stream.Trainer.Profile);
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.False(stream.Trainer.RetryValidation());
        stream.Trainer.Cancel();
        Assert.Null(stream.Trainer.Profile);
        Assert.False(stream.Trainer.RetryValidation());
    }

    [Theory]
    [InlineData(2.995, true)]
    [InlineData(3.000, false)]
    public void ValidationUsesOnsetDeadlineAndLetsAnOnTimeClickFinish(double onsetSeconds, bool accepted)
    {
        var stream = TrainingToValidation();
        WaitForGreen(stream);
        stream.Quiet(onsetSeconds);
        stream.Click(pause: .2);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(accepted ? 1 : 0, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.False(stream.Trainer.Snapshot.CanSave);
    }

    [Fact]
    public void ClickBeginningDuringRedCannotBecomeANewGreenTest()
    {
        var stream = TrainingToValidation();
        // The last learning click already used some of the first red interval.
        while (stream.Trainer.Snapshot.Progress0To1 < .99) stream.Quiet(.005);
        stream.Click(duration: .02, pause: .02);
        Assert.True(stream.Trainer.Snapshot.Collecting);
        Assert.Equal(0, stream.Trainer.Snapshot.CompletedValidationAttempts);
        stream.Quiet(3.2);
        Assert.Equal(1, stream.Trainer.Snapshot.CompletedValidationAttempts);
        Assert.Equal(0, stream.Trainer.Snapshot.ValidatedClicks);
    }

    [Fact]
    public void TrainingAllowsNaturalLevelVariationAndUsesTheMajorityInsteadOfTheFirstClick()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        double[] amplitudes = [.025, .06, .05, .1, .08, .06, .1, .08, .07, .19];
        for (int i = 0; i < amplitudes.Length; i++)
        {
            stream.Click(amplitude: amplitudes[i]);
            Assert.Equal(i + 1, stream.Trainer.Snapshot.CollectedClicks);
        }
        Assert.Equal(0, stream.Trainer.Diagnostics.InconsistentRejected);
        foreach (double amplitude in new[] { .1, .04, .08, .12, .06 }) ValidateClick(stream, amplitude: amplitude);
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        Assert.True(stream.Trainer.Profile!.Matches(new(.1, .07, 20, .6)));
        Assert.InRange(stream.Trainer.Profile.MaximumPeak, .25, .27);
    }

    [Fact]
    public void SeveralConsistentExamplesStillRejectAnUnrelatedClunk()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        stream.Click(amplitude: .025);
        stream.Click(amplitude: .06);
        stream.Click(amplitude: .05);
        stream.Click(amplitude: .08, frequency: 180);
        Assert.Equal(3, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Equal(1, stream.Trainer.Diagnostics.InconsistentRejected);
    }

    [Fact]
    public void QuieterMechanicalTailCannotBroadenTrainingEvenInAQuietRoom()
    {
        var stream = new TrainingStream(background: .0001);
        stream.Quiet(3.1);
        foreach (double amplitude in new[] { .24, .26, .23 }) stream.Click(amplitude: amplitude, duration: .1);
        // The real capture's mechanical tail had about 27% of the weaker click's
        // peak and RMS and similar high-frequency content. Its shorter duration
        // alone must not let it become one of the ten intended examples.
        stream.Click(amplitude: .064, duration: .045);
        Assert.Equal(3, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Equal(1, stream.Trainer.Diagnostics.InconsistentRejected);
        Assert.Equal(0, stream.Trainer.Diagnostics.WeakRejected);
    }

    private static TrainingStream TrainingToValidation()
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        for (int i = 0; i < AudioClickTrainer.RequiredClicks; i++) stream.Click();
        Assert.Equal(AudioClickLearningStage.ValidationClicks, stream.Trainer.Snapshot.Stage);
        return stream;
    }

    private static TrainingStream CompleteTraining()
    {
        var stream = TrainingToValidation();
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++) ValidateClick(stream);
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave);
        return stream;
    }

    [Theory]
    [InlineData(.115)]
    [InlineData(.120)]
    public void LearningAndValidationBothAllowQuietConfirmationAfterTheSoundWindow(double duration)
    {
        var stream = new TrainingStream();
        stream.Quiet(3.1);
        LearnAndValidate(stream, () => stream.Click(duration: duration));
        stream.Quiet(3.1);
        Assert.True(stream.Trainer.Snapshot.CanSave, $"{stream.Trainer.Snapshot}; {stream.Trainer.Diagnostics}");
        Assert.Equal(AudioClickTrainer.RequiredClicks, stream.Trainer.Snapshot.CollectedClicks);
        Assert.Equal(AudioClickTrainer.RequiredValidation, stream.Trainer.Snapshot.ValidatedClicks);
        Assert.Equal(0, stream.Trainer.Diagnostics.Detection.TimedOutCandidates);
        Assert.InRange(stream.Trainer.Profile!.MaximumDurationMs, duration * 1000, AudioClickProfile.MaximumLearnedDurationMs);
    }

    private static void LearnAndValidate(TrainingStream stream, Action click)
    {
        for (int i = 0; i < AudioClickTrainer.RequiredClicks; i++) click();
        for (int i = 0; i < AudioClickTrainer.RequiredValidation; i++)
        {
            WaitForGreen(stream);
            click();
        }
    }

    private static void ValidateClick(TrainingStream stream, double amplitude = .04, double frequency = 5000,
        double duration = .02, double pause = .35)
    {
        WaitForGreen(stream);
        stream.Click(amplitude, frequency, duration, pause);
    }

    private static void WaitForGreen(TrainingStream stream)
    {
        for (int i = 0; i < 300 && !stream.Trainer.Snapshot.Collecting; i++) stream.Quiet(.005);
        Assert.Equal(AudioClickLearningStage.ValidationClicks, stream.Trainer.Snapshot.Stage);
        Assert.True(stream.Trainer.Snapshot.Collecting, stream.Trainer.Snapshot.ToString());
    }

    private abstract class PcmStream(double background)
    {
        private static readonly DateTimeOffset Origin = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        private readonly float[] _samples = new float[240];
        private int _frame;
        public DateTimeOffset Now => Origin.AddSeconds((double)_frame / 48000);
        public void Skip(double seconds) => _frame += (int)Math.Round(seconds * 48000);
        public void Quiet(double seconds) => Sound(seconds, 0, 0);
        public void Click(double amplitude = .04, double frequency = 5000, double duration = .02, double pause = .35)
        {
            Sound(duration, amplitude, frequency);
            Quiet(pause);
        }
        public void SoftLeadingClick()
        {
            Sound(.015, .02, 5000);
            Sound(.005, .2, 5000);
            Quiet(.35);
        }
        public void DecayingClick()
        {
            Sound(.01, .8, 5000);
            for (int i = 0; i < 40; i++) Sound(.005, .08 * Math.Exp(-i * .005 / .5), 5000);
            Quiet(.4);
        }
        public void TimeoutThenClickPacket()
        {
            var packet = new float[4800];
            for (int i = 0; i < packet.Length; i++)
            {
                double offset = (double)i / 48000;
                double t = (double)(_frame + i) / 48000;
                double amplitude = offset < .005 || offset is >= .070 and < .090 ? .04 : 0;
                packet[i] = (float)(background * Math.Sin(2 * Math.PI * 270 * t) +
                    amplitude * Math.Sin(2 * Math.PI * 5000 * t));
            }
            Process(packet, Now);
            _frame += packet.Length;
        }
        public void Sound(double seconds, double amplitude, double frequency)
        {
            int blocks = (int)Math.Round(seconds / .005);
            for (int block = 0; block < blocks; block++)
            {
                for (int i = 0; i < _samples.Length; i++)
                {
                    double t = (double)(_frame + i) / 48000;
                    _samples[i] = (float)(background * Math.Sin(2 * Math.PI * 270 * t) +
                        amplitude * Math.Sin(2 * Math.PI * frequency * t));
                }
                Process(_samples, Now);
                _frame += _samples.Length;
            }
        }
        protected abstract void Process(float[] samples, DateTimeOffset at);
    }

    private sealed class TrainingStream : PcmStream
    {
        public AudioClickTrainer Trainer { get; } = new();
        public TrainingStream(double background = .0003) : base(background) => Trainer.Start("physical-microphone-id");
        protected override void Process(float[] samples, DateTimeOffset at) => Trainer.Process(samples, 48000, 1, at);
    }

    private sealed class DetectorStream(AudioClickDetector detector) : PcmStream(.0003)
    {
        public int Shots { get; private set; }
        protected override void Process(float[] samples, DateTimeOffset at)
        {
            if (detector.Process(samples, 48000, 1, at) is not null) Shots++;
        }
    }
}
