using ProjectTabletop.Interaction;

internal static class BlackjackDealRegression
{
    public static void Run()
    {
        CheckEveryDealRoute();
        CheckPresentationInput();
        CheckLifecycleAndDealer();
        Console.WriteLine("Blackjack DEAL verification passed: committed immutable snapshots, all input routes, " +
            "unique events, presentation locks, delayed gesture barriers, navigation and dealer pause.");
    }

    private static void CheckEveryDealRoute()
    {
        foreach (string route in new[] { "pointer", "pinch", "fingers" })
        {
            var board = Board();
            var events = new List<BlackjackDeal>();
            int hitEvents = 0;
            board.BlackjackHitOccurred += _ => hitEvents++;
            board.BlackjackDealOccurred += deal =>
            {
                Require(ReferenceEquals(deal.Current, board.BlackjackState) &&
                    board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
                    "DEAL event preceded committed game state or input feedback barriers.");
                events.Add(deal);
            };
            var button = Button(board, "bj-deal");
            if (route == "pointer") Require(board.ActivateButton("bj-deal", Time(100)), "Pointer DEAL failed.");
            else if (route == "pinch")
                Require(board.Update([Pinch(button, 1, 100)], Time(100), Time(100))?.ButtonId == "bj-deal", "Pinch DEAL failed.");
            else
            {
                var hand = Fingers(button);
                board.Update([hand], Time(10), Time(10));
                board.Update([hand], Time(110), Time(110));
                hand = hand with { FingersTogether = false, IndexFingerSeparated = true };
                board.Update([hand], Time(130), Time(130));
                Require(board.Update([hand], Time(220), Time(220))?.ButtonId == "bj-deal", "Finger DEAL failed.");
                Require(board.Update([hand], Time(240), Time(240)) is null, "Held finger selection repeated DEAL.");
            }
            Require(events is [{ Sequence: 1, Previous.Phase: BlackjackPhase.Betting, Current.Phase: BlackjackPhase.PlayerTurn }] &&
                events[0].Previous.Hands.Count == 0 && events[0].Previous.DealerCards.Count == 0 &&
                events[0].Current.Hands[0].Cards.Count == 2 && events[0].Current.DealerCards[1] is null &&
                events[0].Current.DealerHoleCardHidden && events[0].StartedAt == Time(route == "fingers" ? 220 : 100) && hitEvents == 0,
                "DEAL payload exposed a hole card, wrong state/time, or emitted a HIT event.");
            Require(!board.ActivateButton("bj-deal", Time(300)) && !board.ActivateButton("unknown", Time(310)) &&
                events.Count == 1, "Rejected game action emitted DEAL.");
            Require(board.ActivateButton("bj-hit", Time(320)) && events[0].Current.Hands[0].Cards.Count == 2 &&
                events[0].Current.DealerCards[1] is null, "Later gameplay mutated a saved DEAL snapshot.");
        }

        var natural = new BoardSession(new BlackjackGame(initialShoe: Cards(1, 9, 10, 7, 1, 8, 10, 6)));
        natural.ShowBlackjack(Time(0));
        var deals = new List<BlackjackDeal>();
        natural.BlackjackDealOccurred += deals.Add;
        Require(natural.ActivateButton("bj-deal", Time(100)) &&
            deals[0].Current is { Phase: BlackjackPhase.RoundOver, Bankroll: 1037.5m, DealerHoleCardHidden: false },
            "DEAL presentation changed immediate natural-blackjack settlement rules.");
        natural.ResetInput(Time(110)); natural.ShowMenu(Time(120)); natural.ShowBlackjack(Time(130));
        Require(natural.ActivateButton("bj-reset", Time(140)) && natural.ActivateButton("bj-deal", Time(150)) &&
            deals.Select(deal => deal.Sequence).SequenceEqual([1L, 2L]) &&
            !natural.ActivateButton("bj-deal", Time(149)) && deals.Count == 2,
            "Navigation/reset reused a DEAL sequence, or an old-time action emitted DEAL.");
    }

