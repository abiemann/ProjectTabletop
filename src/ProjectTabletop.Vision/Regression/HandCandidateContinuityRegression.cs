using System.Text.Json;
using ProjectTabletop.Vision;

internal static class HandCandidateContinuityRegression
{
    public static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hand-candidate-continuity.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        int frames = 0, largeMiddleTipErrors = 0;
        HandDetection? exampleTracked = null, exampleSearch = null;
        foreach (var frame in document.RootElement.EnumerateArray())
        {
            int sequence = frame.GetProperty("sequence").GetInt32();
            var candidates = frame.GetProperty("candidates").EnumerateArray().Select(ReadCandidate).ToArray();
            Require(candidates.Length == 2, $"Recorded sequence {sequence} lost a candidate.");
            var tracked = candidates.Single(candidate => candidate.PreviousIou is not null);
            var search = candidates.Single(candidate => candidate.PreviousIou is null);
            exampleTracked ??= tracked.Hand;
            exampleSearch ??= search.Hand;
            string original = JsonSerializer.Serialize(candidates);
            Require(search.Hand.Confidence > tracked.Hand.Confidence,
                $"Recorded sequence {sequence} no longer reproduces the old confidence-order takeover.");
            if (Distance(tracked.Hand.Landmarks[12], search.Hand.Landmarks[12]) > 35) largeMiddleTipErrors++;

            // Both candidates are inferences from this frame, not previous poses.
            // Reversing input order also verifies this is not a stable-sort accident.
            foreach (var order in new[] { candidates, candidates.Reverse().ToArray() })
            {
                var decisions = new List<Decision>();
                var result = Select(order, decisions);
                Require(result.Count == 1 && ReferenceEquals(result[0], tracked.Hand),
                    $"Sequence {sequence}: a near-tied search fit replaced the reliable fresh tracked ROI.");
                Require(decisions.Any(decision => ReferenceEquals(decision.Hand, search.Hand) &&
                    decision.Reason == "tracked-continuity" && ReferenceEquals(decision.Suppressor, tracked.Hand)),
                    $"Sequence {sequence}: suppression did not report its real winning ROI.");
            }
            Require(JsonSerializer.Serialize(candidates) == original,
                "Selection altered original landmark, confidence, handedness or continuity evidence.");
            frames++;
        }
        Require(frames == 8 && largeMiddleTipErrors == 6,
            "The fixture must cover all eight observed takeovers, including six large finger-collapse pulses.");

