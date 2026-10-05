using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // This is acquisition assistance, never a hand observation or gesture.
    // Learn the camera appearance of a known, stationary rendered table: its
    // projected colors differ from RGB because of the projector and exposure.
    private static readonly TimeSpan AcquisitionSettle = TimeSpan.FromMilliseconds(450);
    // Webcam timestamps are CPU-arrival times, not exposure times. Give the
    // visibly delayed camera image time to lose our own white disk after off.
    private static readonly TimeSpan AcquisitionLightOffSettle = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AcquisitionLightDuration = TimeSpan.FromMilliseconds(900);
    private AcquisitionSceneState? _acquisitionScene;
    private BoardButton[] _acquisitionButtons = [];
    private long _acquisitionRevision;
    private DateTimeOffset _acquisitionQuietUntil;
    private HandAcquisitionHint? _acquisitionHint;
    private HandSpotlight? _acquisitionLight;
    private DateTimeOffset _acquisitionLightUntil;
    private DateTimeOffset _acquisitionLightStarted;
    private HandAcquisitionSceneImage? _acquisitionExpectedScene;
    private CanvasRenderTarget? _acquisitionReferenceTarget;
    private DateTimeOffset _acquisitionReferenceRetryAt;
    private string? _acquisitionReferenceError;
    private string _acquisitionReason = "inactive";
    private long _acquisitionLightCount;
    // At night the phone camera raises its exposure until projected white
    // clips, and lit fingers vanish into it. Start darker when intact white
    // captions already clip, then step both lights down on a clipped lit
    // reading and back up once the lit board reads well below clipping.
    // Evening lit boards read near 231; one step changes it about 1.4 times.
    private const double MinimumLightLevel = .5, NightLightLevel = .8, LightLevelStep = .85, UnclippedLightWhite = 160;
    private double _lightLevel = 1;
    private bool? _lightStartClipped;

    public double SearchLightLevel { get { lock (_gate) return _lightLevel; } }

    private Color LightWhite(byte alpha)
    {
        byte level = (byte)Math.Round(255 * _lightLevel);
        return Color.FromArgb(alpha, level, level, level);
    }

    private readonly record struct AcquisitionSceneState(long Navigation, long Game, long Flights, long LightReset,
        long PhotoRevision);
    public sealed record HandAcquisitionContext(long Revision, bool ObserveMotion,
        PixelPoint[] SearchPolygon, HandAcquisitionHint? IlluminatedHint,
        HandAcquisitionSceneImage? ExpectedScene = null, DateTimeOffset IlluminationStartedAt = default,
        PixelPoint[]? StationarySearchCenters = null, bool RestrictAcquisitionToSearchRegions = true,
        bool AllowsSearchIllumination = true, PixelPoint[]? ContinuousSearchPolygon = null,
        IReadOnlyList<HandDetection>? LostHands = null, IReadOnlyList<PixelPoint[]>? HoldControls = null);

    private AcquisitionSceneState CurrentAcquisitionState() => new(_boardSession.Revision,
        _boardSession.Screen == BoardScreen.Blackjack ? _boardSession.BlackjackState.Revision :
        _boardSession.Screen == BoardScreen.Monopoly ? _boardSession.MonopolyState.Revision :
        _boardSession.Screen == BoardScreen.Globe ? _boardSession.GetGlobeSnapshot(_globeClock()).Revision :
        _boardSession.Screen == BoardScreen.Roulette ? _boardSession.RouletteState.Revision : 0,
        _boardSession.Screen == BoardScreen.Blackjack ? _blackjackFlightRevision : 0, _spotlightResetCount,
        _boardSession.Screen == BoardScreen.PhotoCopy ? _photoCopyRevision : 0);

    public HandAcquisitionContext? GetHandAcquisitionContext(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            if (_boardSession.Screen == BoardScreen.PhotoCopy) SyncPhotoCopySession();
            if (!AcquisitionBoardReady)
            {
                ClearAcquisitionLight();
                _acquisitionScene = null;
                _acquisitionButtons = [];
                _acquisitionExpectedScene = null;
                _acquisitionReferenceError = null;
                _acquisitionReferenceRetryAt = default;
                _acquisitionReason = "inactive";
                return null;
            }
            var blackjack = _boardSession.Screen == BoardScreen.Blackjack;
            var animating = blackjack && HasBlackjackCardAnimation(now) ||
                _boardSession.Screen == BoardScreen.Monopoly &&
                    (MonopolyEntranceActive || HasMonopolyDiceAnimation(_monopolyClock()) || HasMonopolyDrawerAnimation(_monopolyClock())) ||
                HasBoardControlDrawerAnimation();
            var state = CurrentAcquisitionState();
            var buttons = _boardSession.Buttons;
            if (_acquisitionScene != state || !_acquisitionButtons.SequenceEqual(buttons))
            {
                _acquisitionScene = state;
                _acquisitionButtons = buttons.ToArray();
                _acquisitionRevision++;
                _acquisitionExpectedScene = null;
                _acquisitionReferenceRetryAt = default;
                ClearAcquisitionLight();
                _acquisitionQuietUntil = now + AcquisitionSettle;
            }
            // Use the whole calibrated board to size native-camera crops, even
            // when only one small Back button is visible. The expected image's
            // button masks decide which pixels may supply foreground evidence.
            var polygon = new[] { new Point2(.005, .005), new Point2(.995, .005),
                new Point2(.995, .995), new Point2(.005, .995) }
                .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
            HandAcquisitionContext Context(bool observe, HandAcquisitionHint? illuminated)
            {
                if ((observe || illuminated is not null) && _acquisitionExpectedScene is null && now >= _acquisitionReferenceRetryAt)
                    _acquisitionExpectedScene = CaptureExpectedAcquisitionScene();
                var boardCenters = _boardSession.Buttons.Select(button =>
                    new Point2(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2));
                var centers = boardCenters.Select(point =>
                    _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                    .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
                // Photo Copy must see hands beside the subject for its field
                // shutter and to keep a hand from being acquired as an object.
                // This known capture area is separate from button lighting.
                var field = BoardSession.PhotoCopyShutterBounds;
                var capturePolygon = _boardSession.Screen == BoardScreen.PhotoCopy && !_boardSession.PhotoCopyHasSwirl
                    ? new[] { new Point2(field.X, field.Y), new Point2(field.X + field.Width, field.Y),
                        new Point2(field.X + field.Width, field.Y + field.Height), new Point2(field.X, field.Y + field.Height) }
                        .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                        .Select(point => new PixelPoint(point.X, point.Y)).ToArray()
                    : null;
                return new(_acquisitionRevision, observe, polygon, illuminated,
                    _acquisitionExpectedScene, _acquisitionLightStarted, centers,
                    RestrictAcquisitionToSearchRegions: _boardSession.Screen != BoardScreen.HandTracking,
                    AllowsSearchIllumination: true,
                    ContinuousSearchPolygon: capturePolygon, LostHands: LostLitHands(),
                    HoldControls: HoldControlOutlines());
            }
            if (animating || AcquisitionMustYieldToHandOrExecute(now))
            {
                ClearAcquisitionLight();
                _acquisitionQuietUntil = now + AcquisitionSettle;
                _acquisitionReason = animating ? "table-animation" : "hand-or-execute-suppression";
                return Context(false, null);
            }
            if (_acquisitionHint is not null && now < _acquisitionLightUntil)
            {
                _acquisitionReason = "illuminated-search";
                return Context(false, _acquisitionHint);
            }
            if (_acquisitionHint is not null)
            {
                _acquisitionQuietUntil = _acquisitionLightUntil + AcquisitionLightOffSettle;
                ClearAcquisitionLight();
            }
            bool ready = now >= _acquisitionQuietUntil && frameTime >= _acquisitionQuietUntil &&
                frameTime <= now && now - frameTime <= TimeSpan.FromMilliseconds(350);
            _acquisitionReason = ready ? "watching-button-area" : "settling-rendered-scene";
            return Context(ready, null);
        }
    }

    private bool AcquisitionBoardReady => !_disposed && !_boardSession.MenuScrolling && _boardSession.Buttons.Count > 0 &&
        !_blackOutput && !_boardSetup && !IsBoardRevealActive && _calibrationTarget < 0 &&
        _boardMediaClip is not null && _boardCameraMap is not null && _boardSurfaceMap is not null;

    private bool HasAcquiredHandOrSuppression(DateTimeOffset now) => _suppressedHandLights.Count > 0 ||
        _handSpotlights.Any(hand => SpotlightOpacity(now, hand.SourceFrameTime) > 0) ||
        _spotlightCursors.Length > 0 && now >= _spotlightCursorFrameTime &&
        now - _spotlightCursorFrameTime <= TimeSpan.FromMilliseconds(350);

    // Paint keeps only its button-triggered search light while the recognized
    // hand is aiming there. Its ordinary hand/canvas spotlight remains disabled.
    private bool AcquisitionMustYieldToHandOrExecute(DateTimeOffset now) =>
        _boardSession.Screen == BoardScreen.Paint ? _suppressedHandLights.Count > 0 : HasAcquiredHandOrSuppression(now);

    public void CompleteHandAcquisition(HandAcquisitionContext? requested,
        IReadOnlyList<HandAcquisitionHint> hints, IReadOnlyList<HandDetection> hands, DateTimeOffset frameTime,
        bool? illuminatedPresence = null, double? illuminatedWhite = null, bool? projectedWhiteClipped = null)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            var current = GetHandAcquisitionContext(frameTime);
            if (current is null || requested is null || requested.Revision != current.Revision) return;
            if (hands.Count > 0 && _boardSession.Screen != BoardScreen.Paint)
            {
                ClearAcquisitionLight();
                _acquisitionQuietUntil = now + AcquisitionSettle;
                return;
            }
            if (current.IlluminatedHint is not null && requested.IlluminatedHint is not null &&
                current.IlluminationStartedAt == requested.IlluminationStartedAt && frameTime <= now &&
                now - frameTime <= TimeSpan.FromMilliseconds(350))
            {
                // A clipped frame cannot show the hand. Restart the dimmer light
                // so its own camera settling frames are skipped again.
                if (illuminatedWhite >= HandAcquisitionPresenceTracker.SaturatedIlluminatedWhite &&
                    _lightLevel > MinimumLightLevel)
                {
                    _lightLevel = Math.Max(MinimumLightLevel, _lightLevel * LightLevelStep);
                    _acquisitionLightStarted = now;
                    _acquisitionLightUntil = now + AcquisitionLightDuration;
                    _acquisitionReason = "dimming-saturated-search-light";
                    return;
                }
                if (illuminatedWhite < UnclippedLightWhite) _lightLevel = Math.Min(1, _lightLevel / LightLevelStep);
                // Keep searching a stationary foreground object using current
                // camera evidence. The projected white disk alone cannot renew it.
                if (illuminatedPresence == true && hints.Any(hint =>
                    HasCurrentControlObstruction(hint, frameTime) && hint.Center == current.IlluminatedHint.Center))
                {
                    _acquisitionLightUntil = frameTime + TimeSpan.FromMilliseconds(800);
                    RetainPaintButtonLightExclusion();
                    if (_boardSession.Screen == BoardScreen.Paint) NotePaintUserActivity();
                }
                else if (illuminatedPresence is not null)
                {
                    ClearAcquisitionLight();
                    _acquisitionQuietUntil = now + AcquisitionLightOffSettle;
                    _acquisitionReason = illuminatedPresence == false ? "foreground-left" : "insufficient-control-obstruction";
                }
                return;
            }
            if (!current.ObserveMotion || _acquisitionHint is not null || hints.Count == 0) return;
            // Hold buttons act on caption evidence alone and must stay unlit.
            var hint = hints.FirstOrDefault(hint => HasCurrentControlObstruction(hint, frameTime) && !OnHoldButton(hint.Center));
            if (hint is null) return;
            if (!double.IsFinite(hint.RadiusPixels) || hint.RadiusPixels <= 0) return;
            var anchor = _boardCameraMap!.Transform(new(hint.Center.X, hint.Center.Y));
            var board = _boardSurfaceMap!.InverseTransform(anchor);
            if (!_boardSession.Buttons.Any(button => !button.IsHold && button.Bounds.Contains(board.X, board.Y))) return;
            if (hint.ValidatedCandidateBounds is null && ExpectedReachingHand(hint, board) is { } expected) hint = expected;
            var nativeCenter = hint.IlluminationCenter;
            double nativeRadius = hint.IlluminationRadiusPixels;
            if (!double.IsFinite(nativeRadius) || nativeRadius <= 0) return;
            var center = _boardCameraMap.Transform(new(nativeCenter.X, nativeCenter.Y));
            double radius = 0;
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                var point = _boardCameraMap.Transform(new(nativeCenter.X + Math.Cos(angle) * nativeRadius,
                    nativeCenter.Y + Math.Sin(angle) * nativeRadius));
                radius = Math.Max(radius, Math.Sqrt(Math.Pow((point.X - center.X) * _displayAspect, 2) +
                    Math.Pow(point.Y - center.Y, 2)));
            }
            if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(center.X) || !double.IsFinite(center.Y)) return;
            _acquisitionHint = hint;
            // A darker gray at night also avoids a large auto-exposure swing.
            if (projectedWhiteClipped is { } clipped && clipped != _lightStartClipped)
            {
                _lightStartClipped = clipped;
                _lightLevel = clipped ? NightLightLevel : 1;
            }
            if (_boardSession.Screen == BoardScreen.Paint && frameTime <= now &&
                now - frameTime <= TimeSpan.FromMilliseconds(350)) NotePaintUserActivity();
            _acquisitionLight = new(new(center.X, center.Y), Math.Min(radius, hint.ValidatedCandidateBounds is null ? .24 : .32));
            _acquisitionLightUntil = now + AcquisitionLightDuration;
            _acquisitionLightStarted = now;
            RetainPaintButtonLightExclusion();
            _acquisitionLightCount++;
            _acquisitionReason = "illuminated-search";
        }
    }

    // Without a measured hand outline the light covered only the caption, and
    // the back of a palm-down hand beyond it stayed painted with board art:
    // over Monopoly's property row the model saw no hand for seconds. Hands
    // reach in from the viewer's edge (board v = 1); assist over where that
    // hand must be, about 30% of the board long, inside the camera's board view.
    private HandAcquisitionHint? ExpectedReachingHand(HandAcquisitionHint hint, Point2 board)
    {
        Point2 Camera(double u, double v) => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(new(u, v)));
        var hand = new[] { Camera(board.X - .11, board.Y - .02), Camera(board.X + .11, board.Y - .02),
            Camera(board.X - .11, board.Y + .30), Camera(board.X + .11, board.Y + .30) };
        var view = new[] { Camera(.005, .005), Camera(.995, .005), Camera(.995, .995), Camera(.005, .995) };
        if (hand.Concat(view).Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return null;
        double left = Math.Max(Math.Max(0, view.Min(point => point.X)), hand.Min(point => point.X));
        double top = Math.Max(Math.Max(0, view.Min(point => point.Y)), hand.Min(point => point.Y));
        double right = Math.Min(view.Max(point => point.X), hand.Max(point => point.X));
        double bottom = Math.Min(view.Max(point => point.Y), hand.Max(point => point.Y));
        if (right <= left || bottom <= top) return null;
        var expected = hint with { CandidateBounds = new(left, top, right - left, bottom - top) };
        return expected.ValidatedCandidateBounds is null ? null : expected;
    }

    // Long-press buttons act on covered-caption evidence alone. Fingers on them
    // must not start a hand-model search, so no spotlight or fingertip marks.
    private PixelPoint[][] HoldControlOutlines() => _boardSession.Buttons.Where(button => button.IsHold)
        .Select(button => new[]
            {
                new Point2(button.Bounds.X, button.Bounds.Y), new Point2(button.Bounds.X + button.Bounds.Width, button.Bounds.Y),
                new Point2(button.Bounds.X + button.Bounds.Width, button.Bounds.Y + button.Bounds.Height),
                new Point2(button.Bounds.X, button.Bounds.Y + button.Bounds.Height)
            }
            .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
            .Select(point => new PixelPoint(point.X, point.Y)).ToArray())
        .ToArray();

    private bool HasCurrentControlObstruction(HandAcquisitionHint hint, DateTimeOffset frameTime) =>
        hint.ObservedAt == frameTime && hint.ControlCoverage is double coverage &&
        double.IsFinite(coverage) && coverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage &&
        hint.ControlTriggerCoverage is double triggerCoverage &&
        double.IsFinite(triggerCoverage) && triggerCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage;

    public object GetHandAcquisitionDiagnostics()
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            var context = GetHandAcquisitionContext(now);
            return new { revision = _acquisitionRevision, reason = _acquisitionReason,
                minimumControlCoverage = HandAcquisitionPresenceTracker.MinimumControlCoverage,
                restrictAcquisitionToSearchRegions = context?.RestrictAcquisitionToSearchRegions,
                allowsSearchIllumination = context?.AllowsSearchIllumination,
                continuousSearchPolygon = context?.ContinuousSearchPolygon,
                observingMotion = context?.ObserveMotion ?? false, searchPolygon = context?.SearchPolygon,
                searchLightActive = context?.IlluminatedHint is not null, light = _acquisitionLight,
                lightUntil = _acquisitionLightUntil, lightCount = _acquisitionLightCount, lightLevel = _lightLevel,
                expectedSceneReady = _acquisitionExpectedScene is not null,
                expectedSceneError = _acquisitionReferenceError,
                hint = context?.IlluminatedHint };
        }
    }

    private void ClearAcquisitionLight()
    {
        RetainPaintButtonLightExclusion();
        _acquisitionHint = null;
        _acquisitionLight = null;
        _acquisitionLightUntil = DateTimeOffset.MinValue;
        _acquisitionLightStarted = DateTimeOffset.MinValue;
    }

    private HandAcquisitionSceneImage? CaptureExpectedAcquisitionScene()
    {
        var photoCopy = _boardSession.Screen == BoardScreen.PhotoCopy;
        var paint = _boardSession.Screen == BoardScreen.Paint;
        var globe = _boardSession.Screen == BoardScreen.Globe;
        var slots = _boardSession.Screen == BoardScreen.Slots;
        bool roulette = _boardSession.Screen == BoardScreen.Roulette;
        if (_boardApplicationTarget is null || _renderedBoardState is not { } rendered ||
            rendered.Screen != _boardSession.Screen) return null;
        if (!photoCopy && !globe && !roulette && (rendered.HoverMask != 0 || rendered.FingerSelectionStep != 0)) return null;
        if (_boardSession.Screen == BoardScreen.Blackjack &&
            (rendered.BlackjackRevision != _boardSession.BlackjackState.Revision ||
            rendered.BlackjackFlightRevision != _blackjackFlightRevision ||
            HasBlackjackCardAnimation(_blackjackClock()))) return null;
        if (_boardSession.Screen == BoardScreen.Monopoly &&
            (rendered.MonopolyRevision != _boardSession.MonopolyState.Revision ||
            rendered.MonopolyDiceRevision != MonopolyDicePresentationRevision ||
            rendered.MonopolySessionRevision != _boardSession.Revision ||
            MonopolyEntranceActive || HasMonopolyDiceAnimation(_monopolyClock()) || HasMonopolyDrawerAnimation(_monopolyClock()) ||
            GetCrownDeedDevelopmentFrame(_monopolyClock())?.Active == true)) return null;
        if (slots && rendered.SlotsRevision != _boardSession.SlotsState.Revision) return null;
        if (photoCopy && (rendered.PhotoUiRevision != _boardSession.Revision ||
            HasPhotoCopyDrawerAnimation(_blackjackClock()))) return null;
        if (roulette && rendered.RouletteRevision != _boardSession.RouletteState.Revision) return null;
        if (_boardSession.Screen == BoardScreen.Menu &&
            (_boardSession.MenuScrolling || rendered.MenuFrame != MenuVisualFrame(_blackjackClock()))) return null;
        if (globe && (rendered.GlobeRevision != _boardSession.GetGlobeSnapshot(_globeClock()).Revision ||
            rendered.GlobeSessionRevision != _boardSession.Revision || HasGlobeDrawerAnimation(_globeClock()))) return null;
        var cameraToProjector = _boardCameraMap!.ToMatrix();
        var projectorToBoard = _boardSurfaceMap!.Inverse().ToMatrix();
        var cameraToBoard = new double[9];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        for (int middle = 0; middle < 3; middle++)
            cameraToBoard[row * 3 + column] += projectorToBoard[row * 3 + middle] * cameraToProjector[middle * 3 + column];
        // Capture the unlit generated table once per known scene, before the
        // separate hand/search-light overlay. This is not a webcam background.
        try
        {
            // Detection needs a bounded board-UV reference, not a readback of
            // every projector pixel. Keep this analysis image independent of
            // the display cache's resolution and Windows display scaling.
            if (_acquisitionReferenceTarget is null ||
                _acquisitionReferenceTarget.Device != _boardApplicationTarget.Device)
            {
                _acquisitionReferenceTarget?.Dispose();
                _acquisitionReferenceTarget = new CanvasRenderTarget(_boardApplicationTarget.Device,
                    BoardSurfaceSize, BoardSurfaceSize, 96);
            }
            var sourceSize = _boardApplicationTarget.SizeInPixels;
            using (var drawing = _acquisitionReferenceTarget.CreateDrawingSession())
            {
                if (roulette)
                {
                    // Circular chips expose the static felt around them. Include
                    // that exact backing; the animated wheel is a separate layer.
                    DrawRouletteBoard(drawing, _boardSession.RouletteState, _boardSession.Buttons, [], [], PaintBoardAspect());
                }
                else if (slots)
                {
                    // Reels animate beside the controls; only the fixed, opaque
                    // buttons may explain camera interference, as on Globe.
                    DrawSlotControlsReference(drawing);
                }
                else if (globe)
                {
                    // The sphere rotates independently. Only fixed, opaque controls
                    // may explain camera interference or suggest hand illumination.
                    drawing.Clear(Colors.Black);
                    DrawGlobeControls(drawing, _boardSession.Buttons, [], [],
                        drawerOpen: _boardSession.GlobeDrawerOpen, drawerProgress: GlobeDrawerProgress(_globeClock()),
                        boardAspect: PaintBoardAspect());
                }
                else if (photoCopy || paint)
                {
                    // Compare only the opaque interiors of the generated controls.
                    // Swirl stamps, object lighting and status text change independently
                    // and must never look like an arriving hand.
                    drawing.Clear(paint ? PaintColor(3, 5, 12) : AppPalette.PhotoCopyBackground);
                    if (photoCopy) DrawPhotoCopyControls(drawing, _boardSession.Buttons, [], [], _blackjackClock());
                    else foreach (var button in _boardSession.Buttons) DrawPaintButton(drawing, button, false, []);
                }
                else
                {
                    drawing.Clear(Colors.Transparent);
                    drawing.DrawImage(_boardApplicationTarget,
                        new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize),
                        new Rect(0, 0, sourceSize.Width, sourceSize.Height),
                        1, CanvasImageInterpolation.HighQualityCubic);
                }
            }
            var pixels = _acquisitionReferenceTarget.GetPixelBytes();
            _acquisitionReferenceError = null;
            var regions = _boardSession.Buttons.Select(button =>
                roulette && button.Id.StartsWith("roulette-chip-", StringComparison.Ordinal)
                    ? RouletteChipSearchRegion(button)
                    : new HandTrackingBounds(button.Bounds.X + .012, button.Bounds.Y + .012,
                    button.Bounds.Width - .024, button.Bounds.Height - .024)).ToArray();
            // One Back button is not enough to fit the camera's color response
            // when fingers cover most of it. Include fixed, opaque UI nearby as
            // a lighting reference, without allowing it to suggest a search light.
            IReadOnlyList<HandTrackingBounds>? referenceRegions = _boardSession.Screen switch
            {
                BoardScreen.HandTracking => [new(.40, .065, .53, .04)],
                BoardScreen.Settings => [new(.40, .065, .53, .04), .. regions],
                // The plain Paint title and surrounding artwork have no opaque
                // panel. Use the generated button interiors as lighting anchors.
                BoardScreen.Paint => regions,
                BoardScreen.Globe => regions,
                BoardScreen.Slots => regions,
                BoardScreen.Roulette => regions,
                // Fixed deed plaques and gold trim constrain the camera response
                // when a hand covers the only gold action panel. These bands
                // calibrate colour only; searches remain inside the controls.
                BoardScreen.Monopoly => [.. regions,
                    new(.18, .045, .64, .12), new(.18, .835, .64, .12)],
                BoardScreen.Blackjack => [new(.05, .24, .90, .50), new(.31, .05, .63, .095)],
                _ => null
            };
            return new((int)BoardSurfaceSize, (int)BoardSurfaceSize, pixels, cameraToBoard,
                BoardSearchRegions: regions, BoardReferenceRegions: referenceRegions,
                BoardTriggerRegions: _boardSession.Buttons
                    .Select(button => BoardButtonTextRegion(_acquisitionReferenceTarget.Device, button)).ToArray(),
                AllowsLocalForegroundContext: _boardSession.Screen is BoardScreen.Menu or BoardScreen.Settings or
                    BoardScreen.HandTracking or BoardScreen.Blackjack or BoardScreen.Monopoly);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or
            InvalidOperationException or ObjectDisposedException)
        {
            // Device recreation must not escape the camera callback or latch
            // hand inference busy. Existing tracked hands and Photo Copy's
            // capture-field searches can continue while the device recovers.
            _acquisitionReferenceError = error.Message;
            _acquisitionReferenceRetryAt = _blackjackClock() + TimeSpan.FromSeconds(1);
            return null;
        }
    }

    // Already inside the physical board clip; never paint beyond it or feed
    // this exploratory illumination back as a confirmed hand or button input.
    private void DrawHandAcquisitionLight(CanvasDrawingSession ds, Rect output)
    {
        var now = _blackjackClock();
        if (!AcquisitionBoardReady || AcquisitionMustYieldToHandOrExecute(now) ||
            now >= _acquisitionLightUntil || _acquisitionLight is not { } light ||
            _acquisitionScene != CurrentAcquisitionState() ||
            !_acquisitionButtons.SequenceEqual(_boardSession.Buttons) ||
            HasBoardControlDrawerAnimation() ||
            _boardSession.Screen == BoardScreen.Blackjack && HasBlackjackCardAnimation(now) ||
            _boardSession.Screen == BoardScreen.Monopoly &&
                (MonopolyEntranceActive || HasMonopolyDiceAnimation(_monopolyClock()) || HasMonopolyDrawerAnimation(_monopolyClock()))) return;
        var center = new Vector2((float)(output.X + light.Center.X * output.Width),
            (float)(output.Y + light.Center.Y * output.Height));
        float radius = (float)(light.Radius * output.Height);
        using var brush = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = LightWhite(255) },
            new() { Position = .82f, Color = LightWhite(255) },
            new() { Position = 1, Color = LightWhite(0) }
        ]) { Center = center, RadiusX = radius, RadiusY = radius };
        ds.FillCircle(center, radius, brush);
    }
}