    private static void CheckPresentationInput()
    {
        var board = Board();
        board.BlackjackDealOccurred += deal => board.HoldBlackjackPresentationUntil(deal.StartedAt.AddMilliseconds(900));
        Require(board.ActivateButton("bj-deal", Time(100)), "Presentation fixture did not deal.");
        Require(board.Buttons.Where(button => button.Id != "menu").All(button => !button.Enabled) &&
            Button(board, "menu").Enabled, "Presentation did not disable every game control while retaining menu.");
        var hit = Button(board, "bj-hit");
        Require(!board.ActivateButton("bj-hit", Time(200)) && !board.ActivateButton("bj-stand", Time(250)) &&
            board.Update([Pinch(hit, 1, 300)], Time(300), Time(300)) is null && board.HoveredButtonIds.Count == 0,
            "A pointer/pinch or hover passed the presentation lock.");
        var hand = Fingers(hit);
        board.Update([hand], Time(400), Time(400));
        board.Update([hand], Time(500), Time(500));
        Require(board.FingerSelectionFeedback.Count == 0, "Disabled button armed a finger gesture.");
        board.HoldBlackjackPresentationUntil(Time(600));
        Require(!board.ActivateButton("bj-hit", Time(700)), "A shorter hold shortened the presentation.");
        Require(!board.TickBlackjack(Time(999)) && !hit.Enabled && !Button(board, "bj-hit").Enabled,
            "Presentation expired before the supplied deadline.");
        // No input frame is required at expiry. Rejected old pulses may arrive afterwards.
        board.TickBlackjack(Time(1000));
        Require(Button(board, "bj-hit").Enabled, "The supplied deadline did not release controls.");
        Require(board.Update([Pinch(hit, 2, 900)], Time(1001), Time(1001)) is null,
            "An unseen pinch begun during the hold fired after it ended.");
        Require(board.Update([Pinch(hit, 3, 1010) with { SelectionFrameTime = Time(950) }], Time(1010), Time(1010)) is null,
            "A disabled-period pointing anchor survived presentation release.");
        var separated = hand with { FingersTogether = false, IndexFingerSeparated = true };
        board.Update([separated], Time(1020), Time(1020));
        Require(board.Update([separated], Time(1110), Time(1110)) is null && board.BlackjackState.Hands[0].Cards.Count == 2,
            "Fingers armed while disabled selected immediately on release.");
        board.Update([hand], Time(1120), Time(1120));
        board.Update([hand], Time(1220), Time(1220));
        board.Update([separated], Time(1240), Time(1240));
        Require(board.Update([separated], Time(1330), Time(1330))?.ButtonId == "bj-hit",
            "The hold blocked a fresh finger gesture after release.");

        var noTick = Board();
        noTick.ActivateButton("bj-deal", Time(100));
        noTick.HoldBlackjackPresentationUntil(Time(500));
        Require(noTick.ActivateButton("bj-hit", Time(500)), "Pointer time alone did not release the presentation.");
        noTick.HoldBlackjackPresentationUntil(Time(900));
        Require(noTick.Update([Pinch(Button(noTick, "bj-hit"), 10, 901)], Time(901), Time(901))?.ButtonId == "bj-hit",
            "Camera update time alone did not release the presentation for a fresh pinch.");
    }

    private static void CheckLifecycleAndDealer()
    {
        foreach (bool gesture in new[] { false, true })
        {
            var board = Board();
            board.ActivateButton("bj-deal", Time(100));
            board.HoldBlackjackPresentationUntil(Time(2000));
            bool navigated = gesture
                ? board.Update([Pinch(Button(board, "menu"), 1, 300)], Time(300), Time(300))?.ButtonId == "menu"
                : board.ActivateButton("menu", Time(300));
            Require(navigated && board.Screen == BoardScreen.Menu, "Presentation blocked Exit.");
            board.ShowBlackjack(Time(400));
            Require(Button(board, "bj-hit").Enabled, "Menu navigation left a presentation lock behind.");
            board.HoldBlackjackPresentationUntil(Time(2000));
            board.ShowHandTrackingTest(Time(500)); board.ShowBlackjack(Time(600));
            Require(Button(board, "bj-hit").Enabled, "Programmatic navigation left a presentation lock behind.");
            board.HoldBlackjackPresentationUntil(Time(2000));
            board.ResetInput(Time(700));
            Require(Button(board, "bj-hit").Enabled, "ResetInput retained the presentation lock.");
            board.HoldBlackjackPresentationUntil(Time(699));
            Require(Button(board, "bj-hit").Enabled, "An already expired hold disabled controls.");
        }

        var dealer = Board();
        dealer.ActivateButton("bj-deal", Time(100));
        dealer.ActivateButton("bj-stand", Time(200));
        long revision = dealer.BlackjackState.Revision;
        dealer.HoldBlackjackPresentationUntil(Time(2000));
        Require(!dealer.TickBlackjack(Time(900)) && !dealer.TickBlackjack(Time(1999)) &&
            dealer.BlackjackState.Revision == revision && dealer.BlackjackState.DealerHoleCardHidden,
            "Dealer reveal/draw progressed during the presentation.");
        Require(dealer.TickBlackjack(Time(2000)) && !dealer.BlackjackState.DealerHoleCardHidden &&
            dealer.BlackjackState.Revision == revision + 1 && !dealer.TickBlackjack(Time(2001)),
            "Release changed the dealer's normal one-step timing.");

        var betting = Board();
        betting.HoldBlackjackPresentationUntil(Time(500));
        Require(betting.Buttons.Where(button => button.Id != "menu").All(button => !button.Enabled) &&
            !betting.ActivateButton("bj-bet-50", Time(100)) && !betting.ActivateButton("bj-reset", Time(200)) &&
            !betting.ActivateButton("bj-deal", Time(300)), "Betting/reset controls bypassed a presentation lock.");
    }

    private static BoardSession Board()
    {
        var board = new BoardSession(new BlackjackGame(initialShoe: Cards(2, 6, 3, 10, 2, 2, 3, 10)));
        board.ShowBlackjack(Time(0));
        return board;
    }
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardHandSample Pinch(BoardButton button, long id, int at) => new(
        button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2, Time(at + 1000), id);
    private static BoardHandSample Fingers(BoardButton button) => new(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
    {
        TrackingId = 71, FourFingersExtended = true, FingersTogether = true,
        FingerAim = new(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2)
    };
    private static BlackjackCard[] Cards(params int[] ranks) => ranks.Select((rank, index) =>
        new BlackjackCard(rank, (BlackjackSuit)(index % 4))).ToArray();
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
