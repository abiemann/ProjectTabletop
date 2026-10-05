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
    // Only the diagnostic output directory comes from the live window. Every
    // game, clock, canvas and GPU resource belongs to these private fixtures.
    private async Task<object> VerifyCrownDeedWaterAsync()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(SceneCompositor);
        var disposeWater = type.GetMethod("DisposeCrownDeedWater", instance)!;
        var outlines = (Vector2[][])type.GetField("CrownDeedWaterCanalOutlines", statics)!.GetValue(null)!;
        var holes = (Vector2[][])type.GetField("CrownDeedWaterCanalHoles", statics)!.GetValue(null)!;
        Require(outlines.Length == 4 && outlines.All(p => p.Length >= 3), "Four authored canal outlines are required.");
        string directory = Path.Combine(_appDataDirectory, "CrownDeedWaterVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var images = new List<object>();
        var samples = new List<object>();
        int sameClockChecks = 0, stateChecks = 0, controlChecks = 0, outsideMaskChecks = 0;
        int cacheChecks = 0, canalMotionChecks = 0, compositionChecks = 0, recreationChecks = 0;
        foreach (var view in new[] { (Name: "measured", Width: 2777, Height: 2160),
            (Name: "wide", Width: 1920, Height: 1080), (Name: "square", Width: 1280, Height: 1280),
            (Name: "portrait", Width: 1080, Height: 1920) })
        {
            using var scene = Fixture(view.Width, view.Height, out double inset);
            using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), view.Width, view.Height, 96);
            using var projector = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), view.Width, view.Height, 96);
            now += TimeSpan.FromMilliseconds(4975);
            scene.TickMonopoly(now);
            // Reserve the larger laptop raster before checking reuse on either
            // path, so a legitimate first projector/preview resize is excluded.
            DrawPreview(scene, preview); DrawProjector(scene, projector); DrawPreview(scene, preview);
            var started = now;
            var snapshot = scene.MonopolyState;
            string frozen = scene.ExportMonopolySave();
            var controls = scene.CurrentBoardButtons.ToArray();
            var foreground = Field<CanvasRenderTarget>(scene, "_monopolyPreviewTarget");
            var board = Field<CanvasRenderTarget>(scene, "_boardApplicationTarget");
            var water = Field<CanvasRenderTarget>(scene, "_crownDeedWaterTarget");
            var metadata = Field<CanvasRenderTarget>(scene, "_crownDeedWaterMask");
            var epoch = Field<object>(scene, "_crownDeedWaterEpoch");
            var previewKey = Field<object>(scene, "_monopolyPreviewState");
            var boardKey = Field<object>(scene, "_renderedBoardState");
            byte[] fixedForeground = foreground.GetPixelBytes(), fixedBoard = board.GetPixelBytes();
            byte[] firstPreview = DrawPreview(scene, preview), firstProjector = DrawProjector(scene, projector);
            byte[] firstWater = water.GetPixelBytes();
            int waterWidth = (int)water.SizeInPixels.Width, waterHeight = (int)water.SizeInPixels.Height;
            Require(waterWidth >= view.Width * .9 && waterHeight >= view.Height * .9,
                "Water fell below the established native board sampling density.");
            Require(Math.Abs(scene.MonopolyPreviewAspect - view.Width / (double)view.Height) < .001,
                "Water verification lost the requested physical board aspect.");
            var previewMasks = Masks(view.Width, view.Height, Matrix3x2.CreateScale(view.Width / 1000f, view.Height / 1000f));
            var projectedMap = Matrix3x2.CreateScale((float)(view.Width * .93 * (1 - inset) / 1000),
                (float)(view.Height * .93 * (1 - inset) / 1000)) *
                Matrix3x2.CreateTranslation((float)(view.Width * (.035 + .93 * inset / 2)),
                    (float)(view.Height * (.035 + .93 * inset / 2)));
            var projectorMasks = Masks(view.Width, view.Height, projectedMap);
            // The opaque underlay samples the source-size metadata linearly;
            // its hidden support can exceed the final visible cutout edge.
            int underlayGuard = (int)Math.Ceiling(1.5 * Math.Max(waterWidth, waterHeight) /
                Math.Min(metadata.SizeInPixels.Width, metadata.SizeInPixels.Height)) + 1;
            var waterMasks = Masks(waterWidth, waterHeight,
                Matrix3x2.CreateScale(waterWidth / 1000f, waterHeight / 1000f), underlayGuard);
            Require(NontransparentOutside(firstWater, waterMasks.Allowed) == 0,
                "The isolated water layer extends outside the traced canals and source-filter sampling support.");
            outsideMaskChecks++;
            await Capture(preview, view.Name + "-water-0000ms");
            await Capture(water, view.Name + "-isolated-water");
            CheckComposition(scene, preview, firstPreview);
            foreach (int milliseconds in new[] { 700, 1800 })
            {
                now = started.AddMilliseconds(milliseconds);
                byte[] currentPreview = DrawPreview(scene, preview);
                byte[] currentWater = Field<CanvasRenderTarget>(scene, "_crownDeedWaterTarget").GetPixelBytes();
                Require(currentPreview.SequenceEqual(DrawPreview(scene, preview)), "Same-clock laptop water pixels changed.");
                sameClockChecks++;
                byte[] currentProjector = DrawProjector(scene, projector);
                Require(currentProjector.SequenceEqual(DrawProjector(scene, projector)), "Same-clock projected water pixels changed.");
                sameClockChecks++;
                Require(currentWater.SequenceEqual(Field<CanvasRenderTarget>(scene, "_crownDeedWaterTarget").GetPixelBytes()),
                    "Projector and laptop advanced the shared water clock differently.");
                sameClockChecks++;
                Require(ReferenceEquals(snapshot, scene.MonopolyState) && scene.ExportMonopolySave() == frozen,
                    "Water rendering changed the game, turn, dice, balance or saved state.");
                stateChecks++;
                Require(ButtonPixelsEqual(firstPreview, currentPreview, controls, view.Width, view.Height),
                    "Water changed a stationary control surface or caption.");
                controlChecks++;
                var previewDifference = Difference(firstPreview, currentPreview, previewMasks.Allowed);
                var projectorDifference = Difference(firstProjector, currentProjector, projectorMasks.Allowed);
                Require(previewDifference.Outside == 0 && projectorDifference.Outside == 0,
                    $"Water escaped its independently rasterized mask: preview {previewDifference.Outside}, projector {projectorDifference.Outside} pixels.");
                Require(NontransparentOutside(currentWater, waterMasks.Allowed) == 0,
                    "A later isolated water frame escaped the canal mask.");
                outsideMaskChecks += 3;
                Require(ReferenceEquals(foreground, Field<CanvasRenderTarget>(scene, "_monopolyPreviewTarget")) &&
                    ReferenceEquals(board, Field<CanvasRenderTarget>(scene, "_boardApplicationTarget")) &&
                    ReferenceEquals(water, Field<CanvasRenderTarget>(scene, "_crownDeedWaterTarget")) &&
                    ReferenceEquals(metadata, Field<CanvasRenderTarget>(scene, "_crownDeedWaterMask")) &&
                    Equals(previewKey, Field<object>(scene, "_monopolyPreviewState")) &&
                    Equals(boardKey, Field<object>(scene, "_renderedBoardState")) &&
                    fixedForeground.SequenceEqual(foreground.GetPixelBytes()) && fixedBoard.SequenceEqual(board.GetPixelBytes()),
                    "Water animation reallocated or repainted the cached board/foreground or metadata.");
                cacheChecks++;
                var canalChanges = waterMasks.Canals.Select(mask => Difference(firstWater, currentWater, mask).Inside).ToArray();
                if (milliseconds == 1800)
                {
                    Require(canalChanges.All(changed => changed > 8), "One of the four isolated canals remains static.");
                    Require(previewDifference.Inside > 32 && projectorDifference.Inside > 32,
                        "The animated water is not visible through the cached board's cutouts.");
                    canalMotionChecks += 4;
                }
                CheckComposition(scene, preview, currentPreview);
                samples.Add(new { view = view.Name, milliseconds, canalChanges, underlayGuard,
                    previewChanged = previewDifference.Inside, projectorChanged = projectorDifference.Inside,
                    previewOutside = previewDifference.Outside, projectorOutside = projectorDifference.Outside });
                await Capture(preview, view.Name + "-water-" + milliseconds.ToString("D4") + "ms");
            }
            // A resource reset must preserve the presentation epoch, otherwise
            // resizing the laptop or a GPU recreation restarts all four canals.
            byte[] beforeRecreate = DrawPreview(scene, preview);
            disposeWater.Invoke(scene, null);
            Require(RawField(scene, "_crownDeedWaterTarget") is null && RawField(scene, "_crownDeedWaterMask") is null &&
                RawField(scene, "_crownDeedWaterGeometry") is null && Equals(epoch, Field<object>(scene, "_crownDeedWaterEpoch")),
                "Water disposal retained native resources or reset its elapsed-time origin.");
            Require(beforeRecreate.SequenceEqual(DrawPreview(scene, preview)) &&
                !ReferenceEquals(water, Field<CanvasRenderTarget>(scene, "_crownDeedWaterTarget")),
                "Recreating water resources changed the same-time composite or reused a disposed target.");
            recreationChecks++;
            Require(scene.ExportMonopolySave() == frozen, "Water recreation mutated the game.");
            stateChecks++;

            Require(scene.ActivateMonopolyButton("mp-exit"), "The water fixture could not open the exit drawer.");
            now += TimeSpan.FromMilliseconds(350);
            var drawerFirst = DrawPreview(scene, preview);
            string drawerState = scene.ExportMonopolySave();
            var drawerControls = scene.CurrentBoardButtons.ToArray();
            CheckComposition(scene, preview, drawerFirst);
            now += TimeSpan.FromMilliseconds(700);
            var drawerNext = DrawPreview(scene, preview);
            Require(ButtonPixelsEqual(drawerFirst, drawerNext, drawerControls, view.Width, view.Height) &&
                Difference(drawerFirst, drawerNext, previewMasks.Allowed).Outside == 0,
                "Water painted over the settled drawer or its captions.");
            controlChecks++; outsideMaskChecks++;
            Require(scene.ExportMonopolySave() == drawerState, "Water advanced the modal drawer's game state.");
            stateChecks++;
            CheckComposition(scene, preview, drawerNext);
            await Capture(preview, view.Name + "-drawer-over-water");
            scene.Dispose();
            Require(RawField(scene, "_crownDeedWaterTarget") is null && RawField(scene, "_crownDeedWaterMask") is null,
                "Scene disposal leaked its water resources.");
            recreationChecks++;
        }
        await CheckEntrance();
        string reportPath = Path.Combine(directory, "water-samples.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, fourCanals = true, fourAspects = true, sameClockChecks, stateChecks,
            controlChecks, outsideMaskChecks, cacheChecks, canalMotionChecks, compositionChecks, recreationChecks,
            maskSamplingEdgePixels = 1, architecturalWindowsUseOriginalArtwork = true,
            cacheClockIndependent = true, epochPreservedOnRecreation = true,
            liveServicesConstructed = false, directory, reportPath, images };

        SceneCompositor Fixture(int width, int height, out double inset)
        {
            var game = new MonopolyGame(seed: 157, initialRolls: [new(1, 2)]);
            Require(game.HandleAction("mp-start-game", now) && game.HandleAction("mp-ai-minus", now.AddMilliseconds(1)) &&
                game.HandleAction("mp-human-plus", now.AddMilliseconds(2)) && game.HandleAction("mp-start", now.AddMilliseconds(3)),
                "The water fixture could not start a two-human game.");
            now += TimeSpan.FromMilliseconds(4);
            var scene = new SceneCompositor(monopoly: game, blackjackClock: () => now,
                monopolyClock: () => now, boardRevealClock: () => now);
            scene.SetDisplayAspect(width / (double)height);
            scene.SetBoardSetup(true);
            inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.ShowMonopoly();
            return scene;
        }
        async Task CheckEntrance()
        {
            const int width = 1152, height = 896;
            using var scene = Fixture(width, height, out _);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            var started = now;
            byte[] first = DrawPreview(scene, target);
            var baseLayer = Field<CanvasRenderTarget>(scene, "_monopolyEntranceBaseTarget");
            var lid = Field<CanvasRenderTarget>(scene, "_monopolyEntranceLidTarget");
            byte[] basePixels = baseLayer.GetPixelBytes(), lidPixels = lid.GetPixelBytes();
            string frozen = scene.ExportMonopolySave();
            var mask = Masks(width, height, Matrix3x2.CreateScale(width / 1000f, height / 1000f));
            now = started.AddMilliseconds(250);
            byte[] beforeFirstParcel = DrawPreview(scene, target);
            Require(Difference(first, beforeFirstParcel, mask.Allowed).Outside == 0,
                "Pre-arrival water changed the stationary city or captions.");
            outsideMaskChecks++;
            CheckComposition(scene, target, beforeFirstParcel);
            foreach (int milliseconds in new[] { 1100, 2500 })
            {
                now = started.AddMilliseconds(milliseconds);
                byte[] pixels = DrawPreview(scene, target);
                Require(pixels.SequenceEqual(DrawPreview(scene, target)), "Water changed repeated same-time entrance pixels.");
                sameClockChecks++;
                Require(ReferenceEquals(baseLayer, Field<CanvasRenderTarget>(scene, "_monopolyEntranceBaseTarget")) &&
                    ReferenceEquals(lid, Field<CanvasRenderTarget>(scene, "_monopolyEntranceLidTarget")) &&
                    basePixels.SequenceEqual(baseLayer.GetPixelBytes()) && lidPixels.SequenceEqual(lid.GetPixelBytes()),
                    "Live water repainted or replaced the entrance's cached city or captions.");
                cacheChecks++;
                Require(scene.ExportMonopolySave() == frozen, "Water/entrance rendering advanced game rules.");
                stateChecks++;
                CheckComposition(scene, target, pixels);
                await Capture(target, "entrance-water-" + milliseconds + "ms");
            }
        }
        void CheckComposition(SceneCompositor scene, CanvasRenderTarget output, byte[] actual)
        {
            using var expected = new CanvasRenderTarget(output.Device, output.SizeInPixels.Width, output.SizeInPixels.Height, 96);
            using (var drawing = expected.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                var water = Field<CanvasRenderTarget>(scene, "_crownDeedWaterTarget");
                var foreground = Field<CanvasRenderTarget>(scene, "_monopolyPreviewTarget");
                double width = output.Size.Width, height = output.Size.Height, aspect = scene.MonopolyPreviewAspect;
                double drawWidth = Math.Min(width, height * aspect), drawHeight = drawWidth / aspect;
                var destination = new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
                drawing.DrawImage(water, destination, new Rect(0, 0, water.SizeInPixels.Width, water.SizeInPixels.Height));
                drawing.DrawImage(foreground, destination, new Rect(0, 0, foreground.SizeInPixels.Width, foreground.SizeInPixels.Height));
            }
            Require(actual.SequenceEqual(expected.GetPixelBytes()),
                "The live water is not beneath the complete cached foreground, including parcels, captions and drawer.");
            compositionChecks++;
        }
        (bool[] Allowed, bool[][] Canals) Masks(int width, int height, Matrix3x2 transform, int samplingGuard = 1)
        {
            var canals = new bool[outlines.Length][];
            var union = new bool[width * height];
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            for (int index = 0; index < outlines.Length; index++)
            {
                using var outer = Polygon(outlines[index]);
                CanvasGeometry shape = outer;
                try
                {
                    foreach (var points in holes)
                    {
                        using var hole = Polygon(points);
                        var next = shape.CombineWith(hole, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                        if (!ReferenceEquals(shape, outer)) shape.Dispose();
                        shape = next;
                    }
                    using (var drawing = target.CreateDrawingSession())
                    {
                        drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                        drawing.Transform = transform;
                        drawing.FillGeometry(shape, Windows.UI.Color.FromArgb(255, 255, 255, 255));
                    }
                }
                finally { if (!ReferenceEquals(shape, outer)) shape.Dispose(); }
                var pixels = target.GetPixelBytes();
                var mask = new bool[width * height];
                for (int pixel = 0; pixel < mask.Length; pixel++)
                    if (pixels[pixel * 4 + 3] > 0) union[pixel] = mask[pixel] = true;
                canals[index] = mask;
            }
            // Visible output permits one pixel of resampling at the polygon
            // edge. Hidden underlay support instead follows metadata density.
            var allowed = (bool[])union.Clone();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (union[y * width + x])
                    for (int dy = -samplingGuard; dy <= samplingGuard; dy++)
                    for (int dx = -samplingGuard; dx <= samplingGuard; dx++)
                        if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                            allowed[(y + dy) * width + x + dx] = true;
            return (allowed, canals);

            CanvasGeometry Polygon(Vector2[] points)
            {
                using var path = new CanvasPathBuilder(target.Device);
                path.BeginFigure(points[0]);
                foreach (var point in points.Skip(1)) path.AddLine(point);
                path.EndFigure(CanvasFigureLoop.Closed);
                return CanvasGeometry.CreatePath(path);
            }
        }
        static byte[] DrawPreview(SceneCompositor scene, CanvasRenderTarget target)
        {
            using (var drawing = target.CreateDrawingSession())
                scene.DrawMonopolyPreview(drawing, (float)target.Size.Width, (float)target.Size.Height);
            return target.GetPixelBytes();
        }
        static byte[] DrawProjector(SceneCompositor scene, CanvasRenderTarget target)
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, (float)target.Size.Width, (float)target.Size.Height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        async Task Capture(CanvasRenderTarget target, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        object? RawField(object value, string name) => type.GetField(name, instance)!.GetValue(value);
        T Field<T>(object value, string name) => (T)(RawField(value, name) ??
            throw new InvalidOperationException("Missing native water resource or cache: " + name));
        static (int Inside, int Outside) Difference(byte[] first, byte[] second, bool[] mask)
        {
            Require(first.Length == second.Length && first.Length == mask.Length * 4, "A native comparison changed dimensions.");
            int inside = 0, outside = 0;
            for (int pixel = 0; pixel < mask.Length; pixel++)
            {
                int p = pixel * 4;
                if (first[p] == second[p] && first[p + 1] == second[p + 1] &&
                    first[p + 2] == second[p + 2] && first[p + 3] == second[p + 3]) continue;
                if (mask[pixel]) inside++; else outside++;
            }
            return (inside, outside);
        }
        static int NontransparentOutside(byte[] pixels, bool[] mask)
        {
            int count = 0;
            for (int pixel = 0; pixel < mask.Length; pixel++)
                if (!mask[pixel] && pixels[pixel * 4 + 3] != 0) count++;
            return count;
        }
        static bool ButtonPixelsEqual(byte[] first, byte[] second, IReadOnlyList<BoardButton> buttons, int width, int height)
        {
            foreach (var button in buttons)
            {
                var b = button.Bounds;
                for (int y = Math.Max(0, (int)Math.Ceiling(b.Y * height)); y < Math.Min(height, (b.Y + b.Height) * height); y++)
                for (int x = Math.Max(0, (int)Math.Ceiling(b.X * width)); x < Math.Min(width, (b.X + b.Width) * width); x++)
                {
                    int p = (y * width + x) * 4;
                    for (int channel = 0; channel < 4; channel++) if (first[p + channel] != second[p + channel]) return false;
                }
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
