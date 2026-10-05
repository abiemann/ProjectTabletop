#if DEBUG
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Exercise the rendered caption -> camera evidence -> shared hold -> exact
    // frame request path. No hand model, landmark or synthetic selection event
    // participates. Real clock pacing also exercises SetHandCursors freshness.
    private sealed class PhotoCopyCaptionHoldFixture(SceneCompositor scene, Func<byte[]> draw, int size,
        Action<DateTimeOffset>? setClock = null, int? height = null)
    {
        private readonly int _height = height ?? size;
        private readonly HandAcquisitionPresenceTracker _presence = new();
        private long _revision = -1;
        public int SuccessfulHolds { get; private set; }
        public int BrokenCaptionFrames { get; private set; }
        public List<object> FrameDiagnostics { get; } = [];
        public byte[]? LastCameraPixels { get; private set; }
        public SceneCompositor.HoldButtonContext? LastContext { get; private set; }
        private DateTimeOffset? _lastEvidenceTime;

        public async Task OpenDrawerAsync()
        {
            if (!scene.PhotoCopyDrawerOpen)
                Check(scene.ActivatePhotoCopyButton("photo-drawer-open"), "Photo Copy drawer could not open.");
            await Task.Delay(350);
            scene.TickPhotoCopy(MonotonicClock.UtcNow);
            draw();
            Check(scene.PhotoCopyDrawerOpen && scene.CurrentBoardButtons.Any(button => button.Id == "menu" && button.Enabled),
                "Photo Copy actions did not settle after opening the drawer.");
        }

        public async Task<DateTimeOffset> HoldAsync(string id, bool busy = false,
            bool expectActivation = true, bool prepareHeldFrames = true)
        {
            await ReleaseAsync(id, busy);
            DateTimeOffset firstEvidence = default, last = default;
            for (int frame = 0; frame < 18; frame++)
            {
                var observed = await FeedAsync(id, broken: true, busy, prepareHeldFrames);
                last = observed.Time;
                if (observed.Held && firstEvidence == default) firstEvidence = last;
                if (observed.Activated.Count == 0) continue;
                Check(expectActivation && observed.Activated.SequenceEqual([id]),
                    id + " activated while unavailable or selected another control.");
                Check(firstEvidence != default && last - firstEvidence >= BoardSession.HoldActivationInterval,
                    id + " activated before one continuous second of broken lettering.");
                SuccessfulHolds++;
                return last;
            }
            Check(!expectActivation, id + " did not complete its rendered-caption hold.");
            return last;
        }

        public async Task<DateTimeOffset> ContinueHeldAsync(string id)
        {
            DateTimeOffset last = default;
            for (int frame = 0; frame < 12; frame++)
            {
                var observed = await FeedAsync(id, broken: true);
                last = observed.Time;
                Check(observed.Activated.Count == 0, id + " repeated while its caption stayed covered.");
            }
            return last;
        }

        public async Task CheckReleaseCancelsAsync(string id)
        {
            await ReleaseAsync(id);
            for (int frame = 0; frame < 4; frame++)
                Check((await FeedAsync(id, broken: true)).Activated.Count == 0,
                    id + " activated during a short obstruction.");
            Check(scene.CurrentHoldProgress.Any(progress => progress.ButtonId == id),
                id + " never started the partial hold used by the release check.");
            var cleared = await FeedAsync(id, broken: false);
            Check(cleared.Activated.Count == 0 && scene.CurrentHoldProgress.All(progress => progress.ButtonId != id),
                id + " kept partial progress after its lettering became intact.");
        }

        private async Task ReleaseAsync(string id, bool busy = false)
        {
            // More than the shared 350 ms release interval: a spent single-action
            // position must rearm from freshly rendered, uncovered lettering.
            for (int frame = 0; frame < 5; frame++)
                Check((await FeedAsync(id, broken: false, busy)).Activated.Count == 0,
                    "Intact " + id + " lettering activated a Photo Copy control.");
        }

        private async Task<(DateTimeOffset Time, bool Held, IReadOnlyList<string> Activated)> FeedAsync(
            string id, bool broken, bool busy = false, bool prepare = true)
        {
            await Task.Delay(100);
            var time = MonotonicClock.UtcNow;
            setClock?.Invoke(time);
            if (prepare) scene.SetHandCursors([], time, photoCopyCaptureBusy: busy);
            // SetHandCursors updates the board with its own live monotonic clock.
            // Preserve the camera source timestamp, then sample a newer consumer
            // clock just as the live pipeline does after recognition work.
            setClock?.Invoke(MonotonicClock.UtcNow);
            byte[] pixels = draw();
            var context = scene.GetHoldButtonContext(time) ??
                throw new InvalidOperationException("Photo Copy has no current rendered hold reference.");
            if (_revision != context.Revision)
            {
                _presence.Reset();
                _revision = context.Revision;
            }
            int index = context.ButtonIds.ToList().IndexOf(id);
            Check(index >= 0 && scene.CurrentBoardButtons.Single(button => button.Id == id).Hold == BoardButtonHold.Once,
                id + " is not a visible single-action caption hold.");
            if (broken)
            {
                var trigger = context.ExpectedScene.BoardTriggerRegions![index];
                var matrix = context.ExpectedScene.CameraToBoard;
                Point2[] corners = [new(0, 0), new(size, 0), new(size, _height), new(0, _height)];
                var map = Homography.FromFourPoints(corners, corners.Select(point =>
                {
                    double denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
                    return new Point2((matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator,
                        (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator);
                }).ToArray());
                var center = map.InverseTransform(new(trigger.X + trigger.Width / 2, trigger.Y + trigger.Height / 2));
                // Four separated, stationary occlusion strips visibly break the
                // native letters while leaving the surrounding panel as reference.
                for (int strip = 0; strip < 4; strip++)
                    for (int y = Math.Max(0, (int)center.Y - 34); y < Math.Min(_height, (int)center.Y + 34); y++)
                        for (int x = Math.Max(0, (int)center.X - 38 + strip * 20);
                            x < Math.Min(size, (int)center.X - 20 + strip * 20); x++)
                        {
                            int offset = (y * size + x) * 4;
                            pixels[offset] = 75; pixels[offset + 1] = 95; pixels[offset + 2] = 185; pixels[offset + 3] = 255;
                        }
            }
            var presence = _presence.Update(size, _height, size * 4, pixels, context.SearchPolygon,
                context.ExpectedScene, time, MonotonicClock.UtcNow);
            var held = context.HeldButtons(presence);
            var cleared = context.ClearedButtons(presence);
            if (held.Contains(id))
            {
                Check(broken && presence.TextPatterns!.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, LabelIntact: false, ConfirmationFrames: >= 1 },
                    id + " qualified without genuinely broken rendered lettering.");
                BrokenCaptionFrames++;
            }
            setClock?.Invoke(MonotonicClock.UtcNow);
            var activated = scene.ObserveHoldButtons(context, held, time, cleared);
            LastCameraPixels = pixels;
            LastContext = context;
            FrameDiagnostics.Add(new { id, broken, time,
                gapMilliseconds = _lastEvidenceTime is { } previous ? (time - previous).TotalMilliseconds : 0,
                processingMilliseconds = (MonotonicClock.UtcNow - time).TotalMilliseconds,
                revision = context.Revision, held, cleared, activated,
                patterns = presence.TextPatterns?.Where(pattern => pattern.ControlRegion == index).ToArray(),
                progress = scene.CurrentHoldProgress.ToArray(), reason = presence.Reason });
            _lastEvidenceTime = time;
            return (time, held.Contains(id), activated);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
