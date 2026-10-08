using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Football owns two independent profiles. Learning either player must never
    // change the single marker remembered by Water Garden.
    private readonly object _footballInputGate = new();
    private readonly Dictionary<string, FootballCameraProfiles> _footballMarkerProfiles = new(StringComparer.Ordinal);
    private readonly ColorTipTracker[] _footballTipTrackers = [new(), new()];
    private readonly bool[] _footballFingerInputs = [false, false];
    private readonly PixelPoint?[] _footballCameraTips = new PixelPoint?[2];
    private readonly DateTimeOffset[] _footballTipTimes = new DateTimeOffset[2];
    private readonly string[] _footballInputReasons = ["Learn player 1's black bar.", "Learn player 2's black bar."];
    private DispatcherQueueTimer? _footballInputTimer;
    private Task? _footballDetectionTask, _footballLearningTask;
    private long _footballInputGeneration, _footballDetectionTick;
    private DateTimeOffset _footballInputNotBefore;
    private int _footballLearningPlayer = -1, _footballFrameWidth, _footballFrameHeight;
    private bool _footballDetecting, _footballLearningBusy, _footballInputWasActive;
    private string? _footballMarkerSettingsError;
    private sealed record FootballCameraProfiles(BlackTipProfile? Player1, BlackTipProfile? Player2)
    {
        public BlackTipProfile? ForPlayer(int player) => player == 0 ? Player1 : Player2;
    }
    private sealed record FootballMarkerSettings(int Version, Dictionary<string, FootballCameraProfiles> Cameras);
    private string FootballMarkerSettingsPath => Path.Combine(_appDataDirectory, "football-stick-tips.json");
    private bool IsLearningFootballTip { get { lock (_footballInputGate) return _footballLearningPlayer >= 0; } }

    private void InitializeFootballInput()
    {
        try
        {
            if (File.Exists(FootballMarkerSettingsPath))
            {
                if (new FileInfo(FootballMarkerSettingsPath).Length > 131072)
                    throw new InvalidDataException("The saved football marker settings are too large.");
                using var document = JsonDocument.Parse(File.ReadAllText(FootballMarkerSettingsPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out int format))
                    throw new InvalidDataException("The saved football marker settings are invalid.");
                // Version 1 was the earlier colour-marker experiment. Its
                // profiles do not describe a black cardboard bar; leave them
                // untouched until the user successfully learns a new bar.
                if (format != 1)
                {
                    var saved = document.Deserialize<FootballMarkerSettings>();
                    if (saved is not { Version: 2, Cameras: not null } || saved.Cameras.Count > 64 ||
                        saved.Cameras.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 4096 ||
                            pair.Value is null || pair.Value.Player1 is { IsValid: false } || pair.Value.Player2 is { IsValid: false }))
                        throw new InvalidDataException("The saved football marker settings are invalid.");
                    foreach (var pair in saved.Cameras) _footballMarkerProfiles.Add(pair.Key, pair.Value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _footballMarkerSettingsError = "Football marker settings could not be loaded: " + ex.Message; }

        _footballInputTimer = DispatcherQueue.CreateTimer();
        _footballInputTimer.Interval = TimeSpan.FromMilliseconds(100);
        _footballInputTimer.Tick += (_, _) =>
        {
            bool active = _scene.CurrentBoardScreen == BoardScreen.Football;
            if (active != _footballInputWasActive)
            {
                ResetFootballInput();
                _footballInputWasActive = active;
            }
            lock (_footballInputGate)
            {
                var now = MonotonicClock.UtcNow;
                for (int player = 0; player < 2; player++)
                    if (_footballTipTimes[player] != default &&
                        (now - _footballTipTimes[player] > EyeTipLifetime || !_cameraWanted ||
                         !_camera.IsRunning || _cameraHealthWarning || IsBoardScanMeasuring))
                    {
                        _footballTipTrackers[player].Reset();
                        ClearFootballPlayerLocked(player, "Waiting for a fresh view of the tip.", now);
                    }
            }
            UpdateFootballInputStatus();
        };
        _footballInputTimer.Start();
        UpdateFootballInputStatus();
    }

    private void ResetFootballInput()
    {
        lock (_footballInputGate)
        {
            _footballInputGeneration++;
            _footballInputNotBefore = MonotonicClock.UtcNow;
            _footballDetectionTick = 0;
            _footballLearningPlayer = -1;
            _footballFrameWidth = _footballFrameHeight = 0;
            for (int player = 0; player < 2; player++)
            {
                _footballTipTrackers[player].Reset();
                ClearFootballPlayerLocked(player, "Waiting for a fresh view of the tip.", _footballInputNotBefore);
            }
        }
    }

    private void ClearFootballPlayerLocked(int player, string reason, DateTimeOffset frameTime)
    {
        _footballCameraTips[player] = null;
        _footballTipTimes[player] = default;
        _footballInputReasons[player] = reason;
        _scene.SetFootballPlayerCameraPoint(player, null, frameTime);
    }

    private void FootballInputMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || sender is not ComboBox combo || !int.TryParse(combo.Tag?.ToString(), out int player)) return;
        lock (_footballInputGate) _footballFingerInputs[player] = combo.SelectedIndex == 1;
        _scene.SetFootballFingerInput(player, combo.SelectedIndex == 1);
        ResetFootballInput();
        if (combo.SelectedIndex == 1) SetHandTrackingEnabled(true);
        UpdateFootballInputStatus();
    }

    private void LearnFootballTip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int player)) return;
        BeginFootballBarLearning(player);
    }

    private bool BeginFootballBarLearning(int player)
    {
        if (_closing) return false;
        lock (_footballInputGate)
            if (_footballLearningBusy)
            { SetStatus("Wait for the current football learning attempt to finish."); return false; }
        if (!_cameraWanted || !_camera.IsRunning || _camera.ActiveDeviceId != _selectedCameraId || _cameraHealthWarning)
        { SetStatus("Start the selected webcam before learning a football black bar."); return false; }
        (player == 0 ? FootballPlayer1Input : FootballPlayer2Input).SelectedIndex = 0;
        ResetFootballInput();
        _footballInputWasActive = _scene.CurrentBoardScreen == BoardScreen.Football;
        // Cancel another learning gesture without replacing its saved profile.
        lock (_eyeTipGate) { _eyeTipLearning = false; _colorTipLearning = false; }
        UpdateEyeTipStatus();
        _frozenFrame = null;
        lock (_footballInputGate)
        {
            _footballLearningPlayer = player;
            _footballInputReasons[player] = "Click inside this player's black bar in the live camera preview.";
        }
        SetStatus($"Click player {player + 1}'s black bar in the live camera preview. Face the black crossbar toward the webcam and keep it near the board.");
        UpdateFootballInputStatus();
        CameraCanvas.Invalidate();
        return true;
    }

    // Starting another marker's learning replaces a pending football click, just
    // as football learning cancels eye and colour learning.
    private void CancelFootballLearning()
    {
        lock (_footballInputGate)
        {
            if (_footballLearningPlayer < 0) return;
            _footballInputReasons[_footballLearningPlayer] = "Waiting for a fresh view of the tip.";
            _footballLearningPlayer = -1;
        }
        UpdateFootballInputStatus();
    }

    // Player 2 is the computer in one-player mode; its camera input is never read.
    private bool FootballHumanPlayer(int player) =>
        player == 0 || _scene.FootballState.Mode == FootballMode.TwoHumans;

    private void CancelFootballLearning_Click(object sender, RoutedEventArgs e)
    {
        ResetFootballInput();
        UpdateFootballInputStatus();
        CameraCanvas.Invalidate();
    }

    private bool TryLearnFootballTipFromPreview(CameraFrame frame, PixelPoint point)
    {
        lock (_footballInputGate)
        {
            if (_footballLearningPlayer < 0) return false;
            if (_footballLearningBusy) return true;
            _footballLearningBusy = true;
            _footballLearningTask = LearnFootballTipFromPreviewAsync(_footballLearningPlayer, frame, point);
            return true;
        }
    }

    // Local control uses human-facing player numbers (1 or 2) and raw, unmirrored
    // camera pixels. It follows the same click-learning path and reports whether
    // that attempt actually succeeded, including failures handled by the UI.
    private async Task<object> LearnFootballBarForControlAsync(int player, double x, double y)
    {
        if (player is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(player), "Use player 1 or 2.");
        var frame = Volatile.Read(ref _latestCameraFrame);
        if (_closing || frame is null || !_cameraWanted || !_camera.IsRunning || _cameraHealthWarning ||
            _camera.ActiveDeviceId != _selectedCameraId || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
            throw new InvalidOperationException("Start the selected webcam and wait for a fresh frame.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new ArgumentException("Provide x and y inside the raw camera frame.");
        int index = player - 1;
        if (!BeginFootballBarLearning(index)) return new { learned = false, input = GetFootballInputStatus() };
        Task<bool> learning;
        lock (_footballInputGate)
        {
            _footballLearningBusy = true;
            learning = LearnFootballTipFromPreviewAsync(index, frame, new(x, y));
            _footballLearningTask = learning;
        }
        bool learned = await learning;
        return new { learned, input = GetFootballInputStatus() };
    }

    private async Task<bool> LearnFootballTipFromPreviewAsync(int player, CameraFrame frame, PixelPoint point)
    {
        bool learnedSuccessfully = false;
        try
        {
            string? cameraId = _camera.ActiveDeviceId;
            long generation;
            lock (_footballInputGate) generation = _footballInputGeneration;
            if (cameraId is null || cameraId != _selectedCameraId || !_cameraWanted || !_camera.IsRunning ||
                _cameraHealthWarning || !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
                throw new InvalidOperationException("Wait for fresh webcam video, then click the tip again.");
            var learned = await Task.Run(() =>
            {
                var references = StickTipProjectionReference();
                var profile = BlackTipDetector.Learn(frame.Width, frame.Height, frame.Stride, frame.Bgra, point,
                    new(ProjectionFrames: references, FrameTime: frame.Timestamp));
                var detection = BlackTipDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra, profile,
                    new(PreferredCenter: point, ProjectionFrames: references, FrameTime: frame.Timestamp));
                return (Profile: profile, Detection: detection);
            });
            lock (_footballInputGate)
            {
                if (_closing || generation != _footballInputGeneration || _footballLearningPlayer != player ||
                    _camera.ActiveDeviceId != cameraId || cameraId != _selectedCameraId ||
                    !EyeTipFrameFresh(frame, MonotonicClock.UtcNow))
                    throw new InvalidOperationException("The camera changed or the frame expired. Click the tip again.");
                var chosen = NearestStickTipCandidate(learned.Detection.Candidates, point.X, point.Y,
                    item => item.Center, item => item.RadiusPixels);
                if (chosen is null)
                    throw new InvalidOperationException("Click the centre of one clearly visible black crossbar, away from other dark objects.");
                var profiles = _footballMarkerProfiles.GetValueOrDefault(cameraId) ?? new(null, null);
                _footballMarkerProfiles[cameraId] = player == 0
                    ? profiles with { Player1 = learned.Profile } : profiles with { Player2 = learned.Profile };
                _footballInputGeneration++;
                _footballTipTrackers[player].Acquire(chosen, frame.Timestamp);
                _footballLearningPlayer = -1;
                _footballInputReasons[player] = "Tip saved. Confirming it across fresh camera frames.";
            }
            SaveFootballMarkerSettings();
            learnedSuccessfully = true;
            SetStatus(_footballMarkerSettingsError is null
                ? $"Player {player + 1}'s football black bar is saved for this camera."
                : $"Player {player + 1}'s black bar is learned for this session. " + _footballMarkerSettingsError);
        }
        catch (Exception ex)
        {
            if (!_closing) SetStatus("Football tip could not be learned: " + ex.Message);
            lock (_footballInputGate) _footballInputReasons[player] = ex.Message;
        }
        finally
        {
            lock (_footballInputGate) _footballLearningBusy = false;
            UpdateFootballInputStatus();
            if (!_closing) CameraCanvas.Invalidate();
        }
        return learnedSuccessfully;
    }

    private void SaveFootballMarkerSettings()
    {
        try
        {
            FootballMarkerSettings saved;
            lock (_footballInputGate) saved = new(2, new(_footballMarkerProfiles, StringComparer.Ordinal));
            Directory.CreateDirectory(_appDataDirectory);
            string temporary = FootballMarkerSettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FootballMarkerSettingsPath, overwrite: true);
            _footballMarkerSettingsError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _footballMarkerSettingsError = "Football marker settings could not be saved: " + ex.Message; }
    }

    private void QueueFootballDetection(CameraFrame frame)
    {
        lock (_footballInputGate)
        {
            if (_closing || _footballDetecting || _footballLearningPlayer >= 0 ||
                _scene.CurrentBoardScreen != BoardScreen.Football || !_cameraWanted || !_camera.IsRunning ||
                _cameraHealthWarning || IsBoardScanMeasuring || frame.Timestamp < _footballInputNotBefore ||
                !EyeTipFrameFresh(frame, MonotonicClock.UtcNow)) return;
            string? cameraId = _camera.ActiveDeviceId;
            if (cameraId is null || cameraId != _selectedCameraId ||
                !_footballMarkerProfiles.TryGetValue(cameraId, out var profiles)) return;
            var selected = Enumerable.Range(0, 2).Where(player => FootballHumanPlayer(player) &&
                !_footballFingerInputs[player] && profiles.ForPlayer(player) is not null).ToArray();
            if (selected.Length == 0) return;
            long tick = Stopwatch.GetTimestamp();
            if (_footballDetectionTick != 0 && Stopwatch.GetElapsedTime(_footballDetectionTick, tick).TotalMilliseconds < 32) return;
            _footballDetectionTick = tick;
            _footballDetecting = true;
            long generation = _footballInputGeneration;
            _footballDetectionTask = Task.Run(() =>
            {
                try
                {
                    // GPU reference readback can be slow; never hold the input
                    // lock while reading it or scanning either camera image.
                    var references = StickTipProjectionReference();
                    // Both players share one colour conversion of this camera frame.
                    var detections = BlackTipDetector.DetectEach(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                        selected.Select(player => profiles.ForPlayer(player)!).ToArray(),
                        new(ProjectionFrames: references, FrameTime: frame.Timestamp));
                    lock (_footballInputGate)
                    {
                        if (_closing || generation != _footballInputGeneration || !_cameraWanted || !_camera.IsRunning ||
                            _cameraHealthWarning || IsBoardScanMeasuring || _camera.ActiveDeviceId != cameraId ||
                            _scene.CurrentBoardScreen != BoardScreen.Football) return;
                        if (_footballFrameWidth != frame.Width || _footballFrameHeight != frame.Height)
                        {
                            foreach (var tracker in _footballTipTrackers) tracker.Reset();
                            _footballFrameWidth = frame.Width;
                            _footballFrameHeight = frame.Height;
                        }
                        var now = MonotonicClock.UtcNow;
                        bool fresh = EyeTipFrameFresh(frame, now);
                        for (int index = 0; index < selected.Length; index++)
                        {
                            int player = selected[index];
                            if (!fresh)
                            {
                                _footballTipTrackers[player].Reset();
                                ClearFootballPlayerLocked(player, "Waiting for fresh camera and projection images.", frame.Timestamp);
                                continue;
                            }
                            // Identical bars are assigned by calibrated pitch half before temporal matching.
                            var decision = FootballTipAssignment.Update(_footballTipTrackers[player], player,
                                detections[index], camera => _scene.TryMapFootballCameraPoint(camera, out var field) ? field : null,
                                frame.Timestamp, now);
                            switch (decision.Action)
                            {
                                case FootballTipAction.Publish:
                                    _footballCameraTips[player] = decision.Tip!.Center;
                                    _footballTipTimes[player] = frame.Timestamp;
                                    _footballInputReasons[player] = decision.Reason;
                                    _scene.SetFootballPlayerFieldPoint(player, decision.FieldPoint, frame.Timestamp);
                                    break;
                                case FootballTipAction.Clear:
                                    ClearFootballPlayerLocked(player, decision.Reason, frame.Timestamp);
                                    break;
                                case FootballTipAction.Hold:
                                    // Keep the last fresh input. The expiry timer and the game's
                                    // own freshness pause play only if the bar stays away.
                                    _footballInputReasons[player] = decision.Reason;
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_footballInputGate)
                        if (generation == _footballInputGeneration)
                            foreach (int player in selected)
                            {
                                _footballTipTrackers[player].Reset();
                                ClearFootballPlayerLocked(player, "Marker detection failed: " + ex.Message, MonotonicClock.UtcNow);
                            }
                }
                finally { lock (_footballInputGate) _footballDetecting = false; }
            });
        }
    }

    private void PublishFootballFingers(IReadOnlyList<HandDetection> hands, CameraFrame frame)
    {
        if (_scene.CurrentBoardScreen != BoardScreen.Football) return;
        lock (_footballInputGate)
        {
            bool fresh = !_closing && _cameraWanted && _camera.IsRunning && !_cameraHealthWarning &&
                !IsBoardScanMeasuring && frame.Timestamp >= _footballInputNotBefore &&
                EyeTipFrameFresh(frame, MonotonicClock.UtcNow);
            var candidates = new List<(PixelPoint Camera, PixelPoint Field)>[] { [], [] };
            if (fresh)
                foreach (var hand in hands)
                    if (_scene.TryMapFootballCameraPoint(hand.IndexTip, out var fieldPoint))
                        candidates[fieldPoint.X < .5 ? 0 : 1].Add((hand.IndexTip, fieldPoint));
            for (int player = 0; player < 2; player++)
            {
                if (!_footballFingerInputs[player] || !FootballHumanPlayer(player)) continue;
                if (candidates[player].Count > 1)
                {
                    ClearFootballPlayerLocked(player, "Show only one hand in this player's half.", frame.Timestamp);
                    continue;
                }
                if (candidates[player].Count == 0)
                {
                    // A brief miss holds the last fresh fingertip; expiry pauses play.
                    _footballInputReasons[player] = "Point one index fingertip in this player's half.";
                    continue;
                }
                var (camera, field) = candidates[player][0];
                _footballCameraTips[player] = camera;
                _footballTipTimes[player] = frame.Timestamp;
                _footballFrameWidth = frame.Width;
                _footballFrameHeight = frame.Height;
                _footballInputReasons[player] = "Index fingertip tracked.";
                _scene.SetFootballPlayerFieldPoint(player, field, frame.Timestamp);
            }
        }
    }

    private void ClearFootballFingerInput(DateTimeOffset frameTime)
    {
        lock (_footballInputGate)
            for (int player = 0; player < 2; player++)
                if (_footballFingerInputs[player])
                    ClearFootballPlayerLocked(player, "Waiting for a fresh view of the hand.", frameTime);
    }

    private void UpdateFootballInputStatus()
    {
        if (_closing || FootballInputStatusText is null) return;
        lock (_footballInputGate)
        {
            var cameraId = _camera.ActiveDeviceId ?? _selectedCameraId;
            var profiles = cameraId is null ? null : _footballMarkerProfiles.GetValueOrDefault(cameraId);
            string Describe(int player) => $"P{player + 1}: " + (_footballLearningPlayer == player
                ? "Click the black bar in the live camera preview."
                : !FootballHumanPlayer(player) ? "The computer plays this side."
                : _footballFingerInputs[player] ? _footballInputReasons[player]
                : profiles?.ForPlayer(player) is null ? "Choose Learn black bar, then click its centre."
                : _footballInputReasons[player]);
            FootballInputStatusText.Text = Describe(0) + "\n" + Describe(1) +
                (_footballMarkerSettingsError is null ? "" : "\n" + _footballMarkerSettingsError);
        }
    }

    private object GetFootballInputStatus()
    {
        lock (_footballInputGate)
        {
            string? cameraId = _camera.ActiveDeviceId ?? _selectedCameraId;
            var profiles = cameraId is null ? null : _footballMarkerProfiles.GetValueOrDefault(cameraId);
            var now = MonotonicClock.UtcNow;
            return new
            {
                cameraDeviceId = cameraId,
                active = _scene.CurrentBoardScreen == BoardScreen.Football,
                learningPlayer = _footballLearningPlayer >= 0 ? (int?)(_footballLearningPlayer + 1) : null,
                players = Enumerable.Range(0, 2).Select(player => new
                {
                    player = player + 1, human = FootballHumanPlayer(player),
                    input = _footballFingerInputs[player] ? "finger" : "black-bar",
                    learned = profiles?.ForPlayer(player) is not null,
                    profile = profiles?.ForPlayer(player),
                    visible = _footballCameraTips[player] is not null && now - _footballTipTimes[player] <= EyeTipLifetime,
                    cameraPoint = _footballCameraTips[player], frameTime = _footballTipTimes[player],
                    reason = _footballInputReasons[player]
                }).ToArray(),
                settingsPath = FootballMarkerSettingsPath, settingsError = _footballMarkerSettingsError
            };
        }
    }

    private void DrawFootballInputPreview(CanvasDrawingSession drawing, CameraFrame frame, Rect rect)
    {
        lock (_footballInputGate)
        {
            if (_footballFrameWidth != frame.Width || _footballFrameHeight != frame.Height || _frozenFrame is not null) return;
            var now = MonotonicClock.UtcNow;
            for (int player = 0; player < 2; player++)
            {
                if (_footballCameraTips[player] is not { } point || now - _footballTipTimes[player] > EyeTipLifetime) continue;
                var center = new Vector2((float)(rect.X + point.X / frame.Width * rect.Width),
                    (float)(rect.Y + point.Y / frame.Height * rect.Height));
                var color = player == 0 ? Colors.Orange : Colors.DeepSkyBlue;
                drawing.DrawCircle(center, 12, Colors.Black, 5);
                drawing.DrawCircle(center, 12, color, 2);
                drawing.DrawText($"P{player + 1}", center.X + 16, center.Y - 13, color);
            }
        }
    }

    private async Task DisposeFootballInputAsync()
    {
        _footballInputTimer?.Stop();
        ResetFootballInput();
        Task? detection, learning;
        lock (_footballInputGate) { detection = _footballDetectionTask; learning = _footballLearningTask; }
        if (detection is not null) await detection;
        if (learning is not null) await learning;
    }
}
