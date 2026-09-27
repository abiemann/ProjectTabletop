using ProjectTabletop.Interaction;

internal static class BoardFingerSelectionRegression
{
    public static void Run()
    {
        CheckTransitionAndRearm();
        CheckTargetAnchor();
        CheckMissingAndFreshness();
        CheckIndependentHandsAndPinch();
        CheckNavigationResetAndIdentity();
        CheckBlackjackActions();
        Console.WriteLine("Finger-selection verification passed: together-to-sideways-index transition, no timed auto-action, " +
            "middle-tip target anchoring, repeated HIT rearm, fresh confirmation, dropout, identity, pinch and screen/dealer barriers.");
    }

    private static void CheckTransitionAndRearm()
    {
        var board = PhotoCopy();
        var hand = Together(Button(board, "capture-again"));
        long revision = board.Revision;
        Require(At(board, 100, hand) is null && Feedback(board).Stage == BoardFingerSelectionStage.Arming,
            "Together did not begin target arming.");
        At(board, 150, hand);
        Require(Feedback(board) is { Stage: BoardFingerSelectionStage.Arming, Progress: .5 },
            "Grouped confirmation did not use camera evidence.");
        At(board, 200, hand);
        Require(Feedback(board).Stage == BoardFingerSelectionStage.Armed, "Together did not arm after 100 ms.");
        for (int at = 300; at <= 2100; at += 100)
            Require(At(board, at, hand) is null && board.Revision == revision,
                "Holding fingers together still performed a timed automatic action.");
        var apart = Apart(hand);
        Require(At(board, 2200, apart) is null && Feedback(board).Stage == BoardFingerSelectionStage.Separating,
            "One separated observation executed without confirmation.");
        Require(At(board, 2280, apart)?.ButtonId == "capture-again" && board.Revision == revision + 1,
            "Confirmed sideways separation did not select the armed target.");
        for (int at = 2380; at <= 3580; at += 100)
            Require(At(board, at, apart) is null && Feedback(board).Stage == BoardFingerSelectionStage.Selected,
                "Remaining apart repeated the selected action.");
        Require(Select(board, hand, 3700)?.ButtonId == "capture-again" && board.Revision == revision + 2,
            "Rejoining and separating did not rearm the same target without folding or moving away.");
        At(board, 4100, hand); // A single erroneous together frame must not rearm.
        Require(At(board, 4180, apart) is null && At(board, 4260, apart) is null,
            "One together-looking estimate rearmed an already selected hand.");

        board = PhotoCopy();
        for (int at = 100; at <= 1600; at += 100)
            Require(At(board, at, apart) is null && board.FingerSelectionFeedback.Count == 0,
                "A hand first observed separated selected a button without first grouping.");
        var premature = PhotoCopy();
        At(premature, 100, hand);
        Require(At(premature, 150, apart) is null && At(premature, 250, apart) is null,
            "Opening before grouped confirmation selected a button.");
    }

    private static void CheckTargetAnchor()
    {
        var board = PhotoCopy();
        var button = Button(board, "capture-again");
        var edge = Together(button) with { FingerAim = new(.935, .1) };
        At(board, 100, edge); At(board, 200, edge);
        var opened = Apart(edge) with { FingerAim = new(.945, .1) };
        At(board, 300, opened);
        Require(board.HoveredButtonIds.SequenceEqual([button.Id]),
            "A small opening motion lost the highlight at the grouped target.");
        Require(At(board, 380, opened)?.ButtonId == button.Id,
            "A small opening motion outside the visual edge lost the bounded target anchor.");

        board = new BoardSession(new BlackjackGame(initialShoe: Cards(8, 6, 8, 10)));
        board.ShowBlackjack(Time(0)); board.ActivateButton("bj-deal", Time(10));
        var hit = Together(Button(board, "bj-hit")) with { FingerAim = new(.273, .82) };
        At(board, 100, hit); At(board, 200, hit);
        var neighboringStand = Apart(hit) with { FingerAim = new(.300, .82) };
        Require(At(board, 300, neighboringStand) is null && At(board, 380, neighboringStand) is null &&
            board.BlackjackState.Phase == BlackjackPhase.PlayerTurn && board.BlackjackState.Hands[0].Cards.Count == 2,
            "Opening across a neighboring target fired HIT or transferred its anchor to STAND.");

        board = PhotoCopy(); var hand = Together(Button(board, "capture-again"));
        At(board, 100, hand); At(board, 200, hand);
        var moved = Apart(hand) with { FingerAim = new(.85, .1075) };
        Require(At(board, 300, moved) is null && At(board, 380, moved) is null,
            "Moving over .04 board units while opening retained the old anchor.");
        var other = Together(Button(board, "menu"));
        At(board, 500, hand); At(board, 600, hand); At(board, 650, other);
        Require(Feedback(board).Stage == BoardFingerSelectionStage.Arming,
            "Changing the grouped target inherited another button's ready state.");
        Require(At(board, 700, Apart(other)) is null && At(board, 780, Apart(other)) is null,
            "A newly aimed target selected without its own grouped confirmation.");
    }

