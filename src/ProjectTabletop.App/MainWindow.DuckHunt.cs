using System.Diagnostics;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Media;
using ProjectTabletop.App.Settings;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly FrameWorker _duckHuntWorker = new();
    private readonly FrameWorker _duckHuntClickWorker = new();
    private readonly RedDotDetector _duckHuntDetector = new();
    private readonly RedDotDetector _duckHuntClickDetector = new();
    private readonly MicrophoneClickListener _duckHuntMicrophone = new();
    private long _duckHuntGeneration, _duckHuntAudioGeneration;
    private long _duckHuntDetectedClicks, _duckHuntLastClickUtcTicks;
    private long _duckHuntBusyClicks;
    private bool _duckHuntMicrophoneRequested;
    private string? _duckHuntSelectedMicrophoneId, _duckHuntRequestedMicrophoneId;
    private string? _duckHuntMicrophoneSettingsError;
    private IReadOnlyList<DuckHuntMicrophoneChoice> _duckHuntMicrophones = [];
    private long _duckHuntMicrophoneListGeneration;
    private Task _duckHuntAudioTask = Task.CompletedTask;
    private Task _duckHuntMicrophoneListTask = Task.CompletedTask;
    private DuckHuntObservation? _duckHuntObservation;
    private DuckHuntClickSnapshot? _duckHuntClickSnapshot;
    private sealed record DuckHuntObservation(RedDotDetectionResult Dot, DateTimeOffset Timestamp,
        double Milliseconds, long Generation);
    private sealed record DuckHuntClickSnapshot(DateTimeOffset OnsetAt, DateTimeOffset ConfirmedAt,
        DateTimeOffset? FrameTimestamp, RedDotDetectionResult? Dot, string Outcome,
        double DetectionMilliseconds, double TotalMilliseconds, long Generation);

    private void InitializeDuckHunt()
    {
        try { _duckHuntSelectedMicrophoneId = DuckHuntMicrophoneSettingsStore.Read(DuckHuntMicrophoneSettingsPath); }
        catch (Exception error) when (error is IOException or InvalidDataException or
            UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Write("Duck Hunt microphone preference", error);
            _duckHuntMicrophoneSettingsError = "Saved microphone choice could not be read. Using Windows default.";
        }
        _scene.BoardOpened += BoardOpenedForDuckHunt;
        _scene.DuckHuntMicrophonesRequested += DuckHuntMicrophonesRequested;
        _scene.DuckHuntMicrophoneSelected += DuckHuntMicrophoneSelected;
        _duckHuntMicrophone.ClickDetected += DuckHuntClickDetected;
        LoadClickSoundProfiles();
    }

    private string DuckHuntMicrophoneSettingsPath => Path.Combine(_appDataDirectory, DuckHuntMicrophoneSettingsStore.FileName);

    private void DuckHuntMicrophonesRequested() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing && _scene.CurrentBoardScreen == BoardScreen.DuckHunt)
            _duckHuntMicrophoneListTask = RefreshDuckHuntMicrophonesAsync();
    });

    private async Task RefreshDuckHuntMicrophonesAsync()
    {
        long generation = Interlocked.Increment(ref _duckHuntMicrophoneListGeneration);
        _scene.SetDuckHuntMicrophones(_duckHuntMicrophones, _duckHuntSelectedMicrophoneId,
            loading: true, error: _duckHuntMicrophoneSettingsError);
        ApplyClickLearningInputs(_duckHuntMicrophones, _duckHuntSelectedMicrophoneId, loading: true);
        try
        {
            var inputs = await MicrophoneClickListener.GetInputsAsync();
            if (_closing || generation != Interlocked.Read(ref _duckHuntMicrophoneListGeneration)) return;
            _duckHuntMicrophones = inputs.Select(input => new DuckHuntMicrophoneChoice(input.Id, input.Name)).ToArray();
            string? error = _duckHuntSelectedMicrophoneId is { } selected &&
                !_duckHuntMicrophones.Any(input => input.Id == selected)
                ? "Selected microphone is unavailable. Choose another input." : _duckHuntMicrophoneSettingsError;
            _scene.SetDuckHuntMicrophones(_duckHuntMicrophones, _duckHuntSelectedMicrophoneId, loading: false, error: error);
            ApplyClickLearningInputs(_duckHuntMicrophones, _duckHuntSelectedMicrophoneId, loading: false);
        }
        catch (Exception error)
        {
            AppLog.Write("Duck Hunt microphone list", error);
            if (!_closing && generation == Interlocked.Read(ref _duckHuntMicrophoneListGeneration))
            {
                _scene.SetDuckHuntMicrophones(_duckHuntMicrophones, _duckHuntSelectedMicrophoneId,
                    loading: false, error: "Could not list microphones. Close and reopen MIC to retry.");
                ApplyClickLearningInputs(_duckHuntMicrophones, _duckHuntSelectedMicrophoneId, loading: false);
                _clickLearningNotice = "Could not list microphones. Close click learning and reopen it to retry.";
            }
        }
    }

    private void DuckHuntMicrophoneSelected(string? deviceId)
    {
        // Selection can arrive under the compositor lock. Invalidate shots now;
        // publish the preference and perform device work on the UI thread.
        Interlocked.Increment(ref _duckHuntGeneration);
        Volatile.Write(ref _duckHuntObservation, null);
        _scene.SetDuckHuntAudioStatus(false, "Starting microphone...");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            // A projected input change must not attach another microphone's
            // samples to an in-progress training session.
            CancelClickLearning();
            SelectClickLearningInput(deviceId);
        });
    }

    private void OnOpenDuckHuntBoard(object sender, RoutedEventArgs args) => ShowDuckHunt();

    private void ShowDuckHunt()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowDuckHunt();
        ResetDuckHuntInput();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Duck Hunt: aim the red dot and click. Each hit turns the walking duck around; ten hits win. " +
            "If the duck walks off the board, the duck wins. Open the arrow for EXIT and MIC.");
    }

    private void BoardOpenedForDuckHunt(BoardScreen screen)
    {
        // BoardOpened can run under the compositor lock. Invalidate old work now,
        // then perform Windows microphone consent/lifecycle work on the UI thread.
        Interlocked.Increment(ref _duckHuntGeneration);
        Volatile.Write(ref _duckHuntObservation, null);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            UpdateDuckHuntMicrophone();
            if (screen == BoardScreen.DuckHunt && _scene.CurrentBoardScreen == BoardScreen.DuckHunt)
                _duckHuntMicrophoneListTask = RefreshDuckHuntMicrophonesAsync();
        });
    }

    private bool DuckHuntCameraAvailable => !_closing && _cameraWanted && _camera.IsRunning &&
        !_cameraManualRestartRequired && _cameraOperation.CurrentCount != 0 &&
        _camera.ActiveDeviceId == _cameraWantedDeviceId && !Volatile.Read(ref _cameraHealthWarning) &&
        !Volatile.Read(ref _boardSetupActive);

    private bool DuckHuntCameraReady => DuckHuntCameraAvailable && _scene.DuckHuntBoardReady;

    private void UpdateDuckHuntMicrophone(bool forceRetry = false)
    {
        bool wanted = !_closing && (_clickLearningRequested || DuckHuntCameraReady);
        if (wanted != _duckHuntMicrophoneRequested || wanted &&
            (forceRetry || _duckHuntSelectedMicrophoneId != _duckHuntRequestedMicrophoneId))
        {
            _duckHuntMicrophoneRequested = wanted;
            _duckHuntRequestedMicrophoneId = _duckHuntSelectedMicrophoneId;
            long generation = Interlocked.Increment(ref _duckHuntAudioGeneration);
            ResetDuckHuntInput();
            _scene.SetDuckHuntAudioStatus(false, wanted ? "Starting microphone..." : "Waiting for camera and board setup");
            _duckHuntAudioTask = SetDuckHuntMicrophoneAsync(wanted, _duckHuntSelectedMicrophoneId, generation);
        }
        else if (wanted)
            _scene.SetDuckHuntAudioStatus(_duckHuntMicrophone.IsRunning,
                _duckHuntMicrophone.IsRunning ? "Microphone listening" : _duckHuntMicrophone.Status);
    }

    private async Task SetDuckHuntMicrophoneAsync(bool start, string? deviceId, long generation)
    {
        try
        {
            if (start) await _duckHuntMicrophone.StartAsync(deviceId);
            else await _duckHuntMicrophone.StopAsync();
            if (_closing || generation != Interlocked.Read(ref _duckHuntAudioGeneration)) return;
            _scene.SetDuckHuntAudioStatus(start && _duckHuntMicrophone.IsRunning,
                start ? (_duckHuntMicrophone.IsRunning ? "Microphone listening" : _duckHuntMicrophone.Status)
                    : "Waiting for camera and board setup");
        }
        catch (Exception error)
        {
            AppLog.Write("Duck Hunt microphone", error);
            if (!_closing && generation == Interlocked.Read(ref _duckHuntAudioGeneration))
                _scene.SetDuckHuntAudioStatus(false, "Microphone unavailable. Choose MIC to retry.");
        }
    }

    private void ResetDuckHuntInput()
    {
        Interlocked.Increment(ref _duckHuntGeneration);
        _scene.ClearDuckHuntInput();
        Volatile.Write(ref _duckHuntObservation, null);
        _duckHuntWorker.ResetCadence();
        // Camera stop/failure invokes this reset before the next status tick. Release
        // the microphone promptly instead of retaining it for up to another second.
        if (!_closing)
            DispatcherQueue.TryEnqueue(() => { if (!_closing) UpdateDuckHuntMicrophone(); });
    }

    private void QueueDuckHuntDetection(CameraFrame frame)
    {
        if (!DuckHuntCameraAvailable) return;
        long generation = Interlocked.Read(ref _duckHuntGeneration);
        _duckHuntWorker.TryRun(Stopwatch.GetTimestamp(), TimeSpan.Zero, frame, _ =>
        {
            try
            {
                var now = MonotonicClock.UtcNow;
                if (!Current() || frame.Timestamp > now || now - frame.Timestamp > DuckHuntAimGate.Freshness) return;
                var context = _scene.GetDuckHuntInputContext();
                if (context is null || frame.Timestamp <= context.NotBefore) return;
                long started = Stopwatch.GetTimestamp();
                var dot = _duckHuntDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra, context.CameraToBoard);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!Current()) return;
                _scene.PublishDuckHuntDot(dot.Detected ? new BoardAim(dot.BoardX!.Value, dot.BoardY!.Value) : null,
                    frame.Timestamp, context.Epoch);
                Volatile.Write(ref _duckHuntObservation, new(dot, frame.Timestamp, elapsed, generation));
            }
            catch (Exception error)
            {
                AppLog.Write("Duck Hunt red dot", error);
                if (Current()) _scene.ClearDuckHuntInput();
            }
        });
        bool Current() => generation == Interlocked.Read(ref _duckHuntGeneration) && DuckHuntCameraAvailable;
    }

    private void DuckHuntClickDetected(DateTimeOffset at)
    {
        // Never take a compositor/GPU lock on AudioGraph's real-time callback.
        // Retain the camera image available when the click is recognized, without
        // copying pixels or waiting for continuous dot tracking. One work slot
        // prevents a backlog of old shots, and owns the frame until detection ends.
        if (_closing || _clickLearningRequested) return;
        Interlocked.Increment(ref _duckHuntDetectedClicks);
        Interlocked.Exchange(ref _duckHuntLastClickUtcTicks, at.UtcTicks);
        long generation = Interlocked.Read(ref _duckHuntGeneration);
        long audioGeneration = Interlocked.Read(ref _duckHuntAudioGeneration);
        using var frameLease = AcquireLatestCameraFrame();
        // Read after acquiring: a camera frame may arrive during the slot lookup.
        DateTimeOffset confirmedAt = MonotonicClock.UtcNow;
        if (frameLease is null)
        {
            Report("No camera frame", null, null, 0);
            return;
        }
        CameraFrame frame = frameLease.Frame;
        if (!_duckHuntClickWorker.TryRun(Stopwatch.GetTimestamp(), TimeSpan.Zero, frame, _ =>
        {
            try
            {
                if (!Current()) { Report("Input changed", frame.Timestamp, null, 0); return; }
                if (!Fresh()) { Report("No fresh click frame", frame.Timestamp, null, 0); return; }
                var context = _scene.GetDuckHuntInputContext();
                if (context is null || at <= context.NotBefore || frame.Timestamp <= context.NotBefore)
                {
                    Report("Input inactive or reset", frame.Timestamp, null, 0);
                    return;
                }
                long started = Stopwatch.GetTimestamp();
                // The preview detector runs independently and has mutable scratch
                // arrays, so this worker owns its own reusable detector instance.
                var dot = _duckHuntClickDetector.Detect(frame.Width, frame.Height, frame.Stride,
                    frame.Bgra, context.CameraToBoard);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!Current()) { Report("Input changed", frame.Timestamp, dot, elapsed); return; }
                if (!Fresh()) { Report("Click frame expired", frame.Timestamp, dot, elapsed); return; }
                var before = _scene.DuckHuntState;
                bool accepted = _scene.SubmitDuckHuntSnapshot(dot.Detected
                        ? new BoardAim(dot.BoardX!.Value, dot.BoardY!.Value) : null,
                    frame.Timestamp, at, confirmedAt, context.Epoch);
                var after = _scene.DuckHuntState;
                Report(accepted ? after.Hits > before.Hits ? "Duck hit" : after.Shots > before.Shots ? "Shot missed" :
                    after.Phase == DuckHuntPhase.Turning ? "Shot during turn" : after.RoundFinished ? "Round finished" :
                    "Shot not applied" : dot.Detected ? "Snapshot rejected by current board" :
                    dot.Ambiguous ? "Ambiguous red dots" : "No red dot",
                    frame.Timestamp, dot, elapsed);
            }
            catch (Exception error)
            {
                AppLog.Write("Duck Hunt click snapshot", error);
                Report("Snapshot detection failed", frame.Timestamp, null, 0);
            }
        })) Interlocked.Increment(ref _duckHuntBusyClicks);

        bool Current() => generation == Interlocked.Read(ref _duckHuntGeneration) &&
            audioGeneration == Interlocked.Read(ref _duckHuntAudioGeneration) &&
            !_clickLearningRequested && _duckHuntMicrophone.IsRunning && DuckHuntCameraReady;
        bool Fresh()
        {
            var now = MonotonicClock.UtcNow;
            return at <= frame.Timestamp && frame.Timestamp <= confirmedAt && confirmedAt <= now &&
                now - at <= DuckHuntAimGate.Freshness && now - frame.Timestamp <= DuckHuntAimGate.Freshness &&
                frame.Timestamp.UtcTicks >= Interlocked.Read(ref _cameraInputNotBeforeTicks);
        }
        void Report(string outcome, DateTimeOffset? frameTimestamp, RedDotDetectionResult? dot, double elapsed) =>
            Volatile.Write(ref _duckHuntClickSnapshot, new(at, confirmedAt, frameTimestamp, dot, outcome,
                elapsed, (MonotonicClock.UtcNow - confirmedAt).TotalMilliseconds, generation));
    }

    private object DuckHuntInputStatus()
    {
        var observation = Volatile.Read(ref _duckHuntObservation);
        bool ready = DuckHuntCameraReady;
        var now = MonotonicClock.UtcNow;
        long lastClickTicks = Interlocked.Read(ref _duckHuntLastClickUtcTicks);
        return new
        {
            game = _scene.DuckHuntState,
            feathers = _scene.DuckHuntFeatherStatus,
            measurement = _scene.DuckHuntMeasurement,
            ready,
            microphoneMenuOpen = _scene.DuckHuntMicrophoneMenuOpen,
            clickLearning = ClickLearningStatus(),
            dot = observation?.Dot,
            dotVisible = ready && !_clickLearningRequested && !_scene.DuckHuntMicrophoneMenuOpen && observation is { Dot.Detected: true } &&
                observation.Generation == Interlocked.Read(ref _duckHuntGeneration) &&
                observation.Timestamp <= now && now - observation.Timestamp <= DuckHuntAimGate.Freshness,
            observation?.Timestamp, observation?.Milliseconds,
            clickSnapshot = Volatile.Read(ref _duckHuntClickSnapshot),
            microphone = new { _duckHuntMicrophone.IsRunning, _duckHuntMicrophone.DeviceName,
                _duckHuntMicrophone.DeviceId, selectedDeviceId = _duckHuntSelectedMicrophoneId,
                available = _duckHuntMicrophones, settingsError = _duckHuntMicrophoneSettingsError,
                _duckHuntMicrophone.Status, _duckHuntMicrophone.Diagnostics,
                detectedClicks = Interlocked.Read(ref _duckHuntDetectedClicks),
                busyClicks = Interlocked.Read(ref _duckHuntBusyClicks),
                lastClickAt = lastClickTicks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(lastClickTicks, TimeSpan.Zero) }
        };
    }

    private async Task DisposeDuckHuntAsync()
    {
        _scene.BoardOpened -= BoardOpenedForDuckHunt;
        _scene.DuckHuntMicrophonesRequested -= DuckHuntMicrophonesRequested;
        _scene.DuckHuntMicrophoneSelected -= DuckHuntMicrophoneSelected;
        _duckHuntMicrophone.ClickDetected -= DuckHuntClickDetected;
        Interlocked.Increment(ref _duckHuntGeneration);
        Interlocked.Increment(ref _duckHuntAudioGeneration);
        Interlocked.Increment(ref _duckHuntMicrophoneListGeneration);
        await _duckHuntMicrophone.DisposeAsync();
        await _duckHuntAudioTask;
        await _duckHuntMicrophoneListTask;
        await _duckHuntClickWorker.WorkerTask;
        await _duckHuntWorker.WorkerTask;
    }
}
