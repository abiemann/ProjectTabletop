#if DEBUG
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private sealed record SlotsWildFixture(int Seed, int Reel, int Row, int Count, SlotSnapshot Spin);

    // Natural seeded games, an independent rules oracle, and an injected clock
    // separate visual presentation from reel results, awards and phase timing.
    private async Task<object> VerifySlotsWildsAsync()
    {
        const int width = 3840, height = 2160, motionWidth = 1152, motionHeight = 896;
        const int frameRate = 24, frameCount = 144;
        var origin = new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
        SlotsWildFixture? lineWinFixture = null;
        var fixtures = FindFixtures();
        string directory = Path.Combine(_appDataDirectory, "SlotsWildVerification", Guid.NewGuid().ToString("N"));
        string motionDirectory = Path.Combine(directory, "motion");
        Directory.CreateDirectory(motionDirectory);
        var images = new List<string>();
        var visualChanges = new Dictionary<string, int>();
        int controlComparisons = 0, sameClockComparisons = 0, visibilityChecks = 0, lineWinToIdleChecks = 0;
        foreach (var fixture in fixtures.Values) CheckVisibility(fixture);
        if (lineWinFixture is not null && fixtures.Values.All(fixture => fixture.Seed != lineWinFixture.Seed))
            CheckVisibility(lineWinFixture);
        RequireWild(lineWinToIdleChecks > 0, "No natural WILD fixture exercised the LineWins-to-Idle transition.");

        await CaptureStages(fixtures[1], "single", [(.23, "landed"), (.5, "impact"), (.95, "settling"), (1.85, "resting")]);
        await CaptureStages(fixtures[2], "double", [(.23, "heads"), (.5, "roar-start"), (.75, "roar"),
            (1.05, "inferno"), (1.4, "revealing"), (1.85, "merged"), (2.5, "resting")]);
        await CaptureStages(fixtures[3], "triple", [(.5, "heads"), (1.05, "inferno"), (1.85, "merged")]);
        await CaptureAspect(fixtures[2], 2160, 2160, "double-square");
        await CaptureAspect(fixtures[3], 2160, 3840, "triple-portrait");
        await CaptureAspect(fixtures[2], 2777, 2160, "double-measured-board");

        var clock = origin;
        var motionFixture = fixtures[2];
        using var motionTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), motionWidth, motionHeight, 96);
        using var motionScene = new SceneCompositor(blackjackClock: () => clock, slots: new SlotGame(motionFixture.Seed));
        var motionMap = ConfigureWildScene(motionScene, motionWidth, motionHeight);
        var motionOracle = Start(motionScene, motionFixture.Seed);
        var phases = new HashSet<SlotPhase>();
        var renderMilliseconds = new double[frameCount];
        byte[]? previousPixels = null;
        SlotSnapshot? previousState = null;
        for (int frame = 0; frame < frameCount; frame++)
        {
            Advance(motionScene, motionOracle, ref clock, origin.AddSeconds(frame / (double)frameRate));
            var before = motionScene.SlotsState;
            long timestamp = Stopwatch.GetTimestamp();
            byte[] pixels = Draw(motionScene, motionTarget, motionWidth, motionHeight);
            renderMilliseconds[frame] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            // Save first so a native rendering failure retains its exact evidence.
            await motionTarget.SaveAsync(Path.Combine(motionDirectory, $"frame-{frame:D3}.png"), CanvasBitmapFileFormat.Png);
            RequireWild(motionScene.SlotsArtworkReady, "Motion WILD artwork did not load: " + motionScene.SlotsArtworkError);
            CheckGame(motionScene.SlotsState, motionOracle.Snapshot, $"motion frame {frame}");
            RequireWild(motionScene.SlotsState.Revision == before.Revision, "Drawing a motion frame advanced the rules state.");
            CheckMargins(pixels, motionWidth, motionHeight);
            if (frame % frameRate == 0)
            {
                RequireWild(pixels.AsSpan().SequenceEqual(Draw(motionScene, motionTarget, motionWidth, motionHeight)),
                    $"Motion frame {frame} was not deterministic at the same clock.");
                sameClockComparisons++;
            }
            if (previousState?.Revision == before.Revision && previousPixels is not null)
            {
                CheckCameraSafe(previousPixels, pixels, motionWidth, motionHeight, motionMap, $"motion frame {frame}");
                controlComparisons++;
            }
            previousPixels = pixels;
            previousState = before;
            phases.Add(before.Phase);
        }
        RequireWild(motionScene.SlotsState.Phase == SlotPhase.Idle && phases.Contains(SlotPhase.Spinning),
            "The motion sequence did not progress from spinning to idle.");
        RequireWild(SceneCompositor.SlotsWildPresentation(motionScene.SlotsState, clock).Any(run =>
            run.Reel == motionFixture.Reel && run.Row == motionFixture.Row && run.Count == 2 && run.Age > 1.7),
            "The merged two-cell WILD did not persist after the spin settled.");
        Array.Sort(renderMilliseconds);
        return new
        {
            passed = true, directory, images,
            fixtures = fixtures.Values.Select(fixture => new { fixture.Seed, fixture.Reel, fixture.Row, fixture.Count }).ToArray(),
            visibilityChecks, lineWinToIdleChecks, sameClockComparisons, controlComparisons, visualChanges,
            lineWinContinuitySeed = lineWinFixture?.Seed,
            naturalFirstSpins = true, rulesAndPayoutsUnchanged = true, phaseContinuity = true,
            outputMarginsBlack = true, artworkLoaded = true,
            motion = new
            {
                directory = motionDirectory, filePattern = "frame-{0:D3}.png", frameCount, frameRate,
                width = motionWidth, height = motionHeight, durationSeconds = frameCount / (double)frameRate,
                phases = phases.Select(phase => phase.ToString()).ToArray(),
                renderAndReadbackMedianMilliseconds = Math.Round((renderMilliseconds[71] + renderMilliseconds[72]) / 2, 2),
                renderAndReadbackP95Milliseconds = Math.Round(renderMilliseconds[(int)Math.Ceiling(frameCount * .95) - 1], 2)
            }
        };

        Dictionary<int, SlotsWildFixture> FindFixtures()
        {
            var found = new Dictionary<int, SlotsWildFixture>();
            for (int seed = 0; seed < 5000 && (found.Count < 3 || lineWinFixture is null); seed++)
            {
                var game = new SlotGame(seed);
                RequireWild(game.HandleAction("slot-spin", origin), "A natural WILD fixture could not spin.");
                var spin = game.Snapshot;
                var wanted = Runs(spin).Where(run => !found.ContainsKey(run.Count) ||
                    (lineWinFixture is null && spin.LineWins.Count > 0)).ToArray();
                if (wanted.Length == 0) continue;
                bool ordinary = true;
                for (int guard = 0; guard < 10 && game.Phase != SlotPhase.Idle; guard++)
                {
                    var state = game.Snapshot;
                    if (state.Phase is not (SlotPhase.Spinning or SlotPhase.LineWins)) { ordinary = false; break; }
                    game.Tick(state.PhaseStartedAt + state.PhaseDuration);
                }
                if (!ordinary || game.Phase != SlotPhase.Idle || game.Snapshot.SpinNumber != 1) continue;
                foreach (var run in wanted)
                    found.TryAdd(run.Count, new(seed, run.Reel, run.Row, run.Count, spin));
                if (lineWinFixture is null && spin.LineWins.Count > 0)
                {
                    var run = wanted[0];
                    lineWinFixture = new(seed, run.Reel, run.Row, run.Count, spin);
                }
            }
            RequireWild(found.Count == 3, "No natural single, two-cell and three-cell WILD fixtures appeared within 5000 first-spin seeds.");
            RequireWild(lineWinFixture is not null, "No natural winning WILD fixture appeared within 5000 first-spin seeds.");
            return found;
        }

        static List<(int Reel, int Row, int Count)> Runs(SlotSnapshot state)
        {
            var result = new List<(int Reel, int Row, int Count)>();
            for (int reel = 0; reel < SlotGame.Reels; reel++)
                for (int row = SlotGame.BaseFirstRow; row < SlotGame.BaseFirstRow + SlotGame.BaseRowCount;)
                {
                    if (state.Cell(reel, row).Symbol != SlotSymbol.Wild) { row++; continue; }
                    int first = row++;
                    while (row < SlotGame.BaseFirstRow + SlotGame.BaseRowCount && state.Cell(reel, row).Symbol == SlotSymbol.Wild) row++;
                    result.Add((reel, first, row - first));
                }
            return result;
        }

        void CheckVisibility(SlotsWildFixture fixture)
        {
            var state = fixture.Spin;
            RequireWild(SceneCompositor.SlotsWildPresentation(state, origin).Count == 0,
                "Future WILD results were visible at spin start.");
            foreach (var run in Runs(state))
            {
                var stop = state.SpinStartedAt + SlotGame.ReelStop(run.Reel, state.FreeSpin);
                RequireWild(SceneCompositor.SlotsWildPresentation(state, stop.AddMilliseconds(219)).All(item => item.Reel != run.Reel),
                    $"Seed {fixture.Seed}: a future WILD appeared before its reel finished landing.");
                var shown = SceneCompositor.SlotsWildPresentation(state, stop.AddMilliseconds(221));
                RequireWild(shown.Any(item => item.Reel == run.Reel && item.Row == run.Row && item.Count == run.Count &&
                    Math.Abs(item.Age - .221) < .001),
                    $"Seed {fixture.Seed}: a landed WILD run was missing or had the wrong age/extent.");
                visibilityChecks += 2;
            }
            var game = new SlotGame(fixture.Seed);
            game.HandleAction("slot-spin", origin);
            for (int transition = 0; transition < 2 && game.Phase != SlotPhase.Idle; transition++)
            {
                var prior = game.Snapshot;
                var deadline = prior.PhaseStartedAt + prior.PhaseDuration;
                var before = SceneCompositor.SlotsWildPresentation(prior, deadline.AddMilliseconds(-1));
                game.Tick(deadline);
                var current = game.Snapshot;
                var after = SceneCompositor.SlotsWildPresentation(current, deadline);
                RequireWild(current.SpinStartedAt == prior.SpinStartedAt && current.Grid.SequenceEqual(prior.Grid),
                    $"Seed {fixture.Seed}: {prior.Phase}-to-{current.Phase} changed the settled reel result or spin origin.");
                foreach (var run in before)
                    RequireWild(after.Any(item => item.Reel == run.Reel && item.Row == run.Row && item.Count == run.Count &&
                        Math.Abs(item.Age - run.Age - .001) < .0001),
                        $"Seed {fixture.Seed}: {prior.Phase}-to-{current.Phase} restarted or removed an already visible WILD.");
                if (prior.Phase == SlotPhase.LineWins)
                {
                    RequireWild(current.Phase == SlotPhase.Idle && after.Count == before.Count,
                        $"Seed {fixture.Seed}: LineWins-to-Idle changed the settled WILD runs.");
                    lineWinToIdleChecks++;
                }
                visibilityChecks++;
            }
            RequireWild(game.Phase == SlotPhase.Idle, $"Seed {fixture.Seed}: the ordinary WILD fixture did not settle.");
        }

        async Task CaptureStages(SlotsWildFixture fixture, string prefix, (double Age, string Name)[] stages)
        {
            var fixtureClock = origin;
            using var renderTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            using var native = new SceneCompositor(blackjackClock: () => fixtureClock, slots: new SlotGame(fixture.Seed));
            var map = ConfigureWildScene(native, width, height);
            var oracle = Start(native, fixture.Seed);
            byte[]? first = null, previous = null;
            SlotSnapshot? previousGame = null;
            foreach (var stage in stages)
            {
                Advance(native, oracle, ref fixtureClock, origin + SlotGame.ReelStop(fixture.Reel, false) + TimeSpan.FromSeconds(stage.Age));
                var state = native.SlotsState;
                byte[] pixels = Draw(native, renderTarget, width, height);
                string path = Path.Combine(directory, $"{prefix}-{stage.Name}.png");
                await renderTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
                RequireWild(native.SlotsArtworkReady, "WILD artwork did not load: " + native.SlotsArtworkError);
                RequireWild(pixels.AsSpan().SequenceEqual(Draw(native, renderTarget, width, height)),
                    $"{prefix}-{stage.Name}: the same clock produced different native pixels.");
                sameClockComparisons++;
                CheckGame(native.SlotsState, oracle.Snapshot, $"{prefix}-{stage.Name}");
                RequireWild(native.SlotsState.Revision == state.Revision, "Drawing a WILD changed the game revision.");
                CheckMargins(pixels, width, height);
                if (previous is not null && previousGame?.Revision == state.Revision)
                {
                    CheckCameraSafe(previous, pixels, width, height, map, $"{prefix}-{stage.Name}");
                    controlComparisons++;
                }
                if (first is null || (fixture.Count == 1 && stage.Name == "impact")) first = pixels;
                if (stage.Name is "merged" or "resting")
                {
                    var region = WildRegion(fixture, width / (double)height);
                    int changed = ChangedRegion(first, pixels, width, height, map, region.X, region.Y, region.Width, region.Height);
                    RequireWild(changed > 100, $"{prefix}: the WILD run did not visibly change inside its own reel cells.");
                    visualChanges[$"{prefix}-{stage.Name}"] = changed;
                }
                previous = pixels;
                previousGame = state;
            }
        }

        async Task CaptureAspect(SlotsWildFixture fixture, int fixtureWidth, int fixtureHeight, string name)
        {
            var fixtureClock = origin;
            using var renderTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), fixtureWidth, fixtureHeight, 96);
            using var native = new SceneCompositor(blackjackClock: () => fixtureClock, slots: new SlotGame(fixture.Seed));
            ConfigureWildScene(native, fixtureWidth, fixtureHeight);
            var oracle = Start(native, fixture.Seed);
            Advance(native, oracle, ref fixtureClock, origin + SlotGame.ReelStop(fixture.Reel, false) + TimeSpan.FromSeconds(1.85));
            byte[] pixels = Draw(native, renderTarget, fixtureWidth, fixtureHeight);
            string path = Path.Combine(directory, name + ".png");
            await renderTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            RequireWild(native.SlotsArtworkReady, $"{name}: WILD artwork did not load: {native.SlotsArtworkError}");
            CheckGame(native.SlotsState, oracle.Snapshot, name);
            CheckMargins(pixels, fixtureWidth, fixtureHeight);
            RequireWild(SceneCompositor.SlotsWildPresentation(native.SlotsState, fixtureClock).Any(run =>
                run.Reel == fixture.Reel && run.Row == fixture.Row && run.Count == fixture.Count),
                $"{name}: aspect adjustment changed the merged WILD extent.");
        }

        SlotGame Start(SceneCompositor native, int seed)
        {
            native.ShowSlots();
            var oracle = new SlotGame(seed);
            RequireWild(native.ActivateSlotsButton("slot-spin") && oracle.HandleAction("slot-spin", origin),
                "The WILD fixture could not start its natural spin.");
            CheckGame(native.SlotsState, oracle.Snapshot, "start");
            return oracle;
        }

        static void Advance(SceneCompositor native, SlotGame oracle, ref DateTimeOffset clock, DateTimeOffset targetTime)
        {
            RequireWild(targetTime >= clock, "The WILD fixture attempted to rewind time.");
            for (int guard = 0; guard < 30; guard++)
            {
                var state = native.SlotsState;
                var deadline = state.PhaseStartedAt + state.PhaseDuration;
                if (state.Phase == SlotPhase.Idle || state.PhaseDuration <= TimeSpan.Zero || deadline > targetTime) break;
                clock = deadline;
                native.TickSlots(clock);
                oracle.Tick(clock);
                CheckGame(native.SlotsState, oracle.Snapshot, "phase deadline");
            }
            clock = targetTime;
            native.TickSlots(clock);
            oracle.Tick(clock);
            CheckGame(native.SlotsState, oracle.Snapshot, "sample time");
        }

        static void CheckGame(SlotSnapshot actual, SlotSnapshot expected, string context)
        {
            RequireWild(actual.Phase == expected.Phase && actual.Revision == expected.Revision &&
                actual.PhaseStartedAt == expected.PhaseStartedAt && actual.SpinStartedAt == expected.SpinStartedAt &&
                actual.PhaseDuration == expected.PhaseDuration && actual.Balance == expected.Balance &&
                actual.Bet == expected.Bet && actual.RoundWin == expected.RoundWin && actual.SpinNumber == expected.SpinNumber &&
                actual.Grid.SequenceEqual(expected.Grid) && actual.PreviousGrid.SequenceEqual(expected.PreviousGrid) &&
                actual.Keys.SequenceEqual(expected.Keys) && actual.AvailableActions.SequenceEqual(expected.AvailableActions) &&
                WinSignature(actual).SequenceEqual(WinSignature(expected)) && !actual.InRespins && !actual.InFreeSpins,
                $"{context}: graphical WILD merging changed the ordinary game, paylines, balance or phase timing.");

            static IEnumerable<string> WinSignature(SlotSnapshot state) => state.LineWins.Select(win =>
                $"{win.Line}:{win.Symbol}:{win.Count}:{win.Amount}:{string.Join(";", win.Cells.Select(cell => $"{cell.Reel},{cell.Row}"))}");
        }

        static Homography ConfigureWildScene(SceneCompositor native, int fixtureWidth, int fixtureHeight)
        {
            native.SetDisplayAspect(fixtureWidth / (double)fixtureHeight);
            native.SetBoardFacingDegrees(0);
            native.SetBoardSetup(true);
            Vector2[] corners = [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)];
            float inset = native.SetDetectedBoardGrid(corners,
                Homography.FromFourPoints([new(0, 0), new(fixtureWidth, 0), new(fixtureWidth, fixtureHeight), new(0, fixtureHeight)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            native.SetBoardSetup(false);
            var center = corners.Aggregate(Vector2.Zero, (sum, corner) => sum + corner) / 4;
            var safe = corners.Select(corner => Vector2.Lerp(corner, center, inset)).Select(corner => new Point2(corner.X, corner.Y)).ToArray();
            return Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], safe);
        }

        static byte[] Draw(SceneCompositor native, CanvasRenderTarget renderTarget, int fixtureWidth, int fixtureHeight)
        {
            using (var drawing = renderTarget.CreateDrawingSession())
                native.Draw(drawing, fixtureWidth, fixtureHeight, preview: false, runningSlowly: false);
            return renderTarget.GetPixelBytes();
        }

        static (double X, double Y, double Width, double Height) WildRegion(SlotsWildFixture fixture, double aspect)
        {
            double cell = Math.Min(140 / Math.Clamp(aspect, .6, 2.5), 118);
            return (500 - cell * 2.5 + fixture.Reel * cell, 255 + (fixture.Row - SlotGame.BaseFirstRow) * 370.0 / 3,
                cell, fixture.Count * 370.0 / 3);
        }

        static (int Left, int Top, int Right, int Bottom) PixelRegion(int pixelWidth, int pixelHeight,
            Homography map, double x, double y, double regionWidth, double regionHeight)
        {
            var first = map.Transform(new(x / 1000, y / 1000));
            var last = map.Transform(new((x + regionWidth) / 1000, (y + regionHeight) / 1000));
            return ((int)Math.Ceiling(first.X * pixelWidth), (int)Math.Ceiling(first.Y * pixelHeight),
                (int)Math.Floor(last.X * pixelWidth), (int)Math.Floor(last.Y * pixelHeight));
        }

        static int ChangedRegion(byte[] before, byte[] after, int pixelWidth, int pixelHeight,
            Homography map, double x, double y, double regionWidth, double regionHeight)
        {
            var region = PixelRegion(pixelWidth, pixelHeight, map, x, y, regionWidth, regionHeight);
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

        static void CheckCameraSafe(byte[] before, byte[] after, int pixelWidth, int pixelHeight, Homography map, string context)
        {
            foreach (var (top, extent) in new[] { (0.0, 84.0), (830.0, 170.0) })
            {
                var region = PixelRegion(pixelWidth, pixelHeight, map, 0, top, 1000, extent);
                for (int row = region.Top; row < region.Bottom; row++)
                {
                    int offset = (row * pixelWidth + region.Left) * 4, length = (region.Right - region.Left) * 4;
                    RequireWild(before.AsSpan(offset, length).SequenceEqual(after.AsSpan(offset, length)),
                        $"{context}: reel effects changed the static title or camera-observed controls within one game phase.");
                }
            }
        }

        static void CheckMargins(byte[] pixels, int pixelWidth, int pixelHeight)
        {
            foreach (int row in new[] { 0, pixelHeight - 1 })
                for (int column = 0; column < pixelWidth; column++) Check(row, column);
            for (int row = 1; row < pixelHeight - 1; row++) { Check(row, 0); Check(row, pixelWidth - 1); }
            void Check(int row, int column)
            {
                int index = (row * pixelWidth + column) * 4;
                RequireWild(pixels[index] <= 1 && pixels[index + 1] <= 1 && pixels[index + 2] <= 1,
                    "The WILD presentation escaped the calibrated board onto an output edge.");
            }
        }

        static void RequireWild(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