    private static void CheckMissingAndFreshness()
    {
        var board = PhotoCopy(); var hand = Together(Button(board, "capture-again")); var apart = Apart(hand);
        At(board, 100, hand); At(board, 200, hand); At(board, 300, apart); At(board, 340);
        At(board, 400, apart);
        Require(Feedback(board) is { Stage: BoardFingerSelectionStage.Separating, Progress: 0 },
            "A missing interval counted toward separation confirmation.");
        Require(At(board, 480, apart) is not null, "A short missing interval prevented a fresh confirmed separation.");

        board = PhotoCopy(); hand = Together(Button(board, "capture-again")); apart = Apart(hand);
        At(board, 100, hand); At(board, 200, hand);
        At(board, 250, hand with { FingersTogether = false }); // A natural transition between thresholds.
        At(board, 300, apart);
        Require(At(board, 380, apart) is not null, "A brief neutral transition erased a ready target.");

        board = PhotoCopy(); hand = Together(Button(board, "capture-again")); apart = Apart(hand);
        At(board, 100, hand); At(board, 200, hand); At(board, 300);
        Require(At(board, 600, apart) is null && At(board, 680, apart) is null,
            "An armed target survived more than 350 ms without observations.");
        At(board, 800, hand); At(board, 900, hand);
        for (int at = 1000; at <= 1300; at += 100) At(board, at, hand with { FourFingersExtended = false });
        Require(At(board, 1400, apart) is null && At(board, 1480, apart) is null,
            "Repeated unrecognized poses kept an old ready target alive.");

        foreach ((int source, int now) in new[] { (400, 751), (500, 490), (300, 380), (250, 380), (250, 250) })
        {
            board = PhotoCopy(); hand = Together(Button(board, "capture-again")); apart = Apart(hand);
            At(board, 100, hand); At(board, 200, hand); At(board, 300, apart);
            Require(board.Update([apart], Time(source), Time(now)) is null && board.FingerSelectionFeedback.Count == 0,
                "Stale, future, duplicate, out-of-order or reversed-clock input completed separation.");
        }
        foreach (var invalid in new BoardAim?[] { null, new(double.NaN, .1), new(.8, double.PositiveInfinity), new(1.1, .1) })
        {
            board = PhotoCopy(); hand = Together(Button(board, "capture-again")) with { FingerAim = invalid };
            Require(Select(board, hand, 100) is null, "An invalid middle aim activated a target.");
        }
        board = PhotoCopy(); hand = Together(Button(board, "capture-again")) with { TrackingId = 0 };
        Require(Select(board, hand, 100) is null, "A hand without a stable identity activated finger selection.");
    }

    private static void CheckIndependentHandsAndPinch()
    {
        var board = PhotoCopy();
        var first = Together(Button(board, "capture-again"), 1);
        var second = Together(Button(board, "menu"), 2);
        At(board, 100, first); At(board, 200, second, first);
        At(board, 300, Apart(first), second);
        Require(At(board, 380, Apart(second), Apart(first))?.ButtonId == "capture-again",
            "Hand ordering moved target or confirmation evidence to another hand.");
        Require(At(board, 460, Apart(second)) is null && At(board, 540, Apart(second)) is null,
            "Another hand's old armed gesture selected after the first action.");
        Require(Select(board, second, 600)?.ButtonId == "menu", "The independent hand could not make a fresh selection.");

        board = PhotoCopy(); first = Together(Button(board, "capture-again"));
        At(board, 100, first); At(board, 200, first); At(board, 300, Apart(first));
        var back = Center(Button(board, "menu"));
        var pinch = new BoardHandSample(back.U, back.V, Time(1380), 20);
        long revision = board.Revision;
        Require(At(board, 380, Apart(first), pinch)?.ButtonId == "menu" && board.Revision == revision + 1,
            "A simultaneous pinch failed to take precedence or performed two actions.");
        board = PhotoCopy(); first = Together(Button(board, "capture-again"));
        At(board, 100, first); At(board, 200, first); At(board, 300, Apart(first));
        Require(At(board, 380, Apart(first), new(double.NaN, 0, Time(1380), 30))?.ButtonId == "capture-again",
            "An off-target pinch prevented a valid finger selection.");
        Require(At(board, 460, new BoardHandSample(back.U, back.V, Time(1380), 30)) is null,
            "An off-target pinch on the finger-selection frame was not consumed.");
    }

