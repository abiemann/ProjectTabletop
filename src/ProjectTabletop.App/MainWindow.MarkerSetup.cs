using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using SetupStage = ProjectTabletop.Vision.MarkerSetupStage;
using PresentationStage = ProjectTabletop.App.Projection.MarkerSetupStage;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly object _markerSetupGate = new();
    private readonly FrameWorker _markerSetupWorker = new();
    private DispatcherQueueTimer? _markerSetupTimer;
    private MarkerSetupSession? _markerSetupSession;
    private long _markerSetupGeneration, _markerSetupPresentationRevision;
    private bool _markerSetupInvalidated;
    private string? _markerSetupCamera;
    private int _markerSetupTarget;
    private int _markerSetupWidth, _markerSetupHeight;
    private BoardScreen _markerSetupBoard;
    private IReadOnlyList<PixelPoint> _markerSetupRegistration = [];
    private string _markerSetupMessage = "Choose a controller, then set up its double-bar marker.";
    private MarkerTipObservation? _markerSetupMeasurement;
    private DateTimeOffset _markerSetupMeasurementTime;

    private void InitializeMarkerSetup()
    {
        _scene.BoardOpened += BoardOpenedForMarkerSetup;
        _markerSetupTimer = DispatcherQueue.CreateTimer();
        _markerSetupTimer.Interval = TimeSpan.FromMilliseconds(100);
        _markerSetupTimer.Tick += (_, _) => UpdateMarkerSetupUi();
        _markerSetupTimer.Start();
    }

    private void StartMarkerSetup_Click(object sender, RoutedEventArgs e) => BeginMarkerSetup(false);
    private void RecheckMarkerSetup_Click(object sender, RoutedEventArgs e) => BeginMarkerSetup(true);
    private void CancelMarkerSetup_Click(object sender, RoutedEventArgs e) =>
        CancelMarkerSetup("Setup cancelled. Your saved marker is unchanged.");
    private void SaveMarkerSetup_Click(object sender, RoutedEventArgs e) => SaveVerifiedMarker();

    private BlackTipProfile? MarkerSetupProfile(int target, string camera)
    {
        if (target == 0)
        {
            lock (_eyeTipGate) return _wandMarkerProfiles.GetValueOrDefault(camera);
        }
        lock (_footballInputGate) return _footballMarkerProfiles.GetValueOrDefault(camera)?.ForPlayer(target - 1);
    }

    private void SetMarkerSetupMessage(string message)
    {
        lock (_markerSetupGate) _markerSetupMessage = message;
        MarkerSetupStatusText.Text = message;
    }

    private void BeginMarkerSetup(bool recheck, bool firstUse = false)
    {
        if (_closing) return;
        MarkerSetupExpander.IsExpanded = true;
        if (!_cameraWanted || !_camera.IsRunning || _cameraHealthWarning || _cameraManualRestartRequired ||
            _camera.ActiveDeviceId is not { } camera || camera != _selectedCameraId)
        { SetMarkerSetupMessage("Start the selected camera and wait for live video."); return; }
        if (!_scene.HasBoardMediaClip || IsBoardScanMeasuring || _output is null)
        { SetMarkerSetupMessage("Open the projector and complete board setup first."); return; }
        int target = Math.Clamp(MarkerSetupTargetCombo.SelectedIndex, 0, 2);
        var existing = recheck ? MarkerSetupProfile(target, camera) : null;
        if (recheck && existing is not { IsValid: true, BarCount: 2 })
        { SetMarkerSetupMessage("Set up a double-bar marker before checking it again."); return; }

        CancelMarkerSetup(null, continueFirstUse: firstUse);
        // Select the requested board only when necessary. Existing games and the
        // current wand board are preserved when they are already open. Paint
        // and Water Garden share the same camera-specific double-bar profile.
        var board = target == 0 ? (_scene.CurrentBoardScreen == BoardScreen.Paint ? BoardScreen.Paint : BoardScreen.WaterGarden)
            : BoardScreen.Football;
        if (_scene.CurrentBoardScreen != board)
        {
            if (target == 0) ShowWaterGarden();
            else ShowFootball();
        }
        CancelFootballLearning();
        ResetEyeTipTracking("marker-setup");
        ResetFootballInput();
        _frozenFrame = null;
        using var latest = AcquireLatestCameraFrame();
        if (latest?.Frame is not { } frame || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
        { SetMarkerSetupMessage("Wait for a fresh camera frame, then try again."); return; }
        lock (_markerSetupGate)
        {
            _markerSetupTarget = target;
            _markerSetupCamera = camera;
            _markerSetupWidth = frame.Width;
            _markerSetupHeight = frame.Height;
            _markerSetupBoard = board;
            _markerSetupRegistration = _scene.GetMarkerSetupCameraRegion(new(0, 0, 1, 1));
            if (_markerSetupRegistration.Count != 4)
            { SetMarkerSetupMessage("Complete board alignment before setting up a marker."); return; }
            _markerSetupSession = new(MarkerSetupTargets(target, board), existing);
            _markerSetupFirstUseActive = firstUse;
            _markerSetupInvalidated = false;
            _markerSetupGeneration++;
            _markerSetupMeasurement = null;
            _markerSetupPresentationRevision = 0;
            PresentMarkerSetupLocked();
            _markerSetupMessage = "Waiting for the projected target to settle.";
        }
        UpdateMarkerSetupUi();
    }

    private static MarkerSetupTarget[] MarkerSetupTargets(int target, BoardScreen board)
    {
        if (target == 0 && board == BoardScreen.Paint) return [new("the centre", new(.50, .50), .075),
            new("the left side", new(.25, .50), .075), new("the right side", new(.75, .50), .075),
            new("the front", new(.50, .75), .075), new("the back", new(.50, .25), .075)];
        if (target == 0) return [new("the centre", new(.50, .62), .075),
            new("the left side", new(.25, .62), .075), new("the right side", new(.75, .62), .075),
            new("the front", new(.50, .78), .075), new("below the fountain", new(.50, .44), .065)];
        double centre = target == 1 ? .25 : .75;
        return [new("the centre of your half", new(centre, .50), .07),
            new("the left of your half", new(centre - .11, .50), .06),
            new("the right of your half", new(centre + .11, .50), .06),
            new("the near side", new(centre, .73), .07), new("the far side", new(centre, .27), .07)];
    }

    private BoardRect MarkerSetupSearchRegion => _markerSetupTarget switch
    {
        1 => new(0, 0, .5, 1),
        2 => new(.5, 0, .5, 1),
        _ => new(0, 0, 1, 1)
    };

    private void PresentMarkerSetupLocked()
    {
        if (_markerSetupSession is not { } session) return;
        var target = session.CurrentTarget ?? session.Targets[0];
        bool learning = session.Stage == SetupStage.Learning;
        bool moving = session.Stage == SetupStage.Moving;
        if (moving) target = session.MovementTargets[0];
        double halfWidth = learning ? .14 : target.Radius;
        double halfHeight = learning ? .12 : target.Radius;
        var bounds = new BoardRect(target.Position.X - halfWidth, target.Position.Y - halfHeight,
            halfWidth * 2, halfHeight * 2);
        BoardRect? routeEnd = moving ? new(session.MovementTargets[1].Position.X - session.MovementTargets[1].Radius,
            session.MovementTargets[1].Position.Y - session.MovementTargets[1].Radius,
            session.MovementTargets[1].Radius * 2, session.MovementTargets[1].Radius * 2) : null;
        string instruction = session.Stage switch
        {
            SetupStage.Learning => MarkerSetupInstruction(session.Reason),
            SetupStage.Validating => $"{session.Reason} {session.TargetIndex + 1} / {session.Targets.Count}",
            SetupStage.Moving => session.Reason,
            SetupStage.LiftOff => session.Reason,
            _ => "Checks passed. Choose Save marker on the laptop."
        };
        long revision = _scene.UpdateMarkerSetup(learning ? PresentationStage.NeutralAcquisition :
            moving ? PresentationStage.MovementValidation : PresentationStage.SceneValidation,
            instruction, session.Progress, bounds, routeEnd);
        if (revision != _markerSetupPresentationRevision)
        {
            _markerSetupPresentationRevision = revision;
            session.ResetProgress();
            _markerSetupMeasurement = null;
        }
    }

    // Shares the existing retained-frame worker: no camera reopen, frame copies
    // or queued backlog. Other detectors are idle only while setup is active.
    private bool QueueMarkerSetupFrame(CameraFrame frame)
    {
        lock (_markerSetupGate)
        {
            if (_markerSetupSession is not { } session) return false;
            if (!MarkerSetupContextCurrentLocked(frame))
            {
                _markerSetupInvalidated = true;
                return true; // The UI timer cancels; publication and Save reject immediately.
            }
            if (session.Stage == SetupStage.Ready) return true;
            if (_scene.MarkerSetupState?.PresentedAt is not { } presented ||
                frame.Timestamp < presented + TimeSpan.FromMilliseconds(650))
            { _markerSetupMessage = "Waiting for the projected target to settle."; return true; }
            if (!EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
            {
                session.ResetProgress(); _markerSetupMeasurement = null;
                _markerSetupMessage = "Waiting for a fresh camera frame."; return true;
            }
            long generation = _markerSetupGeneration;
            long revision = _markerSetupPresentationRevision;
            var stage = session.Stage;
            var profile = session.CandidateProfile;
            var targetArea = _scene.GetMarkerSetupCameraTarget();
            var searchArea = _scene.GetMarkerSetupCameraRegion(MarkerSetupSearchRegion);
            var hint = _markerSetupMeasurement is { } measured &&
                frame.Timestamp - _markerSetupMeasurementTime < TimeSpan.FromMilliseconds(250)
                ? new BlackTipSearchHint(measured, measured.Center, _markerSetupMeasurementTime) : null;
            _markerSetupWorker.TryRun(Stopwatch.GetTimestamp(), TimeSpan.FromMilliseconds(35), frame, _ =>
            {
                try
                {
                    var references = StickTipProjectionReference(frame);
                    if (references is null || references.Count == 0)
                    {
                        lock (_markerSetupGate)
                        {
                            if (generation == _markerSetupGeneration && revision == _markerSetupPresentationRevision &&
                                ReferenceEquals(session, _markerSetupSession) && MarkerSetupContextCurrentLocked(frame))
                            {
                                session.ResetProgress(); _markerSetupMeasurement = null;
                                _markerSetupMessage = "Waiting for the projected target reference.";
                                PresentMarkerSetupLocked();
                            }
                        }
                        return;
                    }
                    BlackTipLearningResult? learned = null;
                    MarkerTipDetectionResult? detection = null;
                    if (stage == SetupStage.Learning)
                        learned = BlackTipDetector.LearnPairInTarget(frame.Width, frame.Height, frame.Stride,
                            frame.Bgra, targetArea, new(references, frame.Timestamp));
                    else if (profile is not null)
                        detection = BlackTipDetector.DetectEach(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                            [profile], new(ProjectionFrames: references, FrameTime: frame.Timestamp), [hint], searchArea)[0];
                    lock (_markerSetupGate)
                    {
                        if (generation != _markerSetupGeneration || revision != _markerSetupPresentationRevision ||
                            !ReferenceEquals(session, _markerSetupSession) || !MarkerSetupContextCurrentLocked(frame)) return;
                        var now = MonotonicClock.UtcNow;
                        if (learned is not null) session.ObserveLearning(learned, frame.Timestamp, now);
                        else if (detection is not null)
                        {
                            detection = FilterMarkerSetupDetectionLocked(detection);
                            PixelPoint? board = null;
                            if (detection.Candidates.Count == 1 && detection.AmbiguousMarkerCenters.Count == 0 &&
                                _scene.TryMapMarkerSetupCameraPoint(new((float)detection.Candidates[0].Center.X,
                                    (float)detection.Candidates[0].Center.Y), out var mapped))
                            {
                                board = new(mapped.X, mapped.Y);
                                // Only actual fresh full-pair measurements seed a bounded search.
                                if (EyeTipFrameFresh(frame, now))
                                { _markerSetupMeasurement = detection.Candidates[0]; _markerSetupMeasurementTime = frame.Timestamp; }
                            }
                            session.ObserveValidation(detection, board, frame.Timestamp, now);
                        }
                        _markerSetupMessage = MarkerSetupInstruction(session.Reason);
                        PresentMarkerSetupLocked();
                    }
                }
                catch (Exception ex)
                {
                    lock (_markerSetupGate)
                    {
                        if (generation != _markerSetupGeneration || !ReferenceEquals(session, _markerSetupSession)) return;
                        session.ResetProgress();
                        _markerSetupMessage = "Could not read the marker: " + ex.Message;
                    }
                }
            });
            return true;
        }
    }

    private static string MarkerSetupInstruction(string reason) => reason switch
    {
        "waiting-for-projected-reference" => "Waiting for the projected target reference.",
        "show-complete-double-bar-in-target" => "Place both complete black bars inside the ring and keep them flat.",
        "show-only-one-double-bar-in-target" or "too-many-marks-in-target" => "Leave just one double-bar marker inside the ring.",
        "show-solid-black-ink-in-target" => "Keep the black bars flat, still and fully visible inside the ring.",
        "marker-size-or-shape-not-supported" => "Use two solid parallel black bars and show both completely.",
        _ => reason
    };

    private MarkerTipDetectionResult FilterMarkerSetupDetectionLocked(MarkerTipDetectionResult detection)
    {
        bool InRegion(PixelPoint point) => _scene.TryMapMarkerSetupCameraPoint(
            new((float)point.X, (float)point.Y), out var board) && float.IsFinite(board.X) && float.IsFinite(board.Y) &&
            (_markerSetupTarget == 0 || (board.X < .5 ? 1 : 2) == _markerSetupTarget);
        var candidates = detection.Candidates.Where(item => InRegion(item.Center)).ToArray();
        var supporting = detection.SupportingBars.Where(item => InRegion(item.Center)).ToArray();
        var ambiguous = detection.AmbiguousMarkerCenters.Where(InRegion).ToArray();
        // Crop bounds deliberately include a marker-sized margin. Partition the
        // actual results like Football does, including residual strips at lift-off.
        // Reference waits or other invalid evidence must never become a clear frame.
        string reason = detection.Reason is "no-black-bar-pair" or "black-bar-pair-candidate" or "multiple-black-bar-pairs"
            ? candidates.Length switch { 0 => "no-black-bar-pair", 1 => "black-bar-pair-candidate", _ => "multiple-black-bar-pairs" }
            : detection.Reason;
        return detection with { Candidates = candidates, SupportingBars = supporting,
            AmbiguousMarkerCenters = ambiguous, Reason = reason };
    }

    private bool MarkerSetupContextCurrentLocked(CameraFrame? source = null)
    {
        if (_markerSetupInvalidated || _closing || !_cameraWanted || !_camera.IsRunning ||
            _cameraHealthWarning || _cameraManualRestartRequired || IsBoardScanMeasuring || _output is null ||
            _camera.ActiveDeviceId != _markerSetupCamera || _selectedCameraId != _markerSetupCamera ||
            !_scene.HasBoardMediaClip || _scene.CurrentBoardScreen != _markerSetupBoard || !_scene.MarkerSetupActive ||
            !FirstUseMarkerInputStillSelected())
            return false;
        var latest = Volatile.Read(ref _latestCameraFrame);
        if (latest is null || latest.Width != _markerSetupWidth || latest.Height != _markerSetupHeight ||
            source is not null && (source.Width != _markerSetupWidth || source.Height != _markerSetupHeight)) return false;
        var registration = _scene.GetMarkerSetupCameraRegion(new(0, 0, 1, 1));
        if (registration.Count != 4 || registration.Count != _markerSetupRegistration.Count) return false;
        for (int i = 0; i < registration.Count; i++)
            if (!double.IsFinite(registration[i].X) || !double.IsFinite(registration[i].Y) ||
                MarkerTipGeometry.Distance(registration[i], _markerSetupRegistration[i]) > .01) return false;
        return true;
    }

    private void UpdateMarkerSetupUi()
    {
        if (_closing) return;
        bool changed;
        lock (_markerSetupGate)
        {
            changed = _markerSetupSession is not null && !MarkerSetupContextCurrentLocked();
            if (!changed && _markerSetupSession is { Stage: not SetupStage.Ready } session &&
                Volatile.Read(ref _latestCameraFrame) is { } latest && !EyeTipFrameFresh(latest, MonotonicClock.UtcNow))
            {
                session.ResetProgress(); _markerSetupMeasurement = null;
                _markerSetupMessage = "Waiting for a fresh camera frame.";
            }
        }
        if (changed) CancelMarkerSetup("Camera, board, input mode or alignment changed. Start setup again; the saved marker is unchanged.");
        lock (_markerSetupGate)
        {
            var session = _markerSetupSession;
            MarkerSetupTargetCombo.IsEnabled = session is null;
            StartMarkerSetupButton.Content = session is null ? "Set up marker" : "Start again";
            RecheckMarkerSetupButton.IsEnabled = session is null;
            CancelMarkerSetupButton.IsEnabled = session is not null;
            SaveMarkerSetupButton.IsEnabled = session?.Stage == SetupStage.Ready &&
                Volatile.Read(ref _latestCameraFrame) is { } current && EyeTipFrameFresh(current, MonotonicClock.UtcNow);
            MarkerSetupProgress.Value = (session?.Progress ?? 0) * 100;
            MarkerSetupStatusText.Text = _markerSetupMessage;
        }
        TryBeginFirstUseMarkerSetup();
        UpdateFootballAmbience();
    }

    private void CancelMarkerSetup(string? message, bool continueFirstUse = false)
    {
        bool active;
        lock (_markerSetupGate)
        {
            active = _markerSetupSession is not null;
            _markerSetupGeneration++;
            _markerSetupSession = null;
            _markerSetupFirstUseActive = false;
            if (!continueFirstUse) _markerSetupFirstUse.Dismiss();
            _markerSetupInvalidated = false;
            _markerSetupMeasurement = null;
            if (message is not null) _markerSetupMessage = message;
            _scene.EndMarkerSetup();
        }
        if (active && !_closing)
        {
            ResetEyeTipTracking("marker-setup-finished");
            ResetFootballInput();
        }
    }

    private void SaveVerifiedMarker()
    {
        int target;
        bool continueFirstUse;
        lock (_markerSetupGate)
        {
            if (_markerSetupSession is not { Stage: SetupStage.Ready, CandidateProfile: { } profile } ||
                _markerSetupCamera is not { } camera || !MarkerSetupContextCurrentLocked()) return;
            if (Volatile.Read(ref _latestCameraFrame) is not { } latest || !EyeTipFrameFresh(latest, MonotonicClock.UtcNow))
            { _markerSetupMessage = "Wait for fresh camera video before saving the verified marker."; return; }
            target = _markerSetupTarget;
            continueFirstUse = _markerSetupFirstUseActive;
            if (target == 0)
            {
                BlackTipProfile? old;
                string? oldMode;
                lock (_eyeTipGate)
                {
                    old = _wandMarkerProfiles.GetValueOrDefault(camera);
                    oldMode = _wandMarkerModes.GetValueOrDefault(camera);
                    _wandMarkerProfiles[camera] = profile;
                    _wandMarkerModes[camera] = "bar";
                }
                SaveWandMarkerSettings();
                if (_wandMarkerSettingsError is { } error)
                {
                    lock (_eyeTipGate)
                    {
                        if (old is null) _wandMarkerProfiles.Remove(camera); else _wandMarkerProfiles[camera] = old;
                        if (oldMode is null) _wandMarkerModes.Remove(camera); else _wandMarkerModes[camera] = oldMode;
                    }
                    _markerSetupMessage = error + " Your previous marker is unchanged. Try Save again.";
                    return;
                }
            }
            else
            {
                FootballCameraProfiles? old;
                lock (_footballInputGate)
                {
                    old = _footballMarkerProfiles.GetValueOrDefault(camera);
                    var previous = old ?? new(null, null);
                    _footballMarkerProfiles[camera] = target == 1 ? previous with { Player1 = profile } : previous with { Player2 = profile };
                }
                SaveFootballMarkerSettings();
                if (_footballMarkerSaveError is { } error)
                {
                    lock (_footballInputGate)
                    { if (old is null) _footballMarkerProfiles.Remove(camera); else _footballMarkerProfiles[camera] = old; }
                    _markerSetupMessage = error + " Your previous marker is unchanged. Try Save again.";
                    return;
                }
            }
        }
        CancelMarkerSetup("Marker saved. Position, movement and lift-off checks passed for this camera.", continueFirstUse);
        if (target == 0) SetStickTrackingEnabled(true);
        else (target == 1 ? FootballPlayer1Input : FootballPlayer2Input).SelectedIndex = 0;
        UpdateMarkerSetupUi();
    }

    private object GetMarkerSetupStatus()
    {
        lock (_markerSetupGate) return new
        {
            active = _markerSetupSession is not null, target = _markerSetupTarget,
            stage = _markerSetupSession?.Stage.ToString(), targetIndex = _markerSetupSession?.TargetIndex,
            progress = _markerSetupSession?.Progress, status = _markerSetupMessage,
            candidateProfile = _markerSetupSession?.CandidateProfile,
            observedFrames = _markerSetupSession?.ObservedFrames, fullPairFrames = _markerSetupSession?.FullPairFrames,
            missingFrames = _markerSetupSession?.MissingFrames,
            maximumFrameGapMilliseconds = _markerSetupSession?.MaximumFrameGap.TotalMilliseconds,
            movementTraversalsCompleted = _markerSetupSession?.MovementTraversalsCompleted,
            presentation = _scene.MarkerSetupState
        };
    }
}
