#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using ProjectTabletop.Interaction;
using ProjectTabletop.App.Projection;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyDuckHuntCoverResolutionAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntCoverResolution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var scene = new SceneCompositor();
        var device = CanvasDevice.GetSharedDevice();
        scene.ShowDuckHuntTargetPractice();
        await scene.EnsureDuckHuntResourcesAsync(device);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var front = (CanvasRenderTarget)typeof(SceneCompositor).GetField("_galleryFlapFront", fields)!.GetValue(scene)!;
        var back = (CanvasRenderTarget)typeof(SceneCompositor).GetField("_galleryFlapBack", fields)!.GetValue(scene)!;
        var effect = (Transform3DEffect)typeof(SceneCompositor).GetField("_galleryFlapEffect", fields)!.GetValue(scene)!;
        var drawFlap = typeof(SceneCompositor).GetMethod("DrawGalleryFlap", fields)!;
        var results = new List<object>();
        int assertions = 0;
        foreach (var size in new[] { (Width: 3840, Height: 2160), (Width: 2682, Height: 2143) })
        {
            double aspect = size.Width / (double)size.Height;
            var transform = Matrix3x2.CreateScale(size.Width / 1000f, size.Height / 1000f);
            var closed = DuckHuntGalleryLayout.SceneRect(DuckHuntGalleryLayout.FlapSceneBounds, aspect);
            using var reference = new CanvasRenderTarget(device, size.Width, size.Height, 96);
            using (var drawing = reference.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = transform;
                drawing.DrawImage(front, new Rect(closed.X * 1000, closed.Y * 1000, closed.Width * 1000, closed.Height * 1000),
                    new Rect(0, 0, front.Size.Width, front.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
            }
            foreach (double degrees in new[] { 0d, 30d, 95d })
            {
                using var actual = new CanvasRenderTarget(device, size.Width, size.Height, 96);
                using (var drawing = actual.CreateDrawingSession())
                {
                    drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                    drawing.Transform = transform;
                    drawFlap.Invoke(scene, [drawing, degrees, aspect]);
                    Check(drawing.Transform == transform, "Drawing the flap changed the subsequent board transform.");
                }
                double meanError = 0;
                if (degrees == 0)
                {
                    // Compare the cover interior to direct native-resolution HQC.
                    // Exclude the physical hinges and bottom edge strokes.
                    byte[] expected = reference.GetPixelBytes(), observed = actual.GetPixelBytes();
                    int x0 = (int)(closed.X * size.Width) + 24, x1 = (int)((closed.X + closed.Width) * size.Width) - 24;
                    int y0 = (int)(closed.Y * size.Height) + 24, y1 = (int)((closed.Y + closed.Height) * size.Height) - 24;
                    long error = 0, samples = 0;
                    for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int p = (y * size.Width + x) * 4 + channel;
                        error += Math.Abs(expected[p] - observed[p]);
                        samples++;
                    }
                    meanError = error / (double)samples;
                    Check(meanError < .05, $"The closed cover lost native engraving detail: mean error {meanError:F3}/255.");
                }
                else
                {
                    bool reverse = degrees == 95;
                    var face = reverse ? back : front;
                    Check(ReferenceEquals(effect.Source, face), "The rotating flap showed the wrong decorated/wooden face.");
                    var flap = DuckHuntGalleryLayout.FlapSceneBounds;
                    double top = DuckHuntGalleryLayout.SourceYToLayoutY(flap.Y);
                    double height = DuckHuntGalleryLayout.SourceYToLayoutY(flap.Y + flap.Height) - top;
                    double angle = degrees * Math.PI / 180, depth = height * Math.Sin(angle);
                    double center = flap.X + flap.Width / 2, reach = flap.Width / (1 - depth * .35);
                    double bottom = top + height * Math.Cos(angle) - depth * .55;
                    var corners = new[] {
                        (X: 0d, Y: 0d, Point: DuckHuntGalleryLayout.LayoutPoint(flap.X, top, aspect)),
                        (X: face.Size.Width, Y: 0d, Point: DuckHuntGalleryLayout.LayoutPoint(flap.X + flap.Width, top, aspect)),
                        (X: 0d, Y: face.Size.Height, Point: DuckHuntGalleryLayout.LayoutPoint(center - reach / 2, bottom, aspect)),
                        (X: face.Size.Width, Y: face.Size.Height, Point: DuckHuntGalleryLayout.LayoutPoint(center + reach / 2, bottom, aspect)) };
                    foreach (var corner in corners)
                    {
                        var point = Vector4.Transform(new Vector4((float)corner.X, (float)corner.Y, 0, 1), effect.TransformMatrix);
                        double dx = point.X / point.W - corner.Point.U * size.Width;
                        double dy = point.Y / point.W - corner.Point.V * size.Height;
                        Check(Math.Abs(dx) < .02 && Math.Abs(dy) < .02,
                            "The moving flap no longer resolves its original corners at output pixel density.");
                    }
                }
                string image = Path.Combine(directory, $"{size.Width}x{size.Height}-{degrees}.png");
                await actual.SaveAsync(image, CanvasBitmapFileFormat.Png);
                results.Add(new { size.Width, size.Height, Degrees = degrees, MeanClosedHqcError = meanError, Image = image });
            }
        }
        return new { Ok = true, Assertions = assertions, Results = results, Directory = directory };
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
    }
}
#endif
