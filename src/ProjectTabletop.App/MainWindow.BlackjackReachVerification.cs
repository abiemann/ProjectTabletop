#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Feed actual rendered letters through the camera-caption detector, then
    // the production hold policy. No hand landmarks or synthetic hold IDs.
    private async Task<object> VerifyBlackjackReachAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.BlackjackState.Revision);
        const int size = 1200;
        int checks = 0, damagedLowerFrames = 0;
        foreach (bool coverExit in new[] { false, true })
        {
            var now = MonotonicClock.UtcNow.AddMinutes(1);
            using var scene = new SceneCompositor(new BlackjackGame(91), blackjackClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
            scene.SetDisplayAspect(1);
            scene.SetBoardSetup(true);
            double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                    new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.ShowBlackjack();
            var initial = scene.BlackjackState;
            var tracker = new HandAcquisitionPresenceTracker();
            long referenceRevision = -1;
            HandAcquisitionPresenceResult? lastPresence = null;
            DateTimeOffset? firstExitEvidence = null;
            bool exited = false, measuredWholeReach = false;
            for (int frame = 0; frame < 5; frame++)
                Check(Feed(arm: false).Count == 0, "Intact Blackjack captions activated during reach preparation.");
            for (int frame = 0; frame < 20; frame++)
            {
                var activated = Feed(arm: true);
                Check(scene.BlackjackState.Revision == initial.Revision &&
                        scene.BlackjackState.SelectedBet == initial.SelectedBet &&
                        scene.BlackjackState.Bankroll == initial.Bankroll &&
                        scene.BlackjackState.Phase == BlackjackPhase.Betting,
                    "A reaching arm changed the wager, dealt cards, or altered the bankroll.");
                if (activated.Count == 0)
                {
                    Check(scene.CurrentBoardScreen == BoardScreen.Blackjack,
                        "Blackjack left the table without a qualified EXIT hold.");
                    continue;
                }
                Check(coverExit && activated.SequenceEqual(["menu"]) && firstExitEvidence is { } began &&
                        now - began >= BoardSession.HoldActivationInterval && measuredWholeReach,
                    "The reaching EXIT hold activated early, selected a lower control, or lacked concurrent lower-caption damage.");
                Check(scene.CurrentBoardScreen == BoardScreen.Menu,
                    "The completed reaching EXIT hold did not return to the menu.");
                exited = true;
                break;
            }
            Check(coverExit ? exited : !exited && firstExitEvidence is null,
                coverExit ? "EXIT remained blocked by the rendered reaching arm: " +
                    System.Text.Json.JsonSerializer.Serialize(lastPresence?.TextPatterns) :
                    "Damaged lower coins activated an intact EXIT caption.");
            Check(measuredWholeReach, "The arm fixture did not corrupt both rendered 50 and 100 captions.");
            await Task.Yield();

            IReadOnlyList<string> Feed(bool arm)
            {
                now += TimeSpan.FromMilliseconds(100);
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
                byte[] pixels = target.GetPixelBytes();
                var context = scene.GetHoldButtonContext(now) ??
                    throw new InvalidOperationException("The Blackjack reach fixture has no rendered hold reference.");
                if (context.Revision != referenceRevision)
                {
                    tracker.Reset();
                    referenceRevision = context.Revision;
                }
                if (arm)
                {
                    // A palm at the upper-left EXIT leads diagonally to a wider
                    // forearm crossing both lower-right betting captions. The
                    // negative stops below EXIT, leaving its real ink intact.
                    Point2[] shape = coverExit
                        ? [new(.055, .045), new(.235, .045), new(.32, .29),
                            new(.64, 1), new(.35, 1), new(.10, .29)]
                        : [new(.10, .24), new(.30, .24), new(.64, 1), new(.35, 1)];
                    using var image = new OpenCvSharp.Mat(size, size, OpenCvSharp.MatType.CV_8UC4);
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, image.Data, pixels.Length);
                    var polygon = shape.Select(point => new OpenCvSharp.Point(
                        (int)Math.Round(size * (.035 + .93 * (inset / 2 + point.X * (1 - inset)))),
                        (int)Math.Round(size * (.035 + .93 * (inset / 2 + point.Y * (1 - inset)))))).ToArray();
                    OpenCvSharp.Cv2.FillConvexPoly(image, polygon, new OpenCvSharp.Scalar(75, 95, 185, 255));
                    System.Runtime.InteropServices.Marshal.Copy(image.Data, pixels, 0, pixels.Length);
                }
                var presence = tracker.Update(size, size, size * 4, pixels,
                    context.SearchPolygon, context.ExpectedScene, now, now);
                lastPresence = presence;
                var held = context.HeldButtons(presence);
                if (held.Contains("menu"))
                {
                    Check(arm && coverExit, "An intact rendered EXIT caption counted as a hold.");
                    firstExitEvidence ??= now;
                }
                if (held.Contains("bj-bet-50") && held.Contains("bj-bet-100") &&
                    (!coverExit || held.Contains("menu")))
                {
                    measuredWholeReach = true;
                    damagedLowerFrames++;
                }
                var activated = scene.ObserveHoldButtons(context, held, now,
                    context.ClearedButtons(presence), context.BlockedButtons(presence));
                Check(scene.CurrentHoldProgress.All(progress => progress.ButtonId == "menu"),
                    "The reaching arm accumulated progress on a lower betting caption.");
                return activated;
            }
        }
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.BlackjackState.Revision),
            "The isolated Blackjack reach check changed live hardware or the running game.");
        return new { passed = true, checks, damagedLowerFrames, renderedExitAndArmCaptions = true,
            fullSecondExitRequired = true, intactExitRejectsLowerDamage = true,
            wagerAndDealUnchanged = true, liveHardwareUnchanged = true };

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
#endif
