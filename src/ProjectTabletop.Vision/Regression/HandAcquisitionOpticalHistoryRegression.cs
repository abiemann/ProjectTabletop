using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionOpticalHistoryRegression
{
    private const int CameraWidth = 1920, CameraHeight = 1080;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 15, 42, 13, TimeSpan.Zero);
    private static readonly double[] Matrix = [-.0009309454084379321, -2.0175641518932014E-05,
        1.36840998520647, 2.406994160169857E-06, -.0012109300315176644, 1.113236069742482,
        3.0397424476277033E-06, -3.8680600292314245E-05, 1.0007371623175085];
    private static readonly PixelPoint[] Polygon = [new(1444.8074040154906, 918.1914889825101),
        new(416.9864799159064, 916.1610412784847), new(400.9848820481761, 100.0091106960525),
        new(1462.3809582109711, 99.45005171523106)];
    private static readonly HandTrackingBounds[] Controls = [new(.072, .892, .11600000000000002, .051),
        new(.812, .892, .11600000000000002, .051)];
    private static readonly HandTrackingBounds[] Labels = [new(.10691259765625, .9021845703125,
        .0477998046875, .027373046874999973), new(.84013330078125, .902755859375,
        .06024121093750001, .02681445312499997)];

    public static void Run()
    {
        byte[] expected = Pixels("paint-idle-controls-expected.png", 1000, 1000);
        byte[] camera = Pixels("paint-idle-controls-camera.png", CameraWidth, CameraHeight);
        var scene = new HandAcquisitionSceneImage(1000, 1000, expected, Matrix, Controls, Controls, Labels);
        // The captured source contains only the two generated controls and their
        // narrow registration margin. Every unrelated camera/paint pixel is black.
        // This preserves actual projector optics and native sampling, without
        // storing a person's hand, artwork, tabletop, room, or objects.
        var cold = new HandAcquisitionPresenceTracker();
        for (int frame = 0; frame < 5; frame++)
            Clean(Feed(cold, camera, frame * 100), "Cold native idle controls");

        // A sharper exposure of the same generated caption can match almost
        // perfectly. Later normal camera optics must retain the same absolute
        // acceptance floor, rather than inheriting 86% of that historical peak.
        // The generated camera frame uses the captured calibration and actual
        // Exit registration; no temporal geometry recovery is involved here.
        byte[] sharpCamera = ProjectGeneratedCamera(expected, camera, 1, -.002, .001);
        // A lower normal exposure preserves the recorded glyph shape but avoids
        // clipping, so a clipped-stroke recovery cannot mask the acceptance bug.
        byte[] normalCamera = (byte[])camera.Clone();
        for (int pixel = 0; pixel < normalCamera.Length; pixel++)
            if (pixel % 4 != 3) normalCamera[pixel] = (byte)Math.Round(normalCamera[pixel] * .82);
        var sharpnessHistory = new HandAcquisitionPresenceTracker();
        var sharp = Feed(sharpnessHistory, sharpCamera, 0);
        var optical = Feed(sharpnessHistory, normalCamera, 100);
        var sharpExit = sharp.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
        var opticalExit = optical.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
        Require(sharpExit.Correlation >= .98 && opticalExit.Correlation is >= .82 and < .85 &&
            sharpExit.RegistrationX == opticalExit.RegistrationX && sharpExit.RegistrationY == opticalExit.RegistrationY,
            "Historical sharpness fixture did not preserve its intended score and same-geometry transition",
            new { sharpExit, opticalExit });
        Clean(sharp, "Earlier sharp generated controls");
        Clean(optical, "Intact normal optics after sharper history");
        Clean(Feed(sharpnessHistory, normalCamera, 200), "Continued intact normal optics after sharper history");
        Console.WriteLine($"Native sharpness-history regression passed: sharper Exit {sharpExit.Correlation:F5} " +
            $"to intact camera Exit {opticalExit.Correlation:F5} at unchanged registration " +
            $"({opticalExit.RegistrationX},{opticalExit.RegistrationY}); untouched Save remains readable.");

        // The projection's camera fit can move by several logical pixels while
        // the same text remains visible. A verified older registration must not
        // permanently constrain recovery to its tiny local neighborhood.
        // A recovered generated shape cannot teach away real stationary fingers.
        byte[] occupied = (byte[])camera.Clone();
        for (int y = 0; y < CameraHeight; y++)
        for (int x = 0; x < CameraWidth; x++)
        {
            PixelPoint uv = Transform(Matrix, x, y);
            if (uv.Y < .892 || uv.Y > .941) continue;
            for (int finger = 0; finger < 4; finger++)
            {
                double left = .099 + finger * .016;
                if (uv.X < left || uv.X > left + .014) continue;
                int pixel = (y * CameraWidth + x) * 4;
                int texture = (x * 13 + y * 7) % 13;
                occupied[pixel] = (byte)(80 + texture);
                occupied[pixel + 1] = (byte)(105 + texture);
                occupied[pixel + 2] = (byte)(165 + texture);
            }
        }
        foreach (int shiftedControl in new[] { 0, -1 })
        {
            var history = new HandAcquisitionPresenceTracker();
            byte[] shifted = MoveCameraInBoardSpace(camera, 0, -.008, shiftedControl);
            for (int frame = 0; frame < 3; frame++)
                Clean(Feed(history, shifted, frame * 100), "Earlier intact shifted controls");
            for (int frame = 3; frame < 8; frame++)
                Clean(Feed(history, camera, frame * 100), "Intact text after registration moved");
            var pending = Feed(history, occupied, 800);
            Require(pending.Hints.Count == 0 && pending.TextPatterns!.Single(pattern =>
                pattern.ControlRegion == 0).ConfirmationFrames == 1,
                "A recovered native control bypassed two fresh corruption frames", pending);
            var confirmed = Feed(history, occupied, 900);
            Require(confirmed.Hints.Count == 1 && confirmed.Hints[0].ControlCoverage >= .07 &&
                confirmed.Hints[0].ControlTriggerCoverage >= .07 &&
                confirmed.TextPatterns!.Single(pattern => pattern.ControlRegion == 1).LabelIntact,
                "Optical recovery absorbed stationary fingers or acquired untouched Save", confirmed);
            Require(Feed(history, occupied, 1000).Hints.Count == 1,
                "Stationary fingers disappeared after optical recovery");
            Clean(Feed(history, camera, 1100), "Native hand removal");
        }
        Console.WriteLine("Native control optical-history regression passed: camera-only generated control patches, " +
            "cold intact text, moved verified optics, independent untouched Save, stationary four fingers, " +
            "two fresh corruption frames, both 7% area floors, and removal.");

        CheckReadableCaptionReflectance(ProjectGeneratedCamera(expected, camera, 1, -.002, .001,
            fullBoard: true), scene);

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            return tracker.Update(CameraWidth, CameraHeight, CameraWidth * 4, pixels, Polygon, scene, now, now);
        }
    }

    private static void CheckReadableCaptionReflectance(byte[] camera, HandAcquisitionSceneImage scene)
    {
        scene = scene with { BoardReferenceRegions = [new(.2, 0, .8, 1)] };
        // Synthetic skin response preserves the projected letters. The fixture
        // contains generated controls only; no private hand pixels are stored.
        byte[] skin = Tint(camera, uv => Contains(Controls[0], uv));
        var tracker = new HandAcquisitionPresenceTracker();
        var first = Feed(tracker, skin, 0);
        var firstExit = first.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
        Require(firstExit.LabelIntact && !firstExit.ShapeCorrupted && firstExit.CaptionReflectanceChanged &&
            firstExit.CaptionReflectanceCoverage >= .07 && firstExit.CaptionReflectanceTriggerCoverage >= .07 &&
            first.Hints.Count == 0 && firstExit.ConfirmationFrames == 1,
            "Readable letters projected onto synthetic skin did not retain independently measured caption evidence", first);
        var confirmed = Feed(tracker, skin, 100);
        Require(confirmed.Hints.Count == 1 && confirmed.Hints[0].ControlCoverage >= .07 &&
            confirmed.Hints[0].ControlTriggerCoverage >= .07,
            "Readable caption evidence did not survive component grouping and both area floors", confirmed);
        for (int frame = 2; frame < 12; frame++)
            Require(Feed(tracker, skin, frame * 100).Hints.Count == 1,
                "Stationary readable-on-skin evidence was learned as empty background");
        Clean(Feed(tracker, camera, 1200), "Readable skin removal");

        byte[] global = Tint(camera, _ => true);
        byte[] panel = Tint(camera, uv => Contains(Controls[0], uv) &&
            (uv.X < Labels[0].X - .004 || uv.X > Labels[0].X + Labels[0].Width + .004 ||
             uv.Y < Labels[0].Y - .004 || uv.Y > Labels[0].Y + Labels[0].Height + .004));
        byte[] tiny = Tint(camera, uv => Contains(Controls[0], uv) && uv.X < Controls[0].X + .015);
        foreach (var (negative, description) in new[] { (global, "Global color/exposure drift"),
                     (panel, "Local panel-only tint with untouched letters"), (tiny, "Sub-floor local reflectance") })
        {
            var quiet = new HandAcquisitionPresenceTracker();
            for (int frame = 0; frame < 4; frame++)
                Clean(Feed(quiet, negative, frame * 100), description);
        }
        var oneLabelScene = scene with { BoardSearchRegions = [Controls[0]], BoardTriggerRegions = [Labels[0]] };
        var noIndependentLabel = new HandAcquisitionPresenceTracker();
        for (int frame = 0; frame < 3; frame++)
        {
            var time = Epoch.AddMilliseconds(frame * 100);
            var result = noIndependentLabel.Update(CameraWidth, CameraHeight, CameraWidth * 4,
                skin, Polygon, oneLabelScene, time, time);
            Require(result.Hints.Count == 0 && result.TextPatterns!.Single() is
                { LabelIntact: true, CaptionReflectanceChanged: false, CaptionReflectanceCoverage: >= .07 },
                "Caption reflectance without another verified clean label was accepted", result);
        }

        var duplicate = new HandAcquisitionPresenceTracker();
        Require(Feed(duplicate, skin, 0).Hints.Count == 0 && Feed(duplicate, skin, 0).Hints.Count == 0,
            "Duplicate camera timestamps confirmed caption reflectance");
        Require(Feed(duplicate, skin, 1000).Hints.Count == 0,
            "A long camera gap reused earlier caption reflectance confirmation");
        Require(Feed(duplicate, skin, 1100).Hints.Count == 1,
            "Two fresh caption frames after a gap did not reconfirm");
        var stale = new HandAcquisitionPresenceTracker();
        Require(Feed(stale, skin, 0, 1000).Hints.Count == 0 && Feed(stale, skin, 100, 1100).Hints.Count == 0,
            "Stale readable caption frames confirmed acquisition");
        var reverse = new HandAcquisitionPresenceTracker();
        Require(Feed(reverse, skin, 200).Hints.Count == 0 && Feed(reverse, skin, 100).Hints.Count == 0,
            "A backwards timestamp reused reflectance confirmation");
        Require(Feed(reverse, skin, 300).Hints.Count == 0 && Feed(reverse, skin, 400).Hints.Count == 1,
            "Fresh frames after backwards time failed to reconfirm");
        Console.WriteLine("Readable caption-reflectance regression passed: projected letters retain their shape, " +
            "independent clean reference, both 7% floors, stationary/removal, panel-only/global/tiny negatives, " +
            "and two fresh frames with duplicate/stale/gap/backwards barriers.");

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker target, byte[] pixels,
            int milliseconds, int? nowMilliseconds = null)
        {
            var time = Epoch.AddMilliseconds(milliseconds);
            return target.Update(CameraWidth, CameraHeight, CameraWidth * 4, pixels, Polygon, scene,
                time, Epoch.AddMilliseconds(nowMilliseconds ?? milliseconds));
        }
    }

    private static bool Contains(HandTrackingBounds bounds, PixelPoint point) => point.X >= bounds.X &&
        point.X <= bounds.X + bounds.Width && point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.Height;

    private static byte[] Tint(byte[] source, Func<PixelPoint, bool> selected)
    {
        byte[] result = (byte[])source.Clone();
        for (int y = 0; y < CameraHeight; y++)
        for (int x = 0; x < CameraWidth; x++)
        {
            if (!selected(Transform(Matrix, x, y))) continue;
            int pixel = (y * CameraWidth + x) * 4;
            result[pixel] = (byte)Math.Clamp(source[pixel] - 50, 0, 255);
            result[pixel + 1] = (byte)Math.Clamp(source[pixel + 1] - 10, 0, 255);
            result[pixel + 2] = (byte)Math.Clamp(source[pixel + 2] + 40, 0, 255);
        }
        return result;
    }

    private static void Clean(HandAcquisitionPresenceResult result, string description) =>
        Require(result.Hints.Count == 0 && result.TextPatterns is { Count: 2 } &&
            result.TextPatterns.All(pattern => !pattern.ShapeCorrupted),
            description + " falsely acquired illumination from intact generated text", result);

    private static byte[] ProjectGeneratedCamera(byte[] generated, byte[] original, double blur, double offsetU,
        double offsetV, bool fullBoard = false)
    {
        using var source = new Mat(1000, 1000, MatType.CV_8UC4);
        Marshal.Copy(generated, 0, source.Data, generated.Length);
        using var optics = new Mat();
        Cv2.GaussianBlur(source, optics, new Size(0, 0), blur);
        byte[] rendered = new byte[generated.Length];
        Marshal.Copy(optics.Data, rendered, 0, rendered.Length);
        byte[] camera = (byte[])original.Clone();
        for (int y = 0; y < CameraHeight; y++)
        for (int x = 0; x < CameraWidth; x++)
        {
            PixelPoint uv = Transform(Matrix, x, y);
            if (!(fullBoard ? new[] { new HandTrackingBounds(0, 0, 1, 1) } : Controls.Take(1)).Any(control =>
                uv.X >= control.X - .012 && uv.X <= control.X + control.Width + .012 &&
                uv.Y >= control.Y - .012 && uv.Y <= control.Y + control.Height + .012)) continue;
            double sx = (uv.X - offsetU) * 1000, sy = (uv.Y - offsetV) * 1000;
            int left = (int)Math.Floor(sx), top = (int)Math.Floor(sy);
            int pixel = (y * CameraWidth + x) * 4;
            camera[pixel + 3] = 255;
            if (left < 0 || top < 0 || left >= 999 || top >= 999) continue;
            double fx = sx - left, fy = sy - top;
            for (int channel = 0; channel < 3; channel++)
                camera[pixel + channel] = (byte)Math.Clamp((int)Math.Round(
                    rendered[(top * 1000 + left) * 4 + channel] * (1 - fx) * (1 - fy) +
                    rendered[(top * 1000 + left + 1) * 4 + channel] * fx * (1 - fy) +
                    rendered[((top + 1) * 1000 + left) * 4 + channel] * (1 - fx) * fy +
                    rendered[((top + 1) * 1000 + left + 1) * 4 + channel] * fx * fy), 0, 255);
        }
        return camera;
    }

    private static byte[] MoveCameraInBoardSpace(byte[] source, double offsetU, double offsetV, int controlRegion)
    {
        double[] inverse = Inverse(Matrix);
        byte[] moved = (byte[])source.Clone();
        for (int y = 0; y < CameraHeight; y++)
        for (int x = 0; x < CameraWidth; x++)
        {
            PixelPoint uv = Transform(Matrix, x, y);
            if (controlRegion >= 0)
            {
                var region = Controls[controlRegion];
                if (uv.X < region.X - .012 || uv.X > region.X + region.Width + .012 ||
                    uv.Y < region.Y - .012 || uv.Y > region.Y + region.Height + .012) continue;
            }
            PixelPoint original = Transform(inverse, uv.X - offsetU, uv.Y - offsetV);
            int pixel = (y * CameraWidth + x) * 4;
            moved[pixel + 3] = 255;
            int left = (int)Math.Floor(original.X), top = (int)Math.Floor(original.Y);
            if (left < 0 || top < 0 || left >= CameraWidth - 1 || top >= CameraHeight - 1) continue;
            double fx = original.X - left, fy = original.Y - top;
            for (int channel = 0; channel < 3; channel++)
                moved[pixel + channel] = (byte)Math.Clamp((int)Math.Round(
                    source[(top * CameraWidth + left) * 4 + channel] * (1 - fx) * (1 - fy) +
                    source[(top * CameraWidth + left + 1) * 4 + channel] * fx * (1 - fy) +
                    source[((top + 1) * CameraWidth + left) * 4 + channel] * (1 - fx) * fy +
                    source[((top + 1) * CameraWidth + left + 1) * 4 + channel] * fx * fy), 0, 255);
        }
        return moved;
    }

    private static PixelPoint Transform(IReadOnlyList<double> m, double x, double y)
    {
        double divisor = m[6] * x + m[7] * y + m[8];
        return new((m[0] * x + m[1] * y + m[2]) / divisor,
            (m[3] * x + m[4] * y + m[5]) / divisor);
    }
    private static double[] Inverse(IReadOnlyList<double> m)
    {
        double[] c = [m[4]*m[8]-m[5]*m[7], m[2]*m[7]-m[1]*m[8], m[1]*m[5]-m[2]*m[4],
            m[5]*m[6]-m[3]*m[8], m[0]*m[8]-m[2]*m[6], m[2]*m[3]-m[0]*m[5],
            m[3]*m[7]-m[4]*m[6], m[1]*m[6]-m[0]*m[7], m[0]*m[4]-m[1]*m[3]];
        double determinant = m[0]*c[0]+m[1]*c[3]+m[2]*c[6];
        return c.Select(value => value / determinant).ToArray();
    }
    private static byte[] Pixels(string name, int width, int height)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), ImreadModes.Unchanged);
        Require(image.Width == width && image.Height == height && image.Type() == MatType.CV_8UC4,
            "Native control-only fixture lost its dimensions or BGRA pixels");
        byte[] pixels = new byte[width * height * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return pixels;
    }
    private static void Require(bool condition, string message, object? result = null)
    {
        if (!condition) throw new InvalidOperationException(message +
            (result is null ? "." : ": " + JsonSerializer.Serialize(result)));
    }
}
