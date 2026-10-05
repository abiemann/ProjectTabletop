#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Each diagnostic owns its scene, game, clocks and targets. The window is
    // used only for the output directory; no camera, output or input is opened.
    private async Task<object> VerifyCrownDeedWindowsAsync()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var sceneType = typeof(SceneCompositor);
        var panes = (Vector2[][][])sceneType.GetField("CrownDeedWindowPanes", statics)!.GetValue(null)!;
        var timeline = sceneType.Assembly.GetTypes().Single(t => t.Name == "CrownDeedWindowTimeline");
        var isAnimated = timeline.GetMethod("IsAnimated", statics)!;
        var lightLevel = timeline.GetMethod("LightLevel", statics)!;
        var dispose = sceneType.GetMethod("DisposeCrownDeedWindows", fields)!;
        bool[] selected = Enumerable.Range(1, panes.Length)
            .Select(id => (bool)isAnimated.Invoke(null, [id])!).ToArray();
        int selectedCount = selected.Count(value => value);
        Require(panes.Length >= 10 && panes.All(group => group.Length > 0 && group.All(p => p.Length >= 3)),
            "The authored architectural aperture set is missing.");
        Require(selectedCount >= panes.Length * .55 && selectedCount <= panes.Length * .65,
            $"Animated window selection is not approximately 60% ({selectedCount}/{panes.Length}).");
        var times = ChooseSamples();
        string directory = Path.Combine(_appDataDirectory, "CrownDeedWindowVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var samples = new List<object>();
        int sameClockChecks = 0, boundaryChecks = 0, stateChecks = 0, controlChecks = 0;
        int cacheChecks = 0, selectedPaneMotionChecks = 0, staticPaneChecks = 0, compositionChecks = 0, recreationChecks = 0;
        int metadataIdChecks = 0, metadataWindowChecks = 0, gpuFadeChecks = 0;
        int staticSamplingEdgeChangedPixels = 0, unselectedSourcePixelChecks = 0;
        var sourceOracle = new List<object>();
        var staticProbeCoverage = new List<object>();
        int[] entranceUnprobedWindowIds = [];
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await CheckSourceTexels();
        await File.WriteAllTextAsync(Path.Combine(directory, "source-texel-oracle.json"),
            JsonSerializer.Serialize(new { metadataIdChecks, metadataWindowChecks, gpuFadeChecks, unselectedSourcePixelChecks, sourceOracle },
                new JsonSerializerOptions { WriteIndented = true }));
        foreach (var view in new[] { (Name: "measured", Width: 2777, Height: 2160),
            (Name: "wide", Width: 1920, Height: 1080), (Name: "square", Width: 1280, Height: 1280),
            (Name: "portrait", Width: 1080, Height: 1920) })
        {
            using var scene = Fixture(view.Width, view.Height, out double inset);
            using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), view.Width, view.Height, 96);
            using var projector = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), view.Width, view.Height, 96);
            now += TimeSpan.FromMilliseconds(4975);
            scene.TickMonopoly(now);
            Draw(scene, preview, false); Draw(scene, projector, true); Draw(scene, preview, false);
            var started = now;
            string frozen = scene.ExportMonopolySave();
            var snapshot = scene.MonopolyState;
            var buttons = scene.CurrentBoardButtons.ToArray();
            var foreground = Field<CanvasRenderTarget>(scene, "_monopolyPreviewTarget");
            var board = Field<CanvasRenderTarget>(scene, "_boardApplicationTarget");
            var window = Field<CanvasRenderTarget>(scene, "_crownDeedWindowsTarget");
            var metadata = Field<CanvasRenderTarget>(scene, "_crownDeedWindowsMask");
            var epoch = Field<object>(scene, "_crownDeedWindowsEpoch");
            var foregroundKey = Field<object>(scene, "_monopolyPreviewState");
            var boardKey = Field<object>(scene, "_renderedBoardState");
            byte[] fixedForeground = foreground.GetPixelBytes(), fixedBoard = board.GetPixelBytes();
            byte[] firstPreview = Draw(scene, preview, false), firstProjector = Draw(scene, projector, true);
            byte[] firstWindows = window.GetPixelBytes();
            int windowWidth = (int)window.SizeInPixels.Width, windowHeight = (int)window.SizeInPixels.Height;
            int underlayGuard = (int)Math.Ceiling(1.5 * Math.Max(windowWidth, windowHeight) /
                Math.Min(metadata.SizeInPixels.Width, metadata.SizeInPixels.Height)) + 1;
            var previewMask = Mask(view.Width, view.Height,
                Matrix3x2.CreateScale(view.Width / 1000f, view.Height / 1000f), 1);
            var projectorMap = Matrix3x2.CreateScale((float)(view.Width * .93 * (1 - inset) / 1000),
                (float)(view.Height * .93 * (1 - inset) / 1000)) *
                Matrix3x2.CreateTranslation((float)(view.Width * (.035 + .93 * inset / 2)),
                    (float)(view.Height * (.035 + .93 * inset / 2)));
            var projectorMask = Mask(view.Width, view.Height, projectorMap, 1);
            var isolatedMask = Mask(windowWidth, windowHeight,
                Matrix3x2.CreateScale(windowWidth / 1000f, windowHeight / 1000f), underlayGuard);
            var staticProbes = previewMask.Pixels.Select((indices, index) => selected[index] ? [] :
                indices.Where(pixel => !previewMask.Allowed[pixel]).ToArray()).ToArray();
            int probedStaticWindows = staticProbes.Count(indices => indices.Length > 0);
            Require(probedStaticWindows >= (panes.Length - selectedCount) * .5 &&
                staticProbes.Sum(indices => indices.Length) >= panes.Length - selectedCount,
                "Too few unselected-window interior probes remain beyond the selected aperture sampling edges.");
            staticProbeCoverage.Add(new { view = view.Name, probedStaticWindows,
                interiorPixels = staticProbes.Sum(indices => indices.Length),
                unprobedWindowIds = Enumerable.Range(0, panes.Length).Where(index => !selected[index] && staticProbes[index].Length == 0)
                    .Select(index => index + 1).ToArray() });
            Require(NontransparentOutside(firstWindows, isolatedMask.Allowed) == 0,
                "Initial window underlay escapes the authored aperture sampling support.");
            boundaryChecks++;
            await Capture(preview, view.Name + "-initial-lit");
            await Capture(projector, view.Name + "-projector-initial-lit");
            var observed = new bool[panes.Length];
            foreach (float seconds in times)
            {
                now = started.AddSeconds(seconds);
                byte[] pixels = Draw(scene, preview, false);
                byte[] windowPixels = window.GetPixelBytes();
                Require(pixels.SequenceEqual(Draw(scene, preview, false)), "Repeated same-clock laptop windows changed pixels.");
                sameClockChecks++;
                var projected = Draw(scene, projector, true);
                Require(projected.SequenceEqual(Draw(scene, projector, true)) && windowPixels.SequenceEqual(window.GetPixelBytes()),
                    "Projector and laptop sampled different window frames at the same clock.");
                sameClockChecks += 2;
                Require(ReferenceEquals(snapshot, scene.MonopolyState) && scene.ExportMonopolySave() == frozen,
                    "Window rendering changed players, money, property, turn, dice or game state.");
                stateChecks++;
                Require(ButtonsEqual(firstPreview, pixels, buttons, view.Width, view.Height),
                    "A window animation changed a control or its stationary caption.");
                controlChecks++;
                var change = Difference(firstPreview, pixels, previewMask.Allowed);
                var projectedChange = Difference(firstProjector, projected, projectorMask.Allowed);
                var priorCentreChange = Difference(firstProjector, projected, projectorMask.CentreAllowed);
                if (change.Outside > 0 || projectedChange.Outside > 0 || priorCentreChange.Outside > 0)
                {
                    string name = view.Name + "-boundary-" + seconds.ToString("000.0", System.Globalization.CultureInfo.InvariantCulture);
                    await Capture(projector, name + "-projector");
                    await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
                    {
                        view = view.Name, seconds, previewOutside = change.Outside,
                        projectorOutside = projectedChange.Outside, priorCentreOutsideCount = priorCentreChange.Outside,
                        samplingGuardPixels = 1,
                        priorCentreOutside = OutsideDetails(firstProjector, projected, projectorMask.CentreAllowed, view.Width),
                        aaOutside = OutsideDetails(firstProjector, projected, projectorMask.Allowed, view.Width)
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                Require(change.Outside == 0 && projectedChange.Outside == 0,
                    $"Window motion escaped exact apertures: laptop {change.Outside}, projector {projectedChange.Outside} pixels.");
                Require(NontransparentOutside(windowPixels, isolatedMask.Allowed) == 0,
                    "The window underlay escaped its independent aperture/filter support.");
                boundaryChecks += 3;
                Require(ReferenceEquals(foreground, Field<CanvasRenderTarget>(scene, "_monopolyPreviewTarget")) &&
                    ReferenceEquals(board, Field<CanvasRenderTarget>(scene, "_boardApplicationTarget")) &&
                    ReferenceEquals(window, Field<CanvasRenderTarget>(scene, "_crownDeedWindowsTarget")) &&
                    ReferenceEquals(metadata, Field<CanvasRenderTarget>(scene, "_crownDeedWindowsMask")) &&
                    Equals(foregroundKey, Field<object>(scene, "_monopolyPreviewState")) &&
                    Equals(boardKey, Field<object>(scene, "_renderedBoardState")) &&
                    fixedForeground.SequenceEqual(foreground.GetPixelBytes()) && fixedBoard.SequenceEqual(board.GetPixelBytes()),
                    "Window time repainted/reallocated static architecture, controls or aperture metadata.");
                cacheChecks++;
                int[] perPane = isolatedMask.Pixels.Select(indices => indices.Count(pixel => PixelDiffers(firstWindows, windowPixels, pixel))).ToArray();
                int[] staticChanges = previewMask.Pixels.Select((indices, index) => selected[index] ? 0 :
                    indices.Count(pixel => PixelDiffers(firstPreview, pixels, pixel))).ToArray();
                int[] staticInteriorChanges = staticProbes.Select(indices =>
                    indices.Count(pixel => PixelDiffers(firstPreview, pixels, pixel))).ToArray();
                int[] staticEdgeChanges = staticChanges.Select((count, index) => count - staticInteriorChanges[index]).ToArray();
                staticSamplingEdgeChangedPixels += staticEdgeChanges.Sum();
                for (int index = 0; index < panes.Length; index++)
                {
                    if (selected[index]) observed[index] |= perPane[index] > 0;
                    else
                    {
                        // Adjacent panes can share a raster/filter boundary.
                        // Every remaining unselected-window interior stays exact;
                        // edge changes are retained below rather than hidden.
                        if (staticChanges[index] > 0)
                        {
                            string name = view.Name + "-unselected-" + (index + 1) + "-at-" +
                                seconds.ToString("000.0", System.Globalization.CultureInfo.InvariantCulture);
                            await Capture(preview, name);
                            var changed = previewMask.Pixels[index].Where(pixel => PixelDiffers(firstPreview, pixels, pixel)).ToArray();
                            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
                            {
                                view = view.Name, id = index + 1, seconds, changedPixels = changed.Length,
                                totalInteriorPixels = previewMask.Pixels[index].Length,
                                protectedInteriorPixels = staticProbes[index].Length,
                                changedSamplingEdgePixels = staticEdgeChanges[index],
                                outsideSelectedSamplingSupport = changed.Count(pixel => !previewMask.Allowed[pixel]),
                                changes = changed.Select(pixel => new
                                {
                                    x = pixel % view.Width, y = pixel / view.Width,
                                    sourceX = (pixel % view.Width + .5) / view.Width * metadata.SizeInPixels.Width,
                                    sourceY = (pixel / view.Width + .5) / view.Height * metadata.SizeInPixels.Height,
                                    beforeBgra = Enumerable.Range(0, 4).Select(channel => (int)firstPreview[pixel * 4 + channel]).ToArray(),
                                    afterBgra = Enumerable.Range(0, 4).Select(channel => (int)pixels[pixel * 4 + channel]).ToArray(),
                                    maxChannelDelta = Enumerable.Range(0, 4).Max(channel => Math.Abs(firstPreview[pixel * 4 + channel] - pixels[pixel * 4 + channel])),
                                    insideSelectedSamplingSupport = previewMask.Allowed[pixel]
                                }).ToArray()
                            }, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        Require(staticInteriorChanges[index] == 0,
                            $"Unselected aperture {index + 1} changed outside a neighboring selected aperture's sampling support.");
                        if (staticProbes[index].Length > 0) staticPaneChecks++;
                    }
                }
                CheckComposition(scene, preview, pixels);
                samples.Add(new { view = view.Name, seconds, underlayGuard, perPane, staticChanges, staticInteriorChanges, staticEdgeChanges,
                    changed = change.Inside, projectedChanged = projectedChange.Inside,
                    outside = change.Outside, projectedOutside = projectedChange.Outside });
                await Capture(preview, view.Name + "-at-" + seconds.ToString("000.0", System.Globalization.CultureInfo.InvariantCulture) + "s");
            }
            Require(Enumerable.Range(0, panes.Length).Where(i => selected[i]).All(i => observed[i]),
                "At least one selected architectural window never changed its actual native pixels.");
            selectedPaneMotionChecks += selectedCount;
            Require(Difference(firstPreview, Draw(scene, preview, false), previewMask.Allowed).Inside > 0,
                "The animated windows are not visible through the foreground's aperture cutouts.");
            await Capture(window, view.Name + "-isolated-windows");
            byte[] beforeRecreate = Draw(scene, preview, false);
            dispose.Invoke(scene, null);
            Require(Raw(scene, "_crownDeedWindowsTarget") is null && Raw(scene, "_crownDeedWindowsMask") is null &&
                Raw(scene, "_crownDeedWindowsGeometry") is null && Equals(epoch, Field<object>(scene, "_crownDeedWindowsEpoch")),
                "Window resource disposal leaked native resources or reset the lighting schedule.");
            Require(beforeRecreate.SequenceEqual(Draw(scene, preview, false)) &&
                !ReferenceEquals(window, Field<CanvasRenderTarget>(scene, "_crownDeedWindowsTarget")),
                "Window resource recreation changed the same-time frame or reused a disposed target.");
            recreationChecks++;
            Require(scene.ActivateMonopolyButton("mp-exit"), "The window fixture could not open its drawer.");
            now += TimeSpan.FromMilliseconds(350);
            byte[] drawer = Draw(scene, preview, false);
            var drawerButtons = scene.CurrentBoardButtons.ToArray();
            string drawerState = scene.ExportMonopolySave();
            now += TimeSpan.FromSeconds(2.5);
            byte[] drawerLater = Draw(scene, preview, false);
            Require(ButtonsEqual(drawer, drawerLater, drawerButtons, view.Width, view.Height) &&
                Difference(drawer, drawerLater, previewMask.Allowed).Outside == 0,
                "Window motion changed the stationary drawer, its caption or surrounding city.");
            Require(scene.ExportMonopolySave() == drawerState, "Window/drawer rendering advanced game state.");
            controlChecks++; boundaryChecks++; stateChecks++;
            CheckComposition(scene, preview, drawerLater);
            await Capture(preview, view.Name + "-drawer");
            scene.Dispose();
            Require(Raw(scene, "_crownDeedWindowsTarget") is null && Raw(scene, "_crownDeedWindowsMask") is null,
                "Scene disposal retained window GPU resources.");
            recreationChecks++;
        }
        await CheckEntrance();
        string reportPath = Path.Combine(directory, "window-samples.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, apertureCount = panes.Length, selectedCount, unselectedCount = panes.Length - selectedCount,
            selectedFraction = selectedCount / (double)panes.Length, sampledSeconds = times,
            sameClockChecks, boundaryChecks, stateChecks, controlChecks, cacheChecks, selectedPaneMotionChecks, staticPaneChecks,
            compositionChecks, recreationChecks, metadataIdChecks, metadataWindowChecks, gpuFadeChecks, sourceOracle,
            staticSamplingEdgeChangedPixels, staticProbeCoverage, unselectedSourcePixelChecks,
            entranceUnprobedWindowIds,
            visibleMaskSamplingEdgePixels = 1,
            waterFrozenAtInitialFrame = true, liveServicesConstructed = false, directory, reportPath, images };

        float Level(int id, float seconds) => (float)lightLevel.Invoke(null, [id, seconds])!;
        float[] ChooseSamples()
        {
            // Search only the clock API, then prove the chosen lighting states
            // in actual GPU output. This avoids hundreds of native readbacks.
            var candidates = Enumerable.Range(1, 360).Select(i => i * .5f).ToArray();
            var levels = candidates.Select(t => Enumerable.Range(1, panes.Length).Select(id => Level(id, t)).ToArray()).ToArray();
            for (int index = 0; index < panes.Length; index++)
            {
                Require(Level(index + 1, 0) == 1 && Level(index + 1, -1) == 1, "Windows must begin in their painted lit state.");
                Require(levels.All(row => float.IsFinite(row[index]) && row[index] >= 0 && row[index] <= 1),
                    "Window illumination escaped its documented range.");
                if (!selected[index]) Require(levels.All(row => row[index] == 1), "An unselected window's timeline changed.");
                else Require(levels.Min(row => row[index]) < .05f && levels.Max(row => row[index]) > .95f,
                    "A selected window never visits both dark and lit dwelling states.");
                foreach (float time in candidates)
                    Require(Math.Abs(Level(index + 1, time + .02f) - Level(index + 1, time)) < .08f,
                        "Window lighting jumps instead of making a gradual transition.");
            }
            var pending = Enumerable.Range(0, panes.Length).Where(i => selected[i]).ToHashSet();
            var result = new List<float>();
            while (pending.Count > 0 && result.Count < 12)
            {
                int best = Enumerable.Range(0, candidates.Length)
                    .MaxBy(row => pending.Count(index => levels[row][index] < .35f));
                Require(pending.Any(index => levels[best][index] < .35f), "No useful dark-window fixture time exists.");
                result.Add(candidates[best]);
                pending.RemoveWhere(index => levels[best][index] < .35f);
            }
            Require(pending.Count == 0, "The bounded fixture clocks do not cover all selected windows.");
            var transitionStarts = Enumerable.Range(0, panes.Length).Where(i => selected[i])
                .Select(index => Array.FindIndex(levels, row => row[index] < .5f)).Distinct().Count();
            Require(transitionStarts >= 3, "Window lights change together instead of using independent schedules.");
            int fading = Array.FindIndex(levels, row => Enumerable.Range(0, panes.Length)
                .Any(index => selected[index] && row[index] > .25f && row[index] < .75f));
            Require(fading >= 0, "No gradual lighting transition was found.");
            result.Add(candidates[fading]); result.Add(candidates[fading] + .2f);
            return result.Distinct().Order().ToArray();
        }
        SceneCompositor Fixture(int width, int height, out double inset)
        {
            var game = new MonopolyGame(seed: 157, initialRolls: [new(1, 2)]);
            Require(game.HandleAction("mp-start-game", now) && game.HandleAction("mp-ai-minus", now.AddMilliseconds(1)) &&
                game.HandleAction("mp-human-plus", now.AddMilliseconds(2)) && game.HandleAction("mp-start", now.AddMilliseconds(3)),
                "The window fixture could not start two human players.");
            now += TimeSpan.FromMilliseconds(4);
            var scene = new SceneCompositor(monopoly: game, blackjackClock: () => now, monopolyClock: () => now, boardRevealClock: () => now);
            // Keep the ordinary production water layer present but at age zero.
            sceneType.GetField("_crownDeedWaterEpoch", fields)!.SetValue(scene, now.AddYears(1));
            scene.SetDisplayAspect(width / (double)height);
            scene.SetBoardSetup(true);
            inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false); scene.ShowMonopoly();
            return scene;
        }
        async Task CheckSourceTexels()
        {
            // Render the production pass at its original texture density. This
            // removes viewport scaling from the categorical-ID and fade oracle.
            using var scene = Fixture(1254, 1254, out _);
            using var initial = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1254, 1254, 96);
            now += TimeSpan.FromMilliseconds(4975);
            Draw(scene, initial, false);
            string frozen = scene.ExportMonopolySave();
            var city = Field<CanvasBitmap>(scene, "_crownDeedCityBitmap");
            var metadata = Field<CanvasRenderTarget>(scene, "_crownDeedWindowsMask");
            Require(city.SizeInPixels == metadata.SizeInPixels, "Window metadata lost original artwork registration.");
            int width = (int)city.SizeInPixels.Width, height = (int)city.SizeInPixels.Height;
            byte[] cityPixels = city.GetPixelBytes(), identity = metadata.GetPixelBytes();
            var scale = Matrix3x2.CreateScale(width / 1000f, height / 1000f);
            var sourceMask = Mask(width, height, scale, 0);
            var colourPixels = new Dictionary<int, int[]>();
            var unprobed = new List<int>();
            var selectedSourcePolygons = panes.Where((_, index) => selected[index]).SelectMany(group => group)
                .Select(polygon => polygon.Select(p => Vector2.Transform(p, scale)).ToArray()).ToArray();
            var staticSourcePixels = new Dictionary<int, int[]>();
            var unprobedStaticSourceIds = new List<int>();
            for (int index = 0; index < panes.Length; index++)
            {
                var polygons = panes[index].Select(polygon => polygon.Select(p => Vector2.Transform(p, scale)).ToArray()).ToArray();
                if (!selected[index])
                {
                    var clear = sourceMask.Pixels[index].Where(pixel =>
                    {
                        var centre = new Vector2(pixel % width + .5f, pixel / width + .5f);
                        return polygons.Max(polygon => InteriorClearance(polygon, centre)) >= .8f &&
                            selectedSourcePolygons.All(polygon => InteriorClearance(polygon, centre) < 0 &&
                                BoundaryDistance(polygon, centre) > 1);
                    }).ToArray();
                    foreach (int pixel in clear)
                        Require(identity[pixel * 4 + 2] == 0,
                            $"Unselected window {index + 1} has animated metadata coverage in clear source glass.");
                    if (clear.Length > 0) staticSourcePixels.Add(index + 1, clear);
                    else unprobedStaticSourceIds.Add(index + 1);
                    continue;
                }
                var colours = new List<int>();
                int checkedPixels = 0;
                foreach (int pixel in sourceMask.Pixels[index])
                {
                    var centre = new Vector2(pixel % width + .5f, pixel / width + .5f);
                    float clearance = polygons.Max(polygon => InteriorClearance(polygon, centre));
                    if (clearance < .2f) continue;
                    int p = pixel * 4;
                    // GetPixelBytes is BGRA: R is coverage, G/B are ID low/high.
                    Require(identity[p + 2] > 0 && identity[p + 3] == 255,
                        $"Window {index + 1} lost coverage at an independently located source-interior texel.");
                    int actualId = identity[p + 1] + identity[p] * 256;
                    Require(actualId == index + 1,
                        $"Window {index + 1} source-interior texel decoded as ID {actualId}.");
                    metadataIdChecks++; checkedPixels++;
                    if (clearance >= .8f && identity[p + 2] == 255 && Luminance(cityPixels, pixel) >= 60)
                        colours.Add(pixel);
                }
                if (checkedPixels > 0) metadataWindowChecks++; else unprobed.Add(index + 1);
                if (colours.Count > 0) colourPixels.Add(index + 1, colours.Take(12).ToArray());
            }
            Require(metadataWindowChecks == selectedCount,
                "Selected windows lack original-resolution categorical-ID probes: " + string.Join(", ", unprobed));
            Require(colourPixels.Count >= 4,
                "Too few original-resolution aperture interiors remain for an independent fade oracle.");
            Require(staticSourcePixels.Count >= 4 && staticSourcePixels.Sum(pair => pair.Value.Length) >= 12,
                "Too few clear unselected source-glass probes remain beyond all animated apertures.");
            var ids = colourPixels.Keys.Order().ToArray();
            var chosen = Enumerable.Range(0, Math.Min(6, ids.Length))
                .Select(i => ids[(int)Math.Round(i * (ids.Length - 1.0) / (Math.Min(6, ids.Length) - 1))]).Distinct().ToArray();
            var draw = sceneType.GetMethod("DrawCrownDeedWindows", fields)!;
            var epoch = Field<DateTimeOffset>(scene, "_crownDeedWindowsEpoch");
            using var sourceTarget = new CanvasRenderTarget(city.Device, width, height, 96);
            byte[] lit = SourceFrame(0);
            foreach (int id in chosen)
            {
                var selectedPixels = colourPixels[id];
                float darkTime = Enumerable.Range(1, 1800).Select(i => i * .1f).First(t => Level(id, t) == 0);
                float fadeTime = Enumerable.Range(1, 18000).Select(i => i * .01f)
                    .First(t => Level(id, t) > .45f && Level(id, t) < .55f);
                byte[] dark = SourceFrame(darkTime);
                var paintedSource = SourceFrame(darkTime, includePainting: true);
                foreach (var staticWindow in staticSourcePixels)
                    foreach (int pixel in staticWindow.Value)
                    {
                        Require(!PixelDiffers(cityPixels, paintedSource, pixel),
                            $"Unselected window {staticWindow.Key} changed its original source texel away from every selected edge.");
                        unselectedSourcePixelChecks++;
                    }
                int maxSourceError = 0, maxFadeError = 0;
                double maxDarkRatio = 0;
                foreach (int pixel in selectedPixels)
                {
                    int p = pixel * 4;
                    Require(lit[p + 3] == 255 && dark[p + 3] == 255, "A fully covered source-interior window texel is not opaque.");
                    for (int channel = 0; channel < 3; channel++)
                        maxSourceError = Math.Max(maxSourceError, Math.Abs(lit[p + channel] - cityPixels[p + channel]));
                    maxDarkRatio = Math.Max(maxDarkRatio, Luminance(dark, pixel) / Luminance(cityPixels, pixel));
                }
                Require(maxSourceError <= 2, $"Window {id} lit source registration differs by {maxSourceError} channel levels.");
                Require(maxDarkRatio < .25, $"Window {id} never produces an actual dark-glass dwelling state.");
                byte[]? previousFade = null;
                foreach (float time in new[] { fadeTime, fadeTime + .12f })
                {
                    float level = Level(id, time);
                    Require(level > .1f && level < .9f, "The native fade sample missed its intended transition.");
                    byte[] fading = SourceFrame(time);
                    Require(fading.SequenceEqual(SourceFrame(time)), "Repeated native source-resolution fade pixels changed.");
                    sameClockChecks++;
                    foreach (int pixel in selectedPixels)
                    {
                        int p = pixel * 4;
                        Require(fading[p + 3] == 255, "Fading source-interior glass changed opacity.");
                        for (int channel = 0; channel < 3; channel++)
                        {
                            // Endpoint colours are measured GPU pixels, not a
                            // duplicate of the shader's dark-glass formula.
                            double expected = dark[p + channel] + (lit[p + channel] - dark[p + channel]) * level;
                            maxFadeError = Math.Max(maxFadeError, (int)Math.Ceiling(Math.Abs(fading[p + channel] - expected)));
                        }
                    }
                    Require(maxFadeError <= 3, $"Window {id} GPU fade disagrees with its CPU schedule by {maxFadeError} channel levels.");
                    if (previousFade is { } earlier)
                        Require(selectedPixels.Any(pixel => PixelDiffers(earlier, fading, pixel)),
                            "The GPU holds a fixed intermediate window colour instead of a smooth fade.");
                    previousFade = fading;
                    gpuFadeChecks++;
                }
                sourceOracle.Add(new { id, samples = selectedPixels.Length, darkTime, fadeTime,
                    maxSourceError, maxDarkRatio, maxFadeError });
            }
            sourceOracle.Add(new { unprobedSourceInteriorIds = unprobed.ToArray(),
                note = "Subpixel/edge-only groups remain covered by the four-aspect native motion and boundary checks." });
            sourceOracle.Add(new { staticSourceWindowCount = staticSourcePixels.Count,
                staticSourcePixels = staticSourcePixels.Sum(pair => pair.Value.Length),
                unprobedStaticSourceIds = unprobedStaticSourceIds.ToArray(),
                ownApertureClearancePixels = .8, selectedApertureDistancePixels = 1,
                note = "No source-interior invariance claim is made for windows listed without clear source probes." });
            Require(scene.ExportMonopolySave() == frozen, "Window source diagnostics altered game state.");
            stateChecks++;
            await Capture(sourceTarget, "source-resolution-midfade");

            byte[] SourceFrame(float seconds, bool includePainting = false)
            {
                using (var drawing = sourceTarget.CreateDrawingSession())
                {
                    drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    if (includePainting) drawing.DrawImage(city);
                    drawing.Transform = scale;
                    draw.Invoke(scene, [drawing, epoch.AddSeconds(seconds)]);
                }
                return sourceTarget.GetPixelBytes();
            }
        }
        static double Luminance(byte[] pixels, int pixel)
        {
            int p = pixel * 4;
            return .0722 * pixels[p] + .7152 * pixels[p + 1] + .2126 * pixels[p + 2];
        }
        static float InteriorClearance(Vector2[] polygon, Vector2 point)
        {
            bool inside = false;
            float distance = float.MaxValue;
            for (int a = 0, b = polygon.Length - 1; a < polygon.Length; b = a++)
            {
                var first = polygon[a]; var second = polygon[b];
                if ((first.Y > point.Y) != (second.Y > point.Y) &&
                    point.X < (second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y) + first.X)
                    inside = !inside;
                var edge = second - first;
                float t = edge.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(point - first, edge) / edge.LengthSquared(), 0, 1) : 0;
                distance = Math.Min(distance, Vector2.Distance(point, first + edge * t));
            }
            return inside ? distance : -1;
        }
        static float BoundaryDistance(Vector2[] polygon, Vector2 point)
        {
            float distance = float.MaxValue;
            for (int a = 0, b = polygon.Length - 1; a < polygon.Length; b = a++)
            {
                var first = polygon[a]; var edge = polygon[b] - first;
                float t = edge.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(point - first, edge) / edge.LengthSquared(), 0, 1) : 0;
                distance = Math.Min(distance, Vector2.Distance(point, first + edge * t));
            }
            return distance;
        }
        async Task CheckEntrance()
        {
            const int width = 1152, height = 896;
            using var scene = Fixture(width, height, out _);
            // Enter an already-running city-light schedule so this also checks
            // animated apertures behind moving entrance parcels and their lid.
            sceneType.GetField("_crownDeedWindowsEpoch", fields)!.SetValue(scene, now.AddSeconds(-18));
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            var started = now;
            var first = Draw(scene, target, false);
            var baseLayer = Field<CanvasRenderTarget>(scene, "_monopolyEntranceBaseTarget");
            var lid = Field<CanvasRenderTarget>(scene, "_monopolyEntranceLidTarget");
            byte[] basePixels = baseLayer.GetPixelBytes(), lidPixels = lid.GetPixelBytes();
            string frozen = scene.ExportMonopolySave();
            // This smaller entrance capture checks the complete AA boundary and
            // layer composition, not per-window interior motion. Some tiny glass
            // apertures touch pixels without containing a pixel centre; their
            // categorical IDs remain covered by the full source-texel oracle.
            var mask = Mask(width, height, Matrix3x2.CreateScale(width / 1000f, height / 1000f), 1,
                requireEveryInterior: false);
            entranceUnprobedWindowIds = Enumerable.Range(0, panes.Length)
                .Where(index => mask.Pixels[index].Length == 0).Select(index => index + 1).ToArray();
            now = started.AddMilliseconds(250);
            var prearrival = Draw(scene, target, false);
            Require(Difference(first, prearrival, mask.Allowed).Outside == 0,
                "Pre-arrival window motion changed architecture or stationary captions.");
            boundaryChecks++;
            CheckComposition(scene, target, prearrival);
            foreach (int milliseconds in new[] { 1100, 2500 })
            {
                now = started.AddMilliseconds(milliseconds);
                var pixels = Draw(scene, target, false);
                Require(pixels.SequenceEqual(Draw(scene, target, false)), "Same-clock windows/entrance output changed.");
                sameClockChecks++;
                Require(ReferenceEquals(baseLayer, Field<CanvasRenderTarget>(scene, "_monopolyEntranceBaseTarget")) &&
                    ReferenceEquals(lid, Field<CanvasRenderTarget>(scene, "_monopolyEntranceLidTarget")) &&
                    basePixels.SequenceEqual(baseLayer.GetPixelBytes()) && lidPixels.SequenceEqual(lid.GetPixelBytes()),
                    "Window time invalidated the entrance's static city or caption layers.");
                cacheChecks++;
                Require(scene.ExportMonopolySave() == frozen, "Windows/entrance rendering changed game state.");
                stateChecks++;
                CheckComposition(scene, target, pixels);
                await Capture(target, "entrance-windows-" + milliseconds + "ms");
            }
        }
        void CheckComposition(SceneCompositor scene, CanvasRenderTarget output, byte[] actual)
        {
            using var expected = new CanvasRenderTarget(output.Device, output.SizeInPixels.Width, output.SizeInPixels.Height, 96);
            using (var drawing = expected.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                double width = output.Size.Width, height = output.Size.Height, aspect = scene.MonopolyPreviewAspect;
                double drawWidth = Math.Min(width, height * aspect), drawHeight = drawWidth / aspect;
                var destination = new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
                foreach (string name in new[] { "_crownDeedWaterTarget", "_crownDeedWindowsTarget", "_monopolyPreviewTarget" })
                {
                    var image = Field<CanvasRenderTarget>(scene, name);
                    drawing.DrawImage(image, destination, new Rect(0, 0, image.SizeInPixels.Width, image.SizeInPixels.Height));
                }
            }
            Require(actual.SequenceEqual(expected.GetPixelBytes()), "The window layer painted above cached architecture, captions or drawer.");
            compositionChecks++;
        }
        (bool[] Allowed, int[][] Pixels, bool[] CentreAllowed) Mask(int width, int height, Matrix3x2 transform, int guard,
            bool requireEveryInterior = true)
        {
            // Pixel centres identify uncontaminated interior probes. Boundary
            // coverage is independently rasterized below so partially touched
            // corner pixels are included before the one-pixel sampling guard.
            var union = new bool[width * height];
            var pixels = new int[panes.Length][];
            for (int index = 0; index < panes.Length; index++)
            {
                var list = new HashSet<int>();
                foreach (var aperture in panes[index])
                {
                    var polygon = aperture.Select(point => Vector2.Transform(point, transform)).ToArray();
                    int left = Math.Max(0, (int)Math.Floor(polygon.Min(p => p.X)));
                    int right = Math.Min(width, (int)Math.Ceiling(polygon.Max(p => p.X)));
                    int top = Math.Max(0, (int)Math.Floor(polygon.Min(p => p.Y)));
                    int bottom = Math.Min(height, (int)Math.Ceiling(polygon.Max(p => p.Y)));
                    for (int y = top; y < bottom; y++)
                    for (int x = left; x < right; x++)
                    {
                        var p = new Vector2(x + .5f, y + .5f);
                        bool inside = false;
                        for (int a = 0, b = polygon.Length - 1; a < polygon.Length; b = a++)
                        {
                            var p1 = polygon[a]; var p2 = polygon[b];
                            if ((p1.Y > p.Y) != (p2.Y > p.Y) && p.X < (p2.X - p1.X) * (p.Y - p1.Y) / (p2.Y - p1.Y) + p1.X)
                                inside = !inside;
                        }
                        if (!inside) continue;
                        int pixel = y * width + x;
                        list.Add(pixel);
                        if (selected[index]) union[pixel] = true;
                    }
                }
                if (requireEveryInterior)
                    Require(list.Count > 0, $"Window {index + 1} has no native interior samples.");
                pixels[index] = list.ToArray();
            }
            var centreAllowed = Dilate(union);
            using var coverage = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            using (var drawing = coverage.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = transform;
                // Separate polygons are a union even if two traced panes touch.
                drawing.Blend = CanvasBlend.Add;
                for (int index = 0; index < panes.Length; index++)
                {
                    if (!selected[index]) continue;
                    foreach (var aperture in panes[index])
                    {
                        using var path = new CanvasPathBuilder(coverage.Device);
                        path.BeginFigure(aperture[0]);
                        foreach (var point in aperture.Skip(1)) path.AddLine(point);
                        path.EndFigure(CanvasFigureLoop.Closed);
                        using var geometry = CanvasGeometry.CreatePath(path);
                        drawing.FillGeometry(geometry, Windows.UI.Color.FromArgb(255, 255, 255, 255));
                    }
                }
            }
            var rasterized = coverage.GetPixelBytes();
            for (int pixel = 0; pixel < union.Length; pixel++) union[pixel] = rasterized[pixel * 4 + 3] > 0;
            return (Dilate(union), pixels, centreAllowed);

            bool[] Dilate(bool[] source)
            {
                var allowed = (bool[])source.Clone();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (source[y * width + x])
                        for (int dy = -guard; dy <= guard; dy++)
                        for (int dx = -guard; dx <= guard; dx++)
                            if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                                allowed[(y + dy) * width + x + dx] = true;
                return allowed;
            }
        }
        static byte[] Draw(SceneCompositor scene, CanvasRenderTarget target, bool projector)
        {
            using (var drawing = target.CreateDrawingSession())
                if (projector) scene.Draw(drawing, (float)target.Size.Width, (float)target.Size.Height, preview: false, runningSlowly: false);
                else scene.DrawMonopolyPreview(drawing, (float)target.Size.Width, (float)target.Size.Height);
            return target.GetPixelBytes();
        }
        async Task Capture(CanvasRenderTarget target, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        object? Raw(object value, string name) => sceneType.GetField(name, fields)!.GetValue(value);
        T Field<T>(object value, string name) => (T)(Raw(value, name) ?? throw new InvalidOperationException("Missing native window resource: " + name));
        static bool PixelDiffers(byte[] a, byte[] b, int pixel) => !a.AsSpan(pixel * 4, 4).SequenceEqual(b.AsSpan(pixel * 4, 4));
        static (int Inside, int Outside) Difference(byte[] a, byte[] b, bool[] mask)
        {
            Require(a.Length == b.Length && a.Length == mask.Length * 4, "Native window comparison dimensions changed.");
            int inside = 0, outside = 0;
            for (int pixel = 0; pixel < mask.Length; pixel++)
                if (PixelDiffers(a, b, pixel)) { if (mask[pixel]) inside++; else outside++; }
            return (inside, outside);
        }
        static object[] OutsideDetails(byte[] a, byte[] b, bool[] mask, int width) =>
            Enumerable.Range(0, mask.Length).Where(pixel => !mask[pixel] && PixelDiffers(a, b, pixel))
                .Take(100).Select(pixel => (object)new
                {
                    x = pixel % width, y = pixel / width,
                    beforeBgra = Enumerable.Range(0, 4).Select(channel => (int)a[pixel * 4 + channel]).ToArray(),
                    afterBgra = Enumerable.Range(0, 4).Select(channel => (int)b[pixel * 4 + channel]).ToArray(),
                    maxChannelDelta = Enumerable.Range(0, 4).Max(channel => Math.Abs(a[pixel * 4 + channel] - b[pixel * 4 + channel]))
                }).ToArray();
        static int NontransparentOutside(byte[] pixels, bool[] mask)
        {
            int count = 0;
            for (int pixel = 0; pixel < mask.Length; pixel++) if (!mask[pixel] && pixels[pixel * 4 + 3] != 0) count++;
            return count;
        }
        static bool ButtonsEqual(byte[] a, byte[] b, IReadOnlyList<BoardButton> buttons, int width, int height)
        {
            foreach (var button in buttons)
            {
                var bounds = button.Bounds;
                for (int y = Math.Max(0, (int)Math.Ceiling(bounds.Y * height)); y < Math.Min(height, (bounds.Y + bounds.Height) * height); y++)
                for (int x = Math.Max(0, (int)Math.Ceiling(bounds.X * width)); x < Math.Min(width, (bounds.X + bounds.Width) * width); x++)
                    if (PixelDiffers(a, b, y * width + x)) return false;
            }
            return true;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
