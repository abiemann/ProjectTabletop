#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using PresentationStage = ProjectTabletop.App.Projection.MarkerSetupStage;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private offscreen scenes and synthetic registration only. This verifier
    // never starts capture or changes the user's board, output or saved marker.
    private async Task<object> VerifyMarkerSetupAsync()
    {
        const int size = 700;
        var device = CanvasDevice.GetSharedDevice();
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        using var scene = new SceneCompositor(blackjackClock: () => now, waterClock: () => now,
            footballClock: () => now, paintClock: () => now);
        var footballAudio = new List<FootballAudioUpdate>();
        scene.FootballAudioRequested += footballAudio.Add;
        using var output = new CanvasRenderTarget(device, size, size, 96);
        await scene.EnsureMenuPreviewResourcesAsync(device);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
        scene.SetDisplayAspect(1);
        scene.SetBoardFacingDegrees(0);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.08f, .08f), new(.92f, .08f), new(.92f, .92f), new(.08f, .92f)],
            Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        var session = Field<BoardSession>("_boardSession");
        var area = new BoardRect(.36, .50, .28, .24);
        const string instruction = "Place one double-bar marker inside the ring. Keep it flat and still.";
        string directory = Path.Combine(_appDataDirectory, "MarkerSetupVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var checks = new List<object>();
        byte[]? uprightNeutral = null;
        int mappingChecks = 0, referenceChecks = 0;

        foreach (int facing in new[] { 0, 180 })
        {
            scene.SetBoardFacingDegrees(facing);
            scene.ShowWaterGarden();
            Draw();
            now += TimeSpan.FromSeconds(4);
            Draw();
            Require(scene.ActivateWaterGardenButton("water-drawer-open"), "The fixture could not open the water controls.");
            now += BoardSession.WaterGardenDrawerOpeningDuration + TimeSpan.FromMilliseconds(50);
            scene.TickWaterGarden(now);
            Draw();
            var controls = scene.CurrentBoardButtons.Select(button => button.Id).ToArray();
            Require(controls.Length == 4, "The fixture needs the open Water Garden drawer.");
            long navigation = session.NavigationRevision, reset = session.WaterGardenResetRevision;
            var baseline = Source().GetPixelBytes();
            await Save("water-baseline-" + facing);
            long revision = scene.BeginMarkerSetup(PresentationStage.NeutralAcquisition, instruction, .25, area);
            Require(scene.MarkerSetupState is { PresentedAt: null }, "Setup claimed presentation before a draw.");
            Draw(preview: true);
            Require(scene.MarkerSetupState is { PresentedAt: null }, "The laptop preview claimed projector presentation.");
            Draw();
            Require(scene.MarkerSetupState is { PresentedAt: null }, "A diagnostic draw claimed projector presentation.");
            Draw(projectorFrame: true);
            var presented = scene.MarkerSetupState!.PresentedAt;
            Require(presented is not null, "The matching projector draw did not mark setup as presented.");
            AssertInputsBlocked();
            Require(!scene.ActivateWaterGardenButton("water-garden-reset") && scene.WaterGardenDrawerOpen,
                "A hidden water control activated or setup closed the drawer.");
            AssertNeutralInterior();
            AssertReferenceMatchesSource();
            AssertTargetMapping(area);
            await Save("water-neutral-" + facing);
            var neutral = output.GetPixelBytes();
            if (uprightNeutral is null) uprightNeutral = neutral;
            else AssertHalfTurn(uprightNeutral, neutral);

            long sameRevision = scene.UpdateMarkerSetup(PresentationStage.NeutralAcquisition,
                "Keep the marker flat and still.", .6, area);
            Require(sameRevision == revision && scene.MarkerSetupState!.PresentedAt == presented,
                "Progress or instructions restarted the camera settling interval.");

            long validationRevision = scene.UpdateMarkerSetup(PresentationStage.SceneValidation,
                "Move to the centre and hold still. 1 / 5", .65, area);
            Require(validationRevision != revision && scene.MarkerSetupState is { PresentedAt: null },
                "The real-scene transition reused neutral-field presentation timing.");
            Require(scene.GetMarkerProjectionFrames().Count == 0,
                "A stale neutral reference survived before validation was rendered.");
            Draw(projectorFrame: true);
            AssertInputsBlocked();
            AssertReferenceMatchesSource();
            var validation = Source().GetPixelBytes();
            await Save("water-validation-" + facing);
            AssertSameTargetInterior(baseline, validation, "Validation did not restore the real water scenery inside the target.");

            var movedTarget = area with { X = .16 };
            scene.UpdateMarkerSetup(PresentationStage.SceneValidation, "Move to the left and hold still. 2 / 5", .7, movedTarget);
            Require(scene.MarkerSetupState is { PresentedAt: null }, "Moving the target kept old presentation timing.");
            Draw(projectorFrame: true);
            AssertTargetMapping(movedTarget);
            var movementStart = new BoardRect(.18, .53, .14, .14);
            var movementEnd = new BoardRect(.68, .53, .14, .14);
            long movementRevision = scene.UpdateMarkerSetup(PresentationStage.MovementValidation,
                "Move between the two rings and back at your playing speed.", .2, movementStart, movementEnd);
            Draw(projectorFrame: true);
            var movementPresented = scene.MarkerSetupState!.PresentedAt;
            AssertInputsBlocked();
            AssertReferenceMatchesSource();
            AssertTargetMapping(movementStart);
            AssertTargetMapping(movementEnd);
            AssertMovementRingsVisible(movementStart, movementEnd);
            Require(movementPresented is not null && scene.MarkerSetupState.RouteEnd == movementEnd,
                "Movement validation did not present both endpoints.");
            await Save("water-movement-" + facing);
            Require(scene.UpdateMarkerSetup(PresentationStage.MovementValidation,
                "Now move back to the first ring.", .65, movementStart, movementEnd) == movementRevision &&
                scene.MarkerSetupState.PresentedAt == movementPresented,
                "Reversing movement or updating feedback restarted the presentation interval.");
            scene.EndMarkerSetup();
            Require(!scene.MarkerSetupActive && scene.MarkerSetupState is null,
                "Ending setup left its presentation active.");
            Draw();
            Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(controls) &&
                    scene.WaterGardenDrawerOpen && session.NavigationRevision == navigation &&
                    session.WaterGardenResetRevision == reset,
                "Setup changed navigation, reset the garden or failed to restore its controls.");
            AssertSameTargetInterior(baseline, Source().GetPixelBytes(), "Ending setup left neutral pixels on the water.");
            Require(scene.ActivateWaterGardenButton("water-garden-duck-add"),
                "Water Garden controls did not accept input after setup ended.");
            checks.Add(new { board = "WaterGarden", facing, referenceExact = true, neutralRoiClear = true,
                realSceneRestored = true, controlsAndDrawerPreserved = true });
        }

        foreach (int facing in new[] { 0, 180 })
        {
            scene.SetBoardFacingDegrees(facing);
            scene.ShowPaint();
            scene.DisablePaintIdleStreaksForVerification();
            Draw();
            now += TimeSpan.FromSeconds(2);
            Draw();
            long navigation = session.NavigationRevision;
            var controls = scene.CurrentBoardButtons.Select(button => button.Id).ToArray();
            var baseline = Source().GetPixelBytes();
            var before = scene.GetPaintDiagnostics();
            Require(before.DropCount > 0, "The Paint setup fixture needs existing artwork to preserve.");
            scene.BeginMarkerSetup(PresentationStage.NeutralAcquisition, instruction, .2, area);
            Draw(projectorFrame: true);
            AssertInputsBlocked();
            AssertNeutralInterior();
            AssertReferenceMatchesSource();
            Require(!scene.AddPaintDrop(new(.5, .62), .05, now),
                "Marker setup allowed a paint drop over its instructions.");
            Require(scene.GetPaintDiagnostics().DropCount == before.DropCount,
                "Beginning Paint setup deposited extra paint.");
            scene.UpdateMarkerSetup(PresentationStage.SceneValidation,
                "Move to the centre and hold still. 1 / 5", .4, area);
            Draw(projectorFrame: true);
            AssertInputsBlocked();
            AssertReferenceMatchesSource();
            AssertSameTargetInterior(baseline, Source().GetPixelBytes(),
                "Paint marker validation failed to restore the existing artwork.");
            AssertTargetMapping(area);
            var left = new BoardRect(.18, .43, .14, .14);
            var right = new BoardRect(.68, .43, .14, .14);
            scene.UpdateMarkerSetup(PresentationStage.MovementValidation,
                "Move between the two rings and back at your painting speed.", .6, left, right);
            Draw(projectorFrame: true);
            AssertInputsBlocked();
            AssertReferenceMatchesSource();
            AssertMovementRingsVisible(left, right);
            await Save("paint-movement-" + facing);
            scene.EndMarkerSetup();
            Draw();
            Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(controls) &&
                    session.NavigationRevision == navigation && scene.GetPaintDiagnostics().DropCount == before.DropCount,
                "Paint marker setup reset the artwork, changed navigation or failed to restore controls.");
            AssertSameTargetInterior(baseline, Source().GetPixelBytes(), "Ending setup left neutral pixels on Paint.");
            checks.Add(new { board = "Paint", facing, referenceExact = true, neutralRoiClear = true,
                realSceneRestored = true, controlsAndArtworkPreserved = true });
        }

        foreach (int facing in new[] { 0, 90, 180, 270 })
        {
            scene.SetBoardFacingDegrees(facing);
            scene.SetFootballAudioTrainingBlocked(true);
            scene.ShowFootball();
            Draw();
            Require(!footballAudio[^1].AmbienceActive, "Pending first-use setup started the crowd before its presentation.");
            scene.SetFootballAudioTrainingBlocked(false);
            Require(footballAudio[^1].AmbienceActive, "An already-trained Football entry did not start ambience.");
            var before = scene.FootballState;
            foreach (var region in new BoardRect[] { new(0, 0, .5, 1), new(.5, 0, .5, 1) })
            {
                var marker = new BoardRect(region.X + .11, .38, .28, .24);
                long audioGeneration = scene.FootballAudioGeneration;
                scene.BeginMarkerSetup(PresentationStage.NeutralAcquisition, instruction, .1, marker);
                Require(!footballAudio[^1].AmbienceActive && scene.FootballAudioGeneration > audioGeneration,
                    "Starting training did not silence Football and invalidate queued audio.");
                Draw(projectorFrame: true);
                AssertInputsBlocked();
                Require(!scene.ActivateFootballButton("football-play"), "Football PLAY activated during marker setup.");
                AssertTargetMapping(region);
                AssertTargetMapping(marker);
                scene.UpdateMarkerSetup(PresentationStage.SceneValidation, "Move through your half of the pitch.", .5, marker);
                Draw(projectorFrame: true);
                AssertReferenceMatchesSource();
                if (facing is 0 or 180) await Save($"football-validation-{facing}-{(region.X == 0 ? 1 : 2)}");
                var near = new BoardRect(region.X + .18, .20, .14, .14);
                var far = near with { Y = .66 };
                scene.UpdateMarkerSetup(PresentationStage.MovementValidation,
                    "Move between the two rings and back at your playing speed.", .3, near, far);
                Draw(projectorFrame: true);
                AssertInputsBlocked();
                AssertReferenceMatchesSource();
                AssertTargetMapping(near);
                AssertTargetMapping(far);
                AssertMovementRingsVisible(near, far);
                Require(!footballAudio[^1].AmbienceActive, "Movement training restarted Football audio.");
                if (facing is 0 or 180) await Save($"football-movement-{facing}-{(region.X == 0 ? 1 : 2)}");
                scene.UpdateMarkerSetup(PresentationStage.SceneValidation, "Checks passed. Save marker.", 1, marker);
                Draw();
                Require(!footballAudio[^1].AmbienceActive, "Checks passed restarted the crowd before Save marker.");
                scene.SetFootballAudioTrainingBlocked(true);
                scene.EndMarkerSetup();
                Draw();
                Require(!footballAudio[^1].AmbienceActive, "Ending presentation bypassed the pending-profile audio gate.");
                scene.SetFootballAudioTrainingBlocked(false);
                Require(footballAudio[^1].AmbienceActive, "Finishing saved marker setup did not restore ambience.");
                Require(scene.CurrentBoardButtons.Count > 0 && scene.FootballState.Phase == before.Phase &&
                        scene.FootballState.Mode == before.Mode,
                    "Marker setup changed the Football match or failed to restore its controls.");
            }
        }

        return new { passed = true, liveHardwareUntouched = true, mappingChecks, referenceChecks,
            projectorPresentationTiming = true, progressKeepsSettlingTimestamp = true,
            neutralFieldContainsNoSyntheticMarker = true, orientationIncludesProjectedText = true,
            controlsSuppressedAndRestored = true, footballAudioWaitsForSavedTraining = true,
            paintArtworkPreservedDuringTraining = true,
            movementEndpointsPresented = true, movementFeedbackKeepsSettlingTimestamp = true,
            trainingInvalidatesQueuedFootballAudio = true, directory, images, checks };

        void Draw(bool preview = false, bool projectorFrame = false)
        {
            using var drawing = output.CreateDrawingSession();
            scene.Draw(drawing, size, size, preview, false, isProjectorFrame: projectorFrame);
        }

        CanvasRenderTarget Source() => Field<CanvasRenderTarget>("_boardApplicationTarget");
        T Field<T>(string name) => (T)typeof(SceneCompositor).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;

        void AssertMovementRingsVisible(params BoardRect[] targets)
        {
            var source = Source();
            int width = (int)source.SizeInPixels.Width, height = (int)source.SizeInPixels.Height;
            var pixels = source.GetPixelBytes();
            foreach (var target in targets)
            foreach (int direction in new[] { -1, 1 })
            {
                int cx = (int)Math.Round((target.X + target.Width * .5) * width);
                int cy = (int)Math.Round((target.Y + target.Height * .5 +
                    direction * (target.Height * .707107 + .012)) * height);
                bool cyan = false;
                for (int y = Math.Max(0, cy - 3); y <= Math.Min(height - 1, cy + 3); y++)
                for (int x = Math.Max(0, cx - 3); x <= Math.Min(width - 1, cx + 3); x++)
                {
                    int at = (y * width + x) * 4;
                    if (pixels[at] > 200 && pixels[at + 1] > 200 && pixels[at + 2] < 190) cyan = true;
                }
                Require(cyan, "The instruction panel covered a movement ring's upper or lower arc.");
            }
        }

        void AssertInputsBlocked()
        {
            Require(scene.CurrentBoardButtons.Count == 0 && scene.GetHoldButtonContext(now) is null &&
                    scene.GetControlSwipeContext(now) is null,
                "Marker setup exposed controls, caption sensing or swipe input.");
            Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, ExpectedScene: null,
                    IlluminatedHint: null, RestrictAcquisitionToSearchRegions: true },
                "Marker setup allowed a hand-search light or unrestricted hand acquisition.");
        }

        void AssertTargetMapping(BoardRect region)
        {
            var polygon = scene.GetMarkerSetupCameraRegion(region);
            Require(polygon.Count == 4, "The camera target is not a four-corner polygon.");
            var map = Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                polygon.Select(point => new Point2(point.X, point.Y)).ToArray());
            foreach (var uv in new Vector2[] { new(.15f, .2f), new(.5f, .5f), new(.83f, .79f) })
            {
                var pixel = map.Transform(new(uv.X, uv.Y));
                Require(scene.TryMapMarkerSetupCameraPoint(new((float)pixel.X, (float)pixel.Y), out var board) &&
                        Math.Abs(board.X - region.X - uv.X * region.Width) < .00001 &&
                        Math.Abs(board.Y - region.Y - uv.Y * region.Height) < .00001,
                    "The rotated target did not round-trip between camera pixels and player-facing board coordinates.");
                mappingChecks++;
            }
        }

        void AssertReferenceMatchesSource()
        {
            var frames = scene.GetMarkerProjectionFrames();
            Require(frames.Count == 1, "A presentation transition retained references from its previous stage.");
            var frame = frames[0];
            var source = Source();
            using var expected = new CanvasRenderTarget(device, frame.Width, frame.Height, 96);
            using (var drawing = expected.CreateDrawingSession())
                drawing.DrawImage(source, new Rect(0, 0, frame.Width, frame.Height),
                    new Rect(0, 0, source.SizeInPixels.Width, source.SizeInPixels.Height),
                    1, CanvasImageInterpolation.HighQualityCubic, CanvasComposite.Copy);
            Require(frame.Bgra.SequenceEqual(expected.GetPixelBytes()),
                "Marker detector reference pixels differ from the actual displayed board texture.");
            referenceChecks++;
        }

        void AssertNeutralInterior()
        {
            var source = Source();
            byte[] pixels = source.GetPixelBytes();
            int width = (int)source.SizeInPixels.Width, height = (int)source.SizeInPixels.Height;
            ForTargetInterior(width, height, offset => Require(pixels[offset] == 224 && pixels[offset + 1] == 230 &&
                pixels[offset + 2] == 230 && pixels[offset + 3] == 255,
                "Learning ROI contains scenery, instructions or an artificial black marker."));
        }

        void AssertSameTargetInterior(byte[] expected, byte[] actual, string message)
        {
            var sizeInPixels = Source().SizeInPixels;
            Require(expected.Length == actual.Length, "Marker presentation changed the board raster size.");
            int differences = 0, maximum = 0, first = -1;
            ForTargetInterior((int)sizeInPixels.Width, (int)sizeInPixels.Height, offset =>
            {
                if (expected.AsSpan(offset, 4).SequenceEqual(actual.AsSpan(offset, 4))) return;
                differences++;
                if (first < 0) first = offset;
                for (int channel = 0; channel < 4; channel++)
                    maximum = Math.Max(maximum, Math.Abs(expected[offset + channel] - actual[offset + channel]));
            });
            Require(differences == 0, $"{message} Changed pixels: {differences}; max channel delta: {maximum}; " +
                $"first pixel: {first / 4 % (int)sizeInPixels.Width},{first / 4 / (int)sizeInPixels.Width}; captures: {directory}");
        }

        void ForTargetInterior(int width, int height, Action<int> inspect)
        {
            for (int y = (int)Math.Ceiling(area.Y * height) + 3; y < (area.Y + area.Height) * height - 3; y++)
            for (int x = (int)Math.Ceiling(area.X * width) + 3; x < (area.X + area.Width) * width - 3; x++)
                inspect((y * width + x) * 4);
        }

        static void AssertHalfTurn(byte[] upright, byte[] turned)
        {
            double error = 0;
            int different = 0;
            for (int pixel = 0; pixel < size * size; pixel++)
            {
                int a = pixel * 4, b = (size * size - pixel - 1) * 4;
                int maximum = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(upright[a + channel] - turned[b + channel]);
                    error += delta;
                    maximum = Math.Max(maximum, delta);
                }
                if (maximum > 16) different++;
            }
            Require(error / (size * size * 3) < .5 && different < size * size * .003,
                "The target or instruction text failed to rotate with the board's 180-degree facing.");
        }

        async Task Save(string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await output.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
