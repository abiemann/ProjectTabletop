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
    // Own all compositor, game and GPU objects. No MainWindow service or hardware
    // fields participate; the same production caption evidence drives the arrow.
    private async Task<object> VerifyPhotoCopyDrawerAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "PhotoCopyDrawerVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        int sameClockChecks = 0, stableCaptionChecks = 0, stableArrowChecks = 0, capturePreservationChecks = 0, sharedStyleChecks = 0;
        foreach (var (name, width, height) in new[] { ("landscape", 1280, 1000), ("portrait", 1000, 1600) })
        {
            var now = MonotonicClock.UtcNow;
            using var scene = new SceneCompositor(blackjackClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            scene.SetDisplayAspect(width / (double)height);
            scene.SetBoardSetup(true);
            double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.ShowPhotoCopy();
            var paths = new List<string>();
            var closed = await Capture("closed");
            Require(!scene.PhotoCopyDrawerOpen && scene.CurrentBoardButtons.Single() is
                { Id: "photo-drawer-open", Label: "^", Enabled: true, Hold: BoardButtonHold.Once } handle &&
                handle.Bounds == new BoardRect(.01, .87, .18, .12), "Photo Copy initially exposed an action instead of its compact up arrow.");
            var closedHold = scene.GetHoldButtonContext(now);
            Require(closedHold is { ButtonIds.Count: 1 } && closedHold.ExpectedScene.BoardTriggerRegions is { Count: 1 },
                "Photo Copy's closed hold reference included hidden action captions.");
            foreach (string id in new[] { "menu", "photo-swirl", "photo-copy-once", "capture-again", "photo-save" })
                Require(!scene.ActivatePhotoCopyButton(id), "The hidden " + id + " action accepted a pointer.");
            await Task.Delay(1100);
            now = MonotonicClock.UtcNow;
            Draw();
            Require(scene.TryGetPhotoCopyCaptureContext(out var emptyCapture) && emptyCapture.Target is null,
                "The grey capture field did not become ready with its action drawer closed.");
            Require(BoardSession.PhotoCopyShutterBounds == new BoardRect(
                PhotoObjectTarget.CaptureLeft / (double)PhotoHandCutout.BoardPixels,
                PhotoObjectTarget.CaptureTop / (double)PhotoHandCutout.BoardPixels,
                (PhotoObjectTarget.CaptureRight - PhotoObjectTarget.CaptureLeft) / (double)PhotoHandCutout.BoardPixels,
                (PhotoObjectTarget.CaptureBottom - PhotoObjectTarget.CaptureTop) / (double)PhotoHandCutout.BoardPixels),
                "The drawer changed the hidden field shutter's capture bounds.");
            var holds = new PhotoCopyCaptionHoldFixture(scene, Draw, width, time => now = time, height);
            await holds.CheckReleaseCancelsAsync("photo-drawer-open");
            try { await holds.HoldAsync("photo-drawer-open"); }
            catch
            {
                await File.WriteAllTextAsync(Path.Combine(directory, name + "-arrow-evidence-failure.json"),
                    System.Text.Json.JsonSerializer.Serialize(holds.FrameDiagnostics, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                if (holds.LastCameraPixels is { } observed)
                {
                    using var camera = CanvasBitmap.CreateFromBytes(target.Device, observed, width, height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    await camera.SaveAsync(Path.Combine(directory, name + "-arrow-camera-failure.png"), CanvasBitmapFileFormat.Png);
                }
                if (holds.LastContext?.ExpectedScene is { } expected)
                {
                    using var reference = CanvasBitmap.CreateFromBytes(target.Device, expected.Bgra, expected.Width, expected.Height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    await reference.SaveAsync(Path.Combine(directory, name + "-arrow-reference-failure.png"), CanvasBitmapFileFormat.Png);
                }
                throw;
            }
            var openedAt = scene.GetPhotoCopyDrawerDiagnostics(now).OpenedAt ??
                throw new InvalidOperationException("The real up-arrow caption hold did not open the drawer.");
            now = openedAt;
            byte[] start = await Capture("opening-start");
            CheckMoving(0);
            now = openedAt.AddMilliseconds(150);
            byte[] half = await Capture("opening-half");
            CheckMoving(.5);
            Require(SameRegion(start, half, new(.01, .87, .18, .12)), "The down arrow moved or changed while Photo Copy's actions rose.");
            stableArrowChecks++;
            Require(!SameRegion(start, half, new(.20, .87, .485, .12)), "Photo Copy's action row did not visibly rise from below the viewport.");
            Require(SameRegion(start, half, new(.12, .22, .70, .45)), "Opening the drawer altered the independent grey capture field.");
            now = openedAt.AddMilliseconds(299);
            CheckMoving(299d / 300);
            now = openedAt.AddMilliseconds(300);
            scene.TickPhotoCopy(now);
            await Capture("open-ready");
            CheckSettled(["photo-drawer-close", "menu", "photo-swirl", "photo-copy-once"], ["v", "Exit", "Swirl", "Copy"]);
            Require(scene.TryGetPhotoCopyCaptureContext(out var afterOpening) &&
                afterOpening.Revision == emptyCapture.Revision && afterOpening.ReadyAfter == emptyCapture.ReadyAfter &&
                afterOpening.CameraToBoard.SequenceEqual(emptyCapture.CameraToBoard), "Opening/settling the drawer reset the grey field's capture revision or readiness.");
            capturePreservationChecks++;
            CheckSharedStyle();

            // Lock a real segmented object and retain it through pointer close/open.
            byte[] objectPixels = new byte[width * height * 4];
            var a = CameraPoint(.30, .35); var b = CameraPoint(.46, .49);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                bool inside = x >= a.X && x <= b.X && y >= a.Y && y <= b.Y;
                objectPixels[offset] = inside ? (byte)20 : (byte)110;
                objectPixels[offset + 1] = inside ? (byte)35 : (byte)110;
                objectPixels[offset + 2] = inside ? (byte)185 : (byte)110;
                objectPixels[offset + 3] = 255;
            }
            var item = PhotoObjectLocator.Locate(width, height, width * 4, objectPixels, afterOpening.CameraToBoard, out var failure);
            Require(item is not null && scene.SetPhotoCopyObject(item, afterOpening.Revision), "The drawer's object fixture could not lock: " + failure);
            Draw();
            await Task.Delay(450);
            now = MonotonicClock.UtcNow;
            Require(scene.TryGetPhotoCopyCaptureContext(out var litCapture) && ReferenceEquals(litCapture.Target, item),
                "The locked object's illumination did not settle.");
            Require(scene.ActivatePhotoCopyButton("photo-drawer-close"), "The pointer could not close Photo Copy's drawer.");
            await Capture("closed-object");
            Require(scene.TryGetPhotoCopyCaptureContext(out var closedObject) && closedObject.Revision == litCapture.Revision &&
                closedObject.ReadyAfter == litCapture.ReadyAfter && ReferenceEquals(closedObject.Target, item),
                "Closing Photo Copy's drawer lost its object or required new field settling.");
            capturePreservationChecks++;
            Require(!scene.ActivatePhotoCopyButton("menu") && !scene.ActivatePhotoCopyButton("photo-copy-once"),
                "Closing the drawer left a hidden action active.");
            Require(scene.ActivatePhotoCopyButton("photo-drawer-open"), "The pointer could not reopen Photo Copy's drawer.");
            now += BoardSession.PhotoCopyDrawerOpeningDuration;
            scene.TickPhotoCopy(now);
            Draw();
            Require(scene.TryGetPhotoCopyCaptureContext(out var reopenedObject) && reopenedObject.Revision == litCapture.Revision &&
                ReferenceEquals(reopenedObject.Target, item), "Reopening the drawer reset the captured subject.");
            capturePreservationChecks++;
            byte[] photoPixels = new byte[24 * 40 * 4];
            for (int i = 0; i < photoPixels.Length; i += 4)
            { photoPixels[i] = 35; photoPixels[i + 1] = 70; photoPixels[i + 2] = 210; photoPixels[i + 3] = 255; }
            var cutout = new PhotoHandCutout(24, 40, photoPixels, new(12, 20), new(0, -1));
            Require(scene.SetPhotoCopyCapture(cutout, litCapture.Revision, item), "The drawer's retained photograph was rejected.");
            await Capture("open-clear-save");
            CheckSettled(["photo-drawer-close", "menu", "capture-again", "photo-save"], ["v", "Exit", "Clear", "Save"]);
            Require(scene.TryGetPhotoCopyMemoryImage(out var memory), "The drawer discarded its retained photograph.");
            int copies = scene.PhotoCopyCount;
            await holds.HoldAsync("photo-drawer-close");
            await Capture("closed-memory");
            Require(!scene.PhotoCopyDrawerOpen && scene.CurrentBoardButtons.Count == 1 && scene.PhotoCopyCount >= copies &&
                scene.TryGetPhotoCopyMemoryImage(out var afterClose) && afterClose.Revision == memory.Revision &&
                ReferenceEquals(afterClose.Cutout, cutout), "The actual down-arrow hold changed the retained image or its revision.");
            capturePreservationChecks++;
            Require(scene.ActivatePhotoCopyButton("photo-drawer-open"), "The result drawer could not reopen.");
            now += BoardSession.PhotoCopyDrawerOpeningDuration;
            scene.TickPhotoCopy(now);
            await Capture("reopened-memory");
            CheckSettled(["photo-drawer-close", "menu", "capture-again", "photo-save"], ["v", "Exit", "Clear", "Save"]);
            Require(scene.IsPhotoCopyMemoryImageCurrent(memory), "Reopening replaced the memory image identity.");
            capturePreservationChecks++;
            Require(scene.ActivatePhotoCopyButton("capture-again") && scene.PhotoCopyDrawerOpen &&
                scene.CurrentBoardButtons.Single(button => button.Id == "photo-swirl").Label == "Swirl" &&
                !scene.TryGetPhotoCopyMemoryImage(out _) && !scene.TryGetPhotoCopyCaptureContext(out _),
                "Clear did not retain the settled drawer while starting a fresh capture session.");
            await Capture("cleared-open");
            results.Add(new { aspect = name, width, height, paths, holds.SuccessfulHolds, holds.BrokenCaptionFrames });

            void CheckMoving(double expected)
            {
                var state = scene.GetPhotoCopyDrawerDiagnostics(now);
                Require(state is { Open: true, Animating: true, DurationMilliseconds: 300 } && Math.Abs(state.Progress - expected) < .001,
                    "Photo Copy's drawer did not follow its exact300ms injected clock.");
                Require(scene.CurrentBoardButtons.Single(button => button.Id == "photo-drawer-close").Enabled &&
                    scene.CurrentBoardButtons.Where(button => button.Id != "photo-drawer-close").All(button => !button.Enabled) &&
                    !scene.ActivatePhotoCopyButton("menu") && !scene.ActivatePhotoCopyButton("photo-swirl"),
                    "An action enabled before its drawer finished rising.");
                Require(scene.GetHoldButtonContext(now) is null && scene.GetHandAcquisitionContext(now) is
                    { ObserveMotion: false, ExpectedScene: null, IlluminatedHint: null } && scene.ActiveHandSpotlightCount == 0,
                    "A moving Photo Copy drawer supplied hold evidence or acquisition lighting.");
            }
            void CheckSettled(string[] ids, string[] labels)
            {
                Require(scene.GetPhotoCopyDrawerDiagnostics(now) is { Open: true, Animating: false, Progress: 1 } &&
                    scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(ids) &&
                    scene.CurrentBoardButtons.Select(button => button.Label).SequenceEqual(labels) &&
                    scene.CurrentBoardButtons.All(button => button.Hold == BoardButtonHold.Once), "The settled drawer did not expose exactly its handle and three actions.");
                BoardRect[] bounds = [new(.01, .87, .18, .12), new(.20, .87, .155, .12), new(.365, .87, .155, .12), new(.53, .87, .155, .12)];
                Require(scene.CurrentBoardButtons.Select(button => button.Bounds).SequenceEqual(bounds), "Photo Copy's settled row diverged from Globe's physical button layout.");
                var context = scene.GetHoldButtonContext(now);
                Require(context is { ButtonIds.Count: 4 } && context.ExpectedScene.BoardTriggerRegions is { Count: 4 },
                    "The settled Photo Copy drawer did not provide its four stationary caption regions.");
            }
            void CheckSharedStyle()
            {
                using var photo = new CanvasRenderTarget(target.Device, 1000, 1000, 96);
                using var globe = new CanvasRenderTarget(target.Device, 1000, 1000, 96);
                var buttons = scene.CurrentBoardButtons.ToArray();
                var flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                var photoDraw = typeof(SceneCompositor).GetMethod("DrawPhotoCopyControls", flags)!;
                var globeDraw = typeof(SceneCompositor).GetMethod("DrawGlobeControls", flags)!;
                using (var drawing = photo.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    photoDraw.Invoke(scene, [drawing, buttons, Array.Empty<string>(), Array.Empty<BoardFingerSelectionFeedback>(), now]);
                }
                using (var drawing = globe.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    globeDraw.Invoke(null, [drawing, buttons, Array.Empty<string>(), Array.Empty<BoardFingerSelectionFeedback>(), true, 1f, width / (double)height]);
                }
                byte[] aPixels = photo.GetPixelBytes(), bPixels = globe.GetPixelBytes();
                for (int y = 866; y < 995; y++)
                    Require(aPixels.AsSpan(y * 4000, 4000).SequenceEqual(bPixels.AsSpan(y * 4000, 4000)),
                        "Photo Copy and Globe use different actual row glass, captions, radii or vector arrow proportions.");
                sharedStyleChecks++;
            }
            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            async Task<byte[]> Capture(string frame)
            {
                byte[] pixels = Draw();
                byte[] repeat = Draw();
                // The drawer clock is injected. Swirl's existing stamp timer is
                // intentionally live, so compare its protected captions once active.
                if (scene.PhotoCopyCount == 0)
                {
                    Require(pixels.SequenceEqual(repeat), "Photo Copy's drawer changed pixels at the same injected clock.");
                    sameClockChecks++;
                }
                else foreach (var button in scene.CurrentBoardButtons)
                {
                    Require(SameRegion(pixels, repeat, new(button.Bounds.X + .02, button.Bounds.Y + .02,
                        button.Bounds.Width - .04, button.Bounds.Height - .04)), "Swirl altered a stationary drawer caption.");
                    stableCaptionChecks++;
                }
                string path = Path.Combine(directory, name + "-" + frame + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                paths.Add(path);
                return pixels;
            }
            PixelPoint CameraPoint(double u, double v) => new(width * (.035 + .93 * (inset / 2 + u * (1 - inset))),
                height * (.035 + .93 * (inset / 2 + v * (1 - inset))));
            bool SameRegion(byte[] first, byte[] second, BoardRect region)
            {
                var leftTop = CameraPoint(region.X, region.Y); var rightBottom = CameraPoint(region.X + region.Width, region.Y + region.Height);
                for (int y = (int)Math.Ceiling(leftTop.Y); y < rightBottom.Y; y++)
                {
                    int left = (int)Math.Ceiling(leftTop.X), length = ((int)Math.Floor(rightBottom.X) - left) * 4;
                    if (!first.AsSpan((y * width + left) * 4, length).SequenceEqual(second.AsSpan((y * width + left) * 4, length))) return false;
                }
                return true;
            }
        }
        return new { passed = true, directory, sameClockChecks, stableCaptionChecks, stableArrowChecks, capturePreservationChecks, sharedStyleChecks,
            actualBrokenArrowHolds = true, hiddenActionsRejected = true, motionGatesHoldAndAcquisition = true,
            nativeCaptureFieldUnchanged = true, clearSaveVariantsRetained = true, results };
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
