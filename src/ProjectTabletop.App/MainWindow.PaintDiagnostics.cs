#if DEBUG
using System.Text.Json;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private sealed record PaintDiagnosticObservation(CameraFrame Frame, PaintDisturbanceScene Scene,
        PaintDisturbanceResult Result);
    private PaintDiagnosticObservation? _lastPaintDiagnostic, _lastConfirmedPaintDiagnostic;

    // Retain two bounded immutable observations in memory. Export only on request,
    // preserving the exact camera frame and expected history used for the decision.
    private async Task<object> SavePaintDiagnosticsAsync()
    {
        var current = Volatile.Read(ref _lastPaintDiagnostic) ??
            throw new InvalidOperationException("No Paint camera comparison yet.");
        var confirmed = Volatile.Read(ref _lastConfirmedPaintDiagnostic);
        string directory = Path.Combine(_appDataDirectory, "PaintDiagnostics",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await Save("current", current);
        if (confirmed is not null) await Save("confirmed", confirmed);
        return new { directory, hasConfirmed = confirmed is not null };

        async Task Save(string name, PaintDiagnosticObservation observation)
        {
            var frame = observation.Frame;
            var scene = observation.Scene;
            await File.WriteAllBytesAsync(Path.Combine(directory, name + ".camera.bgra"), frame.Bgra);
            var history = new List<object>();
            for (int index = 0; index < scene.ExpectedHistory.Count; index++)
            {
                var expected = scene.ExpectedHistory[index];
                string file = $"{name}.expected-{index}.bgra";
                await File.WriteAllBytesAsync(Path.Combine(directory, file), expected.Bgra);
                history.Add(new { expected.Width, expected.Height, expected.PresentedAt, file });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
            {
                frame.Timestamp, frame.Width, frame.Height, frame.Stride,
                scene.Revision, scene.CameraToBoard, scene.PaintBounds, scene.IgnoredRegions,
                expectedHistory = history, result = observation.Result
            }));
        }
    }
}
#endif
