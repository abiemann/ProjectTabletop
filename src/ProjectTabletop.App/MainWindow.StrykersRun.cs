using System.Diagnostics;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
#if DEBUG
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
#endif

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly FrameWorker _strykersRunWorker = new();
    private readonly PatternRailDetector _strykersRunDetector = new();
    private readonly object _strykersRunInputGate = new();
    private long _strykersRunGeneration, _strykersRunDetectorEpoch = -1;
    private PatternRailObservation? _strykersRunLastObservation;
    private double _strykersRunProcessingMilliseconds;
#if DEBUG
    private sealed record StrykersRunDiagnosticSnapshot(CameraFrame Frame, PatternRailObservation Observation,
        PatternRailGeometry Geometry, PatternRailDiagnostics Diagnostics, long Generation, DateTimeOffset ProcessedAt);
    private StrykersRunDiagnosticSnapshot? _strykersRunDiagnosticSnapshot;
#endif

    private void OnOpenStrykersRunBoard(object sender, RoutedEventArgs args) => ShowStrykersRun();

    private void ShowStrykersRun()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowStrykersRun();
        ResetStrykersRunInput();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Stryker's Run: hold PLAY, then cover the white dots along the bottom edge and slide left or right. " +
            "Stryker shoots automatically; choose gates to recruit troops. His recruits take losses first.");
    }

    private void ResetStrykersRunInput()
    {
        Interlocked.Increment(ref _strykersRunGeneration);
        _scene.ClearStrykersRunInput();
        Volatile.Write(ref _strykersRunLastObservation, null);
        _strykersRunWorker.ResetCadence();
#if DEBUG
        ClearStrykersRunDiagnosticSnapshot();
#endif
    }

    private void QueueStrykersRunDetection(CameraFrame frame)
    {
        if (_closing || !_cameraWanted || !_camera.IsRunning || _cameraOperation.CurrentCount == 0 ||
            Volatile.Read(ref _cameraHealthWarning) || Volatile.Read(ref _boardSetupActive)) return;
        long tick = Stopwatch.GetTimestamp();
        if (!_strykersRunWorker.CanRun(tick, TimeSpan.Zero)) return;
        // Arrival precedes conversion/copy; measured optical delay also keeps a
        // just-finished slide from being learned as the stationary dot matrix.
        var visibleThrough = (frame.ArrivedAt ?? frame.Timestamp).Subtract(
            TimeSpan.FromTicks(Volatile.Read(ref _projectionReferenceMinimumAgeTicks)));
        var geometry = _scene.GetStrykersRunRailGeometry(frame.Timestamp, visibleThrough);
        if (geometry is null) return;
        long generation = Interlocked.Read(ref _strykersRunGeneration);
        _strykersRunWorker.TryRun(tick, TimeSpan.Zero, _ =>
        {
            try
            {
                if (_closing || generation != Interlocked.Read(ref _strykersRunGeneration) ||
                    Volatile.Read(ref _cameraHealthWarning)) return;
                var now = MonotonicClock.UtcNow;
                if (frame.Timestamp > now || now - frame.Timestamp > StrykersRunGame.InputFreshness) return;
                if (!ReferenceEquals(geometry, _scene.GetStrykersRunRailGeometry(frame.Timestamp, visibleThrough))) return;
                PatternRailObservation observation;
                long started = Stopwatch.GetTimestamp();
                lock (_strykersRunInputGate)
                {
                    if (generation != Interlocked.Read(ref _strykersRunGeneration)) return;
                    if (_strykersRunDetectorEpoch != geometry.Revision)
                    {
                        _strykersRunDetector.Reset();
                        _strykersRunDetectorEpoch = geometry.Revision;
                    }
                    observation = _strykersRunDetector.Observe(frame.Bgra, frame.Width, frame.Height, frame.Stride,
                        geometry, frame.Timestamp);
#if DEBUG
                    _strykersRunDiagnosticSnapshot = _boardHandDiagnosticLogging && !_closing &&
                        generation == Interlocked.Read(ref _strykersRunGeneration)
                        ? new(frame, observation, geometry, _strykersRunDetector.GetDiagnostics(), generation, MonotonicClock.UtcNow)
                        : null;
#endif
                }
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                now = MonotonicClock.UtcNow;
                if (_closing || generation != Interlocked.Read(ref _strykersRunGeneration) ||
                    Volatile.Read(ref _cameraHealthWarning) ||
                    frame.Timestamp > now || now - frame.Timestamp > StrykersRunGame.InputFreshness) return;
                if (!_scene.SetStrykersRunRailPosition(observation.Position, frame.Timestamp, geometry.Revision)) return;
                Volatile.Write(ref _strykersRunLastObservation, observation);
                Volatile.Write(ref _strykersRunProcessingMilliseconds, milliseconds);
            }
            catch (Exception exception)
            {
                AppLog.Write("Stryker's Run control strip", exception);
                if (!_closing && generation == Interlocked.Read(ref _strykersRunGeneration) &&
                    !Volatile.Read(ref _cameraHealthWarning))
                    _scene.SetStrykersRunRailPosition(null, frame.Timestamp, geometry.Revision);
            }
        });
    }

    private object StrykersRunControlState()
    {
        var state = _scene.StrykersRunState;
        return new
        {
            phase = state.Phase.ToString(), state.Banner, state.SquadX, state.SoldierCount, state.TroopCount,
            state.HeroAlive, state.Score, state.Wave, state.Distance, state.RunSeconds, state.GameOverSeconds,
            state.RecoveringInput, enemies = state.Enemies.Count, gates = state.Gates.Count, bullets = state.Bullets.Count,
            input = Volatile.Read(ref _strykersRunLastObservation),
            processingMilliseconds = Volatile.Read(ref _strykersRunProcessingMilliseconds),
            railCameraCorners = _scene.GetStrykersRunRailGeometry()?.CameraCorners,
            inputRevision = _scene.StrykersRunInputRevision,
            railProgress = _scene.StrykersRunRailProgress,
            railAnimating = _scene.StrykersRunRailAnimating,
            inputActive = _scene.StrykersRunInputActive
        };
    }

    private async Task DisposeStrykersRunInputAsync()
    {
        Interlocked.Increment(ref _strykersRunGeneration);
        await _strykersRunWorker.WorkerTask;
#if DEBUG
        ClearStrykersRunDiagnosticSnapshot();
#endif
    }

