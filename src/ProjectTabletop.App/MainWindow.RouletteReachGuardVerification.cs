#if DEBUG
using ProjectTabletop.App.Projection;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Exercise the independent caption and hand publications against an
    // isolated production compositor. No camera or live game receives input.
    private async Task<object> VerifyRouletteReachGuardAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        int checks = 0;

        using (var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(71)))
        {
            var context = await Prepare(fixture);
            var scene = fixture.Scene;
            var snapshot = scene.RouletteState;
            var buttons = scene.CurrentBoardButtons.ToArray();
            StartPartial(fixture, context);
            int footer = context.ButtonIds.ToList().IndexOf("roulette-clear");
            int table = context.ButtonIds.ToList().IndexOf("roulette-red");
            var presence = new HandAcquisitionPresenceResult([], true, null, .1, "text-corruption-confirming",
                [Broken(footer), Broken(table)]);
            var blocked = context.BlockedButtons(presence);
            Check(blocked.ToHashSet().SetEquals(context.EnabledHoldIds),
                "Non-footer caption corruption did not veto all enabled Roulette footer controls before hand inference.");
            fixture.Now += TimeSpan.FromMilliseconds(100);
            Check(scene.ObserveHoldButtons(context, context.HeldButtons(presence), fixture.Now,
                    context.ClearedButtons(presence), blocked).Count == 0 && scene.CurrentHoldProgress.Count == 0,
                "Caption-worker-first obstruction completed the nearly finished footer hold.");
            Check(scene.CurrentBoardButtons.SequenceEqual(buttons) && scene.RouletteState == snapshot,
                "A reach veto changed button artwork, available game actions, or the wager.");
            var intact = presence with { TextPatterns = [Broken(footer), Broken(table) with
                { ShapeCorrupted = false, LabelIntact = true }] };
            Check(context.BlockedButtons(intact).Count == 0,
                "Intact non-footer lettering kept the Roulette footer blocked.");
            Check(context.BlockedButtons(presence with { TextPatterns = [Broken(table) with
                    { ShapeCorrupted = false, LabelIntact = true, CaptionReflectanceChanged = true }] }).Count > 0,
                "Qualified non-footer foreground reflectance failed to protect the footer before acquisition.");
            // The selected denomination is disabled for selection, but a hand
            // resting there must still protect the footer beneath its arm.
            int selectedChip = context.ButtonIds.ToList().IndexOf(RouletteGame.ChipAction(snapshot.Chip));
            Check(context.BlockedButtons(presence with { TextPatterns = [Broken(selectedChip)] }).Count > 0,
                "The selected chip's disabled gesture target failed to protect the footer.");
            Check(context.BlockedButtons(presence with { TextPatterns = [Broken(footer)] }).Count == 0,
                "A direct footer press blocked itself.");
            RecoverAndActivate(fixture, context);
        }

        using (var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(72)))
        {
            var context = await Prepare(fixture);
            var scene = fixture.Scene;
            StartPartial(fixture, context);
            var acquisition = scene.GetHandAcquisitionContext(fixture.Now)!;
            Check(acquisition.ObserveMotion, "The isolated Roulette acquisition context never settled.");
            var onset = fixture.Now + TimeSpan.FromMilliseconds(1);
            fixture.Now = onset;
            var hint = Hint(fixture, "roulette-red", onset);
            scene.CompleteHandAcquisition(acquisition, [hint], [], onset);
            Check(scene.GetHandAcquisitionContext(onset)?.IlluminatedHint is not null &&
                    scene.CurrentHoldProgress.Count == 0,
                "Starting a no-landmark search light did not immediately cancel the footer hold.");
            // A delayed caption result may have been computed before the light
            // arrived. It cannot finish the canceled hold or restore readiness.
            Check(scene.ObserveHoldButtons(context, ["roulette-clear"], onset.AddTicks(-1),
                    ["roulette-clear"]).Count == 0 && scene.CurrentHoldProgress.Count == 0,
                "A late pre-light caption publication restored the canceled footer hold.");
            for (int i = 0; i < 4; i++)
            {
                fixture.Now += TimeSpan.FromMilliseconds(100);
                Check(scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []).Count == 0 &&
                        scene.CurrentHoldProgress.Count == 0,
                    "An active acquisition light allowed footer hold progress.");
            }
            var lit = scene.GetHandAcquisitionContext(fixture.Now)!;
            scene.CompleteHandAcquisition(lit, [], [], fixture.Now, illuminatedPresence: false);
            Check(scene.GetHandAcquisitionContext(fixture.Now)?.IlluminatedHint is null,
                "A measured foreground departure did not clear the search light.");
            RecoverAndActivate(fixture, context);
        }

        using (var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(73)))
        {
            var context = await Prepare(fixture);
            var scene = fixture.Scene;
            StartPartial(fixture, context);
            var hand = Hand(fixture, .50, .58);
            fixture.Now = MonotonicClock.UtcNow;
            scene.SetHandSpotlights([hand], fixture.Now.AddSeconds(-1));
            scene.SetHandSpotlights([hand], fixture.Now.AddSeconds(1));
            Check(scene.CurrentHoldProgress.Count == 1,
                "A stale or future hand result canceled a valid current footer press.");
            var observed = fixture.Now;
            scene.SetHandSpotlights([hand], observed);
            Check(scene.CurrentHoldProgress.Count == 0 && scene.ActiveHandSpotlightCount == 1,
                "A fresh hand over the table did not immediately cancel footer progress.");
            fixture.Now += TimeSpan.FromMilliseconds(1);
            scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []);
            Check(scene.CurrentHoldProgress.Count == 0 && scene.RouletteState.TotalBet == 25,
                "The caption result following hand acquisition finished a blocked hold.");
            scene.SetHandSpotlights([], MonotonicClock.UtcNow);
            fixture.Now = MonotonicClock.UtcNow;
            scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []);
            Check(scene.CurrentHoldProgress.Count == 0,
                "One missing inference frame reopened the footer under a retained hand light.");
            await Task.Delay(750);
            fixture.Now = MonotonicClock.UtcNow;
            scene.SetHandSpotlights([], fixture.Now);
            Check(scene.ActiveHandSpotlightCount == 0, "A departed hand retained its light indefinitely.");
            RecoverAndActivate(fixture, context);
        }

        using (var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(75)))
        {
            var context = await Prepare(fixture);
            var scene = fixture.Scene;
            StartPartial(fixture, context);
            fixture.Now = MonotonicClock.UtcNow;
            var acquisition = scene.GetHandAcquisitionContext(fixture.Now)!;
            scene.CompleteHandAcquisition(acquisition, [Hint(fixture, "roulette-chip-5", fixture.Now)], [], fixture.Now);
            Check(scene.GetHandAcquisitionContext(fixture.Now)?.IlluminatedHint is not null,
                "The chip-selection fixture did not start an acquisition light.");
            var chip = scene.CurrentBoardButtons.Single(button => button.Id == "roulette-chip-5").Bounds;
            // Align the synthetic index fingertip with the desired chip centre.
            var hand = Hand(fixture, chip.X + chip.Width / 2 + .032, chip.Y + chip.Height / 2 + .08);
            fixture.Now = MonotonicClock.UtcNow;
            scene.SetHandCursors([new(hand.IndexTip, DateTimeOffset.MinValue) { TrackingId = 9101 }], fixture.Now);
            scene.SetHandSpotlights([hand], fixture.Now);
            scene.CompleteHandAcquisition(acquisition, [], [hand], fixture.Now);
            Check(scene.ActiveHandSpotlightCount == 1, "The acquired chip hand did not start ordinary hand lighting.");
            Check(scene.GetHandAcquisitionContext(fixture.Now)?.IlluminatedHint is null,
                "Hand acquisition did not retire the preliminary search light before testing selected-hand suppression.");
            fixture.Now = MonotonicClock.UtcNow;
            scene.SetHandCursors([new(hand.IndexTip, fixture.Now.AddSeconds(1), 4101) { TrackingId = 9101 }], fixture.Now);
            scene.SetHandSpotlights([hand], fixture.Now);
            Check(scene.RouletteState.Chip == 5 && scene.RouletteState.TotalBet == 25 &&
                    scene.RouletteState.Balance == 975,
                "The active footer guard blocked ordinary chip selection or changed the reserved wager.");
            Check(scene.ActiveHandSpotlightCount == 0 && scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(9101),
                "The selection did not exercise a deliberately suppressed, still-present hand.");
            fixture.Draw();
            context = scene.GetHoldButtonContext(fixture.Now)!;
            scene.ObserveHoldButtons(context, [], fixture.Now, ["roulette-clear"]);
            fixture.Now += TimeSpan.FromMilliseconds(100);
            scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []);
            Check(scene.CurrentHoldProgress.Count == 0 && scene.RouletteState.TotalBet == 25,
                "Suppressing a selected hand's spotlight reopened the footer beneath that hand.");
            scene.SetHandSpotlights([], MonotonicClock.UtcNow);
            // The suppression identity remains for safe reacquisition, but it
            // cannot itself act as proof that a hand still reaches over a button.
            RecoverAndActivate(fixture, context);
        }

        using (var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(76)))
        {
            var context = await Prepare(fixture);
            var scene = fixture.Scene;
            StartPartial(fixture, context);
            var footer = scene.CurrentBoardButtons.Where(button => button.IsHold).ToArray();
            Check(scene.ActivateRouletteButton("roulette-number-7"), "Could not add a second stake to the same target.");
            fixture.Draw();
            var refreshed = scene.GetHoldButtonContext(fixture.Now)!;
            Check(!ReferenceEquals(refreshed.ExpectedScene, context.ExpectedScene) &&
                    scene.CurrentBoardButtons.Where(button => button.IsHold).SequenceEqual(footer),
                "A changed stake reused the old non-footer optical reference despite unchanged footer controls.");
            Check(scene.CurrentHoldProgress is [{ ButtonId: "roulette-clear", Progress: >= .89 and < 1 }],
                "Refreshing the table artwork unnecessarily discarded an unchanged footer caption's progress.");
            fixture.Now += TimeSpan.FromMilliseconds(100);
            Check(scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []).Count == 0,
                "A delayed result from the previous stake artwork activated the footer.");
            fixture.Now += TimeSpan.FromMilliseconds(100);
            Check(scene.ObserveHoldButtons(refreshed, ["roulette-clear"], fixture.Now, [])
                    .SequenceEqual(["roulette-clear"]),
                "The refreshed table reference did not preserve an otherwise valid footer hold.");
        }

        foreach (var position in new[] { (U: .40, V: .93, Name: "footer"),
            (U: 1.2, V: .60, Name: "off-board") })
        {
            using var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(74));
            var context = await Prepare(fixture);
            StartPartial(fixture, context);
            fixture.Now = MonotonicClock.UtcNow;
            fixture.Scene.SetHandSpotlights([Hand(fixture, position.U, position.V, .025)], fixture.Now);
            Check(fixture.Scene.CurrentHoldProgress.Count == 1,
                "A " + position.Name + " hand canceled a direct footer press.");
            fixture.Now += TimeSpan.FromMilliseconds(100);
            Check(fixture.Scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, [])
                    .SequenceEqual(["roulette-clear"]),
                "A " + position.Name + " observation prevented the ordinary one-second footer hold.");
        }

        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated reach-guard check changed live hardware or navigation.");
        return new { passed = true, checks, captionWorkerFirstVeto = true, searchLightCancelsImmediately = true,
            trackedHandCancelsImmediately = true, retainedHandDropoutProtected = true,
            selectedHandRemainsBlockedWithoutLight = true, chipSelectionRemainsAvailable = true,
            wagerArtworkReferenceRefreshed = true,
            delayedCaptionsRejected = true, staleAndFutureHandsIgnored = true,
            directFooterPressPreserved = true, offBoardHandIgnored = true,
            clearThenFullSecondRequired = true, buttonArtworkUnchanged = true, liveHardwareUnchanged = true };

        async Task<SceneCompositor.HoldButtonContext> Prepare(RouletteNativeFixture fixture)
        {
            fixture.Now = MonotonicClock.UtcNow.AddSeconds(-10);
            fixture.Scene.ShowRoulette();
            await fixture.Scene.EnsureBoardArtworkResourcesAsync(fixture.Target.Device, BoardScreen.Roulette);
            Check(fixture.Scene.ActivateRouletteButton("roulette-number-7"), "Could not prepare one test wager.");
            fixture.Draw();
            fixture.Now = MonotonicClock.UtcNow.AddSeconds(-3);
            fixture.Scene.GetHandAcquisitionContext(fixture.Now);
            fixture.Now += TimeSpan.FromMilliseconds(600);
            fixture.Draw();
            fixture.Scene.GetHandAcquisitionContext(fixture.Now);
            return fixture.Scene.GetHoldButtonContext(fixture.Now) ??
                throw new InvalidOperationException("No isolated Roulette hold reference.");
        }

        void StartPartial(RouletteNativeFixture fixture, SceneCompositor.HoldButtonContext context)
        {
            fixture.Now = MonotonicClock.UtcNow.AddMilliseconds(-1100);
            Check(fixture.Scene.ObserveHoldButtons(context, [], fixture.Now, ["roulette-clear"]).Count == 0,
                "Uncovered footer lettering selected an action.");
            for (int i = 0; i < 10; i++)
            {
                fixture.Now += TimeSpan.FromMilliseconds(100);
                Check(fixture.Scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []).Count == 0,
                    "The footer activated before its complete one-second hold.");
            }
            Check(fixture.Scene.CurrentHoldProgress is [{ ButtonId: "roulette-clear", Progress: >= .89 and < 1 }],
                "The isolated fixture did not reach a 900 ms footer hold.");
        }

        void RecoverAndActivate(RouletteNativeFixture fixture, SceneCompositor.HoldButtonContext context)
        {
            for (int i = 0; i < 13; i++)
            {
                fixture.Now += TimeSpan.FromMilliseconds(100);
                Check(fixture.Scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []).Count == 0 &&
                        fixture.Scene.CurrentHoldProgress.Count == 0,
                    "Removing a reach obstruction rearmed a still-covered footer caption.");
            }
            fixture.Now += TimeSpan.FromMilliseconds(100);
            fixture.Scene.ObserveHoldButtons(context, [], fixture.Now, ["roulette-clear"]);
            for (int i = 0; i < 11; i++)
            {
                fixture.Now += TimeSpan.FromMilliseconds(100);
                var result = fixture.Scene.ObserveHoldButtons(context, ["roulette-clear"], fixture.Now, []);
                Check(i < 10 ? result.Count == 0 : result.SequenceEqual(["roulette-clear"]),
                    "A released footer did not require and accept a fresh full one-second hold.");
            }
            Check(fixture.Scene.RouletteState.TotalBet == 0 && fixture.Scene.RouletteState.Balance == 1000,
                "The recovered clear action did not preserve the wager accounting.");
        }

        static HandAcquisitionTextPatternResult Broken(int index) => new(index, false, .2, .20, .30,
            ShapeCorrupted: true, ConfirmationFrames: 1);
        static HandAcquisitionHint Hint(RouletteNativeFixture fixture, string id, DateTimeOffset when)
        {
            var button = fixture.Scene.CurrentBoardButtons.Single(button => button.Id == id);
            var center = fixture.CameraPoint(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2);
            return new(new(center.X - 100, center.Y - 100, 200, 200), center, 60, when, .10,
                ControlCoverage: .20, ControlTriggerCoverage: .20);
        }
        static HandDetection Hand(RouletteNativeFixture fixture, double u, double v, double span = .08)
        {
            PixelPoint[] local = [new(0, .8), new(-.3, .5), new(-.6, .3), new(-.8, .1), new(-1, 0),
                new(-.4, .1), new(-.4, -.3), new(-.4, -.7), new(-.4, -1),
                new(0, 0), new(0, -.4), new(0, -.8), new(0, -1.2),
                new(.4, .1), new(.4, -.3), new(.4, -.6), new(.4, -.9),
                new(.7, .3), new(.8, 0), new(.9, -.2), new(1, -.4)];
            return new(local.Select(point => fixture.CameraPoint(u + point.X * span, v + point.Y * span)).ToArray(), .99, .5);
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
#endif
