#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Deterministic clocks and private compositors exercise the real GPU paths
    // without sending synthetic input to the user's camera, board or game.
    private async Task<object> VerifyBlackjackAnimationAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        const int size = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "BlackjackAnimationSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);

        await VerifyDealAnimations();

        using var scene = new SceneCompositor(Game(2, 6, 2, 10, 2, 2), blackjackClock: () => now);
        scene.ShowBlackjack();
        Act(scene, "bj-deal");
        Require(scene.BlackjackAnimation.Count == 0, "The opening deal incorrectly queued a HIT flight.");
        now = now.AddMilliseconds(1810);
        Act(scene, "bj-hit");
        var started = now;
        var startPixels = Draw(scene);
        var flight = scene.BlackjackAnimation.Single();
        var destination = flight.Destination;
        Require(flight.Hit.HandIndex == 0 && flight.Hit.CardIndex == 2 && flight.Hit.Card.Rank == 2,
            "The HIT animation is attached to the wrong card or player hand.");
        Require(Vector2.Distance(flight.Center, new Vector2(500, -160)) < .01f &&
                Math.Abs(flight.Rotation + MathF.PI * 1.25f) < .001f && flight.Progress == 0,
            "A HIT flight did not begin above TABLETOP at the specified rotation.");
        Require(flight.Center.Y + Math.Sqrt(destination.Width * destination.Width + destination.Height * destination.Height) / 2 < 0,
            "The new card is already visible inside the table at the first frame.");
        Require(BrightPixels(startPixels, destination, size) < 100,
            "The airborne card is duplicated in its resting destination before it lands.");
        await Save(target, "hit-start");

        long revision = scene.BlackjackState.Revision;
        now = started.AddMilliseconds(180);
        Draw(scene);
        Require(Math.Abs(scene.BlackjackAnimation.Single().Rotation) > MathF.PI / 4,
            "The early HIT frame lost its visible spin.");
        await Save(target, "hit-early");
        now = started.AddMilliseconds(360);
        var middlePixels = Draw(scene);
        flight = scene.BlackjackAnimation.Single();
        Require(flight.Progress is > 0 and < 1 && Math.Abs(flight.Rotation) > .01f &&
                flight.Center.Y > 0 && flight.Center.Y < destination.Bottom,
            "The halfway HIT card is missing its visible moving/rotating pose.");
        Require(scene.BlackjackState.Revision == revision && Differences(startPixels, middlePixels) > 3000,
            "Animation motion did not repaint while the game revision stayed unchanged.");
        await Save(target, "hit-middle");

        now = started.AddMilliseconds(720);
        var landedPixels = Draw(scene);
        Require(scene.BlackjackAnimation.Count == 0 && BrightPixels(landedPixels, destination, size) > 6000,
            "The 720 ms landing did not replace the flight with its ordinary face-up card.");
        Require(Differences(middlePixels, landedPixels) > 3000,
            "The final frame reused an old animation or a table texture with the card omitted.");
        await Save(target, "hit-landed");
        now = now.AddMilliseconds(40);
        Require(Differences(landedPixels, Draw(scene)) == 0,
            "A completed HIT flight reappeared or continued changing the table.");

        using (var rapid = new SceneCompositor(Game(2, 6, 2, 10, 2, 2), blackjackClock: () => now))
        {
            rapid.ShowBlackjack();
            Act(rapid, "bj-deal");
            now = now.AddMilliseconds(1800);
            Act(rapid, "bj-hit");
            Draw(rapid);
            var firstDestination = rapid.BlackjackAnimation.Single().Destination;
            now = now.AddMilliseconds(100);
            Act(rapid, "bj-hit");
            var secondStarted = now;
            now = now.AddMilliseconds(260);
            Draw(rapid);
            var flights = rapid.BlackjackAnimation.OrderBy(item => item.Hit.CardIndex).ToArray();
            Require(flights.Length == 2 && flights[0].Hit.CardIndex == 2 && flights[1].Hit.CardIndex == 3 &&
                    flights[0].Hit.Sequence != flights[1].Hit.Sequence && flights[0].Progress > flights[1].Progress,
                "Two rapid HITs lost, duplicated or restarted an in-flight card.");
            Require(flights[0].Destination.X < firstDestination.X &&
                    flights[0].Destination.Right < flights[1].Destination.Right,
                "Concurrent flights did not follow the current four-card hand layout.");
            Require(rapid.BlackjackState.Hands.Single().Total == 8,
                "Animating rapid HITs changed the deterministic game state.");
            await Save(target, "rapid-hit-middle");
            now = secondStarted.AddMilliseconds(720);
            Draw(rapid);
            Require(rapid.BlackjackAnimation.Count == 0, "A rapid HIT remained active after both deadlines.");
            await Save(target, "rapid-hit-landed");
        }

        using (var split = new SceneCompositor(Game(8, 6, 8, 10, 10, 2, 8), blackjackClock: () => now))
        {
            split.ShowBlackjack();
            Act(split, "bj-deal");
            now = now.AddMilliseconds(1800);
            Act(split, "bj-split");
            Require(split.BlackjackAnimation.Count == 0, "Splitting queued an unintended HIT animation.");
            Act(split, "bj-hit");
            var splitStarted = now;
            Draw(split);
            var bustFlight = split.BlackjackAnimation.Single();
            Require(split.BlackjackState.Hands[0].IsBust && split.BlackjackState.ActiveHandIndex == 1 &&
                    bustFlight.Hit.HandIndex == 0 && bustFlight.Hit.CardIndex == 2 && bustFlight.Destination.Right < 500,
                "A busting HIT flew into the newly active split hand instead of its original hand.");
            now = splitStarted.AddMilliseconds(360);
            Draw(split);
            await Save(target, "split-bust-middle");
            now = splitStarted.AddMilliseconds(720);
            var splitLanded = Draw(split);
            Require(split.BlackjackAnimation.Count == 0 && BrightPixels(splitLanded, bustFlight.Destination, size) > 6000,
                "The first split hand's bust card did not land in its retained slot.");
            await Save(target, "split-bust-landed");
        }

        using (var cancelled = new SceneCompositor(Game(2, 6, 2, 10, 2, 2), blackjackClock: () => now))
        {
            cancelled.ShowBlackjack();
            Act(cancelled, "bj-deal");
            now = now.AddMilliseconds(1800);
            Act(cancelled, "bj-hit");
            Draw(cancelled);
            Require(cancelled.BlackjackAnimation.Count == 1, "The navigation-cancel fixture has no flight.");
            cancelled.ShowBoardMenu();
            Require(cancelled.BlackjackAnimation.Count == 0, "Leaving Blackjack retained a flight.");
            cancelled.ShowBlackjack();
            Draw(cancelled);
            Require(cancelled.BlackjackAnimation.Count == 0 && cancelled.BlackjackState.Hands.Single().Cards.Count == 3,
                "Returning to Blackjack replayed a stale flight or lost the dealt card.");
        }
        using (var reset = new SceneCompositor(Game(10, 6, 9, 10, 5), blackjackClock: () => now))
        {
            reset.ShowBlackjack();
            Act(reset, "bj-deal");
            now = now.AddMilliseconds(1800);
            Act(reset, "bj-hit");
            var resetHitStarted = now;
            Draw(reset);
            Require(reset.BlackjackState.Phase == BlackjackPhase.DealerTurn && reset.BlackjackAnimation.Count == 1,
                "The reset-cancel fixture did not retain its busting HIT flight.");
            Require(!reset.ActivateBlackjackButton("bj-reset"),
                "Reset chips was accepted while the busting HIT card was still in flight.");
            now = resetHitStarted.AddMilliseconds(720);
            Require(reset.TickBlackjack(now) && reset.BlackjackAnimation.Count == 0 &&
                    !reset.BlackjackState.DealerHoleCardHidden,
                "The busting HIT did not land before the dealer's reveal.");
            now = now.AddMilliseconds(650);
            Require(reset.TickBlackjack(now) && reset.BlackjackState.Phase == BlackjackPhase.RoundOver,
                "The busting HIT fixture did not settle before Reset chips.");
            Act(reset, "bj-reset");
            Require(reset.BlackjackAnimation.Count == 0 && reset.BlackjackState.Phase == BlackjackPhase.Betting,
                "Reset chips retained an airborne card from the completed game.");
            Draw(reset);
            Require(reset.BlackjackAnimation.Count == 0, "Drawing after Reset chips replayed an old HIT event.");
            Act(reset, "bj-deal");
            Require(reset.BlackjackAnimation.Count == 0, "A new round replayed the previous HIT flight.");
        }

        using (var dealerWait = new SceneCompositor(Game(10, 6, 9, 10, 2), blackjackClock: () => now))
        {
            dealerWait.ShowBlackjack();
            Act(dealerWait, "bj-deal");
            now = now.AddMilliseconds(1800);
            Act(dealerWait, "bj-hit");
            var winningHitStarted = now;
            Require(dealerWait.BlackjackState.Phase == BlackjackPhase.DealerTurn,
                "The 21-point HIT fixture did not advance to the dealer turn.");
            now = winningHitStarted.AddMilliseconds(650);
            Require(!dealerWait.TickBlackjack(now) && dealerWait.BlackjackState.DealerHoleCardHidden,
                "The dealer revealed its hole card before the player's HIT card landed.");
            now = winningHitStarted.AddMilliseconds(720);
            Require(dealerWait.TickBlackjack(now) && !dealerWait.BlackjackState.DealerHoleCardHidden &&
                    dealerWait.BlackjackAnimation.Count == 0,
                "The dealer did not resume once the player's HIT animation completed.");
        }

        using (var projectedScene = new SceneCompositor(Game(2, 6, 2, 10, 2), blackjackClock: () => now))
        using (var projected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1600, 900, 96))
        {
            projectedScene.SetDisplayAspect(16.0 / 9);
            projectedScene.SetBoardSetup(true);
            projectedScene.SetDetectedBoardGrid([new(.15f, .13f), new(.86f, .18f), new(.90f, .88f), new(.10f, .81f)],
                Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            projectedScene.SetBoardSetup(false);
            projectedScene.ShowBlackjack();
            Act(projectedScene, "bj-deal");
            var projectedDealStarted = now;
            now = projectedDealStarted.AddMilliseconds(225);
            var projectedDeal = DrawProjected();
            Require(projectedScene.BlackjackDealAnimation is { Phase: "dealing", LandedCards: 0 } &&
                    OutsideCornersBlack(projectedDeal, 1600, 900),
                "The opening card escaped the physical board clip.");
            await Save(projected, "deal-perspective-middle");
            now = projectedDealStarted.AddMilliseconds(1800);
            Act(projectedScene, "bj-hit");
            var projectedStarted = now;
            now = projectedStarted.AddMilliseconds(360);
            var projectedMiddle = DrawProjected();
            Require(projectedScene.BlackjackAnimation.Count == 1 && OutsideCornersBlack(projectedMiddle, 1600, 900),
                "The projected flight is missing or escapes the physical board mask.");
            await Save(projected, "hit-perspective-middle");
            now = projectedStarted.AddMilliseconds(720);
            var projectedEnd = DrawProjected();
            Require(projectedScene.BlackjackAnimation.Count == 0 && Differences(projectedMiddle, projectedEnd) > 1000 &&
                    OutsideCornersBlack(projectedEnd, 1600, 900),
                "The perspective table failed to redraw the landed card within its clip.");
            await Save(projected, "hit-perspective-landed");
            now = now.AddMilliseconds(40);
            Require(Differences(projectedEnd, DrawProjected()) == 0,
                "The perspective final frame is unstable after animation completion.");

            byte[] DrawProjected()
            {
                using (var drawing = projected.CreateDrawingSession())
                    projectedScene.Draw(drawing, 1600, 900, preview: false, runningSlowly: false);
                return projected.GetPixelBytes();
            }
        }

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated card animation check changed live hardware or gameplay.");
        return new { passed = true, liveHardwareUnchanged = true, startsOffTable = true, rotatesInFlight = true,
            noDuplicateDestination = true, finalCacheRepainted = true, rapidHitsIndependent = true,
            splitBustUsesOriginalHand = true, navigationAndResetCancel = true, dealerWaitsForLanding = true,
            perspectiveClip = true, sequentialOpeningDeal = true, oldCardsSweepLeft = true,
            dealBlocksGameInput = true, dealNavigationCancels = true, flyingHoleCardHidden = true,
            directory, images };

        async Task VerifyDealAnimations()
        {
            Require(SceneCompositor.BlackjackDealCardDuration == TimeSpan.FromMilliseconds(450) &&
                    SceneCompositor.BlackjackSweepDuration == TimeSpan.FromMilliseconds(500),
                "The expected opening/redeal durations changed.");
            var game = Game(10, 10, 8, 8, 3, 4, 6, 10, 8);
            using var opening = new SceneCompositor(game, blackjackClock: () => now);
            opening.ShowBlackjack();
            Act(opening, "bj-deal");
            var dealStarted = now;
            var openingRevision = opening.BlackjackState.Revision;
            var blank = Draw(opening);
            await Save(target, "deal-start");
            var destinations = new Rect[4];
            for (int index = 0; index < 4; index++)
            {
                now = dealStarted.AddMilliseconds(index * 450);
                var pixels = Draw(opening);
                var frame = opening.BlackjackDealAnimation;
                Require(frame is { Phase: "dealing" } && frame.LandedCards == index && frame.Cards.Count == 1,
                    "Opening cards did not fly individually, with one landing every 450 ms.");
                var card = frame!.Cards.Single();
                destinations[index] = card.Destination;
                Require(card.Dealer == (index % 2 == 1) && card.CardIndex == index / 2 &&
                        card.HandIndex == (card.Dealer ? -1 : 0) && !card.Departing && card.Progress == 0,
                    "Opening order must be player, dealer, player, dealer.");
                Require(Vector2.Distance(card.Center, new Vector2(500, -160)) < .01f &&
                        Math.Abs(card.Rotation + MathF.PI * 1.25f) < .001f,
                    "An opening card did not start above TABLETOP with a visible spin.");
                Require(RegionDifferences(blank, pixels, card.Destination) == 0,
                    "A queued or flying opening card was also drawn in its destination.");
                Require(index != 3 || card.Card is null,
                    "The moving dealer hole card exposed its hidden value.");
                Require(opening.BlackjackAnimation.Count == 0 &&
                        opening.BlackjackState.Revision == openingRevision,
                    "Opening animation created a HIT or mutated the dealt game state.");
                foreach (string action in new[] { "bj-hit", "bj-stand", "bj-double", "bj-split", "bj-deal", "bj-reset" })
                    Require(!opening.ActivateBlackjackButton(action), "Opening animation accepted " + action + ".");
                Require(opening.CurrentBoardButtons.Single(button => button.Id == "menu").Enabled &&
                        opening.CurrentBoardButtons.Where(button => button.Id != "menu").All(button => !button.Enabled) &&
                        !opening.TickBlackjack(now),
                    "Dealing did not pause gameplay while preserving menu navigation.");

                now = now.AddMilliseconds(225);
                var movingPixels = Draw(opening);
                var moving = opening.BlackjackDealAnimation!.Cards.Single();
                Require(moving.Progress == .5f && moving.Center.Y > 0 && Math.Abs(moving.Rotation) > .01f &&
                        Differences(pixels, movingPixels) > 1500,
                    "An opening card did not visibly move and rotate between cache boundaries.");
                await Save(target, index == 3 ? "deal-hole-card-middle" : $"deal-card-{index + 1}-middle");
            }
            now = dealStarted.AddMilliseconds(1800);
            var complete = Draw(opening);
            Require(opening.BlackjackDealAnimation is null && opening.BlackjackAnimation.Count == 0 &&
                    destinations.Take(3).All(rect => BrightPixels(complete, rect, size) > 6000) &&
                    RegionDifferences(blank, complete, destinations[3]) > 6000,
                "Opening completion did not retain all four cards, including the concealed dealer card.");
            using (var settled = new SceneCompositor(game, blackjackClock: () => now))
            {
                settled.ShowBlackjack();
                Require(Differences(complete, Draw(settled)) == 0,
                    "The final deal frame differs from the ordinary settled table.");
            }
            Draw(opening);
            await Save(target, "deal-complete");
            now = now.AddMilliseconds(40);
            Require(Differences(complete, Draw(opening)) == 0,
                "A completed opening deal continued changing or reused an incomplete table cache.");

            // Finish the first round so every old card is visible during the next sweep.
            Act(opening, "bj-stand");
            now = now.AddMilliseconds(650);
            Require(opening.TickBlackjack(now), "The redeal fixture did not reveal its dealer card.");
            now = now.AddMilliseconds(650);
            Require(opening.TickBlackjack(now) && opening.BlackjackState.Phase == BlackjackPhase.RoundOver,
                "The redeal fixture did not settle its first round.");
            var previous = opening.BlackjackState;
            var oldTable = Draw(opening);
            Act(opening, "bj-deal");
            var repeatStarted = now;
            var initialSweep = opening.BlackjackDealAnimation;
            Require(initialSweep is { Phase: "clearing", LandedCards: 0 } && initialSweep.Cards.Count == 4 &&
                    initialSweep.Cards.All(card => card.Departing && card.Progress == 0 && card.Rotation == 0),
                "Deal again did not begin by collecting every previous card for the leftward sweep.");
            var sweepStart = Draw(opening);
            foreach (var card in initialSweep!.Cards)
            {
                var oldCard = card.Dealer ? previous.DealerCards[card.CardIndex] : previous.Hands[card.HandIndex].Cards[card.CardIndex];
                Require(card.Card == oldCard && RegionDifferences(oldTable, sweepStart, card.Destination) == 0,
                    "The sweep replaced an old card with a new one before clearing the table.");
            }
            await Save(target, "redeal-sweep-start");
            now = repeatStarted.AddMilliseconds(250);
            var sweepMiddle = Draw(opening);
            var middle = opening.BlackjackDealAnimation;
            Require(middle is { Phase: "clearing" } && middle.Cards.Count == 4 &&
                    middle.Cards.Zip(initialSweep.Cards).All(pair => pair.First.Departing &&
                        pair.First.Center.X < pair.Second.Center.X - 200 && pair.First.Center.Y == pair.Second.Center.Y) &&
                    Differences(sweepStart, sweepMiddle) > 15000,
                "The previous cards did not visibly sweep together toward the left edge.");
            Require(!opening.ActivateBlackjackButton("bj-hit") && !opening.TickBlackjack(now),
                "The clearing phase accepted gameplay before the new cards were dealt.");
            await Save(target, "redeal-sweep-middle");
            now = repeatStarted.AddMilliseconds(499);
            Draw(opening);
            Require(opening.BlackjackDealAnimation is { Phase: "clearing" } lastSweep &&
                    lastSweep.Cards.All(card => card.Center.X + card.Destination.Width / 2 < 0),
                "The old cards had not left the viewing area at the end of the sweep.");
            now = repeatStarted.AddMilliseconds(500);
            var afterClear = Draw(opening);
            Require(opening.BlackjackDealAnimation is { Phase: "dealing", LandedCards: 0 } firstNew &&
                    firstNew.Cards.Single() is { Dealer: false, CardIndex: 0, Departing: false, Progress: 0 } &&
                    destinations.Take(3).All(rect => BrightPixels(afterClear, rect, size) < 100),
                "The new deal did not start on an empty table immediately after the 500 ms sweep.");
            await Save(target, "redeal-cleared");
            for (int index = 1; index < 4; index++)
            {
                now = repeatStarted.AddMilliseconds(500 + index * 450);
                Draw(opening);
                var frame = opening.BlackjackDealAnimation;
                Require(frame is { Phase: "dealing" } && frame.LandedCards == index &&
                        frame.Cards.Single().Dealer == (index % 2 == 1) && frame.Cards.Single().CardIndex == index / 2,
                    "The new round did not retain the alternating deal order after clearing.");
            }
            now = repeatStarted.AddMilliseconds(2300);
            var redealt = Draw(opening);
            Require(opening.BlackjackDealAnimation is null && opening.BlackjackState.RoundNumber == 2 &&
                    opening.BlackjackState.Hands.Single().Total == 9 &&
                    opening.CurrentBoardButtons.Single(button => button.Id == "bj-hit").Enabled,
                "The 2300 ms redeal did not finish and unlock the newly dealt round.");
            await Save(target, "redeal-complete");
            now = now.AddMilliseconds(40);
            Require(Differences(redealt, Draw(opening)) == 0, "The completed redeal left an unstable table cache.");

            // Compare the actual animated hole-card pixels, not just the public snapshot.
            var hiddenStarted = now;
            using var hiddenFirst = new SceneCompositor(Game(10, 6, 5, 10), blackjackClock: () => now);
            using var hiddenSecond = new SceneCompositor(Game(10, 6, 5, 9), blackjackClock: () => now);
            hiddenFirst.ShowBlackjack();
            hiddenSecond.ShowBlackjack();
            Act(hiddenFirst, "bj-deal");
            Act(hiddenSecond, "bj-deal");
            now = hiddenStarted.AddMilliseconds(1575);
            Require(hiddenFirst.BlackjackDealAnimation!.Cards.Single().Card is null &&
                    hiddenSecond.BlackjackDealAnimation!.Cards.Single().Card is null &&
                    Differences(Draw(hiddenFirst), Draw(hiddenSecond)) == 0,
                "Flying dealer-card pixels leaked the hidden rank.");
            Act(hiddenFirst, "menu");
            Require(hiddenFirst.CurrentBoardScreen == BoardScreen.Menu && hiddenFirst.BlackjackDealAnimation is null,
                "Menu navigation did not cancel an active opening deal.");
            hiddenFirst.ShowBlackjack();
            Draw(hiddenFirst);
            Require(hiddenFirst.BlackjackDealAnimation is null && hiddenFirst.BlackjackState.Hands.Single().Cards.Count == 2,
                "Returning to Blackjack replayed an opening animation or lost a dealt card.");

            using var resetDeal = new SceneCompositor(Game(10, 6, 5, 10), blackjackClock: () => now);
            resetDeal.ShowBlackjack();
            Act(resetDeal, "bj-deal");
            now = now.AddMilliseconds(900);
            Require(resetDeal.BlackjackDealAnimation is not null, "The input-reset fixture has no active deal.");
            resetDeal.ClearHandTips(resetInput: true);
            var resetPixels = Draw(resetDeal);
            Require(resetDeal.BlackjackDealAnimation is null &&
                    resetDeal.CurrentBoardButtons.Single(button => button.Id == "bj-hit").Enabled &&
                    destinations.Take(3).All(rect => BrightPixels(resetPixels, rect, size) > 6000),
                "A camera/input reset unlocked controls while leaving the opening cards in flight.");
        }

        byte[] Draw(SceneCompositor drawingScene)
        {
            using (var drawing = target.CreateDrawingSession()) drawingScene.DrawBlackjackPreview(drawing, size, size);
            return target.GetPixelBytes();
        }

        async Task Save(CanvasRenderTarget image, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }

        static BlackjackGame Game(params int[] ranks) => new(seed: 173,
            initialShoe: ranks.Select((rank, index) => new BlackjackCard(rank, (BlackjackSuit)(index % 4))));
        static void Act(SceneCompositor drawingScene, string id) =>
            Require(drawingScene.ActivateBlackjackButton(id), "Animation fixture rejected " + id + ".");
        static int Differences(byte[] first, byte[] second)
        {
            int count = 0;
            for (int index = 0; index < first.Length; index += 4)
                if (first[index] != second[index] || first[index + 1] != second[index + 1] ||
                    first[index + 2] != second[index + 2]) count++;
            return count;
        }
        static int BrightPixels(byte[] pixels, Rect rect, int width)
        {
            int count = 0;
            for (int y = (int)rect.Y + 8; y < rect.Bottom - 8; y++)
            for (int x = (int)rect.X + 8; x < rect.Right - 8; x++)
            {
                int offset = (y * width + x) * 4;
                if (pixels[offset] > 180 && pixels[offset + 1] > 180 && pixels[offset + 2] > 180) count++;
            }
            return count;
        }
        static int RegionDifferences(byte[] first, byte[] second, Rect rect)
        {
            int count = 0;
            for (int y = (int)rect.Y + 8; y < rect.Bottom - 8; y++)
            for (int x = (int)rect.X + 8; x < rect.Right - 8; x++)
            {
                int offset = (y * size + x) * 4;
                if (first[offset] != second[offset] || first[offset + 1] != second[offset + 1] ||
                    first[offset + 2] != second[offset + 2]) count++;
            }
            return count;
        }
        static bool OutsideCornersBlack(byte[] pixels, int width, int height)
        {
            foreach (var corner in new[] { (0, 0), (width - 20, 0), (0, height - 20), (width - 20, height - 20) })
            for (int y = corner.Item2; y < corner.Item2 + 20; y++)
            for (int x = corner.Item1; x < corner.Item1 + 20; x++)
            {
                int offset = (y * width + x) * 4;
                if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0) return false;
            }
            return true;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