        HandDetection roi = exampleTracked!;
        HandDetection tile = exampleSearch!;
        CheckPolicyBoundaries(roi, tile);
        CheckIndependentHandsAndRanking(roi, tile);
        CheckFreshnessAndRecovery(roi, tile);
        Console.WriteLine($"Hand candidate continuity: {frames} recorded near-tie takeovers, " +
            $"{largeMiddleTipErrors} large fingertip-collapse pulses prevented; strong-search recovery, " +
            "weak/low-IoU tracks, second hands, true-score ordering, fresh references and truthful decisions passed.");
    }

    private static void CheckPolicyBoundaries(HandDetection roi, HandDetection tile)
    {
        CheckWinner(roi with { Confidence = 0.95 }, 0.95, tile with { Confidence = 0.96 }, trackedWins: true,
            "A search fit at the inclusive one-percentage-point margin replaced a reliable ROI.");
        CheckWinner(roi with { Confidence = 0.95 }, 0.95, tile with { Confidence = 0.960001 }, trackedWins: false,
            "A clearly stronger search fit could not replace tracking.");
        CheckWinner(roi with { Confidence = 0.90 }, 0.65, tile with { Confidence = 0.905 }, trackedWins: true,
            "The inclusive confidence/previous-IoU thresholds did not preserve a reliable ROI.");
        CheckWinner(roi with { Confidence = 0.8999 }, 0.95, tile with { Confidence = 0.905 }, trackedWins: false,
            "A weak tracked fit overrode a stronger search fit.");
        CheckWinner(roi with { Confidence = 0.95 }, 0.6499, tile with { Confidence = 0.955 }, trackedWins: false,
            "A tracked fit with poor previous overlap overrode search recovery.");
        CheckWinner(roi with { Confidence = 0.95 }, 0.95, tile with { Confidence = 0.90 }, trackedWins: true,
            "A weaker search fit replaced the highest-scoring tracked inference.");
        var equalRoi = roi with { Confidence = 0.95 };
        var equalTile = tile with { Confidence = 0.95 };
        var tie = Select([new(equalTile, null), new(equalRoi, 0.95)]);
        Require(tie.Count == 1 && ReferenceEquals(tie[0], equalRoi),
            "An exact confidence tie depends on search enumeration order.");

        // Competing tracked fits retain their true scores: continuity only
        // arbitrates current tracked crops against full-frame/tiled search.
        var trackedAlternative = tile with { Confidence = 0.99 };
        var twoTracks = Select([new(equalRoi, 0.95), new(trackedAlternative, 0.95)]);
        Require(twoTracks.Count == 1 && ReferenceEquals(twoTracks[0], trackedAlternative),
            "Continuity changed the ranking of two tracked inferences.");
    }

    private static void CheckIndependentHandsAndRanking(HandDetection roi, HandDetection tile)
    {
        var secondHand = Move(roi, -700, -350) with { Confidence = 0.995 };
        var result = Select([new(tile, null), new(secondHand, null), new(roi, 0.98)]);
        Require(result.Count == 2 && ReferenceEquals(result[0], secondHand) && ReferenceEquals(result[1], roi),
            "Continuity hid an arriving second hand or promoted the tracked fit above its true score.");

        var strongSearch = tile with { Confidence = 1.0 };
        var weakerRoi = roi with { Confidence = 0.98 };
        result = Select([new(weakerRoi, 0.98), new(secondHand, null), new(strongSearch, null)]);
        Require(result.Count == 2 && ReferenceEquals(result[0], strongSearch) && ReferenceEquals(result[1], secondHand),
            "A stronger overlapping search fit or unrelated-hand score ordering changed.");

        var thirdHand = Move(roi, 700, 350) with { Confidence = 0.999 };
        result = Select([new(roi, 0.98), new(tile, null), new(secondHand, null), new(thirdHand, null)]);
        Require(result.Count == 2 && ReferenceEquals(result[0], thirdHand) && ReferenceEquals(result[1], secondHand),
            "Continuity bypassed the two-hand limit or displaced higher-scoring unrelated hands.");

        var nonOverlapping = Move(tile, 1000, 0);
        result = Select([new(roi, 0.98), new(nonOverlapping, null)]);
        Require(result.Count == 2 && ReferenceEquals(result[0], nonOverlapping) && ReferenceEquals(result[1], roi),
            "A nonoverlapping near-tie was incorrectly classified as the same hand.");

        // A clearly better search proposal supersedes both the ROI and the
        // near-tied tile, and diagnostics must name that selected search winner.
        var decisions = new List<Decision>();
        var nearSearch = tile with { Confidence = 0.981 };
        result = Select([new(weakerRoi, 0.98), new(nearSearch, null), new(strongSearch, null)], decisions);
        Require(result.Count == 1 && ReferenceEquals(result[0], strongSearch) &&
            decisions.Where(decision => decision.Suppressor is not null).All(decision =>
                decision.Reason == "overlap" && ReferenceEquals(decision.Suppressor, strongSearch)),
            "Suppression referred to a preferred ROI that was not actually selected.");
    }

    private static void CheckFreshnessAndRecovery(HandDetection roi, HandDetection tile)
    {
        var oldEvidence = new Dictionary<HandDetection, double>(ReferenceEqualityComparer.Instance) { [roi] = 0.99 };
        Require(HandCandidateSelector.Select([], oldEvidence).Count == 0,
            "Missing inferences resurrected a previous-frame hand.");
        var recovered = HandCandidateSelector.Select([tile], oldEvidence);
        Require(recovered.Count == 1 && ReferenceEquals(recovered[0], tile),
            "A failed current tracked inference prevented search reacquisition.");

        // Even identical values do not make a new object the old tracked ROI.
        // Production evidence uses reference identity for this Detect call.
        var sameValuesNewObject = roi with { };
        var result = HandCandidateSelector.Select([sameValuesNewObject, tile], oldEvidence);
        Require(result.Count == 1 && ReferenceEquals(result[0], tile),
            "Previous-frame evidence was attached to a different candidate by value equality.");

        var firstSearch = roi with { Confidence = 0.80 };
        var secondSearch = tile with { Confidence = 0.801 };
        result = Select([new(firstSearch, null), new(secondSearch, null)]);
        Require(result.Count == 1 && ReferenceEquals(result[0], secondSearch),
            "Initial acquisition no longer selected the best fresh search fit.");
    }

    private static void CheckWinner(HandDetection tracked, double previousIou, HandDetection search,
        bool trackedWins, string message)
    {
        var decisions = new List<Decision>();
        var result = Select([new(search, null), new(tracked, previousIou)], decisions);
        HandDetection winner = trackedWins ? tracked : search;
        Require(result.Count == 1 && ReferenceEquals(result[0], winner), message);
        Require(decisions.Count == 2 && decisions.Count(decision => decision.Reason == "selected") == 1 &&
            decisions.Single(decision => decision.Suppressor is not null).Suppressor == winner,
            "Selection diagnostics lost a fresh candidate or the chosen suppressor.");
    }

    private static IReadOnlyList<HandDetection> Select(IReadOnlyList<Candidate> candidates,
        List<Decision>? decisions = null)
    {
        var evidence = new Dictionary<HandDetection, double>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
            if (candidate.PreviousIou is { } previousIou) evidence.Add(candidate.Hand, previousIou);
        return HandCandidateSelector.Select(candidates.Select(candidate => candidate.Hand).ToArray(), evidence,
            decisions is null ? null : (hand, reason, suppressor) => decisions.Add(new(hand, reason, suppressor)));
    }

    private static Candidate ReadCandidate(JsonElement element)
    {
        var hand = new HandDetection(element.GetProperty("landmarks").EnumerateArray()
            .Select(point => new PixelPoint(point[0].GetDouble(), point[1].GetDouble())).ToArray(),
            element.GetProperty("confidence").GetDouble(), element.GetProperty("rightHandProbability").GetDouble());
        var previousIou = element.GetProperty("previousBoundsIou");
        bool tracked = element.GetProperty("source").GetString() == "tracked-roi";
        Require(tracked == (previousIou.ValueKind == JsonValueKind.Number), "Recorded source/IoU evidence disagrees.");
        return new(hand, tracked ? previousIou.GetDouble() : null);
    }

    private static HandDetection Move(HandDetection hand, double dx, double dy) => hand with
    {
        Landmarks = hand.Landmarks.Select(point => new PixelPoint(point.X + dx, point.Y + dy)).ToArray()
    };

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed record Candidate(HandDetection Hand, double? PreviousIou);
    private sealed record Decision(HandDetection Hand, string Reason, HandDetection? Suppressor);
}
