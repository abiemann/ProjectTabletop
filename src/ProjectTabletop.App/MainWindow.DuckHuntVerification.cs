#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated projected scenes only. This never opens the camera, microphone,
    // output window or a second app instance, and never changes the live board.
    private async Task<object> VerifyDuckHuntAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntVerification", Guid.NewGuid().ToString("N"));
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
            // Calibration resets input with the real clock. Begin the isolated
            // navigation timeline after it, rather than replaying its older start.
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.ShowBoardMenu();
            Check(board.ActivateButton("menu-scroll-down", now), "Could not open the second menu page.");
            now += BoardSession.MenuScrollDuration;
            Check(board.TickMenu(now), "The second menu page did not settle.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt", now), "The menu could not open Duck Hunt.");
            Draw();
            await Save("aiming");
            Check(scene.CurrentBoardScreen == BoardScreen.DuckHunt && !scene.HandDetectionRequired,
                "Duck Hunt is not active or still requests the hand model.");
            Check(!board.ControlSwipesEnabled && board.Buttons.Count == 1 && board.Buttons[0] is
                { Id: "duck-hunt-drawer-open", Hold: BoardButtonHold.Once },
                "Duck Hunt did not start with only its shared drawer arrow.");
            scene.SetDuckHuntMicrophones([new("usb", "USB recording microphone"),
                new("desk", "Desktop microphone array")], null, false);
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-open", now), "The Duck Hunt drawer did not open.");
            now += TimeSpan.FromMilliseconds(300);
            board.TickDuckHunt(now);
            Draw();
            Check(board.Buttons.Count == 3 && board.Buttons.All(button => button.Enabled),
                "The settled drawer did not enable its arrow, EXIT and MIC.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-mic", now), "MIC did not open the input choices.");
            Draw();
            await Save("microphones");
            Check(scene.DuckHuntMicrophoneMenuOpen && scene.DuckHuntBoardReady && !scene.DuckHuntInputReady,
                "The input picker did not pause shots while keeping its calibrated board ready.");
            var picker = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No microphone choice hold reference.");
            int choiceRegion = picker.ButtonIds.ToList().IndexOf("duck-hunt-microphone-1");
            Check(choiceRegion >= 0 && picker.EnabledHoldIds.Contains("duck-hunt-microphone-1"),
                "The microphone choice has no enabled caption region.");
            var reaching = new HandAcquisitionPresenceResult([], true, null, .1, "fixture",
                [new(choiceRegion, false, .1, .1, .1, ShapeCorrupted: true)]);
            Check(picker.BlockedButtons(reaching).SequenceEqual(["duck-hunt-mic"]),
                "A broken upper microphone caption did not cancel the MIC hold underneath it.");
            Check(picker.BlockedButtons(reaching with { TextPatterns =
                [new(choiceRegion, true, 1, 0, 0, CaptionReflectanceChanged: true)] }).Count == 0,
                "A readable microphone caption vetoed MIC from reflectance alone.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-mic", now) && !scene.DuckHuntMicrophoneMenuOpen,
                "MIC did not remain available to close its choices.");
            byte[] initial = Draw();
            await Save("controls");
            var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No Duck Hunt hold reference.");
            Check(context.ButtonIds.SequenceEqual(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-mic"]) &&
                context.EnabledHoldIds.Contains("duck-hunt-exit"), "The hold reference does not contain the enabled drawer controls.");
            var caption = context.ExpectedScene.BoardTriggerRegions![context.ButtonIds.ToList().IndexOf("duck-hunt-exit")];
            var exit = board.Buttons.Single(button => button.Id == "duck-hunt-exit").Bounds;
            Check(exit.Contains(caption.X, caption.Y) && exit.Contains(caption.X + caption.Width, caption.Y + caption.Height) &&
                caption.Width < exit.Width * .8 && caption.Height < exit.Height * .8,
                "EXIT hold evidence uses the button rectangle instead of its letter shapes.");
            var acquisition = scene.GetHandAcquisitionContext(now);
            Check(acquisition is { AllowsSearchIllumination: false } &&
                scene.ActiveHandSpotlightCount == 0, "Duck Hunt exposes hand search lighting.");
            Check(!scene.SubmitDuckHuntShot(null, now.AddMilliseconds(10)) && scene.DuckHuntState.Shots == 0,
                "A click without a dot counted as a shot.");
            Check(!scene.SubmitDuckHuntShot(new(.1, .4), now.AddMilliseconds(20)) &&
                scene.DuckHuntState.Hits == 0 && scene.DuckHuntState.Shots == 1,
                "A background dot hit the duck.");
            now += TimeSpan.FromMilliseconds(30);
            Check(scene.SubmitDuckHuntShot(new(.5, .48), now) && scene.DuckHuntState.Hits == 1,
                "The dot at the visible duck's centre did not hit it.");
            Check(!scene.SubmitDuckHuntShot(new(.5, .48), now.AddMilliseconds(1)),
                "The falling duck accepted a duplicate shot.");
            now += TimeSpan.FromMilliseconds(650);
            board.TickDuckHunt(now);
            byte[] falling = Draw();
            await Save("falling");
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.Falling, V: > .6, Rotation: > 0 } &&
                !initial.AsSpan().SequenceEqual(falling), "The native duck did not visibly fall.");
            var fallingContext = scene.GetHoldButtonContext(now)!;
            Check(ReferenceEquals(context.ExpectedScene, fallingContext.ExpectedScene) && context.Revision == fallingContext.Revision,
                "Duck animation rebuilt the stationary EXIT caption reference.");
            Check(scene.GetHandAcquisitionContext(now) is { AllowsSearchIllumination: false } && scene.ActiveHandSpotlightCount == 0,
                "The falling duck enabled a hand spotlight.");
            now += TimeSpan.FromMilliseconds(1100);
            board.TickDuckHunt(now);
            Draw();
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.Aiming, Hits: 1 }, "The next duck did not return after falling away.");

            // Feed the actual rendered pixels through native caption recognition.
            // A clean caption must never press; separated occlusion strips break
            // the lettering and must complete the ordinary shared one-second hold.
            var presence = new HandAcquisitionPresenceTracker();
            DateTimeOffset firstHeld = default;
            for (int frame = 0; frame < 6; frame++)
                Check(Feed(false).Count == 0, "The intact EXIT caption activated.");
            bool exited = false;
            for (int frame = 0; frame < 20; frame++)
            {
                var activated = Feed(true);
                if (activated.Count == 0) continue;
                Check(activated.SequenceEqual(["duck-hunt-exit"]) && firstHeld != default &&
                    now - firstHeld >= BoardSession.HoldActivationInterval, "EXIT activated without a full caption hold.");
                exited = true;
                break;
            }
            Check(exited && scene.CurrentBoardScreen == BoardScreen.Menu && board.MenuScrolled,
                "Native EXIT lettering did not restore the second menu page.");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height, Caption = caption,
                Hits = scene.DuckHuntState.Hits, NativeExit = exited, ExitHoldMilliseconds = (now - firstHeld).TotalMilliseconds });

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
            IReadOnlyList<string> Feed(bool broken)
            {
                now += TimeSpan.FromMilliseconds(100);
                byte[] pixels = Draw();
                var reference = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("EXIT reference disappeared during its hold.");
                if (broken) BreakCaption(pixels, reference.ExpectedScene.CameraToBoard, caption);
                var observed = presence.Update(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    pixels, reference.SearchPolygon, reference.ExpectedScene, now, now);
                Check(observed.Reason != "invalid-rendered-scene", "The native caption detector rejected Duck Hunt's reference.");
                var held = reference.HeldButtons(observed);
                Check(broken || held.Count == 0, "Intact EXIT lettering supplied hold evidence.");
                // The engine starts on this first evidence frame with progress 0.
                // HoldProgress intentionally hides zero, so its first visible rim
                // would start the fixture's clock one camera frame too late.
                if (held.Contains("duck-hunt-exit") && firstHeld == default) firstHeld = now;
                return scene.ObserveHoldButtons(reference, held, now, reference.ClearedButtons(observed));
            }
            void BreakCaption(byte[] pixels, IReadOnlyList<double> matrix, HandTrackingBounds bounds)
            {
                // Camera-space pixels are mapped through the exact fixture homography,
                // so the same four narrow finger bands work in every board orientation.
                for (int y = 0; y < dimensions.Height; y++)
                for (int x = 0; x < dimensions.Width; x++)
                {
                    double d = matrix[6] * x + matrix[7] * y + matrix[8];
                    double u = (matrix[0] * x + matrix[1] * y + matrix[2]) / d;
                    double v = (matrix[3] * x + matrix[4] * y + matrix[5]) / d;
                    double across = (u - bounds.X) / bounds.Width;
                    if (across is < 0 or > 1 || v < bounds.Y - .004 || v > bounds.Y + bounds.Height + .004 ||
                        across * 4 - Math.Floor(across * 4) > .76) continue;
                    int p = (y * dimensions.Width + x) * 4;
                    pixels[p] = 95; pixels[p + 1] = 135; pixels[p + 2] = 175; pixels[p + 3] = 255;
                }
            }
        }
        return new { Ok = true, Assertions = assertions, Results = results, Images = images, Directory = directory };

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private async Task<object> VerifyDuckHuntInputAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntInputVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const int width = 1280, height = 960;
        int assertions = 0;
        var uiNow = MonotonicClock.UtcNow;
        using var scene = new SceneCompositor(blackjackClock: () => uiNow);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.06f, .05f), new(.96f, .09f), new(.91f, .95f), new(.09f, .90f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHunt();
        byte[] clear;
        using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, width, height, false, false);
        clear = target.GetPixelBytes();
        string clearPath = Path.Combine(directory, "clear-board.png");
        await target.SaveAsync(clearPath, CanvasBitmapFileFormat.Png);
        var context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Calibrated Duck Hunt has no input context.");
        var surface = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var projected = surface.Transform(new Point2(.5, .48));
        double cameraX = projected.X * width, cameraY = projected.Y * height;
        var m = context.CameraToBoard;
        double divisor = m[6] * cameraX + m[7] * cameraY + m[8];
        Check(Math.Abs((m[0] * cameraX + m[1] * cameraY + m[2]) / divisor - .5) < .000001 &&
            Math.Abs((m[3] * cameraX + m[4] * cameraY + m[5]) / divisor - .48) < .000001,
            "The input context does not invert the rendered duck's calibrated position.");
        var detector = new RedDotDetector();
        var empty = detector.Detect(width, height, width * 4, clear, context.CameraToBoard);
        Check(!empty.Detected && empty.CandidateCount == 0,
            "The rendered duck, title or intact EXIT control was mistaken for a red dot.");
        byte[] dotted = (byte[])clear.Clone();
        for (int y = Math.Max(0, (int)cameraY - 5); y <= Math.Min(height - 1, (int)cameraY + 5); y++)
        for (int x = Math.Max(0, (int)cameraX - 5); x <= Math.Min(width - 1, (int)cameraX + 5); x++)
        {
            if ((x - cameraX) * (x - cameraX) + (y - cameraY) * (y - cameraY) > 16) continue;
            int p = (y * width + x) * 4;
            dotted[p] = 20; dotted[p + 1] = 25; dotted[p + 2] = 255; dotted[p + 3] = 255;
        }
        var detection = detector.Detect(width, height, width * 4, dotted, context.CameraToBoard);
        Check(detection.Detected && Math.Abs(detection.BoardX!.Value - .5) < .002 &&
            Math.Abs(detection.BoardY!.Value - .48) < .002, "The synthetic camera dot missed the calibrated duck centre.");
        var aim = new BoardAim(detection.BoardX!.Value, detection.BoardY!.Value);
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");
        DateTimeOffset latest = MonotonicClock.UtcNow;

        context = Reset();
        Check(!Snapshot(aim, context.Epoch - 1) && scene.DuckHuntState.Shots == 0,
            "An obsolete input epoch supplied a shot.");

        context = Reset();
        Check(!scene.SubmitDuckHuntSnapshot(aim, context.NotBefore.AddTicks(-1), Fresh(), Fresh(), context.Epoch),
            "Pre-reset camera pixels supplied a shot.");

        context = Reset();
        var onset = Fresh();
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh().AddSeconds(1), onset, Fresh(), context.Epoch),
            "A future camera observation supplied a shot.");

        context = Reset();
        var precedingFrame = Fresh();
        Check(!scene.SubmitDuckHuntSnapshot(aim, precedingFrame, Fresh(), Fresh(), context.Epoch),
            "A snapshot from before the click onset supplied a shot.");

        context = Reset();
        onset = Fresh();
        Check(!scene.SubmitDuckHuntSnapshot(null, Fresh(), onset, Fresh(), context.Epoch),
            "A snapshot without an unambiguous dot supplied a shot.");
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh(), onset, Fresh(), context.Epoch),
            "A missing-dot decision was retried using a later dot.");

        context = Reset();
        scene.SetDuckHuntAudioStatus(false, "Microphone unavailable");
        Check(!Snapshot(aim, context.Epoch) && scene.DuckHuntState.Shots == 0,
            "A disabled microphone supplied a shot.");
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");

        context = Reset();
        scene.PublishDuckHuntDot(aim, Fresh(), context.Epoch);
        long visibleRevision = scene.DuckHuntInputVisualRevision;
        var expiredOnset = Fresh();
        await Task.Delay(DuckHuntAimGate.Freshness + TimeSpan.FromMilliseconds(35));
        Check(scene.DuckHuntInputVisualRevision > visibleRevision,
            "The dot-visible hint stayed cached after its frame expired.");
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh(), expiredOnset, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0, "An expired click used a newly visible dot.");

        context = Reset();
        var beforeLearning = context;
        var beforeLearningClick = Fresh();
        long beforeLearningHint = scene.DuckHuntInputVisualRevision;
        scene.SetDuckHuntLearningActive(true);
        Check(scene.DuckHuntBoardReady && !scene.DuckHuntInputReady && scene.GetDuckHuntInputContext() is null,
            "Click learning did not pause shooting while keeping the calibrated board available.");
        Check(scene.DuckHuntInputVisualRevision > beforeLearningHint,
            "Starting click learning did not refresh the projected shots-paused message.");
        Check(!Snapshot(aim, beforeLearning.Epoch) && scene.DuckHuntState.Shots == 0,
            "Click learning accepted a gameplay snapshot or a new training sound.");
        scene.SetDuckHuntLearningActive(false);
        context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Input did not resume after click learning.");
        Check(scene.DuckHuntBoardReady && context.Epoch != beforeLearning.Epoch,
            "Ending click learning reused its previous camera interval.");
        Check(!Snapshot(aim, beforeLearning.Epoch) &&
            !scene.SubmitDuckHuntSnapshot(aim, Fresh(), beforeLearningClick, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0, "Ending click learning reused old snapshot work or audio.");

        context = Reset();
        var oldContext = context;
        var beforeResetClick = Fresh();
        context = Reset();
        Check(!Snapshot(aim, oldContext.Epoch) &&
            !scene.SubmitDuckHuntSnapshot(aim, Fresh(), beforeResetClick, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0 && context.Epoch != oldContext.Epoch,
            "Resetting camera input retained in-flight snapshot work or audio.");

        scene.ShowBoardMenu();
        Check(scene.GetDuckHuntInputContext() is null, "Duck Hunt input stayed active on the menu.");
        scene.ShowDuckHunt();
        context = scene.GetDuckHuntInputContext()!;
        Check(!Snapshot(aim, oldContext.Epoch) && scene.DuckHuntState.Shots == 0,
            "A previous visit's snapshot activated the reopened board.");
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");
        context = Reset();
        // A prior continuous hint can be absent or wrong without vetoing the current image.
        scene.PublishDuckHuntDot(null, Fresh(), context.Epoch);
        onset = Fresh();
        await Task.Delay(60);
        var confirmedAt = Fresh();
        Check(scene.SubmitDuckHuntSnapshot(aim, confirmedAt.AddMilliseconds(-10), onset, confirmedAt, context.Epoch) &&
            scene.DuckHuntState is { Hits: 1, Shots: 1, Phase: DuckHuntPhase.Falling },
            "A current snapshot without prior visibility did not hit the calibrated duck.");
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh(), onset, Fresh(), context.Epoch),
            "The same click was accepted again with newer pixels.");
        Snapshot(aim, context.Epoch);
        Check(scene.DuckHuntState is { Hits: 1, Shots: 1 }, "A repeated click hit the falling duck again.");

        // Only the drawer animation uses an injected presentation clock. Camera
        // and microphone timestamps remain actual monotonic observations.
        uiNow = Fresh();
        scene.ShowDuckHunt();
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        context = Reset();
        oldContext = context;
        var beforePickerClick = Fresh();
        uiNow = Fresh();
        Check(scene.ActivateDuckHuntAt(.10, .93), "The input fixture could not open the drawer.");
        uiNow += TimeSpan.FromMilliseconds(300);
        board.TickDuckHunt(uiNow);
        uiNow += TimeSpan.FromMilliseconds(1);
        Check(scene.ActivateDuckHuntAt(.44, .93), "The input fixture could not open MIC.");
        Check(scene.DuckHuntMicrophoneMenuOpen && scene.DuckHuntBoardReady &&
            !scene.DuckHuntInputReady && scene.GetDuckHuntInputContext() is null,
            "Opening MIC stopped the board microphone lifetime or retained shooting input.");
        Check(!Snapshot(aim, oldContext.Epoch) && scene.DuckHuntState.Shots == 0,
            "A snapshot hit the duck while choosing a microphone.");
        uiNow += TimeSpan.FromMilliseconds(1);
        Check(scene.ActivateDuckHuntAt(.44, .93) && !scene.DuckHuntMicrophoneMenuOpen,
            "The input fixture could not close MIC.");
        context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Closing MIC did not restore input.");
        Check(scene.DuckHuntBoardReady && context.Epoch != oldContext.Epoch,
            "Closing MIC reused the picker entry's camera epoch.");
        Check(!Snapshot(aim, oldContext.Epoch) &&
            !scene.SubmitDuckHuntSnapshot(aim, Fresh(), beforePickerClick, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0, "Closing MIC reused a snapshot or click from before the picker opened.");
        Check(Snapshot(aim, context.Epoch) && scene.DuckHuntState is { Hits: 1, Shots: 1 },
            "A fresh snapshot did not recover after closing MIC.");
        return new { Ok = true, Assertions = assertions, Detection = detection, ClickLearningPauseVerified = true,
            ClickSnapshotVerified = true, NoDotCandidates = empty.CandidateCount,
            Images = new[] { clearPath }, Directory = directory };

        bool Snapshot(BoardAim? point, long epoch)
        {
            var click = Fresh();
            var frame = Fresh();
            return scene.SubmitDuckHuntSnapshot(point, frame, click, Fresh(), epoch);
        }
        SceneCompositor.DuckHuntInputContext Reset()
        {
            scene.ClearDuckHuntInput();
            var reset = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Input context did not return after reset.");
            if (reset.NotBefore > latest) latest = reset.NotBefore;
            return reset;
        }
        DateTimeOffset Fresh()
        {
            DateTimeOffset time;
            do { time = MonotonicClock.UtcNow; } while (time <= latest);
            latest = time;
            return time;
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
