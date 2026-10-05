#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private games and offscreen GPU surfaces only. The calling window supplies
    // an output directory; no camera, projector, control pipe or user save is used.
    private async Task<object> VerifyCrownDeedPiecesAsync()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(SceneCompositor);
        var drawPiece = type.GetMethod("DrawCrownDeedPiece", instance)!;
        var heading = type.GetMethod("CrownDeedPieceHeading", statics)!;
        var artAspect = type.GetNestedType("MonopolyArtAspect", BindingFlags.NonPublic)!;
        Require(drawPiece is not null && heading is not null, "The silver-piece renderer or shared inward heading is missing.");
        string directory = Path.Combine(_appDataDirectory, "CrownDeedPieceVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var assets = new List<object>();
        var orientationSamples = new List<object>();
        int assetChecks = 0, sourceBoundsChecks = 0, orientationChecks = 0, nativeDrawChecks = 0;
        int repeatChecks = 0, stateChecks = 0, cacheChecks = 0, captionChecks = 0;
        int footHeadingChecks = 0, physicalProportionChecks = 0;
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var game = new MonopolyGame(seed: 421);
        foreach (string action in new[] { "mp-start-game", "mp-piece-next-1", "mp-piece-next-2", "mp-start" })
            Require(game.HandleAction(action, now), "The silver-piece fixture could not configure its game.");
        using var scene = new SceneCompositor(monopoly: game, monopolyClock: () => now, boardRevealClock: () => now);
        string saved = scene.ExportMonopolySave();
        var frozen = scene.MonopolyState;
        var device = CanvasDevice.GetSharedDevice();
        using (var warm = new CanvasRenderTarget(device, 256, 256, 96))
        using (var drawing = warm.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            for (int piece = 0; piece < 8; piece++) Draw(drawing, new(128, 128), 36, piece, 1, false);
        }
        var bitmaps = Field<CanvasBitmap?[]>(scene, "_crownDeedPieceBitmaps");
        var bounds = Field<Rect[]>(scene, "_crownDeedPieceSourceBounds");
        Require(bitmaps.Length == 8 && bitmaps.All(bitmap => bitmap is not null) && bounds.Length == 8,
            "Not all eight silver-piece assets were loaded.");
        Require(ReferenceEquals(device, Raw(scene, "_crownDeedPieceDevice")), "Piece textures were cached on another device.");
        var hashes = new HashSet<string>();
        for (int piece = 0; piece < 8; piece++)
        {
            var bitmap = bitmaps[piece]!;
            int width = (int)bitmap.SizeInPixels.Width, height = (int)bitmap.SizeInPixels.Height;
            byte[] pixels = bitmap.GetPixelBytes();
            var source = bounds[piece];
            Require(width >= 128 && height >= 128 && pixels.Length == width * height * 4,
                "A silver-piece source is missing or below useful source resolution.");
            int solid = 0, partial = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte alpha = pixels[(y * width + x) * 4 + 3];
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                    Require(alpha == 0, "A silver-piece source has an opaque outer border.");
                if (alpha > 0) Require(x >= source.X && x < source.X + source.Width &&
                    y >= source.Y && y < source.Y + source.Height, "Source trimming discarded visible piece artwork.");
                // Generated metal interiors are near opaque (mostly alpha252/253).
                // Require substantial coverage, without mistaking quantization
                // below255 for a missing object or an antialiased silhouette.
                if (alpha >= 250) solid++; else if (alpha > 0) partial++;
            }
            string hash = Convert.ToHexString(SHA256.HashData(pixels));
            Require(solid > 1000 && partial > 0 && solid < width * height * .90 && hashes.Add(hash),
                "A piece lacks substantial distinct artwork, transparency or transitional edge pixels: " + MonopolyGame.PieceNames[piece]);
            assetChecks++;
            Require(source.X >= 0 && source.Y >= 0 && source.Width > 1 && source.Height > 1 &&
                source.X + source.Width <= width && source.Y + source.Height <= height,
                "A trimmed silver-piece source rectangle exceeds its bitmap.");
            sourceBoundsChecks++;
            assets.Add(new { piece, name = MonopolyGame.PieceNames[piece], width, height, solid, partial, substantialAlphaMinimum = 250, hash, source });
        }

        foreach (var view in new[] { (Name: "measured", Width: 1440, Height: 1120),
            (Name: "wide", Width: 1600, Height: 900), (Name: "square", Width: 1200, Height: 1200),
            (Name: "portrait", Width: 900, Height: 1600) })
        {
            double aspect = view.Width / (double)view.Height;
            using var target = new CanvasRenderTarget(device, view.Width, view.Height, 96);
            for (int piece = 0; piece < 8; piece++)
            {
                byte[] first = RenderPieces();
                Require(first.SequenceEqual(RenderPieces()), "Repeated silver-piece rendering changed at the same clock.");
                repeatChecks++;
                Require(first.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 1000,
                    "The native piece layout was blank.");
                Require(ReferenceEquals(frozen, scene.MonopolyState) && scene.ExportMonopolySave() == saved,
                    "Drawing pieces changed player identities, selected pieces or gameplay.");
                stateChecks++;
                Require(ReferenceEquals(bitmaps, Raw(scene, "_crownDeedPieceBitmaps")) &&
                    ReferenceEquals(bounds, Raw(scene, "_crownDeedPieceSourceBounds")) &&
                    bitmaps.All(bitmap => bitmap!.Device == device), "Drawing or resizing recreated piece textures.");
                cacheChecks++;
                await Capture(target, view.Name + "-" + MonopolyGame.PieceNames[piece].ToLowerInvariant() + "-forty-parcels");

                byte[] RenderPieces()
                {
                    using (var drawing = target.CreateDrawingSession())
                    {
                        drawing.Clear(Colors.Transparent);
                        drawing.Transform = Matrix3x2.CreateScale(view.Width / 1000f, view.Height / 1000f);
                        for (int parcel = 0; parcel < 40; parcel++)
                        {
                            CheckHeadingAndProportions(SceneCompositor.CrownDeedParcelPose(parcel).Center, false);
                            // Probe the actual foot row, including the sideways
                            // displacement of crowded players. Parcel centres alone
                            // cannot catch a tangent-based or displaced-ray error.
                            foreach (int occupants in new[] { 1, 2, 6 })
                            {
                                var fixture = frozen with { Players = Array.AsReadOnly(Enumerable.Range(0, occupants)
                                    .Select(slot => new MonopolyPlayerSnapshot(slot + 1, "Fixture", false, 1500,
                                        parcel, false, 0, 0, false, slot) { PieceIndex = piece }).ToArray()) };
                                foreach (var player in fixture.Players)
                                    CheckHeadingAndProportions(SceneCompositor.MonopolyTokenCenter(fixture, player.Id), true);
                            }
                            var single = frozen with { Players = Array.AsReadOnly(frozen.Players.Select((player, slot) =>
                                player with { Position = slot == 0 ? parcel : (parcel + 20) % 40 }).ToArray()) };
                            var centre = SceneCompositor.MonopolyTokenCenter(single, single.Players[0].Id);
                            var previous = drawing.Transform;
                            Draw(drawing, centre, 9, piece, aspect, true);
                            Require(drawing.Transform == previous, "A piece left its aspect/heading transform on the next parcel.");
                            nativeDrawChecks++;

                            void CheckHeadingAndProportions(Vector2 position, bool actualFoot)
                            {
                                float angle = (float)heading!.Invoke(null, [position, aspect])!;
                                var inward = Vector2.Normalize(new Vector2((float)((500 - position.X) * aspect), 500 - position.Y));
                                var oldTransform = drawing.Transform;
                                using (var correction = (IDisposable)Activator.CreateInstance(artAspect, drawing, position, aspect)!)
                                {
                                    // Inspect the production correction, then test its
                                    // physical basis independently. Equal perpendicular
                                    // pixel axes preserve every source image's aspect.
                                    var transform = Matrix3x2.CreateRotation(angle, position) * drawing.Transform;
                                    var x = Vector2.TransformNormal(Vector2.UnitX, transform);
                                    var y = Vector2.TransformNormal(Vector2.UnitY, transform);
                                    float expectedScale = Math.Min(view.Width, view.Height) / 1000f;
                                    Require(float.IsFinite(angle) && Vector2.Distance(Vector2.Normalize(-y), inward) < .00002f,
                                        $"Piece {piece} on parcel {parcel} misses the physical centre at aspect {aspect}.");
                                    Require(Math.Abs(x.Length() - expectedScale) < .00002f &&
                                        Math.Abs(y.Length() - expectedScale) < .00002f &&
                                        Math.Abs(Vector2.Dot(Vector2.Normalize(x), Vector2.Normalize(y))) < .00002f,
                                        "Physical piece axes were stretched or skewed on a rectangular board.");
                                    double imageRatio = bounds[piece].Width / bounds[piece].Height;
                                    Require(Math.Abs(bounds[piece].Width * x.Length() /
                                        (bounds[piece].Height * y.Length()) - imageRatio) < imageRatio * .00002,
                                        "The physical transform changed the trimmed silver-piece proportions.");
                                    physicalProportionChecks++;
                                    if (actualFoot) footHeadingChecks++; else orientationChecks++;
                                    if (piece == 0) orientationSamples.Add(new { view = view.Name, parcel, actualFoot,
                                        angle, centre = position, inward, forward = Vector2.Normalize(-y) });
                                }
                                Require(drawing.Transform == oldTransform, "Physical-art correction did not restore the canvas transform.");
                            }
                        }
                    }
                    return target.GetPixelBytes();
                }
            }
            await CheckSetup(view.Name, view.Width, view.Height);
        }
        scene.Dispose();
        Require(Raw(scene, "_crownDeedPieceDevice") is null &&
            ((CanvasBitmap?[]?)Raw(scene, "_crownDeedPieceBitmaps"))?.All(bitmap => bitmap is null) != false,
            "Scene disposal retained silver-piece GPU resources.");
        string reportPath = Path.Combine(directory, "pieces.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { assets, orientationSamples },
            new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, eightDesigns = true, fourAspects = true, fortyParcels = true, assetChecks,
            sourceBoundsChecks, orientationChecks, nativeDrawChecks, repeatChecks, stateChecks, cacheChecks, captionChecks,
            footHeadingChecks, physicalProportionChecks,
            liveServicesConstructed = false, directory, reportPath, images };

        void Draw(CanvasDrawingSession drawing, Vector2 centre, float radius, int piece, double aspect, bool faceCenter) =>
            drawPiece!.Invoke(scene, [drawing, centre, radius, piece, piece % 6, false, aspect, faceCenter]);

        async Task CheckSetup(string name, int width, int height)
        {
            var setupGame = new MonopolyGame(seed: 422);
            foreach (string action in new[] { "mp-start-game", "mp-human-plus", "mp-human-plus", "mp-ai-plus", "mp-ai-plus" })
                Require(setupGame.HandleAction(action, now), "The six-player chooser fixture could not be configured.");
            for (int turn = 0; setupGame.Snapshot.SetupPieces[0] != 6 && turn < 8; turn++)
                Require(setupGame.HandleAction("mp-piece-next-1", now), "The longest piece caption could not be selected.");
            using var setup = new SceneCompositor(monopoly: setupGame, monopolyClock: () => now, boardRevealClock: () => now);
            setup.SetDisplayAspect(width / (double)height);
            setup.SetBoardSetup(true);
            setup.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            setup.SetBoardSetup(false); setup.ShowMonopoly();
            now += TimeSpan.FromMilliseconds(4975); setup.TickMonopoly(now);
            using var target = new CanvasRenderTarget(device, width, height, 96);
            var before = setup.MonopolyState;
            Require(before.Phase == MonopolyPhase.Setup && before.Players.Count == 0 && before.SetupPieces.Count == 6,
                "Showing the chooser reset the prepared six-player Setup phase.");
            using (var drawing = target.CreateDrawingSession()) setup.DrawMonopolyPreview(drawing, width, height);
            Require(ReferenceEquals(before, setup.MonopolyState) && before.SetupPieces.Distinct().Count() == 6,
                "Drawing the chooser mutated its selections.");
            var buttons = setup.CurrentBoardButtons.Where(button => button.Id.StartsWith("mp-piece-next-", StringComparison.Ordinal)).ToArray();
            Require(buttons.Length == 6 && buttons.Any(button => button.Label.Contains("Wheelbarrow", StringComparison.Ordinal)),
                "The chooser lacks its six actual captions or longest design name.");
            foreach (var button in buttons)
            {
                var rectangle = (Rect)type.GetMethod("MonopolyButtonTextRectangle", statics)!.Invoke(null, [button])!;
                using var format = (CanvasTextFormat)type.GetMethod("MonopolyButtonTextFormat", statics)!.Invoke(null, [button])!;
                using var text = new CanvasTextLayout(device, button.Label, format, (float)rectangle.Width, (float)rectangle.Height);
                var ink = text.DrawBounds;
                Require(ink.Width <= rectangle.Width + 1 && ink.Height <= rectangle.Height + 1,
                    "A piece chooser caption does not fit its actual rendered text rectangle: " + button.Label);
                captionChecks++;
            }
            await Capture(target, name + "-six-player-chooser");
        }
        async Task Capture(CanvasRenderTarget target, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png); images.Add(new { name, path });
        }
        object? Raw(object value, string field) => type.GetField(field, instance)!.GetValue(value);
        T Field<T>(object value, string field) => (T)(Raw(value, field) ?? throw new InvalidOperationException("Missing piece resource:" + field));
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
