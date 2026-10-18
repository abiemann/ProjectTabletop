#if DEBUG
using System.Reflection;
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
        await scene.EnsureMenuPreviewResourcesAsync(menuImage.Device);
        // Blackjack lives on the second launcher page. Settle that page before
        // taking the baseline or aiming the gesture at its actual visible card.
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        Require(board.ActivateButton("menu-scroll-down", MonotonicClock.UtcNow),
            "The finger-selection fixture could not open the second menu page.");
        await Task.Delay(BoardSession.MenuScrollDuration + TimeSpan.FromMilliseconds(20));
        board.TickMenu(MonotonicClock.UtcNow);
        var menuBefore = Draw(menuImage);
        var menuHand = AtButton("blackjack");
        foreach (var middle in new[] { new PixelPoint(double.NaN, 0), PointAt(1.3, .5) })
        {
            await Send(menuHand with { FingerTips = Array.AsReadOnly(new[] { menuHand.Position, middle, middle, middle }) });
            Require(scene.CurrentFingerSelectionFeedback.Count == 0, "Invalid middle tip armed a button.");
        }
        foreach (var time in new[] { MonotonicClock.UtcNow.AddSeconds(-1), MonotonicClock.UtcNow.AddSeconds(1) })
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
        Require(scene.GetLastHandBoardSelection() is null, "Invalid, idle or grouped observations fabricated a successful selection.");
        await Select(menuHand, "blackjack", () => scene.CurrentBoardScreen == BoardScreen.Blackjack);
        var menuSelection = scene.GetLastHandBoardSelection();
        for (int i = 0; i < 3; i++) await Send(Separate(menuHand));
        Require(scene.BlackjackState.Phase == BlackjackPhase.Betting, "Held separation crossed into the next screen.");
        Require(ReferenceEquals(menuSelection, scene.GetLastHandBoardSelection()),
            "Held separation overwrote the menu selection route after navigation.");

        // Your Chips remains an ordinary gesture control; betting coins now
        // use caption holds and must never arm from grouped fingers.
        var chips = AtButton("bj-reset");
        var chipsBounds = scene.CurrentBoardButtons.Single(button => button.Id == "bj-reset").Bounds;
        await Send(chips);
        var beforeArm = Draw(casinoImage);
        await Arm(chips, casinoImage);
        Require(Differences(beforeArm, casinoImage.GetPixelBytes(),
            PointAt(chipsBounds.X + chipsBounds.Width * .4, chipsBounds.Y + chipsBounds.Height - .007), 5, false) > 12,
            "The ready-state gold indicator did not redraw on the casino table.");
        var beforeChipsSelection = scene.GetLastHandBoardSelection();
        await Select(chips, "bj-reset", () => !ReferenceEquals(beforeChipsSelection, scene.GetLastHandBoardSelection()));
        var chipsSelection = scene.GetLastHandBoardSelection();
        for (int i = 0; i < 4; i++) await Send(Separate(chips));
        Require(ReferenceEquals(chipsSelection, scene.GetLastHandBoardSelection()),
            "Keeping the index apart repeated the Your Chips selection.");
        // Rearm at precisely the same point without folding or moving away.
        await Arm(chips);
        await Select(chips, "bj-reset", () => !ReferenceEquals(chipsSelection, scene.GetLastHandBoardSelection()));
        var dealSelection = scene.GetLastHandBoardSelection();
        decimal selectedBet = scene.BlackjackState.SelectedBet;
        foreach (string id in new[] { "bj-bet-10", "bj-bet-25", "bj-bet-50", "bj-bet-100", "menu" })
        {
            var coin = AtButton(id);
            for (int i = 0; i < 3; i++) await Send(coin);
            for (int i = 0; i < 3; i++) await Send(Separate(coin));
            Require(scene.CurrentBoardScreen == BoardScreen.Blackjack && scene.CurrentFingerSelectionFeedback.Count == 0 && scene.HoveredBoardButtons.Count == 0 &&
                    scene.BlackjackState.SelectedBet == selectedBet &&
                    ReferenceEquals(dealSelection, scene.GetLastHandBoardSelection()),
                "The finger gesture armed, highlighted or selected the long-press " + id + " control.");
        }
        // Deal is a one-time long-press: grouped fingers never arm it.
        var deal = AtButton("bj-deal");
        for (int i = 0; i < 3; i++) await Send(deal);
        Require(scene.CurrentFingerSelectionFeedback.Count == 0 && scene.BlackjackState.Phase == BlackjackPhase.Betting,
            "Grouped fingers armed the long-press Deal button.");
        Require(scene.ActivateBlackjackButton("bj-deal") && scene.BlackjackState.Phase == BlackjackPhase.PlayerTurn,
            "The finger fixture could not deal.");
        // Keep source-frame timestamps real while the opening presentation finishes.
        await Task.Delay(1850);
        Draw(casinoImage);
        // Split replaces Deal at the same position. Continued separation cannot click it.
        for (int i = 0; i < 4; i++) await Send(Separate(deal));
        Require(scene.BlackjackState.Hands.Count == 1, "Held separation clicked the newly drawn Split control.");
        Require(ReferenceEquals(dealSelection, scene.GetLastHandBoardSelection()),
            "A held gesture overwrote the successful Your Chips route.");

        // Round actions are long-press buttons: grouped fingers never arm or select Hit.
        var hit = AtButton("bj-hit");
        for (int i = 0; i < 3; i++) await Send(hit);
        for (int i = 0; i < 3; i++) await Send(Separate(hit));
        Require(scene.CurrentFingerSelectionFeedback.Count == 0 && scene.BlackjackState.Hands.Single().Cards.Count == 2 &&
            ReferenceEquals(dealSelection, scene.GetLastHandBoardSelection()),
            "The finger gesture armed or selected the long-press Hit button.");
        Require(new[] { menuHand, chips, deal, hit }.All(hand => hand.ExecuteEventId == 0), "Selection generated a pinch event.");
        var lastHitSelection = scene.GetLastHandBoardSelection();
        scene.ShowPaint();
        scene.SetHandCursors([], MonotonicClock.UtcNow);
        Require(ReferenceEquals(lastHitSelection, scene.GetLastHandBoardSelection()),
            "Programmatic navigation or an idle Paint frame overwrote the last successful hand selection.");
        var paintArrow = AtButton("paint-drawer-open");
        for (int i = 0; i < 3; i++) await Send(paintArrow);
        for (int i = 0; i < 3; i++) await Send(Separate(paintArrow));
        Require(scene.CurrentBoardScreen == BoardScreen.Paint && !scene.PaintDrawerOpen &&
                scene.CurrentFingerSelectionFeedback.Count == 0 &&
                ReferenceEquals(lastHitSelection, scene.GetLastHandBoardSelection()),
            "A finger-selection gesture armed or opened Paint's long-press drawer.");
        // Settings Back remains a gesture control. Keep the successful return
        // route check here now that Paint's controls all use caption holds.
        scene.ShowSettings();
        var back = AtButton("menu") with { TrackingId = 812 };
        await Arm(back);
        await Select(back, "menu", () => scene.CurrentBoardScreen == BoardScreen.Menu);
        var backSelection = scene.GetLastHandBoardSelection();
        for (int i = 0; i < 3; i++) await Send(Separate(back));
        Require(scene.CurrentBoardScreen == BoardScreen.Menu && ReferenceEquals(backSelection, scene.GetLastHandBoardSelection()),
            "Settings Back lost its successful route or replayed a held gesture after returning to the menu.");
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
            exactIndexSelectionRouteRetained = true, settingsBackRouteRetainedAcrossNavigation = true,
            paintDrawerRejectsFingerSelection = true, bettingCoinsRejectFingerSelection = true, blackjackExitRejectsFingerSelection = true,
            rejectedAndHeldObservationsDoNotOverwriteSelection = true,
            directory, images = new[] { menuPath, casinoPath } };

        async Task Arm(HandCursor hand, CanvasRenderTarget? target = null)
        {
            var previousSelection = scene.GetLastHandBoardSelection();
            await Send(hand);
            await Send(hand);
            Require(scene.CurrentFingerSelectionFeedback.Any(item => item.Stage == BoardFingerSelectionStage.Armed),
                "Two grouped observations did not arm the middle-tip target.");
            Require(ReferenceEquals(previousSelection, scene.GetLastHandBoardSelection()),
                "Arming a target fabricated or overwrote a successful selection route.");
            if (target is not null) Draw(target);
        }
        async Task Select(HandCursor hand, string buttonId, Func<bool> completed)
        {
            var previous = scene.CurrentBoardScreen;
            var previousSelection = scene.GetLastHandBoardSelection();
            await Send(Separate(hand));
            Require(!completed(), "A single separated observation selected a button.");
            Require(ReferenceEquals(previousSelection, scene.GetLastHandBoardSelection()),
                "Unconfirmed index separation overwrote the last successful selection route.");
            var frameTime = await Send(Separate(hand));
            Require(completed(), "Confirmed index separation did not select the armed target.");
            AssertHandSelectionDiagnostic(scene, previous, scene.CurrentBoardScreen,
                buttonId, "IndexSeparation", hand.TrackingId, frameTime);
        }
        HandCursor AtButton(string id)
        {
            var button = scene.CurrentBoardButtons.SingleOrDefault(candidate => candidate.Id == id) ??
                throw new InvalidOperationException($"The {scene.CurrentBoardScreen} fixture has no visible {id} control.");
            var b = button.Bounds;
            double u = b.X + b.Width / 2, v = b.Y + b.Height / 2;
            var index = PointAt(u, b.Y - .02); // Index is deliberately outside the target.
            return new(index, DateTimeOffset.MinValue) { TrackingId = 811, HasFourExtendedFingers = true, FingersTogether = true,
                FingerTips = Array.AsReadOnly(new[] { index, PointAt(u, v), PointAt(u + .025, v + .01), PointAt(u + .05, v + .025) }) };
        }
        static HandCursor Separate(HandCursor hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
        PixelPoint PointAt(double u, double v) => new(.035 + .93 * (inset / 2 + u * (1 - inset)), .035 + .93 * (inset / 2 + v * (1 - inset)));
        async Task<DateTimeOffset> Send(HandCursor hand)
        {
            await Task.Delay(110);
            var frameTime = MonotonicClock.UtcNow;
            scene.SetHandCursors([hand], frameTime);
            return frameTime;
        }
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
