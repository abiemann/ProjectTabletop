#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Compare real rendered boards with a stationary first-frame finger occlusion.
    // This isolated scene cannot operate the user's camera or projector.
    private async Task<object> VerifySharedBoardAcquisitionAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var game = new BlackjackGame(41, new[] { 2, 6, 2, 10, 2, 2, 3, 4 }
            .Select(rank => new BlackjackCard(rank, BlackjackSuit.Clubs)));
        using var scene = new SceneCompositor(game, blackjackClock: () => now);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        var tested = new List<string>();
        scene.ShowBoardMenu(); VerifyButtons("Menu");
        scene.ShowHandTrackingTest(); VerifyButtons("Hand-Tracking");
        scene.ShowPhotoCopy(); VerifyButtons("Photo Copy");
        long selectionId = 770000;
        foreach (var (id, title) in new[] { ("monopoly", "Monopoly"), ("gta", "GTA"), ("diablo", "Diablo") })
        {
            scene.ShowBoardMenu();
            var button = scene.CurrentBoardButtons.Single(button => button.Id == id);
            await Task.Delay(10);
            var observedAt = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(CameraPoint(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2), observedAt.AddSeconds(1), ++selectionId)], observedAt);
            Require(scene.CurrentBoardTitle == title, "The navigation fixture could not open " + title + ".");
            scene.ClearHandTips();
            VerifyButtons(title);
        }
        scene.ShowBlackjack(); VerifyButtons("Blackjack betting");
        Require(scene.CurrentBoardButtons.Any(button => button.Id == "bj-reset") &&
                scene.CurrentBoardButtons.Any(button => button.Id == "menu"),
            "Blackjack coverage omitted its top Reset or Back controls.");
        Require(scene.ActivateBlackjackButton("bj-deal"), "The player-turn acquisition fixture could not deal.");
        now += TimeSpan.FromSeconds(3);
        Draw();
        VerifyButtons("Blackjack player");
        Require(scene.CurrentBoardButtons.Any(button => button.Id == "bj-hit"),
            "The fixture did not reach a player turn.");
        scene.SetBackground(null);
        Require(scene.CurrentBoardScreen == BoardScreen.Media && scene.GetHandAcquisitionContext(now) is null,
            "A media-only scene with no buttons started acquisition lighting.");
        scene.ShowBoardMenu(); Draw(); scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        var previous = scene.GetHandAcquisitionContext(now)!;
        scene.SetBoardSetup(true);
        Require(scene.GetHandAcquisitionContext(now) is null, "Calibration retained shared button assistance.");
        scene.SetBoardSetup(false);
        scene.ClearBoardMediaClip();
        scene.CompleteHandAcquisition(previous, [], [], now);
        Require(scene.GetHandAcquisitionContext(now) is null, "An uncalibrated scene retained button assistance.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The shared-board verification changed live hardware or navigation.");
        return new { passed = true, stationaryFirstFrameForEveryControl = true, controls = tested,
            noEmptyBoardCandidates = true, assistanceCannotSelect = true,
            topBlackjackBackAndResetIncluded = true, mediaAndCalibrationInactive = true,
            liveHardwareUnchanged = true };

        void VerifyButtons(string label)
        {
            Draw();
            scene.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(600);
            Draw();
            var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null }, label + " did not enable acquisition.");
            Require(context!.StationarySearchCenters?.Length == scene.CurrentBoardButtons.Count &&
                    context.ExpectedScene!.BoardSearchRegions?.Count == scene.CurrentBoardButtons.Count,
                label + " omitted a button from its camera centers or foreground masks.");
            var screen = scene.CurrentBoardScreen;
            long gameRevision = scene.BlackjackState.Revision;
            var ids = scene.CurrentBoardButtons.Select(button => button.Id).ToArray();
            for (int index = 0; index < ids.Length; index++)
            {
                context = scene.GetHandAcquisitionContext(now)!;
                var button = scene.CurrentBoardButtons[index];
                var center = CameraPoint(button.Bounds.X + button.Bounds.Width / 2,
                    button.Bounds.Y + button.Bounds.Height / 2);
                var cameraCenter = context.StationarySearchCenters![index];
                Require(Math.Abs(cameraCenter.X - center.X) < .1 && Math.Abs(cameraCenter.Y - center.Y) < .1,
                    label + "/" + button.Label + " has the wrong camera crop center.");
                var empty = Draw();
                var emptyResult = new HandAcquisitionPresenceTracker().Update(size, size, size * 4, empty,
                    context.SearchPolygon, context.ExpectedScene, now, now);
                Require(emptyResult.BaselineReady && emptyResult.Hints.Count == 0,
                    label + " found an empty-table hand candidate: " + emptyResult.Reason);
                var occupied = (byte[])empty.Clone();
                // A lone Back button has to work when the arriving hand covers
                // most of its interior. The separate static header/panel supplies
                // the appearance reference; the occluded button cannot train it.
                bool loneBack = ids.Length == 1;
                int height = (int)Math.Clamp(button.Bounds.Height * size * .93 * (1 - inset) *
                    (loneBack ? .95 : .75), 34, 95);
                // Keep this geometric fixture visibly distinct even from the
                // gold Deal button. Actual skin/projector contrast is evaluated
                // in recorded camera captures, not asserted by a painted patch.
                for (int finger = 0; finger < 4; finger++)
                    Fill(occupied, (int)center.X - (loneBack ? 107 : 38) + finger * (loneBack ? 55 : 20),
                        (int)center.Y - height / 2, loneBack ? 49 : 18, height, 75, 95, 185);
                var presence = new HandAcquisitionPresenceTracker().Update(size, size, size * 4, occupied,
                    context.SearchPolygon, context.ExpectedScene, now, now);
                if (!presence.Hints.Any(hint => Math.Abs(hint.Center.X - center.X) < 65 &&
                        Math.Abs(hint.Center.Y - center.Y) < 65))
                {
                    string directory = Path.Combine(_appDataDirectory, "SharedAcquisitionFailures", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, "empty.bgra"), empty);
                    File.WriteAllBytes(Path.Combine(directory, "occupied.bgra"), occupied);
                    File.WriteAllBytes(Path.Combine(directory, "expected.bgra"), context.ExpectedScene!.Bgra);
                    File.WriteAllText(Path.Combine(directory, "failure.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        width = size, height = size, stride = size * 4, board = label, button,
                        center, now, context.SearchPolygon,
                        expected = new { context.ExpectedScene.Width, context.ExpectedScene.Height,
                            context.ExpectedScene.CameraToBoard, context.ExpectedScene.BoardSearchRegions,
                            context.ExpectedScene.BoardReferenceRegions },
                        presence
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    throw new InvalidOperationException(label + "/" + button.Label +
                        " missed stationary fingers on its first frame: " + presence.Reason + ". Diagnostic: " + directory);
                }
                scene.CompleteHandAcquisition(context, presence.Hints, [], now);
                var lit = scene.GetHandAcquisitionContext(now)!;
                Require(lit.IlluminatedHint is not null && CountWhite(Draw(), center) > CountWhite(empty, center) + 800,
                    label + "/" + button.Label + " did not illuminate its control.");
                Require(scene.CurrentBoardScreen == screen && scene.BlackjackState.Revision == gameRevision &&
                        scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(ids) &&
                        scene.ActiveHandSpotlightCount == 0 && scene.HoveredBoardButtons.Count == 0 &&
                        !scene.TryTakePhotoCopyCaptureRequest(now, out _) && !scene.TryTakePhotoCopyMemorySaveRequest(now, out _),
                    "A foreground hint executed a board action on " + label + ".");
                tested.Add(label + "/" + button.Label);
                scene.CompleteHandAcquisition(lit, presence.Hints, [], now, illuminatedPresence: false);
                now += TimeSpan.FromMilliseconds(1000);
            }
        }
        PixelPoint CameraPoint(double u, double v) => new(size * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            size * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, size, size, false, false);
            return target.GetPixelBytes();
        }
        static void Fill(byte[] pixels, int left, int top, int width, int height, byte b, byte g, byte r)
        {
            for (int y = Math.Max(0, top); y < Math.Min(size, top + height); y++)
            for (int x = Math.Max(0, left); x < Math.Min(size, left + width); x++)
            {
                int offset = (y * size + x) * 4;
                pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
            }
        }
        static int CountWhite(byte[] pixels, PixelPoint center)
        {
            int count = 0;
            for (int y = (int)center.Y - 20; y <= (int)center.Y + 20; y++)
            for (int x = (int)center.X - 25; x <= (int)center.X + 25; x++)
            {
                int offset = (y * size + x) * 4;
                if (pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245) count++;
            }
            return count;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
