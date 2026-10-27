using System.Runtime.InteropServices;
using ProjectTabletop.Interaction;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace ProjectTabletop.App.Media;

internal sealed record MicrophoneInput(string Id, string Name, bool IsDefault);
internal sealed record MicrophoneAudioTrace(bool Enabled, bool Learning, int Capacity, AudioClickTraceSample[] Windows);

/// <summary>
/// Microphone-to-analysis graph only: no speaker output and no recordings. Start on
/// the WinUI thread for Windows consent; stop when leaving the listening board.
/// ClickDetected runs synchronously on the audio callback thread, so its consumer
/// must be brief and must recheck the active board and camera aim before shooting.
/// </summary>
internal sealed class MicrophoneClickListener : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _analysisGate = new();
    private readonly AudioClickDetector _detector = new();
    private readonly AudioClickTrainer _trainer = new();
    private IReadOnlyDictionary<string, AudioClickProfile> _profiles = new Dictionary<string, AudioClickProfile>();
    private bool _learning;
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _output;
    private int _sampleRate, _channels;
    private long _generation;
    private long _lastAudioTicks;
    private volatile bool _running, _disposed;
    private string _status = "Microphone is off.";
    private string? _deviceName, _deviceId;

    public event Action<DateTimeOffset>? ClickDetected;
    public bool IsRunning => _running;
    public bool IsReceivingAudio => _running && Interlocked.Read(ref _lastAudioTicks) is var ticks && ticks != 0 &&
        MonotonicClock.UtcNow.UtcTicks - ticks < TimeSpan.FromSeconds(2).Ticks;
    public string Status => Volatile.Read(ref _status);
    public string? DeviceName => Volatile.Read(ref _deviceName);
    public string? DeviceId => Volatile.Read(ref _deviceId);
    public AudioClickDetector.Levels Diagnostics
    {
        get { lock (_analysisGate) return _detector.Diagnostics; }
    }
    public AudioClickDetectionDiagnostics Analysis
    {
        get { lock (_analysisGate) return _detector.Analysis; }
    }

    // Explicit diagnostics only: no copies are made by normal status/UI polling.
    // Toggling both detectors keeps capture available across learning transitions.
    public MicrophoneAudioTrace ReadAudioTrace(bool? enabled = null)
    {
        lock (_analysisGate)
        {
            if (enabled is { } capture)
            {
                _detector.SetTraceEnabled(capture);
                _trainer.SetTraceEnabled(capture);
            }
            return new(_learning ? _trainer.TraceEnabled : _detector.TraceEnabled, _learning,
                AudioClickTrace.Capacity, _learning ? _trainer.CopyTrace() : _detector.CopyTrace());
        }
    }

    public AudioClickLearningSnapshot LearningSnapshot
    {
        get
        {
            lock (_analysisGate)
            {
                if (_learning && !IsReceivingAudio && _trainer.Snapshot.Stage is not
                    (AudioClickLearningStage.Failed or AudioClickLearningStage.Cancelled))
                    _trainer.DeviceUnavailable();
                return _trainer.Snapshot;
            }
        }
    }
    public string? LearningDeviceId { get { lock (_analysisGate) return _trainer.DeviceId; } }
    public AudioClickProfile? LearnedProfile { get { lock (_analysisGate) return _trainer.Profile; } }
    public AudioClickTrainingDiagnostics LearningDiagnostics { get { lock (_analysisGate) return _trainer.Diagnostics; } }
    public bool HasLearnedProfile { get { lock (_analysisGate) return _deviceId is { } id && _profiles.ContainsKey(id); } }

    public void SetProfiles(IReadOnlyDictionary<string, AudioClickProfile> profiles)
    {
        lock (_analysisGate)
        {
            _profiles = profiles;
            _detector.SetProfile(_deviceId is { } id && profiles.TryGetValue(id, out var profile) ? profile : null);
        }
    }

    public bool BeginLearning()
    {
        lock (_analysisGate)
        {
            if (!_running || _deviceId is null) return false;
            _trainer.Start(_deviceId);
            _learning = true;
            _detector.Reset();
            return true;
        }
    }

    public void CancelLearning()
    {
        lock (_analysisGate)
        {
            _learning = false;
            _trainer.Cancel();
            _detector.Reset();
        }
    }

    public bool RetryLearningValidation()
    {
        lock (_analysisGate)
            return _learning && _running && IsReceivingAudio && _trainer.RetryValidation();
    }

    public static async Task<IReadOnlyList<MicrophoneInput>> GetInputsAsync()
    {
        string defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
        return devices.Where(device => device.IsEnabled)
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .Select(device => new MicrophoneInput(device.Id, device.Name,
                StringComparer.Ordinal.Equals(device.Id, defaultId))).ToArray();
    }

    public async Task<bool> StartAsync(string? deviceId = null)
    {
        long generation = Interlocked.Increment(ref _generation);
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || generation != Interlocked.Read(ref _generation)) return false;
            StopCore();
            Volatile.Write(ref _status, "Starting microphone…");
            string selectedId = deviceId ?? MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            if (string.IsNullOrWhiteSpace(selectedId))
            {
                Volatile.Write(ref _status, deviceId is null
                    ? "No default microphone found. Choose a microphone beside Duck Hunt in the main app window."
                    : "The selected microphone is unavailable. Choose another microphone beside Duck Hunt in the main app window.");
                return false;
            }
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            var selected = devices.FirstOrDefault(device => device.IsEnabled &&
                StringComparer.Ordinal.Equals(device.Id, selectedId));
            if (selected is null)
            {
                // A saved preference is an exact endpoint, not a hint. An unplugged
                // preferred microphone must never silently activate a different one.
                Volatile.Write(ref _status, "The selected microphone is unavailable. Choose another microphone beside Duck Hunt in the main app window.");
                return false;
            }
            if (_disposed || generation != Interlocked.Read(ref _generation)) return false;
            var format = AudioEncodingProperties.CreatePcm(48000, 1, 32);
            format.Subtype = MediaEncodingSubtypes.Float;
            var result = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.GameMedia)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
                EncodingProperties = format
            });
            if (result.Status != AudioGraphCreationStatus.Success)
            {
                Volatile.Write(ref _status, $"Microphone could not start ({result.Status}).");
                return false;
            }
            _graph = result.Graph;
            if (_disposed || generation != Interlocked.Read(ref _generation))
            {
                StopCore();
                return false;
            }

            var inputResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, format, selected);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Volatile.Write(ref _status, inputResult.Status == AudioDeviceNodeCreationStatus.AccessDenied
                    ? "Microphone access is blocked. Allow microphone access for desktop apps in Windows Settings."
                    : $"Microphone unavailable ({inputResult.Status}). Choose another microphone beside Duck Hunt in the main app window.");
                StopCore();
                return false;
            }
            _input = inputResult.DeviceInputNode;
            if (_disposed || generation != Interlocked.Read(ref _generation))
            {
                StopCore();
                return false;
            }
            _output = _graph.CreateFrameOutputNode(format);
            _input.AddOutgoingConnection(_output);
            _sampleRate = checked((int)_output.EncodingProperties.SampleRate);
            _channels = checked((int)_output.EncodingProperties.ChannelCount);
            lock (_analysisGate)
            {
                Volatile.Write(ref _deviceName, selected.Name);
                Volatile.Write(ref _deviceId, selected.Id);
                _detector.SetProfile(_profiles.TryGetValue(selected.Id, out var profile) ? profile : null);
            }
            Volatile.Write(ref _status, $"Listening on {selected.Name}.");
            Interlocked.Exchange(ref _lastAudioTicks, MonotonicClock.UtcNow.UtcTicks);
            _graph.QuantumStarted += OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred += OnUnrecoverableError;
            _running = true;
            _graph.Start();
            return true;
        }
        catch (Exception error)
        {
            StopCore();
            Volatile.Write(ref _status, error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005)
                ? "Microphone access is blocked. Allow microphone access for desktop apps in Windows Settings."
                : $"Microphone could not start: {error.Message}");
            return false;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        _running = false;
        await _lifecycle.WaitAsync();
        try
        {
            StopCore();
            Volatile.Write(ref _status, "Microphone is off.");
        }
        finally { _lifecycle.Release(); }
    }

    private unsafe void OnQuantumStarted(AudioGraph sender, object args)
    {
        if (!_running || !ReferenceEquals(sender, _graph)) return;
        DateTimeOffset? click;
        long generation = Interlocked.Read(ref _generation);
        try
        {
            lock (_analysisGate)
            {
                if (!_running || !ReferenceEquals(sender, _graph) || _output is null) return;
                using var frame = _output.GetFrame();
                using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
                using var reference = buffer.CreateReference();
                reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out uint capacity);
                uint bytes = Math.Min(buffer.Length, capacity);
                if (bytes == 0) return;
                Interlocked.Exchange(ref _lastAudioTicks, MonotonicClock.UtcNow.UtcTicks);
                int sampleCount = checked((int)(bytes / sizeof(float)));
                sampleCount -= sampleCount % _channels;
                double seconds = (double)sampleCount / (_sampleRate * _channels);
                if (seconds > .1)
                {
                    // GetFrame can accumulate audio when the driver stalls. Never replay it as fresh shots.
                    _detector.Reset();
                    if (_learning) _trainer.DeviceUnavailable();
                    return;
                }
                // AudioGraph does not expose the microphone's absolute hardware capture clock.
                // Estimate onset from this bounded live quantum using the camera's monotonic UTC clock.
                var start = MonotonicClock.UtcNow.AddSeconds(-seconds);
                var samples = new ReadOnlySpan<float>(data, sampleCount);
                if (_learning)
                {
                    _trainer.Process(samples, _sampleRate, _channels, start);
                    click = null;
                }
                else click = _detector.Process(samples, _sampleRate, _channels, start);
            }
            if (click is { } at && _running && generation == Interlocked.Read(ref _generation))
                ClickDetected?.Invoke(at);
        }
        catch (Exception error)
        {
            Fail(sender, $"Microphone stopped: {error.Message}");
        }
    }

    private void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args) =>
        Fail(sender, $"Microphone disconnected or unavailable ({args.Error}). Reconnect it or choose another microphone beside Duck Hunt in the main app window.");

    private void Fail(AudioGraph sender, string status)
    {
        if (!_running || !ReferenceEquals(sender, _graph)) return;
        _running = false;
        Volatile.Write(ref _status, status);
        // Stop/dispose outside the audio callback: the graph may wait for that callback to finish.
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync();
            try { if (ReferenceEquals(sender, _graph)) StopCore(); }
            finally { _lifecycle.Release(); }
        });
    }

    private void StopCore()
    {
        _running = false;
        var graph = _graph;
        _graph = null;
        if (graph is not null)
        {
            graph.QuantumStarted -= OnQuantumStarted;
            graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            try { graph.Stop(); } catch (Exception) { /* Device removal can invalidate the graph. */ }
        }
        lock (_analysisGate)
        {
            try { _output?.Dispose(); } catch (Exception) { }
            try { _input?.Dispose(); } catch (Exception) { }
            try { graph?.Dispose(); } catch (Exception) { }
            _output = null;
            _input = null;
            _detector.Reset();
            if (_learning) _trainer.DeviceUnavailable();
            Volatile.Write(ref _deviceName, null);
            Volatile.Write(ref _deviceId, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync();
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
