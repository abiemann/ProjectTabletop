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
    private long _strykersRunGeneration;
    private PatternRailGeometry? _patternRailDetectorGeometry;
    private PatternRailObservation? _strykersRunLastObservation;
    private double _strykersRunProcessingMilliseconds;
    private PatternRailObservation? _globePitchLastObservation;
    private double _globePitchProcessingMilliseconds;
#if DEBUG
    private sealed record StrykersRunDiagnosticSnapshot(CameraFrame Frame, PatternRailObservation Observation,
        PatternRailGeometry Geometry, PatternRailDiagnostics Diagnostics, long Generation, DateTimeOffset ProcessedAt)
    {
        public CameraFrameLease FrameLease { get; } = Frame.Retain();
    }
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
            "Stryker shoots automatically. Shoot open Bulwark's pod for a monster defender, break supply caches for recruits, " +
            "and choose gates to grow the squad. Survive five one-minute stages and defeat their bosses; " +
            "the road stops for each boss fight. Survivors continue to the next level.");
    }

    private void ResetStrykersRunInput()
    {
        Interlocked.Increment(ref _strykersRunGeneration);
        _scene.ClearStrykersRunInput();
        _scene.ClearGlobePitchInput();
        Volatile.Write(ref _strykersRunLastObservation, null);
        Volatile.Write(ref _globePitchLastObservation, null);
        _strykersRunWorker.ResetCadence();
#if DEBUG
        ClearStrykersRunDiagnosticSnapshot();
#endif
    }

    // Stryker's Run and Globe never share the screen, so they share the same
    // independent worker and detector instead of copying or queuing camera frames.
    private void QueuePatternRailDetection(CameraFrame frame)
    {
        if (_closing || !_cameraWanted || !_camera.IsRunning || _cameraOperation.CurrentCount == 0 ||
            Volatile.Read(ref _cameraHealthWarning) || Volatile.Read(ref _boardSetupActive)) return;
        long tick = Stopwatch.GetTimestamp();
        if (!_strykersRunWorker.CanRun(tick, TimeSpan.Zero)) return;
        // Arrival precedes conversion/copy; measured optical delay also keeps a
        // just-finished slide from being learned as the stationary dot matrix.
        var visibleThrough = (frame.ArrivedAt ?? frame.Timestamp).Subtract(
            TimeSpan.FromTicks(Volatile.Read(ref _projectionReferenceMinimumAgeTicks)));
        var screen = _scene.CurrentBoardScreen;
        PatternRailGeometry? geometry = null;
        long generation = Interlocked.Read(ref _strykersRunGeneration);
        _strykersRunWorker.TryRun(tick, TimeSpan.Zero, frame, _ =>
        {
            try
            {
                if (_closing || generation != Interlocked.Read(ref _strykersRunGeneration) ||
                    Volatile.Read(ref _cameraHealthWarning)) return;
                var now = MonotonicClock.UtcNow;
                if (frame.Timestamp > now || now - frame.Timestamp > StrykersRunGame.InputFreshness) return;
                // Resolve calibrated geometry on this worker, never behind a
                // compositor/GPU lock on the camera delivery thread.
                if (_scene.CurrentBoardScreen != screen) return;
                geometry = CurrentGeometry();
                if (geometry is null) return;
                PatternRailObservation observation;
                long started = Stopwatch.GetTimestamp();
                lock (_strykersRunInputGate)
                {
                    if (generation != Interlocked.Read(ref _strykersRunGeneration)) return;
                    // Epoch numbers are local to each board. Object identity
                    // also forces new optical registration when boards switch.
                    if (!ReferenceEquals(_patternRailDetectorGeometry, geometry))
                    {
                        _strykersRunDetector.Reset();
                        _patternRailDetectorGeometry = geometry;
                    }
                    observation = _strykersRunDetector.Observe(frame.Bgra, frame.Width, frame.Height, frame.Stride,
                        geometry, frame.Timestamp);
#if DEBUG
                    var previous = _strykersRunDiagnosticSnapshot;
                    _strykersRunDiagnosticSnapshot = screen == BoardScreen.StrykersRun &&
                        _boardHandDiagnosticLogging && !_closing &&
                        generation == Interlocked.Read(ref _strykersRunGeneration)
                        ? new(frame, observation, geometry, _strykersRunDetector.GetDiagnostics(), generation, MonotonicClock.UtcNow)
                        : null;
                    previous?.FrameLease.Dispose();
#endif
                }
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                now = MonotonicClock.UtcNow;
                if (_closing || generation != Interlocked.Read(ref _strykersRunGeneration) ||
                    Volatile.Read(ref _cameraHealthWarning) ||
                    frame.Timestamp > now || now - frame.Timestamp > StrykersRunGame.InputFreshness) return;
                if (!Publish(observation.Position)) return;
                if (screen == BoardScreen.Globe)
                {
                    Volatile.Write(ref _globePitchLastObservation, observation);
                    Volatile.Write(ref _globePitchProcessingMilliseconds, milliseconds);
                }
                else
                {
                    Volatile.Write(ref _strykersRunLastObservation, observation);
                    Volatile.Write(ref _strykersRunProcessingMilliseconds, milliseconds);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write(screen == BoardScreen.Globe ? "Globe pitch strip" : "Stryker's Run control strip", exception);
                if (!_closing && generation == Interlocked.Read(ref _strykersRunGeneration) &&
                    !Volatile.Read(ref _cameraHealthWarning))
                    Publish(null);
            }
        });

        PatternRailGeometry? CurrentGeometry() => screen switch
        {
            BoardScreen.StrykersRun => _scene.GetStrykersRunRailGeometry(frame.Timestamp, visibleThrough),
            BoardScreen.Globe => _scene.GetGlobePitchRailGeometry(frame.Timestamp, visibleThrough),
            _ => null
        };

        bool Publish(float? position) => geometry is not null && _scene.CurrentBoardScreen == screen && (screen switch
        {
            BoardScreen.StrykersRun => _scene.SetStrykersRunRailPosition(position, frame.Timestamp, geometry.Revision),
            BoardScreen.Globe => _scene.SetGlobePitchRailPosition(position, frame.Timestamp, geometry.Revision),
            _ => false
        });
    }

    private object GlobePitchInputState() => new
    {
        input = Volatile.Read(ref _globePitchLastObservation),
        processingMilliseconds = Volatile.Read(ref _globePitchProcessingMilliseconds),
        railCameraCorners = _scene.GetGlobePitchRailGeometry()?.CameraCorners
    };

    private object StrykersRunControlState()
    {
        var state = _scene.StrykersRunState;
        return new
        {
            phase = state.Phase.ToString(), state.Banner, state.SquadX, state.SoldierCount, state.TroopCount,
            state.HeroAlive, state.Score, state.Wave, state.Distance, state.RunSeconds, state.GameOverSeconds,
            state.RecoveringInput, enemies = state.Enemies.Count, gates = state.Gates.Count, bullets = state.Bullets.Count,
            state.Level, state.LevelSeconds, stage = state.Stage.ToString(), state.StageSeconds,
            state.Boss, state.Hazards, state.Ally, state.Objectives,
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
        lock (_strykersRunInputGate)
        {
            var previous = _strykersRunDiagnosticSnapshot;
            _strykersRunDiagnosticSnapshot = null;
            previous?.FrameLease.Dispose();
        }
    }

    private async Task<object> CaptureStrykersRunInputAsync()
    {
        StrykersRunDiagnosticSnapshot snapshot;
        CameraFrameLease retainedFrame;
        lock (_strykersRunInputGate)
        {
            if (!_boardHandDiagnosticLogging)
                throw new InvalidOperationException("Enable board diagnostic logging before capturing Stryker's Run input.");
            snapshot = _strykersRunDiagnosticSnapshot ??
                throw new InvalidOperationException("No processed Stryker's Run rail frame is available yet.");
            if (snapshot.Generation != Interlocked.Read(ref _strykersRunGeneration))
                throw new InvalidOperationException("The retained rail frame belongs to a reset input session.");
            retainedFrame = snapshot.Frame.Retain();
        }
        using var frameLease = retainedFrame;
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
