#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> SaveHandAcquisitionSnapshotAsync()
    {
        HoldButtonSnapshot? hold = null;
        SceneCompositor.HandAcquisitionContext? context;
        CameraFrameLease retainedFrame;
        object? acquisitionDetection;
        lock (_handGate)
        {
            if (_scene.CurrentBoardScreen == BoardScreen.StrykersRun)
            {
                if (!_boardHandDiagnosticLogging)
                    throw new InvalidOperationException("Enable board diagnostic logging before capturing a Stryker's Run caption snapshot.");
                hold = _lastHoldButtonSnapshot;
                if (hold is null || !hold.Context.ButtonIds.Any(id => id.StartsWith("strykers-run-", StringComparison.Ordinal)))
                    throw new InvalidOperationException("No processed Stryker's Run caption frame with its generated reference yet.");
            }
            retainedFrame = hold?.Frame.Retain() ?? _lastHandAcquisitionFrame.Acquire() ??
                throw new InvalidOperationException("No acquisition camera frame.");
            context = hold is null ? _lastHandAcquisitionContext : null;
            acquisitionDetection = _lastHandAcquisitionDetection;
        }
        using var frameLease = retainedFrame;
        var frame = frameLease.Frame;
        var expected = hold?.Context.ExpectedScene ?? context?.ExpectedScene ??
            throw new InvalidOperationException("No generated acquisition reference yet.");
        object? detection = hold is null ? acquisitionDetection : new
        {
            frameTime = hold.Frame.Timestamp, hold.Context.Revision, hold.Generation, hold.ProcessedAt,
            hold.Context.ButtonIds, hold.Context.EnabledHoldIds,
            held = hold.Query.Held, cleared = hold.Query.Cleared, presence = hold.Query.Presence
        };
        string directory = Path.Combine(_appDataDirectory, "HandAcquisitionSnapshots",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            snapshotKind = hold is null ? "hand-acquisition" : "hold-caption",
            latestCameraFrameUtc = Volatile.Read(ref _latestCameraFrame)?.Timestamp,
            frame.Timestamp, cameraWidth = frame.Width, cameraHeight = frame.Height, frame.Stride,
            expected.Width, expected.Height, expected.CameraToBoard, expected.BoardSearchRegions,
            expected.BoardReferenceRegions, expected.BoardTriggerRegions, expected.BoardControlReferenceRegions,
            expected.BoardVectorGlyphRegions,
            expected.AllowsLocalForegroundContext, Revision = hold?.Context.Revision ?? context!.Revision,
            SearchPolygon = hold?.Context.SearchPolygon ?? context!.SearchPolygon,
            IlluminatedHint = context?.IlluminatedHint, IlluminationStartedAt = context?.IlluminationStartedAt,
            lighting = _scene.GetHandAcquisitionDiagnostics(), detection
        });
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "expected.bgra"), expected.Bgra);
        await File.WriteAllBytesAsync(Path.Combine(directory, "camera.bgra"), frame.Bgra);
        await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), metadata);
        return new { directory };
    }

    private async Task<object> VerifyHandAcquisitionAsync()
    {
        const int size = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var game = new BlackjackGame(41, new[] { 2, 6, 2, 10, 2, 2, 3, 4 }
            .Select(rank => new BlackjackCard(rank, BlackjackSuit.Clubs)));
        using var scene = new SceneCompositor(game, blackjackClock: () => now);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
            Homography.FromFourPoints([new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowBlackjack();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false }, "Scene changes need a settling interval.");
        now += TimeSpan.FromMilliseconds(600);
        var ready = scene.GetHandAcquisitionContext(now)!;
        Require(ready.ObserveMotion, "The settled Blackjack button area did not start watching.");
        var center = BoardPoint(.84, .0765);
        var hint = new HandAcquisitionHint(new(center.X - 140, center.Y - 140, 280, 280),
            center, 82, now, .05, ControlCoverage: .10, ControlTriggerCoverage: .10);
        var baseline = Draw();
        ready = scene.GetHandAcquisitionContext(now)!;
        Require(ready.ExpectedScene is { Width: 1000, Height: 1000 } expected &&
            expected.Bgra.Length == 1000 * 1000 * 4,
            "The generated unlit table was not available for stationary-hand comparison.");
        Require(ready.StationarySearchCenters?.Length == scene.CurrentBoardButtons.Count,
            "Stationary button searches must include every Blackjack control.");
        long gameRevision = scene.BlackjackState.Revision;
        foreach (var coin in scene.CurrentBoardButtons.Where(button => button.Id.StartsWith("bj-bet-", StringComparison.Ordinal) || button.Id == "menu"))
        {
            var coinCenter = BoardPoint(coin.Bounds.X + coin.Bounds.Width / 2, coin.Bounds.Y + coin.Bounds.Height / 2);
            scene.CompleteHandAcquisition(ready, [hint with { Center = coinCenter }], [], now);
            Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && scene.BlackjackState.Revision == gameRevision,
                "The long-press " + coin.Id + " control started a hand-search light or changed the wager.");
        }
        foreach (double? coverage in new double?[] { null, .069999, double.NaN,
                     double.PositiveInfinity, double.NegativeInfinity })
        {
            scene.CompleteHandAcquisition(ready, [hint with { ControlCoverage = coverage }], [], now);
            Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && !IsWhite(Draw(), center),
                "Unmeasured, sub-7%, or non-finite foreground evidence started acquisition illumination.");
            scene.CompleteHandAcquisition(ready, [hint with { ControlTriggerCoverage = coverage }], [], now);
            Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && !IsWhite(Draw(), center),
                "Unmeasured, sub-7%, or non-finite label evidence started acquisition illumination.");
        }
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now.AddMilliseconds(-1) }], [], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
            "Foreground evidence from another camera frame started acquisition illumination.");
        // An accepted current model result uses normal hand lighting. It must
        // not turn on the preliminary light first or perform a board action.
        var nativeAccepted = new HandDetection(Enumerable.Repeat(center, 21).ToArray(), .99, .5);
        scene.CompleteHandAcquisition(ready, [hint], [nativeAccepted], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && !IsWhite(Draw(), center) &&
                scene.BlackjackState.Revision == gameRevision && scene.ActiveHandSpotlightCount == 0 &&
                scene.HoveredBoardButtons.Count == 0,
            "An accepted native hand started fallback illumination or executed a board action.");
        now += TimeSpan.FromMilliseconds(600);
        ready = scene.GetHandAcquisitionContext(now)!;
        hint = hint with { ObservedAt = now };
        scene.CompleteHandAcquisition(ready, [hint with { ControlCoverage = .07, ControlTriggerCoverage = .07 }], [], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is not null,
            "Fresh foreground covering exactly 7% of a control did not get acquisition illumination.");
        var lit = Draw();
        Require(IsWhite(lit, center) && !IsWhite(baseline, center), "The acquisition light did not cover the Your Chips caption.");
        // Without a measured outline, light the hand reaching in from the viewer's edge too.
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint?.ValidatedCandidateBounds is { } expectedHand &&
            expectedHand.Y + expectedHand.Height > center.Y + 40 && IsWhite(lit, BoardPoint(.84, .23)) &&
            !IsWhite(baseline, BoardPoint(.84, .23)),
            "A caption-only hint did not extend its light over the reaching hand toward the viewer.");
        Require(lit.Take(4).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Search light escaped the board clip.");
        Require(scene.BlackjackState.Revision == gameRevision && scene.ActiveHandSpotlightCount == 0 &&
            scene.HoveredBoardButtons.Count == 0, "Motion manufactured a hand, hover, or game action.");
        Require(!scene.GetHandAcquisitionContext(now)!.ObserveMotion, "The search light was allowed to detect its own redraw.");

        var illuminated = scene.GetHandAcquisitionContext(now)!;
        Require(ReferenceEquals(illuminated.ExpectedScene, ready.ExpectedScene),
            "The search light replaced the unlit rendered reference.");
        for (int index = 0; index < 5; index++)
        {
            now += TimeSpan.FromMilliseconds(500);
            hint = hint with { ObservedAt = now, ControlCoverage = index == 0 ? .07 : .10 };
            scene.CompleteHandAcquisition(illuminated, [hint], [], now, illuminatedPresence: true);
            illuminated = scene.GetHandAcquisitionContext(now)!;
            Require(illuminated.IlluminatedHint is not null && IsWhite(Draw(), center),
                "Fresh stationary foreground evidence did not sustain the search light.");
        }
        // Night exposure clips projected white over the hand. That reading dims
        // and restarts the light; a later unclipped lit board restores it.
        now += TimeSpan.FromMilliseconds(100);
        scene.CompleteHandAcquisition(illuminated, [hint], [], now, illuminatedWhite: 254);
        var dimmed = scene.GetHandAcquisitionContext(now)!;
        var dimPixels = Draw();
        int dimOffset = ((int)center.Y * size + (int)center.X) * 4;
        Require(dimmed.IlluminatedHint is not null && dimmed.IlluminationStartedAt == now &&
            scene.SearchLightLevel is > .8 and < .9 && dimPixels[dimOffset] is > 200 and < 230 &&
            dimPixels[dimOffset + 1] == dimPixels[dimOffset] && dimPixels[dimOffset + 2] == dimPixels[dimOffset],
            "A clipped lit core did not dim and restart the search light.");
        scene.CompleteHandAcquisition(illuminated, [hint], [], now, illuminatedWhite: 254);
        Require(scene.SearchLightLevel is > .8 and < .9, "A reading from the replaced light dimmed it again.");
        now += TimeSpan.FromMilliseconds(300);
        hint = hint with { ObservedAt = now };
        scene.CompleteHandAcquisition(dimmed, [hint], [], now, illuminatedPresence: true, illuminatedWhite: 150);
        illuminated = scene.GetHandAcquisitionContext(now)!;
        Require(scene.SearchLightLevel == 1 && illuminated.IlluminatedHint is not null && IsWhite(Draw(), center),
            "An unclipped lit reading did not restore the full search light.");
        scene.CompleteHandAcquisition(illuminated, [hint], [], now, illuminatedPresence: false);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null } &&
            !IsWhite(Draw(), center), "An empty illuminated area retained its search light.");

        // A foreground flag alone must not bypass the measured threshold after
        // the spotlight is already lit. Each result belongs to this exact frame
        // and the original illuminated control, even when the hand holds still.
        foreach (double? coverage in new double?[] { null, .069999, double.NaN,
                     double.PositiveInfinity, double.NegativeInfinity })
        {
            AssertInvalidRenewal([hint with { ControlCoverage = coverage }], refreshObservation: true);
            AssertInvalidRenewal([hint with { ControlTriggerCoverage = coverage }], refreshObservation: true);
        }
        AssertInvalidRenewal([], refreshObservation: true);
        AssertInvalidRenewal([hint], refreshObservation: false);
        AssertInvalidRenewal([hint with { Center = BoardPoint(.3, .83) }], refreshObservation: true);

        now += TimeSpan.FromMilliseconds(500);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false },
            "The camera's delayed view of a switched-off light was allowed to retrigger acquisition.");
        now += TimeSpan.FromMilliseconds(450);
        ready = scene.GetHandAcquisitionContext(now)!;
        hint = hint with { ObservedAt = now };
        // Clipped white captions before lighting start a darker gray light.
        scene.CompleteHandAcquisition(ready, [hint], [], now, projectedWhiteClipped: true);
        illuminated = scene.GetHandAcquisitionContext(now)!;
        var nightPixels = Draw();
        int nightOffset = ((int)center.Y * size + (int)center.X) * 4;
        Require(illuminated.IlluminatedHint is not null && scene.SearchLightLevel == .8 &&
            nightPixels[nightOffset] is > 195 and < 215 && nightPixels[nightOffset + 2] == nightPixels[nightOffset],
            "Clipped captions did not start a darker gray search light.");
        now += TimeSpan.FromMilliseconds(500);
        scene.CompleteHandAcquisition(illuminated, [hint], [], now.AddMilliseconds(-450), illuminatedPresence: true);

        now += TimeSpan.FromMilliseconds(450);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Search illumination did not expire into a quiet interval.");
        Require(!IsWhite(Draw(), center), "An expired search light remained visible.");
        now += TimeSpan.FromMilliseconds(900);
        ready = scene.GetHandAcquisitionContext(now)!;
        Require(ready.ObserveMotion, "Motion watching did not resume after illumination settled.");
        hint = hint with { ObservedAt = now };
        scene.CompleteHandAcquisition(ready, [hint], [], now, projectedWhiteClipped: false);
        Require(scene.SearchLightLevel == 1 && IsWhite(Draw(), center),
            "Unclipped captions did not return the search light to full white.");
        var inferred = new HandDetection(Enumerable.Repeat(center, 21).ToArray(), .99, .5);
        scene.CompleteHandAcquisition(ready, [hint], [inferred], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
            "The preliminary light was not removed when a real hand inference arrived.");

        now += TimeSpan.FromMilliseconds(600);
        ready = scene.GetHandAcquisitionContext(now)!;
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now.AddSeconds(-1));
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null, "A stale frame started illumination.");
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now.AddSeconds(1));
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null, "A future frame started illumination.");
        scene.ActivateBlackjackButton("bj-deal");
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "An old scene's result survived the deal redraw.");
        now += TimeSpan.FromMilliseconds(600);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Opening cards allowed acquisition before the final landing.");
        now += TimeSpan.FromMilliseconds(1200);
        Draw(); // Present the landed deal and release the temporary disabled controls.
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        Draw();
        ready = scene.GetHandAcquisitionContext(now)!;
        // Player-turn actions and EXIT stay unlit; the ordinary counter keeps gesture assistance.
        scene.CompleteHandAcquisition(ready, [hint with { Center = BoardPoint(.55, .83), ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && !IsWhite(Draw(), center),
            "A long-press player action was lit.");
        var counterCenter = BoardPoint(.84, .0765);
        var counterHint = hint with { SearchBounds = new(counterCenter.X - 140, Math.Max(0, counterCenter.Y - 140), 280, 280),
            Center = counterCenter, ObservedAt = now };
        scene.CompleteHandAcquisition(ready, [counterHint], [], now);
        Require(IsWhite(Draw(), counterCenter), "The ordinary Your Chips counter did not allow assistance.");
        Require(scene.ActivateBlackjackButton("bj-hit"), "The fixture could not HIT.");
        Require(!IsWhite(Draw(), counterCenter) && scene.GetHandAcquisitionContext(now) is { ObserveMotion: false },
            "A card flight kept the obsolete light or enabled motion scanning.");
        now += TimeSpan.FromMilliseconds(800);
        Draw(); // Observe the completed HIT before requesting its new control template.
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        Draw();
        ready = scene.GetHandAcquisitionContext(now)!;
        Require(ready.ObserveMotion, "Motion watching did not recover after the HIT animation.");
        // Finish the round before checking navigation and ordinary-control acquisition again.
        Require(scene.ActivateBlackjackButton("bj-stand"), "The fixture could not stand.");
        for (int step = 0; step < 20 && scene.BlackjackState.Phase != BlackjackPhase.RoundOver; step++)
        {
            now += TimeSpan.FromMilliseconds(400);
            Draw();
        }
        Require(scene.BlackjackState.Phase == BlackjackPhase.RoundOver, "The dealer fixture did not settle.");
        Draw(); scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        Draw();
        ready = scene.GetHandAcquisitionContext(now)!;
        scene.ShowBoardMenu();
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Blackjack's old acquisition result survived navigation to the menu.");

        scene.ShowBlackjack();
        scene.ClearHandTips();
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        ready = scene.GetHandAcquisitionContext(now)!;
        await Task.Delay(5);
        var sourceTime = MonotonicClock.UtcNow;
        scene.SetHandCursors([new HandCursor(BoardPoint(.5, .5), sourceTime.AddSeconds(1), 999)
            { TrackingId = 79 }], sourceTime);
        Require(scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(79), "The fixture did not suppress an executed hand.");
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Motion relit a hand that executed a selection.");
        scene.ClearHandTips();
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        ready = scene.GetHandAcquisitionContext(now)!;
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        // A caption locates the fingertips, not necessarily the palm. Assistance
        // may follow independently measured connected context while selection
        // and illuminated renewal stay anchored to the original control.
        scene.CompleteHandAcquisition(scene.GetHandAcquisitionContext(now), [], [], now, illuminatedPresence: false);
        now += TimeSpan.FromMilliseconds(1000);
        ready = scene.GetHandAcquisitionContext(now)!;
        var connectedGameRevision = scene.BlackjackState.Revision;
        var candidate = hint with { ObservedAt = now,
            CandidateBounds = new(center.X - 65, center.Y - 20, 130, 180) };
        scene.CompleteHandAcquisition(ready, [candidate], [], now);
        var connected = scene.GetHandAcquisitionContext(now)!;
        var palmCenter = candidate.IlluminationCenter;
        Require(connected.IlluminatedHint?.Center == candidate.Center &&
                connected.IlluminatedHint.CandidateBounds == candidate.CandidateBounds &&
                IsWhite(Draw(), palmCenter) && IsWhite(Draw(), center),
            "Connected acquisition light failed to cover palm context and preserve its control anchor.");
        var query = CreateHandAcquisitionQuery(new Camera.CameraFrame(size, size, size * 4,
            new byte[size * size * 4], now), connected, new HandAcquisitionPresenceTracker(), now);
        var crop = query.SearchRegions.Single();
        Require(crop.Width == crop.Height && crop.X >= 0 && crop.Y >= 0 &&
                crop.X + crop.Width <= size && crop.Y + crop.Height <= size &&
                palmCenter.X >= crop.X && palmCenter.X < crop.X + crop.Width &&
                palmCenter.Y >= crop.Y && palmCenter.Y < crop.Y + crop.Height &&
                center.X >= crop.X && center.X < crop.X + crop.Width &&
                center.Y >= crop.Y && center.Y < crop.Y + crop.Height,
            "Connected context lost the palm, control core, or native square camera bounds.");
        Require(scene.BlackjackState.Revision == connectedGameRevision && scene.HoveredBoardButtons.Count == 0,
            "Connected geometry manufactured a button selection.");
        // Lost hands are searched with the acquisition crop's 60% frame context.
        var lostHand = new HandDetection(Enumerable.Range(0, 21)
            .Select(index => new PixelPoint(1000 + index * 5, 500 + index * 7)).ToArray(), .99, .5);
        Require(LostHandBounds(lostHand, 1920, 1080) is { Width: 324, Height: 324 } lostBounds &&
            Math.Abs(lostBounds.X + 162 - 1050) <= 1 && Math.Abs(lostBounds.Y + 162 - 570) <= 1,
            "A lost hand's search crop was not a square around its last landmarks.");
        var edgeHand = lostHand with { Landmarks = lostHand.Landmarks.Select(point => new PixelPoint(point.X + 850, point.Y - 500)).ToArray() };
        Require(LostHandBounds(edgeHand, 1920, 1080) is { X: >= 0, Y: >= 0 } edgeBounds &&
            edgeBounds.X + edgeBounds.Width <= 1920 && edgeBounds.Y + edgeBounds.Height <= 1080,
            "A lost hand at the camera edge produced a crop outside the frame.");
        Require(LostHandBounds(lostHand with { Landmarks = [new(double.NaN, double.NaN)] }, 1920, 1080) is null,
            "A lost hand without finite landmarks produced a search crop.");
        // A lit search alternates its full crop with rotating closer corners.
        var litCrop = new HandTrackingBounds(676, 22, 648, 648);
        var litViews = Enumerable.Range(0, 12).Select(step =>
            LitSearchView(litCrop, TimeSpan.FromMilliseconds(step * 100 + 50), 1920, 1080)).ToArray();
        Require(litViews.Where((_, step) => step % 2 == 0).All(view => view is null) &&
            litViews.Where((_, step) => step % 2 == 1).Take(4).Distinct().Count() == 4 &&
            litViews.OfType<HandTrackingBounds>().All(view => view.Width == 432 && view.Height == 432 &&
                view.X >= litCrop.X && view.Y >= litCrop.Y && view.X + 432 <= litCrop.X + 648 && view.Y + 432 <= litCrop.Y + 648),
            "A lit search did not alternate its crop with four closer corner views.");
        Require(LitSearchView(new(900, 300, 300, 300), TimeSpan.FromMilliseconds(350), 1920, 1080) is null &&
            LitSearchView(litCrop, TimeSpan.FromMilliseconds(-150), 1920, 1080) is null,
            "A small crop or a frame before the light was given a corner view.");
        await VerifyCaptionHeightPriorityAsync(reverseCamera: false);
        await VerifyCaptionHeightPriorityAsync(reverseCamera: true);
        scene.SetBoardSetup(true);
        Require(scene.GetHandAcquisitionContext(now) is null, "Calibration did not cancel assistance.");
        return new { passed = true, reachingHandLight = true, lostHandSearchCrop = true, litSearchCornerViews = true, gestureCaptionLit = true, bettingCoinsAndExitStayUnlit = true, motionCannotSelect = true,
            expiresWithoutFeedback = true, handHandover = true, staleAndSceneRejection = true,
            animationQuiet = true, executeSuppressionPreserved = true, boardClip = true,
            generatedUnlitReference = true, stationarySearchCenters = true, acceptedNativeHandSkipsFallbackLight = true,
            stationaryPresenceRetainsLight = true, emptyAndStalePresenceCannotRetainLight = true,
            sevenPercentBoundaryEnforced = true, nonfiniteAndUnmeasuredEvidenceRejected = true,
            renewalRequiresFreshMeasuredEvidenceAtOriginalControl = true,
            highestCaptionPriorityAcrossCameraOrientations = true,
            higherCaptionReplacesLowerSearchWithoutSelection = true,
            connectedContextLightsPalmAndControlWithoutSelecting = true };

        async Task VerifyCaptionHeightPriorityAsync(bool reverseCamera)
        {
            var priorityNow = now;
            using var priorityScene = new SceneCompositor(blackjackClock: () => priorityNow);
            using var priorityTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
            priorityScene.SetDisplayAspect(1);
            priorityScene.SetBoardSetup(true);
            var priorityInset = priorityScene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
                Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)], reverseCamera
                    ? [new(1, 1), new(0, 1), new(0, 0), new(1, 0)]
                    : [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            priorityScene.SetBoardSetup(false);
            priorityScene.ShowBoardMenu();
            await priorityScene.EnsureMenuPreviewResourcesAsync(priorityTarget.Device);
            var watching = Settle();
            var lower = Caption("crown-deed");
            var higher = Caption("football");
            var hold = Caption("menu-scroll-down");
            Require(reverseCamera ? higher.Center.Y > lower.Center.Y : higher.Center.Y < lower.Center.Y,
                "Caption priority fixture did not reverse camera Y independently of board height.");
            long revision = priorityScene.BlackjackState.Revision;
            priorityScene.CompleteHandAcquisition(watching, [lower, higher], [], priorityNow);
            Require(priorityScene.GetHandAcquisitionContext(priorityNow)?.IlluminatedHint?.Center == higher.Center,
                "Initial acquisition selected lower arm interference instead of the higher caption.");
            priorityScene.CompleteHandAcquisition(priorityScene.GetHandAcquisitionContext(priorityNow), [], [], priorityNow,
                illuminatedPresence: false);
            priorityNow += TimeSpan.FromMilliseconds(1000);
            watching = priorityScene.GetHandAcquisitionContext(priorityNow)!;
            lower = Caption("crown-deed"); higher = Caption("football"); hold = Caption("menu-scroll-down");
            var invalidHigher = new[] { higher with { ObservedAt = priorityNow.AddMilliseconds(-1) },
                higher with { ControlCoverage = .069999 }, higher with { ControlTriggerCoverage = .069999 },
                higher with { ControlCoverage = double.NaN }, hold };
            priorityScene.CompleteHandAcquisition(watching, [.. invalidHigher, lower], [], priorityNow);
            var lowerSearch = priorityScene.GetHandAcquisitionContext(priorityNow)!;
            Require(lowerSearch.IlluminatedHint?.Center == lower.Center,
                "Stale, insufficient, nonfinite or hold evidence displaced the valid lower caption.");
            priorityNow += TimeSpan.FromMilliseconds(500);
            lower = Caption("crown-deed"); higher = Caption("football");
            priorityScene.CompleteHandAcquisition(lowerSearch,
                [.. invalidHigher.Select(item => item with { ObservedAt = item == invalidHigher[0]
                    ? priorityNow.AddMilliseconds(-1) : priorityNow }), lower], [], priorityNow, illuminatedPresence: true);
            Require(priorityScene.GetHandAcquisitionContext(priorityNow)?.IlluminationStartedAt == lowerSearch.IlluminationStartedAt,
                "Invalid higher-caption evidence restarted an otherwise valid stationary search.");
            priorityScene.CompleteHandAcquisition(lowerSearch, [lower, higher], [], priorityNow, illuminatedPresence: true);
            var higherSearch = priorityScene.GetHandAcquisitionContext(priorityNow)!;
            Require(higherSearch.IlluminatedHint?.Center == higher.Center && higherSearch.IlluminationStartedAt == priorityNow &&
                    ReferenceEquals(higherSearch.ExpectedScene, watching.ExpectedScene),
                "Fresh higher-caption evidence failed to replace the lower search with a new settling interval.");
            var higherStart = higherSearch.IlluminationStartedAt;
            priorityNow += TimeSpan.FromMilliseconds(100);
            double lightLevel = priorityScene.SearchLightLevel;
            priorityScene.CompleteHandAcquisition(lowerSearch, [Caption("water-garden")], [], priorityNow,
                illuminatedPresence: true, illuminatedWhite: 254);
            Require(priorityScene.GetHandAcquisitionContext(priorityNow)?.IlluminationStartedAt == higherStart &&
                    priorityScene.SearchLightLevel == lightLevel,
                "An in-flight callback from the previous light replaced or dimmed the new search.");
            priorityNow += TimeSpan.FromMilliseconds(400);
            higher = Caption("football");
            var jitter = higher with { Center = new(higher.Center.X, higher.Center.Y + (reverseCamera ? 1 : -1)) };
            priorityScene.CompleteHandAcquisition(higherSearch, [Caption("crown-deed"), jitter, higher], [], priorityNow,
                illuminatedPresence: true);
            Require(priorityScene.GetHandAcquisitionContext(priorityNow) is { IlluminatedHint: { } kept } stable &&
                    kept.Center == higher.Center && stable.IlluminationStartedAt == higherStart &&
                    priorityScene.CurrentBoardScreen == BoardScreen.Menu &&
                    priorityScene.BlackjackState.Revision == revision && priorityScene.HoveredBoardButtons.Count == 0 &&
                    priorityScene.ActiveHandSpotlightCount == 0,
                "Lower disturbance or same-caption jitter moved the light or manufactured an input action.");

            SceneCompositor.HandAcquisitionContext Settle()
            {
                using (var drawing = priorityTarget.CreateDrawingSession())
                    priorityScene.Draw(drawing, size, size, preview: false, runningSlowly: false);
                priorityScene.GetHandAcquisitionContext(priorityNow);
                priorityNow += TimeSpan.FromMilliseconds(600);
                var context = priorityScene.GetHandAcquisitionContext(priorityNow)!;
                Require(context is { ObserveMotion: true, ExpectedScene: not null },
                    "Caption priority fixture did not settle its generated board.");
                return context;
            }

            HandAcquisitionHint Caption(string id)
            {
                int index = priorityScene.CurrentBoardButtons.ToList().FindIndex(button => button.Id == id);
                var bounds = watching.ExpectedScene!.BoardTriggerRegions![index];
                double x = size * (.1 + .8 * (priorityInset + (1 - 2 * priorityInset) * (bounds.X + bounds.Width / 2)));
                double y = size * (.1 + .8 * (priorityInset + (1 - 2 * priorityInset) * (bounds.Y + bounds.Height / 2)));
                var point = new PixelPoint(reverseCamera ? size - x : x, reverseCamera ? size - y : y);
                return new(new(Math.Clamp(point.X - 140, 0, size - 280), Math.Clamp(point.Y - 140, 0, size - 280), 280, 280),
                    point, 24, priorityNow, .1, ControlCoverage: .1, ControlTriggerCoverage: .1);
            }
        }

        void AssertInvalidRenewal(HandAcquisitionHint[] evidence, bool refreshObservation)
        {
            now += TimeSpan.FromMilliseconds(1000);
            var watching = scene.GetHandAcquisitionContext(now)!;
            Require(watching.ObserveMotion, "Renewal rejection fixture did not finish its quiet interval.");
            hint = hint with { ObservedAt = now, ControlCoverage = .10 };
            scene.CompleteHandAcquisition(watching, [hint], [], now);
            now += TimeSpan.FromMilliseconds(500);
            var searching = scene.GetHandAcquisitionContext(now)!;
            Require(searching.IlluminatedHint is not null, "Renewal rejection fixture did not start illumination.");
            if (refreshObservation) evidence = evidence.Select(item => item with { ObservedAt = now }).ToArray();
            scene.CompleteHandAcquisition(searching, evidence, [], now, illuminatedPresence: true);
            Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null } &&
                    !IsWhite(Draw(), center),
                "Absent, stale, misplaced, sub-7%, or non-finite evidence retained an active spotlight.");
        }

        PixelPoint BoardPoint(double u, double v) => new(1000 * (.1 + .8 * (inset + (1 - 2 * inset) * u)),
            1000 * (.1 + .8 * (inset + (1 - 2 * inset) * v)));
        byte[] Draw()
        {
            using (var ds = target.CreateDrawingSession()) scene.Draw(ds, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        static bool IsWhite(byte[] pixels, PixelPoint point)
        {
            int offset = ((int)point.Y * size + (int)point.X) * 4;
            return pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
