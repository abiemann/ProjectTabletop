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
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.Opening, FlapAngleDegrees: 0 },
                "The flap did not begin from its exact closed plane.");
            now += TimeSpan.FromMilliseconds(350);
            Draw();
            await Save("opening-front");
            now += TimeSpan.FromMilliseconds(450);
            Draw();
            await Save("opening-reverse");
            now += TimeSpan.FromMilliseconds(250);
            byte[] revealed = Draw();
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.RevealWait, FlapAngleDegrees: 95, ConveyorPosition: 0 },
                "The panel did not reach 95 degrees before the reveal pause.");
            await Save("revealed");
            now += TimeSpan.FromMilliseconds(700);
            Check(revealed.AsSpan().SequenceEqual(Draw()), "The conveyor moved during the one-second reveal pause.");
            now += TimeSpan.FromMilliseconds(650);
            Draw();
            await Save("advancing");
            now += TimeSpan.FromSeconds(1.5);
            byte[] waiting = Draw();
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.WaitingForHits, HitTargets: 0, ConveyorPosition: 1 },
                "Five papers did not stop at the firing position.");
            CheckClearOptics("five clean papers");
            await Save("waiting");
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
            Check(waiting.AsSpan().SequenceEqual(Draw()), "The stationary target row continued moving before all five hits.");
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
            Check(!forming.AsSpan().SequenceEqual(settled) && scene.GalleryVisualFrame(now) == 0,
                "The torn rim did not animate briefly and settle.");
            Check(PixelDark(firstAim), "The paper's projected hit location has no black opening.");
            CheckClearOptics("punctured paper");
            var besideHole = PaperPoint(.24, .18);
            CheckLaser(firstQuad.Map(besideHole.U, besideHole.V), "paper beside its puncture");
            await Save("puncture-settled");
            now += TimeSpan.FromSeconds(1);
            Check(settled.AsSpan().SequenceEqual(Draw()), "A settled puncture changed while its paper was stationary.");
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
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.SuccessWait, HitTargets: 5, ConveyorPosition: 1 },
                "The completed row did not hold before departing.");
            await Save("all-hit");
            now += TimeSpan.FromMilliseconds(300);
            Check(complete.AsSpan().SequenceEqual(Draw()), "The completed row skipped its one-second pause.");
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
            Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, PlayVisible: true, FlapAngleDegrees: 0 },
                "The saloon did not return to its closed PLAY state.");
            await Save("complete");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(new(.5, .46), now) && scene.GalleryPunctures.Count == 0 &&
                scene.GalleryState.HitTargets == 0, "The next row retained the old punctures.");
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
                Papers = 5, Punctures = 7, ClosedAngle = 0, OpenAngle = 95, OpticalCandidates = 0 });

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
