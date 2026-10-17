#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.StrykersRun;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // All cameras, clocks, scenes and game fixtures below are synthetic. Native
    // Win2D resources exercise the installed graphics path without touching a device.
    private async Task<object> VerifyStrykersRunAsync()
    {
        Check(!_closing && !_camera.IsRunning && !_cameraWanted,
            "Stryker's Run verification requires the existing app's camera to be stopped.");
        var liveScreen = _scene.CurrentBoardScreen;
        long liveNavigation = Session(_scene).NavigationRevision;
        string directory = Path.Combine(_appDataDirectory, "StrykersRunVerification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var device = CanvasDevice.GetSharedDevice();
        // Stay later than internal calibration barriers, which use the real clock.
        var now = MonotonicClock.UtcNow.AddMinutes(10);
        int renderedFrames = 0;
        using var scene = MakeScene(9d / 16, out double inset);
        await scene.EnsureStrykersRunResourcesAsync(device);
        using var portrait = new CanvasRenderTarget(device, 900, 1600, 96);
        using var camera = new CanvasRenderTarget(device, 1000, 900, 96);
        Draw(scene, camera);
        DrawPreview(scene, portrait);
        Check(Math.Abs(scene.StrykersRunPreviewAspect - 9d / 16) < .00001,
            "The portrait fixture lost its physical aspect.");
        var field = scene.StrykersRunFieldBounds();
        Check(Math.Abs(field.Width * scene.StrykersRunPreviewAspect / field.Height - 9d / 16) < .00001,
            "The playfield stretched instead of preserving its portrait aspect.");
        Check(scene.StrykersRunState is { Phase: StrykersRunPhase.Ready, HeroAlive: true, SoldierCount: 1, TroopCount: 0 } &&
            Session(scene).Buttons.Select(button => button.Label).SequenceEqual(["EXIT", "PLAY"]),
            "The ready board must show its lone hero and exactly EXIT and PLAY.");
        Check(scene.HandDetectionRequired, "Stationary ready controls did not enable caption processing.");
        CheckMenu();
        CheckPerspective();
        CheckPlayerEdgeOrientation();
        long readyRailEpoch = scene.StrykersRunInputRevision;
        Check(!scene.StrykersRunInputActive && scene.GetStrykersRunRailGeometry() is null &&
            scene.StrykersRunRailProgress == 0 && RailDotCenter(scene, BoardPixels(scene)) is null,
            "The ready screen exposed input geometry or visible dot marks before PLAY.");
        var hold = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No ready caption reference.");
        Check(hold.EnabledHoldIds.SetEquals(["strykers-run-exit", "strykers-run-play"]) &&
            hold.ExpectedScene.BoardTriggerRegions?.Count == 2,
            "Ready controls lost their actual rendered-letter reference regions.");
        object captionEvidence = CheckCaptions(hold, camera.GetPixelBytes());
        object controlExposure = await CheckControlExposure();
        object footerLayout = await CheckFooterLayout();
        DrawPreview(scene, portrait);
        await Save(portrait, "ready-portrait");

        Draw(scene, camera);
        var readyPixels = BoardPixels(scene);
        var readyButtonBounds = Session(scene).Buttons.Select(button => button.Bounds).ToArray();
        double readyPlate = ControlPlateLight(scene, readyPixels);
        var beforePlay = now;
        Check(scene.ActivateStrykersRunButton("strykers-run-play") &&
            scene.StrykersRunState.Phase == StrykersRunPhase.Starting &&
            Session(scene).Buttons.All(button => !button.Enabled) && !scene.HandDetectionRequired &&
            !scene.StrykersRunInputActive && scene.GetStrykersRunRailGeometry() is null,
            "PLAY did not disable caption and rail processing during the transition.");
        now += TimeSpan.FromMilliseconds(150);
        Draw(scene, camera);
        Check(Session(scene).StrykersRunControlsProgress is > 0 and < 1 && scene.GetHoldButtonContext(now) is null &&
            !scene.ActivateStrykersRunButton("strykers-run-exit") &&
            !scene.SetStrykersRunRailPosition(.5f, now, readyRailEpoch) &&
            !scene.SetStrykersRunRailPosition(.5f, now, scene.StrykersRunInputRevision),
            "Fading controls or the moving rail accepted camera input.");
        var earlyPixels = BoardPixels(scene);
        double? earlyDotCenter = RailDotCenter(scene, earlyPixels);
        double earlyPlate = ControlPlateLight(scene, earlyPixels);
        Check(Session(scene).Buttons.Select(button => button.Bounds).SequenceEqual(readyButtonBounds) &&
            earlyPlate < readyPlate - 10 && earlyPlate > 20 && scene.StrykersRunRailAnimating &&
            scene.GetStrykersRunRailGeometry() is null,
            "PLAY must fade its fixed controls while the input rail moves upward without a detection reference.");
        now += TimeSpan.FromMilliseconds(150);
        Draw(scene, camera);
        var slidingFrameArrival = now;
        var laterPixels = BoardPixels(scene);
        double? laterDotCenter = RailDotCenter(scene, laterPixels);
        Check(earlyDotCenter.HasValue && laterDotCenter.HasValue && laterDotCenter.Value < earlyDotCenter.Value - 5 &&
            ControlPlateLight(scene, laterPixels) < earlyPlate - 10 &&
            Session(scene).Buttons.Select(button => button.Bounds).SequenceEqual(readyButtonBounds) &&
            !scene.StrykersRunInputActive && scene.GetStrykersRunRailGeometry() is null,
            "The rail did not visibly scroll upward while stationary controls continued fading.");
        DrawPreview(scene, portrait);
        await Save(portrait, "starting-fade-and-rail-entry");
        for (int frame = 0; frame < 2; frame++) { now += TimeSpan.FromMilliseconds(85); Draw(scene, camera); }
        Check(scene.StrykersRunState.Phase == StrykersRunPhase.WaitingForInput && Session(scene).Buttons.Count == 0 &&
            !scene.HandDetectionRequired && scene.StrykersRunInputActive && !scene.StrykersRunRailAnimating,
            "The control fade and rail entry did not finish without requiring a hand.");
        var rail = scene.GetStrykersRunRailGeometry() ?? throw new InvalidOperationException("No playing rail geometry.");
        var railSettledAt = now;
        Check(scene.GetStrykersRunRailGeometry(now) is null,
            "The rail's activation frame was available to optical registration.");
        CheckRailGeometry(scene, rail, inset, rotated: false);
        var settledRailPixels = BoardPixels(scene);
        CheckRailDotPixels(scene, settledRailPixels);
        now += TimeSpan.FromMilliseconds(40);
        Draw(scene, camera);
        Check(RailEqual(settledRailPixels, BoardPixels(scene), scene.StrykersRunRailBounds()) &&
            scene.GetStrykersRunRailGeometry()!.Revision == rail.Revision,
            "The settled rail continued animating or changing its detection epoch.");
        Check(now - slidingFrameArrival < StrykersRunGame.InputFreshness &&
            scene.GetStrykersRunRailGeometry(slidingFrameArrival) is null &&
            scene.GetStrykersRunRailGeometry(now, slidingFrameArrival) is null &&
            scene.GetStrykersRunRailGeometry(now, railSettledAt) is null,
            "A fresh slide frame, delayed CPU copy or pre-settle optical reference could seed rail detection.");
        Check(ReferenceEquals(rail, scene.GetStrykersRunRailGeometry(now, railSettledAt.AddTicks(1))) &&
            scene.GetStrykersRunRailGeometry(now.AddSeconds(1)) is null &&
            scene.GetStrykersRunRailGeometry(now - StrykersRunGame.InputFreshness - TimeSpan.FromTicks(1)) is null,
            "Rail registration did not distinguish settled frames from future or stale camera work.");
        Check(!scene.SetStrykersRunRailPosition(.5f, beforePlay, rail.Revision) &&
            !scene.SetStrykersRunRailPosition(.5f, now.AddSeconds(1), rail.Revision),
            "PLAY admitted a pre-start or future observation.");
        now += TimeSpan.FromMilliseconds(10);
        Check(scene.SetStrykersRunRailPosition(0, now, rail.Revision), "A fresh left-edge observation was rejected.");
        Draw(scene, camera);
        var accepted = now;
        Check(!scene.SetStrykersRunRailPosition(1, accepted, rail.Revision) &&
            !scene.SetStrykersRunRailPosition(1, accepted.AddMilliseconds(-1), rail.Revision) &&
            scene.GetStrykersRunRailGeometry(accepted) is null &&
            scene.GetStrykersRunRailGeometry(accepted.AddMilliseconds(-1)) is null,
            "Duplicate or reversed camera frames changed the rail position.");
        MoveTo(0);
        Check(Math.Abs(scene.StrykersRunState.SquadX - StrykersRunGame.RoadLeft) < .0001,
            "Rail zero did not map to the road's left edge.");
        MoveTo(1);
        Check(Math.Abs(scene.StrykersRunState.SquadX - StrykersRunGame.RoadRight) < .0001,
            "Rail one did not map to the road's right edge.");
        Check(!scene.HandDetectionRequired && scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, ContinuousSearchPolygon: null,
            RestrictAcquisitionToSearchRegions: true, StationarySearchCenters.Length: 0 },
            "Rail-only play enabled a hand-model search region.");
        var paused = scene.StrykersRunState;
        scene.ClearStrykersRunInput();
        Check(scene.GetStrykersRunRailGeometry(now) is null,
            "A queued frame from before input reset could enter the rail detector.");
        for (int step = 0; step < 5; step++) { now += TimeSpan.FromMilliseconds(100); Draw(scene, camera); }
        Check(scene.StrykersRunState is { Phase: StrykersRunPhase.WaitingForInput, RecoveringInput: true,
                HeroAlive: true, TroopCount: 0 } && scene.HandDetectionRequired && SameRun(paused, scene.StrykersRunState),
            "Clearing the rail failed to preserve the run and enable the paused EXIT captions.");
        string[] pausedExitIds = ["strykers-run-exit", "strykers-run-exit-right"];
        var pausedButtons = Session(scene).Buttons;
        var pausedRailBounds = scene.StrykersRunRailBounds();
        Check(pausedButtons.Select(button => button.Id).SequenceEqual(pausedExitIds) &&
            pausedButtons.All(button => button is { Label: "EXIT", Enabled: true, Hold: BoardButtonHold.Once }) &&
            pausedButtons[0].Bounds == new BoardRect(.015, field.Y + field.Height * .9075, .12, field.Height * .085) &&
            pausedButtons[1].Bounds == new BoardRect(.865, field.Y + field.Height * .9075, .12, field.Height * .085) &&
            pausedButtons[0].Bounds.X + pausedButtons[0].Bounds.Width < pausedRailBounds.X &&
            pausedRailBounds.Right < pausedButtons[1].Bounds.X &&
            Math.Abs(pausedRailBounds.X - (field.X + field.Width * .18)) < .000001 &&
            Math.Abs(pausedRailBounds.Width - field.Width * .64) < .000001,
            "Paused EXIT holds must sit beside both ends of the permanently shortened rail.");
        var pausedHold = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No paused EXIT caption references.");
        Check(pausedHold.Revision != hold.Revision && pausedHold.ButtonIds.SequenceEqual(pausedExitIds) &&
            pausedHold.EnabledHoldIds.SetEquals(pausedExitIds) &&
            pausedHold.ExpectedScene.BoardTriggerRegions?.Count == 2 &&
            RailEqual(settledRailPixels, BoardPixels(scene), scene.StrykersRunRailBounds()),
            "The paused EXIT controls reused ready captions, exposed PLAY or changed the rail pixels.");
        DrawPreview(scene, portrait);
        await Save(portrait, "paused-exit-portrait");
        object pausedCaptionEvidence = CheckPausedCaptions(pausedHold, camera.GetPixelBytes());
        var resumedRail = scene.GetStrykersRunRailGeometry()!;
        Check(resumedRail.Revision != rail.Revision && !scene.SetStrykersRunRailPosition(.5f, now, rail.Revision) &&
            !scene.SetStrykersRunRailPosition(.5f, now.AddSeconds(-1), resumedRail.Revision) &&
            !ReferenceEquals(rail, scene.GetStrykersRunRailGeometry(now)),
            "A reset accepted an old geometry epoch or stale camera result.");

        // A normal uncovered-rail observation pauses without resetting optical
        // registration. Returning to the same rail must hide EXIT without a new run.
        now += TimeSpan.FromMilliseconds(10);
        Check(scene.SetStrykersRunRailPosition(1, now, resumedRail.Revision), "Fresh input could not resume the paused run.");
        Draw(scene, camera);
        Check(scene.StrykersRunState.Phase == StrykersRunPhase.Playing && SameRun(paused, scene.StrykersRunState) &&
            Session(scene).Buttons.Count == 0 && !scene.HandDetectionRequired && scene.GetHoldButtonContext(now) is null &&
            scene.ObserveHoldButtons(pausedHold, pausedExitIds, now, []).Count == 0 &&
            scene.CurrentHoldProgress.Count == 0 && ReferenceEquals(resumedRail, scene.GetStrykersRunRailGeometry()),
            "Resuming changed the run or rail epoch, retained EXIT or accepted its old caption context.");
        now += TimeSpan.FromMilliseconds(10);
        Check(scene.SetStrykersRunRailPosition(null, now, resumedRail.Revision), "An uncovered rail observation was rejected.");
        for (int step = 0; step < 5; step++) { now += TimeSpan.FromMilliseconds(100); Draw(scene, camera); }
        var uncoveredHold = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("EXIT did not return after lifting off the rail.");
        Check(scene.StrykersRunState is { Phase: StrykersRunPhase.WaitingForInput, RecoveringInput: true } &&
            SameRun(paused, scene.StrykersRunState) && scene.HandDetectionRequired &&
            uncoveredHold.ButtonIds.SequenceEqual(pausedExitIds) && uncoveredHold.EnabledHoldIds.SetEquals(pausedExitIds) &&
            ReferenceEquals(resumedRail, scene.GetStrykersRunRailGeometry()) &&
            RailEqual(settledRailPixels, BoardPixels(scene), scene.StrykersRunRailBounds()),
            "A normal input pause changed the run, reset rail registration or failed to restore only EXIT.");
        now += TimeSpan.FromMilliseconds(10);
        Check(scene.SetStrykersRunRailPosition(1, now, resumedRail.Revision), "Returning to an uncovered rail failed to resume.");
        Draw(scene, camera);
        Check(scene.StrykersRunState.Phase == StrykersRunPhase.Playing && SameRun(paused, scene.StrykersRunState) &&
            Session(scene).Buttons.Count == 0 && !scene.HandDetectionRequired && scene.GetHoldButtonContext(now) is null &&
            scene.ObserveHoldButtons(uncoveredHold, pausedExitIds, now, []).Count == 0 &&
            scene.CurrentHoldProgress.Count == 0 && ReferenceEquals(resumedRail, scene.GetStrykersRunRailGeometry()) &&
            RailEqual(settledRailPixels, BoardPixels(scene), scene.StrykersRunRailBounds()),
            "Normal input resume reset the run or rail, retained caption processing or accepted a paused EXIT observation.");

        scene.SetBoardFacingDegrees(0);
        Draw(scene, camera);
        var turned = scene.GetStrykersRunRailGeometry() ?? throw new InvalidOperationException("Quarter-turn rail disappeared.");
        Check(turned.Revision != resumedRail.Revision, "Rotation reused the camera worker's old epoch.");
        CheckRailGeometry(scene, turned, inset, rotated: true);
        Check(!scene.SetStrykersRunRailPosition(.5f, now, resumedRail.Revision), "Rotation accepted an in-flight old geometry result.");
        // Replacing the calibrated camera map also rejects observations from the old map.
        scene.SetBoardFacingDegrees(-90);
        scene.SetBoardSetup(true);
        Configure(scene, out _);
        scene.SetBoardSetup(false);
        scene.ShowStrykersRun();
        Draw(scene, camera);
        Check(!scene.StrykersRunInputActive && scene.GetStrykersRunRailGeometry() is null &&
            scene.StrykersRunInputRevision != turned.Revision && !scene.SetStrykersRunRailPosition(.5f, now, turned.Revision),
            "A new camera calibration accepted the previous geometry epoch.");

        using var renderer = new StrykersRunRenderer(device);
        await renderer.EnsureReadyAsync();
        Check(renderer.Ready && renderer.Error is null, "The native Stryker's Run resources did not load.");
        var clean = scene.StrykersRunState with { Phase = StrykersRunPhase.Playing, RunSeconds = 17, Distance = 136,
            Score = 480, Wave = 3, Banner = "Stryker's Run" };
        var recruited = clean with { SoldierCount = 8 };
        var busy = DenseState(clean);
        RenderRaw(recruited);
        await Save(portrait, "squad-eight");
        RenderRaw(busy);
        await Save(portrait, "squad-sixty-four-monsters-and-gates");
        object allyRendering = await CheckAllies();
        var sceneryRendering = await StrykersRunSceneryVerification.RunAsync(device, directory);
        images.AddRange(sceneryRendering.Images);
        var campaignRendering = await StrykersRunCampaignVerification.RunAsync(device, directory);
        images.AddRange(campaignRendering.Images);
        renderedFrames += campaignRendering.RenderedFrames;
        var wallFrames = new List<object>();
        object? settledWallComparison = null, sameTimeWallComparison = null;
        var wallFace = new Rect(180, 960, 540, 330);
        int sameTimeWallFaceChangedPixels = 0;
        const int wallFaceQuantizationFloor = 540 * 330 / 10000;
        byte[]? noWall = null, settledWall = null;
        foreach (float seconds in new[] { 0f, .4f, .85f, 1.5f, 2.7f, 3.15f })
        {
            var wallState = busy with { Phase = StrykersRunPhase.GameOver, SoldierCount = 0, GameOverSeconds = seconds, Effects = [] };
            RenderRaw(wallState);
            byte[] pixels = portrait.GetPixelBytes();
            await Save(portrait, "game-over-" + seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (seconds == 0) noWall = pixels;
            int changed = Changed(noWall!, pixels, 900, new(0, 0, 900, 1440));
            if (seconds == .85f) Check(changed > 1000, "The impact wall did not visibly enter the frame.");
            Check(Changed(noWall!, pixels, 900, new(0, 1442, 900, 158)) == 0,
                "The falling wall or debris escaped into the protected rail.");
            if (seconds == 2.7f)
            {
                RenderRaw(wallState);
                byte[] repeated = portrait.GetPixelBytes();
                await Save(portrait, "game-over-2.70-repeat");
                sameTimeWallFaceChangedPixels = Changed(pixels, repeated, 900, wallFace);
                sameTimeWallComparison = CompareSettledWall(pixels, repeated, "Repeated 2.7-second frame", null);
                settledWall = repeated;
            }
            if (seconds == 3.15f)
                settledWallComparison = CompareSettledWall(settledWall!, pixels, "Settled 2.7-to-3.15-second wall",
                    sameTimeWallFaceChangedPixels + wallFaceQuantizationFloor);
            wallFrames.Add(new { seconds, changedPixels = changed });
        }
        CheckReturningControls(victory: false);
        DrawPreview(scene, portrait);
        await Save(portrait, "game-over-returned-controls");
        CheckReturningControls(victory: true);
        DrawPreview(scene, portrait);
        await Save(portrait, "victory-returned-controls");
        using (var wide = MakeScene(16d / 9, out _))
        using (var landscape = new CanvasRenderTarget(device, 1600, 900, 96))
        {
            await wide.EnsureStrykersRunResourcesAsync(device);
            DrawPreview(wide, landscape);
            var bounds = wide.StrykersRunFieldBounds();
            Check(Math.Abs(bounds.Width * wide.StrykersRunPreviewAspect / bounds.Height - 9d / 16) < .00001 &&
                bounds.X > .3 && bounds.Right < .7, "The wide board stretched the portrait road instead of letterboxing it.");
            await Save(landscape, "wide-landscape");
        }
        object timing = MeasureRendering();
        Check(_scene.CurrentBoardScreen == liveScreen && Session(_scene).NavigationRevision == liveNavigation &&
            !_camera.IsRunning && !_cameraWanted, "The isolated verifier changed the live board or camera state.");
        return new { passed = true, directory, images, renderedFrames, captionEvidence, pausedCaptionEvidence, controlExposure, footerLayout, allyRendering, campaignRendering, sceneryRendering, wallFrames, settledWallComparison, sameTimeWallComparison, timing,
            loneHero = true, perspective = true, railPixelsProtected = true, normalizedRailEdges = true,
            geometryEpochs = true, cameraTimeBarriers = true, inputFreeze = true, returningCaptionBarrier = true,
            pausedExitOnly = true, pausedExitBothSides = true, pauseResumeKeepsRailEpoch = true, pausedExitCaptionBarrier = true,
            playerEdgeOrientation = true, savedFacingRestoredOnExit = true,
            handProcessingPhaseGate = true, readyRailHidden = true, fixedControlFade = true,
            railScrollWithoutDetection = true, marksCenteredInBlackFooter = true,
            slideFramesExcludedBeforeDetection = true, queuedRailEpochRechecked = true,
            liveSceneUnchanged = true, cameraOpened = false };

        async Task<object> CheckAllies()
        {
            var fixture = clean with { SoldierCount = 7, Objectives = [], Ally = null,
                Enemies = [], Gates = [], Bullets = [], Effects = [] };
            var locked = fixture with { Objectives = [new(900, .5f, .49f, StrykersRunObjectiveKind.Rescue, 8, 8)] };
            RenderRaw(locked);
            byte[] intact = portrait.GetPixelBytes();
            await Save(portrait, "bulwark-locked-pod");
            var damaged = locked with { Objectives = [new(900, .5f, .49f, StrykersRunObjectiveKind.Rescue, 3, 8)] };
            RenderRaw(damaged);
            byte[] broken = portrait.GetPixelBytes();
            Check(Changed(intact, broken, 900, new(325, 565, 250, 245)) > 100,
                "Shooting the rescue lock did not visibly crack the pod and update its remaining strength.");
            await Save(portrait, "bulwark-damaged-pod");
            var defender = StrykersRunFormation.DefenderPosition(fixture.SquadX);
            var freed = fixture with { Ally = new(defender.X, defender.Y, 24, 24, .2f, 1),
                Effects = [new(StrykersRunEffectKind.AllyUnlocked, .5f, .49f, .18f, 110)] };
            RenderRaw(freed);
            await Save(portrait, "bulwark-freed-full-health");
            var steady = freed with { Effects = [] };
            RenderRaw(steady);
            byte[] healthy = portrait.GetPixelBytes();
            var injured = steady with { Ally = new(defender.X, defender.Y, 7, 24, .2f, 1) };
            RenderRaw(injured);
            byte[] hurt = portrait.GetPixelBytes();
            Check(Changed(healthy, hurt, 900, new(325, 780, 250, 100)) > 100,
                "The defender's numeric HP and friendly health bar did not change after damage.");
            await Save(portrait, "bulwark-injured-health");
            var melee = injured with { Ally = new(defender.X, defender.Y, 7, 24, .2f, .32f),
                Enemies = [new(901, .39f, .59f, StrykersRunEnemyKind.Brute, 3, 8, .2f),
                    new(902, .61f, .56f, StrykersRunEnemyKind.Runner, 1, 1, .4f)],
                Effects = [new(StrykersRunEffectKind.AllyAttack, defender.X, defender.Y, .19f, 113)] };
            RenderRaw(melee);
            await Save(portrait, "bulwark-melee-shockwave");
            RenderRaw(injured with { Ally = null, Effects = [new(StrykersRunEffectKind.AllyLost, defender.X, defender.Y, .27f, 117)] });
            await Save(portrait, "bulwark-defeated-hero-alive");
            var cache = steady with { Objectives = [new(903, .73f, .48f, StrykersRunObjectiveKind.Reinforcements, 12, 12)] };
            RenderRaw(cache);
            await Save(portrait, "reinforcement-armoured-cache");
            RenderRaw(steady with { SoldierCount = 13,
                Effects = [new(StrykersRunEffectKind.SupplyOpened, .73f, .48f, .23f, 120)] });
            await Save(portrait, "reinforcement-cache-opened");
            Check(Changed(intact, portrait.GetPixelBytes(), 900, new(0, 1442, 900, 158)) == 0,
                "The defender, rescue pod or reinforcement effects entered the protected control strip.");
            return new { rescueLockDamageVisible = true, defenderHealthVisible = true, meleeShockwave = true,
                defeatLeavesHero = true, reinforcementCache = true, railPixelsProtected = true,
                additionalInspectionImages = 8, maximumObjectives = StrykersRunGame.MaximumObjectives };
        }

        object CompareSettledWall(byte[] initial, byte[] pixels, string comparison, int? wallFacePixelLimit)
        {
            int count = 0, maximum = 0, left = 900, top = 1600, right = -1, bottom = -1;
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                int difference = 0;
                for (int channel = 0; channel < 4; channel++)
                    difference = Math.Max(difference, Math.Abs(initial[pixel + channel] - pixels[pixel + channel]));
                if (difference == 0) continue;
                int x = pixel / 4 % 900, y = pixel / 4 / 900;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                count++; maximum = Math.Max(maximum, difference);
            }
            // Identical-time native renders can round channels by one unit even
            // on the wall. Bound the later wall-face comparison by that observed
            // repeat noise plus a 0.01% face-area floor, using the warmed reference.
            int wallFaceChangedPixels = Changed(initial, pixels, 900, wallFace);
            Check(maximum <= 1 && (!wallFacePixelLimit.HasValue || wallFaceChangedPixels <= wallFacePixelLimit.Value),
                $"{comparison} changed: {wallFaceChangedPixels} wall-face pixels; {count} full-frame pixels, " +
                $"wall-face limit {wallFacePixelLimit?.ToString() ?? "repeat baseline"}, maximum channel difference {maximum}, " +
                $"bounds ({left},{top})–({right},{bottom}). Captures: {directory}");
            return new { changedPixels = count, maximumChannelDifference = maximum,
                changedBounds = count == 0 ? null : new[] { left, top, right, bottom },
                wallFaceChangedPixels, wallFacePixelLimit, wallFaceQuantizationFloor, wallFaceBounds = new[] { 180, 960, 719, 1289 } };
        }

        object CheckCaptions(SceneCompositor.HoldButtonContext context, byte[] empty)
        {
            var tracker = new HandAcquisitionPresenceTracker();
            HandAcquisitionPresenceResult Feed(byte[] pixels)
            {
                now += TimeSpan.FromMilliseconds(100);
                return tracker.Update(1000, 900, 4000, pixels, context.SearchPolygon, context.ExpectedScene, now, now);
            }
            var clear = Feed(empty);
            Check(context.ClearedButtons(clear).Contains("strykers-run-play") && context.HeldButtons(clear).Count == 0,
                "The real rendered PLAY lettering was not recognized intact.");
            scene.ObserveHoldButtons(context, [], now, context.ClearedButtons(clear));
            int index = context.ButtonIds.ToList().IndexOf("strykers-run-play");
            var control = context.ExpectedScene.BoardSearchRegions![index];
            var broken = (byte[])empty.Clone();
            var a = CameraPoint(control.X + control.Width * .24, control.Y + control.Height * .10, inset, false);
            var b = CameraPoint(control.X + control.Width * .76, control.Y + control.Height * .90, inset, false);
            for (int y = Math.Max(0, (int)a.Y); y < Math.Min(900, b.Y); y++)
            for (int x = Math.Max(0, (int)a.X); x < Math.Min(1000, b.X); x++)
            { int offset = (y * 1000 + x) * 4; broken[offset] = 58; broken[offset + 1] = 93; broken[offset + 2] = 169; broken[offset + 3] = 255; }
            HandAcquisitionPresenceResult damaged = clear;
            for (int frame = 0; frame < 3; frame++)
            {
                damaged = Feed(broken);
                Check(scene.ObserveHoldButtons(context, context.HeldButtons(damaged), now, context.ClearedButtons(damaged)).Count == 0,
                    "A short caption obstruction prematurely pressed PLAY.");
            }
            Check(context.HeldButtons(damaged).Contains("strykers-run-play") && scene.CurrentHoldProgress.Count == 1,
                "Actual corruption of the rendered PLAY letters failed to create partial hold evidence.");
            Check(context.HeldButtons(damaged with { Hints = [], TextPatterns = damaged.TextPatterns!.Select(pattern =>
                    pattern with { ControlCoverage = 0, ConfirmationFrames = 0 }).ToArray() }).Contains("strykers-run-play"),
                "Stryker's PLAY required whole-plate damage or hand acquisition in addition to broken lettering.");
            var tinted = (byte[])empty.Clone();
            for (int pixel = 0; pixel < tinted.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++) tinted[pixel + channel] = (byte)(tinted[pixel + channel] * .60 + 18);
            var intact = Feed(tinted);
            Check(context.ClearedButtons(intact).Contains("strykers-run-play") && context.HeldButtons(intact).Count == 0,
                "Intact dimmed lettering became a hold.");
            scene.ObserveHoldButtons(context, [], now, context.ClearedButtons(intact));
            Check(scene.CurrentHoldProgress.Count == 0, "Intact lettering failed to cancel partial PLAY progress.");
            return new { generatedCaptionShapes = true, actualBrokenLetters = true, intactExposureCancels = true };
        }

        object CheckPausedCaptions(SceneCompositor.HoldButtonContext context, byte[] empty)
        {
            var evidence = new List<object>();
            var railBounds = scene.StrykersRunRailBounds();
            var railStart = CameraPoint(railBounds.X, railBounds.Y, inset, false);
            var railEnd = CameraPoint(railBounds.Right, railBounds.Bottom, inset, false);
            var railPixels = new Rect(railStart.X, railStart.Y, railEnd.X - railStart.X, railEnd.Y - railStart.Y);
            for (int index = 0; index < context.ButtonIds.Count; index++)
            {
                string id = context.ButtonIds[index];
                var button = Session(scene).Buttons[index];
                var caption = context.ExpectedScene.BoardTriggerRegions![index];
                Check(caption.X > button.Bounds.X && caption.Y > button.Bounds.Y &&
                    caption.X + caption.Width < button.Bounds.X + button.Bounds.Width &&
                    caption.Y + caption.Height < button.Bounds.Y + button.Bounds.Height,
                    $"{id} has a caption reference clipped against its button edge.");
                var tracker = new HandAcquisitionPresenceTracker();
                HandAcquisitionPresenceResult Feed(byte[] pixels)
                {
                    now += TimeSpan.FromMilliseconds(100);
                    return tracker.Update(1000, 900, 4000, pixels, context.SearchPolygon, context.ExpectedScene, now, now);
                }
                var clear = Feed(empty);
                Check(context.ClearedButtons(clear).Order().SequenceEqual(context.ButtonIds.Order()) &&
                    context.HeldButtons(clear).Count == 0 && clear.TextPatterns!.All(pattern => !pattern.CaptionClipped),
                    $"The paired paused EXIT lettering was not fully readable before testing {id}.");
                scene.ObserveHoldButtons(context, [], now, context.ClearedButtons(clear));
                var broken = (byte[])empty.Clone();
                var a = CameraPoint(caption.X + caption.Width * .24, caption.Y, inset, false);
                var b = CameraPoint(caption.X + caption.Width * .76, caption.Y + caption.Height, inset, false);
                for (int y = Math.Max(0, (int)a.Y); y < Math.Min(900, b.Y); y++)
                for (int x = Math.Max(0, (int)a.X); x < Math.Min(1000, b.X); x++)
                { int offset = (y * 1000 + x) * 4; broken[offset] = 58; broken[offset + 1] = 93; broken[offset + 2] = 169; broken[offset + 3] = 255; }
                Check(Changed(empty, broken, 1000, railPixels) == 0,
                    $"Covering the {id} lettering modified the synthetic control rail.");
                HandAcquisitionPresenceResult damaged = clear;
                for (int frame = 0; frame < 3; frame++)
                {
                    damaged = Feed(broken);
                    Check(scene.ObserveHoldButtons(context, context.HeldButtons(damaged), now,
                        context.ClearedButtons(damaged)).Count == 0, $"A short {id} obstruction exited the run.");
                }
                Check(context.HeldButtons(damaged).SequenceEqual([id]) && scene.CurrentHoldProgress.Count == 1,
                    $"Actual broken {id} lettering did not independently create hold progress with the rail clear.");
                clear = Feed(empty);
                scene.ObserveHoldButtons(context, [], now, context.ClearedButtons(clear));
                Check(scene.CurrentHoldProgress.Count == 0 && SameRun(paused, scene.StrykersRunState),
                    $"Restoring intact {id} lettering failed to cancel its hold or changed the paused run.");
                evidence.Add(new { id, caption, actualBrokenLetters = true, intactCancels = true, railUnchanged = true });
            }
            return evidence;
        }

        async Task<object> CheckFooterLayout()
        {
            using var current = MakeScene(.4, out _);
            await current.EnsureStrykersRunResourcesAsync(device);
            using var image = new CanvasRenderTarget(device, 1000, 900, 96);
            var evidence = new List<object>();
            foreach (double aspect in new[] { .4, 9d / 16, .79, 1, 16d / 9 })
            {
                current.SetDisplayAspect(aspect);
                now += TimeSpan.FromMilliseconds(20);
                Draw(current, image);
                var bounds = current.StrykersRunFieldBounds();
                var buttons = Session(current).Buttons;
                Check(buttons.Select(button => button.Label).SequenceEqual(["EXIT", "PLAY"]) &&
                    buttons.All(button => button is { Enabled: true, Hold: BoardButtonHold.Once }),
                    $"The {aspect:0.###} board lost the shared ready EXIT/PLAY controls.");
                for (int index = 0; index < buttons.Count; index++)
                {
                    var button = buttons[index].Bounds;
                    double expectedX = bounds.X + bounds.Width * (index == 0 ? .10 : .58);
                    Check(Math.Abs(button.X - expectedX) < .000001 &&
                        Math.Abs(button.Width - bounds.Width * .32) < .000001 &&
                        Math.Abs(button.Height - bounds.Height * .085) < .000001 &&
                        Math.Abs(button.Y + button.Height / 2 - (bounds.Y + bounds.Height * .95)) < .000001 &&
                        button.X >= bounds.X && button.X + button.Width <= bounds.Right &&
                        button.Y >= bounds.Y + bounds.Height * .90 && button.Y + button.Height <= bounds.Bottom,
                        $"The {aspect:0.###} board's {buttons[index].Label} is not centered inside its actual black footer.");
                }
                var context = current.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No footer caption reference.");
                Check(context.EnabledHoldIds.SetEquals(["strykers-run-exit", "strykers-run-play"]) &&
                    context.ExpectedScene.BoardTriggerRegions?.Count == 2,
                    $"The {aspect:0.###} footer did not retain both generated caption references.");
                for (int index = 0; index < buttons.Count; index++)
                {
                    var caption = context.ExpectedScene.BoardTriggerRegions![index];
                    var button = buttons[index].Bounds;
                    Check(caption.X > button.X && caption.Y > button.Y &&
                        caption.X + caption.Width < button.X + button.Width &&
                        caption.Y + caption.Height < button.Y + button.Height &&
                        button.Contains(caption.X + caption.Width / 2, caption.Y + caption.Height / 2),
                        $"The {aspect:0.###} footer caption escaped its rendered button and hit bounds.");
                }
                var intact = new HandAcquisitionPresenceTracker().Update(1000, 900, 4000, image.GetPixelBytes(),
                    context.SearchPolygon, context.ExpectedScene, now, now);
                Check(context.ClearedButtons(intact).Order().SequenceEqual(context.ButtonIds.Order()) &&
                    context.HeldButtons(intact).Count == 0,
                    $"The {aspect:0.###} footer's actual rendered lettering disagrees with its camera reference.");
                using var preview = new CanvasRenderTarget(device, (float)(900 * aspect), 900, 96);
                DrawPreview(current, preview);
                await Save(preview, "footer-layout-" + aspect.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                evidence.Add(new { aspect, field = bounds, controls = buttons.Select(button => button.Bounds).ToArray(),
                    captionsReadable = true, footerCentered = true });
            }
            return evidence;
        }

        async Task<object> CheckControlExposure()
        {
            using var current = MakeScene(.78, out _);
            await current.EnsureStrykersRunResourcesAsync(device);
            using var image = new CanvasRenderTarget(device, 1000, 900, 96);
            Draw(current, image);
            var context = current.GetHoldButtonContext(now)!;
            var originalReference = context.ExpectedScene;
            long originalRailEpoch = current.StrykersRunInputRevision;
            Check(current.HoldControlBrightness == 1, "Field controls did not start at the original Football palette.");
            byte[] clipped = image.GetPixelBytes();
            for (int pixel = 0; pixel < clipped.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++)
                    clipped[pixel + channel] = (byte)Math.Min(255, clipped[pixel + channel] * 6 + 180);
            now += TimeSpan.FromMilliseconds(100);
            var washedOut = new HandAcquisitionPresenceTracker().Update(1000, 900, 4000, clipped,
                context.SearchPolygon, context.ExpectedScene, now, now);
            Check(washedOut.TextPatterns?.Any(pattern => pattern.CaptionClipped && !pattern.LabelIntact) == true,
                "The exposure fixture did not wash out the actual generated captions.");
            var safeUnreadable = new HandAcquisitionPresenceResult([], true, null, 0, "fixture",
                [new(1, false, .74, .02, .25, CaptionClipped: true)]);
            Check(!current.ObserveHoldControlExposure(context, safeUnreadable, now.AddSeconds(1)) &&
                !current.ObserveHoldControlExposure(context, safeUnreadable, now.AddSeconds(-1)),
                "Future or stale camera evidence dimmed the controls.");
            var readable = safeUnreadable with { TextPatterns = [safeUnreadable.TextPatterns![0] with { LabelIntact = true }] };
            Check(!current.ObserveHoldControlExposure(context, readable, now),
                "Readable clipped lettering unnecessarily changed the Football palette.");
            now += TimeSpan.FromMilliseconds(100);
            var covered = safeUnreadable with { TextPatterns = [safeUnreadable.TextPatterns![0] with
                { ShapeCorrupted = true, ControlCoverage = .01, ControlTriggerCoverage = .3, ConfirmationFrames = 0 }] };
            Check(!current.ObserveHoldControlExposure(context, covered, now),
                "Qualified hand coverage changed the caption's optical reference.");
            now += TimeSpan.FromMilliseconds(100);
            Check(current.ObserveHoldControlExposure(context, washedOut, now) &&
                Math.Abs(current.HoldControlBrightness - .85) < .000001,
                "Unreadable overexposed rendered captions did not take one bounded brightness step.");
            Check(current.GetHoldButtonContext(now) is null &&
                current.ObserveHoldButtons(context, ["strykers-run-play"], now, ["strykers-run-play"]).Count == 0 &&
                current.CurrentHoldProgress.Count == 0,
                "The app's own brightness transition armed or held PLAY.");
            Draw(current, image);
            Check(current.GetStrykersRunRailGeometry() is null && current.StrykersRunInputRevision == originalRailEpoch &&
                RailDotCenter(current, BoardPixels(current)) is null,
                "Caption exposure protection revealed the hidden rail or changed its input epoch.");
            now += TimeSpan.FromMilliseconds(899);
            Check(current.GetHoldButtonContext(now) is null, "The caption reference returned before the camera settled.");
            now += TimeSpan.FromMilliseconds(2);
            var dimmed = current.GetHoldButtonContext(now)!;
            Check(dimmed.Revision != context.Revision && !ReferenceEquals(dimmed.ExpectedScene, originalReference) &&
                dimmed.ExpectedScene.BoardTriggerRegions!.SequenceEqual(originalReference.BoardTriggerRegions!),
                "Dimming did not refresh the exact reference while retaining the original letter geometry.");
            int unchangedInk = 0, changedPlate = 0;
            for (int pixel = 0; pixel < originalReference.Bgra.Length; pixel += 4)
            {
                if (originalReference.Bgra[pixel] == 36 && originalReference.Bgra[pixel + 1] == 46 && originalReference.Bgra[pixel + 2] == 21)
                {
                    Check(dimmed.ExpectedScene.Bgra.AsSpan(pixel, 4).SequenceEqual(originalReference.Bgra.AsSpan(pixel, 4)),
                        "Exposure protection changed the caption ink color.");
                    unchangedInk++;
                }
                if (originalReference.Bgra[pixel] == 211 && originalReference.Bgra[pixel + 1] == 229 && originalReference.Bgra[pixel + 2] == 221 &&
                    dimmed.ExpectedScene.Bgra[pixel] == 179 && dimmed.ExpectedScene.Bgra[pixel + 1] == 195 && dimmed.ExpectedScene.Bgra[pixel + 2] == 188)
                    changedPlate++;
            }
            Check(unchangedInk > 50 && changedPlate > 1000, "Dimming did not preserve ink and change only the plate brightness.");
            Check(!current.ObserveHoldControlExposure(context, safeUnreadable, now), "A superseded reference changed brightness again.");
            current.ObserveHoldButtons(dimmed, ["strykers-run-play"], now, []);
            Check(current.CurrentHoldProgress.Count == 0, "The regenerated caption bypassed fresh-clear arming.");
            // The rejected held frame still consumes its camera timestamp.
            // A later clear observation must come from a genuinely newer frame.
            now += TimeSpan.FromMilliseconds(100);
            var intact = new HandAcquisitionPresenceTracker().Update(1000, 900, 4000, image.GetPixelBytes(),
                dimmed.SearchPolygon, dimmed.ExpectedScene, now, now);
            Check(dimmed.ClearedButtons(intact).Contains("strykers-run-play"), "The dimmed actual lettering was not recognized clear.");
            current.ObserveHoldButtons(dimmed, [], now, dimmed.ClearedButtons(intact));
            now += TimeSpan.FromMilliseconds(100);
            current.ObserveHoldButtons(dimmed, ["strykers-run-play"], now, []);
            now += TimeSpan.FromMilliseconds(100);
            current.ObserveHoldButtons(dimmed, ["strykers-run-play"], now, []);
            now += TimeSpan.FromMilliseconds(1);
            Check(current.CurrentHoldProgress.Count == 1, "A fresh clear caption did not arm the fixture's partial hold.");
            Check(!current.ObserveHoldControlExposure(dimmed, safeUnreadable, now),
                "Brightness adaptation accepted evidence during a partial hold.");
            Check(current.CurrentHoldProgress.Count == 1 && Math.Abs(current.HoldControlBrightness - .85) < .000001,
                "Brightness adaptation changed the existing partial hold or its plate.");
            current.ObserveHoldButtons(dimmed, [], now, ["strykers-run-play"]);
            for (int step = 0; step < 8; step++)
            {
                now += TimeSpan.FromMilliseconds(901);
                Draw(current, image);
                var fresh = current.GetHoldButtonContext(now)!;
                current.ObserveHoldControlExposure(fresh, safeUnreadable, now);
            }
            Check(current.HoldControlBrightness == .5, "Repeated clipping crossed the shared brightness floor.");
            current.ClearHandTips();
            Draw(current, image);
            Check(current.HoldControlBrightness == 1, "Camera input reset retained learned plate exposure.");
            current.ShowFootball();
            await current.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
            Draw(current, image);
            var football = current.GetHoldButtonContext(now)!;
            Check(current.HoldControlBrightness == 1 && Enumerable.Range(0, football.ExpectedScene.Bgra.Length / 4).Any(index =>
                football.ExpectedScene.Bgra[index * 4] == 211 && football.ExpectedScene.Bgra[index * 4 + 1] == 229 &&
                football.ExpectedScene.Bgra[index * 4 + 2] == 221 && football.ExpectedScene.Bgra[index * 4 + 3] == 255),
                "Football did not retain its original default plate brightness.");
            now += TimeSpan.FromMilliseconds(100);
            Check(current.ObserveHoldControlExposure(football, safeUnreadable, now) &&
                Math.Abs(current.HoldControlBrightness - .85) < .000001,
                "Football did not share the same bounded exposure protection.");
            // Every long-press board shares it, dimming only its light caption element:
            // Blackjack and Slots' pale plates, Roulette's cream lettering on a dark plate.
            foreach (var (show, screen) in new (Action, BoardScreen)[]
                { (current.ShowBlackjack, BoardScreen.Blackjack), (current.ShowSlots, BoardScreen.Slots),
                  (current.ShowRoulette, BoardScreen.Roulette) })
            {
                show();
                await current.EnsureBoardArtworkResourcesAsync(device, screen);
                Draw(current, image);
                now += TimeSpan.FromMilliseconds(100);
                var original = current.GetHoldButtonContext(now)!;
                int region = Enumerable.Range(0, original.ButtonIds.Count)
                    .First(index => original.EnabledHoldIds.Contains(original.ButtonIds[index]));
                var unreadable = safeUnreadable with { TextPatterns = [safeUnreadable.TextPatterns![0] with { ControlRegion = region }] };
                Check(current.HoldControlBrightness == 1 && current.ObserveHoldControlExposure(original, unreadable, now) &&
                    Math.Abs(current.HoldControlBrightness - .85) < .000001,
                    screen + " did not share the bounded caption exposure protection.");
                now += TimeSpan.FromMilliseconds(901);
                Draw(current, image);
                var adjusted = current.GetHoldButtonContext(now)!;
                var holds = Session(current).Buttons.Where(button => button.IsHold).Select(button => button.Bounds).ToArray();
                int width = adjusted.ExpectedScene.Width, height = adjusted.ExpectedScene.Height, inside = 0, outside = 0;
                for (int pixel = 0; pixel < adjusted.ExpectedScene.Bgra.Length; pixel += 4)
                {
                    if (adjusted.ExpectedScene.Bgra.AsSpan(pixel, 4).SequenceEqual(original.ExpectedScene.Bgra.AsSpan(pixel, 4))) continue;
                    // Allow each long-press control's antialiased edge pixels.
                    double u = (pixel / 4 % width + .5) / width, v = (pixel / 4 / width + .5) / height;
                    if (holds.Any(control => u >= control.X - 3d / width && u <= control.X + control.Width + 3d / width &&
                        v >= control.Y - 3d / height && v <= control.Y + control.Height + 3d / height)) inside++;
                    else outside++;
                }
                Check(inside > 100 && outside == 0, screen + " exposure protection changed pixels outside its long-press controls: " +
                    $"{inside} inside and {outside} outside.");
            }
            current.ShowBoardMenu(); current.ShowStrykersRun(); Draw(current, image);
            Check(current.HoldControlBrightness == 1, "A new board visit inherited another board's dimmed controls.");
            return new { actualOverexposure = true, sharedPlateOnlyDimming = true, floor = .5,
                staleAndCoveredEvidenceIgnored = true, delayedCameraBarrier = true, freshClearRequired = true,
                originalFootballPalette = true, railUnchanged = true, resetOnCameraAndNavigation = true,
                sharedWithBlackjackSlotsAndRoulette = true };
        }

        void MoveTo(float x)
        {
            for (int frame = 0; frame < 30; frame++)
            {
                now += TimeSpan.FromMilliseconds(16);
                Check(scene.SetStrykersRunRailPosition(x, now, rail.Revision), "Fresh live rail input was rejected.");
                Draw(scene, camera);
            }
        }

        void CheckReturningControls(bool victory)
        {
            var game = (StrykersRunGame)typeof(BoardSession).GetField("_strykersRun", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Session(scene))!;
            Put(game, "_phase", victory ? StrykersRunPhase.Victory : StrykersRunPhase.GameOver);
            Put(game, "_soldiers", victory ? 7 : 0);
            Put(game, "_phaseSeconds", 0f); Put(game, "_lastAdvance", now); Put(game, "_snapshot", null);
            Draw(scene, camera);
            Check(!scene.HandDetectionRequired, "The terminal presentation enabled unnecessary caption processing.");
            var endedAt = now;
            for (int frame = 1; frame <= 320; frame++)
            {
                now = endedAt.AddMilliseconds(frame * 10);
                Session(scene).TickStrykersRun(now);
                if (frame == 260) Check(Session(scene).Buttons.Count == 0 && !scene.HandDetectionRequired,
                    "Controls or caption processing returned before the ending settled.");
                if (frame == 280) Check(Session(scene).StrykersRunControlsAnimating &&
                    Session(scene).Buttons.All(button => !button.Enabled) && scene.GetHoldButtonContext(now) is null &&
                    !scene.HandDetectionRequired,
                    "The returning fade accepted caption references.");
            }
            Draw(scene, camera);
            var returned = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No returned controls.");
            Check(scene.HandDetectionRequired && returned.Revision != hold.Revision &&
                returned.EnabledHoldIds.SetEquals(["strykers-run-exit", "strykers-run-play"]),
                "Returned controls reused their pre-run caption context.");
            for (int frame = 0; frame <= 11; frame++)
            {
                now += TimeSpan.FromMilliseconds(100);
                Check(scene.ObserveHoldButtons(returned, ["strykers-run-play"], now, []).Count == 0,
                    "A returning covered caption restarted without first seeing intact letters.");
            }
            now += TimeSpan.FromMilliseconds(10);
            scene.ObserveHoldButtons(returned, [], now, ["strykers-run-play"]);
            Check(scene.ObserveHoldButtons(hold, ["strykers-run-play"], now, []).Count == 0,
                "A pre-run hold reference remained valid after the ending.");
            now += TimeSpan.FromMilliseconds(100);
            Check(scene.ObserveHoldButtons(returned, ["strykers-run-play"], now, []).Count == 0,
                "A freshly cleared returned PLAY activated immediately.");
            now += TimeSpan.FromMilliseconds(100);
            Check(scene.ObserveHoldButtons(returned, ["strykers-run-play"], now, []).Count == 0 &&
                scene.CurrentHoldProgress.Count == 1, "A freshly cleared returned PLAY could not begin another hold.");
            now += TimeSpan.FromMilliseconds(10);
            scene.ObserveHoldButtons(returned, [], now, ["strykers-run-play"]);
        }

        object MeasureRendering()
        {
            const int width = 3840, height = 2160, samples = 5, warm = 2;
            using var output = new CanvasRenderTarget(device, width, height, 96);
            var bounds = new Rect((width - height * 9d / 16) / 2, 0, height * 9d / 16, height);
            StrykersRunSnapshot[] states = [clean, busy];
            var cpu = new double[2, samples]; var readback = new double[2, samples];
            for (int frame = -warm; frame < samples; frame++)
            for (int mode = 0; mode < 2; mode++)
            {
                long start = Stopwatch.GetTimestamp();
                using (var draw = output.CreateDrawingSession())
                { draw.Clear(Windows.UI.Color.FromArgb(255, 7, 10, 17)); renderer.Draw(draw, bounds, states[mode], 12 + frame * .016); }
                double submission = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                start = Stopwatch.GetTimestamp();
                byte[] pixels = output.GetPixelBytes();
                double read = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Check(pixels.Length == width * height * 4, "Incomplete native 4K readback.");
                if (frame >= 0) { cpu[mode, frame] = submission; readback[mode, frame] = read; }
            }
            return new { width, height, samplesPerScene = samples, warmFramesPerScene = warm,
                measurement = "Offscreen Win2D CPU command submission and synchronous full-target GPU wait/readback plus CPU allocation/copy. Excludes presentation, camera work and PNG encoding; not displayed frame rate.",
                oneHero = Summary(0), busySquad = Summary(1) };
            object Summary(int mode)
            {
                var submissions = Enumerable.Range(0, samples).Select(index => cpu[mode, index]).Order().ToArray();
                var reads = Enumerable.Range(0, samples).Select(index => readback[mode, index]).Order().ToArray();
                var totals = Enumerable.Range(0, samples).Select(index => cpu[mode, index] + readback[mode, index]).Order().ToArray();
                return new { states[mode].SoldierCount, enemies = states[mode].Enemies.Count, bullets = states[mode].Bullets.Count,
                    submissionMedianMilliseconds = Math.Round(submissions[samples / 2], 3),
                    readbackMedianMilliseconds = Math.Round(reads[samples / 2], 3),
                    combinedMedianMilliseconds = Math.Round(totals[samples / 2], 3), combinedMaximumMilliseconds = Math.Round(totals[^1], 3) };
            }
        }

        SceneCompositor MakeScene(double aspect, out double padding)
        {
            var result = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now,
                footballClock: () => now, strykersRunClock: () => now);
            // The board's +90-degree player-edge offset yields an upright fixture.
            result.SetDisplayAspect(aspect); result.SetBoardFacingDegrees(-90); result.SetBoardSetup(true);
            Configure(result, out padding); result.SetBoardSetup(false); result.ShowStrykersRun();
            return result;
        }
        void CheckPlayerEdgeOrientation()
        {
            using var current = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now, strykersRunClock: () => now);
            current.SetDisplayAspect(16d / 9);
            current.SetBoardFacingDegrees(0);
            current.SetBoardSetup(true); Configure(current, out _); current.SetBoardSetup(false);
            var menuMap = SurfaceMap(current);
            long before = current.StrykersRunInputRevision;
            current.ShowStrykersRun();
            Check(current.GetBoardFacingDegrees() == 0 &&
                Math.Abs(BoardOrientation.Heading(current.GetDetectedBoardCorners()!, 16d / 9) - 90) < .00001 &&
                Math.Abs(current.StrykersRunPreviewAspect - 9d / 16) < .00001 && current.StrykersRunInputRevision > before,
                "Stryker's Run did not face the first player's short edge while preserving the saved base facing.");
            Point2[] samples = [new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(.5, .97)];
            var playerMap = SurfaceMap(current);
            foreach (var point in samples)
                Check(Near(playerMap.Transform(point), menuMap.Transform(new(1 - point.Y, point.X))),
                    "The Stryker player-edge map did not rotate the complete calibrated surface together.");
            before = current.StrykersRunInputRevision;
            current.SetBoardSetup(true); Configure(current, out _); current.SetBoardSetup(false);
            Check(current.StrykersRunInputRevision > before && current.GetBoardFacingDegrees() == 0 &&
                samples.All(point => Near(playerMap.Transform(point), SurfaceMap(current).Transform(point))),
                "Calibration lost the Stryker facing offset or reused its old camera epoch.");
            before = current.StrykersRunInputRevision;
            current.ShowBoardMenu();
            Check(current.GetBoardFacingDegrees() == 0 && current.StrykersRunInputRevision > before &&
                samples.All(point => Near(menuMap.Transform(point), SurfaceMap(current).Transform(point))),
                "Leaving Stryker's Run did not restore the menu's original calibrated mapping and invalidate rail input.");

            static Homography SurfaceMap(SceneCompositor target) => (Homography)typeof(SceneCompositor)
                .GetField("_boardSurfaceMap", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
            static bool Near(Point2 a, Point2 b) => Math.Abs(a.X - b.X) < .000001 && Math.Abs(a.Y - b.Y) < .000001;
        }
        static void Configure(SceneCompositor current, out double padding) => padding = current.SetDetectedBoardGrid(
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], Homography.FromFourPoints(
                [new(0, 0), new(1000, 0), new(1000, 900), new(0, 900)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        void Draw(SceneCompositor current, CanvasRenderTarget target)
        { using var draw = target.CreateDrawingSession(); current.Draw(draw, (float)target.Size.Width, (float)target.Size.Height, false, false); renderedFrames++; }
        void DrawPreview(SceneCompositor current, CanvasRenderTarget target)
        { using var draw = target.CreateDrawingSession(); current.DrawStrykersRunPreview(draw, (float)target.Size.Width, (float)target.Size.Height); renderedFrames++; }
        void RenderRaw(StrykersRunSnapshot state)
        { using var draw = portrait.CreateDrawingSession(); draw.Clear(Windows.UI.Color.FromArgb(255, 7, 10, 17)); renderer.Draw(draw, new(0, 0, 900, 1600), state, 12); renderedFrames++; }
        async Task Save(CanvasRenderTarget target, string name)
        { string path = Path.Combine(directory, name + ".png"); await target.SaveAsync(path, CanvasBitmapFileFormat.Png); images.Add(path); }
        static BoardSession Session(SceneCompositor current) => (BoardSession)typeof(SceneCompositor).GetField("_boardSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
        static void Put(object target, string field, object? value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        static byte[] BoardPixels(SceneCompositor current) => ((CanvasRenderTarget)typeof(SceneCompositor).GetField("_boardApplicationTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!).GetPixelBytes();
        static PixelPoint CameraPoint(double u, double v, double padding, bool rotated)
        { if (rotated) (u, v) = (1 - v, u); return new(1000 * (padding / 2 + u * (1 - padding)), 900 * (padding / 2 + v * (1 - padding))); }
        static void CheckRailGeometry(SceneCompositor current, PatternRailGeometry geometry, double padding, bool rotated)
        {
            var r = current.StrykersRunRailBounds();
            var expected = new[] { CameraPoint(r.X, r.Y, padding, rotated), CameraPoint(r.Right, r.Y, padding, rotated),
                CameraPoint(r.Right, r.Bottom, padding, rotated), CameraPoint(r.X, r.Bottom, padding, rotated) };
            Check(geometry.CameraCorners.Count == 4 && expected.Zip(geometry.CameraCorners).All(pair =>
                Math.Abs(pair.First.X - pair.Second.X) < .01 && Math.Abs(pair.First.Y - pair.Second.Y) < .01),
                "Calibrated rail corners did not preserve their normalized order through rotation.");
        }
        static bool RailEqual(byte[] a, byte[] b, Rect rail) => Changed(a, b, 1000,
            new(rail.X * 1000 + 3, rail.Y * 1000 + 3, rail.Width * 1000 - 6, rail.Height * 1000 - 6)) == 0;
        static double ControlPlateLight(SceneCompositor current, byte[] pixels)
        {
            // The upper centre of the current EXIT plate remains blank as its
            // fixed captions fade and the rail slides into the footer below it.
            var bounds = Session(current).Buttons.Single(button => button.Id == "strykers-run-exit").Bounds;
            int x = (int)((bounds.X + bounds.Width * .5) * 1000);
            int y = (int)((bounds.Y + bounds.Height * .2) * 1000);
            int pixel = (y * 1000 + x) * 4;
            return (pixels[pixel] + pixels[pixel + 1] + pixels[pixel + 2]) / 3d;
        }
        static double? RailDotCenter(SceneCompositor current, byte[] pixels)
        {
            var rail = current.StrykersRunRailBounds();
            var field = current.StrykersRunFieldBounds();
            // A central column lies between EXIT and PLAY, including throughout their fades.
            int x = (int)((rail.X + rail.Width * PatternRailGeometry.ColumnCenter(24) /
                PatternRailGeometry.ReferenceWidth) * 1000);
            int count = 0, sum = 0;
            for (int y = (int)((field.Y + field.Height * .90) * 1000); y < 1000; y++)
            {
                int pixel = (y * 1000 + x) * 4;
                if (pixels[pixel] < 240 || pixels[pixel + 1] < 240 || pixels[pixel + 2] < 240) continue;
                count++; sum += y;
            }
            return count == 0 ? null : sum / (double)count;
        }
        static void CheckRailDotPixels(SceneCompositor current, byte[] pixels)
        {
            var rail = current.StrykersRunRailBounds();
            var field = current.StrykersRunFieldBounds();
            double? center = RailDotCenter(current, pixels);
            Check(center.HasValue && Math.Abs(center.Value - (field.Y + field.Height * .95) * 1000) < 1.5,
                "The dot pair was not vertically centered in the full black footer.");
            for (int column = 0; column < PatternRailGeometry.Columns; column++)
            {
                int y1 = PatternRailGeometry.DotY1ForColumn(column), y2 = PatternRailGeometry.DotY2ForColumn(column);
                Check(y1 + y2 == PatternRailGeometry.ReferenceHeight,
                    "A regular or phase-witness colon lost its symmetric optical geometry.");
                int x = (int)((rail.X + rail.Width * PatternRailGeometry.ColumnCenter(column) /
                    PatternRailGeometry.ReferenceWidth) * 1000);
                foreach (int dotY in new[] { y1, y2 })
                {
                    int y = (int)((rail.Y + rail.Height * dotY / PatternRailGeometry.ReferenceHeight) * 1000);
                    int pixel = (y * 1000 + x) * 4;
                    Check(pixels[pixel] >= 240 && pixels[pixel + 1] >= 240 && pixels[pixel + 2] >= 240,
                        "A rendered rail mark disagreed with the detector's canonical geometry.");
                }
            }
        }
        static int Changed(byte[] a, byte[] b, int width, Rect region)
        {
            int changes = 0;
            for (int y = (int)region.Y; y < (int)region.Bottom; y++)
            for (int x = (int)region.X; x < (int)region.Right; x++)
            { int i = (y * width + x) * 4; if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) changes++; }
            return changes;
        }
        static bool SameRun(StrykersRunSnapshot a, StrykersRunSnapshot b) => a.SquadX == b.SquadX && a.RunSeconds == b.RunSeconds &&
            a.Distance == b.Distance && a.Score == b.Score && a.SoldierCount == b.SoldierCount && a.Enemies.SequenceEqual(b.Enemies) &&
            a.Gates.SequenceEqual(b.Gates) && a.Bullets.SequenceEqual(b.Bullets) && a.Effects.SequenceEqual(b.Effects) &&
            a.Ally == b.Ally && a.Objectives.SequenceEqual(b.Objectives) && a.Level == b.Level &&
            a.LevelSeconds == b.LevelSeconds && a.Stage == b.Stage && a.StageSeconds == b.StageSeconds &&
            a.Boss == b.Boss && a.Hazards.SequenceEqual(b.Hazards);
        static void CheckPerspective()
        {
            Check(StrykersRunRenderer.DepthScale(.10f) < StrykersRunRenderer.DepthScale(.6f) &&
                Math.Abs(StrykersRunRenderer.DepthScale(StrykersRunGame.SquadY) - 1) < .000001 &&
                Vector2.Distance(StrykersRunRenderer.Project(.08f, StrykersRunGame.SquadY), new(.08f, StrykersRunGame.SquadY)) < .000001,
                "Road depth or hero-to-rail perspective alignment is incorrect.");
        }
        static void CheckMenu()
        {
            var menu = new BoardSession();
            var time = MonotonicClock.UtcNow;
            Check(menu.GetMenuCards(time).Count == 10 && menu.Buttons.Any(button => button.Id == "strykers-run") &&
                menu.Buttons.All(button => button.Id != "blackjack"), "First-page menu placement is incorrect.");
            Check(menu.ActivateButton("menu-scroll-down", time) && menu.TickMenu(time.AddMilliseconds(650)) &&
                menu.Buttons.Any(button => button.Id == "blackjack"), "Blackjack was not retained on the second page.");
        }
        static StrykersRunSnapshot DenseState(StrykersRunSnapshot state) => state with
        {
            SoldierCount = 64,
            Ally = new(.5f, .66f, 17, 24, .3f, .32f),
            Objectives = [new(900, .28f, .36f, StrykersRunObjectiveKind.Rescue, 5, 8),
                new(901, .75f, .57f, StrykersRunObjectiveKind.Reinforcements, 7, 12)],
            Enemies = Enumerable.Range(0, 60).Select(i => new StrykersRunEnemySnapshot(i + 1,
                .12f + i % 10 * .083f, .12f + i / 10 * .11f,
                i % 19 == 0 ? StrykersRunEnemyKind.Boss : i % 4 == 0 ? StrykersRunEnemyKind.Brute : StrykersRunEnemyKind.Runner,
                i % 19 == 0 ? 26 : 2, i % 19 == 0 ? 70 : 4, i % 4 * .25f)).ToArray(),
            Gates = [new(501, .28f, .47f, .40f, StrykersRunGateKind.Add, 8), new(502, .72f, .47f, .40f, StrykersRunGateKind.Subtract, 5),
                new(503, .28f, .18f, .40f, StrykersRunGateKind.Multiply, 2), new(504, .72f, .18f, .40f, StrykersRunGateKind.Subtract, 8)],
            Bullets = Enumerable.Range(0, 120).Select(i => new StrykersRunBulletSnapshot(600 + i,
                .37f + i % 12 * .024f, .15f + i / 12 * .057f)).ToArray(),
            Effects = Enumerable.Range(0, 48).Select(i => new StrykersRunEffectSnapshot(
                i % 2 == 0 ? StrykersRunEffectKind.Hit : StrykersRunEffectKind.Defeat,
                .14f + i % 8 * .10f, .22f + i / 8 * .075f, .12f + i % 4 * .06f, i * 31)).ToArray()
        };
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
#endif
