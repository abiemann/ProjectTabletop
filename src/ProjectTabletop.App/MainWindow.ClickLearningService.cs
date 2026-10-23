using ProjectTabletop.App.Settings;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private volatile bool _clickLearningRequested;
    private bool _clickLearningStarting;
    private long _clickLearningGeneration;
    private string? _clickLearningNotice, _clickLearningFailure, _clickProfilesError;
    private IReadOnlyDictionary<string, AudioClickProfile> _clickSoundProfiles =
        new Dictionary<string, AudioClickProfile>();

    private string ClickSoundProfilesPath => Path.Combine(_appDataDirectory, ClickSoundProfileSettingsStore.FileName);

    private void LoadClickSoundProfiles()
    {
        try
        {
            _clickSoundProfiles = ClickSoundProfileSettingsStore.Read(ClickSoundProfilesPath);
            _duckHuntMicrophone.SetProfiles(_clickSoundProfiles);
            _clickProfilesError = null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            AppLog.Write("Load click learning", error);
            _clickProfilesError = "Saved click learning could not be read. The ordinary click detector is being used.";
        }
    }

    private Task RefreshClickLearningInputsAsync()
    {
        _duckHuntMicrophoneListTask = RefreshDuckHuntMicrophonesAsync();
        return _duckHuntMicrophoneListTask;
    }

    private void SelectClickLearningInput(string? deviceId)
    {
        if (_closing || _clickLearningRequested) return;
        _duckHuntSelectedMicrophoneId = deviceId;
        _duckHuntMicrophoneSettingsError = null;
        _clickLearningNotice = null;
        try { DuckHuntMicrophoneSettingsStore.Write(DuckHuntMicrophoneSettingsPath, deviceId); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Write("Save Duck Hunt microphone preference", error);
            _duckHuntMicrophoneSettingsError = "Microphone selected for this session; the choice could not be saved.";
        }
        SetDuckHuntMicrophoneNotice(_duckHuntMicrophoneSettingsError);
        ApplyClickLearningInputs(_duckHuntMicrophones, deviceId, loading: false);
        UpdateDuckHuntMicrophone(forceRetry: true);
    }

    private async Task StartClickLearningAsync()
    {
        if (_closing || _clickLearningStarting) return;
        long generation = ++_clickLearningGeneration;
        _clickLearningRequested = true;
        _clickLearningStarting = true;
        _clickLearningNotice = _clickLearningFailure = null;
        _scene.SetDuckHuntLearningActive(true);
        ResetDuckHuntInput();
        _duckHuntMicrophone.CancelLearning();
        UpdateClickLearningUi();
        try
        {
            UpdateDuckHuntMicrophone(forceRetry: !_duckHuntMicrophone.IsReceivingAudio);
            await _duckHuntAudioTask;
            if (_closing || generation != _clickLearningGeneration || !_clickLearningRequested) return;
            if (!_duckHuntMicrophone.BeginLearning())
                _clickLearningFailure = _duckHuntMicrophone.Status;
        }
        catch (Exception error)
        {
            AppLog.Write("Start click learning", error);
            if (generation == _clickLearningGeneration)
                _clickLearningFailure = "The microphone could not start. Check its connection, then retry.";
        }
        finally
        {
            if (generation == _clickLearningGeneration) _clickLearningStarting = false;
            if (!_closing) UpdateClickLearningUi();
        }
    }

    private void CancelClickLearning()
    {
        if (!_clickLearningRequested && !_clickLearningStarting) return;
        FinishClickLearning("Learning cancelled. Your previous sound profile is unchanged.");
    }

    private void FinishClickLearning(string message)
    {
        ++_clickLearningGeneration;
        _clickLearningRequested = _clickLearningStarting = false;
        _clickLearningFailure = null;
        _clickLearningNotice = message;
        _duckHuntMicrophone.CancelLearning();
        _scene.SetDuckHuntLearningActive(false);
        ResetDuckHuntInput();
        if (!_closing)
        {
            UpdateDuckHuntMicrophone();
            UpdateClickLearningUi();
        }
    }

    private Task SaveClickLearningAsync()
    {
        if (_closing || !_clickLearningRequested || _clickLearningStarting ||
            _duckHuntMicrophone.LearnedProfile is not { } profile ||
            _duckHuntMicrophone.LearningDeviceId is not { } id ||
            !_duckHuntMicrophone.IsReceivingAudio || id != _duckHuntMicrophone.DeviceId)
            return Task.CompletedTask;
        try
        {
            // This explicit, small local write is outside the audio callback.
            // Merge with disk so learning one input preserves all other inputs.
            ClickSoundProfileSettingsStore.Save(ClickSoundProfilesPath, id, profile);
            _clickSoundProfiles = ClickSoundProfileSettingsStore.Read(ClickSoundProfilesPath);
            _duckHuntMicrophone.SetProfiles(_clickSoundProfiles);
            _clickProfilesError = null;
            FinishClickLearning("Click learning saved for this microphone. You can now use it in Duck Hunt.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            AppLog.Write("Save click learning", error);
            _clickLearningNotice = "Could not save or apply learning. Try Save again or Cancel.";
            UpdateClickLearningUi();
        }
        return Task.CompletedTask;
    }

    private void UpdateClickLearningUi()
    {
        if (_closing) return;
        var snapshot = _duckHuntMicrophone.LearningSnapshot;
        bool failed = _clickLearningFailure is not null || snapshot.Stage == AudioClickLearningStage.Failed;
        string microphone = _duckHuntMicrophone.DeviceName ??
            _duckHuntMicrophones.FirstOrDefault(input => input.Id == _duckHuntSelectedMicrophoneId)?.Label ??
            (_duckHuntSelectedMicrophoneId is null ? "System default" : "Unavailable microphone");
        string title = !_clickLearningRequested ? "Ready to learn" : _clickLearningStarting ? "WAIT · Starting microphone" :
            failed ? "Please try again" : snapshot.Stage switch
            {
                AudioClickLearningStage.QuietBaseline => "WAIT · DON'T CLICK YET",
                AudioClickLearningStage.LearningClicks => $"CLICK NOW · {snapshot.CollectedClicks}/6",
                AudioClickLearningStage.ValidationClicks => $"CLICK NOW · TEST {snapshot.ValidatedClicks}/3",
                AudioClickLearningStage.QuietValidation => "STOP CLICKING · QUIET CHECK",
                AudioClickLearningStage.Complete => "Checks passed",
                _ => "Click learning"
            };
        string instruction = _clickLearningNotice ?? _clickLearningFailure ??
            (_clickLearningStarting ? "Keep the room quiet while the microphone starts." :
             _clickLearningRequested ? snapshot.Message : _clickProfilesError ?? _duckHuntMicrophoneSettingsError ??
             ((_duckHuntMicrophone.HasLearnedProfile || _duckHuntSelectedMicrophoneId is { } selected &&
                _clickSoundProfiles.ContainsKey(selected))
                 ? "A saved click profile is available. Start learning to replace it; Cancel keeps it."
                 : "Choose a microphone, then start. Use your click device from your normal playing position."));
        ApplyClickLearningUi(title, instruction, _clickLearningRequested ? snapshot.Progress0To1 : 0,
            active: _clickLearningRequested, canStart: !_clickLearningStarting && (!_clickLearningRequested || failed),
            canSave: _clickLearningRequested && !_clickLearningStarting && snapshot.CanSave &&
                _duckHuntMicrophone.IsReceivingAudio,
            retry: _clickLearningRequested && failed, microphoneName: microphone,
            collecting: _clickLearningRequested && !_clickLearningStarting && !failed && snapshot.Stage is
                AudioClickLearningStage.LearningClicks or AudioClickLearningStage.ValidationClicks or AudioClickLearningStage.Complete);
    }

    private object ClickLearningStatus() => new
    {
        active = _clickLearningRequested, starting = _clickLearningStarting,
        deviceId = _duckHuntMicrophone.LearningDeviceId,
        snapshot = _duckHuntMicrophone.LearningSnapshot,
        diagnostics = _duckHuntMicrophone.LearningDiagnostics,
        learnedProfileActive = _duckHuntMicrophone.HasLearnedProfile,
        notice = _clickLearningNotice, error = _clickLearningFailure ?? _clickProfilesError
    };
}
