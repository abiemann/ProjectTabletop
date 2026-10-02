#if DEBUG
using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Renders the slot machine through every phase on a fixture scene with its
    // own clock. It never touches the camera, projector or the live board.
    private async Task<object> VerifySlotsAsync()
    {
        const int width = 3840, height = 2160;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "SlotsVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var capturedEffects = new HashSet<SlotEffect>();
        var outroChangedPixels = new Dictionary<string, int>();
        int outroSameClockComparisons = 0;
        var game = new SlotGame(20260930);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, globeClock: () => now, slots: game);
        Configure(scene, width, height);

        scene.ShowBoardMenu();
        var cog = scene.CurrentBoardButtons.Single(button => button.Id == "settings");
        Require(scene.CurrentBoardButtons.First().Id == "slots" && cog.Destination == BoardScreen.Settings &&
            scene.CurrentBoardButtons.All(button => button.Destination != BoardScreen.HandTracking),
            "The menu does not lead with Dragon Slots and a Settings cog.");
        byte[] menu = await Capture("menu-with-cog");
        scene.ShowSettings();
        byte[] settings = await Capture("settings");
        Require(ChangedPixels(menu, settings) > 100000, "Settings did not render its own page.");

        scene.ShowSlots();
        byte[] idle = await Capture("slots-idle");
        Require(scene.SlotsArtworkReady, "Slot artwork did not load: " + (scene.SlotsArtworkError ?? "no load error reported"));
        var idleRevision = scene.SlotsState.Revision;
        now += TimeSpan.FromMilliseconds(250);
        byte[] idleLater = await Capture("slots-idle-250ms");
        int idleChangedPixels = ChangedPixels(idle, idleLater, 12);
        Require(idleChangedPixels > 100, "Idle artwork did not visibly animate after 250 ms.");
        Require(scene.SlotsState.Phase == SlotPhase.Idle && scene.SlotsState.Revision == idleRevision,
            "Idle visual animation changed the game state.");
        int controlsOffset = (height * 4 / 5) * width * 4;
        Require(idle.AsSpan(controlsOffset).SequenceEqual(idleLater.AsSpan(controlsOffset)),
            "Idle animation changed the lower 20% of the output containing the camera-observed controls.");
        Require(idleLater.AsSpan().SequenceEqual(Draw()), "Repeated slot draws at the same clock produced different pixels.");

        // Measure the complete native render plus GPU readback after warm-up.
        // PNG encoding is deliberately outside these timings; no machine-specific
        // frame budget is asserted by this rendering regression.
        for (int warm = 0; warm < 3; warm++)
        {
            now += TimeSpan.FromSeconds(1.0 / 60);
            Draw();
        }
        var renderMilliseconds = new double[20];
        for (int frame = 0; frame < renderMilliseconds.Length; frame++)
        {
            now += TimeSpan.FromSeconds(1.0 / 60);
            long timestamp = Stopwatch.GetTimestamp();
            Draw();
            renderMilliseconds[frame] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        }
        Array.Sort(renderMilliseconds);
        double medianMilliseconds = (renderMilliseconds[9] + renderMilliseconds[10]) / 2;
        double p95Milliseconds = renderMilliseconds[(int)Math.Ceiling(renderMilliseconds.Length * .95) - 1];
        Require(scene.SlotsState.Phase == SlotPhase.Idle && scene.SlotsState.Revision == idleRevision,
            "Warm render sampling changed the idle game state.");
        var buttons = scene.CurrentBoardButtons;
        Require(buttons.Count == 5 && buttons.All(button => button.Hold == BoardButtonHold.Once),
            "The slot machine does not have five long-press controls.");
        var context = scene.GetHandAcquisitionContext(now);
        Require(context?.HoldControls?.Count == 5, "The slot controls are not all excluded from hand-model searches.");

        // A base spin, sampled while reels turn and after they stop.
        Require(scene.ActivateSlotsButton("slot-spin"), "Spin was refused.");
        var started = now;
        now = started.AddMilliseconds(450);
        byte[] fast = await Capture("spin-fast");
        now = started.AddMilliseconds(1350);
        byte[] stopping = await Capture("spin-reels-stopping");
        Require(ChangedPixels(idle, fast) > 50000 && ChangedPixels(fast, stopping) > 50000,
            "The reels did not visibly spin.");
        Settle();

        // Spin until a line win shows, to capture its presentation.
        for (int spin = 0; spin < 40; spin++)
            if (await CapturedLineWin()) break;
        Require(images.Any(path => path.EndsWith("line-wins.png", StringComparison.Ordinal)), "No line win appeared in 40 spins.");

        await Feature(SlotDemo.Respins, "respins");
        await Feature(SlotDemo.FreeSpins, "free-spins");
        await Feature(SlotDemo.Vault, "vault");
        Require(outroSameClockComparisons == 9,
            "The feature fixtures did not cover all three outro phases at 22%, 55% and 90%.");

        // Buy runs the respins directly.
        Require(scene.ActivateSlotsButton("slot-buy"), "Buy was refused.");
        Advance(SlotPhase.RespinIntro);
        await Capture("buy-respins-intro");
        Advance(SlotPhase.Respinning);
        for (int warm = 0; warm < 3; warm++)
        {
            now += TimeSpan.FromSeconds(1.0 / 60);
            Draw();
        }
        var featureRenderMilliseconds = new double[20];
        int featureCoins = scene.SlotsState.Grid.Count(cell => cell.Symbol == SlotSymbol.Coin);
        for (int frame = 0; frame < featureRenderMilliseconds.Length; frame++)
        {
            now += TimeSpan.FromSeconds(1.0 / 60);
            long timestamp = Stopwatch.GetTimestamp();
            Draw();
            featureRenderMilliseconds[frame] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        }
        Require(scene.SlotsState.InRespins && featureCoins > 0,
            "Feature timing did not render active respin coins.");
        Array.Sort(featureRenderMilliseconds);
        Settle();

        // The primary demonstration seed is not guaranteed to land every egg.
        // Search only the pure game state, rendering at most the missing effects.
        await CaptureMissingEffects();

        // A square board keeps symbols in their physical proportions.
        await CheckAspect(2160, 2160, "square-board");
        await CheckAspect(2160, 3840, "portrait-board");
        await CheckAspect(2777, 2160, "measured-board-72x56");
        return new
        {
            passed = true, directory, images, artworkLoaded = true, idleChangedPixels,
            sameClockPixelsIdentical = true, idleControlsUnchanged = true,
            outroChangedPixels, outroSameClockComparisons,
            effects = capturedEffects.OrderBy(effect => effect).Select(effect => effect.ToString()).ToArray(),
            renderAndReadback = new
            {
                width, height, warmFrames = 3, samples = renderMilliseconds.Length,
                medianMilliseconds = Math.Round(medianMilliseconds, 2), p95Milliseconds = Math.Round(p95Milliseconds, 2),
                includesGpuReadback = true, includesPngEncoding = false
            },
            featureRenderAndReadback = new
            {
                width, height, warmFrames = 3, samples = featureRenderMilliseconds.Length, featureCoins,
                medianMilliseconds = Math.Round((featureRenderMilliseconds[9] + featureRenderMilliseconds[10]) / 2, 2),
                p95Milliseconds = Math.Round(featureRenderMilliseconds[(int)Math.Ceiling(featureRenderMilliseconds.Length * .95) - 1], 2),
                includesGpuReadback = true, includesPngEncoding = false
            }
        };

        async Task<bool> CapturedLineWin()
        {
            Require(scene.ActivateSlotsButton("slot-spin"), "Spin was refused.");
            now = scene.SlotsState.PhaseStartedAt + scene.SlotsState.PhaseDuration;
            scene.TickSlots(now);
            if (scene.SlotsState.Phase == SlotPhase.LineWins)
            {
                now += TimeSpan.FromMilliseconds(500);
                await Capture("line-wins");
                Settle();
                return true;
            }
            Settle();
            return false;
        }

        async Task Feature(SlotDemo demo, string name)
        {
            scene.DemonstrateSlots(demo);
            Require(scene.ActivateSlotsButton("slot-spin"), $"Spin was refused before {name}.");
            var seen = new HashSet<string>();
            for (int guard = 0; guard < 400 && scene.SlotsState.Phase != SlotPhase.Idle; guard++)
            {
                var state = scene.SlotsState;
                string label = state.Phase switch
                {
                    SlotPhase.RespinEffect => "effect-" + state.Effect.ToString().ToLowerInvariant(),
                    _ => state.Phase.ToString().ToLowerInvariant()
                };
                if (seen.Add(label))
                {
                    var phaseStart = state.PhaseStartedAt;
                    byte[]? earlyEffect = null;
                    byte[]? earlyOutro = null;
                    bool outro = state.Phase is SlotPhase.RespinOutro or SlotPhase.FreeSpinsOutro or SlotPhase.VaultOutro;
                    if (state.Phase == SlotPhase.RespinEffect)
                    {
                        now = phaseStart + state.PhaseDuration * .22;
                        earlyEffect = await Capture($"{name}-{label}-early");
                    }
                    if (outro)
                    {
                        now = phaseStart + state.PhaseDuration * .22;
                        earlyOutro = await Capture($"{name}-{label}-early");
                        CheckOutroFrame(earlyOutro, state, "early");
                    }
                    now = phaseStart + state.PhaseDuration * (state.Phase is SlotPhase.Respinning ? .45 : .55);
                    byte[] middle = await Capture($"{name}-{label}");
                    if (earlyEffect is not null)
                    {
                        Require(ChangedPixels(earlyEffect, middle) > 100, $"{state.Effect} did not visibly animate.");
                        Require(scene.SlotsState.Revision == state.Revision, $"Rendering {state.Effect} changed its game state.");
                        capturedEffects.Add(state.Effect);
                    }
                    if (earlyOutro is not null)
                    {
                        CheckOutroFrame(middle, state, "middle");
                        now = phaseStart + state.PhaseDuration * .90;
                        byte[] final = await Capture($"{name}-{label}-final");
                        CheckOutroFrame(final, state, "final");
                        int earlyToMiddle = ChangedPixels(earlyOutro, middle);
                        int middleToFinal = ChangedPixels(middle, final);
                        Require(earlyToMiddle > 100 && middleToFinal > 100,
                            $"{state.Phase} did not visibly progress through its early, middle and final celebration stages.");
                        outroChangedPixels[$"{state.Phase}-early-to-middle"] = earlyToMiddle;
                        outroChangedPixels[$"{state.Phase}-middle-to-final"] = middleToFinal;
                    }
                    if (state.Phase == SlotPhase.Respinning)
                    {
                        now = phaseStart + state.PhaseDuration * .85;
                        await Capture($"{name}-{label}-landed");
                    }
                }
                now = state.PhaseStartedAt + state.PhaseDuration;
                scene.TickSlots(now);
            }
            Require(scene.SlotsState.Phase == SlotPhase.Idle, $"The {name} feature never finished.");
        }

        void CheckOutroFrame(byte[] pixels, SlotSnapshot state, string stage)
        {
            Require(pixels.AsSpan().SequenceEqual(Draw()),
                $"{state.Phase} {stage}: repeated draws at the same clock produced different pixels.");
            Require(scene.SlotsState.Phase == state.Phase && scene.SlotsState.Revision == state.Revision,
                $"{state.Phase} {stage}: rendering the celebration changed its game state.");
            outroSameClockComparisons++;
        }

        async Task CaptureMissingEffects()
        {
            for (int seed = 0; seed < 64 && capturedEffects.Count < 3; seed++)
            {
                var candidate = new SlotGame(seed, 1_000_000);
                var effectNow = now;
                candidate.Demonstrate(SlotDemo.Respins);
                Require(candidate.HandleAction("slot-spin", effectNow), $"Effect fixture {seed} could not spin.");
                for (int guard = 0; guard < 400 && candidate.Phase != SlotPhase.Idle; guard++)
                {
                    var state = candidate.Snapshot;
                    if (state.Phase == SlotPhase.RespinEffect && !capturedEffects.Contains(state.Effect))
                    {
                        using var effectScene = new SceneCompositor(blackjackClock: () => effectNow, slots: candidate);
                        Configure(effectScene, width, height);
                        effectScene.ShowSlots();
                        string name = $"seed-{seed:D2}-effect-{state.Effect.ToString().ToLowerInvariant()}";
                        effectNow = state.PhaseStartedAt + state.PhaseDuration * .22;
                        byte[] early = await CaptureFixture(effectScene, name + "-early");
                        Require(effectScene.SlotsArtworkReady, "Effect artwork did not load: " + effectScene.SlotsArtworkError);
                        effectNow = state.PhaseStartedAt + state.PhaseDuration * .55;
                        byte[] middle = await CaptureFixture(effectScene, name + "-middle");
                        Require(ChangedPixels(early, middle) > 100, $"Seed {seed}: {state.Effect} did not visibly animate.");
                        Require(candidate.Revision == state.Revision, $"Rendering seed {seed} changed its effect state.");
                        capturedEffects.Add(state.Effect);
                        if (capturedEffects.Count == 3) break;
                    }
                    effectNow = state.PhaseStartedAt + state.PhaseDuration;
                    candidate.Tick(effectNow);
                }
            }
            Require(capturedEffects.SetEquals([SlotEffect.Expand, SlotEffect.Collect, SlotEffect.Boost]),
                "The render fixtures did not cover Expand, Collect and Boost within 64 deterministic seeds.");
        }

        void Advance(SlotPhase phase)
        {
            for (int guard = 0; guard < 100 && scene.SlotsState.Phase != phase; guard++)
            {
                now = scene.SlotsState.PhaseStartedAt + scene.SlotsState.PhaseDuration;
                scene.TickSlots(now);
            }
            Require(scene.SlotsState.Phase == phase, $"The machine never reached {phase}.");
            now += TimeSpan.FromMilliseconds(600);
        }

        void Settle()
        {
            for (int guard = 0; guard < 2000 && scene.SlotsState.Phase != SlotPhase.Idle; guard++)
            {
                now = scene.SlotsState.PhaseStartedAt + scene.SlotsState.PhaseDuration;
                scene.TickSlots(now);
            }
            Require(scene.SlotsState.Phase == SlotPhase.Idle, "The machine never settled.");
        }

        async Task CheckAspect(int fixtureWidth, int fixtureHeight, string name)
        {
            using var fixtureTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), fixtureWidth, fixtureHeight, 96);
            using var fixture = new SceneCompositor(blackjackClock: () => now, slots: new SlotGame(3));
            Configure(fixture, fixtureWidth, fixtureHeight);
            fixture.ShowSlots();
            using (var drawing = fixtureTarget.CreateDrawingSession())
                fixture.Draw(drawing, fixtureWidth, fixtureHeight, preview: false, runningSlowly: false);
            Require(fixture.SlotsArtworkReady, $"{name} artwork did not load: {fixture.SlotsArtworkError}");
            Require(fixture.CurrentBoardButtons.Count == 5 && fixture.SlotsState.Phase == SlotPhase.Idle,
                $"{name} lost its controls or changed the initial game state.");
            string path = Path.Combine(directory, name + ".png");
            await fixtureTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }

        async Task<byte[]> Capture(string name)
            => await CaptureFixture(scene, name);

        async Task<byte[]> CaptureFixture(SceneCompositor fixture, string name)
        {
            byte[] pixels = DrawFixture(fixture);
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            return pixels;
        }

        byte[] Draw() => DrawFixture(scene);

        byte[] DrawFixture(SceneCompositor fixture)
        {
            using (var drawing = target.CreateDrawingSession())
                fixture.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }

        static void Configure(SceneCompositor fixture, int fixtureWidth, int fixtureHeight)
        {
            fixture.SetDisplayAspect(fixtureWidth / (double)fixtureHeight);
            fixture.SetBoardSetup(true);
            fixture.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(fixtureWidth, 0), new(fixtureWidth, fixtureHeight), new(0, fixtureHeight)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            fixture.SetBoardSetup(false);
        }

        static int ChangedPixels(byte[] first, byte[] second, int rgbThreshold = 30)
        {
            int changed = 0;
            for (int index = 0; index < first.Length; index += 4)
                if (Math.Abs(first[index] - second[index]) + Math.Abs(first[index + 1] - second[index + 1]) +
                    Math.Abs(first[index + 2] - second[index + 2]) > rgbThreshold) changed++;
            return changed;
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    // A short, reproducible native sequence for motion review. The game advances
    // on an injected clock; no camera, output window or user's game is touched.
    private async Task<object> VerifySlotsMotionAsync()
    {
        const int width = 1152, height = 896, frameRate = 24, frameCount = 48;
        const int seed = 20260930;
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        string directory = Path.Combine(_appDataDirectory, "SlotsMotion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, slots: new SlotGame(seed));
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowSlots();
        scene.DemonstrateSlots(SlotDemo.Respins);
        RequireMotion(scene.ActivateSlotsButton("slot-spin"), "The motion fixture could not start its feature.");
        for (int guard = 0; guard < 100 && scene.SlotsState.Phase != SlotPhase.Respinning; guard++)
        {
            var state = scene.SlotsState;
            now = state.PhaseStartedAt + state.PhaseDuration;
            scene.TickSlots(now);
        }
        RequireMotion(scene.SlotsState.Phase == SlotPhase.Respinning, "The motion fixture never reached its first respin.");
        var respin = scene.SlotsState;
        now = respin.PhaseStartedAt + SlotGame.RespinLanding + TimeSpan.FromSeconds((SlotGame.Reels - 1) * .06 + .025);
        RequireMotion(now < respin.PhaseStartedAt + respin.PhaseDuration,
            "The motion fixture's post-landing start exceeded its respin phase.");
        int firstFrameCoins = respin.Grid.Count(cell => cell.Symbol == SlotSymbol.Coin);
        RequireMotion(firstFrameCoins > 0, "The motion fixture has no flame-bearing coins.");
        for (int warm = 0; warm < 3; warm++) Render();
        RequireMotion(scene.SlotsArtworkReady, "Motion artwork did not load: " + scene.SlotsArtworkError);

        var start = now;
        var files = new List<string>(frameCount);
        var phases = new List<object>(frameCount);
        var renderMilliseconds = new double[frameCount];
        byte[]? firstPixels = null;
        int changedPixels = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            now = start + TimeSpan.FromSeconds(frame / (double)frameRate);
            scene.TickSlots(now);
            long timestamp = Stopwatch.GetTimestamp();
            byte[] pixels = Render();
            renderMilliseconds[frame] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            firstPixels ??= pixels;
            if (frame == frameCount - 1)
                for (int index = 0; index < pixels.Length; index += 4)
                    if (Math.Abs(firstPixels[index] - pixels[index]) + Math.Abs(firstPixels[index + 1] - pixels[index + 1]) +
                        Math.Abs(firstPixels[index + 2] - pixels[index + 2]) > 24) changedPixels++;
            string path = Path.Combine(directory, $"frame-{frame:D3}.png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            files.Add(path);
            var state = scene.SlotsState;
            phases.Add(new
            {
                frame, seconds = Math.Round(frame / (double)frameRate, 4),
                phase = state.Phase.ToString(), effect = state.Effect.ToString(), state.Revision,
                coins = state.Grid.Count(cell => cell.Symbol == SlotSymbol.Coin)
            });
        }
        RequireMotion(changedPixels > 1000, "The native motion sequence did not visibly change.");
        Array.Sort(renderMilliseconds);
        return new
        {
            passed = true, directory, files, width, height, frameRate, frameCount, seed,
            durationSeconds = frameCount / (double)frameRate, firstFrameCoins, changedPixels, phases,
            renderAndReadback = new
            {
                samples = frameCount, warmFrames = 3,
                medianMilliseconds = Math.Round((renderMilliseconds[23] + renderMilliseconds[24]) / 2, 2),
                p95Milliseconds = Math.Round(renderMilliseconds[(int)Math.Ceiling(frameCount * .95) - 1], 2),
                includesGpuReadback = true, includesPngEncoding = false
            }
        };

        byte[] Render()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }

        static void RequireMotion(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
    // Exercises real rainbow-egg effects and their durable hatch timestamps.
    // Exact phase boundaries are ticked between samples so PNG encoding speed
    // cannot change the game timeline or the progression in the motion gallery.
    private async Task<object> VerifySlotsHatchingAsync()
    {
        const int width = 3840, height = 2160, motionWidth = 1152, motionHeight = 896;
        const int frameRate = 24, frameCount = 192;
        var origin = new DateTimeOffset(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);
        var (seed, hatches) = FindRainbow();
        var firstHatch = hatches.Min(hatch => hatch.HatchedAt);
        var allResting = hatches.Max(hatch => hatch.HatchedAt).AddSeconds(2.2);
        string directory = Path.Combine(_appDataDirectory, "SlotsHatching", Guid.NewGuid().ToString("N"));
        string motionDirectory = Path.Combine(directory, "motion");
        Directory.CreateDirectory(motionDirectory);
        var images = new List<string>();
        var headerChanges = new Dictionary<string, int>();
        var now = origin;
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, slots: new SlotGame(seed));
        var boardMap = ConfigureHatching(scene, width, height);
        Start(scene);
        var samples = new List<(DateTimeOffset Time, string Name, SlotEffect Power, double Age)>
        {
            (firstHatch.AddSeconds(-.65), "eggs-before-hatching", SlotEffect.None, -.65),
            (allResting, "all-three-resting", SlotEffect.None, 2.2)
        };
        foreach (var hatch in hatches)
            foreach (double age in new[] { 0, .2, .45, .8, 1.2, 2.0 })
                samples.Add((hatch.HatchedAt.AddSeconds(age),
                    $"{hatch.Power.ToString().ToLowerInvariant()}-{(int)Math.Round(age * 1000):D4}ms", hatch.Power, age));
        var beginnings = new Dictionary<SlotEffect, (byte[] Pixels, long Revision)>();
        foreach (var sample in samples.OrderBy(sample => sample.Time))
        {
            AdvanceHatching(scene, ref now, sample.Time);
            var state = scene.SlotsState;
            byte[] pixels = Draw(scene, target, width, height);
            // Publish the exact sample before validating it, so a failed region
            // comparison retains both native frames for visual diagnosis.
            string path = Path.Combine(directory, sample.Name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            RequireHatching(scene.SlotsArtworkReady, "Hatch artwork did not load: " + scene.SlotsArtworkError);
            RequireHatching(pixels.AsSpan().SequenceEqual(Draw(scene, target, width, height)),
                $"{sample.Name}: same-clock hatch rendering changed pixels.");
            CheckBlackMargin(pixels, width, height, sample.Name);
            if (sample.Power != SlotEffect.None)
            {
                RequireHatching(state.DragonHatches.Any(hatch => hatch.Power == sample.Power &&
                    hatch.HatchedAt == hatches.Single(item => item.Power == sample.Power).HatchedAt),
                    $"{sample.Name}: the effect's first hatch timestamp was missing or restarted.");
                if (sample.Age == 0) beginnings[sample.Power] = (pixels, state.Revision);
                if (sample.Age == 1.2)
                {
                    var beginning = beginnings[sample.Power];
                    RequireHatching(beginning.Revision == state.Revision,
                        $"{sample.Name}: the hatch-only comparison crossed a game transition.");
                    double aspect = Math.Clamp(width / (double)height, .6, 2.5);
                    int column = sample.Power switch { SlotEffect.Expand => -1, SlotEffect.Collect => 0, _ => 1 };
                    double center = 500 + column * 150 / aspect;
                    int changed = ChangedRegion(beginning.Pixels, pixels, width, height, boardMap,
                        center - 71 / aspect, 136, 142 / aspect, 100);
                    RequireHatching(changed > 100, $"{sample.Power}: the egg did not visibly become a dragon in its own portrait.");
                    headerChanges.Add(sample.Power.ToString(), changed);
                    for (int header = 0; header < 3; header++)
                    {
                        double headerCenter = 500 + (header - 1) * 150 / aspect;
                        RequireSameRegion(beginning.Pixels, pixels, width, height, boardMap,
                            headerCenter - 70 / aspect, 96, 140 / aspect, 37,
                            $"{sample.Power}: hatching intruded into the static jackpot headers.");
                    }
                    RequireSameRegion(beginning.Pixels, pixels, width, height, boardMap, 0, 830, 1000, 170,
                        $"{sample.Power}: hatching changed the camera-observed bottom controls.");
                }
            }
        }
        RequireHatching(headerChanges.Count == 3 && scene.SlotsState.DragonHatches.Count == 3,
            "The rainbow feature did not hatch all three header dragons.");

        // Preserve all three portraits through the actual feature outro and idle,
        // then prove the next real spin preserves their first hatch records.
        for (int guard = 0; guard < 1000 && scene.SlotsState.Phase != SlotPhase.Idle; guard++)
        {
            var state = scene.SlotsState;
            AdvanceHatching(scene, ref now, state.PhaseStartedAt + state.PhaseDuration);
            RequireHatching(scene.SlotsState.DragonHatches.Count == 3 && hatches.All(expected =>
                scene.SlotsState.DragonHatches.Any(actual => actual == expected)),
                "A repeated effect or the feature outro restarted or removed a dragon.");
        }
        RequireHatching(scene.SlotsState.Phase == SlotPhase.Idle, "The hatch fixture never settled.");
        await SaveCurrent("all-three-idle");
        RequireHatching(scene.ActivateSlotsButton("slot-spin") && scene.SlotsState.DragonHatches.SequenceEqual(hatches),
            "The next spin removed or restarted a hatched header dragon.");
        await SaveCurrent("next-spin-dragons-retained");

        await CheckHatchAspect(2160, 2160, "all-three-square");
        await CheckHatchAspect(2160, 3840, "all-three-portrait");
        await CheckHatchAspect(2777, 2160, "all-three-measured-board");

        // Eight seconds show unhatched eggs, the three sequential rainbow powers,
        // and settled dragons while the real respin state continues advancing.
        var motionNow = origin;
        using var motionTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), motionWidth, motionHeight, 96);
        using var motionScene = new SceneCompositor(blackjackClock: () => motionNow, slots: new SlotGame(seed));
        ConfigureHatching(motionScene, motionWidth, motionHeight);
        Start(motionScene);
        var motionStart = firstHatch.AddSeconds(-1);
        var hatchProgress = new List<object>();
        int previousCount = -1;
        var frameMilliseconds = new double[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            AdvanceHatching(motionScene, ref motionNow, motionStart.AddSeconds(frame / (double)frameRate));
            long timestamp = Stopwatch.GetTimestamp();
            byte[] pixels = Draw(motionScene, motionTarget, motionWidth, motionHeight);
            frameMilliseconds[frame] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            RequireHatching(motionScene.SlotsArtworkReady, "Motion hatch artwork did not load: " + motionScene.SlotsArtworkError);
            var state = motionScene.SlotsState;
            RequireHatching(state.DragonHatches.Count >= previousCount,
                "The hatch count went backwards during the feature motion sequence.");
            if (state.DragonHatches.Count != previousCount)
            {
                hatchProgress.Add(new
                {
                    frame, seconds = Math.Round(frame / (double)frameRate, 4), count = state.DragonHatches.Count,
                    powers = state.DragonHatches.Select(hatch => hatch.Power.ToString()).ToArray()
                });
                previousCount = state.DragonHatches.Count;
            }
            CheckBlackMargin(pixels, motionWidth, motionHeight, $"motion-{frame}");
            string path = Path.Combine(motionDirectory, $"frame-{frame:D3}.png");
            await motionTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
        }
        RequireHatching(previousCount == 3, "The motion sequence ended before all three dragons hatched.");
        Array.Sort(frameMilliseconds);
        return new
        {
            passed = true, directory, images, seed, headerChanges,
            hatchEvents = hatches.Select(hatch => new { power = hatch.Power.ToString(), hatch.HatchedAt }).ToArray(),
            sameClockPixelsIdentical = true, jackpotHeadersAndControlsUnchanged = true, outputMarginsBlack = true,
            dragonsPersistThroughIdle = true, dragonsPersistThroughNextSpin = true,
            motion = new
            {
                directory = motionDirectory, filePattern = "frame-{index:D3}.png", width = motionWidth, height = motionHeight,
                frameRate, frameCount, durationSeconds = frameCount / (double)frameRate, hatchProgress,
                renderAndReadbackMedianMilliseconds = Math.Round((frameMilliseconds[95] + frameMilliseconds[96]) / 2, 2),
                renderAndReadbackP95Milliseconds = Math.Round(frameMilliseconds[(int)Math.Ceiling(frameCount * .95) - 1], 2)
            }
        };

        (int Seed, SlotDragonHatch[] Hatches) FindRainbow()
        {
            for (int candidateSeed = 0; candidateSeed < 128; candidateSeed++)
            {
                var candidate = new SlotGame(candidateSeed);
                candidate.Demonstrate(SlotDemo.Respins);
                candidate.HandleAction("slot-spin", origin);
                if (!candidate.Snapshot.Grid.Any(cell => cell.Symbol == SlotSymbol.EggRainbow)) continue;
                for (int guard = 0; guard < 400 && candidate.Phase != SlotPhase.Idle; guard++)
                {
                    var state = candidate.Snapshot;
                    if (state.DragonHatches.Count == 3)
                        return (candidateSeed, state.DragonHatches.OrderBy(hatch => hatch.HatchedAt).ToArray());
                    candidate.Tick(state.PhaseStartedAt + state.PhaseDuration);
                }
            }
            throw new InvalidOperationException("No complete rainbow hatching fixture appeared within 128 deterministic seeds.");
        }

        void Start(SceneCompositor fixture)
        {
            fixture.ShowSlots();
            fixture.DemonstrateSlots(SlotDemo.Respins);
            RequireHatching(fixture.ActivateSlotsButton("slot-spin"), "The rainbow fixture could not spin.");
        }

        async Task SaveCurrent(string name)
        {
            Draw(scene, target, width, height);
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }

        async Task CheckHatchAspect(int fixtureWidth, int fixtureHeight, string name)
        {
            var fixtureNow = origin;
            using var fixtureTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), fixtureWidth, fixtureHeight, 96);
            using var fixture = new SceneCompositor(blackjackClock: () => fixtureNow, slots: new SlotGame(seed));
            ConfigureHatching(fixture, fixtureWidth, fixtureHeight);
            Start(fixture);
            AdvanceHatching(fixture, ref fixtureNow, allResting);
            byte[] pixels = Draw(fixture, fixtureTarget, fixtureWidth, fixtureHeight);
            RequireHatching(fixture.SlotsArtworkReady && fixture.SlotsState.DragonHatches.Count == 3,
                $"{name}: three loaded dragon portraits were unavailable: {fixture.SlotsArtworkError}");
            CheckBlackMargin(pixels, fixtureWidth, fixtureHeight, name);
            string path = Path.Combine(directory, name + ".png");
            await fixtureTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }

        static void AdvanceHatching(SceneCompositor fixture, ref DateTimeOffset clock, DateTimeOffset targetTime)
        {
            RequireHatching(targetTime >= clock, "The hatching fixture attempted to rewind its game clock.");
            for (int guard = 0; guard < 1000; guard++)
            {
                var state = fixture.SlotsState;
                var deadline = state.PhaseStartedAt + state.PhaseDuration;
                if (state.Phase == SlotPhase.Idle || state.PhaseDuration <= TimeSpan.Zero || deadline > targetTime) break;
                clock = deadline;
                fixture.TickSlots(clock);
            }
            clock = targetTime;
            fixture.TickSlots(clock);
        }

        static Homography ConfigureHatching(SceneCompositor fixture, int fixtureWidth, int fixtureHeight)
        {
            fixture.SetDisplayAspect(fixtureWidth / (double)fixtureHeight);
            fixture.SetBoardFacingDegrees(0);
            fixture.SetBoardSetup(true);
            System.Numerics.Vector2[] physicalCorners =
                [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)];
            float inset = fixture.SetDetectedBoardGrid(physicalCorners,
                Homography.FromFourPoints([new(0, 0), new(fixtureWidth, 0), new(fixtureWidth, fixtureHeight), new(0, fixtureHeight)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            fixture.SetBoardSetup(false);
            // Rendering uses GridCorners, after BoardGrid's safety inset, rather
            // than the detected physical edges. Use the returned inset and the
            // same float interpolation as production before projecting regions.
            var center = physicalCorners.Aggregate(System.Numerics.Vector2.Zero, (sum, corner) => sum + corner) / 4;
            var safeCorners = physicalCorners.Select(corner => System.Numerics.Vector2.Lerp(corner, center, inset))
                .Select(corner => new Point2(corner.X, corner.Y)).ToArray();
            return Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], safeCorners);
        }

        static byte[] Draw(SceneCompositor fixture, CanvasRenderTarget renderTarget, int fixtureWidth, int fixtureHeight)
        {
            using (var drawing = renderTarget.CreateDrawingSession())
                fixture.Draw(drawing, fixtureWidth, fixtureHeight, preview: false, runningSlowly: false);
            return renderTarget.GetPixelBytes();
        }

        static (int Left, int Top, int Right, int Bottom) PixelRegion(int pixelWidth, int pixelHeight,
            Homography boardMap, double x, double y, double regionWidth, double regionHeight)
        {
            Point2[] logical = [new(x / 1000, y / 1000), new((x + regionWidth) / 1000, y / 1000),
                new((x + regionWidth) / 1000, (y + regionHeight) / 1000), new(x / 1000, (y + regionHeight) / 1000)];
            var projected = logical.Select(boardMap.Transform).ToArray();
            return ((int)Math.Ceiling(pixelWidth * projected.Min(point => point.X)),
                (int)Math.Ceiling(pixelHeight * projected.Min(point => point.Y)),
                (int)Math.Floor(pixelWidth * projected.Max(point => point.X)),
                (int)Math.Floor(pixelHeight * projected.Max(point => point.Y)));
        }

        static int ChangedRegion(byte[] before, byte[] after, int pixelWidth, int pixelHeight,
            Homography boardMap, double x, double y, double regionWidth, double regionHeight)
        {
            var region = PixelRegion(pixelWidth, pixelHeight, boardMap, x, y, regionWidth, regionHeight);
            int changed = 0;
            for (int row = region.Top; row < region.Bottom; row++)
                for (int column = region.Left; column < region.Right; column++)
                {
                    int index = (row * pixelWidth + column) * 4;
                    if (Math.Abs(before[index] - after[index]) + Math.Abs(before[index + 1] - after[index + 1]) +
                        Math.Abs(before[index + 2] - after[index + 2]) > 24) changed++;
                }
            return changed;
        }

        static void RequireSameRegion(byte[] before, byte[] after, int pixelWidth, int pixelHeight,
            Homography boardMap, double x, double y, double regionWidth, double regionHeight, string message)
        {
            var region = PixelRegion(pixelWidth, pixelHeight, boardMap, x, y, regionWidth, regionHeight);
            for (int row = region.Top; row < region.Bottom; row++)
            {
                int offset = (row * pixelWidth + region.Left) * 4;
                int length = (region.Right - region.Left) * 4;
                RequireHatching(before.AsSpan(offset, length).SequenceEqual(after.AsSpan(offset, length)), message);
            }
        }

        static void CheckBlackMargin(byte[] pixels, int pixelWidth, int pixelHeight, string name)
        {
            foreach (int row in new[] { 0, 1, pixelHeight - 2, pixelHeight - 1 })
                for (int column = 0; column < pixelWidth; column++) CheckPixel(row, column);
            for (int row = 2; row < pixelHeight - 2; row++)
            {
                CheckPixel(row, 0);
                CheckPixel(row, 1);
                CheckPixel(row, pixelWidth - 2);
                CheckPixel(row, pixelWidth - 1);
            }

            void CheckPixel(int row, int column)
            {
                int index = (row * pixelWidth + column) * 4;
                RequireHatching(pixels[index] <= 1 && pixels[index + 1] <= 1 && pixels[index + 2] <= 1,
                    $"{name}: the calibrated scene reached the outer output edge.");
            }
        }

        static void RequireHatching(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
