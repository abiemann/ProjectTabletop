#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyFingerSelectionAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        using var scene = new SceneCompositor(new BlackjackGame(seed: 173, initialShoe:
            new[] { 2, 6, 2, 10, 2, 2, 8, 4 }.Select((rank, index) => new BlackjackCard(rank, (BlackjackSuit)(index % 4)))));
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        const int size = 1200;
        using var menuImage = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        using var casinoImage = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        var menuBefore = Draw(menuImage);
        var menuHand = AtButton("blackjack");
        foreach (var middle in new[] { new PixelPoint(double.NaN, 0), PointAt(1.3, .5) })
        {
            await Send(menuHand with { FingerTips = Array.AsReadOnly(new[] { menuHand.Position, middle, middle, middle }) });
            Require(scene.CurrentFingerSelectionFeedback.Count == 0, "Invalid middle tip armed a button.");
        }
        foreach (var time in new[] { DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1) })
        {
            scene.SetHandCursors([menuHand], time);
            Require(scene.CurrentFingerSelectionFeedback.Count == 0, "A stale or future frame armed a button.");
        }
        for (int i = 0; i < 3; i++) await Send(Separate(menuHand));
        Require(scene.CurrentBoardScreen == BoardScreen.Menu, "Entering with separated fingers selected a button.");
        var outside = new PixelPoint(-.2, .5);
        var edgeHand = menuHand with { Position = outside,
            FingerTips = Array.AsReadOnly(new[] { outside, menuHand.FingerTips[1], menuHand.FingerTips[2], menuHand.FingerTips[3] }) };
        await Send(edgeHand);
        Require(scene.HoveredBoardButtons.SequenceEqual(["blackjack"]) &&
            Differences(menuBefore, Draw(menuImage), edgeHand.FingerTips[1], 20, true) > 25,
            "An off-projector index suppressed the middle aim marker or hover.");
        await Arm(menuHand, menuImage);
        // No elapsed hold, however long, is an execute command.
        for (int i = 0; i < 11; i++) await Send(menuHand);
        Require(scene.CurrentBoardScreen == BoardScreen.Menu, "Holding grouped fingers caused an automatic click.");
        await Select(menuHand, () => scene.CurrentBoardScreen == BoardScreen.Blackjack);
        for (int i = 0; i < 3; i++) await Send(Separate(menuHand));
        Require(scene.BlackjackState.Phase == BlackjackPhase.Betting, "Held separation crossed into the next screen.");

        var deal = AtButton("bj-deal");
        var dealBounds = scene.CurrentBoardButtons.Single(button => button.Id == "bj-deal").Bounds;
        await Send(deal);
        var beforeDeal = Draw(casinoImage);
        await Arm(deal, casinoImage);
        Require(Differences(beforeDeal, casinoImage.GetPixelBytes(),
            PointAt(dealBounds.X + dealBounds.Width * .4, dealBounds.Y + dealBounds.Height - .007), 5, false) > 12,
            "The ready-state gold indicator did not redraw on the casino table.");
        await Select(deal, () => scene.BlackjackState.Phase == BlackjackPhase.PlayerTurn);
        // Split replaces Deal at the same position. Continued separation cannot click it.
        for (int i = 0; i < 4; i++) await Send(Separate(deal));
        Require(scene.BlackjackState.Hands.Count == 1, "Held separation clicked the newly drawn Split control.");

        var hit = AtButton("bj-hit");
        await Arm(hit);
        await Select(hit, () => scene.BlackjackState.Hands.Single().Cards.Count == 3);
        for (int i = 0; i < 4; i++) await Send(Separate(hit));
        Require(scene.BlackjackState.Hands.Single().Cards.Count == 3, "Keeping index apart repeated HIT.");
        // Rearm at precisely the same point without folding or moving away.
        await Arm(hit);
        await Select(hit, () => scene.BlackjackState.Hands.Single().Cards.Count == 4);
        Require(scene.BlackjackState.Phase == BlackjackPhase.PlayerTurn, "The repeated HIT fixture ended too early.");
        Require(new[] { menuHand, deal, hit }.All(hand => hand.ExecuteEventId == 0), "Selection generated a pinch event.");
        string directory = Path.Combine(_appDataDirectory, "FingerSelectionSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string menuPath = Path.Combine(directory, "menu-ready.png"), casinoPath = Path.Combine(directory, "blackjack-ready.png");
        await menuImage.SaveAsync(menuPath, CanvasBitmapFileFormat.Png);
        await casinoImage.SaveAsync(casinoPath, CanvasBitmapFileFormat.Png);
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated selection check changed live hardware or gameplay.");
        return new { passed = true, middleAim = true, offProjectorIndexKeepsMiddleVisible = true,
            groupedHoldNeverExecutes = true, separationSelectsOnce = true, sameTargetRearms = true,
            readyFeedbackRendered = true, noPinchEvents = true, liveHardwareUnchanged = true,
            directory, images = new[] { menuPath, casinoPath } };

        async Task Arm(HandCursor hand, CanvasRenderTarget? target = null)
        {
            await Send(hand);
            await Send(hand);
            Require(scene.CurrentFingerSelectionFeedback.Any(item => item.Stage == BoardFingerSelectionStage.Armed),
                "Two grouped observations did not arm the middle-tip target.");
            if (target is not null) Draw(target);
        }
        async Task Select(HandCursor hand, Func<bool> completed)
        {
            await Send(Separate(hand));
            Require(!completed(), "A single separated observation selected a button.");
            await Send(Separate(hand));
            Require(completed(), "Confirmed index separation did not select the armed target.");
        }
        HandCursor AtButton(string id)
        {
            var b = scene.CurrentBoardButtons.Single(button => button.Id == id).Bounds;
            double u = b.X + b.Width / 2, v = b.Y + b.Height / 2;
            var index = PointAt(u, b.Y - .02); // Index is deliberately outside the target.
            return new(index, DateTimeOffset.MinValue) { TrackingId = 811, HasFourExtendedFingers = true, FingersTogether = true,
                FingerTips = Array.AsReadOnly(new[] { index, PointAt(u, v), PointAt(u + .025, v + .01), PointAt(u + .05, v + .025) }) };
        }
        static HandCursor Separate(HandCursor hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
        PixelPoint PointAt(double u, double v) => new(.035 + .93 * (inset / 2 + u * (1 - inset)), .035 + .93 * (inset / 2 + v * (1 - inset)));
        async Task Send(HandCursor hand) { await Task.Delay(110); scene.SetHandCursors([hand], DateTimeOffset.UtcNow); }
        byte[] Draw(CanvasRenderTarget target)
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        static int Differences(byte[] first, byte[] second, PixelPoint center, int radius, bool goldOnly)
        {
            int count = 0;
            for (int y = (int)(center.Y * size) - radius; y <= center.Y * size + radius; y++)
            for (int x = (int)(center.X * size) - radius; x <= center.X * size + radius; x++)
            {
                int offset = (y * size + x) * 4;
                if (goldOnly && !(second[offset + 2] > 200 && second[offset + 1] is > 160 and < 235 && second[offset] < 160)) continue;
                if (Math.Abs(first[offset] - second[offset]) + Math.Abs(first[offset + 1] - second[offset + 1]) + Math.Abs(first[offset + 2] - second[offset + 2]) > 30) count++;
            }
            return count;
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
