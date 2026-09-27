#if DEBUG
using System.Text.Json;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private sealed record PhotoCopyFailure(CameraFrame Frame, HandDetection[] Hands,
        SceneCompositor.PhotoCopyCaptureContext Context, string? Failure);
    private PhotoCopyFailure? _lastPhotoCopyFailure;

    // Retain only the latest failed observation in memory. A diagnostic is
    // written only on explicit request, with the exact target and camera frame
    // that disagreed, rather than a later photograph of a different lock.
    private async Task<object> SavePhotoCopyDiagnosticsAsync()
    {
        var failed = _lastPhotoCopyFailure;
        var observation = _latestPhotoCopyFrame ?? throw new InvalidOperationException("No Photo Copy camera frame.");
        if (!_scene.TryGetPhotoCopyCaptureContext(out var current))
            throw new InvalidOperationException("Photo Copy is not ready.");
        string directory = Path.Combine(_appDataDirectory, "PhotoCopyDiagnostics",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await Save("current", observation.Frame, observation.Hands, current, null);
        if (failed is not null) await Save("failure", failed.Frame, failed.Hands, failed.Context, failed.Failure);
        return new { directory, hasFailure = failed is not null };

        async Task Save(string name, CameraFrame frame, HandDetection[] hands,
            SceneCompositor.PhotoCopyCaptureContext context, string? failure)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, name + ".bgra"), frame.Bgra);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
            {
                frame.Timestamp, frame.Width, frame.Height, frame.Stride, context, hands, failure
            }));
        }
    }

    // Exercise the live delayed-capture path independently of gesture input.
    private async Task<object> CapturePhotoCopyForVerificationAsync()
    {
        if (_photoCopyTask is { IsCompleted: false } ||
            !_scene.TryGetPhotoCopyCaptureContext(out var context) || context.Target is null)
            throw new InvalidOperationException("Wait for a locked object before testing Copy.");
        string? previous = _lastSavedPhotoPath;
        _photoCopyTask = CaptureDelayedPhotoCopyAsync(context, _handGeneration);
        await _photoCopyTask;
        return new { saved = _lastSavedPhotoPath != previous, path = _lastSavedPhotoPath, status = _scene.PhotoCopyStatus };
    }
}
#endif
