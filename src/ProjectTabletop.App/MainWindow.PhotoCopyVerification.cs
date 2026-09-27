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
    // Exercises the real GPU upload/warp path offscreen. No camera or projector
    // scene is altered, and the synthetic image never enters hand recognition.
    private async Task<object> VerifyPhotoCopyRenderAsync()
    {
        using var scene = new SceneCompositor();
        scene.ShowPhotoCopy();
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 800, 96);
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 800, 800, preview: false, runningSlowly: false);
        }
        Draw();
        var before = target.GetPixelBytes();
        int PixelIndex(double u, double v)
        {
            int x = (int)Math.Round(800 * (.1 + .8 * (inset / 2 + u * (1 - inset))));
            int y = (int)Math.Round(800 * (.1 + .8 * (inset / 2 + v * (1 - inset))));
            return (y * 800 + x) * 4;
        }
        await Task.Delay(1100);
        if (!scene.TryGetPhotoCopyCaptureContext(out var context))
            throw new InvalidOperationException("Offscreen white capture context did not become ready.");

        const int size = 80;
        var pixels = new byte[size * size * 4];
        for (int y = 5; y < size - 5; y++)
        for (int x = 25; x < 55; x++)
        {
            int index = (y * size + x) * 4;
            pixels[index] = 30;
            pixels[index + 1] = 100;
            pixels[index + 2] = 210;
            pixels[index + 3] = (byte)(x is 25 or 54 ? 96 : 255);
        }
        var sprite = new PhotoHandCutout(size, size, pixels, new(40, 55), new(0, -1));
        if (!scene.SetPhotoCopyCapture(sprite, context.Revision))
            throw new InvalidOperationException("Offscreen capture was not accepted.");
        await Task.Delay(1200);
        Draw(); // Upload the alpha-bearing bitmap and render the first copies.
        if (scene.PhotoCopyCount is < 1 or >= PhotoCopyLayout.DefaultCount)
            throw new InvalidOperationException("The first Photo Copy draw failed: " + scene.PhotoCopyStatus);
        var completionWait = Stopwatch.StartNew();
        while (scene.PhotoCopyCount < PhotoCopyLayout.DefaultCount && completionWait.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(100);
        Draw();
        if (scene.PhotoCopyCount != PhotoCopyLayout.DefaultCount)
            throw new InvalidOperationException("Photo Copy did not fill the board: " + scene.PhotoCopyStatus);
        var rendered = target.GetPixelBytes();
        var coloredPixels = 0;
        for (int index = 0; index < rendered.Length; index += 4)
            if (rendered[index + 2] > rendered[index] + 30 &&
                rendered[index + 2] > rendered[index + 1] + 30) coloredPixels++;
        if (coloredPixels < 200)
            throw new InvalidOperationException("Photo Copy counted stamps without rendering their color pixels.");
        // Check real rendered coverage near every corner, edge midpoint and center.
        foreach (var (u, v) in new[] { (.025, .025), (.5, .025), (.975, .025),
            (.025, .5), (.5, .5), (.975, .5), (.025, .975), (.5, .975), (.975, .975) })
        {
            var covered = 0;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var index = PixelIndex(u + dx * .005, v + dy * .005);
                if (rendered[index + 2] > rendered[index] + 30 &&
                    rendered[index + 2] > rendered[index + 1] + 30) covered++;
            }
            if (covered < 10)
                throw new InvalidOperationException($"Copies leave the board empty near ({u}, {v}).");
        }
        // Opaque buttons must remain identical even with hundreds of copies beneath.
        foreach (var u in new[] { .075, .655 })
        {
            var index = PixelIndex(u, .1);
            if (!before.AsSpan(index, 4).SequenceEqual(rendered.AsSpan(index, 4)))
                throw new InvalidOperationException("Photo Copy stamps covered a navigation button.");
        }
        var directory = Path.Combine(_appDataDirectory, "ProjectionSnapshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"photo-copy-verification-{Guid.NewGuid():N}.png");
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return new { passed = true, copies = scene.PhotoCopyCount, transparentSource = true,
            fullBoardCoverage = true, buttonsVisible = true, path };
    }
}
#endif
