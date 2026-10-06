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
        (BoardScreen.Blackjack, "blackjack"), (BoardScreen.Paint, "paint"), (BoardScreen.WaterGarden, "water-garden"),
        (BoardScreen.CrownDeed, "crown-deed"), (BoardScreen.Globe, "globe"),
        (BoardScreen.Slots, "dragon-slots"), (BoardScreen.Roulette, "roulette")
    ];
    private readonly Assembly _app;
    private readonly Type _sceneType;
    private readonly IDisposable _scene;
    private readonly CanvasBitmap _dragon;
    private readonly CanvasBitmap _waterRocks;
    private readonly CanvasRenderTarget _crownDeedBoard;
    private readonly object _globe;
    private readonly PaletteColors Palette;
    private readonly Vector3[] PaintPigments;
    private readonly Color CrownDeedIvory, CrownDeedInk;

    private PreviewRecipes(Assembly app, IDisposable scene, CanvasBitmap dragon, object globe,
        CanvasRenderTarget crownDeedBoard, CanvasBitmap waterRocks)
    {
        _app = app; _scene = scene; _dragon = dragon; _globe = globe; _crownDeedBoard = crownDeedBoard;
        _waterRocks = waterRocks;
        _sceneType = scene.GetType();
        Palette = new(app.GetType("ProjectTabletop.App.AppPalette", true)!);
        PaintPigments = (Vector3[])_sceneType.GetField("PaintPigments", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        CrownDeedIvory = (Color)_sceneType.GetField("CrownDeedIvory", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        CrownDeedInk = (Color)_sceneType.GetField("CrownDeedInk", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
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
        await (Task)Call(scene, "EnsureCrownDeedResourcesAsync", device)!;
        object globe = Call(scene, "GetGlobeRenderer", device)!;
        using var dragon = await CanvasBitmap.LoadAsync(device,
            Path.Combine(AppContext.BaseDirectory, "SlotsRendering", "Assets", "slot-menu-dragon.png"), 96).AsTask();
        using var crownDeedBoard = RenderCrownDeedBoard(device, scene);
        using var waterRocks = await CanvasBitmap.LoadAsync(device,
            Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "moss-rocks.png"), 96).AsTask();
        await crownDeedBoard.SaveAsync(Path.Combine(comparisons, "crown-deed-native-board.png"), CanvasBitmapFileFormat.Png).AsTask();
        var recipes = new PreviewRecipes(app, scene, dragon, globe, crownDeedBoard, waterRocks);
        var assets = new List<object>();
        var comparisonRecords = new List<object>();
        foreach (var (screen, stem) in Boards)
        {
            using var image = recipes.Render(device, screen, ImageWidth, ImageHeight, legacy: false);
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
                using var before = recipes.Render(device, screen, width, ImageHeight, legacy: true);
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
            "src/ProjectTabletop.App/Projection/SceneCompositor.CrownDeed.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.CrownDeedAssets.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.CrownDeedBuildings.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.CrownDeedPieces.cs",
            "src/ProjectTabletop.App/Projection/CrownDeedPieceSources.cs",
            "src/ProjectTabletop.Interaction/CrownDeedBoard.cs",
            "src/ProjectTabletop.Interaction/CrownDeedGame.cs",
            "src/ProjectTabletop.Interaction/CrownDeedModels.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.Paint.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.Roulette.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.RouletteMotion.cs",
            "src/ProjectTabletop.App/Projection/SceneCompositor.RouletteMaterials.cs",
            "src/ProjectTabletop.App/Projection/RouletteRendering/RouletteWoodShader.cs",
            "src/ProjectTabletop.Interaction/GlobeHome.cs",
            "src/ProjectTabletop.App/SlotsRendering/Assets/slot-menu-dragon.png",
            "src/ProjectTabletop.App/Assets/WaterGarden/moss-rocks.png",
            "src/ProjectTabletop.App/GlobeRendering/Assets/earth-day-8192.png",
            "src/ProjectTabletop.App/GlobeRendering/Assets/earth-clouds-2048.jpg"
        ];
        var sources = sourceFiles.Concat(Directory.GetFiles(Path.Combine(root, "src/ProjectTabletop.App/Assets/CrownDeed"), "*.png", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Concat(Directory.GetFiles(Path.Combine(root, "src/ProjectTabletop.App/Projection/PaintFluid"), "*.cs")
                .Concat(Directory.GetFiles(Path.Combine(root, "src/ProjectTabletop.App/Projection/WaterGarden"), "*.cs"))
                .Concat(Directory.GetFiles(Path.Combine(root, "src/ProjectTabletop.App/Projection/GlobeRendering"), "*.cs"))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Distinct().OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { file = path, sha256 = Hash(Path.Combine(root, path)) }).ToArray();
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            recipeVersion = 4,
            generation = "Offline native Win2D using the project's existing artwork, geometry and shaders; never run during app startup.",
            width = ImageWidth, height = ImageHeight, logicalWidth = CanonicalSpan, logicalHeight = MenuPreviewUnits,
            framing = "Scale uniformly by destination height; align the right edge and crop the left; never stretch.",
            crownDeed = new { composition = "Close-up of the native lamplit city, property boulevard and silver pieces.",
                boardRaster = 2400, crop = new { x = 0, y = 0, width = 1000, height = 350 },
                snapshot = "Fixed representative game; hat on Amber Exchange, car on Festival Dues, three shops on each Crown Quarter property.",
                water = "Original still city artwork" },
            roulette = new { composition = "Full bowl and spindle framed to the thumbnail height.",
                wheelFromRight = 102, wheelY = 77, wheelScale = .48 },
            globe = new { rotationDegrees = GlobeHome.DefaultRestingRotationDegrees, latitudeDegrees = GlobeHome.DefaultViewLatitudeDegrees,
                imagery = "NASA Blue Marble; credits and source URLs in THIRD_PARTY_NOTICES.md" },
            paint = new { fieldWidth = 432, fieldHeight = 96, updates = 60, fixedStepsPerUpdate = 4, shaderPassesPerStep = 14,
                seed = "101 + dropIndex * 37" },
            waterGarden = new { composition = "Native clear-water surface over original procedural river stones.",
                attribution = "Evan Wallace WebGL Water, MIT; see THIRD_PARTY_NOTICES.md" },
            assets, sources
        }, options) + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(comparisons, "comparisons.json"), JsonSerializer.Serialize(new
        {
            productionAssemblySha256 = Hash(app.Location), noMainWindowCameraOutputOrPipeConstructed = true,
            exactProductionBoardHelpers = true, recipeVersion = 4, comparisons = comparisonRecords
        }, options) + Environment.NewLine);
    }

    private CanvasRenderTarget Render(CanvasDevice device, BoardScreen screen, int width, int height, bool legacy)
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
                    case BoardScreen.WaterGarden: fluid = DrawWaterGardenPreview(drawing, span); break;
                    case BoardScreen.CrownDeed:
                        if (legacy) DrawLegacyCrownDeedPreview(drawing, span);
                        else DrawCrownDeedPreview(drawing, span);
                        break;
                    case BoardScreen.Globe: DrawGlobePreview(drawing, span); break;
                    case BoardScreen.Slots: DrawSlotsPreview(drawing, span); break;
                    case BoardScreen.Roulette: DrawRoulettePreview(drawing, span, legacy); break;
                    default: throw new ArgumentOutOfRangeException(nameof(screen));
                }
            }
            return image;
        }
        catch { image.Dispose(); throw; }
        finally { fluid?.Dispose(); }
    }

    private static CanvasRenderTarget RenderCrownDeedBoard(CanvasDevice device, IDisposable scene)
    {
        var game = new CrownDeedGame(seed: 27);
        if (!game.HandleAction("mp-start-game", DateTimeOffset.UnixEpoch) ||
            !game.HandleAction("mp-start", DateTimeOffset.UnixEpoch))
            throw new InvalidOperationException("Could not prepare the Crown & Deed preview fixture.");
        var gameState = game.Snapshot;
        var snapshot = gameState with
        {
            Players = gameState.Players.Select((player, index) => player with
                { Position = index == 0 ? 21 : 24, PieceIndex = index }).ToArray(),
            Properties = gameState.Properties.Select(property => property.SpaceIndex is 21 or 22
                ? property with { OwnerId = gameState.Players[0].Id, Houses = 3 } : property).ToArray()
        };
        var board = new CanvasRenderTarget(device, 2400, 2400, 96);
        try
        {
            using var drawing = board.CreateDrawingSession();
            drawing.Transform = Matrix3x2.CreateScale(2.4f);
            Call(scene, "DrawCrownDeedFrame", drawing);
            foreach (var space in CrownDeedGame.Spaces)
                Call(scene, "DrawCrownDeedSpace", drawing, space, snapshot, 1d, 1f);
            return board;
        }
        catch { board.Dispose(); throw; }
    }

    private void DrawCrownDeedPreview(CanvasDrawingSession drawing, float span)
    {
        drawing.Clear(ThemeColor(9, 25, 22));
        // Extend the native scene leftward at the same scale. Its boundary then
        // stays outside the menu's visible artwork on both supported card shapes.
        const float cropHeight = 350, cropWidth = 1000;
        float width = MenuPreviewUnits * cropWidth / cropHeight;
        double density = _crownDeedBoard.Size.Width / 1000;
        drawing.DrawImage(_crownDeedBoard, new Rect(span - width, 0, width, MenuPreviewUnits),
            new Rect(0, 0, cropWidth * density, cropHeight * density),
            1, CanvasImageInterpolation.HighQualityCubic);
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
        float recipeSpan = RouletteCompositionSpan;
        var previous = drawing.Transform;
        drawing.Transform = Matrix3x2.CreateTranslation(span - recipeSpan, 0) * previous;
        try
        {
            var game = new RouletteGame(7).Snapshot;
            if (legacy)
            {
                // Retain the previously shipped thumbnail for before/after comparisons.
                CallBoard("DrawRouletteWheel", drawing, game, DateTimeOffset.UnixEpoch,
                    new Vector2(recipeSpan * .70f, recipeSpan * .52f), 1d, recipeSpan / 390);
                CallBoard("DrawRouletteChip", drawing, new Vector2(recipeSpan * .35f, recipeSpan * .80f), recipeSpan * .08f, 25m, 1d, false, false);
                CallBoard("DrawRouletteChip", drawing, new Vector2(recipeSpan * .20f, recipeSpan * .76f), recipeSpan * .075f, 5m, 1d, false, false);
            }
            else
            {
                // Frame the full wheel against the 160-unit HEIGHT. A width-based
                // center/scale hid the spindle below the tile and left its top empty.
                CallBoard("DrawRouletteWheel", drawing, game, DateTimeOffset.UnixEpoch,
                    new Vector2(recipeSpan - 102, 77), 1d, .48f);
            }
        }
        finally { drawing.Transform = previous; }
    }

    private IDisposable CreateFluid(CanvasDevice device, int width, int height) =>
        (IDisposable)Activator.CreateInstance(_app.GetType("ProjectTabletop.App.Projection.PaintFluid.PaintFluidSimulation", true)!,
            [device, width, height, width / (double)height])!;

    private void DrawDie(CanvasDrawingSession drawing, Rect bounds, int value, Color face, Color dots) =>
        CallBoard("DrawCrownDeedDie", drawing, bounds, value, face, dots, 1d);

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
