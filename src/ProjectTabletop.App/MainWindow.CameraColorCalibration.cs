using System.Diagnostics;
using System.Numerics;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Settings;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _cameraColorCalibrationCancellation;
    private Task? _cameraColorCalibrationTask;
    private bool _cameraColorCalibrationAttempted;
    private string _cameraColorCalibrationStatus = "Not measured.";
    private BoardColorCalibrationResult? _cameraColorCalibrationBefore, _cameraColorCalibrationAfter;
    private int _cameraColorCalibrationSamples, _cameraColorCalibrationAccepted;
    private double? _cameraColorCalibrationExposure, _cameraColorCalibrationWhiteBalance;

    private async Task FinishCameraColorCalibrationAsync()
    {
        if (_cameraColorCalibrationTask is not { IsCompleted: false } pending) return;
        if ((BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) == BoardSetupPhase.ColorCalibration)
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _cameraColorCalibrationCancellation?.Cancel();
        await pending;
    }

    private void ResetCameraColorCalibration()
    {
        _cameraColorCalibrationCancellation?.Cancel();
        _cameraColorCalibrationCancellation = null;
        _cameraColorCalibrationAttempted = false;
        _cameraColorCalibrationStatus = "Not measured.";
        _cameraColorCalibrationBefore = _cameraColorCalibrationAfter = null;
        _cameraColorCalibrationSamples = _cameraColorCalibrationAccepted = 0;
        _cameraColorCalibrationExposure = _cameraColorCalibrationWhiteBalance = null;
    }

    private bool TryBeginCameraColorCalibration(CameraFrame frame, BoardDetection board, Vector2[] corners)
    {
        if (_cameraColorCalibrationAttempted) return false;
        _cameraColorCalibrationAttempted = true;
        if (_camera.ActiveDeviceId is not { } device) return false;
        var boardToCamera = Homography.FromFourPoints(
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
            board.Corners.Select(point => new Point2(point.X, point.Y)).ToArray());
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.ColorCalibration);
        _scene.ShowBoardCalibrationSpot(-1);
        _scene.ShowSetupCorners(corners);
        _cameraColorCalibrationStatus = "Checking webcam exposure and colour from the board strips. Keep the cardboard still.";
        BoardSetupStatusText.Text = _cameraColorCalibrationStatus;
        _cameraColorCalibrationTask = CalibrateCameraColorAsync(frame.Width, frame.Height, boardToCamera, device,
            _camera.CaptureEpoch, Interlocked.Read(ref _boardSetupGeneration));
        return true;
    }

    private async Task CalibrateCameraColorAsync(int width, int height, Homography boardToCamera,
        string device, long epoch, long generation)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _cameraColorCalibrationCancellation = cancellation;
        var token = cancellation.Token;
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset readyAfter = MonotonicClock.UtcNow + SetupCornersSettle;
        double? exposureToRestore = null, whiteBalanceToRestore = null;
        double? exposureTrialValue = null, whiteBalanceTrialValue = null;
        Task<CameraCaptureService.CameraManualControlResult>? pendingWrite = null;
        bool SameSession() => _camera.IsRunning && _camera.ActiveDeviceId == device && _camera.CaptureEpoch == epoch;
        bool Current() => !_closing && Volatile.Read(ref _boardSetupActive) &&
            generation == Interlocked.Read(ref _boardSetupGeneration) &&
            (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) == BoardSetupPhase.ColorCalibration &&
            SameSession() && !_cameraManualRestartRequired && !_boardScanSuspendedForCameraOutage;
        void RequireCurrent(CancellationToken? operationToken = null)
        {
            (operationToken ?? token).ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException();
        }
        bool CanTrial() => _cameraColorCalibrationSamples < 8 &&
            Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(5500);
        PixelPoint Map(PixelPoint point)
        {
            var mapped = boardToCamera.Transform(new(point.X, point.Y));
            return new(mapped.X, mapped.Y);
        }

        try
        {
            var controls = await _camera.GetControlsAsync().WaitAsync(TimeSpan.FromMilliseconds(1200), token);
            RequireCurrent();
            var exposure = controls.Status == "queried" && controls.Epoch == epoch
                ? PictureControl(controls, "legacyExposure") : null;
            var whiteBalance = controls.Status == "queried" && controls.Epoch == epoch
                ? PictureControl(controls, "legacyWhiteBalance") : null;
            _cameraColorCalibrationExposure = exposure?.Value;
            _cameraColorCalibrationWhiteBalance = whiteBalance?.Value;
            double fps = _camera.NegotiatedFormat?.FramesPerSecond ?? 0;
            double maximumExposure = fps > 0 ? Math.Floor(Math.Log2(1 / fps)) : double.NegativeInfinity;
            var best = await SampleAsync();
            _cameraColorCalibrationBefore = _cameraColorCalibrationAfter = best;
            if (best.IsUsable)
            {
                // One advertised exposure step per observation, at most three trials.
                // No brightness/contrast changes: persisted picture profiles retain their meaning.
                for (int attempt = 0; exposure is not null && best.ExposureDirection != 0 &&
                    attempt < 3 && CanTrial(); attempt++)
                {
                    double previous = exposure.Value;
                    double target = NextControlValue(exposure, best.ExposureDirection, exposure.Step);
                    if (target == previous || target > maximumExposure && target > previous) break;
                    exposureToRestore = previous;
                    exposureTrialValue = target;
                    if (!await WriteAsync(target, null)) { exposureToRestore = null; break; }
                    var trial = await SampleAsync();
                    if (trial.IsUsable && trial.ExposureScore + 2 < best.ExposureScore)
                    {
                        best = trial;
                        _cameraColorCalibrationAfter = best;
                        exposure = exposure with { Value = target };
                        _cameraColorCalibrationExposure = target;
                        _cameraColorCalibrationAccepted++;
                        exposureToRestore = null;
                    }
                    else
                    {
                        if (!await WriteAsync(previous, null))
                            throw new InvalidOperationException("The camera did not confirm its best exposure setting.");
                        exposureToRestore = null;
                        break;
                    }
                }

                // A Kelvin control cannot correct a green cast or clipped neutral patches.
                for (int attempt = 0; whiteBalance is not null && best.WhiteBalanceDirection != 0 &&
                    attempt < 3 && CanTrial(); attempt++)
                {
                    double previous = whiteBalance.Value;
                    double step = Math.Ceiling(300 / whiteBalance.Step) * whiteBalance.Step;
                    if (step > 500) break;
                    double target = NextControlValue(whiteBalance, best.WhiteBalanceDirection, step);
                    if (Math.Abs(target - previous) < 200) break;
                    whiteBalanceToRestore = previous;
                    whiteBalanceTrialValue = target;
                    if (!await WriteAsync(null, target)) { whiteBalanceToRestore = null; break; }
                    var trial = await SampleAsync();
                    if (trial.IsUsable && trial.WhiteBalanceScore + .006 < best.WhiteBalanceScore &&
                        trial.ExposureScore <= best.ExposureScore + 8 &&
                        trial.ClippedFraction <= best.ClippedFraction + .01)
                    {
                        best = trial;
                        _cameraColorCalibrationAfter = best;
                        whiteBalance = whiteBalance with { Value = target };
                        _cameraColorCalibrationWhiteBalance = target;
                        _cameraColorCalibrationAccepted++;
                        whiteBalanceToRestore = null;
                    }
                    else
                    {
                        if (!await WriteAsync(null, previous))
                            throw new InvalidOperationException("The camera did not confirm its best white balance setting.");
                        whiteBalanceToRestore = null;
                        break;
                    }
                }
            }
            _cameraColorCalibrationAfter = best;
            _cameraColorCalibrationStatus = !best.IsUsable
                ? "Colour strips were not clear enough to adjust the webcam; current settings retained."
                : exposure is null && whiteBalance is null
                ? "Colour strips measured. Compatible manual webcam controls are unavailable."
                : _cameraColorCalibrationAccepted > 0
                ? "Webcam exposure and colour checked; improved settings retained."
                : "Webcam exposure and colour checked; current settings retained.";

            // Always recapture the dot reference after the last write/revert and measure
            // all five dots again, so timing belongs to the final camera settings.
            using var reference = await FreshFrameAsync();
            RequireCurrent();
            BeginRegistrationSpots(reference.Frame);
        }
        catch (OperationCanceledException)
        {
            bool restored = await RestoreUnmeasuredTrialAsync();
            if (Current())
            {
                if (restored && await ResumeAfterInconclusiveCalibrationAsync()) return;
                if (Current()) Fail("Webcam colour calibration timed out. Press Scan again.");
            }
        }
        catch (TimeoutException)
        {
            bool restored = await RestoreUnmeasuredTrialAsync();
            if (Current())
            {
                if (restored && await ResumeAfterInconclusiveCalibrationAsync()) return;
                if (Current()) Fail("Webcam colour calibration could not get a fresh verified camera view. Press Scan again.");
            }
        }
        catch (Exception ex)
        {
            await RestoreUnmeasuredTrialAsync();
            if (Current())
            {
                Fail("Webcam colour calibration stopped: " + ex.Message + " Press Scan again.");
            }
        }
        finally
        {
            if (ReferenceEquals(_cameraColorCalibrationCancellation, cancellation))
                _cameraColorCalibrationCancellation = null;
        }

        void Fail(string message)
        {
            _cameraColorCalibrationStatus = _boardSetupDiagnostic = message;
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Failed);
            ResetCameraLatency("Estimated Visual Latency: unavailable because webcam calibration did not finish. Scan again.");
            BoardSetupStatusText.Text = message;
        }

        async Task<bool> RestoreUnmeasuredTrialAsync()
        {
            if (exposureToRestore is null && whiteBalanceToRestore is null)
                return pendingWrite is not { IsCompleted: false };
            if (!SameSession()) return false;
            using var restoreCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            try
            {
                bool restored = await WriteAsync(exposureToRestore, whiteBalanceToRestore,
                    restoreCancellation.Token, restoreObsoleteTrial: true);
                if (!restored && Current())
                    _cameraColorCalibrationExposure = _cameraColorCalibrationWhiteBalance = null;
                return restored;
            }
            catch
            {
                if (Current()) _cameraColorCalibrationExposure = _cameraColorCalibrationWhiteBalance = null;
                return false;
            }
        }

        async Task<bool> ResumeAfterInconclusiveCalibrationAsync()
        {
            if (!Current() || _cameraHealthWarning || pendingWrite is { IsCompleted: false }) return false;
            using var resumeCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            try
            {
                using var frame = await FreshFrameAsync(resumeCancellation.Token);
                RequireCurrent(resumeCancellation.Token);
                _cameraColorCalibrationStatus = "Webcam colour check was inconclusive; best verified settings retained.";
                BeginRegistrationSpots(frame.Frame);
                return true;
            }
            catch { return false; }
        }

        async Task<CameraFrameLease> FreshFrameAsync(CancellationToken? operationToken = null)
        {
            var frameToken = operationToken ?? token;
            RequireCurrent(frameToken);
            var delay = readyAfter - MonotonicClock.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, frameToken);
            long waiting = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(waiting) < TimeSpan.FromMilliseconds(700))
            {
                RequireCurrent(frameToken);
                var lease = AcquireLatestCameraFrame();
                if (lease is not null)
                {
                    var frame = lease.Frame;
                    if (frame.Width == width && frame.Height == height &&
                        (frame.ArrivedAt ?? frame.Timestamp) >= readyAfter &&
                        MonotonicClock.UtcNow - frame.Timestamp < TimeSpan.FromMilliseconds(350)) return lease;
                    lease.Dispose();
                }
                await Task.Delay(30, frameToken);
            }
            throw new TimeoutException("No fresh settled camera frame arrived.");
        }

        async Task<BoardColorCalibrationResult> SampleAsync()
        {
            using var lease = await FreshFrameAsync();
            var frame = lease.Frame;
            var measured = await Task.Run(() => BoardColorCalibration.Analyze(
                frame.Width, frame.Height, frame.Stride, frame.Bgra, Map), token);
            RequireCurrent();
            _cameraColorCalibrationSamples++;
            return measured;
        }

        async Task<bool> WriteAsync(double? nextExposure, double? nextWhiteBalance,
            CancellationToken? operationToken = null, bool restoreObsoleteTrial = false)
        {
            var writeToken = operationToken ?? token;
            void RequireWriteSession()
            {
                if (!restoreObsoleteTrial) RequireCurrent(writeToken);
                else
                {
                    writeToken.ThrowIfCancellationRequested();
                    if (!SameSession()) throw new OperationCanceledException();
                }
            }
            RequireWriteSession();
            if (!await _cameraOperation.WaitAsync(TimeSpan.FromMilliseconds(350), writeToken)) return false;
            Task<CameraCaptureService.CameraManualControlResult>? pending = null;
            try
            {
                RequireWriteSession();
                if (restoreObsoleteTrial)
                {
                    // A new scan waits for this cleanup. A manual change made while
                    // cancellation was pending still wins over our obsolete trial.
                    var snapshot = await _camera.GetControlsAsync().WaitAsync(TimeSpan.FromMilliseconds(600), writeToken);
                    RequireWriteSession();
                    if (snapshot.Status != "queried" || snapshot.Epoch != epoch ||
                        !Restorable("legacyExposure", nextExposure, exposureTrialValue) ||
                        !Restorable("legacyWhiteBalance", nextWhiteBalance, whiteBalanceTrialValue)) return false;
                    bool Restorable(string name, double? bestValue, double? trialValue) =>
                        bestValue is null || PictureControl(snapshot, name)?.Value is { } actual &&
                        (actual == bestValue || actual == trialValue);
                }
                pending = _camera.SetManualControlsAsync(device, epoch,
                    exposureLog2Seconds: nextExposure, whiteBalanceKelvin: nextWhiteBalance,
                    cancellationToken: writeToken);
                pendingWrite = pending;
                var result = await pending.WaitAsync(TimeSpan.FromMilliseconds(1200), writeToken);
                RequireWriteSession();
                if (result.Changed)
                    readyAfter = MonotonicClock.UtcNow + TimeSpan.FromMilliseconds(450);
                if (result.Status is not ("applied" or "unchanged"))
                {
                    if (result.Changed && !result.RollbackSucceeded)
                        throw new InvalidOperationException("The driver could not verify its camera settings.");
                    return false;
                }
                return result.Epoch == epoch &&
                    (nextExposure is null || result.After.ExposureLog2Seconds == nextExposure) &&
                    (nextWhiteBalance is null || result.After.WhiteBalanceKelvin == nextWhiteBalance);
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
            finally
            {
                // A native driver call cannot be forcibly interrupted. Keep admission
                // paused until it exits; cancellation asks its setter to roll back.
                if (pending is { IsCompleted: false })
                    _ = pending.ContinueWith(_ => _cameraOperation.Release(), TaskScheduler.Default);
                else _cameraOperation.Release();
            }
        }
    }

    private static double NextControlValue(CameraPictureControl control, int direction, double delta)
    {
        double value = Math.Clamp(control.Value + direction * delta, control.Minimum, control.Maximum);
        return control.Minimum + Math.Round((value - control.Minimum) / control.Step) * control.Step;
    }

    private object GetCameraColorCalibrationStatus() => new
    {
        attempted = _cameraColorCalibrationAttempted,
        running = (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) == BoardSetupPhase.ColorCalibration,
        status = _cameraColorCalibrationStatus,
        samples = _cameraColorCalibrationSamples,
        acceptedChanges = _cameraColorCalibrationAccepted,
        exposureLog2Seconds = _cameraColorCalibrationExposure,
        whiteBalanceKelvin = _cameraColorCalibrationWhiteBalance,
        validEdges = _cameraColorCalibrationAfter?.ValidEdges,
        before = _cameraColorCalibrationBefore is { IsUsable: true } ? _cameraColorCalibrationBefore : null,
        after = _cameraColorCalibrationAfter is { IsUsable: true } ? _cameraColorCalibrationAfter : null
    };
}