    private static void CheckNavigationResetAndIdentity()
    {
        foreach (Action<BoardSession> invalidate in new Action<BoardSession>[]
        {
            board => board.ResetInput(Time(250)),
            board => board.ShowPhotoCopy(Time(250)),
            board => board.ActivateButton("capture-again", Time(250))
        })
        {
            var board = PhotoCopy(); var hand = Together(Button(board, "capture-again"));
            At(board, 100, hand); At(board, 200, hand); invalidate(board);
            Require(At(board, 300, Apart(hand)) is null && At(board, 380, Apart(hand)) is null,
                "A pre-reset, pre-navigation or pre-action grouped pose selected afterward.");
            Require(Select(board, hand, 500) is not null, "Invalidation prevented a fresh together-to-apart gesture.");
        }
        var identityBoard = PhotoCopy(); var oldHand = Together(Button(identityBoard, "capture-again"), 1);
        At(identityBoard, 100, oldHand); At(identityBoard, 200, oldHand);
        var newHand = oldHand with { TrackingId = 2 };
        Require(At(identityBoard, 300, Apart(newHand)) is null && At(identityBoard, 380, Apart(newHand)) is null,
            "A new hand identity borrowed another hand's ready target.");
        Require(Select(identityBoard, newHand, 500) is not null, "A new identity could not arm itself.");
        At(identityBoard, 900);
        Require(At(identityBoard, 1400, Apart(oldHand with { TrackingId = 3 })) is null &&
            At(identityBoard, 1480, Apart(oldHand with { TrackingId = 3 })) is null,
            "Long-dropout reacquisition repeated a stationary separated hand.");
    }

    private static void CheckBlackjackActions()
    {
        var board = new BoardSession(new BlackjackGame(initialShoe: Cards(8, 6, 8, 10, 10, 2, 10, 4)));
        board.ShowBlackjack(Time(0));
        Require(Select(board, Together(Button(board, "bj-deal")), 100)?.ButtonId == "bj-deal",
            "Index separation failed to deal blackjack.");
        Require(board.ActivateButton("bj-split", Time(500)), "The split-hand fixture could not split.");
        var hit = Together(Button(board, "bj-hit"));
        Require(Select(board, hit, 600)?.ButtonId == "bj-hit" && board.BlackjackState.ActiveHandIndex == 1 &&
            board.BlackjackState.Hands[0].IsBust && board.BlackjackState.Hands[1].Cards.Count == 2,
            "HIT did not bust the first split hand and move to the second.");
        Require(At(board, 980, Apart(hit)) is null && At(board, 1060, Apart(hit)) is null,
            "A held separated index also hit the second hand.");
        Require(Select(board, hit, 1160)?.ButtonId == "bj-hit" && board.BlackjackState.Hands[1].Cards.Count == 3,
            "Rejoining the four fingers did not rearm HIT for the next split hand.");

        board = new BoardSession(new BlackjackGame(initialShoe: Cards(10, 10, 8, 7)));
        board.ShowBlackjack(Time(0)); var deal = Together(Button(board, "bj-deal"));
        board.ActivateButton("bj-deal", Time(50)); board.ActivateButton("bj-stand", Time(100));
        At(board, 200, deal); At(board, 300, deal); board.TickBlackjack(Time(750));
        At(board, 800, deal); At(board, 900, deal); board.TickBlackjack(Time(1400));
        Require(board.BlackjackState.Phase == BlackjackPhase.RoundOver, "The dealer fixture did not finish.");
        Require(At(board, 1500, Apart(deal)) is null && At(board, 1580, Apart(deal)) is null &&
            board.BlackjackState.RoundNumber == 1, "Fingers grouped over disabled dealer controls armed the next Deal.");
        Require(Select(board, deal, 1700)?.ButtonId == "bj-deal" && board.BlackjackState.RoundNumber == 2,
            "A fresh together-to-apart selection could not deal after settlement.");
    }

    private static BoardSession PhotoCopy()
    {
        var board = new BoardSession(); board.ShowPhotoCopy(Time(0)); return board;
    }
    private static BoardNavigation? Select(BoardSession board, BoardHandSample hand, int start)
    {
        Require(At(board, start, hand) is null && At(board, start + 100, hand) is null &&
            At(board, start + 200, Apart(hand)) is null, "A gesture selected before separation confirmation.");
        return At(board, start + 280, Apart(hand));
    }
    private static BoardHandSample Together(BoardButton button, long trackingId = 1) => new(double.NaN, double.NaN,
        DateTimeOffset.MinValue, 0) { TrackingId = trackingId, FourFingersExtended = true, FingersTogether = true, FingerAim = Center(button) };
    private static BoardHandSample Apart(BoardHandSample hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardAim Center(BoardButton button) => new(button.Bounds.X + button.Bounds.Width / 2,
        button.Bounds.Y + button.Bounds.Height / 2);
    private static BoardFingerSelectionFeedback Feedback(BoardSession board) => board.FingerSelectionFeedback.Single();
    private static BoardNavigation? At(BoardSession board, int at, params BoardHandSample[] hands) => board.Update(hands, Time(at), Time(at));
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static BlackjackCard[] Cards(params int[] ranks) => ranks.Select((rank, index) =>
        new BlackjackCard(rank, (BlackjackSuit)(index % 4))).ToArray();
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
