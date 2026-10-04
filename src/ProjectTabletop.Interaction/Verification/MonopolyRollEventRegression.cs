using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectTabletop.Interaction;

internal static class MonopolyRollEventRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SaveJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static int _milliseconds;

    public static void Run()
    {
        HumanRollsAndImmutablePayloads();
        AiRollsUseTheSameCommittedEvent();
        SequencesSurviveNewGamesAndLoads();
        JailRollsRetainTheirActualMovement();
        Console.WriteLine("Monopoly roll events passed: committed immutable before/after snapshots, real dice and " +
            "human/AI movement, rejected/duplicate/backwards barriers, non-roll silence, sequence continuity " +
            "across new games and saves, and stationary/releasing Jail rolls.");
    }

    private static void HumanRollsAndImmutablePayloads()
    {
        var game = new MonopolyGame(seed: 31, initialRolls: [new(1, 2), new(2, 3)]);
        var events = Observe(game);
        Require(!game.HandleAction("mp-roll", Next()) && !game.HandleAction("unknown", Next()) &&
            !game.Tick(Next(1100)) && events.Count == 0,
            "Rejected landing input or idle AI tick emitted a roll.");
        Start(game, humansOnly: true);
        Require(events.Count == 0, "Setup or Start emitted a dice animation event.");
        var previous = game.Snapshot;
        var rolledAt = Next();
        Require(game.HandleAction("mp-roll", rolledAt) && events.Count == 1,
            "Accepted human Roll did not publish exactly one event.");
        var first = events[0];
        Require(first.StartedAt == rolledAt && ReferenceEquals(first.Previous, previous) &&
            first.Previous.Phase == MonopolyPhase.AwaitingRoll && first.Previous.Dice == default &&
            first.Previous.ActivePlayer!.Position == 0 && first.Current.Dice == new MonopolyDice(1, 2) &&
            first.Current.ActivePlayer!.Position == 3 && first.Current.ActivePlayer.Id == first.Previous.ActivePlayer.Id &&
            first.Current.Phase == MonopolyPhase.AwaitingPurchase && first.Current.PendingPropertyIndex == 3,
            "Human event lost the committed dice, actor, landing or source time.");
        long revision = game.Revision;
        Require(!game.HandleAction("mp-roll", rolledAt) && !game.HandleAction("mp-roll", rolledAt.AddMilliseconds(-1)) &&
            events.Count == 1 && game.Revision == revision,
            "Duplicate unavailable or backwards Roll emitted a second event.");
        string frozenPrevious = JsonSerializer.Serialize(first.Previous);
        string frozenCurrent = JsonSerializer.Serialize(first.Current);
        Act(game, "mp-buy");
        Act(game, "mp-end-turn");
        Require(events.Count == 1 && game.Snapshot.Properties.Single(property => property.SpaceIndex == 3).OwnerId == 1,
            "Purchase or End Turn emitted a roll or failed to mutate later ownership.");
        var secondPrevious = game.Snapshot;
        var secondTime = Next();
        Require(game.HandleAction("mp-roll", secondTime) && events.Count == 2 &&
            events[1].StartedAt == secondTime && ReferenceEquals(events[1].Previous, secondPrevious) &&
            events[1].Previous.ActivePlayer!.Id == 2 && events[1].Previous.ActivePlayer!.Position == 0 &&
            events[1].Current.Dice == new MonopolyDice(2, 3) && events[1].Current.ActivePlayer!.Position == 5,
            "The next player's roll reused the prior actor, dice or before snapshot.");
        Require(JsonSerializer.Serialize(first.Previous) == frozenPrevious &&
            JsonSerializer.Serialize(first.Current) == frozenCurrent &&
            first.Current.Properties.Single(property => property.SpaceIndex == 3).OwnerId is null &&
            first.Current.Players[0].Money == 1500,
            "Later purchases or turns mutated an already published roll payload.");
    }

    private static void AiRollsUseTheSameCommittedEvent()
    {
        var game = new MonopolyGame(seed: 41, initialRolls: [new(1, 2), new(1, 2)]);
        var events = Observe(game);
        Start(game, humansOnly: false);
        Act(game, "mp-roll"); Act(game, "mp-buy"); Act(game, "mp-end-turn");
        Require(events.Count == 1 && game.Snapshot.ActivePlayer!.IsAi &&
            !game.HandleAction("mp-roll", Next()) && events.Count == 1,
            "A human input overrode or emitted a roll for the AI actor.");
        var previous = game.Snapshot;
        var time = Next(1100);
        Require(game.Tick(time) && events.Count == 2, "The AI's accepted Roll tick did not publish an event.");
        var roll = events[1];
        Require(ReferenceEquals(roll.Previous, previous) && roll.StartedAt == time &&
            roll.Previous.ActivePlayer!.IsAi && roll.Previous.ActivePlayer.Position == 0 &&
            roll.Current.ActivePlayer!.Id == roll.Previous.ActivePlayer.Id &&
            roll.Current.ActivePlayer.IsAi && roll.Current.ActivePlayer.Position == 3 &&
            roll.Current.ActivePlayer.Money == 1496 && roll.Current.Dice == new MonopolyDice(1, 2) &&
            roll.Current.Phase == MonopolyPhase.AwaitingEndTurn,
            "AI event did not contain its actual committed movement and rent result.");
        Require(!game.Tick(time) && !game.Tick(time.AddMilliseconds(-1)) && events.Count == 2,
            "Duplicate or backwards AI ticks emitted another Roll.");
        Require(game.Tick(Next(1100)) && game.Snapshot.ActivePlayerIndex == 0 && events.Count == 2,
            "The AI's non-roll End Turn emitted a dice event.");
    }

    private static void SequencesSurviveNewGamesAndLoads()
    {
        var game = new MonopolyGame(seed: 47, initialRolls: [new(1, 2), new(1, 3), new(2, 3)]);
        var events = Observe(game);
        Start(game, humansOnly: true);
        string initialSave = game.ExportSave();
        Act(game, "mp-roll");
        var first = events.Single();
        string frozen = JsonSerializer.Serialize(first);
        Act(game, "mp-exit"); Act(game, "mp-exit-without-saving");
        Start(game, humansOnly: true);
        Require(events.Count == 1, "Returning to landing and starting anew emitted a Roll.");
        Act(game, "mp-roll");
        Require(events.Count == 2 && events[1].Sequence == first.Sequence + 1 &&
            events[1].Current.Dice == new MonopolyDice(1, 3),
            "A new game reset or duplicated the per-game-instance roll sequence.");
        game.StageSave(initialSave);
        game.LoadSave(initialSave, Next());
        Require(events.Count == 2 && game.Snapshot.Phase == MonopolyPhase.AwaitingRoll,
            "Staging or loading a save fabricated a roll event.");
        Act(game, "mp-roll");
        Require(events.Count == 3 && events[2].Sequence == events[1].Sequence + 1 &&
            events[2].Current.Dice == new MonopolyDice(2, 3) && JsonSerializer.Serialize(first) == frozen,
            "A loaded game reset sequence identity or changed a historical payload.");
    }

    private static void JailRollsRetainTheirActualMovement()
    {
        foreach (var dice in new MonopolyDice[] { new(1, 2), new(2, 2) })
        {
            var game = new MonopolyGame(seed: 53, initialRolls: [dice]);
            Start(game, humansOnly: true);
            var save = JsonSerializer.Deserialize<MonopolySaveData>(game.ExportSave(), SaveJson)!;
            save.Players[0].Position = 13; save.Players[0].InJail = true;
            game.LoadSave(JsonSerializer.Serialize(save, SaveJson), Next());
            var events = Observe(game);
            var time = Next();
            Require(game.HandleAction("mp-roll", time) && events.Count == 1, "A legal Jail roll emitted no event.");
            var roll = events[0];
            Require(roll.StartedAt == time && roll.Previous.ActivePlayer is { Position: 13, InJail: true, JailTurns: 0 } &&
                roll.Current.Dice == dice && roll.Current.ActivePlayer!.Money == 1500,
                "Jail roll payload lost its before state or actual dice.");
            Require(dice.IsDouble
                ? roll.Current.ActivePlayer is { Position: 17, InJail: false, JailTurns: 0 } &&
                    roll.Current.Phase == MonopolyPhase.AwaitingPurchase
                : roll.Current.ActivePlayer is { Position: 13, InJail: true, JailTurns: 1 } &&
                    roll.Current.Phase == MonopolyPhase.AwaitingEndTurn,
                "Jail roll event invented movement or missed release on doubles.");
        }
    }

    private static List<MonopolyRoll> Observe(MonopolyGame game)
    {
        var events = new List<MonopolyRoll>();
        game.RollOccurred += roll =>
        {
            Require(roll.Sequence > 0 && (events.Count == 0 || roll.Sequence == events[^1].Sequence + 1) &&
                roll.Current.Revision == game.Revision && roll.Current.Revision == roll.Previous.Revision + 1 &&
                ReferenceEquals(roll.Current, game.Snapshot),
                "Roll was delivered before Changed(), with a stale current snapshot or non-monotonic identity.");
            events.Add(roll);
        };
        return events;
    }

    private static void Start(MonopolyGame game, bool humansOnly)
    {
        Act(game, "mp-start-game");
        if (humansOnly)
        {
            while (game.Snapshot.AiPlayers > 0) Act(game, "mp-ai-minus");
            while (game.Snapshot.HumanPlayers < 2) Act(game, "mp-human-plus");
        }
        Act(game, "mp-start");
    }
    private static DateTimeOffset Next(int milliseconds = 20) => Epoch.AddMilliseconds(_milliseconds += milliseconds);
    private static void Act(MonopolyGame game, string id) => Require(game.HandleAction(id, Next()),
        $"Legal {id} was rejected in {game.Snapshot.Phase}.");
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
