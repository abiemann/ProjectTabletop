#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Real off-screen rendering and detector input; no app or capture devices.
    private async Task<object> VerifyDuckHuntSkullEyesAsync()
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const int fps = 60, frames = 120;
        int assertions = 0, opticalChecks = 0;
        string directory = Path.Combine(_appDataDirectory, "GallerySkullEyes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        var cameraTransformBaselines = new List<object>();
        foreach (var size in new[] { (Name: "1080p", Width: 1920, Height: 1080), (Name: "4k", Width: 3840, Height: 2160) })
        {
            double aspect = size.Width / (double)size.Height;
            var now = MonotonicClock.UtcNow;
            using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size.Width, size.Height, 96);
            var tracker = (DuckHuntMeasurementTracker)typeof(SceneCompositor).GetField("_duckHuntMeasurement", privateInstance)!.GetValue(scene)!;
            var detector = new RedDotDetector();
            int? currentAim = null;
            scene.SetDisplayAspect(aspect);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(size.Width, 0), new(size.Width, size.Height), new(0, size.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.ShowDuckHuntTargetPractice();
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.SetDuckHuntAudioStatus(true, "Isolated skull-eye fixture");
            Draw();
            Aim(2); Draw(); now += TimeSpan.FromMilliseconds(120); Aim(2); Draw();
            Check(EyesOff(), "An unrevealed skull glowed behind the closed backdrop.");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(new(.5, .46), now), "The eye fixture could not start the gallery.");
            now += TimeSpan.FromSeconds(4);
            Draw();
            Check(scene.GalleryState.UpperPhase == DuckHuntGalleryUpperPhase.WaitingForHits, "The skulls are not exposed.");
            var beforeHover = scene.GalleryState;
            var socketChanges = new List<object>();
            foreach (int skull in size.Name == "1080p" ? new[] { 0, 1, 2, 3, 4 } : new[] { 2 })
            {
                Aim(skull); Draw();
                now += TimeSpan.FromMilliseconds(120);
                Aim(skull); byte[] glowing = Capture();
                Check(scene.GallerySkullEyeAmount(skull) >= .99f &&
                    Enumerable.Range(0, 5).Where(index => index != skull).All(index => scene.GallerySkullEyeAmount(index) == 0),
                    "Aiming did not light exactly the selected intact skull.");
                if (skull == 2) await Save("glowing");
                await CheckOptics(glowing, $"{size.Name} skull {skull}", variants: skull == 2);
                foreach (var local in new[] { new BoardAim(.5, .28), new BoardAim(444.0 / 1254, 605.0 / 1254), new BoardAim(810.0 / 1254, 605.0 / 1254) })
                {
                    CheckLaser(glowing, SkullPoint(skull, local), clipped: false);
                    CheckLaser(glowing, SkullPoint(skull, local), clipped: true);
                }
                // Pause at precisely the same animation time to obtain the
                // unlit reference without moving lamps, reflections or targets.
                scene.SetDuckHuntAudioStatus(false, "Isolated eye pause");
                byte[] unlit = Capture();
                Check(scene.GalleryState.Paused && EyesOff(), "Pausing left a skull's eyes lit.");
                socketChanges.Add(CheckLocalized(glowing, unlit, skull));
                scene.SetDuckHuntAudioStatus(true, "Isolated eye resume"); Draw();
            }

            Aim(0); Draw(); now += TimeSpan.FromMilliseconds(120); Aim(0); Draw();
            Aim(4); Draw(); now += TimeSpan.FromMilliseconds(50); Aim(4);
            await CheckOptics(Capture(), "changing skull during the fade", variants: true);
            now += TimeSpan.FromMilliseconds(100); Aim(4); Draw();
            Check(scene.GallerySkullEyeAmount(0) == 0 && scene.GallerySkullEyeAmount(4) >= .99f,
                "The previous skull retained its glow after aiming at another skull.");
            Aim(null); Draw(); now += TimeSpan.FromMilliseconds(50);
            await CheckOptics(Capture(), "null aim during the fade", variants: true);
            now += TimeSpan.FromMilliseconds(100); Draw();
            Check(EyesOff(), "Leaving the skull did not clear its eyes after the fade.");

            Aim(2); Draw(); now += TimeSpan.FromMilliseconds(120); Aim(2); Draw();
            // Replay an accepted observation whose camera timestamp has now
            // expired. This uses the actual tracker rules without sleeping.
            var expiredAt = MonotonicClock.UtcNow - DuckHuntAimGate.Freshness - TimeSpan.FromMilliseconds(20);
            tracker.Reset(expiredAt - TimeSpan.FromMilliseconds(1));
            Check(tracker.Observe(SkullPoint(2, new(.5, .28)), expiredAt, expiredAt), "Could not seed the expiry history.");
            Draw(); now += TimeSpan.FromMilliseconds(120); Draw();
            Check(scene.DuckHuntMeasurement.LiveDetectedXY is null && EyesOff(), "An expired laser kept the eyes glowing.");
            Aim(2); Draw(); now += TimeSpan.FromMilliseconds(120); Aim(2); Draw();
            var oldContext = scene.GetDuckHuntInputContext()!;
            scene.ClearDuckHuntInput(); Draw();
            Check(EyesOff(), "An input reset retained the previous eye glow.");
            scene.PublishDuckHuntDot(SkullPoint(2, new(.5, .28)), MonotonicClock.UtcNow, oldContext.Epoch);
            Draw(); Check(EyesOff(), "An obsolete input epoch reactivated the eyes.");
            Check(scene.GalleryState.Shots == beforeHover.Shots && scene.GalleryState.HitMask == beforeHover.HitMask &&
                scene.GalleryState.SkullHitMask == beforeHover.SkullHitMask && scene.GalleryState.Cycle == beforeHover.Cycle,
                "Hover feedback changed the shot count, hits or round.");

            if (size.Name == "1080p")
            {
                var origin = now;
                for (int frame = 0; frame < frames; frame++)
                {
                    now = origin + TimeSpan.FromSeconds(frame / (double)fps);
                    Aim(frame < 30 ? 0 : frame < 60 ? 4 : frame < 90 ? null : 2);
                    Draw();
                    if (frame % 20 == 0) await CheckOptics(target.GetPixelBytes(), $"video frame {frame}");
                    await target.SaveAsync(Path.Combine(directory, $"frame-{frame:D4}.png"), CanvasBitmapFileFormat.Png);
                }
            }
            Aim(2); Draw(); now += TimeSpan.FromMilliseconds(120); Aim(2); Draw();
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(SkullPoint(2, new(.5, .28)), now), "The highlighted skull did not accept a normal shot.");
            Draw();
            Check(scene.GalleryState.HitSkulls == 1 && EyesOff(), "A shattered skull retained its eye glow.");
            now += TimeSpan.FromMilliseconds(120); Aim(2); Draw();
            Check(EyesOff() && scene.GalleryState.HitSkulls == 1, "Aiming at a destroyed skull lit it or awarded another hit.");
            await Save("shattered");
            results.Add(new { size.Name, size.Width, size.Height, SocketChanges = socketChanges });

            void Aim(int? skull)
            {
                currentAim = skull;
                var context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No eye input context.");
                var stamp = MonotonicClock.UtcNow;
                while (stamp <= context.NotBefore || stamp <= scene.DuckHuntMeasurement.LiveFrameTimestamp)
                    stamp = MonotonicClock.UtcNow;
                scene.PublishDuckHuntDot(skull is { } index ? SkullPoint(index, new(.5, .28)) : null, stamp, context.Epoch);
            }
            bool EyesOff() => Enumerable.Range(0, 5).All(index => scene.GallerySkullEyeAmount(index) == 0);
            BoardAim SkullPoint(int index, BoardAim local)
            {
                var bounds = DuckHuntGalleryLayout.SkullBounds(index, aspect);
                return new(bounds.X + bounds.Width * local.U, bounds.Y + bounds.Height * local.V);
            }
            (double X, double Y) CameraPoint(BoardAim aim)
            {
                var map = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap", privateInstance)!.GetValue(scene)!;
                var p = map.Transform(new(aim.U, aim.V));
                return (p.X * size.Width, p.Y * size.Height);
            }
            object CheckLocalized(byte[] glowing, byte[] unlit, int index)
            {
                var left = Box(325, 485, 575, 698); var right = Box(679, 485, 929, 698);
                int leftYellow = 0, rightYellow = 0, rounded = 0, outside = 0;
                for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                {
                    int p = (y * size.Width + x) * 4;
                    int delta = 0;
                    for (int channel = 0; channel < 4; channel++) delta = Math.Max(delta, Math.Abs(glowing[p + channel] - unlit[p + channel]));
                    bool inLeft = Inside(left, x, y), inRight = Inside(right, x, y);
                    if (!inLeft && !inRight && delta > 0) { if (delta <= 1) rounded++; else outside++; }
                    int redOverBlue = glowing[p + 2] - glowing[p] - unlit[p + 2] + unlit[p];
                    int greenOverBlue = glowing[p + 1] - glowing[p] - unlit[p + 1] + unlit[p];
                    if (redOverBlue >= 10 && greenOverBlue >= 10) { if (inLeft) leftYellow++; if (inRight) rightYellow++; }
                }
                Check(leftYellow >= 6 && rightYellow >= 6, "Both actual eye sockets did not become visibly yellow.");
                // Same bounded1-level GPU rounding seen in the gallery hold check.
                Check(outside == 0 && rounded <= 64, "Eye animation changed pixels outside the selected skull's sockets.");
                return new { Skull = index, LeftYellowPixels = leftYellow, RightYellowPixels = rightYellow, RoundedOutsidePixels = rounded };
                (double X1, double Y1, double X2, double Y2) Box(double x1, double y1, double x2, double y2)
                {
                    var a = CameraPoint(SkullPoint(index, new(x1 / 1254, y1 / 1254)));
                    var b = CameraPoint(SkullPoint(index, new(x2 / 1254, y2 / 1254)));
                    return (a.X - 2, a.Y - 2, b.X + 2, b.Y + 2);
                }
                static bool Inside((double X1, double Y1, double X2, double Y2) box, int x, int y) =>
                    x >= box.X1 && x <= box.X2 && y >= box.Y1 && y <= box.Y2;
            }
            async Task CheckOptics(byte[] original, string stage, bool variants = false)
            {
                foreach (var gains in variants ? new[] { (.8, .8, .8), (1d, 1d, 1d), (1.2, 1.2, 1.2), (1d, .95, 1.10) } : new[] { (1d, 1d, 1d) })
                {
                    byte[] pixels = ApplyGains(original);
                    var input = scene.GetDuckHuntInputContext()!;
                    var dot = detector.Detect(size.Width, size.Height, size.Width * 4, pixels, input.CameraToBoard);
                    opticalChecks++;
                    bool transformedCamera = gains != (1d, 1d, 1d);
                    byte[]? unlit = null;
                    if (transformedCamera)
                    {
                        // Camera transforms can create candidates in the original
                        // scenery, including clipped crystal highlights at 4K.
                        // Compare identical frames with just the overlay omitted;
                        // a clear baseline must still remain completely clear.
                        // Identity and injected-laser checks stay fully unmasked.
                        var amounts = Enumerable.Range(0, 5).Select(scene.GallerySkullEyeAmount).ToArray();
                        unlit = ApplyGains(CaptureWithoutEyes());
                        var baseline = new RedDotDetector().Detect(size.Width, size.Height, size.Width * 4, unlit, input.CameraToBoard);
                        var skullResults = new List<object>();
                        bool skullsUnchanged = true;
                        for (int index = 0; index < 5; index++)
                        {
                            if (amounts[index] <= 0) continue;
                            var litSkull = new RedDotDetector().Detect(size.Width, size.Height, size.Width * 4, ScopeSkull(pixels, index), input.CameraToBoard);
                            var unlitSkull = new RedDotDetector().Detect(size.Width, size.Height, size.Width * 4, ScopeSkull(unlit, index), input.CameraToBoard);
                            opticalChecks += 2;
                            skullResults.Add(new { Skull = index, Lit = litSkull, Unlit = unlitSkull });
                            // Yellow may remove a pre-existing coloured highlight.
                            // It must not increase candidates, turn ambiguity into
                            // a false single aim, or move an existing single aim.
                            skullsUnchanged &= litSkull.CandidateCount <= unlitSkull.CandidateCount &&
                                (!litSkull.Detected || unlitSkull.Detected &&
                                    litSkull.CameraPosition == unlitSkull.CameraPosition);
                        }
                        var comparison = new { Size = size.Name, Stage = stage, RedGain = gains.Item1, GreenGain = gains.Item2,
                            BlueGain = gains.Item3, Lit = dot, Unlit = baseline, Skulls = skullResults };
                        cameraTransformBaselines.Add(comparison);
                        await File.WriteAllTextAsync(Path.Combine(directory, $"{size.Name}-camera-transform-{opticalChecks:D3}.json"),
                            System.Text.Json.JsonSerializer.Serialize(comparison, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        Check(skullsUnchanged, $"{stage}, RGB gain{gains}: the eye overlay increased candidates or produced a false single aim on a skull or rim.");
                        Check(dot.CandidateCount <= baseline.CandidateCount && (!dot.Detected || baseline.Detected),
                            $"{stage}, RGB gain{gains}: the eye overlay added candidates under the camera colour transform.");
                        continue;
                    }
                    if (dot.Detected || dot.CandidateCount != 0)
                    {
                        string prefix = Path.Combine(directory, $"{size.Name}-optical-failure-{opticalChecks:D3}");
                        var glowAmounts = Enumerable.Range(0, 5).Select(scene.GallerySkullEyeAmount).ToArray();
                        unlit = ApplyGains(CaptureWithoutEyes());
                        var unlitDot = new RedDotDetector().Detect(size.Width, size.Height, size.Width * 4, unlit, input.CameraToBoard);
                        await SavePixels(pixels, prefix + "-glowing.png");
                        await SavePixels(unlit, prefix + "-unlit.png");
                        await File.WriteAllTextAsync(prefix + ".json", System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Stage = stage, RedGain = gains.Item1, GreenGain = gains.Item2, BlueGain = gains.Item3,
                            AnimationTime = now, GlowAmounts = glowAmounts, GlowingDetection = dot, UnlitDetection = unlitDot,
                            GlowingImage = prefix + "-glowing.png", UnlitImage = prefix + "-unlit.png"
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    Check(!dot.Detected && dot.CandidateCount == 0, $"{stage}, RGB gain{gains}: glow created {dot.CandidateCount} aiming candidates.");

                    byte[] ApplyGains(byte[] source)
                    {
                        if (gains == (1d, 1d, 1d)) return source;
                        byte[] transformed = (byte[])source.Clone();
                        for (int p = 0; p < transformed.Length; p += 4)
                        {
                            transformed[p] = (byte)Math.Clamp(Math.Round(source[p] * gains.Item3), 0, 255);
                            transformed[p + 1] = (byte)Math.Clamp(Math.Round(source[p + 1] * gains.Item2), 0, 255);
                            transformed[p + 2] = (byte)Math.Clamp(Math.Round(source[p + 2] * gains.Item1), 0, 255);
                        }
                        return transformed;
                    }
                }
            }
            byte[] CaptureWithoutEyes()
            {
                var overlayField = typeof(SceneCompositor).GetField("_gallerySkullEyes", privateInstance)!;
                var cacheField = typeof(SceneCompositor).GetField("_renderedBoardState", privateInstance)!;
                object? overlay = overlayField.GetValue(scene);
                // Counterfactual rendering only: preserve the live aim, fade,
                // game and lamp clocks, and restore the GPU resource in finally.
                Aim(currentAim);
                try
                {
                    overlayField.SetValue(scene, null);
                    cacheField.SetValue(scene, null);
                    return Capture();
                }
                finally
                {
                    overlayField.SetValue(scene, overlay);
                    cacheField.SetValue(scene, null);
                    Aim(currentAim);
                }
            }
            byte[] ScopeSkull(byte[] pixels, int index)
            {
                var topLeft = CameraPoint(SkullPoint(index, new(-.06, -.06)));
                var bottomRight = CameraPoint(SkullPoint(index, new(1.06, 1.06)));
                byte[] scoped = new byte[pixels.Length];
                for (int p = 3; p < scoped.Length; p += 4) scoped[p] = 255;
                int x1 = Math.Max(0, (int)Math.Floor(topLeft.X)), x2 = Math.Min(size.Width, (int)Math.Ceiling(bottomRight.X));
                for (int y = Math.Max(0, (int)Math.Floor(topLeft.Y)); y < Math.Min(size.Height, (int)Math.Ceiling(bottomRight.Y)); y++)
                    Buffer.BlockCopy(pixels, (y * size.Width + x1) * 4, scoped, (y * size.Width + x1) * 4, (x2 - x1) * 4);
                return scoped;
            }
            async Task SavePixels(byte[] pixels, string path)
            {
                using var bitmap = CanvasBitmap.CreateFromBytes(target.Device, pixels, size.Width, size.Height,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
            }
            void CheckLaser(byte[] original, BoardAim aim, bool clipped)
            {
                byte[] pixels = (byte[])original.Clone();
                var center = CameraPoint(aim);
                for (int y = Math.Max(0, (int)center.Y - 5); y <= Math.Min(size.Height - 1, (int)center.Y + 5); y++)
                for (int x = Math.Max(0, (int)center.X - 5); x <= Math.Min(size.Width - 1, (int)center.X + 5); x++)
                {
                    double radius = (x - center.X) * (x - center.X) + (y - center.Y) * (y - center.Y);
                    if (radius > 20) continue;
                    int p = (y * size.Width + x) * 4;
                    pixels[p] = clipped ? (byte)255 : (byte)30;
                    pixels[p + 1] = clipped ? radius <= 4 ? (byte)235 : (byte)90 : (byte)30;
                    pixels[p + 2] = clipped && radius > 4 ? (byte)240 : (byte)255;
                }
                var dot = detector.Detect(size.Width, size.Height, size.Width * 4, pixels, scene.GetDuckHuntInputContext()!.CameraToBoard);
                opticalChecks++;
                Check(dot.Detected && dot.CandidateCount == 1 && Math.Abs(dot.BoardX!.Value - aim.U) < .003 && Math.Abs(dot.BoardY!.Value - aim.V) < .003,
                    $"A {(clipped ? "magenta/clipped" : "pure-red")} laser on the lit skull was lost, displaced or made ambiguous.");
            }
            void Draw()
            {
                using var drawing = target.CreateDrawingSession();
                scene.Draw(drawing, size.Width, size.Height, preview: false, runningSlowly: false);
            }
            byte[] Capture() { Draw(); return target.GetPixelBytes(); }
            Task Save(string name) => target.SaveAsync(Path.Combine(directory, $"{size.Name}-{name}.png"), CanvasBitmapFileFormat.Png).AsTask();
        }
        return new { Ok = true, Assertions = assertions, OpticalChecks = opticalChecks, Directory = directory, Results = results,
            CameraTransformBaselines = cameraTransformBaselines,
            OpticalScope = "Identity rendering requires zero full-frame candidates; injected lasers use the full unmasked frame. Synthetic neutral gains 0.8/1.2 and mild blue bias compare with the same-time unlit scenery: a zero-candidate baseline must remain clear; otherwise candidate counts may only decrease, ambiguity cannot become a false single aim, and a retained single aim must not move. These checks also apply to each lit skull and its rim. Existing transformed-scene sensitivity, including clipped 4K crystal highlights, is reported rather than claimed resolved; real camera acceptance remains pending.",
            Fps = fps, Frames = frames, TimingScope = "Actual compositor frames at a synthetic clock, not live projector pacing or hardware optical acceptance." };
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
