#if DEBUG
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Theme snapshots use an isolated compositor. They never navigate the live
    // board, consume a real pinch, or enter camera recognition.
    private async Task<object> VerifyThemeAsync()
    {
        const int size = 1200;
        using var scene = new SceneCompositor();
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.015f, .015f), new(.985f, .015f), new(.985f, .985f), new(.015f, .985f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        string directory = Path.Combine(_appDataDirectory, "ThemeSnapshots", "theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();

        await Save("main-menu");
        var hoverTime = DateTimeOffset.UtcNow;
        scene.SetHandCursors([new(BoardPoint(.28, .33), DateTimeOffset.MinValue)], hoverTime);
        if (!scene.HoveredBoardButtons.SequenceEqual(["hand-tracking"]))
            throw new InvalidOperationException("The theme hover fixture did not select Hand-Tracking.");
        await Save("main-menu-hover");

        scene.ClearHandTips(resetInput: false);
        scene.ShowHandTrackingTest();
        await Save("hand-tracking");
        scene.ShowPhotoCopy();
        await Save("photo-copy");
        byte[] grey = target.GetPixelBytes();
        foreach (var point in new[] { BoardPoint(.2, .4), BoardPoint(.5, .55), BoardPoint(.8, .4) })
        {
            int index = ((int)(point.Y * size) * size + (int)(point.X * size)) * 4;
            if (grey[index] != AppPalette.PhotoCopyBackground.B || grey[index + 1] != AppPalette.PhotoCopyBackground.G ||
                grey[index + 2] != AppPalette.PhotoCopyBackground.R || grey[index + 3] != 255)
                throw new InvalidOperationException("The Photo Copy capture field is not solid neutral grey.");
        }

        long eventId = 0;
        foreach (BoardScreen screen in new[] { BoardScreen.Blackjack, BoardScreen.Paint, BoardScreen.Monopoly, BoardScreen.Diablo })
        {
            scene.ShowBoardMenu();
            await Task.Delay(2); // A new selection must follow external navigation.
            BoardButton button = new BoardSession().Buttons.Single(item => item.Destination == screen);
            var time = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(BoardPoint(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2), time.AddSeconds(1), ++eventId)], time);
            if (scene.CurrentBoardScreen != screen)
                throw new InvalidOperationException("The theme fixture could not open " + screen + ".");
            scene.ClearHandTips(resetInput: false);
            await Save(screen.ToString().ToLowerInvariant());
        }
        images.Add(await SaveLaptopThemeSnapshotAsync(directory));
        return new { passed = true, neutralGreyCaptureField = true, directory, images };

        PixelPoint BoardPoint(double u, double v) => new(.015 + .97 * (inset / 2 + u * (1 - inset)),
            .015 + .97 * (inset / 2 + v * (1 - inset)));

        async Task Save(string name)
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
    }

    private async Task<object> SaveLaptopThemeSnapshotAsync(string directory, FrameworkElement? element = null)
    {
        var root = element ?? Content as FrameworkElement;
        if (root is null || root.ActualWidth <= 0 || root.ActualHeight <= 0)
            throw new InvalidOperationException("The laptop window has no arranged content to capture.");
        root.UpdateLayout();
        var rendered = new RenderTargetBitmap();
        await rendered.RenderAsync(root);
        var buffer = await rendered.GetPixelsAsync();
        int width = rendered.PixelWidth, height = rendered.PixelHeight;
        if (width <= 0 || height <= 0 || buffer.Length != (long)width * height * 4)
            throw new InvalidOperationException("The laptop theme snapshot returned an empty or incomplete bitmap.");
        byte[] pixels = new byte[checked(width * height * 4)];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        bool visible = false;
        for (int index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 0) { visible = true; break; }
        if (!visible) throw new InvalidOperationException("The laptop theme snapshot is fully transparent.");
        // RenderTargetBitmap supplies premultiplied BGRA. The GPU camera and
        // projector canvases may be blank, but native WinUI controls are present.
        using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), pixels,
            width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        string path = Path.Combine(directory, "laptop-controls.png");
        await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return new { name = "laptop-controls", path, width, height,
            note = "Actual WinUI controls; GPU video canvases may not appear in this XAML snapshot." };
    }
}
#endif
