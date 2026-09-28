#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Exercise the production native-photo layer and both projective GPU passes.
    // Measure the resulting photographed shape after reconstructing camera view;
    // matrix orthogonality alone would miss a second normalization/stretch.
    private async Task<object> VerifyPhotoCopyProportionsAsync()
    {
        const int frameWidth = 1600, frameHeight = 1000, spriteWidth = 120, spriteHeight = 80;
        var device = CanvasDevice.GetSharedDevice();
        using var reconstructed = new CanvasRenderTarget(device, frameWidth, frameHeight, 96);
        var source = new byte[spriteWidth * spriteHeight * 4];
        for (int y = 2; y < spriteHeight - 2; y++)
        for (int x = 2; x < spriteWidth - 2; x++)
        {
            int index = (y * spriteWidth + x) * 4;
            source[index] = source[index + 1] = source[index + 2] = 235;
            source[index + 3] = 255;
        }
        foreach (var (cx, cy, channel) in new[] { (16, 20, 2), (104, 20, 1), (16, 60, 0) })
        for (int y = cy - 5; y <= cy + 5; y++)
        for (int x = cx - 5; x <= cx + 5; x++)
        {
            int index = (y * spriteWidth + x) * 4;
            source[index] = source[index + 1] = source[index + 2] = 0;
            source[index + channel] = 255;
        }
        using var bitmap = CanvasBitmap.CreateFromBytes(device, source, spriteWidth, spriteHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        Point2[] boardCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Point2[][] cameraQuads =
        [
            [new(200, 150), new(1400, 150), new(1400, 850), new(200, 850)],
            [new(310, 105), new(1370, 215), new(1480, 870), new(130, 780)]
        ];
        var placements = new[]
        {
            new PhotoCopyPlacement(.25, .3, .27, .18, 0),
            new PhotoCopyPlacement(.75, .3, .27, .18, 0),
            new PhotoCopyPlacement(.75, .75, .27, .18, 0),
            new PhotoCopyPlacement(.25, .75, .27, .18, 0)
        };
        double worstRatioError = 0, worstAxisDot = 0, worstAnchorError = 0;
        int checkedCopies = 0;
        double largestCameraRasterScale = 1;
        foreach (var (boardWidth, boardHeight) in new[] { (1000, 1000), (3840, 2160) })
        foreach (var quad in cameraQuads)
        {
            var mapping = Homography.FromFourPoints(quad, boardCorners);
            var inverse = mapping.Inverse().ToMatrix();
            var sprite = new PhotoHandCutout(spriteWidth, spriteHeight, source, new(60, 40), new(0, -1))
            {
                CameraGeometry = new(frameWidth, frameHeight, mapping.ToMatrix())
            };
            float rasterScale = SceneCompositor.GetPhotoCopyCameraRasterScale(sprite.CameraGeometry,
                boardWidth, boardHeight, device.MaximumBitmapSizeInPixels);
            int cameraWidth = (int)Math.Ceiling(frameWidth * (double)rasterScale);
            int cameraHeight = (int)Math.Ceiling(frameHeight * (double)rasterScale);
            Require(cameraWidth <= device.MaximumBitmapSizeInPixels && cameraHeight <= device.MaximumBitmapSizeInPixels,
                "The native photograph layer exceeded the GPU texture limit.");
            if (boardWidth == 3840)
                Require(rasterScale > 1, "The 4K board retained a webcam-resolution photograph layer.");
            largestCameraRasterScale = Math.Max(largestCameraRasterScale, rasterScale);
            using var cameraLayer = new CanvasRenderTarget(device, cameraWidth, cameraHeight, 96);
            using var boardLayer = new CanvasRenderTarget(device, boardWidth, boardHeight, 96);
            foreach (var placement in placements)
            {
                using (var drawing = boardLayer.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Transparent);
                    drawing.Transform = Matrix3x2.CreateScale(boardWidth / 1000f, boardHeight / 1000f);
                    SceneCompositor.DrawPhotoCopyNativeLayer(drawing, cameraLayer, bitmap, sprite, [placement], 1, rasterScale);
                }
                ReconstructCamera(boardLayer, inverse);
                var pixels = reconstructed.GetPixelBytes();
                Vector2 red = MarkerCenter(pixels, 2), green = MarkerCenter(pixels, 1), blue = MarkerCenter(pixels, 0);
                var horizontal = green - red;
                var vertical = blue - red;
                double ratioError = Math.Abs(horizontal.Length() / vertical.Length() / (88d / 40) - 1);
                double axisDot = Math.Abs(Vector2.Dot(Vector2.Normalize(horizontal), Vector2.Normalize(vertical)));
                var measuredAnchor = red + horizontal * .5f + vertical * .5f;
                var expectedAnchorPoint = mapping.InverseTransform(new(placement.CenterU, placement.CenterV));
                var expectedAnchor = new Vector2((float)expectedAnchorPoint.X, (float)expectedAnchorPoint.Y);
                double anchorError = Vector2.Distance(measuredAnchor, expectedAnchor);
                var centerPoint = mapping.InverseTransform(new(.5, .5));
                var expectedInward = Vector2.Normalize(new Vector2((float)centerPoint.X, (float)centerPoint.Y) - expectedAnchor);
                Require(ratioError < .025, $"A native photo changed its camera aspect ratio by {ratioError:P1}.");
                Require(axisDot < .025, $"A native photo acquired skew ({axisDot:F4}).");
                Require(anchorError < 2.5, $"A native photo missed its calibrated destination by {anchorError:F2} pixels.");
                Require(Vector2.Dot(-Vector2.Normalize(vertical), expectedInward) > .999,
                    "The photograph's original upward axis did not face the board center in camera view.");
                worstRatioError = Math.Max(worstRatioError, ratioError);
                worstAxisDot = Math.Max(worstAxisDot, axisDot);
                worstAnchorError = Math.Max(worstAnchorError, anchorError);
                checkedCopies++;
            }
            // Reuse the same GPU target with zero stamps: previous photos must not
            // survive a layer rebuild. Session invalidation also disposes this target.
            using (var drawing = boardLayer.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.Transform = Matrix3x2.CreateScale(boardWidth / 1000f, boardHeight / 1000f);
                SceneCompositor.DrawPhotoCopyNativeLayer(drawing, cameraLayer, bitmap, sprite, placements, 0, rasterScale);
            }
            ReconstructCamera(boardLayer, inverse);
            var cleared = reconstructed.GetPixelBytes();
            Require(Enumerable.Range(0, cleared.Length / 4).All(i => cleared[i * 4 + 3] == 0),
                "The native camera layer retained pixels from a previous stamp draw.");
        }
        // Save a representative warped-board/camera round trip for visual review.
        var lastMap = Homography.FromFourPoints(cameraQuads[^1], boardCorners);
        var finalSprite = new PhotoHandCutout(spriteWidth, spriteHeight, source, new(60, 40), new(0, -1))
        {
            CameraGeometry = new(frameWidth, frameHeight, lastMap.ToMatrix())
        };
        float finalScale = SceneCompositor.GetPhotoCopyCameraRasterScale(finalSprite.CameraGeometry,
            3840, 2160, device.MaximumBitmapSizeInPixels);
        using var finalCameraLayer = new CanvasRenderTarget(device,
            (int)Math.Ceiling(frameWidth * (double)finalScale), (int)Math.Ceiling(frameHeight * (double)finalScale), 96);
        using var finalBoardLayer = new CanvasRenderTarget(device, 3840, 2160, 96);
        using (var drawing = finalBoardLayer.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            drawing.Transform = Matrix3x2.CreateScale(3.84f, 2.16f);
            SceneCompositor.DrawPhotoCopyNativeLayer(drawing, finalCameraLayer, bitmap, finalSprite,
                placements, placements.Length, finalScale);
        }
        ReconstructCamera(finalBoardLayer, lastMap.Inverse().ToMatrix());
        string directory = Path.Combine(_appDataDirectory, "ProjectionSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"photo-copy-proportions-{Guid.NewGuid():N}.png");
        await reconstructed.SaveAsync(path, CanvasBitmapFileFormat.Png);
        // Fine image detail must survive a small swirl stamp. Compare the old
        // webcam-sized intermediate with the production dense layer at the exact
        // same 4K destination; geometry-only assertions would miss this bottleneck.
        var stripes = new byte[source.Length];
        for (int y = 2; y < spriteHeight - 2; y++)
        for (int x = 2; x < spriteWidth - 2; x++)
        {
            int index = (y * spriteWidth + x) * 4;
            stripes[index] = stripes[index + 1] = stripes[index + 2] = (byte)((x / 4) % 2 == 0 ? 0 : 255);
            stripes[index + 3] = 255;
        }
        using var stripeBitmap = CanvasBitmap.CreateFromBytes(device, stripes, spriteWidth, spriteHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        var detailSprite = new PhotoHandCutout(spriteWidth, spriteHeight, stripes, new(60, 40), new(0, -1))
        {
            CameraGeometry = new(frameWidth, frameHeight, Homography.FromFourPoints(cameraQuads[0], boardCorners).ToMatrix())
        };
        float detailScale = SceneCompositor.GetPhotoCopyCameraRasterScale(detailSprite.CameraGeometry,
            3840, 2160, device.MaximumBitmapSizeInPixels);
        using var legacyCameraLayer = new CanvasRenderTarget(device, frameWidth, frameHeight, 96);
        double legacyDetailContrast = DetailContrast(legacyCameraLayer, 1);
        double nativeDetailContrast = DetailContrast(finalCameraLayer, detailScale);
        Require(nativeDetailContrast > legacyDetailContrast * 1.10 + 5,
            $"The projector-resolution photo layer did not retain more small-stamp detail ({nativeDetailContrast:F1} versus {legacyDetailContrast:F1}).");

        return new { passed = true, checkedCopies, anisotropicBoard = true, perspectiveBoard = true,
            nativeCameraProportions = true, inwardDirection = true, transparentLayerClears = true,
            fullResolutionBoard = true, largestCameraRasterScale,
            legacyDetailContrast, nativeDetailContrast,
            worstRatioError, worstAxisDot, worstAnchorError, path };

        double DetailContrast(CanvasRenderTarget layer, float scale)
        {
            using (var drawing = finalBoardLayer.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.Transform = Matrix3x2.CreateScale(3.84f, 2.16f);
                SceneCompositor.DrawPhotoCopyNativeLayer(drawing, layer, stripeBitmap, detailSprite,
                    [new(.3, .3, .0525, .035, 0)], 1, scale);
            }
            var pixels = finalBoardLayer.GetPixelBytes();
            double contrast = 0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 245) continue;
                contrast += Math.Abs(pixels[i] - 127.5);
                count++;
            }
            Require(count > 1000, "The small-stamp detail check did not render its photograph.");
            return contrast / count;
        }

        void ReconstructCamera(CanvasRenderTarget boardLayer, double[] h)
        {
            // Board pixels -> native camera pixels. This is the camera-view
            // equivalent of the calibrated outer board-to-projector mapping.
            double width = boardLayer.SizeInPixels.Width, height = boardLayer.SizeInPixels.Height;
            var matrix = new Matrix4x4(
                (float)(h[0] / width), (float)(h[3] / width), 0, (float)(h[6] / width),
                (float)(h[1] / height), (float)(h[4] / height), 0, (float)(h[7] / height),
                0, 0, 1, 0, (float)h[2], (float)h[5], 0, (float)h[8]);
            using var effect = new Transform3DEffect
            {
                Source = boardLayer, TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
            };
            using var drawing = reconstructed.CreateDrawingSession();
            drawing.Clear(Colors.Transparent);
            drawing.DrawImage(effect);
        }

        static Vector2 MarkerCenter(byte[] pixels, int channel)
        {
            double sumX = 0, sumY = 0, weight = 0;
            for (int y = 0; y < frameHeight; y++)
            for (int x = 0; x < frameWidth; x++)
            {
                int index = (y * frameWidth + x) * 4;
                int dominant = pixels[index + channel];
                int other = Math.Max(pixels[index + (channel + 1) % 3], pixels[index + (channel + 2) % 3]);
                if (dominant < 150 || other > 80 || pixels[index + 3] < 180) continue;
                int contrast = dominant - other;
                sumX += x * contrast; sumY += y * contrast; weight += contrast;
            }
            Require(weight > 1000, "A rendered native photograph lost a colored measurement marker.");
            return new((float)(sumX / weight), (float)(sumY / weight));
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
