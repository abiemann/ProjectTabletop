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
        using var cameraLayer = new CanvasRenderTarget(device, frameWidth, frameHeight, 96);
        using var boardLayer = new CanvasRenderTarget(device, 1000, 1000, 96);
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
        foreach (var quad in cameraQuads)
        {
            var mapping = Homography.FromFourPoints(quad, boardCorners);
            var inverse = mapping.Inverse().ToMatrix();
            var sprite = new PhotoHandCutout(spriteWidth, spriteHeight, source, new(60, 40), new(0, -1))
            {
                CameraGeometry = new(frameWidth, frameHeight, mapping.ToMatrix())
            };
            foreach (var placement in placements)
            {
                using (var drawing = boardLayer.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Transparent);
                    SceneCompositor.DrawPhotoCopyNativeLayer(drawing, cameraLayer, bitmap, sprite, [placement], 1);
                }
                ReconstructCamera(inverse);
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
                SceneCompositor.DrawPhotoCopyNativeLayer(drawing, cameraLayer, bitmap, sprite, placements, 0);
            }
            ReconstructCamera(inverse);
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
        using (var drawing = boardLayer.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            SceneCompositor.DrawPhotoCopyNativeLayer(drawing, cameraLayer, bitmap, finalSprite, placements, placements.Length);
        }
        ReconstructCamera(lastMap.Inverse().ToMatrix());
        string directory = Path.Combine(_appDataDirectory, "ProjectionSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"photo-copy-proportions-{Guid.NewGuid():N}.png");
        await reconstructed.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return new { passed = true, checkedCopies, anisotropicBoard = true, perspectiveBoard = true,
            nativeCameraProportions = true, inwardDirection = true, transparentLayerClears = true,
            worstRatioError, worstAxisDot, worstAnchorError, path };

        void ReconstructCamera(double[] h)
        {
            // Board pixels -> native camera pixels. This is the camera-view
            // equivalent of the calibrated outer board-to-projector mapping.
            var matrix = new Matrix4x4(
                (float)(h[0] / 1000), (float)(h[3] / 1000), 0, (float)(h[6] / 1000),
                (float)(h[1] / 1000), (float)(h[4] / 1000), 0, (float)(h[7] / 1000),
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
