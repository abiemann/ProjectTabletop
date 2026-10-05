#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Inspect actual GPU pixels, not just the dimensions of an allocated texture.
    // Private scenes also keep this check independent of the user's game and scan.
    private async Task<object> VerifyBoardResolutionAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        const int width = 3840, height = 2160;
        var device = CanvasDevice.GetSharedDevice();
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        using var native = new CanvasRenderTarget(device, width, height, 96);
        using var highDpi = new CanvasRenderTarget(device, width / 3, height / 3, 288);
        using var legacy = new CanvasRenderTarget(device, 1000, 1000, 96);
        using var enlargedLegacy = new CanvasRenderTarget(device, width, height, 96);
        using var preview = new CanvasRenderTarget(device, 400, 300, 96);
        using var scene = NewScene();
        using var dpiScene = NewScene();
        using var legacyScene = NewScene();
        // Load the shipped thumbnail atlas before comparing stationary scenes.
        await Task.WhenAll(scene.EnsureGlobeResourcesAsync(device), dpiScene.EnsureGlobeResourcesAsync(device),
            legacyScene.EnsureGlobeResourcesAsync(device), scene.EnsureMenuPreviewResourcesAsync(device),
            dpiScene.EnsureMenuPreviewResourcesAsync(device), legacyScene.EnsureMenuPreviewResourcesAsync(device));
        var menuButtons = scene.CurrentBoardButtons.ToArray();

        Draw(scene, native, width, height);
        var nativePixels = native.GetPixelBytes();
        var raster = scene.GetBoardResolutionDiagnostics();
        Require(raster.LogicalSize == 1000 && raster.BoardPixelWidth > 3000 && raster.BoardPixelHeight > 1800,
            "The 4K board retained the old 1000-pixel rendering surface.");
        Require(raster.ProjectorPixelWidth == width && raster.ProjectorPixelHeight == height,
            "The compositor did not record the real projector pixel dimensions.");

        Draw(dpiScene, highDpi, width / 3, height / 3);
        var dpiRaster = dpiScene.GetBoardResolutionDiagnostics();
        Require(highDpi.SizeInPixels.Width == width && highDpi.SizeInPixels.Height == height &&
                dpiRaster.BoardPixelWidth == raster.BoardPixelWidth &&
                dpiRaster.BoardPixelHeight == raster.BoardPixelHeight &&
                dpiRaster.ProjectorPixelWidth == width && dpiRaster.ProjectorPixelHeight == height,
            "Windows scaling changed the board raster resolution for the same physical 4K output.");
        var dpiDifference = PixelDifference(nativePixels, highDpi.GetPixelBytes());
        Require(dpiDifference.MeanChannelError < 1 && dpiDifference.PixelsOver16 < width * height * .005,
            $"DPI changed projected geometry or detail: mean error {dpiDifference.MeanChannelError:F3}, " +
            $"{dpiDifference.PixelsOver16} pixels differ substantially.");

        // A literal 1000-pixel rendered screenshot is the control. Enlarging it
        // must not be mistaken for drawing the glyphs and vectors at output density.
        Draw(legacyScene, legacy, 1000, 1000);
        Require(legacyScene.GetBoardResolutionDiagnostics().BoardPixelWidth == 1000 &&
                legacyScene.GetBoardResolutionDiagnostics().BoardPixelHeight == 1000,
            "The low-resolution comparison fixture is not a 1000-pixel board.");
        using (var drawing = enlargedLegacy.CreateDrawingSession())
        {
            drawing.Clear(Colors.Black);
            drawing.DrawImage(legacy, new Rect(0, 0, width, height), new Rect(0, 0, 1000, 1000),
                1, CanvasImageInterpolation.Linear);
        }
        var legacyPixels = enlargedLegacy.GetPixelBytes();
        var heading = new Rect(width * .075, height * .095, width * .69, height * .09);
        var nativeEdges = SharpHorizontalEdges(nativePixels, heading);
        var legacyEdges = SharpHorizontalEdges(legacyPixels, heading);
        var detailDifference = PixelDifference(nativePixels, legacyPixels, heading);
        Require(nativeEdges > 100 && nativeEdges > legacyEdges * 1.2 && detailDifference.PixelsOver16 > 1000,
            $"The high-resolution heading did not resolve fresh vector detail: native sharp edges {nativeEdges}, " +
            $"upscaled edges {legacyEdges}, changed pixels {detailDifference.PixelsOver16}.");

        Draw(scene, preview, 400, 300, isPreview: true);
        var afterPreview = scene.GetBoardResolutionDiagnostics();
        Require(afterPreview.BoardPixelWidth == raster.BoardPixelWidth &&
                afterPreview.BoardPixelHeight == raster.BoardPixelHeight &&
                afterPreview.ProjectorPixelWidth == width && afterPreview.ProjectorPixelHeight == height,
            "The laptop preview shrank the projector's shared board texture or output size.");
        Draw(scene, native, width, height);
        Require(nativePixels.SequenceEqual(native.GetPixelBytes()) && scene.CurrentBoardButtons.SequenceEqual(menuButtons),
            "A small preview changed the subsequent projector pixels or the board's normalized hit regions.");

        string directory = Path.Combine(_appDataDirectory, "BoardResolutionSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        await Save(native, "menu-native-4k");
        await Save(enlargedLegacy, "menu-1000-pixels-enlarged-to-4k");
        await Save(highDpi, "menu-4k-at-300-percent-windows-scaling");

        // The violet "06 / BOARDS" caption is the menu's only AccentSecondary ink.
        // Locate it on the upright board, then predict it on each mapped board.
        var uprightMap = ReferenceMap([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        var uprightMarker = MarkerCentroid(nativePixels, uprightMap);
        var marker = uprightMap.Inverse().Transform(new(uprightMarker.X / width, uprightMarker.Y / height));
        var mappedBoards = new List<object>();
        foreach (var (name, corners) in new (string, Vector2[])[]
        {
            ("rotated", [new(.84f, .1f), new(.84f, .9f), new(.16f, .9f), new(.16f, .1f)]),
            ("perspective", [new(.13f, .12f), new(.91f, .18f), new(.82f, .91f), new(.20f, .82f)])
        })
        {
            using var mapped = NewScene(corners);
            using var scaledMapped = NewScene(corners);
            await Task.WhenAll(mapped.EnsureGlobeResourcesAsync(device), scaledMapped.EnsureGlobeResourcesAsync(device),
                mapped.EnsureMenuPreviewResourcesAsync(device), scaledMapped.EnsureMenuPreviewResourcesAsync(device));
            var reference = ReferenceMap(corners);
            Draw(mapped, native, width, height);
            var mappedPixels = native.GetPixelBytes();
            var mappedRaster = mapped.GetBoardResolutionDiagnostics();
            var density = SampleRequiredDensity(reference);
            Require(mappedRaster.BoardPixelWidth + 1 >= density.U && mappedRaster.BoardPixelHeight + 1 >= density.V,
                $"The {name} board undersampled a magnified edge of its calibrated projection.");
            if (name == "rotated")
                Require(mappedRaster.BoardPixelWidth < mappedRaster.BoardPixelHeight,
                    "A 90-degree board rotation did not exchange the source axes' physical pixel requirements.");
            var expectedMarker = reference.Transform(marker);
            var mappedMarker = MarkerCentroid(mappedPixels, reference);
            double markerError = Math.Sqrt(Math.Pow(mappedMarker.X - expectedMarker.X * width, 2) +
                Math.Pow(mappedMarker.Y - expectedMarker.Y * height, 2));
            Require(markerError < 2,
                $"The {name} board moved a known logical marker {markerError:F2} pixels away from its calibrated position.");
            Require(new[] { 0, width - 1, (height - 1) * width, width * height - 1 }
                    .All(pixel => mappedPixels[pixel * 4] == 0 && mappedPixels[pixel * 4 + 1] == 0 && mappedPixels[pixel * 4 + 2] == 0),
                "The mapped board leaked into projector pixels outside the physical board.");
            Draw(scaledMapped, highDpi, width / 3, height / 3);
            var mappedDpiDifference = PixelDifference(mappedPixels, highDpi.GetPixelBytes());
            Require(mappedDpiDifference.MeanChannelError < 1 && mappedDpiDifference.PixelsOver16 < width * height * .005 &&
                    mapped.CurrentBoardButtons.SequenceEqual(menuButtons),
                $"The {name} board changed its mapped image or hit geometry at a different Windows scale.");
            await Save(native, name + "-menu-native-4k");
            mappedBoards.Add(new { name, raster = mappedRaster, markerError,
                requiredUPixels = density.U, requiredVPixels = density.V,
                mappedDpiDifference.MeanChannelError, mappedDpiDifference.PixelsOver16 });
        }

        scene.ShowBlackjack();
        Draw(scene, native, width, height);
        var unlitTablePixels = native.GetPixelBytes();
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false },
            "The dense acquisition reference skipped its scene-settling interval.");
        now = now.AddMilliseconds(600);
        var acquisition = scene.GetHandAcquisitionContext(now);
        Require(acquisition is { ObserveMotion: true, ExpectedScene.Width: 1000, ExpectedScene.Height: 1000 } &&
                acquisition.ExpectedScene.Bgra.Length == 1000 * 1000 * 4,
            "The 4K table did not produce a bounded 1000-square hand-acquisition reference.");
        var expectedScene = acquisition!.ExpectedScene!;
        var unlitMap = ReferenceMap([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        int worstReferenceColorError = 0;
        foreach (var (x, y) in new[] { (180, 600), (800, 790) })
        {
            var board = new Point2((x + .5) / 1000, (y + .5) / 1000);
            var projected = unlitMap.Transform(board);
            int sourceOffset = ((int)(projected.Y * height) * width + (int)(projected.X * width)) * 4;
            int referenceOffset = (y * 1000 + x) * 4;
            for (int channel = 0; channel < 3; channel++)
                worstReferenceColorError = Math.Max(worstReferenceColorError,
                    Math.Abs(expectedScene.Bgra[referenceOffset + channel] - unlitTablePixels[sourceOffset + channel]));
            var matrix = expectedScene.CameraToBoard;
            double denominator = matrix[6] * projected.X + matrix[7] * projected.Y + matrix[8];
            double u = (matrix[0] * projected.X + matrix[1] * projected.Y + matrix[2]) / denominator;
            double v = (matrix[3] * projected.X + matrix[4] * projected.Y + matrix[5]) / denominator;
            Require(Math.Abs(u - board.X) < 1e-7 && Math.Abs(v - board.Y) < 1e-7,
                "The dense acquisition reference changed camera-to-board coordinates.");
        }
        int feltOffset = (600 * 1000 + 180) * 4, goldOffset = (790 * 1000 + 800) * 4;
        Require(worstReferenceColorError < 12 && expectedScene.Bgra[feltOffset + 1] > expectedScene.Bgra[feltOffset + 2] * 2 &&
                expectedScene.Bgra[feltOffset + 1] > expectedScene.Bgra[feltOffset] &&
                expectedScene.Bgra[goldOffset + 2] > expectedScene.Bgra[goldOffset + 1] &&
                expectedScene.Bgra[goldOffset + 1] > expectedScene.Bgra[goldOffset],
            "The dense acquisition readback cropped, stretched or changed the green felt and gold Deal button reference.");
        Require(scene.ActivateBlackjackButton("bj-deal"), "The high-resolution Blackjack fixture could not deal.");
        now = now.AddMilliseconds(1810);
        Require(scene.ActivateBlackjackButton("bj-hit"), "The high-resolution Blackjack fixture could not HIT.");
        now = now.AddMilliseconds(360);
        var hitButtons = scene.CurrentBoardButtons.ToArray();
        Draw(scene, native, width, height);
        var flightRaster = scene.GetBoardResolutionDiagnostics();
        Require(scene.BlackjackAnimation.Count == 1 && flightRaster.FlightPixelWidth >= raster.BoardPixelWidth &&
                flightRaster.FlightPixelHeight >= raster.BoardPixelHeight,
            "The moving card layer stayed at 1000 pixels while the table gained resolution.");
        var flight = scene.BlackjackAnimation.Single();
        var flightPixels = native.GetPixelBytes();
        var flightMap = ReferenceMap([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        int paperSamples = 0;
        foreach (float x in new[] { -.3f, .3f })
        foreach (float y in new[] { -.15f, .15f })
        {
            // These blank-paper positions exclude the rank, corners and pips.
            // Calculate their destinations independently of the GPU layer scale.
            var local = new Vector2(x * (float)flight.Destination.Width, y * (float)flight.Destination.Height);
            var board = Vector2.Transform(local, Matrix3x2.CreateRotation(flight.Rotation)) + flight.Center;
            var projected = flightMap.Transform(new(board.X / 1000, board.Y / 1000));
            int offset = ((int)(projected.Y * height) * width + (int)(projected.X * width)) * 4;
            if (flightPixels[offset] > 200 && flightPixels[offset + 1] > 220 && flightPixels[offset + 2] > 220)
                paperSamples++;
        }
        Require(paperSamples == 4, "The dense animated card did not occupy its independent rotated reference bounds.");
        await Save(native, "blackjack-moving-card-native-4k");
        using (var drawing = preview.CreateDrawingSession()) scene.DrawBlackjackPreview(drawing, 400, 300);
        var flightAfterPreview = scene.GetBoardResolutionDiagnostics();
        Require(flightAfterPreview.FlightPixelWidth == flightRaster.FlightPixelWidth &&
                flightAfterPreview.FlightPixelHeight == flightRaster.FlightPixelHeight &&
                flightAfterPreview.BoardPixelWidth == raster.BoardPixelWidth &&
                flightAfterPreview.BoardPixelHeight == raster.BoardPixelHeight &&
                scene.CurrentBoardButtons.SequenceEqual(hitButtons),
            "The laptop Blackjack preview changed high-resolution card layers or normalized button geometry.");
        var hit = scene.CurrentBoardButtons.Single(button => button.Id == "bj-hit");
        int cardsBefore = scene.BlackjackState.Hands.Single().Cards.Count;
        Require(scene.ActivateBlackjackAt(hit.Bounds.X + hit.Bounds.Width / 2, hit.Bounds.Y + hit.Bounds.Height / 2) &&
                scene.BlackjackState.Hands.Single().Cards.Count == cardsBefore + 1,
            "A normalized button center no longer selects HIT after the resolution change.");

        scene.ClearBoardMediaClip();
        var clearedRaster = scene.GetBoardResolutionDiagnostics();
        Require(clearedRaster.BoardPixelWidth == 0 && clearedRaster.BoardPixelHeight == 0 &&
                clearedRaster.FlightPixelWidth == 0 && clearedRaster.FlightPixelHeight == 0 &&
                clearedRaster.PreviewPixelWidth == 0 && clearedRaster.PreviewPixelHeight == 0 &&
                clearedRaster.ProjectorPixelWidth == 0 && clearedRaster.ProjectorPixelHeight == 0,
            "Clearing calibration retained old projector dimensions or high-resolution GPU layers.");
        Point2[] resetUnit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
            Homography.FromFourPoints(resetUnit, resetUnit));
        scene.SetBoardSetup(false);
        scene.ShowBoardMenu();
        var originalLowResolutionPixels = legacy.GetPixelBytes();
        Draw(scene, legacy, 1000, 1000);
        var resetRaster = scene.GetBoardResolutionDiagnostics();
        Require(resetRaster.BoardPixelWidth == 1000 && resetRaster.BoardPixelHeight == 1000 &&
                resetRaster.ProjectorPixelWidth == 1000 && resetRaster.ProjectorPixelHeight == 1000 &&
                originalLowResolutionPixels.SequenceEqual(legacy.GetPixelBytes()),
            "A new lower-resolution setup retained the former 4K allocation or stale rendered content.");

        Require(ReferenceEquals(_output, liveOutput) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "Resolution verification changed the user's camera, calibration, board or game.");
        return new
        {
            passed = true, raster, highDpiRaster = dpiRaster,
            dpiDifference = new { dpiDifference.MeanChannelError, dpiDifference.PixelsOver16 },
            nativeEdges, legacyEdges, headingPixelsWithNewDetail = detailDifference.PixelsOver16,
            genuineVectorDetail = true, equivalentPhysicalDpi = true, previewCannotShrinkOutput = true,
            movingCardsUseDenseRaster = true, normalizedHitGeometryUnchanged = true,
            animatedCardReferenceBounds = true, paperSamples, rotatedAndPerspectiveBoards = true,
            denseAcquisitionReferenceBoundedAndMapped = true, worstReferenceColorError,
            calibrationResetReleasesDenseLayers = true, resetRaster,
            liveStateUnchanged = true, flightRaster, mappedBoards, images
        };

        SceneCompositor NewScene(Vector2[]? corners = null)
        {
            var game = new BlackjackGame(seed: 173, initialShoe: new[] { 2, 6, 2, 10, 2, 2, 2 }
                .Select((rank, index) => new BlackjackCard(rank, (BlackjackSuit)(index % 4))));
            var created = new SceneCompositor(game, blackjackClock: () => now);
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            corners ??= [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            created.SetDisplayAspect(16d / 9);
            // A new output now faces projector-upright regardless of corner order.
            // Request each fixture's own facing so the rotated board stays rotated.
            created.SetBoardFacingDegrees(BoardOrientation.Heading(
                corners.Select(point => new Point2(point.X, point.Y)).ToArray(), 16d / 9));
            created.SetBoardSetup(true);
            Require(Math.Abs(created.SetDetectedBoardGrid(corners, Homography.FromFourPoints(unit, unit)) - .01f) < 1e-6,
                "The reference fixture no longer has its known one-percent calibration guard band.");
            created.SetBoardSetup(false);
            return created;
        }

        static Homography ReferenceMap(Vector2[] corners)
        {
            var center = corners.Aggregate(Vector2.Zero, (sum, point) => sum + point) / 4;
            return Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                corners.Select(point => Vector2.Lerp(point, center, .01f))
                    .Select(point => new Point2(point.X, point.Y)).ToArray());
        }

        static (double U, double V) SampleRequiredDensity(Homography map)
        {
            double maximumU = 0, maximumV = 0;
            double Distance(Point2 a, Point2 b) => Math.Sqrt(Math.Pow((a.X - b.X) * width, 2) + Math.Pow((a.Y - b.Y) * height, 2));
            foreach (double u in new[] { 0d, .25, .5, .75, 1 })
            foreach (double v in new[] { 0d, .25, .5, .75, 1 })
            {
                const double step = .00001;
                double lowU = Math.Max(0, u - step), highU = Math.Min(1, u + step);
                double lowV = Math.Max(0, v - step), highV = Math.Min(1, v + step);
                maximumU = Math.Max(maximumU, Distance(map.Transform(new(lowU, v)), map.Transform(new(highU, v))) / (highU - lowU));
                maximumV = Math.Max(maximumV, Distance(map.Transform(new(u, lowV)), map.Transform(new(u, highV))) / (highV - lowV));
            }
            return (maximumU, maximumV);
        }

        // Returns the caption's ink-coverage-weighted pixel centroid. Weighting
        // antialiased edges by coverage makes it independent of raster density
        // and orientation, unlike counting only fully coloured pixels.
        static Point2 MarkerCentroid(byte[] pixels, Homography map)
        {
            // The caption's logical box, clear of the backdrop's border lines
            // and of the Settings cog to its right.
            var box = new[] { new Point2(.465, .050), new Point2(.615, .050), new Point2(.615, .088), new Point2(.465, .088) }
                .Select(point => map.Transform(point)).ToArray();
            int left = Math.Max(0, (int)(box.Min(point => point.X) * width));
            int right = Math.Min(width - 1, (int)Math.Ceiling(box.Max(point => point.X) * width));
            int top = Math.Max(0, (int)(box.Min(point => point.Y) * height));
            int bottom = Math.Min(height - 1, (int)Math.Ceiling(box.Max(point => point.Y) * height));
            var blues = new List<int>();
            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                blues.Add(pixels[(y * width + x) * 4]);
            blues.Sort();
            // Most of the box is backdrop; its median blue, plus a small margin
            // for the backdrop's gentle gradient, is the local background.
            double background = blues[blues.Count / 2] + 6, ink = AppPalette.AccentSecondary.B;
            double xSum = 0, ySum = 0, weightSum = 0;
            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                double weight = Math.Clamp((pixels[(y * width + x) * 4] - background) / (ink - background), 0, 1);
                xSum += (x + .5) * weight; ySum += (y + .5) * weight; weightSum += weight;
            }
            Require(weightSum > 10, "The independently located menu marker was missing from the projected pixels.");
            return new(xSum / weightSum, ySum / weightSum);
        }

        static void Draw(SceneCompositor source, CanvasRenderTarget target, float logicalWidth, float logicalHeight,
            bool isPreview = false)
        {
            using var drawing = target.CreateDrawingSession();
            source.Draw(drawing, logicalWidth, logicalHeight, preview: isPreview, runningSlowly: false);
        }

        async Task Save(CanvasRenderTarget target, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }

        static (double MeanChannelError, int PixelsOver16) PixelDifference(byte[] first, byte[] second, Rect? region = null)
        {
            Require(first.Length == second.Length, "The physical output pixel buffers have different sizes.");
            var area = region ?? new Rect(0, 0, width, height);
            long error = 0, samples = 0;
            int different = 0;
            for (int y = (int)area.Y; y < (int)area.Bottom; y++)
            for (int x = (int)area.X; x < (int)area.Right; x++)
            {
                int offset = (y * width + x) * 4;
                int maximum = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(first[offset + channel] - second[offset + channel]);
                    error += delta;
                    maximum = Math.Max(maximum, delta);
                    samples++;
                }
                if (maximum > 16) different++;
            }
            return ((double)error / samples, different);
        }

        static int SharpHorizontalEdges(byte[] pixels, Rect region)
        {
            int count = 0;
            for (int y = (int)region.Y; y < (int)region.Bottom; y++)
            for (int x = (int)region.X + 1; x < (int)region.Right; x++)
            {
                int index = (y * width + x) * 4;
                int contrast = 0;
                for (int channel = 0; channel < 3; channel++)
                    contrast = Math.Max(contrast, Math.Abs(pixels[index + channel] - pixels[index - 4 + channel]));
                if (contrast >= 65) count++;
            }
            return count;
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
