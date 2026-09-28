#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Every game, gesture and board map below belongs to an isolated compositor.
    // This check must never start the camera, align the real board or open output.
    private async Task<object> VerifyBlackjackAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        const int size = 1200;
        string directory = Path.Combine(_appDataDirectory, "BlackjackSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        using var scene = new SceneCompositor(Game(10, 6, 5, 10, 3, 5));
        var inset = Align(scene, [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)]);

        await Pinch("blackjack", 1);
        Require(scene.CurrentBoardScreen == BoardScreen.Blackjack && scene.BlackjackState.Phase == BlackjackPhase.Betting,
            "The menu gesture did not open the Blackjack betting table.");
        await Pinch("bj-deal", 1);
        Require(scene.BlackjackState.Phase == BlackjackPhase.Betting,
            "The menu's held pinch also dealt a hand.");
        scene.ClearHandTips(resetInput: false);
        var betting = DrawPreview(scene);
        CheckTopControls(scene, resetEnabled: true);
        Require(NonBlackPixels(betting) > size * size / 2, "The laptop casino preview is blank or mostly missing.");
        await Save("betting");

        await Task.Delay(2);
        var chips = scene.CurrentBoardButtons.Single(button => button.Id == "bj-reset");
        scene.SetHandCursors([new(PointAt(chips), DateTimeOffset.MinValue)], DateTimeOffset.UtcNow);
        var chipsHovered = DrawPreview(scene);
        Require(scene.HoveredBoardButtons.SequenceEqual(["bj-reset"]) &&
                DifferentPixels(betting, chipsHovered, chips.Bounds) > 100 &&
                DifferentPixels(betting, chipsHovered, new(.752, .088, .178, .043)) == 0,
            "Your Chips lacks plaque hover feedback or hovering altered its balance display.");
        await Save("your-chips-hover");
        scene.ClearHandTips(resetInput: false);

        await Task.Delay(2);
        var dealButton = scene.CurrentBoardButtons.Single(button => button.Id == "bj-deal");
        scene.SetHandCursors([new(PointAt(dealButton), DateTimeOffset.MinValue)], DateTimeOffset.UtcNow);
        Require(scene.HoveredBoardButtons.SequenceEqual(["bj-deal"]),
            "Hover does not follow the shared Blackjack button rectangle.");
        var hovered = DrawPreview(scene);
        Require(DifferentPixels(betting, hovered, dealButton.Bounds) > 100,
            "The Deal button has no visible hover feedback.");
        await Save("betting-hover");

        await Pinch("bj-deal", 2);
        var playerState = scene.BlackjackState;
        Require(playerState.Phase == BlackjackPhase.PlayerTurn && playerState.Hands.Single().Total == 15 &&
            playerState.DealerHoleCardHidden && playerState.DealerCards[1] is null && playerState.DealerTotal == 6,
            "The opening hand or concealed dealer snapshot is incorrect.");
        scene.ClearHandTips(resetInput: false);
        // Gesture timestamps use the real camera clock; wait for all four landings
        // before testing another real-time gesture. The animation verifier uses a fake clock.
        await Task.Delay(1850);
        var player = DrawPreview(scene);
        CheckTopControls(scene, resetEnabled: false);
        Require(DifferentPixels(betting, player) > 1000, "Dealing did not redraw the cached table.");
        Require(BrightPixels(player, new(.08, .23, .84, .50)) > 5000,
            "The opening cards are not visibly rendered on the table.");
        await Save("player-turn");

        await Pinch("bj-hit", 2);
        Require(scene.BlackjackState.Hands.Single().Cards.Count == 2,
            "Holding the deal pinch also hit the player hand.");
        await Pinch("bj-hit", 3);
        Require(scene.BlackjackState.Hands.Single().Total == 18 && scene.BlackjackState.Hands.Single().Cards.Count == 3,
            "A fresh hit gesture did not deal exactly one card.");
        scene.ClearHandTips(resetInput: false);
        await Task.Delay(760); // Inspect the settled hand after its HIT card flies in.
        var hit = DrawPreview(scene);
        Require(DifferentPixels(player, hit) > 1000, "Hit changed the model but left an old table texture visible.");
        await Save("player-hit");
        await Pinch("bj-stand", 3);
        Require(scene.BlackjackState.Phase == BlackjackPhase.PlayerTurn,
            "Moving the held hit pinch onto Stand ended the turn.");

        await Pinch("bj-stand", 4);
        Require(scene.BlackjackState.Phase == BlackjackPhase.DealerTurn &&
            scene.CurrentBoardButtons.Where(button => button.Id != "menu").All(button => !button.Enabled),
            "The dealer turn retained an enabled player action.");
        scene.ClearHandTips(resetInput: false);
        DrawPreview(scene);
        CheckTopControls(scene, resetEnabled: false);
        await Save("dealer-turn");
        var future = DateTimeOffset.UtcNow.AddSeconds(2);
        Require(scene.TickBlackjack(future) && !scene.BlackjackState.DealerHoleCardHidden,
            "The dealer did not reveal its hidden card.");
        Require(scene.TickBlackjack(future.AddSeconds(1)) && scene.BlackjackState.DealerCards.Count == 3,
            "The dealer did not draw on 16.");
        Require(scene.TickBlackjack(future.AddSeconds(2)) && scene.BlackjackState.Phase == BlackjackPhase.RoundOver,
            "The dealer did not settle the round.");
        Require(scene.BlackjackState.Bankroll == 975 && scene.BlackjackState.Hands.Single().Result == "DEALER WINS",
            "The displayed completed game has the wrong result or bankroll.");
        var settled = DrawPreview(scene);
        CheckTopControls(scene, resetEnabled: true);
        Require(DifferentPixels(hit, settled) > 1000, "Round settlement did not redraw cards and result controls.");
        await Save("dealer-wins");

        // Two different hidden cards must produce exactly the same public table.
        var firstHiddenGame = StartedGame(10, 6, 5, 10);
        var secondHiddenGame = StartedGame(10, 6, 5, 9);
        using (var firstHiddenScene = new SceneCompositor(firstHiddenGame))
        using (var secondHiddenScene = new SceneCompositor(secondHiddenGame))
        {
            firstHiddenScene.ShowBlackjack();
            secondHiddenScene.ShowBlackjack();
            Require(DifferentPixels(DrawPreview(firstHiddenScene), DrawPreview(secondHiddenScene)) == 0,
                "The rendered player turn leaks information about the dealer's hidden card.");
        }

        var fixtureTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var splitGame = Game(8, 6, 8, 10, 3, 2, 5);
        Act(splitGame, "bj-deal", fixtureTime);
        Act(splitGame, "bj-split", fixtureTime.AddSeconds(1));
        using (var splitScene = new SceneCompositor(splitGame))
        {
            splitScene.ShowBlackjack();
            DrawPreview(splitScene);
            await Save("split-hands");
            Require(splitScene.BlackjackState.Hands.Count == 2 && splitScene.BlackjackState.ActiveHandIndex == 0,
                "The split fixture did not expose two player hands.");
            // Reuse the same render path under a nontrivial board homography.
            Align(splitScene, [new(.15f, .13f), new(.86f, .18f), new(.90f, .88f), new(.10f, .81f)]);
            splitScene.SetDisplayAspect(16.0 / 9);
            using var projected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1600, 900, 96);
            using (var drawing = projected.CreateDrawingSession())
                splitScene.Draw(drawing, 1600, 900, preview: false, runningSlowly: false);
            var pixels = projected.GetPixelBytes();
            Require(pixels[0] == 0 && pixels[1] == 0 && pixels[2] == 0 &&
                NonBlackPixels(pixels) > 200000, "The mapped table is blank or spills outside the board clip.");
            var projectedPath = Path.Combine(directory, "split-projected-perspective.png");
            await projected.SaveAsync(projectedPath, CanvasBitmapFileFormat.Png);
            images.Add(new { name = "split-projected-perspective", path = projectedPath });
        }

        await SaveFixture("blackjack-natural", StartedGame(1, 9, 13, 8));
        await SaveFixture("push", CompletedGame([10, 10, 8, 8]));
        await SaveFixture("player-wins", CompletedGame([10, 6, 8, 10, 10]));
        var broke = Game(Enumerable.Repeat(new[] { 10, 10, 7, 10 }, 10).SelectMany(ranks => ranks).ToArray());
        Act(broke, "bj-bet-100", fixtureTime);
        for (int round = 0; round < 10; round++)
        {
            var roundTime = fixtureTime.AddSeconds(10 * round + 1);
            Act(broke, "bj-deal", roundTime);
            Act(broke, "bj-stand", roundTime.AddSeconds(1));
            Require(broke.Tick(roundTime.AddSeconds(2)) && broke.Tick(roundTime.AddSeconds(3)),
                "The exhausted-bankroll fixture did not settle a loss.");
        }
        Require(broke.Snapshot.Bankroll == 0 && broke.Snapshot.AvailableActions.SequenceEqual(["bj-reset"]),
            "A zero-credit player can still wager or deal.");
        using (var brokeScene = new SceneCompositor(broke))
        {
            brokeScene.ShowBlackjack();
            var exhausted = DrawPreview(brokeScene);
            CheckTopControls(brokeScene, resetEnabled: true);
            await Save("out-of-credits");
            Require(!brokeScene.ActivateBlackjackButton("bj-deal"), "A disabled Deal button can be invoked.");
            var reset = brokeScene.CurrentBoardButtons.Single(button => button.Id == "bj-reset");
            Require(brokeScene.ActivateBlackjackAt(reset.Bounds.X + reset.Bounds.Width / 2,
                reset.Bounds.Y + reset.Bounds.Height / 2) && brokeScene.BlackjackState.Bankroll == 1000 &&
                brokeScene.BlackjackState.Phase == BlackjackPhase.Betting,
                "The laptop coordinate hit target could not restore virtual credits.");
            Require(DifferentPixels(exhausted, DrawPreview(brokeScene), new(.752, .088, .178, .043)) > 50,
                "Restoring 1000 chips left the old exhausted balance in the top counter.");
            await Save("your-chips-restored");
            Require(!brokeScene.HasBoardMediaClip, "The laptop action unexpectedly created a projector clip.");
        }

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated Blackjack check changed the live camera, projector, board or game.");
        return new { passed = true, liveHardwareUnchanged = true, menuAndPinchActions = true,
            heldPinchSuppressed = true, hoverRendered = true, cachedTableUpdated = true,
            exitCaptionAndTopCounterTargets = true, yourChipsPlaqueHoverAndRestoredBalance = true, noFooterReset = true,
            hiddenCardDoesNotLeak = true, dealerRevealAndSettlement = true,
            splitRendered = true, perspectiveClip = true, zeroCreditsAndLaptopReset = true, directory, images };

        async Task Pinch(string id, long eventId)
        {
            await Task.Delay(2); // Every source frame follows the preceding selection/reset barrier.
            var button = scene.CurrentBoardButtons.Single(item => item.Id == id);
            var now = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(PointAt(button), now.AddSeconds(1), eventId)], now);
        }

        PixelPoint PointAt(BoardButton button)
        {
            double u = button.Bounds.X + button.Bounds.Width / 2, v = button.Bounds.Y + button.Bounds.Height / 2;
            return new(.035 + .93 * (inset / 2 + u * (1 - inset)), .035 + .93 * (inset / 2 + v * (1 - inset)));
        }

        byte[] DrawPreview(SceneCompositor drawingScene)
        {
            using (var drawing = target.CreateDrawingSession()) drawingScene.DrawBlackjackPreview(drawing, size, size);
            return target.GetPixelBytes();
        }

        async Task Save(string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }

        async Task SaveFixture(string name, BlackjackGame game)
        {
            using var fixture = new SceneCompositor(game);
            fixture.ShowBlackjack();
            DrawPreview(fixture);
            await Save(name);
        }

        static BlackjackGame Game(params int[] ranks) => new(seed: 173,
            initialShoe: ranks.Select((rank, index) => new BlackjackCard(rank, (BlackjackSuit)(index % 4))));

        static BlackjackGame StartedGame(params int[] ranks)
        {
            var game = Game(ranks);
            Act(game, "bj-deal", DateTimeOffset.UtcNow.AddMinutes(-1));
            return game;
        }

        static BlackjackGame CompletedGame(int[] ranks)
        {
            var game = Game(ranks);
            var start = DateTimeOffset.UtcNow.AddMinutes(-1);
            Act(game, "bj-deal", start);
            Act(game, "bj-stand", start.AddSeconds(1));
            for (int step = 2; game.Snapshot.Phase == BlackjackPhase.DealerTurn && step < 20; step++)
                game.Tick(start.AddSeconds(step));
            Require(game.Snapshot.Phase == BlackjackPhase.RoundOver, "A completed-game fixture did not settle.");
            return game;
        }

        static float Align(SceneCompositor fixture, Vector2[] corners)
        {
            fixture.SetDisplayAspect(1);
            fixture.SetBoardSetup(true);
            var fraction = fixture.SetDetectedBoardGrid(corners, Homography.FromFourPoints(
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            fixture.SetBoardSetup(false);
            return fraction;
        }

        static void Act(BlackjackGame game, string id, DateTimeOffset time) =>
            Require(game.HandleAction(id, time), "The deterministic fixture rejected " + id + ".");

        static void CheckTopControls(SceneCompositor fixture, bool resetEnabled)
        {
            var buttons = fixture.CurrentBoardButtons;
            var exit = buttons.Single(button => button.Id == "menu");
            var reset = buttons.Single(button => button.Id == "bj-reset");
            Require(exit.Label == "Exit" && exit.Bounds == new BoardRect(.06, .055, .18, .09) && exit.Enabled &&
                    reset.Label == "Your Chips" && reset.Bounds == new BoardRect(.742, .055, .198, .09) &&
                    reset.Enabled == resetEnabled && buttons.All(button => !button.Label.Contains("Reset chips", StringComparison.OrdinalIgnoreCase)),
                "The casino retained its old navigation/footer reset or changed the top-counter interaction rules.");
        }

        static int NonBlackPixels(byte[] pixels)
        {
            int count = 0;
            for (int index = 0; index < pixels.Length; index += 4)
                if (pixels[index] + pixels[index + 1] + pixels[index + 2] > 30) count++;
            return count;
        }

        static int BrightPixels(byte[] pixels, BoardRect region)
        {
            int count = 0;
            for (int y = (int)(region.Y * size); y < (region.Y + region.Height) * size; y++)
            for (int x = (int)(region.X * size); x < (region.X + region.Width) * size; x++)
            {
                int index = (y * size + x) * 4;
                if (pixels[index] > 170 && pixels[index + 1] > 170 && pixels[index + 2] > 170) count++;
            }
            return count;
        }

        static int DifferentPixels(byte[] before, byte[] after, BoardRect? region = null)
        {
            var bounds = region ?? new BoardRect(0, 0, 1, 1);
            int count = 0;
            for (int y = (int)(bounds.Y * size); y < (bounds.Y + bounds.Height) * size; y++)
            for (int x = (int)(bounds.X * size); x < (bounds.X + bounds.Width) * size; x++)
            {
                int index = (y * size + x) * 4;
                if (before[index] != after[index] || before[index + 1] != after[index + 1] || before[index + 2] != after[index + 2]) count++;
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
