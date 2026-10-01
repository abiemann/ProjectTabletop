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
    private sealed record SlotsWildPixels(int Left, int Top, int Width, int Height, byte[] Pixels);

    // Natural seeded games, an independent rules oracle, and an injected clock
    // separate visual presentation from reel results, awards and phase timing.
    private async Task<object> VerifySlotsWildsAsync()
    {
        const int width = 3840, height = 2160, motionWidth = 1152, motionHeight = 896;
        const int frameRate = 24, frameCount = 192;
        var origin = new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
        SlotsWildFixture? lineWinFixture = null;
        var fixtures = FindFixtures();
        string directory = Path.Combine(_appDataDirectory, "SlotsWildVerification", Guid.NewGuid().ToString("N"));
        string motionDirectory = Path.Combine(directory, "motion");
        Directory.CreateDirectory(motionDirectory);
        var images = new List<string>();
        var visualChanges = new Dictionary<string, int>();
        var flameCoverage = new Dictionary<string, object>();
        var flameAftercare = new Dictionary<string, object>();
        int controlComparisons = 0, sameClockComparisons = 0, visibilityChecks = 0, lineWinToIdleChecks = 0;
        foreach (var fixture in fixtures.Values) CheckVisibility(fixture);
        if (lineWinFixture is not null && fixtures.Values.All(fixture => fixture.Seed != lineWinFixture.Seed))
            CheckVisibility(lineWinFixture);
        RequireWild(lineWinToIdleChecks > 0, "No natural WILD fixture exercised the LineWins-to-Idle transition.");

        await CaptureStages(fixtures[1], "single", [(.23, "landed"), (.5, "impact"), (.95, "settling"), (1.85, "resting")]);
        (double Age, string Name)[] mergeStages = [(.23, "heads"), (.5, "roar-start"), (.9, "independent-breath"),
            (1.2, "breath-held"), (1.5, "coalescing"), (1.72, "inferno"), (1.85, "inferno-held"),
            (2.1, "revealing"), (2.55, "merged"), (2.9, "sides-held"), (3.65, "sides-held-late"),
            (4.4, "sides-decaying"), (5.35, "extinguished"), (5.85, "extinguished-followup")];
        await CaptureStages(fixtures[2], "double", mergeStages);
        await CaptureStages(fixtures[3], "triple", mergeStages);
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
            run.Reel == motionFixture.Reel && run.Row == motionFixture.Row && run.Count == 2 && run.Age > 2.55),
            "The merged two-cell WILD did not persist after the spin settled.");
        Array.Sort(renderMilliseconds);
        return new
        {
            passed = true, directory, images,
            fixtures = fixtures.Values.Select(fixture => new { fixture.Seed, fixture.Reel, fixture.Row, fixture.Count }).ToArray(),
            visibilityChecks, lineWinToIdleChecks, sameClockComparisons, controlComparisons, visualChanges, flameCoverage, flameAftercare,
            lineWinContinuitySeed = lineWinFixture?.Seed,
            naturalFirstSpins = true, rulesAndPayoutsUnchanged = true, phaseContinuity = true,
            outputMarginsBlack = true, artworkLoaded = true,
            motion = new
            {
                directory = motionDirectory, filePattern = "frame-{0:D3}.png", frameCount, frameRate,
                width = motionWidth, height = motionHeight, durationSeconds = frameCount / (double)frameRate,
                phases = phases.Select(phase => phase.ToString()).ToArray(),
                renderAndReadbackMedianMilliseconds = Math.Round((renderMilliseconds[(frameCount - 1) / 2] + renderMilliseconds[frameCount / 2]) / 2, 2),
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
            double? breathCoverage = null;
            double[]? independentBreathSeams = null;
            var aftercareFrames = new Dictionary<string, SlotsWildPixels>();
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
                if (fixture.Count > 1 && stage.Name is ("independent-breath" or "breath-held" or "coalescing" or "inferno" or "inferno-held" or "revealing"))
                {
                    var coverage = AddedFlameCoverage(first, pixels, width, height, map, fixture);
                    flameCoverage[$"{prefix}-{stage.Name}"] = new
                    {
                        addedWarmFraction = Math.Round(coverage.Whole, 4),
                        verticalBands = coverage.VerticalBands.Select(value => Math.Round(value, 4)).ToArray(),
                        headCells = coverage.HeadCells.Select(value => Math.Round(value, 4)).ToArray(),
                        seams = coverage.Seams.Select(value => Math.Round(value, 4)).ToArray()
                    };
                    if (stage.Name is "independent-breath" or "breath-held")
                        RequireWild(coverage.HeadCells.All(value => value > .015),
                            $"{prefix}-{stage.Name}: a stacked head did not produce a visible bright breath region " +
                            $"(cell coverage: {string.Join(", ", coverage.HeadCells.Select(value => value.ToString("P1")))}).");
                    // Compare seam growth against the separate jets, before
                    // the late swelling stage deliberately crosses each seam.
                    if (stage.Name == "independent-breath") independentBreathSeams = coverage.Seams;
                    if (stage.Name == "breath-held") breathCoverage = coverage.Whole;
                    if (stage.Name is "inferno" or "inferno-held")
                    {
                        RequireWild(coverage.Whole > .30 && coverage.VerticalBands.All(value => value > .10),
                            $"{prefix}-{stage.Name}: the bright flame wall did not cover the full stacked height " +
                            $"(total: {coverage.Whole:P1}; bands: {string.Join(", ", coverage.VerticalBands.Select(value => value.ToString("P1")))}).");
                        RequireWild(breathCoverage is not null && coverage.Whole > breathCoverage + .05 &&
                            independentBreathSeams is not null && coverage.Seams.Zip(independentBreathSeams, (wall, breath) => wall - breath).All(gain => gain > .05),
                            $"{prefix}-{stage.Name}: the individual breaths did not grow into a joined flame wall " +
                            $"(breath: {breathCoverage:P1}; wall: {coverage.Whole:P1}; wall seams: " +
                            $"{string.Join(", ", coverage.Seams.Select(value => value.ToString("P1")))}).");
                    }
                    if (stage.Name == "inferno-held" && previous is not null)
                    {
                        var region = WildRegion(fixture, width / (double)height);
                        int changed = ChangedRegion(previous, pixels, width, height, map, region.X, region.Y, region.Width, region.Height);
                        RequireWild(changed > 100, $"{prefix}: the held flame wall stopped visibly animating.");
                        visualChanges[$"{prefix}-inferno-held"] = changed;
                    }
                }
                if (stage.Name is "merged" or "resting" or "extinguished")
                {
                    var region = WildRegion(fixture, width / (double)height);
                    int changed = ChangedRegion(first, pixels, width, height, map, region.X, region.Y, region.Width, region.Height);
                    RequireWild(changed > 100, $"{prefix}: the WILD run did not visibly change inside its own reel cells.");
                    visualChanges[$"{prefix}-{stage.Name}"] = changed;
                }
                if (fixture.Count > 1 && stage.Name is ("inferno-held" or "merged" or "sides-held" or "sides-held-late" or
                    "sides-decaying" or "extinguished" or "extinguished-followup"))
                    aftercareFrames[stage.Name] = CopyFlameArea(pixels, width, height, map, fixture);
                previous = pixels;
                previousGame = state;
            }
            if (fixture.Count > 1) CheckAftercare();

            void CheckAftercare()
            {
                var baseline = aftercareFrames["extinguished-followup"];
                var run = WildRegion(fixture, width / (double)height);
                // The strips straddle the run's sides; spill probes are wholly
                // outside its original cells. The centre omits the WILD legend.
                var left = Region(-.06, .04, .25, .90);
                var right = Region(.81, .04, .25, .90);
                var center = Region(.32, .14, .36, .65);
                var spill = new[] { Region(-.07, .08, .055, .84), Region(1.015, .08, .055, .84), Region(.12, -.045, .76, .025) };
                var held = Measure(aftercareFrames["sides-held"]);
                var late = Measure(aftercareFrames["sides-held-late"]);
                var decaying = Measure(aftercareFrames["sides-decaying"]);
                var ended = Measure(aftercareFrames["extinguished"]);
                int spillPixels = spill.Sum(region => AddedWarm(aftercareFrames["inferno-held"], region).Count);
                int lateMovement = MovingWarm(aftercareFrames["sides-held"], aftercareFrames["sides-held-late"], left)
                    + MovingWarm(aftercareFrames["sides-held"], aftercareFrames["sides-held-late"], right);
                var finishingJet = MeasureFinishingJet();
                flameAftercare[prefix] = new
                {
                    spillPixels, lateMovement, finishingJet,
                    held = Summary(held), late = Summary(late), decaying = Summary(decaying), extinguished = Summary(ended),
                    baseline = "extinguished-followup", baselineAge = 5.85
                };
                RequireWild(spillPixels > 100, $"{prefix}: the inferno did not visibly spill outside its original cells ({spillPixels} pixels).");
                foreach (var sample in new[] { (Name: "sides-held", Value: held), (Name: "sides-held-late", Value: late) })
                {
                    RequireWild(sample.Value.Left > .015 && sample.Value.Right > .015,
                        $"{prefix}-{sample.Name}: both sides did not retain fire after the central jet ended " +
                        $"(left {sample.Value.Left:P1}, right {sample.Value.Right:P1}).");
                    RequireWild(sample.Value.Center < .03,
                        $"{prefix}-{sample.Name}: flame still obscured the guardian's cleared centre ({sample.Value.Center:P1}).");
                }
                RequireWild(lateMovement > 100, $"{prefix}: the lingering side flames stopped moving ({lateMovement} changed warm pixels).");
                RequireWild(decaying.SidePixels < late.SidePixels * .85,
                    $"{prefix}: the side flames did not visibly diminish during decay ({late.SidePixels} to {decaying.SidePixels} warm pixels).");
                RequireWild(ended.Left < .01 && ended.Right < .01 && ended.Center < .01 && ended.SidePixels < late.SidePixels * .10,
                    $"{prefix}: fire remained at age 5.35 (left {ended.Left:P1}, right {ended.Right:P1}, centre {ended.Center:P1}).");

                object MeasureFinishingJet()
                {
                    // Sample only the downstream centre of the existing 2.55s
                    // capture. Side wisps are outside this crop. Subtracting a
                    // best-fit line distinguishes curvature from a tilted jet;
                    // no shader frequency, phase or wave formula is duplicated.
                    var sample = aftercareFrames["merged"];
                    var bounds = Region(0, 0, 1, 1);
                    double middle = (bounds.Left + bounds.Right) / 2.0;
                    double span = bounds.Right - bounds.Left;
                    var centers = new double[10];
                    var warmPixels = new int[centers.Length];
                    for (int band = 0; band < centers.Length; band++)
                    {
                        var region = Region(.25, .30 + band * .05, .50, .05);
                        long sumX = 0;
                        for (int y = region.Top; y < region.Bottom; y++)
                            for (int x = region.Left; x < region.Right; x++)
                                if (IsAddedWarm(sample, x, y)) { warmPixels[band]++; sumX += x; }
                        RequireWild(warmPixels[band] > 40,
                            $"{prefix}: the finishing jet was missing from downstream band {band} ({warmPixels[band]} warm pixels).");
                        centers[band] = (sumX / (double)warmPixels[band] - middle) / span;
                    }
                    double mean = centers.Average(), midpoint = (centers.Length - 1) / 2.0;
                    double slope = Enumerable.Range(0, centers.Length).Sum(index => (index - midpoint) * (centers[index] - mean))
                        / Enumerable.Range(0, centers.Length).Sum(index => (index - midpoint) * (index - midpoint));
                    var residuals = centers.Select((value, index) => value - mean - slope * (index - midpoint)).ToArray();
                    double bend = residuals.Max() - residuals.Min();
                    RequireWild(bend > .025,
                        $"{prefix}: the finishing jet remained visually straight (nonlinear bend {bend:P2} of cell width).");
                    return new
                    {
                        age = 2.55, nonlinearBendFraction = Math.Round(bend, 4),
                        centerOffsets = centers.Select(value => Math.Round(value, 4)).ToArray(), warmPixels
                    };
                }

                (int Left, int Top, int Right, int Bottom) Region(double x, double y, double w, double h)
                    => PixelRegion(width, height, map, run.X + x * run.Width, run.Y + y * run.Height, w * run.Width, h * run.Height);

                (double Left, double Right, double Center, int SidePixels) Measure(SlotsWildPixels sample)
                {
                    var l = AddedWarm(sample, left);
                    var r = AddedWarm(sample, right);
                    return (l.Fraction, r.Fraction, AddedWarm(sample, center).Fraction, l.Count + r.Count);
                }

                static object Summary((double Left, double Right, double Center, int SidePixels) value)
                    => new { left = Math.Round(value.Left, 4), right = Math.Round(value.Right, 4), center = Math.Round(value.Center, 4), value.SidePixels };

                (int Count, double Fraction) AddedWarm(SlotsWildPixels sample, (int Left, int Top, int Right, int Bottom) region)
                {
                    int count = 0, total = (region.Right - region.Left) * (region.Bottom - region.Top);
                    for (int y = region.Top; y < region.Bottom; y++)
                        for (int x = region.Left; x < region.Right; x++)
                            if (IsAddedWarm(sample, x, y)) count++;
                    return (count, total > 0 ? count / (double)total : 0);
                }

                int MovingWarm(SlotsWildPixels first, SlotsWildPixels second, (int Left, int Top, int Right, int Bottom) region)
                {
                    int changed = 0;
                    for (int y = region.Top; y < region.Bottom; y++)
                        for (int x = region.Left; x < region.Right; x++)
                        {
                            int index = ((y - first.Top) * first.Width + x - first.Left) * 4;
                            if ((IsAddedWarm(first, x, y) || IsAddedWarm(second, x, y)) &&
                                Math.Abs(first.Pixels[index] - second.Pixels[index]) +
                                Math.Abs(first.Pixels[index + 1] - second.Pixels[index + 1]) +
                                Math.Abs(first.Pixels[index + 2] - second.Pixels[index + 2]) > 30) changed++;
                        }
                    return changed;
                }

                bool IsAddedWarm(SlotsWildPixels sample, int x, int y)
                {
                    int index = ((y - sample.Top) * sample.Width + x - sample.Left) * 4;
                    int warmth = Warmth(sample.Pixels, index);
                    if (warmth < 160) return false;
                    // Compare with the same opaque guardian after extinction.
                    // A small neighbourhood rejects its breathing edges, while
                    // chroma rejects silver highlights and the blue live aura.
                    int reference = 0;
                    for (int dy = -6; dy <= 6; dy += 3)
                        for (int dx = -6; dx <= 6; dx += 3)
                        {
                            int column = Math.Clamp(x - baseline.Left + dx, 0, baseline.Width - 1);
                            int row = Math.Clamp(y - baseline.Top + dy, 0, baseline.Height - 1);
                            reference = Math.Max(reference, Warmth(baseline.Pixels, (row * baseline.Width + column) * 4));
                        }
                    return warmth > reference + 45;
                }

                static int Warmth(byte[] pixels, int index)
                {
                    int blue = pixels[index], green = pixels[index + 1], red = pixels[index + 2];
                    return red >= 160 && green >= 65 && red >= blue + 45 ? red + green - 2 * blue : 0;
                }
            }
        }

        async Task CaptureAspect(SlotsWildFixture fixture, int fixtureWidth, int fixtureHeight, string name)
        {
            var fixtureClock = origin;
            using var renderTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), fixtureWidth, fixtureHeight, 96);
            using var native = new SceneCompositor(blackjackClock: () => fixtureClock, slots: new SlotGame(fixture.Seed));
            ConfigureWildScene(native, fixtureWidth, fixtureHeight);
            var oracle = Start(native, fixture.Seed);
            Advance(native, oracle, ref fixtureClock, origin + SlotGame.ReelStop(fixture.Reel, false) + TimeSpan.FromSeconds(2.7));
            byte[] pixels = Draw(native, renderTarget, fixtureWidth, fixtureHeight);
            string path = Path.Combine(directory, name + ".png");
            await renderTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            RequireWild(native.SlotsArtworkReady, $"{name}: WILD artwork did not load: {native.SlotsArtworkError}");
            CheckGame(native.SlotsState, oracle.Snapshot, name);
            CheckMargins(pixels, fixtureWidth, fixtureHeight);
            RequireWild(SceneCompositor.SlotsWildPresentation(native.SlotsState, fixtureClock).Any(run =>
                run.Reel == fixture.Reel && run.Row == fixture.Row && run.Count == fixture.Count && run.Age > 2.55),
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

        static SlotsWildPixels CopyFlameArea(byte[] pixels, int pixelWidth, int pixelHeight, Homography map, SlotsWildFixture fixture)
        {
            var run = WildRegion(fixture, pixelWidth / (double)pixelHeight);
            var region = PixelRegion(pixelWidth, pixelHeight, map, run.X - run.Width * .16, run.Y - run.Height * .08,
                run.Width * 1.32, run.Height * 1.13);
            int cropWidth = region.Right - region.Left, cropHeight = region.Bottom - region.Top;
            var copy = new byte[cropWidth * cropHeight * 4];
            for (int row = 0; row < cropHeight; row++)
                pixels.AsSpan(((region.Top + row) * pixelWidth + region.Left) * 4, cropWidth * 4)
                    .CopyTo(copy.AsSpan(row * cropWidth * 4, cropWidth * 4));
            return new(region.Left, region.Top, cropWidth, cropHeight, copy);
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

        static (double Whole, double[] VerticalBands, double[] HeadCells, double[] Seams) AddedFlameCoverage(
            byte[] heads, byte[] frame, int pixelWidth, int pixelHeight, Homography map, SlotsWildFixture fixture)
        {
            // Measure the visible result, excluding the gold border and requiring
            // a brightness increase over the initial heads. This does not depend
            // on the shader's noise, envelopes, particle positions or jet shape.
            var run = WildRegion(fixture, pixelWidth / (double)pixelHeight);
            double left = run.X + run.Width * .14, coreWidth = run.Width * .72;
            double top = run.Y + run.Height * .035, coreHeight = run.Height * .93;
            double cellHeight = run.Height / fixture.Count;
            var bands = Enumerable.Range(0, fixture.Count * 3)
                .Select(index => Fraction(top + coreHeight * index / (fixture.Count * 3), coreHeight / (fixture.Count * 3))).ToArray();
            var cells = Enumerable.Range(0, fixture.Count)
                .Select(index => Fraction(run.Y + cellHeight * (index + .06), cellHeight * .88)).ToArray();
            var seams = Enumerable.Range(1, fixture.Count - 1)
                .Select(index => Fraction(run.Y + cellHeight * (index - .045), cellHeight * .09)).ToArray();
            return (Fraction(top, coreHeight), bands, cells, seams);

            double Fraction(double regionTop, double regionHeight)
            {
                var region = PixelRegion(pixelWidth, pixelHeight, map, left, regionTop, coreWidth, regionHeight);
                int warm = 0, count = (region.Right - region.Left) * (region.Bottom - region.Top);
                for (int row = region.Top; row < region.Bottom; row++)
                    for (int column = region.Left; column < region.Right; column++)
                    {
                        int index = (row * pixelWidth + column) * 4;
                        // CanvasRenderTarget's default byte format is BGRA8.
                        int blue = frame[index], green = frame[index + 1], red = frame[index + 2];
                        int gain = blue + green + red - heads[index] - heads[index + 1] - heads[index + 2];
                        bool flameColor = red >= 160 && green >= 70 &&
                            (red >= blue + 18 || (red >= 230 && green >= 230 && blue >= 230));
                        if (flameColor && gain >= 60) warm++;
                    }
                return count > 0 ? warm / (double)count : 0;
            }
        }

        static void CheckCameraSafe(byte[] before, byte[] after, int pixelWidth, int pixelHeight, Homography map, string context)
        {
            // Only the three jackpot plaques are static in the header band;
            // the surrounding illustrated eye and marquee can animate.
            double aspect = Math.Clamp(pixelWidth / (double)pixelHeight, .6, 2.5);
            var regions = new List<(double X, double Y, double Width, double Height)> { (0, 830, 1000, 170) };
            for (int header = 0; header < 3; header++)
                regions.Add((500 + (header - 1) * 150 / aspect - 70 / aspect, 96, 140 / aspect, 37));
            foreach (var bounds in regions)
            {
                var region = PixelRegion(pixelWidth, pixelHeight, map, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                for (int row = region.Top; row < region.Bottom; row++)
                {
                    int offset = (row * pixelWidth + region.Left) * 4, length = (region.Right - region.Left) * 4;
                    RequireWild(before.AsSpan(offset, length).SequenceEqual(after.AsSpan(offset, length)),
                        $"{context}: effects changed the static jackpot headers or camera-observed controls within one game phase.");
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
