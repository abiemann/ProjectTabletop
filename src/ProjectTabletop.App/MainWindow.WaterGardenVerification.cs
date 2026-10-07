#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Synthetic camera coordinates and private scenes keep this GPU check away
    // from the user's live camera, projector, registration and water field.
    private async Task<object> VerifyWaterGardenAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.GetBoardFacingDegrees());
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        using (var navigation = new SceneCompositor(waterClock: () => now))
        {
            var opened = new List<BoardScreen>();
            navigation.BoardOpened += opened.Add;
            navigation.ShowWaterGarden();
            Require(navigation.ActivateWaterGardenButton("water-garden-calm") &&
                    opened.SequenceEqual(new[] { BoardScreen.WaterGarden }),
                "Calm Water replayed the board-entry notification that enables remembered stick tracking.");
            navigation.ShowBoardMenu();
            navigation.ShowWaterGarden();
            Require(opened.SequenceEqual(new[] { BoardScreen.WaterGarden, BoardScreen.Menu, BoardScreen.WaterGarden }),
                "The compositor failed to forward each deliberate board entry exactly once.");
        }
        string directory = Path.Combine(_appDataDirectory, "WaterGardenVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var aspects = new List<object>();
        var perspectiveTouches = new List<object>();
        int perspectiveRoundTrips = 0;
        foreach (float aspect in new[] { 1f, 16f / 9, 9f / 16 })
        foreach (var surface in new Vector2[] { new(.08f, .08f), new(.83f, .17f), new(.5f, .5f), new(.17f, .83f), new(.91f, .89f) })
        {
            var screen = WaterGardenView.SurfaceToScreen(surface);
            var world = new Vector3((surface.X - .5f) * aspect, surface.Y - .5f, 0);
            var projected = WaterGardenView.Project(world, aspect);
            Require(Vector2.Distance(WaterGardenView.ScreenToSurface(screen), surface) < 1e-5 &&
                    Vector2.Distance(projected, screen) < 1e-5 &&
                    Vector2.Distance(WaterGardenView.ScreenToSurface(projected), surface) < 1e-5,
                $"The oblique garden camera failed its {aspect:G4} aspect round trip at {surface}.");
            Require(WaterGardenView.Project(world + new Vector3(0, 0, .08f), aspect).Y < projected.Y,
                "Raised garden geometry did not rise above its water-plane position.");
            perspectiveRoundTrips++;
        }
        var farLeft = WaterGardenView.SurfaceToScreen(new(0, 0));
        var farRight = WaterGardenView.SurfaceToScreen(new(1, 0));
        var nearLeft = WaterGardenView.SurfaceToScreen(new(0, 1));
        var nearRight = WaterGardenView.SurfaceToScreen(new(1, 1));
        Require(nearRight.X - nearLeft.X > farRight.X - farLeft.X &&
                nearLeft.Y > farLeft.Y && farLeft.Y > .1 && nearLeft.Y < .85,
            "The garden camera lost its foreshortened, close oblique framing.");
        Require(!WaterGardenView.ContainsWater(new(.04f, .22f)) &&
                !WaterGardenView.ContainsWater(WaterGardenView.SurfaceToScreen(new(.5f, .98f))) &&
                !WaterGardenView.ContainsWater(new(float.NaN, .5f)),
            "The water hit region accepted a point outside the trapezoid, behind the near rim, or non-finite.");
        var device = CanvasDevice.GetSharedDevice();
        double drawMilliseconds = 0, readbackMilliseconds = 0;
        int renderedFrames = 0;
        using var target = new CanvasRenderTarget(device, 1000, 1000, 96);
        using var preview = new CanvasRenderTarget(device, 400, 300, 96);
        using var scene = NewScene(1, out double inset);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
        Draw(scene, target);
        var projectedEyeFrames = scene.GetEyeTipProjectionFrames();
        Require(projectedEyeFrames.Count == 1 && projectedEyeFrames[0].Width <= 2048 &&
                projectedEyeFrames[0].Height <= 2048 && projectedEyeFrames[0].CameraToBoard.Count == 9 &&
                ReferenceEquals(projectedEyeFrames[0], scene.GetEyeTipProjectionFrames()[0]),
            "Eye tracking did not retain a bounded, calibrated projection reference between camera samples.");
        var water = Simulation(scene);
        water.SetAmbientEnabledForVerification(false);
        water.SetFountainEnabledForVerification(false);
        now += TimeSpan.FromSeconds(1.0 / 60);
        Draw(scene, target);
        var blank = Pixels(target);
        var calm = Statistics(water);
        var initialDucks = DuckStates(water);
        Require(IsFinite(calm) && calm.TotalEnergy == 0 && calm.MaximumHeight == 0,
            "A new Water Garden did not create a finite, calm GPU field.");
        Require(initialDucks.Count == 10 && initialDucks.All(DuckIsFiniteAndInside),
            "A new Water Garden did not create ten finite floating ducks inside the basin.");
        var visibleDuckPixels = initialDucks.Select(duck => YellowPixels(blank,
            WaterGardenView.SurfaceToScreen(new(duck.SurfaceX, duck.SurfaceY)))).ToArray();
        Require(visibleDuckPixels.All(count => count > 4),
            $"The native calm render did not show every yellow duck: {string.Join(", ", visibleDuckPixels)} pixels.");
        Require(scene.CurrentBoardButtons.Count == 2 && scene.CurrentBoardButtons.All(button => button.Hold == BoardButtonHold.Once),
            "Water Garden must expose only its two stationary long-press controls.");
        await Save(target, "water-square-calm");

        now += TimeSpan.FromSeconds(1.0 / 60);
        Require(scene.SetWaterStickTip(CameraPoint(.5, .5, inset), now),
            "The calibrated eye-tip fixture was not accepted.");
        Draw(scene, target);
        var injected = Statistics(water);
        Require(IsFinite(injected) && injected.MaximumHeight > .0001 && injected.TotalEnergy > 0,
            "A confirmed eye tip did not disturb the GPU water field.");
        var firstPixels = Pixels(target);
        Require(ChangedPixels(blank, firstPixels, new(.35, .35, .30, .30)) > 20,
            "The GPU disturbance did not change the visible water.");
        var firstDiagnostics = water.GetDiagnostics();
        var firstDucks = DuckStates(water);
        long firstRevision = water.Revision;
        Draw(scene, preview, isPreview: true);
        DrawRawPreview(scene, preview);
        Draw(scene, target);
        Require(ReferenceEquals(water, Simulation(scene)) && water.Revision == firstRevision &&
                water.GetDiagnostics().SimulationSteps == firstDiagnostics.SimulationSteps &&
                firstDucks.SequenceEqual(DuckStates(water)) &&
                firstPixels.SequenceEqual(Pixels(target)),
            "A same-clock preview/output draw advanced or changed the water field or its floating ducks twice.");
        await Save(target, "water-square-touch");
        scene.SetWaterStickTip(null, now);

        var propagationProbe = WaterGardenView.ScreenToSurface(new(.5f, .5f)) + new Vector2(.065f, 0);
        double propagatedHeight = 0;
        WaterGardenFieldStatistics? propagating = null;
        for (int frame = 1; frame <= 150; frame++)
        {
            now += TimeSpan.FromSeconds(1.0 / 60);
            Draw(scene, target);
            if (frame <= 45 && frame % 5 == 0)
            {
                var timer = Stopwatch.StartNew();
                var probe = water.GetFieldProbe(propagationProbe);
                readbackMilliseconds += timer.Elapsed.TotalMilliseconds;
                Require(double.IsFinite(probe.Height) && double.IsFinite(probe.Velocity),
                    "The propagating wave probe became non-finite.");
                propagatedHeight = Math.Max(propagatedHeight, Math.Abs(probe.Height));
            }
            if (frame == 45)
            {
                propagating = Statistics(water);
                await Save(target, "water-square-ripples");
            }
        }
        var decayed = Statistics(water);
        Require(propagatedHeight > 1e-7 && propagating is not null && IsFinite(propagating) && IsFinite(decayed) &&
                decayed.TotalEnergy < injected.TotalEnergy,
            $"The wave did not propagate and dissipate: neighbor={propagatedHeight:G6}, " +
            $"energy={injected.TotalEnergy:G6}->{decayed.TotalEnergy:G6}.");
        var animated = Pixels(target);
        foreach (var button in scene.CurrentBoardButtons)
        {
            var b = button.Bounds;
            // Opaque interiors include the complete stationary caption; rounded
            // corners and the exterior shadow deliberately expose the water.
            Require(ChangedPixels(blank, animated, new(b.X + .025, b.Y + .018, b.Width - .05, b.Height - .036)) == 0,
                $"Water animation changed the stationary {button.Label} caption or its opaque interior.");
        }
        await Save(target, "water-square-decayed");

        // These two isolated impulses exercise the full camera -> compositor ->
        // GPU field -> shader projection path. Pixel centroids must stay under
        // the physical tip at both depths, independently of CPU round trips.
        foreach (var (label, boardPoint) in new[]
                 { ("far", new Vector2(.55f, .28f)), ("near", new Vector2(.50f, .68f)) })
        {
            now += TimeSpan.FromMilliseconds(20);
            Require(scene.ActivateWaterGardenButton("water-garden-calm"), "Perspective touch fixture could not reset its field.");
            Draw(scene, target);
            var beforeTouch = Pixels(target);
            now += TimeSpan.FromMilliseconds(20);
            Require(scene.SetWaterStickTip(CameraPoint(boardPoint.X, boardPoint.Y, inset), now),
                $"The visible {label} water surface rejected its calibrated tip.");
            Draw(scene, target);
            var surfacePoint = WaterGardenView.ScreenToSurface(boardPoint);
            var probe = water.GetFieldProbe(surfacePoint);
            var emptyProbe = water.GetFieldProbe(surfacePoint + new Vector2(.09f, 0));
            var visibleChange = WaterChangeCentroid(beforeTouch, Pixels(target));
            Require(double.IsFinite(probe.Height) && probe.Height < -.0001 && Math.Abs(emptyProbe.Height) < 1e-8,
                $"The {label} camera observation did not place a local depression at the inverse-projected GPU field position: " +
                $"centre height={probe.Height:G9}, offset height={emptyProbe.Height:G9}.");
            Require(visibleChange.Count > 20 &&
                    Vector2.Distance(visibleChange.Centre, boardPoint) < .03,
                $"The {label} ripple appeared away from the physical tip: tip={boardPoint}, " +
                $"visible centre={visibleChange.Centre}, changed pixels={visibleChange.Count}.");
            perspectiveTouches.Add(new { label,
                boardPoint = new { x = boardPoint.X, y = boardPoint.Y },
                surfacePoint = new { x = surfacePoint.X, y = surfacePoint.Y }, probe,
                changedPixels = visibleChange.Count,
                visibleCentre = new { x = visibleChange.Centre.X, y = visibleChange.Centre.Y } });
            await Save(target, "water-perspective-touch-" + label);
        }

        now += TimeSpan.FromMilliseconds(20);
        Require(scene.ActivateWaterGardenButton("water-garden-calm"), "Calm Water did not activate.");
        Draw(scene, target);
        var reset = Statistics(water);
        Require(IsFinite(reset) && reset.TotalEnergy == 0 && reset.MaximumHeight == 0 &&
                DuckStatesWithinTolerance(initialDucks, DuckStates(water)) &&
                scene.GetWaterGardenDiagnostics() is { TipVisible: false, InputCount: 0, PendingDisturbances: 0 },
            "Calm Water left a wave, moving duck, queued disturbance or active eye tip.");
        Require(!scene.SetWaterStickTip(CameraPoint(.5, .5, inset), now),
            "A frame captured at the reset watermark replayed an old disturbance.");
        await Save(target, "water-square-reset");

        // Compare the same buoyant object in calm and disturbed GPU fields.
        // An offset ripple must change height and attitude before it can drift;
        // idle animation alone cannot satisfy this ambient-disabled fixture.
        object duckPhysics;
        using (var duckWater = new WaterGardenSimulation(device, 256, 256, 1))
        {
            duckWater.SetAmbientEnabledForVerification(false);
            duckWater.SetFountainEnabledForVerification(false);
            var start = DuckStates(duckWater);
            for (int frame = 0; frame < 60; frame++) duckWater.Advance(1.0 / 30);
            Require(DuckStatesWithinTolerance(start, DuckStates(duckWater)),
                "Ducks moved in a flat, ambient-disabled water field without any force.");
            duckWater.Reset();
            Require(DuckStatesWithinTolerance(start, DuckStates(duckWater)),
                "Resetting a calm duck fixture changed its initial placement.");
            var subject = start[0];
            var stickTip = new Vector2(subject.SurfaceX - .016f, subject.SurfaceY);
            duckWater.AddDisturbance(stickTip, .034f, .004f);
            double maximumHeave = 0, maximumSlope = 0, maximumDrift = 0;
            for (int frame = 0; frame < 60; frame++)
            {
                duckWater.Advance(1.0 / 30);
                if (frame % 3 != 0 && frame != 59) continue;
                var states = DuckStates(duckWater);
                Require(states.All(DuckIsFiniteAndInside),
                    "A floating duck became non-finite or escaped the basin during a wave.");
                var duck = states[0];
                maximumHeave = Math.Max(maximumHeave, Math.Abs(duck.Height - subject.Height));
                maximumSlope = Math.Max(maximumSlope,
                    Math.Sqrt(duck.SlopeX * duck.SlopeX + duck.SlopeY * duck.SlopeY));
                maximumDrift = Math.Max(maximumDrift,
                    Vector2.Distance(new(duck.SurfaceX, duck.SurfaceY), new(subject.SurfaceX, subject.SurfaceY)));
            }
            Require(maximumHeave > 1e-5 && maximumSlope > 1e-4 && maximumDrift > 1e-5,
                $"A nearby water wave failed to bob, tilt and carry its duck: " +
                $"heave={maximumHeave:G6}, slope={maximumSlope:G6}, drift={maximumDrift:G6}.");
            var waveOnlyDuck = DuckStates(duckWater)[0];
            duckWater.Reset();
            Require(DuckStatesWithinTolerance(start, DuckStates(duckWater)),
                "Preparing the moving-stick comparison failed to reset duck positions.");
            duckWater.AddDisturbance(stickTip, .034f, .004f);
            // The matching ripple-only run above is the acquisition control;
            // the moving stroke below must add a separate directed shove.
            duckWater.AddStickStroke(
                new(subject.SurfaceX - .035f, subject.SurfaceY),
                new(subject.SurfaceX - .005f, subject.SurfaceY), .5f);
            for (int frame = 0; frame < 60; frame++) duckWater.Advance(1.0 / 30);
            var movedByStick = DuckStates(duckWater)[0];
            double additionalStrokeTravel = movedByStick.SurfaceX - waveOnlyDuck.SurfaceX;
            double outwardStrokeTravel = movedByStick.SurfaceX - subject.SurfaceX;
            Require(DuckIsFiniteAndInside(movedByStick) && additionalStrokeTravel > .004 &&
                    outwardStrokeTravel > .004,
                $"A confirmed moving stick failed to push the nearby duck beyond the matching " +
                $"acquisition ripple: additional travel={additionalStrokeTravel:G6}, " +
                $"outward travel={outwardStrokeTravel:G6}.");
            for (int frame = 0; frame < 120; frame++) duckWater.Advance(1.0 / 30);
            var settled = DuckStates(duckWater);
            Require(settled.All(DuckIsFiniteAndInside),
                "Floating ducks became non-finite or left the basin after six simulated seconds.");

            // A fast confirmed stroke can queue several segments before the
            // next simulation step. The duck is beside a middle segment; the
            // final segment ends well beyond it. Its motion must not depend on
            // the last segment alone surviving in the wake state.
            var fastStroke = new[]
            {
                new Vector2(subject.SurfaceX - .12f, subject.SurfaceY),
                new Vector2(subject.SurfaceX - .06f, subject.SurfaceY),
                new Vector2(subject.SurfaceX, subject.SurfaceY),
                new Vector2(subject.SurfaceX + .10f, subject.SurfaceY),
                new Vector2(subject.SurfaceX + .22f, subject.SurfaceY)
            };
            duckWater.Reset();
            for (int segment = 1; segment < fastStroke.Length; segment++)
                duckWater.AddStickStroke(fastStroke[segment - 1], fastStroke[segment], .7f);
            for (int frame = 0; frame < 60; frame++) duckWater.Advance(1.0 / 30);
            var fastStrokeDucks = DuckStates(duckWater);
            double fastStrokeTravel = fastStrokeDucks[0].SurfaceX - subject.SurfaceX;
            Require(fastStrokeDucks.All(DuckIsFiniteAndInside),
                "A fast multi-segment stick stroke drove a duck outside the basin.");
            duckWater.Reset();
            duckWater.AddStickStroke(fastStroke[^2], fastStroke[^1], .7f);
            for (int frame = 0; frame < 60; frame++) duckWater.Advance(1.0 / 30);
            var lastSegmentOnlyDuck = DuckStates(duckWater)[0];
            double intermediateSegmentTravel = fastStrokeDucks[0].SurfaceX - lastSegmentOnlyDuck.SurfaceX;
            Require(fastStrokeTravel > .004 && intermediateSegmentTravel > .004,
                $"A duck beside an intermediate stick segment did not receive its push: " +
                $"fast stroke={fastStrokeTravel:G6}, beyond-last-segment control " +
                $"difference={intermediateSegmentTravel:G6}.");
            duckWater.Reset();
            var firstReset = DuckStates(duckWater);
            duckWater.Advance(1.0 / 30);
            duckWater.Reset();
            Require(DuckStatesWithinTolerance(start, firstReset) && firstReset.SequenceEqual(DuckStates(duckWater)),
                "Repeated Calm Water resets did not restore identical duck placement and motion.");
            duckPhysics = new { count = start.Count, ambientDisabledCalmStable = true,
                maximumHeave, maximumSlope, maximumDrift, additionalStrokeTravel, outwardStrokeTravel,
                fastStrokeTravel, intermediateSegmentTravel,
                simulatedSecondsAfterImpulse = 6,
                boundedAfterSettling = true, repeatedResetDeterministic = true };
        }

        // No stick input or ambient motion can satisfy this fixture. Persistent
        // 3D parcels must contact the stone, take time to reach the pond, and feed
        // the actual wave field. Replaying Calm must reproduce both simulations.
        object fountainPhysics;
        using (var fountainWater = new WaterGardenSimulation(device, 256, 256, 1))
        {
            fountainWater.SetAmbientEnabledForVerification(false);
            Require(fountainWater.GetDiagnostics().FountainEnabled,
                "The Water Garden fountain was not enabled by default.");
            var fluid = Fountain(fountainWater);
            var fountainStartDucks = DuckStates(fountainWater);
            var waterfallOutlet = new Vector2(.5f, .5f + fluid.WorldMaximum.Y);
            int nearestDuckIndex = fountainStartDucks.OrderBy(duck =>
                Vector2.Distance(new(duck.SurfaceX, duck.SurfaceY), waterfallOutlet)).First().Index;
            var nearestDuckStart = fountainStartDucks[nearestDuckIndex];
            var nearestDuckPrevious = new Vector2(nearestDuckStart.SurfaceX, nearestDuckStart.SurfaceY);
            double nearestDuckPath = 0, maximumDownstreamTravel = 0, maximumAwayFromWaterfall = 0;
            var maximumDuckTravel = new double[fountainStartDucks.Count];
            byte[] calmRockPixels;
            using (var calmRocks = new CanvasRenderTarget(device, 1536, 1536, 96))
            {
                using (var drawing = calmRocks.CreateDrawingSession())
                    fountainWater.Draw(drawing, new Windows.Foundation.Rect(0, 0, 1536, 1536));
                calmRockPixels = Pixels(calmRocks);
            }
            // This probe lies ahead of the whole particle domain, so only a
            // propagated pond wave can reach it, regardless of the landing path.
            var neighbour = new Vector2(.5f, .5f + fluid.WorldMaximum.Y + .08f);
            double maximumNeighbourHeight = 0;
            double maximumMass = 0, maximumHeight = 0, maximumSpeed = 0;
            WaterGardenFieldStatistics? firstFlow = null;
            IReadOnlyList<WaterGardenDuckState>? firstFlowDucks = null;
            WaterFountainFluidDiagnostics? firstFlowFluid = null;
            byte[]? firstFlowAtlas = null;
            long firstFlowImpacts = 0;
            int firstFlowFrame = 0, maximumParticles = 0, maximumOccupiedVoxels = 0;
            double? firstEmissionSeconds = null, firstImpactSeconds = null;
            bool airborneBeforeImpact = false;
            for (int frame = 1; frame <= 600; frame++)
            {
                fountainWater.Advance(1.0 / 30);
                var currentFluid = fountainWater.GetFountainDiagnostics();
                var currentPond = fountainWater.GetDiagnostics();
                Require(FountainIsFiniteAndConserved(currentFluid) &&
                        currentFluid.Steps == currentPond.SimulationSteps &&
                        currentFluid.PoolImpactParticles == currentPond.FountainImpactCount,
                    "The fountain lost finite particle state, parcel conservation, or a real pond arrival: " +
                    System.Text.Json.JsonSerializer.Serialize(new { currentFluid, currentPond }));
                maximumParticles = Math.Max(maximumParticles, currentFluid.ParticleCount);
                if (currentFluid.EmittedParticles > 0) firstEmissionSeconds ??= frame / 30.0;
                if (currentFluid.PoolImpactParticles > 0) firstImpactSeconds ??= frame / 30.0;
                airborneBeforeImpact |= currentFluid.ParticleCount > 0 && currentFluid.PoolImpactParticles == 0;
                if (frame % 15 == 0 && currentFluid.PoolImpactParticles > 0)
                {
                    var ducks = DuckStates(fountainWater);
                    Require(ducks.All(DuckIsFiniteAndInside) && ducks.All(duck =>
                            duck.SurfaceX >= .159f && duck.SurfaceX <= .841f &&
                            duck.SurfaceY >= .219f && duck.SurfaceY <= .821f),
                        "Fountain-driven ducks became non-finite or crossed a pond margin.");
                    for (int index = 0; index < ducks.Count; index++)
                    {
                        var start = fountainStartDucks[index];
                        var current = ducks[index];
                        maximumDuckTravel[index] = Math.Max(maximumDuckTravel[index],
                            Vector2.Distance(new(start.SurfaceX, start.SurfaceY),
                                new(current.SurfaceX, current.SurfaceY)));
                    }
                    var near = ducks[nearestDuckIndex];
                    var nearPosition = new Vector2(near.SurfaceX, near.SurfaceY);
                    nearestDuckPath += Vector2.Distance(nearestDuckPrevious, nearPosition);
                    nearestDuckPrevious = nearPosition;
                    maximumDownstreamTravel = Math.Max(maximumDownstreamTravel,
                        near.SurfaceY - nearestDuckStart.SurfaceY);
                    maximumAwayFromWaterfall = Math.Max(maximumAwayFromWaterfall,
                        Vector2.Distance(nearPosition, waterfallOutlet) -
                        Vector2.Distance(new(nearestDuckStart.SurfaceX, nearestDuckStart.SurfaceY), waterfallOutlet));
                }
                if (frame == 30)
                    Require(currentFluid is { ParticleCount: 0, EmittedParticles: 0, PoolImpactParticles: 0 } &&
                            Statistics(fountainWater).TotalEnergy == 0 &&
                            DuckStatesWithinTolerance(fountainStartDucks, DuckStates(fountainWater)),
                        "The fountain emitted parcels, moved ducks or disturbed the first quiet second after Calm Water.");
                if (frame >= 60 && frame % 15 == 0)
                {
                    var probe = fountainWater.GetFieldProbe(neighbour);
                    Require(double.IsFinite(probe.Height) && double.IsFinite(probe.Velocity),
                        "The fountain's neighbouring wave probe became non-finite.");
                    maximumNeighbourHeight = Math.Max(maximumNeighbourHeight, Math.Abs(probe.Height));
                }
                if (firstFlow is null && frame >= 90 && frame % 15 == 0 && currentFluid.PoolImpactParticles > 0)
                {
                    var candidate = Statistics(fountainWater);
                    if (candidate.TotalEnergy > 0)
                    {
                        firstFlow = candidate;
                        firstFlowFrame = frame;
                        firstFlowDucks = DuckStates(fountainWater);
                        firstFlowImpacts = currentPond.FountainImpactCount;
                        var volume = FountainVolume(fluid);
                        Require(volume.Occupied > 0 && volume.Width > 2 && volume.Depth > 2 && volume.Height > 2 &&
                                fluid.GetDiagnostics().RockContacts > 0,
                            "The flowing fountain did not reconstruct a three-dimensional volume after contacting its stone.");
                        firstFlowFluid = fluid.GetDiagnostics();
                        firstFlowAtlas = (byte[])fluid.BuildDensityAtlas().Clone();
                    }
                }
                if (frame == 90)
                {
                    using var earlyFlow = new CanvasRenderTarget(device, 1536, 1536, 96);
                    using (var drawing = earlyFlow.CreateDrawingSession())
                        fountainWater.Draw(drawing, new Windows.Foundation.Rect(0, 0, 1536, 1536));
                    await Save(earlyFlow, "water-fountain-early");
                }
                if (frame % 150 != 0) continue;
                var densityVolume = FountainVolume(fluid);
                currentFluid = fluid.GetDiagnostics();
                maximumOccupiedVoxels = Math.Max(maximumOccupiedVoxels, densityVolume.Occupied);
                var field = Statistics(fountainWater);
                maximumMass = Math.Max(maximumMass, Math.Abs(field.HeightMass));
                maximumHeight = Math.Max(maximumHeight, Math.Max(Math.Abs(field.MinimumHeight), Math.Abs(field.MaximumHeight)));
                maximumSpeed = Math.Max(maximumSpeed, field.MaximumSpeed);
                Require(IsFinite(field) && maximumMass < 2e-5 && maximumHeight < .03 && maximumSpeed < .3 &&
                        DuckStates(fountainWater).All(DuckIsFiniteAndInside) &&
                        FountainIsFiniteAndConserved(currentFluid) && currentFluid.ParticleCount > 0 &&
                        currentFluid.RockContacts > 0 && densityVolume.Occupied > 0,
                    $"The continuously running fountain became unbounded or filled the basin: " +
                    $"seconds={frame / 30.0:G4}, mass={maximumMass:G6}, height={maximumHeight:G6}, speed={maximumSpeed:G6}.");
            }
            long impactsAfterTwentySeconds = fountainWater.GetDiagnostics().FountainImpactCount;
            var finalFluid = fluid.GetDiagnostics();
            Require(impactsAfterTwentySeconds > 0 && maximumNeighbourHeight > 1e-7 &&
                    firstFlow is not null && firstFlowFrame > 0 && airborneBeforeImpact &&
                    firstEmissionSeconds is { } emissionSeconds && firstImpactSeconds is { } impactSeconds &&
                    impactSeconds > emissionSeconds &&
                    maximumParticles > 0 && maximumOccupiedVoxels > 0 && finalFluid.RockContacts > 0,
                "The fountain did not carry persistent parcels over the rock into a propagating pond wave.");
            int travellingDucks = maximumDuckTravel.Count(travel => travel > .015);
            Require(maximumDownstreamTravel > .025 && maximumAwayFromWaterfall > .025 &&
                    nearestDuckPath > .05 && travellingDucks >= 3,
                $"Real waterfall arrivals failed to carry ducks away through the pond: " +
                $"nearest index={nearestDuckIndex}, downstream={maximumDownstreamTravel:G6}, " +
                $"away={maximumAwayFromWaterfall:G6}, path={nearestDuckPath:G6}, " +
                $"travelling ducks={travellingDucks}.");
            using (var steady = new CanvasRenderTarget(device, 1536, 1536, 96))
            {
                using (var drawing = steady.CreateDrawingSession())
                    fountainWater.Draw(drawing, new Windows.Foundation.Rect(0, 0, 1536, 1536));
                byte[] flowingPixels = Pixels(steady);
                int visibleRockFlowPixels = 0;
                for (int y = 130; y < 480; y++)
                for (int x = 580; x < 960; x++)
                {
                    int pixel = (y * 1536 + x) * 4;
                    if (Math.Abs(flowingPixels[pixel] - calmRockPixels[pixel]) > 12 ||
                        Math.Abs(flowingPixels[pixel + 1] - calmRockPixels[pixel + 1]) > 12 ||
                        Math.Abs(flowingPixels[pixel + 2] - calmRockPixels[pixel + 2]) > 12)
                        visibleRockFlowPixels++;
                }
                Require(visibleRockFlowPixels > 1500,
                    $"The 3D fountain was physically active but not visibly rendered over the rock: " +
                    $"{visibleRockFlowPixels} changed pixels.");
                await Save(steady, "water-fountain-steady");
            }
            fountainWater.Reset();
            var resetVolume = FountainVolume(fluid);
            Require(fountainWater.GetDiagnostics() is { FountainEnabled: true, FountainImpactCount: 0 } &&
                    fluid.GetDiagnostics() is { ParticleCount: 0, EmittedParticles: 0, PoolImpactParticles: 0,
                        EscapedParticles: 0, RockContacts: 0, Steps: 0 } && resetVolume.Occupied == 0 &&
                    Statistics(fountainWater).TotalEnergy == 0 &&
                    DuckStatesWithinTolerance(fountainStartDucks, DuckStates(fountainWater)),
                "Calm Water failed to restore ducks or clear fountain particles, density, waves and impacts.");
            for (int frame = 0; frame < firstFlowFrame; frame++) fountainWater.Advance(1.0 / 30);
            var resumedFlow = Statistics(fountainWater);
            var resumedFlowDucks = DuckStates(fountainWater);
            var resumedDiagnostics = fountainWater.GetDiagnostics();
            _ = FountainVolume(fluid);
            var resumedFluid = fluid.GetDiagnostics();
            // Replaying a float32 GPU field can differ in its last few bits
            // across texture realizations; CPU parcels and volume remain exact.
            Require(resumedDiagnostics.FountainImpactCount == firstFlowImpacts &&
                    resumedDiagnostics.SimulationSteps == firstFlowFrame * 4 &&
                    Math.Abs(resumedDiagnostics.SimulatedSeconds - firstFlowFrame / 30.0) < 1e-9 &&
                    resumedFluid == firstFlowFluid && firstFlowAtlas is not null &&
                    firstFlowAtlas.AsSpan().SequenceEqual(fluid.BuildDensityAtlas()) &&
                    firstFlow is not null && IsFinite(resumedFlow) &&
                    Math.Abs(resumedFlow.HeightMass - firstFlow.HeightMass) < 1e-10 &&
                    Math.Abs(resumedFlow.TotalEnergy - firstFlow.TotalEnergy) < 1e-10 &&
                    Math.Abs(resumedFlow.MinimumHeight - firstFlow.MinimumHeight) < 1e-8 &&
                    Math.Abs(resumedFlow.MaximumHeight - firstFlow.MaximumHeight) < 1e-8 &&
                    Math.Abs(resumedFlow.MaximumSpeed - firstFlow.MaximumSpeed) < 1e-8 &&
                    firstFlowDucks is not null && resumedFlowDucks.All(DuckIsFiniteAndInside) &&
                    DuckStatesWithinTolerance(firstFlowDucks, resumedFlowDucks),
                "The fountain did not resume reproducibly after Calm Water. " +
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    initialImpacts = firstFlowImpacts, firstFlowFrame, resumedDiagnostics,
                    initialFluid = firstFlowFluid, resumedFluid,
                    initialField = firstFlow, resumedField = resumedFlow,
                    initialDucks = firstFlowDucks, resumedDucks = resumedFlowDucks
                }));
            fountainPhysics = new { simulatedSeconds = 20, impactsAfterTwentySeconds,
                maximumNeighbourHeight, maximumMass, maximumHeight, maximumSpeed,
                maximumParticles, maximumOccupiedVoxels, firstEmissionSeconds, firstImpactSeconds,
                firstFlowSeconds = firstFlowFrame / 30.0, finalFluid,
                nearestDuckIndex, maximumDownstreamTravel, maximumAwayFromWaterfall,
                nearestDuckPath, travellingDucks, maximumDuckTravel,
                realParcelArrivalsMatchImpacts = true, particleConservation = true,
                occupiedThreeDimensionalDensity = true, stoneCollisions = true,
                drivesWavesWithoutStick = true, initialCalmSecond = true,
                duckTravelBounded = true, resetReproducible = true };
        }

        now += TimeSpan.FromMilliseconds(20);
        Require(scene.SetWaterStickTip(CameraPoint(.25, .40, inset), now), "Fresh eye-tip reacquisition failed.");
        Require(scene.GetWaterGardenDiagnostics().PendingDisturbances == 1, "Acquisition must start with one local disturbance.");
        now += TimeSpan.FromMilliseconds(20);
        Require(scene.SetWaterStickTip(CameraPoint(.30, .40, inset), now) &&
                scene.GetWaterGardenDiagnostics().PendingDisturbances > 2,
            "Continuous stick movement did not interpolate the short ripple trail.");
        Draw(scene, target);
        long beforeLoss = scene.GetWaterGardenDiagnostics().InputCount;
        scene.SetWaterStickTip(null, now);
        Require(scene.GetWaterGardenDiagnostics() is { TipVisible: false, PendingDisturbances: 0 },
            "A missing tip retained its visible pointer or queued disturbances.");
        now += TimeSpan.FromMilliseconds(100);
        Require(scene.SetWaterStickTip(CameraPoint(.34, .40, inset), now) &&
                scene.GetWaterGardenDiagnostics() is { PendingDisturbances: 1 } reacquired &&
                reacquired.InputCount == beforeLoss + 1,
            "Reacquisition drew a connecting stroke across a lost-tip interval.");
        Require(!scene.SetWaterStickTip(CameraPoint(.4, .4, inset), now) &&
                scene.GetWaterGardenDiagnostics().InputCount == beforeLoss + 1,
            "A duplicate camera frame deposited a second ripple.");
        var oldFrame = now;
        now += TimeSpan.FromMilliseconds(300);
        Require(!scene.SetWaterStickTip(CameraPoint(.4, .4, inset), oldFrame) &&
                scene.GetWaterGardenDiagnostics() is { TipVisible: false, PendingDisturbances: 0 },
            "A stale tip survived the 250 millisecond freshness limit.");
        Require(!scene.SetWaterStickTip(CameraPoint(.4, .4, inset), now.AddSeconds(1)) &&
                !scene.SetWaterStickTip(new(double.NaN, 0), now),
            "A future or non-finite camera observation was accepted.");
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetWaterStickTip(CameraPoint(.75, .90, inset), now) &&
                scene.CurrentBoardScreen == BoardScreen.WaterGarden &&
                scene.GetWaterGardenDiagnostics().PendingDisturbances == 0,
            "The eye-tip input entered the stationary control row.");
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetWaterStickTip(CameraPoint(.04, .22, inset), now) &&
                scene.GetWaterGardenDiagnostics() is { TipVisible: false, PendingDisturbances: 0 },
            "A tip inside the old rectangular region but outside the oblique water trapezoid created a ripple.");
        now += TimeSpan.FromMilliseconds(20);
        var hiddenByRim = WaterGardenView.SurfaceToScreen(new(.5f, .98f));
        Require(!scene.SetWaterStickTip(CameraPoint(hiddenByRim.X, hiddenByRim.Y, inset), now) &&
                scene.GetWaterGardenDiagnostics() is { TipVisible: false, PendingDisturbances: 0 },
            "A tip behind the raised near rim created a hidden ripple.");

        scene.SetBoardFacingDegrees(90);
        Require(scene.GetEyeTipProjectionFrames().Count == 0,
            "Changing board orientation retained an eye reference with the previous camera mapping.");
        Require(scene.GetWaterGardenDiagnostics() is { TipVisible: false, InputCount: 0 },
            "Changing board orientation retained the old camera association.");
        now += TimeSpan.FromMilliseconds(20);
        // A clockwise quarter-turn sends board (u,v) to projector (1-v,u).
        Require(scene.SetWaterStickTip(CameraPoint(.37, .42, inset, facing90: true), now),
            "The rotated calibrated camera point was rejected.");
        var rotatedTip = scene.GetWaterGardenDiagnostics().BoardTip;
        Require(rotatedTip is { } tip && Vector2.Distance(new((float)tip.X, (float)tip.Y), new(.37f, .42f)) < 1e-5,
            "A 90-degree board turn did not map the raw camera point into the new board coordinates.");
        Draw(scene, target);
        await Save(target, "water-square-facing-90");
        Require(scene.ActivateWaterGardenButton("menu") && scene.CurrentBoardScreen == BoardScreen.Menu &&
                scene.GetWaterGardenDiagnostics() is { Active: false, TipVisible: false, Simulation: null },
            "Exit did not release the Water Garden field and return to the menu.");

        foreach (var (width, height, label) in new[] { (3840, 2160, "landscape"), (2160, 3840, "portrait") })
        {
            using var aspectScene = NewScene(width / (double)height, out double aspectInset);
            await aspectScene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
            using var output = new CanvasRenderTarget(device, width, height, 96);
            Draw(aspectScene, output);
            var initialAspectPixels = Pixels(output);
            var aspectWater = Simulation(aspectScene);
            // Allow actual parcels to travel over the rock before capturing the
            // falling water. Advance at the normal cadence without rendering 4K
            // frames for every physics step; do not assume an analytic impact time.
            for (int frame = 0; frame < 240; frame++)
            {
                aspectWater.Advance(1.0 / 30);
                if (frame >= 71 && aspectWater.GetFountainDiagnostics().PoolImpactParticles > 0) break;
            }
            now += TimeSpan.FromMilliseconds(20);
            Require(aspectScene.SetWaterStickTip(CameraPoint(.45, .48, aspectInset), now),
                $"The {label} camera mapping rejected its input.");
            for (int frame = 0; frame < 10; frame++)
            {
                now += TimeSpan.FromSeconds(1.0 / 60);
                Draw(aspectScene, output);
            }
            var statistics = Statistics(aspectWater);
            var diagnostics = aspectWater.GetDiagnostics();
            var fountainState = aspectWater.GetFountainDiagnostics();
            Require(IsFinite(statistics) && statistics.TotalEnergy > 0 &&
                    diagnostics.FountainEnabled && diagnostics.FountainImpactCount > 0 &&
                    FountainIsFiniteAndConserved(fountainState) && fountainState.ParticleCount > 0 &&
                    fountainState.OccupiedVoxels > 0 && fountainState.RockContacts > 0 &&
                    fountainState.PoolImpactParticles == diagnostics.FountainImpactCount &&
                    DuckStates(aspectWater).All(DuckIsFiniteAndInside) &&
                    Math.Max(diagnostics.FieldWidth, diagnostics.FieldHeight) == 512,
                $"The {label} native render produced an invalid or unbounded simulation.");
            long fountainRevision = aspectWater.Revision;
            var fountainDucks = DuckStates(aspectWater);
            Draw(aspectScene, preview, isPreview: true);
            DrawRawPreview(aspectScene, preview);
            Draw(aspectScene, output);
            Require(aspectWater.Revision == fountainRevision &&
                    aspectWater.GetDiagnostics().FountainImpactCount == diagnostics.FountainImpactCount &&
                    aspectWater.GetFountainDiagnostics() == fountainState &&
                    fountainDucks.SequenceEqual(DuckStates(aspectWater)),
                "A same-clock preview/output draw advanced fountain particles, duplicated impacts or moved its ducks.");
            var flowingAspectPixels = Pixels(output);
            foreach (var button in aspectScene.CurrentBoardButtons)
            {
                var b = button.Bounds;
                Require(ChangedPixels(initialAspectPixels, flowingAspectPixels,
                        new(b.X + .025, b.Y + .018, b.Width - .05, b.Height - .036),
                        width, height, aspectInset) == 0,
                    $"The {label} fountain changed the stationary {button.Label} caption or its opaque interior.");
            }
            aspects.Add(new { width, height, diagnostics, statistics, fountainState });
            await Save(output, "water-" + label + "-native");
        }

        using (var uncalibrated = new SceneCompositor(blackjackClock: () => now, waterClock: () => now))
        {
            uncalibrated.ShowWaterGarden();
            await uncalibrated.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
            DrawRawPreview(uncalibrated, preview);
            var previewWater = Simulation(uncalibrated);
            long previewRevision = previewWater.Revision;
            var previewPixels = Pixels(preview);
            DrawRawPreview(uncalibrated, preview);
            Require(previewRevision == previewWater.Revision && previewPixels.SequenceEqual(Pixels(preview)) &&
                    previewPixels.Where((_, index) => index % 4 != 3).Distinct().Count() > 16,
                "The uncalibrated local Water Garden preview was blank or changed at the same clock.");
            previewWater.AddDisturbance(new(.5f, .5f), .022f, .006f);
            Require(Statistics(previewWater).TotalEnergy > 0,
                "The isolated local-preview reset fixture did not begin with a disturbed field.");
            now += TimeSpan.FromMilliseconds(20);
            Require(!uncalibrated.SetWaterStickTip(new(500, 500), now) &&
                    uncalibrated.ActivateWaterGardenAt(.75, .90) &&
                    uncalibrated.CurrentBoardScreen == BoardScreen.WaterGarden,
                "The uncalibrated preview accepted unmapped camera input or could not activate Calm Water.");
            DrawRawPreview(uncalibrated, preview);
            var previewReset = Statistics(Simulation(uncalibrated));
            Require(IsFinite(previewReset) && previewReset.TotalEnergy == 0 &&
                    uncalibrated.ActivateWaterGardenAt(.18, .90) && uncalibrated.CurrentBoardScreen == BoardScreen.Menu,
                "The uncalibrated local preview could not calm its field or return to the menu.");
        }

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.GetBoardFacingDegrees()),
            "Water Garden verification changed the live camera, projector or board registration.");
        return new { passed = true, propagatedHeight, injected, propagating, decayed, reset,
            duckPhysics, fountainPhysics, visibleDuckPixels, sameClockDuckStateStable = true,
            sameClockFountainStable = true,
            perspectiveRoundTrips, perspectiveTouches, trapezoidAndRimOcclusionRejected = true,
            boundedEyeProjectionReference = true,
            sameClockDrawStable = true, stationaryCaptionPixelsStable = true, freshCalibratedStickMapping = true,
            rawPreviewSameClockStable = true, uncalibratedPreviewControls = true,
            lossHasNoConnectingStroke = true, staleAndDuplicateFramesRejected = true, quarterTurnMapping = true,
            liveHardwareUnchanged = true, aspects, renderedFrames, drawMilliseconds, readbackMilliseconds,
            timingScope = "Isolated GPU draw submission and explicit field/pixel readbacks; PNG encoding excluded; not projector frame pacing.",
            directory, images };

        SceneCompositor NewScene(double aspect, out double safetyInset)
        {
            var result = new SceneCompositor(blackjackClock: () => now, waterClock: () => now);
            result.SetDisplayAspect(aspect);
            result.SetBoardFacingDegrees(0);
            result.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] camera = [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)];
            safetyInset = result.SetDetectedBoardGrid(corners, Homography.FromFourPoints(camera, unit));
            result.SetBoardSetup(false);
            result.ShowWaterGarden();
            return result;
        }
        static PixelPoint CameraPoint(double u, double v, double safetyInset, bool facing90 = false)
        {
            if (facing90) (u, v) = (1 - v, u);
            return new(1000 * (safetyInset / 2 + u * (1 - safetyInset)),
                1000 * (safetyInset / 2 + v * (1 - safetyInset)));
        }
        void Draw(SceneCompositor drawingScene, CanvasRenderTarget output, bool isPreview = false)
        {
            var timer = Stopwatch.StartNew();
            using (var drawing = output.CreateDrawingSession())
                drawingScene.Draw(drawing, (float)output.Size.Width, (float)output.Size.Height,
                    preview: isPreview, runningSlowly: false);
            drawMilliseconds += timer.Elapsed.TotalMilliseconds;
            renderedFrames++;
        }
        void DrawRawPreview(SceneCompositor drawingScene, CanvasRenderTarget output)
        {
            var timer = Stopwatch.StartNew();
            using (var drawing = output.CreateDrawingSession())
                drawingScene.DrawWaterGardenPreview(drawing, (float)output.Size.Width, (float)output.Size.Height);
            drawMilliseconds += timer.Elapsed.TotalMilliseconds;
            renderedFrames++;
        }
        byte[] Pixels(CanvasRenderTarget output)
        {
            var timer = Stopwatch.StartNew();
            var pixels = output.GetPixelBytes();
            readbackMilliseconds += timer.Elapsed.TotalMilliseconds;
            return pixels;
        }
        WaterGardenFieldStatistics Statistics(WaterGardenSimulation simulation)
        {
            var timer = Stopwatch.StartNew();
            var statistics = simulation.GetFieldStatistics();
            readbackMilliseconds += timer.Elapsed.TotalMilliseconds;
            return statistics;
        }
        IReadOnlyList<WaterGardenDuckState> DuckStates(WaterGardenSimulation simulation)
        {
            var timer = Stopwatch.StartNew();
            var states = simulation.GetDuckStates();
            readbackMilliseconds += timer.Elapsed.TotalMilliseconds;
            return states;
        }
        async Task Save(CanvasRenderTarget output, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await output.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        int ChangedPixels(byte[] first, byte[] second, BoardRect region,
            int imageWidth = 1000, int imageHeight = 1000, double? safetyInset = null)
        {
            int count = 0;
            double padding = safetyInset ?? inset;
            int left = (int)Math.Ceiling(imageWidth * (padding / 2 + region.X * (1 - padding)));
            int top = (int)Math.Ceiling(imageHeight * (padding / 2 + region.Y * (1 - padding)));
            int right = (int)Math.Floor(imageWidth * (padding / 2 + (region.X + region.Width) * (1 - padding)));
            int bottom = (int)Math.Floor(imageHeight * (padding / 2 + (region.Y + region.Height) * (1 - padding)));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int offset = (y * imageWidth + x) * 4;
                if (!first.AsSpan(offset, 4).SequenceEqual(second.AsSpan(offset, 4))) count++;
            }
            return count;
        }
        (int Count, Vector2 Centre) WaterChangeCentroid(byte[] first, byte[] second)
        {
            int count = 0;
            double totalWeight = 0, sumX = 0, sumY = 0;
            for (int y = 0; y < 1000; y++)
            for (int x = 0; x < 1000; x++)
            {
                var boardPoint = new Vector2((float)((x / 1000.0 - inset / 2) / (1 - inset)),
                    (float)((y / 1000.0 - inset / 2) / (1 - inset)));
                if (!WaterGardenView.ContainsWater(boardPoint)) continue;
                int offset = (y * 1000 + x) * 4;
                int weight = Math.Abs(first[offset] - second[offset]) +
                    Math.Abs(first[offset + 1] - second[offset + 1]) +
                    Math.Abs(first[offset + 2] - second[offset + 2]);
                if (weight < 3) continue;
                count++;
                totalWeight += weight;
                sumX += boardPoint.X * weight;
                sumY += boardPoint.Y * weight;
            }
            return (count, totalWeight == 0 ? new(float.NaN, float.NaN) :
                new((float)(sumX / totalWeight), (float)(sumY / totalWeight)));
        }
        int YellowPixels(byte[] pixels, Vector2 boardCentre)
        {
            int count = 0;
            int centreX = (int)(1000 * (inset / 2 + boardCentre.X * (1 - inset)));
            int centreY = (int)(1000 * (inset / 2 + boardCentre.Y * (1 - inset)));
            for (int y = Math.Max(0, centreY - 48); y < Math.Min(1000, centreY + 48); y++)
            for (int x = Math.Max(0, centreX - 48); x < Math.Min(1000, centreX + 48); x++)
            {
                int offset = (y * 1000 + x) * 4;
                int blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
                if (red > 130 && green > 95 && blue < 105 && red > blue * 1.7 && green > blue * 1.4) count++;
            }
            return count;
        }
        static WaterGardenSimulation Simulation(SceneCompositor drawingScene) =>
            typeof(SceneCompositor).GetField("_waterSimulation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(drawingScene) as WaterGardenSimulation ?? throw new InvalidOperationException("No GPU water simulation was allocated.");
        static WaterFountainFluid Fountain(WaterGardenSimulation simulation) =>
            typeof(WaterGardenSimulation).GetField("_fountainFluid", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(simulation) as WaterFountainFluid ?? throw new InvalidOperationException("No three-dimensional fountain simulation was allocated.");
        static (int Occupied, int Width, int Depth, int Height) FountainVolume(WaterFountainFluid fluid)
        {
            fluid.BuildDensityAtlas();
            var values = fluid.DensityAtlas;
            int occupied = 0, left = int.MaxValue, back = int.MaxValue, bottom = int.MaxValue;
            int right = int.MinValue, front = int.MinValue, top = int.MinValue;
            for (int offset = 0; offset < values.Length; offset += 4)
            {
                Require(float.IsFinite(values[offset]) && values[offset] >= 0 &&
                        float.IsFinite(values[offset + 1]) && float.IsFinite(values[offset + 2]) &&
                        float.IsFinite(values[offset + 3]),
                    "The fountain reconstructed a non-finite density or velocity voxel.");
                if (values[offset] < .20f) continue;
                int atlasX = offset / 4 % WaterFountainFluid.AtlasWidth;
                int atlasY = offset / 4 / WaterFountainFluid.AtlasWidth;
                int x = atlasX % WaterFountainFluid.GridX, y = atlasY % WaterFountainFluid.GridY;
                int z = atlasY / WaterFountainFluid.GridY * WaterFountainFluid.AtlasTiles +
                    atlasX / WaterFountainFluid.GridX;
                occupied++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                back = Math.Min(back, y); front = Math.Max(front, y);
                bottom = Math.Min(bottom, z); top = Math.Max(top, z);
            }
            Require(occupied == fluid.GetDiagnostics().OccupiedVoxels,
                "The fountain's occupied-voxel diagnostics differed from its actual density volume.");
            return occupied == 0 ? (0, 0, 0, 0) :
                (occupied, right - left + 1, front - back + 1, top - bottom + 1);
        }
        static bool IsFinite(WaterGardenFieldStatistics statistics) => statistics.NonFiniteValues == 0 &&
            double.IsFinite(statistics.HeightMass) && double.IsFinite(statistics.MinimumHeight) &&
            double.IsFinite(statistics.MaximumHeight) && double.IsFinite(statistics.MaximumSpeed) &&
            double.IsFinite(statistics.TotalEnergy);
        static bool FountainIsFiniteAndConserved(WaterFountainFluidDiagnostics fluid) =>
            fluid.NonFiniteParticles == 0 && fluid.ParticleCount >= 0 && fluid.ParticleCount <= fluid.Capacity &&
            double.IsFinite(fluid.SimulatedSeconds) && double.IsFinite(fluid.MaximumSpeed) &&
            double.IsFinite(fluid.MaximumDensity) && double.IsFinite(fluid.AtlasMaximumDensity) &&
            fluid.MaximumSpeed >= 0 && fluid.MaximumDensity >= 0 && fluid.AtlasMaximumDensity >= 0 &&
            fluid.EmittedParticles == fluid.ParticleCount + fluid.PoolImpactParticles + fluid.EscapedParticles;
        static bool DuckIsFiniteAndInside(WaterGardenDuckState duck) =>
            float.IsFinite(duck.SurfaceX) && float.IsFinite(duck.SurfaceY) &&
            float.IsFinite(duck.VelocityX) && float.IsFinite(duck.VelocityY) &&
            float.IsFinite(duck.Height) && float.IsFinite(duck.VerticalVelocity) &&
            float.IsFinite(duck.SlopeX) && float.IsFinite(duck.SlopeY) &&
            float.IsFinite(duck.Yaw) && float.IsFinite(duck.YawVelocity) && float.IsFinite(duck.Scale) &&
            duck.SurfaceX > .01f && duck.SurfaceX < .99f && duck.SurfaceY > .01f && duck.SurfaceY < .99f &&
            Math.Abs(duck.Height) < .1f && Math.Abs(duck.SlopeX) < 2 && Math.Abs(duck.SlopeY) < 2 &&
            duck.Scale > 0;
        static bool DuckStatesWithinTolerance(IReadOnlyList<WaterGardenDuckState> first,
            IReadOnlyList<WaterGardenDuckState> second) => first.Count == second.Count &&
            first.Zip(second).All(pair => pair.First.Index == pair.Second.Index &&
                Math.Abs(pair.First.SurfaceX - pair.Second.SurfaceX) < 1e-6f &&
                Math.Abs(pair.First.SurfaceY - pair.Second.SurfaceY) < 1e-6f &&
                Math.Abs(pair.First.VelocityX - pair.Second.VelocityX) < 1e-6f &&
                Math.Abs(pair.First.VelocityY - pair.Second.VelocityY) < 1e-6f &&
                Math.Abs(pair.First.Height - pair.Second.Height) < 1e-6f &&
                Math.Abs(pair.First.VerticalVelocity - pair.Second.VerticalVelocity) < 1e-6f &&
                Math.Abs(pair.First.SlopeX - pair.Second.SlopeX) < 1e-6f &&
                Math.Abs(pair.First.SlopeY - pair.Second.SlopeY) < 1e-6f &&
                Math.Abs(pair.First.Yaw - pair.Second.Yaw) < 1e-6f &&
                Math.Abs(pair.First.YawVelocity - pair.Second.YawVelocity) < 1e-6f &&
                Math.Abs(pair.First.Scale - pair.Second.Scale) < 1e-6f);
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
