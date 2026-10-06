using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ShortCaptionFixtureGenerator;

internal static class FixtureRecipe
{
    private const int CameraWidth = 3840, CameraHeight = 2160, ReferenceSize = 1000;
    // The historical native camera mapping remains fixed so the replacement
    // exercises the same caption sampling and foreground measurements.
    private const double CameraScaleX = .0002828463922517498, CameraScaleY = .0005028380306697774;
    private const double CameraOffset = -.04306506098490942;
    private static readonly BoardButton[] Buttons =
    [
        new("fixture-exit", "Exit", new(.825, .012, .15, .038), BoardScreen.CrownDeed),
        new("mp-human-minus", "-", new(.36, .42, .07, .058), BoardScreen.CrownDeed),
        new("mp-human-plus", "+", new(.57, .42, .07, .058), BoardScreen.CrownDeed),
        new("mp-ai-minus", "-", new(.36, .53, .07, .058), BoardScreen.CrownDeed),
        new("mp-ai-plus", "+", new(.57, .53, .07, .058), BoardScreen.CrownDeed),
        new("mp-start", "Start", new(.365, .665, .27, .072), BoardScreen.CrownDeed),
        new("mp-setup-cancel", "Cancel", new(.395, .755, .21, .048), BoardScreen.CrownDeed)
    ];

    public static async Task GenerateAsync(Assembly application, string output)
    {
        Directory.CreateDirectory(output);
        using var device = new CanvasDevice();
        Type sceneType = application.GetType("ProjectTabletop.App.Projection.SceneCompositor", true)!;
        var constructor = sceneType.GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(Func<DateTimeOffset>)
                ? (object)new Func<DateTimeOffset>(() => DateTimeOffset.UnixEpoch)
                : parameter.DefaultValue).ToArray();
        using var scene = (IDisposable)constructor.Invoke(arguments);
        MethodInfo drawButton = sceneType.GetMethod("DrawCrownDeedButton", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(sceneType.FullName, "DrawCrownDeedButton");
        using var expected = Render(device, ReferenceSize, ReferenceSize, Matrix3x2.Identity, scene, drawButton);
        var cameraTransform = Matrix3x2.CreateScale((float)(1 / (1000 * CameraScaleX)), (float)(1 / (1000 * CameraScaleY))) *
            Matrix3x2.CreateTranslation((float)(-CameraOffset / CameraScaleX), (float)(-CameraOffset / CameraScaleY));
        using var empty = Render(device, CameraWidth, CameraHeight, cameraTransform, scene, drawButton);
        byte[] occupiedPixels = empty.GetPixelBytes();
        int left = CameraX(.381), right = CameraX(.619), top = CameraY(.678), bottom = CameraY(.724);
        int span = right - left;
        for (int finger = 0; finger < 4; finger++)
        for (int y = top; y < bottom; y++)
        for (int x = left + finger * span / 4; x < left + finger * span / 4 + span / 4 - 2; x++)
        {
            int offset = (y * CameraWidth + x) * 4;
            occupiedPixels[offset] = 15; occupiedPixels[offset + 1] = 20;
            occupiedPixels[offset + 2] = 225; occupiedPixels[offset + 3] = 255;
        }
        using var occupied = CanvasBitmap.CreateFromBytes(device, occupiedPixels, CameraWidth, CameraHeight,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        var files = new List<object>();
        foreach (var (kind, image) in new (string, CanvasBitmap)[] { ("expected", expected), ("empty", empty), ("occupied", occupied) })
        {
            string name = "short-gold-caption-" + kind + ".png";
            string path = Path.Combine(output, name);
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask();
            using var saved = await CanvasBitmap.LoadAsync(device, path, 96).AsTask();
            if (!saved.GetPixelBytes().SequenceEqual(image.GetPixelBytes()))
                throw new InvalidOperationException("The saved PNG altered optical fixture pixels: " + name);
            files.Add(new { file = name, width = (int)saved.SizeInPixels.Width, height = (int)saved.SizeInPixels.Height,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "short-gold-caption.json"),
            JsonSerializer.Serialize(new { recipe = 1, renderer = "Win2D production control plates on a neutral calibration scene",
                camera = new { width = CameraWidth, height = CameraHeight, scaleX = CameraScaleX,
                    scaleY = CameraScaleY, offset = CameraOffset },
                obstruction = new { left, right, top, bottom, strips = 4, gapPixels = 2, bgra = new[] { 15, 20, 225, 255 } },
                files }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine("Generated and losslessly verified three neutral short-caption fixtures.");
    }

    private static CanvasRenderTarget Render(CanvasDevice device, int width, int height, Matrix3x2 transform,
        object scene, MethodInfo drawButton)
    {
        var target = new CanvasRenderTarget(device, width, height, 96);
        try
        {
            using var drawing = target.CreateDrawingSession();
            drawing.Clear(Color.FromArgb(255, 0, 0, 0));
            drawing.Transform = transform;
            drawing.FillRectangle(new Rect(0, 0, 1000, 1000), Color.FromArgb(255, 15, 43, 32));
            drawing.DrawRoundedRectangle(new Rect(9, 9, 982, 982), 18, 18, Color.FromArgb(255, 222, 188, 112), 2);
            using var format = new CanvasTextFormat
            {
                FontFamily = "Bahnschrift", FontSize = 26,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            drawing.DrawText("OPTICAL CONTROL FIXTURE", new Rect(210, 210, 580, 65),
                Color.FromArgb(255, 253, 246, 223), format);
            drawing.DrawText("Independent colour references", new Rect(210, 295, 580, 40),
                Color.FromArgb(255, 175, 199, 178), format);
            // Fixed independent colour bands replace named property artwork.
            // Their variation constrains camera-colour fitting without using
            // the physically covered gold control as its own lighting reference.
            Color[] colors =
            [
                Color.FromArgb(255, 236, 230, 208), Color.FromArgb(255, 108, 69, 46),
                Color.FromArgb(255, 34, 81, 125), Color.FromArgb(255, 168, 54, 51),
                Color.FromArgb(255, 55, 116, 80), Color.FromArgb(255, 208, 177, 91),
                Color.FromArgb(255, 107, 76, 132), Color.FromArgb(255, 119, 143, 151)
            ];
            foreach (int bandTop in new[] { 45, 835 })
            for (int row = 0; row < 2; row++)
            for (int column = 0; column < 8; column++)
                drawing.FillRectangle(new Rect(180 + column * 80, bandTop + row * 60, 80, 60),
                    colors[(column + row * 3) % colors.Length]);
            foreach (var button in Buttons) drawButton.Invoke(scene, [drawing, button, false, 1d]);
            return target;
        }
        catch { target.Dispose(); throw; }
    }
    private static int CameraX(double board) => (int)((board - CameraOffset) / CameraScaleX);
    private static int CameraY(double board) => (int)((board - CameraOffset) / CameraScaleY);
}
