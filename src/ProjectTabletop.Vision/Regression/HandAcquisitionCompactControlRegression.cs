using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionCompactControlRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 22, 0, 0, TimeSpan.Zero);
    public static void Run()
    {
        const int width = 1920, height = 1080;
        HandTrackingBounds control = new(.022, .917, .156, .061);
        HandTrackingBounds trigger = new(.0808125, .9315, .038375, .032);
        using var board = new Mat(1000, 1000, MatType.CV_8UC4, new Scalar(12, 9, 8, 255));
        for (int y = 905; y < 990; y++)
        for (int x = 10; x < 190; x++)
        {
            double nearestX = Math.Clamp(x, 37.5, 162.5), nearestY = Math.Clamp(y, 932.5, 962.5);
            if (Math.Pow(x - nearestX, 2) + Math.Pow(y - nearestY, 2) > 27.5 * 27.5) continue;
            double t = (y - 905) / 85.0;
            board.Set(y, x, new Vec4b((byte)(244 - t * 30), (byte)(239 - t * 40), (byte)(231 - t * 55), 255));
        }
        // 54 × 24 logical profile corrected to the renderer's 16:9 output aspect.
        Point[] chevron = [new(85, 955), new(100, 935), new(115, 955),
            new(113, 959), new(100, 943), new(87, 959)];
        Cv2.FillPoly(board, [chevron], new Scalar(112, 83, 22, 255), LineTypes.AntiAlias);
        byte[] expected = Bytes(board);
        using var camera = new Mat(height, width, MatType.CV_8UC4, new Scalar(18, 18, 18, 255));
        using var resized = new Mat();
        Cv2.Resize(board, resized, new Size(1050, 820), interpolation: InterpolationFlags.Linear);
        using (var target = new Mat(camera, new Rect(405, 100, 1050, 820))) resized.CopyTo(target);
        Cv2.GaussianBlur(camera, camera, new Size(5, 5), .75);
        byte[] empty = Bytes(camera);
        for (int index = 0; index < empty.Length; index += 4)
        {
            empty[index] = (byte)Math.Clamp(empty[index] * .89 + 5, 0, 255);
            empty[index + 1] = (byte)Math.Clamp(empty[index + 1] * .95 + 4, 0, 255);
            empty[index + 2] = (byte)Math.Clamp(empty[index + 2] * .91 + 8, 0, 255);
        }
        var scene = new HandAcquisitionSceneImage(1000, 1000, expected,
            [1.0 / 1050, 0, -405.0 / 1050, 0, 1.0 / 820, -100.0 / 820, 0, 0, 1], [control], [control], [trigger]);
        HandTrackingBounds independentPanel = new(.4, .8, .2, .08);
        var openScene = scene with { BoardSearchRegions = [control, independentPanel],
            BoardReferenceRegions = [control, independentPanel], BoardTriggerRegions = [trigger, independentPanel] };
        PixelPoint[] polygon = [new(405, 100), new(1455, 100), new(1455, 920), new(405, 920)];
        foreach (var currentScene in new[] { scene, openScene })
        foreach (bool warm in new[] { false, true })
        foreach (bool preserveInk in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) for (int frame = 0; frame < 3; frame++) Require(Feed(empty, frame * 100).Hints.Count == 0, "Empty compact control acquired.");
            byte[] hand = Patch(.0532, .91265, .0936, .0697, preserveInk);
            var first = Feed(hand, 400); var second = Feed(hand, 500);
            Require(first.Hints.Count == 0 && second.Hints.Count == 1 &&
                second.Hints[0].ControlCoverage >= .07 && second.Hints[0].ControlTriggerCoverage >= .07,
                $"A central grouped-finger obstruction trained away its own compact control (warm={warm}, ink={preserveInk}): " + JsonSerializer.Serialize(second));
            Require(Feed(hand, 500).Hints.Count == 0 && Feed(hand, 600).Hints.Count == 1 && Feed(empty, 700).Hints.Count == 0,
                "Compact-control confirmation, stationary evidence or removal failed.");
            HandAcquisitionPresenceResult Feed(byte[] pixels, int milliseconds)
            {
                var now = Epoch.AddMilliseconds(milliseconds);
                return tracker.Update(width, height, width * 4, pixels, polygon, currentScene, now, now);
            }
        }
        foreach (var currentScene in new[] { scene, openScene })
        foreach (var negative in new[] { empty, Patch(.022, .917, .156, .061, true),
            Patch(.022, .922, .030, .050, false), Patch(.098, .9315, .004, .032, false) })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            for (int frame = 0; frame < 4; frame++)
            {
                var now = Epoch.AddMilliseconds(frame * 100);
                var result = tracker.Update(width, height, width * 4, negative, polygon, currentScene, now, now);
                Require(result.Hints.Count == 0, "Empty, whole-panel tint, intact offside glyph or sub-7% patch acquired: " + JsonSerializer.Serialize(result));
            }
        }
        Console.WriteLine("Compact-control acquisition passed: 1080p camera optics, lone and independently referenced chevrons, fresh opposite margins, cold/warm grouped fingers, readable projected ink, two frames, both 7% floors, stationary hold, removal, whole-panel tint and offside rejection.");

        byte[] Patch(double x, double y, double w, double h, bool ink)
        {
            byte[] pixels = (byte[])empty.Clone();
            for (int cy = (int)(100 + y * 820); cy < 100 + (y + h) * 820; cy++)
            for (int cx = (int)(405 + x * 1050); cx < 405 + (x + w) * 1050; cx++)
            {
                int offset = (cy * width + cx) * 4;
                if (ink)
                {
                    pixels[offset] = (byte)(pixels[offset] * .52 + 8);
                    pixels[offset + 1] = (byte)(pixels[offset + 1] * .66 + 10);
                    pixels[offset + 2] = (byte)(pixels[offset + 2] * .87 + 24);
                }
                else
                {
                    int texture = (cx * 7 + cy * 11) % 9 - 4;
                    pixels[offset] = (byte)(85 + texture); pixels[offset + 1] = (byte)(125 + texture); pixels[offset + 2] = (byte)(185 + texture);
                }
            }
            return pixels;
        }
    }

    private static byte[] Bytes(Mat image)
    {
        byte[] pixels = new byte[image.Rows * image.Cols * 4]; Marshal.Copy(image.Data, pixels, 0, pixels.Length); return pixels;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    // Explicit private replay: camera photographs remain outside the repository.
    public static void ReplayNative(string path)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(path));
        var root = metadata.RootElement;
        T Read<T>(string name) => JsonSerializer.Deserialize<T>(root.GetProperty(name).GetRawText())!;
        string directory = Path.GetDirectoryName(path)!;
        int width = Read<int>("cameraWidth"), height = Read<int>("cameraHeight"), stride = Read<int>("cameraStride");
        var scene = new HandAcquisitionSceneImage(Read<int>("expectedWidth"), Read<int>("expectedHeight"),
            File.ReadAllBytes(Path.Combine(directory, Read<string>("expectedBgra"))), Read<double[]>("expectedCameraToBoard"),
            Read<HandTrackingBounds[]>("boardSearchRegions"), Read<HandTrackingBounds[]>("boardReferenceRegions"),
            Read<HandTrackingBounds[]>("boardTriggerRegions"), Read<bool>("AllowsLocalForegroundContext"));
        byte[] empty = Photo(Read<string>("cleanImage")), occupied = Photo(Read<string>("occupiedImage"));
        var polygon = Read<PixelPoint[]>("searchPolygon");
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) for (int frame = 0; frame < 3; frame++) Feed(empty, frame * 100, "empty");
            for (int frame = 0; frame < 4; frame++) Feed(occupied, 500 + frame * 100, "hand");
            Feed(empty, 1000, "removed");
            void Feed(byte[] pixels, int milliseconds, string state)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                var result = tracker.Update(width, height, stride, pixels, polygon, scene, time, time);
                Console.WriteLine(JsonSerializer.Serialize(new { warm, state, milliseconds, result, tracker.SampledCellCount }));
                Require(state == "hand" ? result.Hints.Count == (milliseconds == 500 ? 0 : 1) : result.Hints.Count == 0,
                    "The exact generated native-camera control fixture did not acquire or reject correctly.");
            }
        }
        byte[] Photo(string name)
        {
            using var image = Cv2.ImRead(Path.Combine(directory, name), ImreadModes.Color);
            using var bgra = new Mat(); Cv2.CvtColor(image, bgra, ColorConversionCodes.BGR2BGRA);
            Require(bgra.Width == width && bgra.Height == height, "Native fixture camera geometry changed.");
            return Bytes(bgra);
        }
    }

    public static void Replay(string directory, string occupiedPath)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "snapshot.json")));
        var root = metadata.RootElement;
        T Read<T>(string name) => JsonSerializer.Deserialize<T>(root.GetProperty(name).GetRawText())!;
        int width = Read<int>("cameraWidth"), height = Read<int>("cameraHeight"), stride = Read<int>("Stride");
        var scene = new HandAcquisitionSceneImage(Read<int>("Width"), Read<int>("Height"),
            File.ReadAllBytes(Path.Combine(directory, "expected.bgra")), Read<double[]>("CameraToBoard"),
            Read<HandTrackingBounds[]>("BoardSearchRegions"), Read<HandTrackingBounds[]>("BoardReferenceRegions"),
            Read<HandTrackingBounds[]>("BoardTriggerRegions"), Read<bool>("AllowsLocalForegroundContext"));
        byte[] empty = File.ReadAllBytes(Path.Combine(directory, "camera.bgra"));
        using var photo = Cv2.ImRead(occupiedPath, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(photo, bgra, ColorConversionCodes.BGR2BGRA);
        byte[] occupied = new byte[bgra.Rows * bgra.Cols * 4];
        Marshal.Copy(bgra.Data, occupied, 0, occupied.Length);
        if (bgra.Width != width || bgra.Height != height) throw new InvalidOperationException("Replay camera dimensions differ.");
        var polygon = Read<PixelPoint[]>("SearchPolygon");
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) for (int frame = 0; frame < 4; frame++) Feed(empty, frame * 100, "empty");
            for (int frame = 0; frame < 4; frame++) Feed(occupied, 500 + frame * 100, "hand");
            Feed(empty, 1000, "removed");

            void Feed(byte[] pixels, int milliseconds, string state)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var result = tracker.Update(width, height, stride, pixels, polygon, scene, time, time);
                elapsed.Stop();
                Require(state == "hand" ? result.Hints.Count == (milliseconds == 500 ? 0 : 1) : result.Hints.Count == 0,
                    "Private compact-control replay lost two-frame acquisition or empty/removal rejection.");
                Console.WriteLine(JsonSerializer.Serialize(new { warm, state, milliseconds, result,
                    tracker.SampledCellCount, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds }));
            }
        }
    }
}