#if DEBUG
    private void ClearStrykersRunDiagnosticSnapshot()
    {
        lock (_strykersRunInputGate) _strykersRunDiagnosticSnapshot = null;
    }

    private async Task<object> CaptureStrykersRunInputAsync()
    {
        StrykersRunDiagnosticSnapshot snapshot;
        lock (_strykersRunInputGate)
        {
            if (!_boardHandDiagnosticLogging)
                throw new InvalidOperationException("Enable board diagnostic logging before capturing Stryker's Run input.");
            snapshot = _strykersRunDiagnosticSnapshot ??
                throw new InvalidOperationException("No processed Stryker's Run rail frame is available yet.");
            if (snapshot.Generation != Interlocked.Read(ref _strykersRunGeneration))
                throw new InvalidOperationException("The retained rail frame belongs to a reset input session.");
        }
        var frame = snapshot.Frame;
        string directory = Path.Combine(_appDataDirectory, "StrykersRunInputSnapshots",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow, snapshotKind = "processed-pattern-rail",
            frame.Timestamp, frame.ArrivedAt, frame.Width, frame.Height, frame.Stride,
            snapshot.Generation, snapshot.ProcessedAt, snapshot.Geometry, snapshot.Observation, snapshot.Diagnostics,
            // This is capture-time state, distinct from the exact processed frame and detector tuple above.
            currentState = StrykersRunControlState(), latestCameraFrameUtc = Volatile.Read(ref _latestCameraFrame)?.Timestamp
        });
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "camera.bgra"), frame.Bgra);
        using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), frame.Bgra,
            frame.Width, frame.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
        await bitmap.SaveAsync(Path.Combine(directory, "camera.png"), CanvasBitmapFileFormat.Png);
        await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), metadata);
        return new { directory, frame.Timestamp, snapshot.Generation, inputRevision = snapshot.Geometry.Revision,
            snapshot.Observation.Reason, snapshot.Observation.Position };
    }
#endif
}
