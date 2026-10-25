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
    // Off-screen GPU targets and synthetic input only. No MainWindow, microphone,
    // camera, projection window or control pipe is constructed by this check.
    private async Task<object> VerifyDuckHuntAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntGalleryVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int assertions = 0;
        var images = new List<string>();
        var results = new List<object>();
        foreach (var dimensions in new[]
        {
            (Name: "landscape", Width: 1920, Height: 1080),
            (Name: "square", Width: 1000, Height: 1000),
            (Name: "portrait", Width: 1080, Height: 1920),
            (Name: "4k", Width: 3840, Height: 2160)
        })
        {
            var now = MonotonicClock.UtcNow;
            double aspect = dimensions.Width / (double)dimensions.Height;
            using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), dimensions.Width, dimensions.Height, 96);
            var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
            scene.SetDisplayAspect(aspect);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(dimensions.Width, 0), new(dimensions.Width, dimensions.Height), new(0, dimensions.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.ShowDuckHuntTargetPractice();
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.SetDuckHuntAudioStatus(true, "Isolated gallery microphone");
            Draw();
            Check(scene.DuckHuntArtworkReady && scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, Paused: false },
                "The saloon is not ready with its closed flap and PLAY target.");
            Check(!scene.ActivateDuckHuntAt(.5, .46) && board.Buttons.All(button => !button.Bounds.Contains(.5, .46)),
                "A hand/laptop button can activate gallery PLAY.");
            CheckClearOptics("closed saloon");
            CheckLaser(new(.5, .46), "PLAY");
            await Save("closed");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(new(.5, .46), now), "A shot on PLAY did not open the gallery.");
            Draw();
            await Save("opening-zero");
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.Opening, FlapAngleDegrees: 0,
                UpperPhase: DuckHuntGalleryUpperPhase.Raising, UpperOpenProgress: 0 },
                "One PLAY shot did not begin both the flap and upper curtain together.");
            now += TimeSpan.FromMilliseconds(350);
            Draw();
            await Save("opening-front");
            now += TimeSpan.FromMilliseconds(450);
            Draw();
            await Save("opening-reverse");
            now += TimeSpan.FromMilliseconds(250);
            byte[] revealed = Draw();
            var revealedState = scene.GalleryState;
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.RevealWait, FlapAngleDegrees: 95, ConveyorPosition: 0 },
                "The panel did not reach 95 degrees before the reveal pause.");
            await Save("revealed");
            now += TimeSpan.FromMilliseconds(700);
            byte[] revealPaused = Draw();
            if (scene.GalleryState is not { Phase: DuckHuntGalleryPhase.RevealWait, FlapAngleDegrees: 95, ConveyorPosition: 0 } ||
                !LowerEqual(revealed, revealPaused))
            {
                await Save("reveal-pause-failure");
                var pausedState = scene.GalleryState;
                byte[] repeated = Draw();
                await Save("reveal-pause-repeat");
                string diagnosticPath = Path.Combine(directory, dimensions.Name + "-reveal-pause-difference.json");
                await File.WriteAllTextAsync(diagnosticPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    dimensions.Name, dimensions.Width, dimensions.Height, Aspect = aspect,
                    Before = revealedState, After = pausedState, Repeated = scene.GalleryState,
                    Difference = RegionDifference(revealed, revealPaused, LowerRegion()),
                    SameTimeDifference = RegionDifference(revealPaused, repeated, LowerRegion())
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Check(false, $"The conveyor moved during the one-second reveal pause ({dimensions.Name}); diagnostics: {diagnosticPath}");
            }
            else Check(true, "The conveyor moved during the one-second reveal pause.");
            now += TimeSpan.FromMilliseconds(650);
            Draw();
            await Save("advancing");
            now += TimeSpan.FromSeconds(1.5);
            byte[] waiting = Draw();
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.WaitingForHits, HitTargets: 0, ConveyorPosition: 1 },
                "Five papers did not stop at the firing position.");
            CheckClearOptics("five clean papers");
            await Save("waiting");
            Check(scene.GalleryState is { UpperPhase: DuckHuntGalleryUpperPhase.WaitingForHits, HitSkulls: 0, UpperOpenProgress: 1 },
                "The upper curtain did not reveal five independently hittable skulls.");
            for (int index = 0; index < DuckHuntGalleryGame.SkullCount; index++)
            {
                var skull = DuckHuntGalleryLayout.SkullBounds(index, aspect);
                var forehead = new BoardAim(skull.X + skull.Width * .5, skull.Y + skull.Height * .28);
                Check(PixelBrightness(forehead) > 70, "A revealed crystal skull is missing or concealed by its curtain.");
                if (index == 0) CheckLaser(forehead, "crystal skull forehead");
            }
            var targetBeforeFlicker = TargetTexture(0).GetPixelBytes();
            foreach (double advance in new[] { .071, .113, .217, .339, .531, .823, 1.039, 1.377 })
            {
                now += TimeSpan.FromSeconds(advance);
                Draw();
                CheckClearOptics("crystal reflections at varied flicker phases");
            }
            Check(targetBeforeFlicker.AsSpan().SequenceEqual(TargetTexture(0).GetPixelBytes()),
                "Skull-light animation rebuilt or changed an unmarked paper texture.");
            await Save("skulls-lit");
            var firstSkull = DuckHuntGalleryLayout.SkullBounds(0, aspect);
            var firstSkullAim = new BoardAim(firstSkull.X + firstSkull.Width * .5, firstSkull.Y + firstSkull.Height * .4);
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(firstSkullAim, now) && scene.GalleryState is { HitSkulls: 1, HitTargets: 0 },
                "A skull could not be shot before the lower papers.");
            now += TimeSpan.FromMilliseconds(45);
            byte[] shattering = Draw();
            await Save("skull-shatter-start");
            CheckClearOptics("initial crystal shatter");
            now += TimeSpan.FromMilliseconds(250);
            Draw();
            await Save("skull-shatter-flying");
            CheckClearOptics("flying crystal fragments");
            now += TimeSpan.FromSeconds(1);
            byte[] crumbs = Draw();
            var firstSkullRegion = new BoardRect(firstSkull.X - firstSkull.Width * .6, firstSkull.Y,
                firstSkull.Width * 2.2, firstSkull.Height * 1.05);
            Check(!RegionEqual(shattering, crumbs, firstSkullRegion), "The crystal fragments did not fall onto the shelf.");
            CheckClearOptics("settled crystal crumbs");
            await Save("skull-crumbs-settled");
            now += TimeSpan.FromSeconds(1);
            Check(RegionEqual(crumbs, Draw(), firstSkullRegion), "Settled skull crumbs moved or disappeared from the shelf.");
            for (int index = 0; index < DuckHuntGalleryGame.TargetCount; index++)
            {
                var paper = DuckHuntGalleryLayout.TargetQuad(index, scene.GalleryState, aspect);
                Check(paper.Bounds.X >= 0 && paper.Bounds.Y >= 0 &&
                    paper.Bounds.X + paper.Bounds.Width <= 1 && paper.Bounds.Y + paper.Bounds.Height <= 1,
                    "A paper is cropped off the calibrated board.");
                var clearPaper = PaperPoint(.18, .22);
                Check(PixelLight(paper.Map(clearPaper.U, clearPaper.V)),
                    "A clean round paper is hidden behind the panel or incorrectly projected.");
                if (index == 0)
                {
                    CheckTargetTransparency();
                    var center = PaperPoint(0, 0);
                    // A subpixel ink crossing can straddle two raster pixels.
                    Check(PixelBrightness(paper.Map(center.U, center.V), radius: 1) + 35 <
                        PixelBrightness(paper.Map(clearPaper.U, clearPaper.V)),
                        "The round target has no contrasting centre cross.");
                    CheckLaser(paper.Map(clearPaper.U, clearPaper.V), "clear round paper");
                    CheckLaser(paper.Map(center.U, center.V), "the centre +");
                    CheckLaser(paper.Map(PaperPoint(159.5 / 432, 0).U, center.V), "the inner scoring ring");
                    CheckLaser(paper.Map(PaperPoint(356.5 / 432, 0).U, center.V), "the outer scoring ring");
                }
            }
            now += TimeSpan.FromSeconds(2);
            Check(LowerEqual(waiting, Draw()), "The stationary target row continued moving before all five hits.");
            var firstQuad = DuckHuntGalleryLayout.TargetQuad(0, scene.GalleryState, aspect);
            var firstLocal = PaperPoint(-.24, .18);
            var firstAim = firstQuad.Map(firstLocal.U, firstLocal.V);
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(firstAim, now) && scene.GalleryState.HitTargets == 1 && scene.GalleryPunctures.Count == 1,
                "A paper shot did not record its local puncture and unique target.");
            now += TimeSpan.FromMilliseconds(50);
            byte[] forming = Draw();
            await Save("puncture-forming");
            now += TimeSpan.FromMilliseconds(200);
            byte[] settled = Draw();
            Check(!LowerEqual(forming, settled) && scene.GalleryVisualFrame(now) == 0,
                "The torn rim did not animate briefly and settle.");
            Check(PixelDark(firstAim), "The paper's projected hit location has no black opening.");
            CheckClearOptics("punctured paper");
            var besideHole = PaperPoint(.24, .18);
            CheckLaser(firstQuad.Map(besideHole.U, besideHole.V), "paper beside its puncture");
            await Save("puncture-settled");
            now += TimeSpan.FromSeconds(1);
            Check(LowerEqual(settled, Draw()), "A settled puncture changed while its paper was stationary.");
            now += TimeSpan.FromMilliseconds(1);
            var repeatedLocal = PaperPoint(.28, .28);
            Check(scene.SubmitDuckHuntShot(firstQuad.Map(repeatedLocal.U, repeatedLocal.V), now) && scene.GalleryState.HitTargets == 1 &&
                scene.GalleryPunctures.Count == 2, "A repeated paper hit lost its hole or counted the same paper twice.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            byte[] beforeEdgeHit = TargetTexture(0).GetPixelBytes();
            // Bare paper beside the lower-right metal clip: the hole overlaps
            // both the circular edge and the clip, and must respect both.
            var edgeLocal = PaperPoint(.96, .16);
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(firstQuad.Map(edgeLocal.U, edgeLocal.V), now) && scene.GalleryState.HitTargets == 1 &&
                scene.GalleryPunctures.Count == 3, "A shot at the circular paper rim was lost or counted twice.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            CheckPunctureClippedToPaper(beforeEdgeHit);
            await Save("puncture-at-rim");
            for (int index = 1; index < DuckHuntGalleryGame.TargetCount; index++)
            {
                now += TimeSpan.FromMilliseconds(1);
                var paper = DuckHuntGalleryLayout.TargetQuad(index, scene.GalleryState, aspect);
                Check(scene.SubmitDuckHuntShot(paper.Map(DuckHuntGalleryLayout.PaperCenterU, DuckHuntGalleryLayout.PaperCenterV), now),
                    "A remaining paper did not register its hit.");
            }
            var allHitAt = now;
            now += TimeSpan.FromMilliseconds(400);
            byte[] complete = Draw();
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.SuccessWait, HitTargets: 5, FlapAngleDegrees: 95, ConveyorPosition: 1 },
                "The completed row did not hold before departing.");
            await Save("all-hit");
            now += TimeSpan.FromMilliseconds(300);
            byte[] completedPaused = Draw();
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.SuccessWait, FlapAngleDegrees: 95, ConveyorPosition: 1 } &&
                LowerEqual(complete, completedPaused), "The completed row skipped its one-second pause.");
            now += TimeSpan.FromMilliseconds(400);
            Draw();
            await Save("departing");
            double previousTilt = 0;
            foreach (var sample in new[]
            {
                (Name: "departing-deck", Progress: .66),
                (Name: "departing-curve-start", Progress: .76),
                (Name: "departing-curve-middle", Progress: .89),
                (Name: "departing-curve-end", Progress: .999)
            })
            {
                now = allHitAt + DuckHuntGalleryGame.SuccessWaitDuration +
                    TimeSpan.FromTicks((long)(DuckHuntGalleryGame.DepartDuration.Ticks * sample.Progress));
                Draw();
                var pose = DuckHuntGalleryLayout.TargetPose(2, scene.GalleryState, aspect);
                Check(scene.GalleryState.Phase == DuckHuntGalleryPhase.Departing && pose.Supported &&
                    pose.TiltDegrees >= previousTilt, "A target lost support or reversed its forward tip on the roller.");
                if (sample.Progress > .7)
                    Check(pose.TiltDegrees > 0 && pose.InFrontOfLip,
                        "The wrapped target did not tilt in front of the conveyor lip.");
                previousTilt = pose.TiltDegrees;
                await Save(sample.Name);
            }
            now = allHitAt + DuckHuntGalleryGame.SuccessWaitDuration + DuckHuntGalleryGame.DepartDuration;
            Draw();
            var released = DuckHuntGalleryLayout.TargetPose(2, scene.GalleryState, aspect);
            Check(scene.GalleryState.Phase == DuckHuntGalleryPhase.Falling && !released.Supported &&
                released.TiltDegrees >= previousTilt && released.InFrontOfLip,
                "The targets did not release from their tilted pose at the conveyor edge.");
            await Save("falling-release");
            now += TimeSpan.FromMilliseconds(60);
            Draw();
            await Save("falling-front");
            now += TimeSpan.FromMilliseconds(250);
            Draw();
            await Save("falling-turn");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            await Save("falling-reverse");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            Check(scene.GalleryState.Phase == DuckHuntGalleryPhase.Closing, "The flap did not close after the row fell away.");
            await Save("closing");
            now += TimeSpan.FromSeconds(1);
            Draw();
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, PlayVisible: false, FlapAngleDegrees: 0,
                UpperPhase: DuckHuntGalleryUpperPhase.WaitingForHits, HitSkulls: 1 },
                "The lower flap did not close independently while the remaining skulls stayed available.");
            await Save("lower-closed-skulls-open");
            for (int index = 1; index < DuckHuntGalleryGame.SkullCount; index++)
            {
                var skull = DuckHuntGalleryLayout.SkullBounds(index, aspect);
                now += TimeSpan.FromMilliseconds(1);
                Check(scene.SubmitDuckHuntShot(new(skull.X + skull.Width * .5, skull.Y + skull.Height * .4), now),
                    "A remaining skull did not accept its shot after the lower stage closed.");
            }
            var finalSkullHitAt = now;
            now += TimeSpan.FromMilliseconds(400);
            Draw();
            Check(scene.GalleryState is { HitSkulls: 5, UpperPhase: DuckHuntGalleryUpperPhase.ShatterWait, PlayVisible: false },
                "The upper row did not allow its last fragments to settle.");
            CheckClearOptics("all skulls shattered");
            await Save("all-skulls-shattered");
            now += TimeSpan.FromMilliseconds(800);
            Draw();
            Check(scene.GalleryState.UpperPhase == DuckHuntGalleryUpperPhase.Lowering, "The upper curtain did not begin closing.");
            await Save("upper-curtain-lowering");
            var middleStartedAt = finalSkullHitAt + DuckHuntGalleryGame.UpperShatterWaitDuration +
                DuckHuntGalleryGame.UpperLowerDuration;
            now = middleStartedAt;
            byte[] closedMiddle = Draw();
            Check(scene.GalleryState is { PlayVisible: false, UpperPhase: DuckHuntGalleryUpperPhase.Closed,
                Phase: DuckHuntGalleryPhase.Ready, MiddlePhase: DuckHuntGalleryMiddlePhase.Raising },
                "The middle curtain did not start only after the two original rows closed.");
            await Save("middle-curtain-start");
            now += DuckHuntGalleryGame.MiddleRaiseDuration / 2;
            Draw();
            await Save("middle-curtain-raising");
            for (int index = 0; index < DuckHuntGalleryGame.MiddleTargetCount; index++)
            {
                var enteredAt = middleStartedAt + DuckHuntGalleryGame.MiddleRaiseDuration +
                    DuckHuntGalleryGame.MiddleTargetDuration * index;
                now = enteredAt + DuckHuntGalleryGame.MiddleEnterDuration / 2;
                byte[] arriving = Draw();
                var arrivingPose = DuckHuntGalleryLayout.MiddleTargetPose(scene.GalleryState, aspect);
                Check(scene.GalleryState is { MiddlePhase: DuckHuntGalleryMiddlePhase.Entering, PlayVisible: false } &&
                    scene.GalleryState.MiddleTargetIndex == index && arrivingPose.Visible &&
                    arrivingPose.YawDegrees == 90 && arrivingPose.BaseContact.U > .5,
                    "A middle target did not enter from the right edge-on in sequence.");
                var panel = DuckHuntGalleryLayout.MiddlePanelSceneBounds;
                var frameTop = DuckHuntGalleryLayout.ScenePoint(panel.X + panel.Width + .004, panel.Y, aspect);
                var frameBottom = DuckHuntGalleryLayout.ScenePoint(panel.X + panel.Width + .018,
                    panel.Y + panel.Height, aspect);
                Check(RegionEqual(closedMiddle, arriving, new(frameTop.U, frameTop.V,
                    frameBottom.U - frameTop.U, frameBottom.V - frameTop.V)),
                    "An entering target or curtain painted over the middle stage's side frame.");
                var faceAt = enteredAt + DuckHuntGalleryGame.MiddleEnterDuration + DuckHuntGalleryGame.MiddleTurnDuration;
                now = faceAt - DuckHuntGalleryGame.MiddleTurnDuration / 2;
                Draw();
                var turningPose = DuckHuntGalleryLayout.MiddleTargetPose(scene.GalleryState, aspect);
                Check(scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.TurningToFace &&
                    turningPose.YawDegrees > 0 && turningPose.YawDegrees < 90,
                    "The middle target did not rotate from its edge to its paper face.");
                if (index == 0) await Save("middle-turning-front");
                now = faceAt;
                Draw();
                var facePose = DuckHuntGalleryLayout.MiddleTargetPose(scene.GalleryState, aspect);
                var local = PaperPoint(-.24, .18);
                var aim = facePose.Quad.Map(local.U, local.V);
                Check(scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit &&
                    facePose.YawDegrees == 0 && Math.Abs(facePose.BaseContact.U - .5) < 1e-8 &&
                    PixelLight(aim), "The middle paper did not stop fully facing the player at the stage centre.");
                Check(!scene.SubmitDuckHuntShot(null, now) && scene.GalleryPunctures.Count == 7 + index,
                    "A missing dot punctured a middle target.");
                Check(scene.SubmitDuckHuntShot(aim, now) && scene.GalleryPunctures.Count == 8 + index &&
                    scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit,
                    "A middle hit failed or skipped its three-second facing window.");
                var middleHole = scene.GalleryPunctures[^1];
                Check(middleHole.TargetIndex == DuckHuntGalleryGame.MiddlePunctureOffset + index &&
                    Math.Abs(middleHole.U - local.U) < 1e-6 && Math.Abs(middleHole.V - local.V) < 1e-6,
                    "A middle puncture was attached to the wrong target or paper coordinate.");
                now += TimeSpan.FromMilliseconds(300);
                Draw();
                Check(PixelDark(aim), "The photographed middle-paper hit location has no black opening.");
                CheckClearOptics("punctured middle paper");
                if (index == 0)
                {
                    CheckLaser(facePose.Quad.Map(PaperPoint(.24, .18).U, local.V), "middle paper beside its puncture");
                    await Save("middle-puncture-face");
                }
                now = faceAt + DuckHuntGalleryGame.MiddleFaceDuration - TimeSpan.FromTicks(1);
                Draw();
                Check(scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit &&
                    scene.GalleryState.MiddleTargetIndex == index, "A target shortened the full three-second aiming window.");
                now += TimeSpan.FromTicks(1);
                Draw();
                Check(scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.TurningAway &&
                    !scene.SubmitDuckHuntShot(aim, now), "The middle target remained hittable after three seconds.");
                now += DuckHuntGalleryGame.MiddleTurnDuration / 2;
                Draw();
                var away = DuckHuntGalleryLayout.MiddleTargetPose(scene.GalleryState, aspect);
                Check(away.YawDegrees < 0 && away.YawDegrees > -90,
                    "The punctured middle target did not turn away through ninety degrees.");
                Check(PixelDark(away.Quad.Map(local.U, local.V)),
                    "The middle puncture detached from its paper during the turn away.");
                if (index == 0) await Save("middle-puncture-turn-away");
                now += DuckHuntGalleryGame.MiddleTurnDuration / 2 + DuckHuntGalleryGame.MiddleLeaveDuration / 2;
                Draw();
                var leavingPose = DuckHuntGalleryLayout.MiddleTargetPose(scene.GalleryState, aspect);
                Check(scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.Leaving &&
                    leavingPose.YawDegrees == -90 && leavingPose.BaseContact.U < .5,
                    "A middle target did not leave edge-on toward the left.");
            }
            now = middleStartedAt + DuckHuntGalleryGame.MiddleSequenceDuration - DuckHuntGalleryGame.MiddleLowerDuration / 2;
            Draw();
            Check(scene.GalleryState is { MiddlePhase: DuckHuntGalleryMiddlePhase.Lowering, PlayVisible: false },
                "The middle curtain did not lower after exactly five targets.");
            await Save("middle-curtain-lowering");
            now = middleStartedAt + DuckHuntGalleryGame.MiddleSequenceDuration;
            Draw();
            Check(scene.GalleryState is { PlayVisible: true, MiddlePhase: DuckHuntGalleryMiddlePhase.Complete,
                MiddleTargetIndex: -1, MiddleHitMask: 31 }, "The round did not end after the middle curtain closed.");
            await Save("complete");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(new(.5, .46), now) && scene.GalleryPunctures.Count == 0 &&
                scene.GalleryState is { HitTargets: 0, HitSkulls: 0, MiddleHitMask: 0,
                    MiddlePhase: DuckHuntGalleryMiddlePhase.Closed }, "The next round retained old holes or shattered skulls.");
            now += DuckHuntGalleryGame.OpeningDuration + DuckHuntGalleryGame.RevealWaitDuration + DuckHuntGalleryGame.AdvanceDuration;
            Draw();
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.1, .93), "The gallery controls arrow did not open.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            var controls = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("Gallery drawer has no caption reference.");
            Check(controls.EnabledHoldIds.Contains("duck-hunt-exit") && controls.EnabledHoldIds.Contains("duck-hunt-back") &&
                controls.ButtonIds.All(id => id != "duck-hunt-replay"), "The gallery drawer lost EXIT/BACK or retained an obsolete round control.");
            await Save("controls");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-back", now) && scene.CurrentDuckHuntView == DuckHuntView.Menu,
                "BACK did not return from the gallery to the Duck Hunt entry screen.");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height,
                Papers = 5, Punctures = 12, Skulls = 5, MiddleTargets = 5,
                MiddleFaceSeconds = DuckHuntGalleryGame.MiddleFaceDuration.TotalSeconds,
                ClosedAngle = 0, OpenAngle = 95, OpticalCandidates = 0 });

            bool LowerEqual(byte[] before, byte[] after)
            {
                // Shared-device render order can round a few minified target-edge
                // samples differently: the captured portrait failure changed 14
                // pixels by only 1/255, with identical poses and a stable repeat.
                // Bound that GPU rounding strictly; movement must still fail.
                var region = LowerRegion();
                var top = CameraPoint(new(region.X, region.Y));
                var bottom = CameraPoint(new(region.X + region.Width, region.Y + region.Height));
                int left = Math.Clamp((int)Math.Floor(top.X), 0, dimensions.Width - 1);
                int right = Math.Clamp((int)Math.Ceiling(bottom.X), left + 1, dimensions.Width);
                int first = Math.Clamp((int)Math.Floor(top.Y), 0, dimensions.Height - 1);
                int last = Math.Clamp((int)Math.Ceiling(bottom.Y), first + 1, dimensions.Height);
                int changed = 0;
                for (int y = first; y < last; y++)
                for (int x = left; x < right; x++)
                {
                    int pixel = (y * dimensions.Width + x) * 4;
                    if (before.AsSpan(pixel, 4).SequenceEqual(after.AsSpan(pixel, 4))) continue;
                    if (++changed > 64) return false;
                    for (int channel = 0; channel < 4; channel++)
                        if (Math.Abs(before[pixel + channel] - after[pixel + channel]) > 1) return false;
                }
                return true;
            }
            BoardRect LowerRegion()
            {
                var top = DuckHuntGalleryLayout.ScenePoint(.09, .50, aspect);
                var bottom = DuckHuntGalleryLayout.ScenePoint(.91, .86, aspect);
                return new(top.U, top.V, bottom.U - top.U, bottom.V - top.V);
            }
            object RegionDifference(byte[] before, byte[] after, BoardRect region)
            {
                var top = CameraPoint(new(region.X, region.Y));
                var bottom = CameraPoint(new(region.X + region.Width, region.Y + region.Height));
                int left = Math.Clamp((int)Math.Floor(top.X), 0, dimensions.Width - 1);
                int right = Math.Clamp((int)Math.Ceiling(bottom.X), left + 1, dimensions.Width);
                int first = Math.Clamp((int)Math.Floor(top.Y), 0, dimensions.Height - 1);
                int last = Math.Clamp((int)Math.Ceiling(bottom.Y), first + 1, dimensions.Height);
                int changed = 0, maximumChannelDelta = 0, minX = right, minY = last, maxX = -1, maxY = -1;
                for (int y = first; y < last; y++)
                for (int x = left; x < right; x++)
                {
                    int pixel = (y * dimensions.Width + x) * 4;
                    if (before.AsSpan(pixel, 4).SequenceEqual(after.AsSpan(pixel, 4))) continue;
                    changed++;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    for (int channel = 0; channel < 4; channel++)
                        maximumChannelDelta = Math.Max(maximumChannelDelta,
                            Math.Abs(before[pixel + channel] - after[pixel + channel]));
                }
                return new { ChangedPixels = changed, MaximumChannelDelta = maximumChannelDelta,
                    MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
                    Region = region, CameraTop = new { top.X, top.Y }, CameraBottom = new { bottom.X, bottom.Y } };
            }
            bool RegionEqual(byte[] before, byte[] after, BoardRect region)
            {
                var top = CameraPoint(new(region.X, region.Y));
                var bottom = CameraPoint(new(region.X + region.Width, region.Y + region.Height));
                int x = Math.Clamp((int)Math.Floor(top.X), 0, dimensions.Width - 1);
                int right = Math.Clamp((int)Math.Ceiling(bottom.X), x + 1, dimensions.Width);
                int y = Math.Clamp((int)Math.Floor(top.Y), 0, dimensions.Height - 1);
                int last = Math.Clamp((int)Math.Ceiling(bottom.Y), y + 1, dimensions.Height);
                for (; y < last; y++)
                    if (!before.AsSpan((y * dimensions.Width + x) * 4, (right - x) * 4)
                        .SequenceEqual(after.AsSpan((y * dimensions.Width + x) * 4, (right - x) * 4))) return false;
                return true;
            }

            BoardAim PaperPoint(double radiusX, double radiusY) => new(
                DuckHuntGalleryLayout.PaperCenterU + radiusX * DuckHuntGalleryLayout.PaperRadiusU,
                DuckHuntGalleryLayout.PaperCenterV + radiusY * DuckHuntGalleryLayout.PaperRadiusV);
            CanvasRenderTarget TargetTexture(int index) => ((CanvasRenderTarget[])typeof(SceneCompositor)
                .GetField("_galleryTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!)[index];
            void CheckTargetTransparency()
            {
                var texture = TargetTexture(0);
                byte[] pixels = texture.GetPixelBytes();
                int width = (int)texture.Size.Width, height = (int)texture.Size.Height;
                Check(pixels[3] == 0 && pixels[(width - 1) * 4 + 3] == 0 &&
                    pixels[((height - 1) * width) * 4 + 3] == 0 && pixels[^1] == 0,
                    "The generated target carries a rectangular opaque backdrop.");
            }
            void CheckPunctureClippedToPaper(byte[] before)
            {
                var texture = TargetTexture(0);
                byte[] after = texture.GetPixelBytes();
                int width = (int)texture.Size.Width, height = (int)texture.Size.Height;
                int changedInside = 0, changedOutside = 0;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int pixel = (y * width + x) * 4;
                    if (before.AsSpan(pixel, 4).SequenceEqual(after.AsSpan(pixel, 4))) continue;
                    double u = (x + .5) / width, v = (y + .5) / height;
                    double dx = (u - DuckHuntGalleryLayout.PaperCenterU) / DuckHuntGalleryLayout.PaperRadiusU;
                    double dy = (v - DuckHuntGalleryLayout.PaperCenterV) / DuckHuntGalleryLayout.PaperRadiusV;
                    bool insideMetal = DuckHuntGalleryLayout.PaperClipBounds.Any(clip =>
                        x + .5 > clip.X * width + 1 && x + .5 < (clip.X + clip.Width) * width - 1 &&
                        y + .5 > clip.Y * height + 1 && y + .5 < (clip.Y + clip.Height) * height - 1);
                    // A one-pixel AA fringe belongs to the physical paper edge.
                    if (dx * dx + dy * dy <= 1.025 && !insideMetal) changedInside++;
                    else changedOutside++;
                }
                Check(changedInside > 0 && changedOutside == 0,
                    "A rim puncture painted the metal holder or the transparent space outside its round paper.");
            }

            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, dimensions.Width, dimensions.Height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            async Task Save(string name)
            {
                string path = Path.Combine(directory, dimensions.Name + "-" + name + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
            (double X, double Y) CameraPoint(BoardAim aim)
            {
                var map = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
                var point = map.Transform(new Point2(aim.U, aim.V));
                return (point.X * dimensions.Width, point.Y * dimensions.Height);
            }
            bool PixelLight(BoardAim point) => PixelBrightness(point) > 160;
            bool PixelDark(BoardAim point) => PixelBrightness(point) < 50;
            int PixelBrightness(BoardAim aim, int radius = 0)
            {
                var p = CameraPoint(aim);
                int x = Math.Clamp((int)Math.Round(p.X), 0, dimensions.Width - 1),
                    y = Math.Clamp((int)Math.Round(p.Y), 0, dimensions.Height - 1);
                byte[] pixels = target.GetPixelBytes();
                int darkest = 255;
                for (int sampleY = Math.Max(0, y - radius); sampleY <= Math.Min(dimensions.Height - 1, y + radius); sampleY++)
                for (int sampleX = Math.Max(0, x - radius); sampleX <= Math.Min(dimensions.Width - 1, x + radius); sampleX++)
                {
                    int offset = (sampleY * dimensions.Width + sampleX) * 4;
                    darkest = Math.Min(darkest, (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3);
                }
                return darkest;
            }
            void CheckClearOptics(string stage)
            {
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Gallery has no optical mapping.");
                var detection = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    target.GetPixelBytes(), input.CameraToBoard);
                Check(!detection.Detected && detection.CandidateCount == 0,
                    $"{dimensions.Name} {stage} created {detection.CandidateCount} false red-dot candidates.");
            }
            void CheckLaser(BoardAim aim, string stage)
            {
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Gallery has no optical mapping.");
                var camera = CameraPoint(aim);
                byte[] pixels = target.GetPixelBytes();
                for (int y = Math.Max(0, (int)camera.Y - 6); y <= Math.Min(dimensions.Height - 1, (int)camera.Y + 6); y++)
                for (int x = Math.Max(0, (int)camera.X - 6); x <= Math.Min(dimensions.Width - 1, (int)camera.X + 6); x++)
                {
                    if ((x - camera.X) * (x - camera.X) + (y - camera.Y) * (y - camera.Y) > 20) continue;
                    int p = (y * dimensions.Width + x) * 4;
                    pixels[p] = (byte)Math.Min(255, pixels[p] + 180);
                    pixels[p + 2] = 255;
                }
                var dot = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4, pixels, input.CameraToBoard);
                Check(dot.Detected && Math.Abs(dot.BoardX!.Value - aim.U) < .003 && Math.Abs(dot.BoardY!.Value - aim.V) < .003,
                    $"The additive laser over {stage} was lost or misplaced.");
            }
        }
        return new { Ok = true, Assertions = assertions, Results = results, Images = images, Directory = directory };

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
