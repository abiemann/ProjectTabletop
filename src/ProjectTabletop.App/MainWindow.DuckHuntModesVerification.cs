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
    // Local render targets and simulated input only; never starts camera/audio,
    // opens a projection window, or changes the user's current board.
    private async Task<object> VerifyDuckHuntModesAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntModesVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var results = new List<object>();
        int assertions = 0;
        foreach (var dimensions in new[]
        {
            (Name: "landscape", Width: 1920, Height: 1080),
            (Name: "square", Width: 1000, Height: 1000),
            (Name: "portrait", Width: 1080, Height: 1920)
        })
        {
            var now = MonotonicClock.UtcNow;
            using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), dimensions.Width, dimensions.Height, 96);
            var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
            scene.SetDisplayAspect(dimensions.Width / (double)dimensions.Height);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(dimensions.Width, 0), new(dimensions.Width, dimensions.Height), new(0, dimensions.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.ShowBoardMenu();
            Check(board.ActivateButton("menu-scroll-down", now), "The launcher did not open its second page.");
            now += BoardSession.MenuScrollDuration;
            Check(board.TickMenu(now), "The second page did not finish scrolling.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt", now), "The launcher did not open Duck Hunt.");
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            byte[] welcome = Draw();
            await Save("welcome");
            Check(scene.DuckHuntArtworkReady, "Duck Hunt's welcome, field or duck artwork is missing.");
            Check(scene.CurrentDuckHuntView == DuckHuntView.Menu && !scene.HandDetectionRequired,
                "The launcher did not open the welcome screen without the hand model.");
            CheckIdleInput("welcome");
            var choices = board.Buttons.Where(button => button.Id is "duck-hunt-target-practice" or "duck-hunt-hunt").ToArray();
            Check(choices.Length == 2 && choices[0].Bounds.Width == choices[1].Bounds.Width &&
                choices[0].Bounds.Height == choices[1].Bounds.Height && choices.All(button => button.Hold == BoardButtonHold.Once),
                "The two mode choices do not share one button size and caption-hold policy.");
            var menuContext = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("The mode menu has no caption reference.");
            Check(menuContext.ButtonIds.Contains("duck-hunt-target-practice") && menuContext.ButtonIds.Contains("duck-hunt-hunt"),
                "A mode button is absent from the projected caption reference.");
            foreach (var button in choices)
            {
                var caption = menuContext.ExpectedScene.BoardTriggerRegions![menuContext.ButtonIds.ToList().IndexOf(button.Id)];
                Check(button.Bounds.Contains(caption.X, caption.Y) &&
                    button.Bounds.Contains(caption.X + caption.Width, caption.Y + caption.Height) &&
                    caption.Width < button.Bounds.Width * .9 && caption.Height < button.Bounds.Height * .8,
                    $"{button.Label} does not have a tight letter-only trigger region.");
            }
            await HoldControl("duck-hunt-target-practice", DuckHuntView.TargetPractice);
            Check(scene.DuckHuntBoardReady && scene.GetDuckHuntInputContext() is not null,
                "Target Practice did not create calibrated shot input.");
            scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
            Draw();
            await Save("target-practice");
            Check(scene.GalleryState.Phase == DuckHuntGalleryPhase.Ready && scene.DuckHuntInputReady,
                "Target Practice did not wait for a shot on PLAY.");
            var practiceInput = scene.GetDuckHuntInputContext()!;
            var oldPoint = new BoardAim(.21, .36);
            var newPoint = new BoardAim(.27, .42);
            var before = MonotonicClock.UtcNow;
            scene.PublishDuckHuntDot(oldPoint, before, practiceInput.Epoch);
            var click = LaterThan(before);
            var frame = LaterThan(click);
            Check(scene.SubmitDuckHuntSnapshot(newPoint, frame, click, LaterThan(frame), practiceInput.Epoch) &&
                scene.DuckHuntMeasurement.HasCompletedPair, "Target Practice did not accept its isolated coordinate measurement.");

            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.10, .93), "Target Practice's BACK fixture could not open the drawer.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            CheckBackLayout();
            await Save("target-practice-controls");
            await HoldControl("duck-hunt-back", DuckHuntView.Menu, "target-practice-back");
            CheckIdleInput("reopened welcome");
            Check(!board.DuckHuntDrawerOpen &&
                board.Buttons.Select(button => button.Id).SequenceEqual(
                    ["duck-hunt-target-practice", "duck-hunt-hunt", "duck-hunt-drawer-open"]),
                "Target Practice BACK did not restore both welcome choices with its drawer closed.");
            Check(!scene.SubmitDuckHuntSnapshot(newPoint, LaterThan(frame), click, MonotonicClock.UtcNow, practiceInput.Epoch),
                "A previous practice shot crossed the welcome screen boundary.");
            await HoldControl("duck-hunt-hunt", DuckHuntView.Hunt);
            byte[] field = Draw();
            await Save("hunt");
            Check(!welcome.AsSpan().SequenceEqual(field), "HUNT still draws the welcome image.");
            Check(board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-open"]),
                "HUNT initially exposes controls other than its up-arrow.");
            CheckHuntReady("hunt");
            now += TimeSpan.FromSeconds(30);
            Check(field.AsSpan().SequenceEqual(Draw()), "HUNT started without a laser shot on START.");
            CheckHuntReady("hunt after waiting");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.10, .93), "HUNT's drawer arrow did not open.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            await Save("hunt-controls");
            Check(board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back"]) &&
                board.Buttons.All(button => button.Enabled), "HUNT's drawer does not contain only its down-arrow, EXIT and BACK.");
            CheckBackLayout();
            CheckHuntReady("hunt with controls");
            await HoldControl("duck-hunt-back", DuckHuntView.Menu, "hunt-back");
            CheckIdleInput("welcome after Hunt BACK");
            Check(!board.DuckHuntDrawerOpen &&
                board.Buttons.Select(button => button.Id).SequenceEqual(
                    ["duck-hunt-target-practice", "duck-hunt-hunt", "duck-hunt-drawer-open"]),
                "HUNT BACK did not restore the welcome choices with its drawer closed.");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.50, .62), "HUNT could not be reopened after BACK.");
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            Draw();
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.10, .93), "HUNT's EXIT fixture could not reopen the drawer.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-exit", now) && board.Screen == BoardScreen.Menu && board.MenuScrolled,
                "EXIT did not return to the main menu's preserved second page.");
            results.Add(new { dimensions.Name, Width = dimensions.Width, Height = dimensions.Height,
                CaptionHolds = 4, HuntWaitsForLaserStart = true, BackReturnsToWelcome = true, ExitReturnsToPageTwo = true });

            void CheckBackLayout()
            {
                var exit = board.Buttons.Single(button => button.Id == "duck-hunt-exit").Bounds;
                var backButton = board.Buttons.Single(button => button.Id == "duck-hunt-back");
                var back = backButton.Bounds;
                Check(backButton.Label == "BACK" && backButton.Hold == BoardButtonHold.Once && backButton.Enabled &&
                    back.Y == exit.Y && back.Width == exit.Width && back.Height == exit.Height &&
                    Math.Abs(back.X - (exit.X + exit.Width) - .01) < .000001,
                    "BACK is not a matching hold button immediately beside EXIT with a regular gap.");
                var reference = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("BACK has no hold reference.");
                var caption = reference.ExpectedScene.BoardTriggerRegions![reference.ButtonIds.ToList().IndexOf("duck-hunt-back")];
                Check(back.Contains(caption.X, caption.Y) && back.Contains(caption.X + caption.Width, caption.Y + caption.Height) &&
                    caption.Width < back.Width * .9 && caption.Height < back.Height * .8,
                    "BACK does not have a tight letter-only trigger region.");
            }

            void CheckIdleInput(string stage)
            {
                scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
                Check(!scene.DuckHuntBoardReady && !scene.DuckHuntInputReady && scene.GetDuckHuntInputContext() is null,
                    $"The {stage} view requests microphone/laser gameplay input.");
                Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, HitTargets: 0, Shots: 0, SkullHitMask: 0,
                        UpperPhase: DuckHuntGalleryUpperPhase.Closed, MiddlePhase: DuckHuntGalleryMiddlePhase.Closed,
                        MiddleTargetIndex: -1, MiddleHitMask: 0, Score: 0 } &&
                    board.GalleryPunctures.Count == 0 && scene.DuckHuntMeasurement is
                        { LiveDetectedXY: null, OldDetectedXY: null, NewDetectedXY: null },
                    $"The {stage} view retained a gallery round, puncture or shot mark.");
                Check(!scene.SubmitDuckHuntShot(new(.5, .48), now), $"The {stage} view accepted a direct shot.");
            }

            void CheckHuntReady(string stage)
            {
                scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
                Check(scene.DuckHuntBoardReady && scene.DuckHuntInputReady && scene.GetDuckHuntInputContext() is not null,
                    $"The {stage} view cannot listen for a shot on START.");
                Check(scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Ready, Score: 0, BirdsLaunched: 0 } &&
                    scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, HitTargets: 0, Shots: 0, SkullHitMask: 0,
                        UpperPhase: DuckHuntGalleryUpperPhase.Closed, MiddlePhase: DuckHuntGalleryMiddlePhase.Closed,
                        MiddleTargetIndex: -1, MiddleHitMask: 0, Score: 0 } &&
                    board.GalleryPunctures.Count == 0,
                    $"The {stage} view started birds without a shot or retained Target Practice state.");
                Check(!scene.ActivateDuckHuntAt(.5, .5) &&
                    board.Buttons.All(button => button.Id != "duck-hunt-start"),
                    $"The {stage} START target can be activated by a hand or laptop tap.");
            }

            async Task HoldControl(string id, DuckHuntView expected, string? captureName = null)
            {
                var presence = new HandAcquisitionPresenceTracker();
                for (int i = 0; i < 6; i++) Check(Feed(false).Count == 0, "An intact caption activated.");
                bool activated = false;
                for (int i = 0; i < 20 && !activated; i++)
                {
                    var actions = Feed(true);
                    if (actions.Count == 0) continue;
                    Check(actions.SequenceEqual([id]), "A caption hold activated the wrong control.");
                    activated = true;
                }
                Check(activated && scene.CurrentDuckHuntView == expected,
                    $"The actual broken {id} lettering did not select its destination.");
                await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
                Draw();
                await Save(captureName ?? id + "-selected");

                IReadOnlyList<string> Feed(bool broken)
                {
                    now += TimeSpan.FromMilliseconds(100);
                    byte[] pixels = Draw();
                    var reference = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("Mode caption reference disappeared.");
                    var caption = reference.ExpectedScene.BoardTriggerRegions![reference.ButtonIds.ToList().IndexOf(id)];
                    if (broken) BreakCaption(pixels, reference.ExpectedScene.CameraToBoard, caption);
                    var observation = presence.Update(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                        pixels, reference.SearchPolygon, reference.ExpectedScene, now, now);
                    Check(observation.Reason != "invalid-rendered-scene", "The mode menu has an invalid rendered caption reference.");
                    var held = reference.HeldButtons(observation);
                    Check(broken || held.Count == 0, "Intact mode lettering supplied hold evidence.");
                    return scene.ObserveHoldButtons(reference, held, now, reference.ClearedButtons(observation));
                }
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
            void BreakCaption(byte[] pixels, IReadOnlyList<double> matrix, HandTrackingBounds bounds)
            {
                for (int y = 0; y < dimensions.Height; y++)
                for (int x = 0; x < dimensions.Width; x++)
                {
                    double divisor = matrix[6] * x + matrix[7] * y + matrix[8];
                    double u = (matrix[0] * x + matrix[1] * y + matrix[2]) / divisor;
                    double v = (matrix[3] * x + matrix[4] * y + matrix[5]) / divisor;
                    double across = (u - bounds.X) / bounds.Width;
                    if (across is < 0 or > 1 || v < bounds.Y - .004 || v > bounds.Y + bounds.Height + .004 ||
                        across * 4 - Math.Floor(across * 4) > .76) continue;
                    int p = (y * dimensions.Width + x) * 4;
                    pixels[p] = 95; pixels[p + 1] = 135; pixels[p + 2] = 175; pixels[p + 3] = 255;
                }
            }
        }
        return new { Ok = true, Assertions = assertions, Results = results, Images = images, Directory = directory };

        static DateTimeOffset LaterThan(DateTimeOffset previous)
        {
            DateTimeOffset next;
            do { next = MonotonicClock.UtcNow; } while (next <= previous);
            return next;
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
