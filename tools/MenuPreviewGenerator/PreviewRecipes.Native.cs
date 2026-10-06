using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace MenuPreviewGenerator;

internal sealed partial class PreviewRecipes
{
    private const int ImageWidth = 2304, ImageHeight = 512;
    private const float CanonicalSpan = 720, RouletteCompositionSpan = 512;
    private static readonly (BoardScreen Screen, string Stem)[] Boards =
    [
        (BoardScreen.HandTracking, "hand-tracking"), (BoardScreen.PhotoCopy, "photo-copy"),
        (BoardScreen.Blackjack, "blackjack"), (BoardScreen.Paint, "paint"),
        (BoardScreen.Monopoly, "crown-deed"), (BoardScreen.Globe, "globe"),
        (BoardScreen.Slots, "dragon-slots"), (BoardScreen.Roulette, "roulette")
    ];
    private readonly Assembly _app;
    private readonly Type _sceneType;
    private readonly IDisposable _scene;
    private readonly CanvasBitmap _dragon;
    private readonly object _globe;
    private readonly PaletteColors Palette;
    private readonly Vector3[] PaintPigments;
    private readonly Color MonopolyIvory, MonopolyInk;

    private PreviewRecipes(Assembly app, IDisposable scene, CanvasBitmap dragon, object globe)
    {
        _app = app; _scene = scene; _dragon = dragon; _globe = globe;
        _sceneType = scene.GetType();
        Palette = new(app.GetType("ProjectTabletop.App.AppPalette", true)!);
        PaintPigments = (Vector3[])_sceneType.GetField("PaintPigments", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        MonopolyIvory = (Color)_sceneType.GetField("MonopolyIvory", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        MonopolyInk = (Color)_sceneType.GetField("MonopolyInk", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    }

    public static async Task GenerateAsync(Assembly app, string root, string output, string comparisons)
    {
        Directory.CreateDirectory(output); Directory.CreateDirectory(comparisons);
        using var device = new CanvasDevice();
        Type sceneType = app.GetType("ProjectTabletop.App.Projection.SceneCompositor", true)!;
        var arguments = sceneType.GetConstructors().Single().GetParameters().Select(parameter =>
            parameter.Name == "globe" ? (object)GlobeHome.CreateDefault() :
            parameter.ParameterType == typeof(Func<DateTimeOffset>) ? new Func<DateTimeOffset>(() => DateTimeOffset.UnixEpoch) :
            parameter.DefaultValue).ToArray();
        using var scene = (IDisposable)sceneType.GetConstructors().Single().Invoke(arguments);
        await (Task)Call(scene, "EnsureGlobeResourcesAsync", device)!;
        await (Task)Call(scene, "EnsureRouletteResourcesAsync", device)!;
        object globe = Call(scene, "GetGlobeRenderer", device)!;
        using var dragon = await CanvasBitmap.LoadAsync(device,
            Path.Combine(AppContext.BaseDirectory, "SlotsRendering", "Assets", "slot-menu-dragon.png"), 96).AsTask();
        var recipes = new PreviewRecipes(app, scene, dragon, globe);
        var assets = new List<object>();
        var comparisonRecords = new List<object>();
        foreach (var (screen, stem) in Boards)
        {
            using var image = recipes.Render(device, screen, ImageWidth, ImageHeight, legacyRoulette: false);
            string path = Path.Combine(output, stem + ".png");
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask();
            // Validate the saved file, including its PNG colour type, rather
            // than assuming encoding retained the render target's alpha.
            byte[] encoded = File.ReadAllBytes(path);
            if (encoded.Length < 33 || !encoded.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("Output is not a PNG: " + path);
            using var decoded = await CanvasBitmap.LoadAsync(device, path, 96).AsTask();
            if (decoded.SizeInPixels.Width != ImageWidth || decoded.SizeInPixels.Height != ImageHeight)
                throw new InvalidOperationException("Saved thumbnail has incorrect dimensions: " + path);
            byte[] pixels = decoded.GetPixelBytes();
            int transparent = 0, translucent = 0, opaque = 0;
            for (int offset = 3; offset < pixels.Length; offset += 4)
                if (pixels[offset] == 0) transparent++;
                else if (pixels[offset] == 255) opaque++;
                else translucent++;
            if (screen == BoardScreen.Slots)
            {
                // The isolated dragon must have real alpha throughout empty
                // space; this deliberately forbids a checkerboard or matte.
                if (encoded[25] != 6 || transparent < ImageWidth * ImageHeight / 2 || translucent == 0 || opaque == 0)
                    throw new InvalidOperationException("Dragon thumbnail is not a clean RGBA cutout.");
                foreach (var (x, y) in new[] { (0, 0), (0, ImageHeight - 1), (ImageWidth - 1, 0), (ImageWidth - 1, ImageHeight - 1), (ImageWidth / 3, ImageHeight / 2) })
                    if (pixels[(y * ImageWidth + x) * 4 + 3] != 0)
                        throw new InvalidOperationException($"Dragon exterior alpha is nonzero at {x},{y}.");
            }
            assets.Add(new { board = screen.ToString(), file = stem + ".png", width = ImageWidth, height = ImageHeight,
                sha256 = Hash(path), bytes = new FileInfo(path).Length,
                pngColorType = encoded[25], alpha = new { transparent, translucent, opaque, exteriorVerified = screen == BoardScreen.Slots } });
            foreach (double aspect in new[] { 16.0 / 9, 1.4 })
            {
                // Main menu cards are .40×.16 of the board; artwork covers the
                // right .72, giving physical preview aspect = 1.8*boardAspect.
                int width = (int)Math.Round(ImageHeight * 1.8 * aspect);
                using var before = recipes.Render(device, screen, width, ImageHeight, legacyRoulette: true);
                using var after = new CanvasRenderTarget(device, width, ImageHeight, 96);
                using (var drawing = after.CreateDrawingSession())
                {
                    drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                    drawing.DrawImage(image, new Rect(0, 0, width, ImageHeight),
                        new Rect(ImageWidth - width, 0, width, ImageHeight), 1, CanvasImageInterpolation.HighQualityCubic);
                }
                string suffix = aspect > 1.7 ? "16x9" : "1.4x1";
                string beforeName = $"{stem}-{suffix}-before.png", afterName = $"{stem}-{suffix}-after.png";
                await before.SaveAsync(Path.Combine(comparisons, beforeName), CanvasBitmapFileFormat.Png).AsTask();
                await after.SaveAsync(Path.Combine(comparisons, afterName), CanvasBitmapFileFormat.Png).AsTask();
                byte[] previous = before.GetPixelBytes(), baked = after.GetPixelBytes();
                long absolute = 0;
                for (int i = 0; i < previous.Length; i++) absolute += Math.Abs(previous[i] - baked[i]);
                comparisonRecords.Add(new { board = screen.ToString(), boardAspect = aspect, width, height = ImageHeight,
                    before = beforeName, after = afterName, meanAbsoluteChannelDifference = absolute / (double)previous.Length });
            }
            Console.WriteLine($"Generated {stem}: {ImageWidth}×{ImageHeight}, {new FileInfo(path).Length:N0} bytes");
        }
        string[] sourceFiles =
        [
            "tools/MenuPreviewGenerator/Program.cs", "tools/MenuPreviewGenerator/PreviewRecipes.cs",
            "tools/MenuPreviewGenerator/PreviewRecipes.Native.cs", "tools/MenuPreviewGenerator/MenuPreviewGenerator.csproj",
            "tools/MenuPreviewGenerator/Generate.ps1", "src/ProjectTabletop.App/AppPalette.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.Blackjack.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.Monopoly.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.Paint.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.Roulette.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.RouletteMotion.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.RouletteMaterials.cs",
            "src/ProjectTabletop.App/Projection/RouletteRendering/RouletteWoodShader.cs",
            "src/ProjectTabletop.Interaction/GlobeHome.cs",
            "src/ProjectTabletop.App/SlotsRendering/Assets/slot-menu-dragon.png",
            "src/ProjectTabletop.App/GlobeRendering/Assets/earth-day-8192.png",
            "src/ProjectTabletop.App/GlobeRendering/Assets/earth-clouds-2048.jpg"
        ];
        var sources = sourceFiles.Concat(Directory.GetFiles(Path.Combine(root, "src/ProjectTabletop.App/Projection/PaintFluid"), "*.cs")
                .Concat(Directory.GetFiles(Path.Combine(root, "src/ProjectTabletop.App/Projection/GlobeRendering"), "*.cs"))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Distinct().OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { file = path, sha256 = Hash(Path.Combine(root, path)) }).ToArray();
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            recipeVersion = 1,
            generation = "Offline native Win2D using the project's existing artwork, geometry and shaders; never run during app startup.",
            width = ImageWidth, height = ImageHeight, logicalWidth = CanonicalSpan, logicalHeight = MenuPreviewUnits,
            framing = "Scale uniformly by destination height; align the right edge and crop the left; never stretch.",
            globe = new { rotationDegrees = GlobeHome.DefaultRestingRotationDegrees, latitudeDegrees = GlobeHome.DefaultViewLatitudeDegrees,
                imagery = "NASA Blue Marble; credits and source URLs in THIRD_PARTY_NOTICES.md" },
            paint = new { fieldWidth = 432, fieldHeight = 96, updates = 60, fixedStepsPerUpdate = 4, shaderPassesPerStep = 14,
                seed = "101 + dropIndex * 37" },
            assets, sources
        }, options) + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(comparisons, "comparisons.json"), JsonSerializer.Serialize(new
        {
            productionAssemblySha256 = Hash(app.Location), noMainWindowCameraOutputOrPipeConstructed = true,
            exactProductionBoardHelpers = true, recipeVersion = 1, comparisons = comparisonRecords
        }, options) + Environment.NewLine);
    }

    private CanvasRenderTarget Render(CanvasDevice device, BoardScreen screen, int width, int height, bool legacyRoulette)
    {
        var image = new CanvasRenderTarget(device, width, height, 96);
        IDisposable? fluid = null;
        try
        {
            using (var drawing = image.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                float scale = height / MenuPreviewUnits, span = width / scale;
                drawing.Transform = Matrix3x2.CreateScale(scale);
                switch (screen)
                {
                    case BoardScreen.HandTracking: DrawHandTrackingPreview(drawing, span); break;
                    case BoardScreen.PhotoCopy: DrawPhotoCopyPreview(drawing, span); break;
                    case BoardScreen.Blackjack: DrawBlackjackPreview(drawing, span); break;
                    case BoardScreen.Paint: fluid = DrawPaintPreview(drawing, span); break;
                    case BoardScreen.Monopoly: DrawMonopolyPreview(drawing, span); break;
                    case BoardScreen.Globe: DrawGlobePreview(drawing, span); break;
                    case BoardScreen.Slots: DrawSlotsPreview(drawing, span); break;
                    case BoardScreen.Roulette: DrawRoulettePreview(drawing, span, legacyRoulette); break;
                    default: throw new ArgumentOutOfRangeException(nameof(screen));
                }
            }
            return image;
        }
        catch { image.Dispose(); throw; }
        finally { fluid?.Dispose(); }
    }

    private void DrawSlotsPreview(CanvasDrawingSession drawing, float span)
    {
        const float height = MenuPreviewUnits - 6;
        double width = height * _dragon.Size.Width / _dragon.Size.Height;
        drawing.DrawImage(_dragon, new Rect(span - width - 4, 3, width, height),
            new Rect(0, 0, _dragon.Size.Width, _dragon.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
    }

    private void DrawGlobePreview(CanvasDrawingSession drawing, float span)
    {
        drawing.Clear(ThemeColor(2, 5, 11));
        const float zoom = .3f, radius = 66;
        float scale = radius / (335 * zoom);
        var center = new Vector2(span - 80, 80);
        var previous = drawing.Transform;
        drawing.Transform = Matrix3x2.CreateTranslation(-500, -500) * Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            if (Call(_globe, "Draw", drawing, zoom, (float)GlobeHome.DefaultRestingRotationDegrees, 1d,
                    (float)GlobeHome.DefaultViewLatitudeDegrees) is not true)
                throw new InvalidOperationException("NASA globe textures did not finish loading.");
        }
        finally { drawing.Transform = previous; }
    }

    private void DrawRoulettePreview(CanvasDrawingSession drawing, float span, bool legacy)
    {
        drawing.Clear(ThemeColor(8, 28, 35));
        float recipeSpan = legacy ? span : RouletteCompositionSpan;
        var previous = drawing.Transform;
        drawing.Transform = Matrix3x2.CreateTranslation(span - recipeSpan, 0) * previous;
        try
        {
            var game = new RouletteGame(7).Snapshot;
            CallBoard("DrawRouletteWheel", drawing, game, DateTimeOffset.UnixEpoch,
                new Vector2(recipeSpan * .70f, recipeSpan * .52f), 1d, recipeSpan / 390);
            CallBoard("DrawRouletteChip", drawing, new Vector2(recipeSpan * .35f, recipeSpan * .80f), recipeSpan * .08f, 25m, 1d, false, false);
            CallBoard("DrawRouletteChip", drawing, new Vector2(recipeSpan * .20f, recipeSpan * .76f), recipeSpan * .075f, 5m, 1d, false, false);
        }
        finally { drawing.Transform = previous; }
    }

    private IDisposable CreateFluid(CanvasDevice device, int width, int height) =>
        (IDisposable)Activator.CreateInstance(_app.GetType("ProjectTabletop.App.Projection.PaintFluid.PaintFluidSimulation", true)!,
            [device, width, height, width / (double)height])!;

    private void DrawDie(CanvasDrawingSession drawing, Rect bounds, int value, Color face, Color dots) =>
        CallBoard("DrawMonopolyDie", drawing, bounds, value, face, dots, 1d);

    private object? CallBoard(string name, params object?[] arguments) => Call(_scene, name, arguments);

    private static object? Call(object instance, string name, params object?[] arguments)
    {
        var method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length &&
                candidate.GetParameters().Zip(arguments).All(pair => pair.Second is null || pair.First.ParameterType.IsInstanceOfType(pair.Second)));
        try { return method.Invoke(method.IsStatic ? null : instance, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static Color ThemeColor(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class PaletteColors(Type type)
    {
        public Color Background => Get(nameof(Background));
        public Color GridLine => Get(nameof(GridLine));
        public Color IndicatorOn => Get(nameof(IndicatorOn));
        public Color PhotoCopyBackground => Get(nameof(PhotoCopyBackground));
        private Color Get(string name) => (Color)type.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    }
}
