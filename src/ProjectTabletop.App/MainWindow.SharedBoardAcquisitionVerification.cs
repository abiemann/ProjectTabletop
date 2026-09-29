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
    // Compare real rendered boards with a stationary arriving finger occlusion.
    // This isolated scene cannot operate the user's camera or projector.
    private async Task<object> VerifySharedBoardAcquisitionAsync(string? board = null)
    {
        // A single-board run keeps native diagnostics within the pipe's bounded
        // timeout. Omitting the filter still verifies every board and control.
        BoardScreen? selectedBoard = null;
        if (board is not null)
        {
            if (!Enum.TryParse<BoardScreen>(board, ignoreCase: true, out var requestedBoard) ||
                !Enum.IsDefined(requestedBoard) || requestedBoard == BoardScreen.Media)
                throw new ArgumentException("Provide an interactive board name.");
            selectedBoard = requestedBoard;
        }
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var globeNow = now;
        var game = new BlackjackGame(41, new[] { 2, 6, 2, 10, 2, 2, 3, 4 }
            .Select(rank => new BlackjackCard(rank, BlackjackSuit.Clubs)));
        using var scene = new SceneCompositor(game, blackjackClock: () => now, globeClock: () => globeNow);
        await scene.EnsureGlobeResourcesAsync(CanvasDevice.GetSharedDevice());
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        var tested = new List<string>();
        scene.ShowBoardMenu(); VerifyButtons("Menu");
        scene.ShowHandTrackingTest(); VerifyButtons("Hand-Tracking");
        scene.ShowPhotoCopy(); VerifyButtons("Photo Copy");
        foreach (var (id, title) in new[] { ("paint", "Paint"), ("monopoly", "Monopoly"), ("globe", "Globe") })
        {
            // Acquisition runs on a synthetic presentation clock. Navigation
            // gestures use wall time and are covered by Interaction verification.
            switch (id)
            {
                case "paint": scene.ShowPaint(); break;
                case "monopoly": scene.ShowMonopoly(); break;
                case "globe": scene.ShowGlobe(); break;
            }
            Require(scene.CurrentBoardTitle == title, "The acquisition fixture could not open " + title + ".");
            scene.ClearHandTips();
            VerifyButtons(title);
            if (id == "globe")
            {
                globeNow = now;
                Require(scene.ActivateGlobeButton("globe-drawer-open"), "The Globe drawer fixture could not open.");
                globeNow += BoardSession.GlobeDrawerOpeningDuration;
                now = globeNow;
                scene.TickGlobe(globeNow);
                VerifyButtons("Globe drawer");
            }
        }
        scene.ShowBlackjack(); VerifyButtons("Blackjack betting");
        Require(scene.CurrentBoardButtons.Any(button => button.Id == "bj-reset" && button.Label == "Your Chips" &&
                    button.Bounds == new BoardRect(.742, .055, .198, .09)) &&
                scene.CurrentBoardButtons.Any(button => button.Id == "menu" && button.Label == "Exit" &&
                    button.Bounds == new BoardRect(.06, .055, .18, .09)),
            "Blackjack coverage omitted its top Your Chips plaque or Exit control.");
        Require(scene.ActivateBlackjackButton("bj-deal"), "The player-turn acquisition fixture could not deal.");
        now += TimeSpan.FromSeconds(3);
        Draw();
        VerifyButtons("Blackjack player");
        Require(scene.CurrentBoardButtons.Any(button => button.Id == "bj-hit"),
            "The fixture did not reach a player turn.");
        scene.SetBackground(null);
        Require(scene.CurrentBoardScreen == BoardScreen.Media && scene.GetHandAcquisitionContext(now) is null,
            "A media-only scene with no buttons started acquisition lighting.");
        scene.ShowBoardMenu(); Draw(); scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        var previous = scene.GetHandAcquisitionContext(now)!;
        scene.SetBoardSetup(true);
        Require(scene.GetHandAcquisitionContext(now) is null, "Calibration retained shared button assistance.");
        scene.SetBoardSetup(false);
        scene.ClearBoardMediaClip();
        scene.CompleteHandAcquisition(previous, [], [], now);
        Require(scene.GetHandAcquisitionContext(now) is null, "An uncalibrated scene retained button assistance.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The shared-board verification changed live hardware or navigation.");
        return new { passed = true, scope = selectedBoard?.ToString() ?? "All", stationaryArrivalWithoutMotionForEveryControl = true, controls = tested,
            generatedCaptionCorruptionNeedsTwoFreshFrames = true,
            noEmptyBoardCandidates = true, assistanceCannotSelect = true,
            sevenPercentCoverageRequiredAcrossBoards = true,
            buttonRegionsOnlyBeforeQualification = true, nativeFocusedModelSearchBeforeFallbackIllumination = true,
            noBlindButtonSweepOrWholeBoardMotionFallback = true,
            resetClearsForegroundQueryHistory = true,
            boundedPhotoCopyFieldAndUnrestrictedGestureTesterPreserved = true,
            rectangularPhotoCopyFieldCoveredWithoutStretching = true,
            paintControlTextCorruptionRequiredBeforeSpotlight = true,
            renderedLabelMasksContainLightAndDarkInkAcrossBoards = true,
            foregroundFixturesCoverActualLabels = true,
            untouchedLabelRejectsControlEdgeDisturbanceAcrossBoards = true,
            blackjackExitAndYourChipsIncluded = true, yourChipsAcquiresHeadingInsteadOfBalance = true,
            mediaAndCalibrationInactive = true,
            globeClosedAndOpenControlsCovered = tested.Count(label => label.StartsWith("Globe", StringComparison.Ordinal)) == 5,
            liveHardwareUnchanged = true };

        void VerifyButtons(string label)
        {
            if (selectedBoard is not null && scene.CurrentBoardScreen != selectedBoard) return;
            Draw();
            scene.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(600);
            Draw();
            var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null }, label + " did not enable acquisition.");
            Require(context!.StationarySearchCenters?.Length == scene.CurrentBoardButtons.Count &&
                    context.ExpectedScene!.BoardSearchRegions?.Count == scene.CurrentBoardButtons.Count,
                label + " omitted a button from its camera centers or foreground masks.");
            var screen = scene.CurrentBoardScreen;
            Require(context.RestrictAcquisitionToSearchRegions == (screen != BoardScreen.HandTracking) &&
                    context.AllowsSearchIllumination &&
                    (context.ContinuousSearchPolygon is not null) == (screen == BoardScreen.PhotoCopy),
                label + " did not preserve its intended acquisition policy.");
            Require(context.ExpectedScene!.BoardTriggerRegions?.Count == scene.CurrentBoardButtons.Count,
                label + " omitted its generated control-label trigger masks.");
            long gameRevision = scene.BlackjackState.Revision;
            var ids = scene.CurrentBoardButtons.Select(button => button.Id).ToArray();
            for (int index = 0; index < ids.Length; index++)
            {
                context = scene.GetHandAcquisitionContext(now)!;
                var button = scene.CurrentBoardButtons[index];
                var buttonCenter = CameraPoint(button.Bounds.X + button.Bounds.Width / 2,
                    button.Bounds.Y + button.Bounds.Height / 2);
                var trigger = context.ExpectedScene!.BoardTriggerRegions![index];
                var control = context.ExpectedScene.BoardSearchRegions![index];
                Require(trigger.Width > 0 && trigger.Height > 0 && trigger.X >= control.X && trigger.Y >= control.Y &&
                        trigger.X + trigger.Width <= control.X + control.Width + 1e-9 &&
                        trigger.Y + trigger.Height <= control.Y + control.Height + 1e-9 &&
                        ContainsGeneratedInk(context.ExpectedScene, trigger),
                    label + "/" + button.Label + " has no actual rendered letter ink in its label mask.");
                var center = CameraPoint(trigger.X + trigger.Width / 2, trigger.Y + trigger.Height / 2);
                var cameraCenter = context.StationarySearchCenters![index];
                Require(Math.Abs(cameraCenter.X - buttonCenter.X) < .1 && Math.Abs(cameraCenter.Y - buttonCenter.Y) < .1,
                    label + "/" + button.Label + " has the wrong camera crop center.");
                var empty = Draw();
                var emptyResult = new HandAcquisitionPresenceTracker().Update(size, size, size * 4, empty,
                    context.SearchPolygon, context.ExpectedScene, now, now);
                Require(emptyResult.BaselineReady && emptyResult.Hints.Count == 0,
                    label + " found an empty-table hand candidate: " + emptyResult.Reason);
                if (screen == BoardScreen.Blackjack && button.Id is "bj-reset" or "menu")
                {
                    Require(emptyResult.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                        { LabelIntact: true, Correlation: > .8 }, label + "/" + button.Label + " has no actual generated glyph template.");
                    if (button.Id == "bj-reset")
                        Require(trigger.X >= .752 && trigger.X + trigger.Width <= .930 &&
                                trigger.Y >= .065 && trigger.Y + trigger.Height <= .095 && trigger.Height < .04,
                            "Your Chips acquisition watched the balance/body instead of its small heading.");
                }
                var emptyQuery = Query(empty, context);
                Require(emptyQuery.Hints.Count == 0 && emptyQuery.LightingHints.Count == 0,
                    label + "/" + button.Label + " generated a candidate on its empty rendered board.");
                if (context.ContinuousSearchPolygon is null)
                    Require(emptyQuery.SearchRegions.Count == 0,
                        label + "/" + button.Label + " blindly searched an undisturbed button.");
                else
                {
                    Require(emptyQuery.SearchRegions.Count is > 0 and <= 2 &&
                            emptyQuery.SearchRegions.All(region => region.Width == region.Height &&
                                region.X >= 0 && region.Y >= 0 &&
                                region.X + region.Width <= size && region.Y + region.Height <= size),
                        "Photo Copy lost its bounded native square object-field search.");
                    foreach (var field in new PixelPoint[][]
                    {
                        [new(100, 300), new(900, 300), new(900, 500), new(100, 500)],
                        [new(300, 100), new(500, 100), new(500, 900), new(300, 900)],
                        [new(288, 146), new(854, 712), new(712, 854), new(146, 288)]
                    })
                    {
                        var fieldQuery = Query(empty, context with { ContinuousSearchPolygon = field });
                        Require(fieldQuery.SearchRegions.Count is > 0 and <= 2 &&
                                fieldQuery.SearchRegions.All(region => region.Width == region.Height) &&
                                field.All(point => fieldQuery.SearchRegions.Any(region =>
                                    point.X >= region.X && point.X <= region.X + region.Width &&
                                    point.Y >= region.Y && point.Y <= region.Y + region.Height)) &&
                                fieldQuery.SearchRegions.Any(region =>
                                    field.Average(point => point.X) >= region.X &&
                                    field.Average(point => point.X) <= region.X + region.Width &&
                                    field.Average(point => point.Y) >= region.Y &&
                                    field.Average(point => point.Y) <= region.Y + region.Height),
                            "A wide or tall Photo Copy field left its middle unsearched or stretched a model crop.");
                    }
                }
                // A large, stationary off-button occlusion must not become a
                // control candidate or a whole-board motion fallback search.
                var outside = (byte[])empty.Clone();
                var outsideCenter = CameraPoint(.5, .2);
                Fill(outside, (int)outsideCenter.X - 30, (int)outsideCenter.Y - 30, 60, 60, 15, 20, 225);
                var outsideQuery = Query(outside, context);
                Require(outsideQuery.LightingHints.Count == 0 &&
                        outsideQuery.SearchRegions.SequenceEqual(emptyQuery.SearchRegions),
                    label + "/" + button.Label + " searched an unrelated board disturbance.");
                var occupied = (byte[])empty.Clone();
                // A lone Back button has to work when the arriving hand covers
                // most of its interior. The separate static header/panel supplies
                // the appearance reference; the occluded button cannot train it.
                bool loneBack = ids.Length == 1;
                int height = (int)Math.Clamp(button.Bounds.Height * size * .93 * (1 - inset) *
                    (loneBack ? .95 : .75), 34, 95);
                // Keep this geometric fixture visibly distinct even from the
                // gold Deal button. Actual skin/projector contrast is evaluated
                // in recorded camera captures, not asserted by a painted patch.
                bool globeArrow = button.Id is "globe-drawer-open" or "globe-drawer-close";
                if (globeArrow)
                {
                    // Obstruct the arrow and >7% of its glass while retaining a
                    // clean photometric reference around the simulated fingers.
                    double scale = size * .93 * (1 - inset);
                    double coveredHeight = Math.Max(trigger.Height, button.Bounds.Height * .42);
                    double searchedArea = (button.Bounds.Width - .024) * (button.Bounds.Height - .024);
                    double coveredWidth = Math.Min(Math.Max(trigger.Width + .004, button.Bounds.Width * .40),
                        .36 * searchedArea / coveredHeight);
                    int span = (int)Math.Ceiling(coveredWidth * scale);
                    int stripeHeight = (int)Math.Ceiling(coveredHeight * scale);
                    int stripeWidth = Math.Max(2, (span - 9) / 4);
                    for (int finger = 0; finger < 4; finger++)
                        Fill(occupied, (int)center.X - span / 2 + finger * (stripeWidth + 3),
                            (int)center.Y - stripeHeight / 2, stripeWidth, stripeHeight, 75, 95, 185);
                }
                else
                    for (int finger = 0; finger < 4; finger++)
                        Fill(occupied, (int)center.X - (loneBack ? 107 : 38) + finger * (loneBack ? 55 : 20),
                            (int)center.Y - height / 2, loneBack ? 49 : 18, height, 75, 95, 185);
                var occupiedTracker = new HandAcquisitionPresenceTracker();
                var firstArrival = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                    context, occupiedTracker, now);
                Require(firstArrival.LightingHints.Count == 0 &&
                        firstArrival.SearchRegions.SequenceEqual(emptyQuery.SearchRegions),
                    label + "/" + button.Label + " accepted a single unconfirmed generated-label obstruction.");
                now += TimeSpan.FromMilliseconds(125);
                var pendingQuery = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                    context, occupiedTracker, now);
                var presence = pendingQuery.Presence!;
                if (!presence.Hints.Any(hint => Math.Abs(hint.Center.X - center.X) < 65 &&
                        Math.Abs(hint.Center.Y - center.Y) < 65 &&
                        hint.ControlCoverage is double coverage && double.IsFinite(coverage) && coverage >= .07))
                {
                    string directory = Path.Combine(_appDataDirectory, "SharedAcquisitionFailures", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, "empty.bgra"), empty);
                    File.WriteAllBytes(Path.Combine(directory, "occupied.bgra"), occupied);
                    File.WriteAllBytes(Path.Combine(directory, "expected.bgra"), context.ExpectedScene!.Bgra);
                    File.WriteAllText(Path.Combine(directory, "failure.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        width = size, height = size, stride = size * 4, board = label, button,
                        center, now, context.SearchPolygon,
                        expected = new { context.ExpectedScene.Width, context.ExpectedScene.Height,
                            context.ExpectedScene.CameraToBoard, context.ExpectedScene.BoardSearchRegions,
                            context.ExpectedScene.BoardReferenceRegions, context.ExpectedScene.BoardTriggerRegions },
                        presence
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    throw new InvalidOperationException(label + "/" + button.Label +
                        " missed stationary fingers after two fresh frames: " + presence.Reason + ". Diagnostic: " + directory);
                }
                var measured = presence.Hints.First(hint => Math.Abs(hint.Center.X - center.X) < 65 &&
                    Math.Abs(hint.Center.Y - center.Y) < 65 && hint.ControlCoverage is double coverage &&
                    double.IsFinite(coverage) && coverage >= .07);
                Require(pendingQuery.LightingHints.Any(hint => hint.Center == measured.Center),
                    label + "/" + button.Label + " did not pass measured control evidence to illumination.");
                Require(pendingQuery.SearchRegions.Count is > 0 and <= 2 &&
                        pendingQuery.SearchRegions.Contains(AcquisitionSearchBounds(measured, size, size)) &&
                        pendingQuery.SearchRegions.All(crop => crop.Width == crop.Height && crop.X >= 0 && crop.Y >= 0 &&
                            crop.X + crop.Width <= size && crop.Y + crop.Height <= size) && context.IlluminatedHint is null,
                    label + "/" + button.Label + " did not try its qualified native camera crop before fallback illumination.");
                Require(measured.ControlTriggerCoverage is double textCoverage && textCoverage >= .07,
                    label + "/" + button.Label + " did not measure at least 7% of its actual control-label obstruction.");
                if (screen == BoardScreen.Blackjack && button.Id is "bj-reset" or "menu")
                    Require(presence.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                        { ShapeCorrupted: true, ConfirmationFrames: >= 2 },
                        label + "/" + button.Label + " qualified without confirming stationary interference with the rendered heading.");
                var edge = LargestUnlabelledPart(control, trigger);
                if (edge.Width * edge.Height >= control.Width * control.Height * .07)
                {
                    var edgeOnly = (byte[])empty.Clone();
                    var edgeTopLeft = CameraPoint(edge.X, edge.Y);
                    var edgeBottomRight = CameraPoint(edge.X + edge.Width, edge.Y + edge.Height);
                    Fill(edgeOnly, (int)edgeTopLeft.X, (int)edgeTopLeft.Y,
                        (int)(edgeBottomRight.X - edgeTopLeft.X),
                        (int)(edgeBottomRight.Y - edgeTopLeft.Y), 75, 95, 185);
                    var edgeTracker = new HandAcquisitionPresenceTracker();
                    var edgeQuery = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, edgeOnly, now),
                        context, edgeTracker, now);
                    now += TimeSpan.FromMilliseconds(125);
                    edgeQuery = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, edgeOnly, now),
                        context, edgeTracker, now);
                    Require(edgeQuery.Hints.Count == 0 && edgeQuery.LightingHints.Count == 0 &&
                            edgeQuery.SearchRegions.SequenceEqual(emptyQuery.SearchRegions),
                        label + "/" + button.Label + " illuminated or searched a control edge while its label remained intact.");
                }
                // The edge-only comparison advanced the fixture clock. Refresh
                // measured obstruction before the scene accepts its exact frame.
                if (edge.Width * edge.Height >= control.Width * control.Height * .07)
                {
                    pendingQuery = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                        context, occupiedTracker, now);
                    Require(pendingQuery.Presence is { Hints.Count: > 0 },
                        label + "/" + button.Label + " lost a confirmed stationary text obstruction.");
                    presence = pendingQuery.Presence!;
                    measured = presence.Hints.First(hint => Math.Abs(hint.Center.X - center.X) < 65 &&
                        Math.Abs(hint.Center.Y - center.Y) < 65 && hint.ControlCoverage is >= .07);
                }
                var resetTracker = new HandAcquisitionPresenceTracker();
                CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                    context, resetTracker, now);
                resetTracker.Reset();
                var afterReset = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, empty, now),
                    context with { Revision = context.Revision + 1 }, resetTracker, now);
                Require(afterReset.Hints.Count == 0 && afterReset.LightingHints.Count == 0 &&
                        afterReset.SearchRegions.SequenceEqual(emptyQuery.SearchRegions),
                    label + "/" + button.Label + " retained foreground query evidence after a context/camera reset.");
                foreach (double? coverage in new double?[] { null, .069999 })
                {
                    scene.CompleteHandAcquisition(context, [measured with { ControlCoverage = coverage }], [], now);
                    Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                        label + "/" + button.Label + " accepted motion-only or sub-7% foreground evidence.");
                }
                foreach (double? coverage in new double?[] { null, .069999, double.NaN,
                             double.PositiveInfinity, double.NegativeInfinity })
                {
                    scene.CompleteHandAcquisition(context,
                        [measured with { ControlTriggerCoverage = coverage }], [], now);
                    Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                        label + "/" + button.Label + " accepted missing, sub-7%, or non-finite label obstruction.");
                }
                scene.CompleteHandAcquisition(context, presence.Hints, [], now);
                var lit = scene.GetHandAcquisitionContext(now)!;
                if (button.HoldToRepeat)
                    // Hold buttons act on caption evidence alone; a light would erase it.
                    Require(lit.IlluminatedHint is null && CountWhite(Draw(), center) <= CountWhite(empty, center) + 50,
                        label + "/" + button.Label + " lit a hold-to-repeat button.");
                else
                {
                    Require(lit.IlluminatedHint is not null && CountWhite(Draw(), center) > CountWhite(empty, center) + 800,
                        label + "/" + button.Label + " did not illuminate its control.");
                    var focusedQuery = Query(occupied, lit);
                    Require(focusedQuery.SearchRegions.Count is > 0 and <= 2 &&
                            focusedQuery.SearchRegions.Contains(AcquisitionSearchBounds(lit.IlluminatedHint!, size, size)),
                        label + "/" + button.Label + " did not focus inference on its illuminated point of interest.");
                }
                Require(scene.CurrentBoardScreen == screen && scene.BlackjackState.Revision == gameRevision &&
                        scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(ids) &&
                        scene.ActiveHandSpotlightCount == 0 && scene.HoveredBoardButtons.Count == 0 &&
                        !scene.TryTakePhotoCopyCaptureRequest(now, out _) && !scene.TryTakePhotoCopyMemorySaveRequest(now, out _),
                    "A foreground hint executed a board action on " + label + ".");
                tested.Add(label + "/" + button.Label);
                scene.CompleteHandAcquisition(lit, presence.Hints, [], now, illuminatedPresence: false);
                now += TimeSpan.FromMilliseconds(1000);
            }
        }
        PixelPoint CameraPoint(double u, double v) => new(size * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            size * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        HandAcquisitionQuery Query(byte[] pixels, SceneCompositor.HandAcquisitionContext context) =>
            CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, pixels, now), context,
                new HandAcquisitionPresenceTracker(), now);
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, size, size, false, false);
            return target.GetPixelBytes();
        }
        static void Fill(byte[] pixels, int left, int top, int width, int height, byte b, byte g, byte r)
        {
            for (int y = Math.Max(0, top); y < Math.Min(size, top + height); y++)
            for (int x = Math.Max(0, left); x < Math.Min(size, left + width); x++)
            {
                int offset = (y * size + x) * 4;
                pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
            }
        }
        static int CountWhite(byte[] pixels, PixelPoint center)
        {
            int count = 0;
            for (int y = (int)center.Y - 20; y <= (int)center.Y + 20; y++)
            for (int x = (int)center.X - 25; x <= (int)center.X + 25; x++)
            {
                int offset = (y * size + x) * 4;
                if (pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245) count++;
            }
            return count;
        }
        static HandTrackingBounds LargestUnlabelledPart(HandTrackingBounds control, HandTrackingBounds text)
        {
            const double gap = .004;
            HandTrackingBounds[] regions =
            [
                new(control.X, control.Y, Math.Max(0, text.X - gap - control.X), control.Height),
                new(text.X + text.Width + gap, control.Y,
                    Math.Max(0, control.X + control.Width - text.X - text.Width - gap), control.Height),
                new(control.X, control.Y, control.Width, Math.Max(0, text.Y - gap - control.Y)),
                new(control.X, text.Y + text.Height + gap, control.Width,
                    Math.Max(0, control.Y + control.Height - text.Y - text.Height - gap))
            ];
            return regions.MaxBy(region => region.Width * region.Height);
        }
        static bool ContainsGeneratedInk(HandAcquisitionSceneImage expected, HandTrackingBounds text)
        {
            int left = (int)Math.Ceiling(text.X * expected.Width), top = (int)Math.Ceiling(text.Y * expected.Height);
            int right = (int)Math.Floor((text.X + text.Width) * expected.Width);
            int bottom = (int)Math.Floor((text.Y + text.Height) * expected.Height);
            var values = new List<double>();
            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                int offset = (y * expected.Width + x) * 4;
                values.Add(expected.Bgra[offset] * .114 + expected.Bgra[offset + 1] * .587 + expected.Bgra[offset + 2] * .299);
            }
            if (values.Count < 20) return false;
            values.Sort();
            double median = values[values.Count / 2];
            int ink = values.Count(value => Math.Abs(value - median) > 24);
            // This detects either bright letters on a dark pane or dark letters
            // on a gold control; a flat control-color patch cannot satisfy it.
            return ink >= 20 && ink < values.Count * .75;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
