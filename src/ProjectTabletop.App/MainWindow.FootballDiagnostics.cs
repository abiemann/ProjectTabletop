using System.Text.Json;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private sealed record FootballDiagnosticObservation(CameraFrame Frame,
        IReadOnlyList<EyeTipProjectionFrame>? References, int[] Players, BlackTipProfile[] Profiles,
        BlackTipSearchHint?[] Hints, IReadOnlyList<ColorTipDetectionResult> Results,
        string[] Decisions, double?[] InputAgeMilliseconds, PixelPoint[]? SearchArea);
    private readonly object _footballDiagnosticGate = new();
    private bool _footballDiagnosticCapture;
    private FootballDiagnosticObservation? _currentFootballDiagnostic, _missedFootballDiagnostic;

    private object SetFootballDiagnosticCapture(bool enabled)
    {
        lock (_footballDiagnosticGate)
        {
            _footballDiagnosticCapture = enabled;
            _currentFootballDiagnostic = _missedFootballDiagnostic = null;
        }
        return new { enabled };
    }

    private void RecordFootballDetection(CameraFrame frame, IReadOnlyList<EyeTipProjectionFrame>? references,
        int[] players, BlackTipProfile[] profiles, BlackTipSearchHint?[] hints,
        IReadOnlyList<ColorTipDetectionResult> results, string[] decisions, double?[] inputAgeMilliseconds,
        PixelPoint[]? searchArea)
    {
        lock (_footballDiagnosticGate)
        {
            if (!_footballDiagnosticCapture) return;
            // Frames and scene references are immutable owned buffers. Retain only two
            // observations, and write nothing unless the local diagnostic client requests it.
            var observation = new FootballDiagnosticObservation(frame, references, players, profiles, hints, results,
                decisions, inputAgeMilliseconds, searchArea);
            _currentFootballDiagnostic = observation;
            // A full-pair miss bridged by one strip is still successful tracking.
            // Preserve a sustained measured-input miss, not that benign recovery.
            if (_missedFootballDiagnostic is null && decisions.Where((decision, index) =>
                    hints[index] is not null && inputAgeMilliseconds[index] >= 100 &&
                    decision is "missing" or "confirming").Any())
                _missedFootballDiagnostic = observation;
        }
    }

    private async Task<object> SaveFootballDiagnosticsAsync()
    {
        FootballDiagnosticObservation current;
        FootballDiagnosticObservation? missed;
        lock (_footballDiagnosticGate)
        {
            current = _currentFootballDiagnostic ?? throw new InvalidOperationException(
                "Enable Football diagnostic capture and wait for a marker comparison first.");
            missed = _missedFootballDiagnostic;
        }
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
