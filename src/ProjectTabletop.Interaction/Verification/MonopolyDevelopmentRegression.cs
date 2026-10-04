using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectTabletop.Interaction;

internal static class MonopolyDevelopmentRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Run()
    {
        HumanDevelopmentIsCommittedAndImmutable();
        AiDevelopmentAndBoardRelay();
        Console.WriteLine("Crown & Deed development events passed: committed human/AI cash and buildings, immutable payloads, " +
            "rejected actions, load/new-game silence and sequence continuity, and shared presentation barriers.");
    }

    private static void HumanDevelopmentIsCommittedAndImmutable()
    {
        var game = Fixture();
        var events = Observe(game);
        string undeveloped = game.ExportSave();
        Require(game.HandleAction("mp-manage", At(100)), "The owner could not open estate management.");
        var before = game.Snapshot;
        Require(game.HandleAction("mp-build", At(120)) && events.Count == 1, "An accepted shop emitted no event.");
        var first = events.Single();
        Require(first.Sequence == 1 && first.SpaceIndex == 1 && first.PlayerId == 1 && first.StartedAt == At(120) &&
            ReferenceEquals(first.Previous, before) && Level(first.Previous, 1) == 0 && Level(first.Current, 1) == 1 &&
            first.Previous.Players[0].Money == 1500 && first.Current.Players[0].Money == 1450,
            "The development event lost its source clock, estate or committed before/after transaction.");
        string frozen = JsonSerializer.Serialize(first);
        Require(!game.HandleAction("mp-build", At(120)) && !game.HandleAction("mp-build", At(119)) && events.Count == 1,
            "An unavailable or backwards build replayed the development.");
        Throws<NotSupportedException>(() => ((IList<MonopolyPropertySnapshot>)first.Current.Properties)[0] =
            first.Current.Properties[0] with { Houses = 5 }, "A published development property was mutable.");
        Throws<NotSupportedException>(() => ((IList<string>)first.Current.AvailableActions)[0] = "mp-build",
            "A published development action list was mutable.");
        Require(game.HandleAction("mp-property-next", At(140)) && game.HandleAction("mp-build", At(160)) &&
            events.Count == 2 && events[1].SpaceIndex == 3 && events[1].Sequence == 2,
            "The next legal even development did not receive a distinct event.");
        Require(game.HandleAction("mp-sell", At(180)) && events.Count == 2 &&
            JsonSerializer.Serialize(first) == frozen, "Selling fabricated a rise or mutated an earlier event.");
        game.StageSave(undeveloped);
        game.LoadSave(undeveloped, At(200));
        Require(events.Count == 2, "Loading or staging replayed historical buildings.");
        Require(game.HandleAction("mp-manage", At(220)) && game.HandleAction("mp-build", At(240)) &&
            events.Count == 3 && events[^1].Sequence == 3, "A save load reset development sequence identity.");
        Require(game.HandleAction("mp-exit", At(260)) && game.HandleAction("mp-exit-without-saving", At(280)) &&
            game.HandleAction("mp-start-game", At(300)) && game.HandleAction("mp-start", At(320)) && events.Count == 3,
            "A new game emitted an unearned development.");
        game.LoadSave(undeveloped, At(340));
        Require(game.HandleAction("mp-manage", At(360)) && game.HandleAction("mp-build", At(380)) &&
            events[^1].Sequence == 4, "Starting a new game reused development sequence identity.");

        var hall = Fixture(level: 4);
        var hallEvents = Observe(hall);
        Require(hall.HandleAction("mp-manage", At(100)) && hall.HandleAction("mp-build", At(120)) &&
            hallEvents is [{ Previous: var previous, Current: var current }] &&
            Level(previous, 1) == 4 && Level(current, 1) == 5 && current.Players[0].Money == 1450,
            "A grand hall did not emit the actual fourth-shop-to-hall development.");
    }

    private static void AiDevelopmentAndBoardRelay()
    {
        var game = Fixture(ai: true);
        var board = new BoardSession(monopoly: game);
        board.ShowMonopoly(At(100));
        var events = new List<MonopolyDevelopment>();
        long boardRevision = board.Revision;
        board.MonopolyDevelopmentOccurred += development =>
        {
            Require(board.Revision == boardRevision + 1 && ReferenceEquals(development.Current, board.MonopolyState),
                "The board relayed development before its input barrier and committed snapshot.");
            events.Add(development);
            board.HoldMonopolyPresentationUntil(development.StartedAt.AddMilliseconds(1500));
        };
        Require(board.TickMonopoly(At(1100)) && events.Count == 1 && events[0].Current.ActivePlayer!.IsAi &&
            events[0].StartedAt == At(1100) && Level(events[0].Current, 1) == 1 &&
            events[0].Current.Players[0].Money == 1450 && board.IsMonopolyPresentationActive(At(1100)),
            "An AI build was omitted, replayed or presented before its transaction.");
        Require(!board.TickMonopoly(At(1100)) && !board.TickMonopoly(At(1099)) && !board.TickMonopoly(At(2500)) &&
            events.Count == 1, "Duplicate, stale or presentation-blocked AI ticks built again.");
        // Releasing presentation creates one barrier before the next AI build creates its own.
        Require(!board.IsMonopolyPresentationActive(At(2600)), "The injected presentation deadline did not release.");
        boardRevision = board.Revision;
        Require(board.TickMonopoly(At(2601)) && events.Count == 2 && events[1].Sequence == 2 &&
            events[1].SpaceIndex == 3 && Level(events[1].Current, 3) == 1,
            "The AI could not make its next visible even development after the presentation.");
    }

    private static List<MonopolyDevelopment> Observe(MonopolyGame game)
    {
        var events = new List<MonopolyDevelopment>();
        game.DevelopmentOccurred += value =>
        {
            Require(value.Current.Revision == value.Previous.Revision + 1 &&
                ReferenceEquals(value.Current, game.Snapshot) && value.Current.Revision == game.Revision,
                "Development was published before the full mutation was committed.");
            events.Add(value);
        };
        return events;
    }

    private static MonopolyGame Fixture(bool ai = false, int level = 0)
    {
        var game = new MonopolyGame(seed: 51);
        Require(game.HandleAction("mp-start-game", At(0)) && game.HandleAction("mp-ai-minus", At(10)) &&
            game.HandleAction("mp-human-plus", At(20)) && game.HandleAction("mp-start", At(30)), "Fixture setup failed.");
        var data = JsonSerializer.Deserialize<MonopolySaveData>(game.ExportSave(), Json)!;
        foreach (var estate in data.Properties.Where(p => p.SpaceIndex is 1 or 3))
        {
            estate.OwnerId = 1;
            estate.Houses = level;
        }
        if (ai)
        {
            data.Humans = 1; data.Ais = 1; data.Players[0].IsAi = true;
            data.Phase = MonopolyPhase.AwaitingEndTurn;
        }
        game.LoadSave(JsonSerializer.Serialize(data, Json), At(40));
        return game;
    }
    private static int Level(MonopolySnapshot state, int index) => state.Properties.Single(p => p.SpaceIndex == index).Houses;
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException(message);
    }
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
