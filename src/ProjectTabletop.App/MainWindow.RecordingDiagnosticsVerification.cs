#if DEBUG
using System.Reflection;
using System.Text.Json;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Run only through the passive verification harness: its window and camera
    // constructors never run, and its compositor has no visible output.
    private async Task<object> VerifyRecordingDiagnosticsAsync()
    {
        if (_initialized || _statusTimer is not null || _handDetectionLog is not null ||
            _handVideoRecorder is not null)
            throw new InvalidOperationException("Recording policy verification requires an isolated passive owner.");

        int checks = 0;
        string directory = Path.Combine(_appDataDirectory, "recording-toggle-" + Guid.NewGuid().ToString("N"));
        string logs = Path.Combine(directory, "logs"), videos = Path.Combine(directory, "videos");
        Directory.CreateDirectory(directory);
        using var operation = new SemaphoreSlim(1, 1);
        SetField("_cameraOperation", operation);
        SetField("_strykersRunInputGate", new object());
        typeof(CameraCaptureService).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_camera, true);
        _cameraWanted = _handTrackingEnabled = true;
        _scene.ShowHandTrackingTest();
        _handDetectionLog = new(logs);
        _handVideoRecorder = new(videos);
        var hand = new HandDetection(Enumerable.Range(0, 21)
            .Select(index => new PixelPoint(80 + index % 5 * 12, 70 + index / 5 * 12)).ToArray(), .99, .5);
        var oldFrame = Frame(MonotonicClock.UtcNow.AddSeconds(-1));
        var recordings = new List<HandTrackingVideoRecorderStatus>();
        try
        {
            Check(!_recordingDiagnosticsEnabled && !_boardHandDiagnosticLogging, "Diagnostics did not start off.");
            BlockedInputs(oldFrame, 0);
            Check(!Directory.Exists(logs) && !Directory.Exists(videos) &&
                _handDetectionLog.Status.QueuedRecords == 0 && _handDetectionLog.Status.WrittenRecords == 0,
                "Default-off diagnostics created files or queued records.");
            Check(!_handVideoRecorder.Status.IsRecording, "Default-off accepted detection started video.");

            ApplyRecordingDiagnosticsPolicy(true);
            Check(_recordingDiagnosticsEnabled && _handVideoNotBefore > oldFrame.Timestamp,
                "Enabling did not establish a fresh-frame boundary.");
            LogHandTrackingEvent("enabled_event");
            RecordHandDiagnostic(new { type = "enabled_direct" });
            var firstFrame = Frame(MonotonicClock.UtcNow);
            QueueHandTrackingVideoFrame(firstFrame);
            Detection(firstFrame, 1);
            Check(_handVideoRecorder.Status.IsRecording, "Enabled tester detection did not start video.");
            _boardHandDiagnosticLogging = true;
            ApplyRecordingDiagnosticsPolicy(false);
            Check(!_recordingDiagnosticsEnabled && !_boardHandDiagnosticLogging &&
                _handVideoNotBefore == DateTimeOffset.MaxValue, "Off retained logging or stale-frame eligibility.");
            Check(!_handVideoRecorder.Status.IsRecording, "Off did not stop active video immediately.");
            BlockedInputs(Frame(MonotonicClock.UtcNow), 2);
            Check(!_handVideoRecorder.Status.IsRecording, "Late accepted detection restarted disabled video.");
            await _handVideoRecorder.DisposeAsync();
            recordings.Add(_handVideoRecorder.Status);

            // A fresh recorder has no finishing clip to accidentally mask a
            // missing stale-frame guard when diagnostics are enabled again.
            _handVideoRecorder = new(videos);
            ApplyRecordingDiagnosticsPolicy(true);
            LogHandTrackingEvent("reenabled_event");
            QueueHandTrackingVideoFrame(firstFrame);
            Detection(firstFrame, 3);
            Check(!_handVideoRecorder.Status.IsRecording, "A pre-toggle frame restarted video after re-enable.");
            var freshFrame = Frame(MonotonicClock.UtcNow);
            QueueHandTrackingVideoFrame(freshFrame);
            Detection(freshFrame, 4);
            Check(_handVideoRecorder.Status.IsRecording, "A fresh frame could not restart recording after re-enable.");
            ApplyRecordingDiagnosticsPolicy(false);
            await _handVideoRecorder.DisposeAsync();
            recordings.Add(_handVideoRecorder.Status);
            await _handDetectionLog.DisposeAsync();
            Check(_handDetectionLog.Status.Error is null && _handDetectionLog.Status.DroppedRecords == 0,
                "The private detection log failed or dropped records.");
            var entries = Directory.GetFiles(logs, "*.jsonl").SelectMany(File.ReadLines)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            string[] types = entries.Select(entry => entry.GetProperty("type").GetString()!).ToArray();
            Check(types.SequenceEqual(new[] { "enabled_event", "enabled_direct", "detection",
                "reenabled_event", "detection", "detection" }),
                "Off-state records leaked, or enabling failed to restore logging.");
            foreach (var recording in recordings)
            {
                Check(recording.Error is null && recording.WrittenFrames > 0 &&
                    recording.QueuedFrames == 0 && recording.QueuedRecords == 0,
                    "The stopped recording did not finish its queued synthetic frames.");
                var timeline = File.ReadLines(recording.TimelinePath!).Select(line =>
                    JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                Check(timeline.Any(entry => entry.GetProperty("type").GetString() == "session_end" &&
                    entry.GetProperty("reason").GetString() == "diagnostics_disabled"),
                    "The master switch did not finalize the recording with its stop reason.");
            }
            return new { passed = true, checks, directory, defaultOff = true, forcedEventsBlocked = true,
                activeVideoStopped = true, staleFrameBlocked = true, reenableWorks = true,
                syntheticFramesOnly = true, liveHardwareUnchanged = true };
        }
        finally
        {
            ApplyRecordingDiagnosticsPolicy(false);
            if (_handVideoRecorder is not null) await _handVideoRecorder.DisposeAsync();
            if (_handDetectionLog is not null) await _handDetectionLog.DisposeAsync();
            _handVideoRecorder = null;
            _handDetectionLog = null;
            _cameraWanted = _handTrackingEnabled = false;
            typeof(CameraCaptureService).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_camera, false);
            _scene.ShowBoardMenu();
        }

        void BlockedInputs(CameraFrame frame, long sequence)
        {
            LogHandTrackingEvent("off_forced", force: true);
            RecordHandDiagnostic(new { type = "off_direct" });
            QueueHandTrackingVideoFrame(frame);
            Detection(frame, sequence);
        }
        void Detection(CameraFrame frame, long sequence)
        {
            lock (_handGate)
                LogHandDetection(sequence, true, frame, 0, false, 1, null, null,
                    [hand], [hand], [], "accepted");
        }
        void SetField(string name, object value) => typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        static CameraFrame Frame(DateTimeOffset time)
        {
            const int width = 320, height = 240;
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)128);
            for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            return new(width, height, width * 4, pixels, time);
        }
    }
}
#endif
