using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // A square logical surface is fitted to the physical board by the very same
    // homography used in reverse for hit testing. Buttons never occupy clipped
    // corners of the board's projector-space bounding rectangle.
    private const float BoardSurfaceSize = 1000;
    private readonly BoardSession _boardSession;
    private CanvasRenderTarget? _boardApplicationTarget;
    private BoardSurfaceState? _renderedBoardState;

    private readonly record struct BoardSurfaceState(BoardScreen Screen, int HoverMask, int FingerSelectionStep, int HandStatus,
        int PhotoStampCount, string? PhotoStatus, long PhotoRevision, long BlackjackRevision, long BlackjackFlightRevision);

    public SceneCompositor(BlackjackGame? blackjack = null, Func<DateTimeOffset>? blackjackClock = null,
        Func<DateTimeOffset>? boardRevealClock = null)
    {
        _boardSession = new BoardSession(blackjack);
        _blackjackClock = blackjackClock ?? (() => DateTimeOffset.UtcNow);
        _boardRevealClock = boardRevealClock ?? (() => DateTimeOffset.UtcNow);
        _boardSession.BlackjackHitOccurred += OnBlackjackHit;
        _boardSession.BlackjackDealOccurred += OnBlackjackDeal;
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
            _boardSession.ShowMenu();
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
        if (_boardApplicationTarget is null || _boardApplicationTarget.Device != ds.Device)
        {
            _boardApplicationTarget?.Dispose();
            _boardApplicationTarget = new CanvasRenderTarget(ds.Device,
                BoardSurfaceSize, BoardSurfaceSize, 96);
            _renderedBoardState = null;
        }

        var now = DateTimeOffset.UtcNow;
        var blackjackNow = _blackjackClock();
        TickBlackjackVisuals(blackjackNow);
        var flights = GetBlackjackFlights(blackjackNow);
        var deal = GetBlackjackDealFrame(blackjackNow);
        var photoCopy = _boardSession.Screen == BoardScreen.PhotoCopy;
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
            _blackjackFlightRevision);
        // Cursor motion is drawn separately. Reuse the UI texture until its
        // screen, hovered button, or gesture status changes on either canvas.
        if (_renderedBoardState != state)
        {
            using var surface = _boardApplicationTarget.CreateDrawingSession();
            surface.Clear(photoCopy ? AppPalette.PhotoCopyBackground : _boardSession.Screen == BoardScreen.HandTracking
                ? Colors.Transparent : AppPalette.Background);
            if (photoCopy)
            {
                DrawPhotoCopyStamps(surface, state.PhotoStampCount);
                DrawPhotoCopyObjectSpotlight(surface);
            }
            else if (_boardSession.Screen is not (BoardScreen.HandTracking or BoardScreen.Blackjack))
                DrawMetalBackdrop(surface);
            using var heading = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 54,
                FontWeight = FontWeights.SemiBold,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var label = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 32,
                FontWeight = FontWeights.SemiBold,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
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
            else if (_boardSession.Screen == BoardScreen.Menu)
            {
                surface.DrawLine(80, 65, 109, 65, AppPalette.IndicatorOn, 3);
                surface.DrawText("PROJECT TABLETOP", 125, 51, AppPalette.MutedText, small);
                surface.DrawText("06  /  BOARDS", 771, 54, AppPalette.AccentSecondary, small);
                surface.DrawText("Choose a board", 76, 99, AppPalette.Text, heading);
                surface.DrawText("Four fingers together. Aim, then move index sideways.", 80, 182, muted, body);
                for (int index = 0; index < _boardSession.Buttons.Count; index++)
                {
                    var button = _boardSession.Buttons[index];
                    var hovered = handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id);
                    DrawMenuButton(surface, button, hovered, label, small, index + 1, selectionFeedback);
                }
                surface.FillCircle(new Vector2(88, 894), 4, AppPalette.IndicatorOn);
                surface.DrawText(FingerSelectionCaption(selectionFeedback, "Bring fingers together"), 105, 877, AppPalette.Text, body);
                surface.DrawText("Aim with your middle fingertip. Bring fingers together to select again.", 80, 923, muted, small);
            }
            else
            {
                foreach (var button in _boardSession.Buttons)
                    if (photoCopy)
                        DrawPhotoCopyButton(surface, button, handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id),
                            small, selectionFeedback);
                    else
                        DrawBoardButton(surface, button,
                            handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id), label, small, selectionFeedback);

                if (photoCopy)
                {
                    // Opaque panels keep copied images below the controls.
                    // Hand illumination is drawn later, across the whole board.
                    surface.DrawText("PHOTO COPY", 61, 18, AppPalette.AccentSecondary, small);
                    using var photoStatus = new CanvasTextFormat
                    {
                        FontFamily = "Segoe UI",
                        FontSize = state.PhotoStatus == "Image Saved" ? 27 : 19,
                        WordWrapping = CanvasWordWrapping.Wrap
                    };
                    DrawGlassPanel(surface, new Rect(50, 165, 900, 50));
                    surface.DrawText(state.PhotoStatus ?? PhotoCopyReadyMessage,
                        new Rect(69, 170, 862, 44), AppPalette.Text, photoStatus);
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

        var h = _boardSurfaceMap.ToMatrix();
        // System.Numerics uses row vectors. Include the source-pixel → UV scale,
        // the projective denominator, and the preview's fitted output rectangle.
        // Dividing the resulting XY by W exactly matches Homography.Transform.
        var matrix = new Matrix4x4(
            (float)((output.Width * h[0] + output.X * h[6]) / BoardSurfaceSize),
            (float)((output.Height * h[3] + output.Y * h[6]) / BoardSurfaceSize), 0,
            (float)(h[6] / BoardSurfaceSize),
            (float)((output.Width * h[1] + output.X * h[7]) / BoardSurfaceSize),
            (float)((output.Height * h[4] + output.Y * h[7]) / BoardSurfaceSize), 0,
            (float)(h[7] / BoardSurfaceSize),
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
    }

    private static void DrawMenuButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label, CanvasTextFormat small, int number,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered);
        ds.DrawText(number.ToString("00"), (float)rect.X + 32, (float)rect.Y + 16,
            hovered ? AppPalette.IndicatorOn : AppPalette.MutedText, small);
        ds.DrawText(button.Label, (float)rect.X + 32, (float)rect.Y + 52,
            AppPalette.ButtonText, label);
        var description = button.Destination switch
        {
            BoardScreen.HandTracking => "Test gestures",
            BoardScreen.PhotoCopy => "Copy hands and objects",
            BoardScreen.Blackjack => "Play against the dealer",
            _ => "Coming soon"
        };
        ds.DrawText(description, (float)rect.X + 32, (float)rect.Y + 111, AppPalette.MutedText, small);
        var iconCenter = new Vector2((float)rect.Right - 55, (float)rect.Y + 48);
        ds.FillCircle(iconCenter, 30, ThemeColor(9, 21, 35, 110));
        ds.DrawCircle(iconCenter, 30, hovered ? AppPalette.IndicatorOn : ThemeColor(123, 158, 187, 65), 1);
        DrawBoardSymbol(ds, button.Destination, iconCenter, .79f,
            hovered ? AppPalette.IndicatorOn : AppPalette.AccentSecondary);
        float arrowX = (float)rect.Right - 41, arrowY = (float)rect.Bottom - 30;
        ds.DrawLine(arrowX - 12, arrowY, arrowX, arrowY, hovered ? AppPalette.IndicatorOn : AppPalette.MetalEdge, 2);
        ds.DrawLine(arrowX - 6, arrowY - 6, arrowX, arrowY, hovered ? AppPalette.IndicatorOn : AppPalette.MetalEdge, 2);
        ds.DrawLine(arrowX - 6, arrowY + 6, arrowX, arrowY, hovered ? AppPalette.IndicatorOn : AppPalette.MetalEdge, 2);
        DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, AppPalette.IndicatorOn);
    }

    private static void DrawBoardButton(CanvasDrawingSession ds, BoardButton button,
        bool hovered, CanvasTextFormat label, CanvasTextFormat small, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        string? description = null)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered);
        var x = (float)rect.X + 33;
        var y = (float)rect.Y + (description is null ? 27 : 33);
        ds.DrawText(button.Label, x, y, AppPalette.ButtonText, label);
        if (description is not null)
            ds.DrawText(description, x, (float)rect.Y + 100,
                AppPalette.ButtonText, small);
        DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, AppPalette.IndicatorOn, showCaption: description is null);
    }

}
