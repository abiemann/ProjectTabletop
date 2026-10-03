using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // A square logical layout is fitted to the physical board by the very same
    // homography used in reverse for hit testing. Buttons never occupy clipped
    // corners of the board's projector-space bounding rectangle.
    private const float BoardSurfaceSize = 1000;
    private readonly BoardSession _boardSession;
    private CanvasRenderTarget? _boardApplicationTarget;
    private BoardSurfaceState? _renderedBoardState;

    private readonly record struct BoardSurfaceState(BoardScreen Screen, int HoverMask, int FingerSelectionStep, int HandStatus,
        int PhotoStampCount, string? PhotoStatus, long PhotoRevision, long BlackjackRevision, long BlackjackFlightRevision,
        long PaintRevision, string? PaintStatus, bool PaintSaveEnabled, long MonopolyRevision, long MonopolyDiceRevision,
        long MonopolySessionRevision, int MonopolyDrawerFrame, long MonopolyEntranceRevision, int MonopolyEntranceFrame,
        long GlobeRevision, long GlobeFrame, long GlobeSessionRevision, long SlotsRevision, double SlotsAspect, int MenuFrame,
        long RouletteRevision, double RouletteAspect, string? RouletteHover);

    public SceneCompositor(BlackjackGame? blackjack = null, Func<DateTimeOffset>? blackjackClock = null,
        Func<DateTimeOffset>? boardRevealClock = null, Func<DateTimeOffset>? paintClock = null,
        MonopolyGame? monopoly = null, Func<DateTimeOffset>? monopolyClock = null,
        GlobeState? globe = null, Func<DateTimeOffset>? globeClock = null, SlotGame? slots = null, RouletteGame? roulette = null)
    {
        _boardSession = new BoardSession(blackjack, monopoly, globe, slots, roulette);
        _blackjackClock = blackjackClock ?? (() => DateTimeOffset.UtcNow);
        _boardRevealClock = boardRevealClock ?? (() => DateTimeOffset.UtcNow);
        _paintClock = paintClock ?? (() => DateTimeOffset.UtcNow);
        _monopolyClock = monopolyClock ?? blackjackClock ?? (() => DateTimeOffset.UtcNow);
        _globeClock = globeClock ?? blackjackClock ?? (() => DateTimeOffset.UtcNow);
        _boardSession.BlackjackHitOccurred += OnBlackjackHit;
        _boardSession.BlackjackDealOccurred += OnBlackjackDeal;
        _boardSession.MonopolyRollOccurred += OnMonopolyRoll;
        _boardSession.BoardOpened += OnBoardOpened;
    }

    public BoardScreen CurrentBoardScreen
    {
        get { lock (_gate) return _boardSession.Screen; }
    }

    public string CurrentBoardTitle
    {
        get { lock (_gate) return _boardSession.Title; }
    }

    public string HandTrackingTestStatus
    {
        get { lock (_gate) return _boardSession.Screen == BoardScreen.HandTracking
            ? HandTestCaption(HandStatusAt(DateTimeOffset.UtcNow)) : string.Empty; }
    }

    private int HandStatusAt(DateTimeOffset now)
    {
        if (_handFrameTime > now || now - _handFrameTime > TimeSpan.FromMilliseconds(350) || _handTips.Length == 0)
            return 0;
        if (_handTips.Any(cursor => cursor.IsSpreadOut)) return 3;
        if (_handTips.Any(cursor => now < cursor.ExecuteUntil)) return 2;
        return _handTips.Any(cursor => cursor.FourFingersExtended) ? 4 : 1;
    }

    private static string HandTestCaption(int status) => status switch
    {
        3 => "Spread out hand",
        2 => "PINCH DETECTED",
        4 => "Together, then separate index",
        1 => "Pinch: red circle for one second",
        _ => "Waiting for a hand"
    };

    public IReadOnlyList<string> HoveredBoardButtons
    {
        get
        {
            lock (_gate)
                return DateTimeOffset.UtcNow - _handFrameTime <= TimeSpan.FromMilliseconds(350)
                    ? _boardSession.HoveredButtonIds.ToArray() : Array.Empty<string>();
        }
    }

    public void ShowBoardMenu()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowMenu(_blackjackClock());
            SyncPhotoCopySession();
        }
    }

    public void ShowHandTrackingTest()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowHandTrackingTest();
            SyncPhotoCopySession();
        }
    }

    private void DrawBoardApplication(CanvasDrawingSession ds, Rect output, bool preview)
    {
        try { DrawBoardApplicationCore(ds, output, preview); }
        catch (Exception error) when (_boardSession.Screen == BoardScreen.PhotoCopy &&
            !ds.Device.IsDeviceLost(error.HResult))
        {
            // EndDraw on the cached drawing session may report deferred GPU
            // errors after individual stamp calls have already returned.
            HandlePhotoCopyRenderFailure(error);
        }
    }

    private void DrawBoardApplicationCore(CanvasDrawingSession ds, Rect output, bool preview)
    {
        if (_boardSurfaceMap is null) return;
        SyncPhotoCopySession();
        SyncPaintSession();
        ReserveProjectedBoardPixels(ds, output, preview);
        if (EnsureBoardRenderTarget(ref _boardApplicationTarget, ds.Device)) _renderedBoardState = null;

        var now = DateTimeOffset.UtcNow;
        var blackjackNow = _blackjackClock();
        _boardSession.TickMenu(blackjackNow);
        TickBlackjackVisuals(blackjackNow);
        var monopolyNow = _monopolyClock();
        var monopolyEntrance = GetMonopolyEntranceFrame(monopolyNow);
        if (monopolyEntrance?.Active != true) _boardSession.TickMonopoly(monopolyNow);
        var monopolyPresented = MonopolyPresentedState(monopolyNow);
        var monopolyDicePresented = HasMonopolyDicePresentation(monopolyNow);
        var flights = GetBlackjackFlights(blackjackNow);
        var deal = GetBlackjackDealFrame(blackjackNow);
        var photoCopy = _boardSession.Screen == BoardScreen.PhotoCopy;
        var paint = _boardSession.Screen == BoardScreen.Paint;
        var paintNow = _paintClock();
        var globeNow = _globeClock();
        var globe = _boardSession.Screen == BoardScreen.Globe;
        if (globe) _boardSession.TickGlobe(globeNow);
        var slots = _boardSession.Screen == BoardScreen.Slots;
        if (slots) _boardSession.TickSlots(blackjackNow);
        bool roulette = _boardSession.Screen == BoardScreen.Roulette;
        if (roulette) _boardSession.TickRoulette(blackjackNow);
        if (paint) AdvancePaintAutomatic(paintNow);
        _boardSession.PaintSaveEnabled = paint && CanSavePaint;
        if (photoCopy && !preview && PhotoCopyCaptureAllowed)
            MarkPhotoCopySurfacePresented(now);
        var handsFresh = _handFrameTime <= now &&
            now - _handFrameTime <= TimeSpan.FromMilliseconds(350);
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback = handsFresh
            ? _boardSession.FingerSelectionFeedback : Array.Empty<BoardFingerSelectionFeedback>();
        var handStatus = HandStatusAt(now);
        var hoverMask = 0;
        for (var index = 0; index < _boardSession.Buttons.Count; index++)
            if (handsFresh && _boardSession.HoveredButtonIds.Contains(_boardSession.Buttons[index].Id))
                hoverMask |= 1 << index;
        var state = new BoardSurfaceState(_boardSession.Screen, hoverMask, FingerSelectionRenderStep(selectionFeedback),
            handStatus,
            photoCopy ? PhotoCopyStampCount(now) : 0,
            photoCopy ? PhotoCopyDisplayStatus(now) : null,
            photoCopy ? _photoCopyRevision : 0,
            _boardSession.Screen == BoardScreen.Blackjack ? _boardSession.BlackjackState.Revision : 0,
            _blackjackFlightRevision, paint ? PaintVisualRevision(paintNow) : 0,
            paint ? GetPaintSaveStatus(paintNow) : null, paint && _boardSession.PaintSaveEnabled,
            _boardSession.Screen == BoardScreen.Monopoly ? _boardSession.MonopolyState.Revision : 0,
            _boardSession.Screen == BoardScreen.Monopoly ? MonopolyDicePresentationRevision : 0,
            _boardSession.Screen == BoardScreen.Monopoly ? _boardSession.Revision : 0,
            _boardSession.Screen == BoardScreen.Monopoly ? MonopolyDrawerFrame(monopolyNow) : -1,
            _boardSession.Screen == BoardScreen.Monopoly ? MonopolyEntranceRevision : 0,
            MonopolyEntranceRenderFrame(monopolyEntrance),
            globe ? _boardSession.GetGlobeSnapshot(globeNow).Revision : 0,
            globe ? GlobeVisualFrame(globeNow) : 0,
            globe ? _boardSession.Revision : 0,
            slots ? _boardSession.SlotsState.Revision : 0, slots ? PaintBoardAspect() : 0, MenuVisualFrame(blackjackNow),
            roulette ? _boardSession.RouletteState.Revision : 0, roulette ? PaintBoardAspect() : 0,
            roulette && handsFresh ? string.Join('|', _boardSession.HoveredButtonIds) : null);
        // Cursor motion is drawn separately. Reuse the UI texture until its
        // screen, hovered button, or gesture status changes on either canvas.
        if (_renderedBoardState != state)
        {
            using var surface = _boardApplicationTarget!.CreateDrawingSession();
            surface.Transform = BoardRasterTransform(_boardApplicationTarget);
            surface.Clear(photoCopy ? AppPalette.PhotoCopyBackground : _boardSession.Screen == BoardScreen.HandTracking
                ? Colors.Transparent : AppPalette.Background);
            if (paint)
                DrawPaintSurface(surface, paintNow);
            else if (photoCopy)
            {
                DrawPhotoCopyStamps(surface, state.PhotoStampCount);
                DrawPhotoCopyObjectSpotlight(surface);
            }
            else if (_boardSession.Screen is not (BoardScreen.HandTracking or BoardScreen.Blackjack or BoardScreen.Monopoly or
                BoardScreen.Globe or BoardScreen.Slots or BoardScreen.Roulette))
                DrawMetalBackdrop(surface, drawFooterDivider: _boardSession.Screen != BoardScreen.Menu);
            using var heading = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 54,
                FontWeight = FontWeights.SemiBold,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var label = BoardButtonTextFormat();
            using var body = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 23,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var small = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 20,
                WordWrapping = CanvasWordWrapping.NoWrap
            };

            var muted = AppPalette.MutedText;
            if (_boardSession.Screen == BoardScreen.Blackjack)
            {
                DrawBlackjackTable(surface, _boardSession.BlackjackState, _boardSession.Buttons,
                    handsFresh ? _boardSession.HoveredButtonIds : Array.Empty<string>(), selectionFeedback,
                    HiddenBlackjackCards(flights), deal);
            }
            else if (_boardSession.Screen == BoardScreen.Monopoly)
            {
                DrawMonopolyBoard(surface, monopolyPresented, _boardSession.Buttons,
                    handsFresh ? _boardSession.HoveredButtonIds : Array.Empty<string>(), selectionFeedback,
                    PaintBoardAspect(), hideDiceDisplay: monopolyDicePresented,
                    rolling: HasMonopolyDiceAnimation(monopolyNow), drawerOpen: _boardSession.MonopolyDrawerOpen,
                    drawerProgress: MonopolyDrawerProgress(monopolyNow), entrance: monopolyEntrance);
            }
            else if (slots)
                DrawSlotsMachine(surface, _boardSession.SlotsState, _boardSession.Buttons, PaintBoardAspect());
            else if (roulette)
                DrawRouletteBoard(surface, _boardSession.RouletteState, _boardSession.Buttons,
                    handsFresh ? _boardSession.HoveredButtonIds : [], selectionFeedback, PaintBoardAspect());
            else if (globe)
            {
                DrawGlobeBoard(surface, _boardSession.GetGlobeSnapshot(globeNow), _boardSession.Buttons,
                    handsFresh ? _boardSession.HoveredButtonIds : Array.Empty<string>(), selectionFeedback, PaintBoardAspect(),
                    drawerOpen: _boardSession.GlobeDrawerOpen, drawerProgress: GlobeDrawerProgress(globeNow));
            }
            else if (_boardSession.Screen == BoardScreen.Menu)
            {
                DrawMenuSurface(surface, blackjackNow, handsFresh, heading, label, small, selectionFeedback);
            }
            else
            {
                foreach (var button in _boardSession.Buttons)
                    if (_boardSession.Screen == BoardScreen.Settings && button.Destination == BoardScreen.HandTracking)
                        DrawMenuButton(surface, button, handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id),
                            label, small, selectionFeedback);
                    else if (photoCopy)
                        DrawPhotoCopyButton(surface, button, handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id),
                            small, selectionFeedback);
                    else if (paint)
                        DrawPaintButton(surface, button, handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id),
                            selectionFeedback);
                    else
                        DrawBoardButton(surface, button,
                            handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id), label, small, selectionFeedback);

                if (paint)
                {
                    DrawBoardTitle(surface, "PAINT");
                    if (state.PaintStatus is { } paintStatus)
                    {
                        using var statusFormat = new CanvasTextFormat
                        {
                            FontFamily = "Segoe UI", FontSize = 20,
                            HorizontalAlignment = CanvasHorizontalAlignment.Center,
                            VerticalAlignment = CanvasVerticalAlignment.Center,
                            WordWrapping = CanvasWordWrapping.NoWrap
                        };
                        var statusBounds = new Rect(PaintStatusBounds.X * BoardSurfaceSize,
                            PaintStatusBounds.Y * BoardSurfaceSize, PaintStatusBounds.Width * BoardSurfaceSize,
                            PaintStatusBounds.Height * BoardSurfaceSize);
                        DrawGlassPanel(surface, statusBounds);
                        surface.DrawText(paintStatus, statusBounds,
                            paintStatus == "Image Saved" ? AppPalette.IndicatorOn : AppPalette.Text, statusFormat);
                    }
                }
                else if (photoCopy)
                {
                    // Opaque panels keep copied images behind the lower controls.
                    // Hand illumination is drawn later, across the whole board.
                    DrawBoardTitle(surface, "PHOTO COPY");
                    using var photoStatus = new CanvasTextFormat
                    {
                        FontFamily = "Segoe UI",
                        FontSize = state.PhotoStatus == "Image Saved" ? 27 : 21,
                        VerticalAlignment = CanvasVerticalAlignment.Center,
                        WordWrapping = CanvasWordWrapping.Wrap
                    };
                    surface.FillRoundedRectangle(new Rect(80, 740, 840, 65), 12, 12,
                        Windows.UI.Color.FromArgb(255, 185, 185, 185));
                    surface.DrawText(state.PhotoStatus ?? PhotoCopyReadyMessage,
                        new Rect(98, 745, 804, 55), Colors.Black, photoStatus);
                }
                else if (_boardSession.Screen == BoardScreen.Settings)
                {
                    // The same fixed glass header as the tester: a lighting
                    // reference while fingers cover the Back button.
                    DrawGlassPanel(surface, new Rect(390, 55, 550, 105));
                    surface.DrawText("Settings", 414, 69, AppPalette.Text, body);
                    surface.DrawText("Tools for checking and tuning the table", 414, 113, AppPalette.IndicatorOn, small);
                }
                else if (_boardSession.Screen == BoardScreen.HandTracking)
                {
                    DrawGlassPanel(surface, new Rect(390, 55, 550, 105));
                    surface.DrawText("Hand-Tracking", 414, 69, AppPalette.Text, body);
                    surface.DrawCircle(new Vector2(906, 88), 9, AppPalette.AccentSecondary, 2);
                    surface.FillCircle(new Vector2(906, 88), 3, AppPalette.IndicatorOn);
                    var statusColor = state.HandStatus == 2 ? Colors.Red : AppPalette.IndicatorOn;
                    var status = HandTestCaption(state.HandStatus);
                    surface.DrawText(status, 414, 113, statusColor, small);
                    DrawEstimatedBoardSize(surface, body, small);
                }
                else
                {
                    surface.DrawText("PROJECT TABLETOP", 390, 85, muted, small);
                    DrawGlassPanel(surface, new Rect(80, 270, 840, 445));
                    surface.DrawText("COMING SOON", 119, 307, AppPalette.AccentSecondary, small);
                    surface.DrawText(_boardSession.Title, 115, 355, AppPalette.Text, heading);
                    DrawBoardSymbol(surface, _boardSession.Screen, new Vector2(813, 384), 1.85f, AppPalette.IndicatorOn);
                    surface.DrawLine(120, 453, 880, 453, AppPalette.MetalEdge, 1);
                    surface.DrawText("A new way to play is on its way.", 120, 494, AppPalette.Text, body);
                    surface.DrawText("Choose another board from the menu.", 120, 548, muted, body);
                    surface.DrawText("YOUR TABLE. MORE POSSIBILITIES.", 120, 660, muted, small);
                }
            }
            _renderedBoardState = state;
        }

        if (paint && !preview) CapturePaintExpectedFrame(ds.Device, paintNow);

        var h = _boardSurfaceMap.ToMatrix();
        var pixels = _boardApplicationTarget!.SizeInPixels;
        // System.Numerics uses row vectors. Include the source-pixel → UV scale,
        // the projective denominator, and the preview's fitted output rectangle.
        // Dividing the resulting XY by W exactly matches Homography.Transform.
        var matrix = new Matrix4x4(
            (float)((output.Width * h[0] + output.X * h[6]) / pixels.Width),
            (float)((output.Height * h[3] + output.Y * h[6]) / pixels.Width), 0,
            (float)(h[6] / pixels.Width),
            (float)((output.Width * h[1] + output.X * h[7]) / pixels.Height),
            (float)((output.Height * h[4] + output.Y * h[7]) / pixels.Height), 0,
            (float)(h[7] / pixels.Height),
            0, 0, 1, 0,
            (float)(output.Width * h[2] + output.X * h[8]),
            (float)(output.Height * h[5] + output.Y * h[8]), 0, (float)h[8]);
        using var perspective = new Transform3DEffect
        {
            Source = _boardApplicationTarget,
            TransformMatrix = matrix,
            InterpolationMode = CanvasImageInterpolation.Linear,
            BorderMode = EffectBorderMode.Soft
        };
        ds.DrawImage(perspective);
        if (DrawBlackjackFlightLayer(ds.Device, flights, deal) is { } flightLayer)
        {
            using var moving = new Transform3DEffect
            {
                Source = flightLayer, TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
            };
            ds.DrawImage(moving);
        }
        if (DrawMonopolyDiceLayer(ds.Device, monopolyNow, PaintBoardAspect()) is { } diceLayer)
        {
            using var movingDice = new Transform3DEffect
            {
                Source = diceLayer, TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
            };
            ds.DrawImage(movingDice);
        }
        if (slots && DrawSlotsReelLayer(ds.Device, blackjackNow, PaintBoardAspect()) is { } reelLayer)
        {
            using var reels = new Transform3DEffect
            {
                Source = reelLayer, TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
            };
            ds.DrawImage(reels);
        }
        if (roulette && DrawRouletteMotionLayer(ds.Device, blackjackNow, PaintBoardAspect()) is { } rouletteLayer)
        {
            using var moving = new Transform3DEffect
            {
                Source = rouletteLayer, TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
            };
            ds.DrawImage(moving);
        }
        if (DrawHoldFeedbackLayer(ds.Device) is { } holdLayer)
        {
            using var heldRims = new Transform3DEffect
            {
                Source = holdLayer, TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
            };
            ds.DrawImage(heldRims);
        }
    }

    private void DrawMenuButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label, CanvasTextFormat small,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered,
            (inside, radius) => DrawMenuPreview(ds, rect, inside, radius, button.Destination));
        ds.DrawText(button.Label, (float)rect.X + 32, (float)rect.Y + 52,
            AppPalette.ButtonText, label);
        var description = button.Destination switch
        {
            BoardScreen.HandTracking => "Test gestures",
            BoardScreen.Slots => "Win all the treasure",
            BoardScreen.PhotoCopy => "Copy hands and objects",
            BoardScreen.Blackjack => "Play against the dealer",
            BoardScreen.Paint => "Liquid colour & metallic ink",
            BoardScreen.Monopoly => "Play with humans and AI",
            BoardScreen.Globe => "Zoom and rotate Earth",
            BoardScreen.Roulette => "Place your chips and spin",
            _ => "Coming soon"
        };
        ds.DrawText(description, (float)rect.X + 32, (float)rect.Y + 111, AppPalette.MutedText, small);
        DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, AppPalette.IndicatorOn);
    }

    private static void DrawBoardTitle(CanvasDrawingSession ds, string title)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 20,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        ds.DrawText(title, 61, 18, AppPalette.MutedText, format);
    }

    private static void DrawPaintButton(CanvasDrawingSession ds, BoardButton button, bool hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered && button.Enabled);
        using var text = PaintButtonTextFormat();
        ds.DrawText(button.Label, new Rect(rect.X + 20, rect.Y + 6, rect.Width - 40, rect.Height - 18),
            button.Enabled ? AppPalette.ButtonText : AppPalette.MutedText, text);
        DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, AppPalette.IndicatorOn);
    }

    private static CanvasTextFormat PaintButtonTextFormat() => new()
    {
        FontFamily = "Segoe UI", FontSize = 36, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static CanvasTextFormat BoardButtonTextFormat() => new()
    {
        FontFamily = "Segoe UI", FontSize = 32, FontWeight = FontWeights.SemiBold,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static HandTrackingBounds PaintButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        var b = button.Bounds;
        using var format = PaintButtonTextFormat();
        using var layout = new CanvasTextLayout(device, button.Label, format,
            (float)(b.Width * BoardSurfaceSize - 40), (float)(b.Height * BoardSurfaceSize - 18));
        return ButtonInkRegion(button, layout.DrawBounds, b.X * BoardSurfaceSize + 20, b.Y * BoardSurfaceSize + 6);
    }

    private static void DrawBoardButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label, CanvasTextFormat small, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        string? description = null)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered && button.Enabled);
        var x = (float)rect.X + 33;
        var y = (float)rect.Y + (description is null ? 27 : 33);
        ds.DrawText(button.Label, x, y, button.Enabled ? AppPalette.ButtonText : AppPalette.MutedText, label);
        if (description is not null)
            ds.DrawText(description, x, (float)rect.Y + 100,
                AppPalette.ButtonText, small);
        DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, AppPalette.IndicatorOn, showCaption: description is null);
    }

}
