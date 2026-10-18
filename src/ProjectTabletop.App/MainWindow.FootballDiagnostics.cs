using System.Text.Json;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private sealed record FootballDiagnosticObservation(CameraFrame Frame,
        IReadOnlyList<MarkerProjectionFrame>? References, int[] Players, BlackTipProfile[] Profiles,
        BlackTipSearchHint?[] Hints, IReadOnlyList<MarkerTipDetectionResult> Results,
        string[] Decisions, double?[] InputAgeMilliseconds, PixelPoint[]? SearchArea)
    {
        public CameraFrameLease FrameLease { get; } = Frame.Retain();
    }
    private readonly object _footballDiagnosticGate = new();
    private bool _footballDiagnosticCapture;
    private FootballDiagnosticObservation? _currentFootballDiagnostic, _missedFootballDiagnostic;

    private object SetFootballDiagnosticCapture(bool enabled)
    {
        lock (_footballDiagnosticGate)
        {
            _footballDiagnosticCapture = enabled;
            ClearFootballDiagnosticFrames();
        }
        return new { enabled };
    }

    private void ClearFootballDiagnosticFrames()
    {
        lock (_footballDiagnosticGate)
        {
            _currentFootballDiagnostic?.FrameLease.Dispose();
            _missedFootballDiagnostic?.FrameLease.Dispose();
            _currentFootballDiagnostic = _missedFootballDiagnostic = null;
        }
    }

    private void RecordFootballDetection(CameraFrame frame, IReadOnlyList<MarkerProjectionFrame>? references,
        int[] players, BlackTipProfile[] profiles, BlackTipSearchHint?[] hints,
        IReadOnlyList<MarkerTipDetectionResult> results, string[] decisions, double?[] inputAgeMilliseconds,
        PixelPoint[]? searchArea)
    {
        lock (_footballDiagnosticGate)
        {
            if (!_footballDiagnosticCapture) return;
            // Frame leases keep the pooled pixels immutable. Retain only two
            // observations, and write nothing unless the local diagnostic client requests it.
            var observation = new FootballDiagnosticObservation(frame, references, players, profiles, hints, results,
                decisions, inputAgeMilliseconds, searchArea);
            var previous = _currentFootballDiagnostic;
            _currentFootballDiagnostic = observation;
            previous?.FrameLease.Dispose();
            // A full-pair miss bridged by one strip is still successful tracking.
            // Preserve a sustained measured-input miss or an immediate ambiguity
            // disarm, not that benign recovery. Clearing input also clears its age.
            if (_missedFootballDiagnostic is null && decisions.Where((decision, index) =>
                    decision == "ambiguous" || hints[index] is not null && inputAgeMilliseconds[index] >= 100 &&
                    decision is "missing" or "confirming" or "uncertain-strips" or "stale-frame" or "publication-frame-rejected" or "waiting-reference").Any())
                _missedFootballDiagnostic = new(frame, references, players, profiles, hints, results,
                    decisions, inputAgeMilliseconds, searchArea);
        }
    }

    private async Task<object> SaveFootballDiagnosticsAsync()
    {
        FootballDiagnosticObservation current;
        FootballDiagnosticObservation? missed;
        CameraFrameLease currentFrame;
        CameraFrameLease? missedFrame;
        lock (_footballDiagnosticGate)
        {
            current = _currentFootballDiagnostic ?? throw new InvalidOperationException(
                "Enable Football diagnostic capture and wait for a marker comparison first.");
            missed = _missedFootballDiagnostic;
            currentFrame = current.Frame.Retain();
            missedFrame = missed?.Frame.Retain();
        }
        using var currentLease = currentFrame;
        using var missedLease = missedFrame;
        string directory = Path.Combine(_appDataDirectory, "FootballDiagnostics",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await Save("current", current);
        if (missed is not null) await Save("missed", missed);
        return new { directory, hasMissed = missed is not null };

        async Task Save(string name, FootballDiagnosticObservation observation)
        {
            var frame = observation.Frame;
            await File.WriteAllBytesAsync(Path.Combine(directory, name + ".camera.bgra"), frame.Bgra);
            var history = new List<object>();
            int index = 0;
            foreach (var reference in observation.References ?? [])
            {
                string file = $"{name}.expected-{index++}.bgra";
                await File.WriteAllBytesAsync(Path.Combine(directory, file), reference.Bgra);
                history.Add(new { reference.Width, reference.Height, reference.RenderedAt,
                    reference.CameraToBoard, file });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
            {
                frame.Timestamp, frame.Width, frame.Height, frame.Stride,
                observation.Players, observation.Profiles, observation.Hints, observation.Results,
                observation.Decisions, observation.InputAgeMilliseconds, observation.SearchArea,
                expectedHistory = history
            }));
        }
    }
}
