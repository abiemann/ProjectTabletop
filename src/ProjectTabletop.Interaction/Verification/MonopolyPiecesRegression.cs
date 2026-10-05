using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProjectTabletop.Interaction;

internal static class MonopolyPiecesRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private static readonly int[] OldToNew =
        [0, 1, 2, 3, 10, 5, 17, 7, 18, 20, 13, 26, 4, 28, 29, 16, 6, 15, 8, 9,
         23, 31, 19, 32, 34, 25, 11, 12, 27, 14, 33, 37, 38, 30, 39, 35, 36, 21, 24, 22];

    public static void Run()
    {
        ReachableUniqueAndImmutable();
        SaveCompatibilityAndAtomicRejection();
        PieceChoicesDoNotConsumeGameRandomness();
        Console.WriteLine("Crown & Deed pieces passed: all eight designs, unique2–6-player setup/reconfiguration, " +
            "immutable choices, legacy defaults, saved human/AI identities, atomic invalid-save rejection and unchanged seeded gameplay.");
    }

    private static void ReachableUniqueAndImmutable()
    {
        Require(MonopolyGame.PieceNames.SequenceEqual(["Hat", "Car", "Shoe", "Dog", "Gun", "Iron", "Wheelbarrow", "Steamship"]),
            "The stable piece/atlas order changed.");
        for (int count = 2; count <= 6; count++)
        {
            var game = Setup();
            while (game.Snapshot.HumanPlayers + game.Snapshot.AiPlayers < count) Act(game, "mp-ai-plus");
            var original = game.Snapshot;
            int[] originalChoices = original.SetupPieces.ToArray();
            var seen = originalChoices.ToHashSet();
            for (int pass = 0; pass < 12; pass++)
            for (int slot = 0; slot < count; slot++)
            {
                int[] before = game.Snapshot.SetupPieces.ToArray();
                Act(game, $"mp-piece-next-{slot + 1}");
                var choices = game.Snapshot.SetupPieces;
                Require(choices.Count == count && choices.Distinct().Count() == count && choices.All(p => p is >= 0 and < 8) &&
                    choices[slot] != before[slot] && Enumerable.Range(0, count).Where(i => i != slot).All(i => choices[i] == before[i]),
                    "Cycling took another player's piece or changed the wrong slot.");
                seen.UnionWith(choices);
            }
            Require(seen.SetEquals(Enumerable.Range(0, 8)), $"Not all eight designs were reachable with{count} players.");
            Require(original.SetupPieces.SequenceEqual(originalChoices), "A later selection mutated an earlier setup snapshot.");
            Throws<NotSupportedException>(() => ((IList<int>)original.SetupPieces)[0] = 7,
                "Setup choices exposed a mutable backing array.");

            // An inactive slot may retain a preference now taken by a smaller
            // group. Re-enabling slots must preserve current choices and use spares.
            while (game.Snapshot.AiPlayers > 1) Act(game, "mp-ai-minus");
            for (int step = 0; step < 5; step++) Act(game, "mp-piece-next-1");
            int[] remaining = game.Snapshot.SetupPieces.ToArray();
            while (game.Snapshot.HumanPlayers + game.Snapshot.AiPlayers < 6) Act(game, "mp-human-plus");
            Require(game.Snapshot.SetupPieces.Distinct().Count() == 6 &&
                game.Snapshot.SetupPieces.Take(remaining.Length).SequenceEqual(remaining),
                "Reconfiguring players reassigned existing pieces or restored duplicate inactive choices.");
            int[] selected = game.Snapshot.SetupPieces.ToArray();
            Act(game, "mp-start");
            Require(game.Snapshot.Players.Select(p => p.PieceIndex).SequenceEqual(selected) &&
                game.Snapshot.Players.Select(p => p.ColorIndex).SequenceEqual(Enumerable.Range(0, 6)) &&
                game.Snapshot.Players.Select(p => p.Id).SequenceEqual(Enumerable.Range(1, 6)),
                "Starting the game lost selected pieces or changed player/ownership identities.");
            Require(!game.HandleAction("mp-piece-next-1", Epoch), "A piece selection remained active after setup.");
        }
    }

    private static void SaveCompatibilityAndAtomicRejection()
    {
        var game = Setup();
        Choose(game, 0, 7); Choose(game, 1, 6);
        Act(game, "mp-start");
        string saved = game.ExportSave();
        var restored = new MonopolyGame(seed: 91); restored.LoadSave(saved, Epoch);
        Require(restored.ExportSave() == saved && restored.Snapshot.Players.Select(p => p.PieceIndex).SequenceEqual([7, 6]) &&
            !restored.Snapshot.Players[0].IsAi && restored.Snapshot.Players[1].IsAi &&
            restored.Snapshot.SetupPieces.SequenceEqual([7, 6]), "Piece round-trip changed a human/AI identity or design.");
        var staged = new MonopolyGame(); staged.StageSave(saved); Act(staged, "mp-resume");
        Require(staged.ExportSave() == saved, "Staging and resuming discarded saved silver pieces.");

        foreach (int version in new[] { 1, 2 })
        {
            var data = Read(saved);
            data.Players[0].ColorIndex = 4; data.Players[1].ColorIndex = 2;
            data.Players.ForEach(p => p.PieceIndex = null);
            data.Version = version;
            if (version == 1)
                foreach (var property in data.Properties) property.SpaceIndex = Array.IndexOf(OldToNew, property.SpaceIndex);
            var legacy = JsonNode.Parse(JsonSerializer.Serialize(data, Json))!;
            foreach (var player in legacy["players"]!.AsArray()) player!.AsObject().Remove("pieceIndex");
            restored.LoadSave(legacy.ToJsonString(), Epoch);
            Require(restored.Snapshot.Players.Select(p => p.PieceIndex).SequenceEqual([4, 2]) &&
                restored.Snapshot.Players.Select(p => p.ColorIndex).SequenceEqual([4, 2]) && Read(restored.ExportSave()).Version == 2,
                $"Version{version} missing piece fields did not preserve their colour-based defaults.");
        }
        var mixed = Read(saved); mixed.Players[0].PieceIndex = 1; mixed.Players[1].PieceIndex = null;
        restored.LoadSave(JsonSerializer.Serialize(mixed, Json), Epoch);
        Require(restored.Snapshot.Players.Select(p => p.PieceIndex).SequenceEqual([1, 2]),
            "A missing legacy piece took an explicitly reserved design.");
        mixed.Players.ForEach(p => { p.PieceIndex = null; p.ColorIndex = 3; });
        restored.LoadSave(JsonSerializer.Serialize(mixed, Json), Epoch);
        Require(restored.Snapshot.Players.Select(p => p.PieceIndex).SequenceEqual([3, 4]),
            "Previously valid repeated legacy colours produced repeated physical pieces.");

        restored.LoadSave(saved, Epoch); restored.StageSave(saved);
        var before = restored.Snapshot;
        foreach (int invalid in new[] { -1, 8, int.MaxValue, 6 })
        {
            var data = Read(saved); data.Players[0].PieceIndex = invalid;
            string bad = JsonSerializer.Serialize(data, Json);
            Throws<FormatException>(() => restored.LoadSave(bad, Epoch.AddSeconds(1)), "An invalid/duplicate saved piece was accepted.");
            Throws<FormatException>(() => restored.StageSave(bad), "An invalid/duplicate piece replaced the staged game.");
            Require(ReferenceEquals(before, restored.Snapshot) && restored.ExportSave() == saved,
                "Rejecting a piece save partially mutated the active game or snapshot.");
        }
        // The same explicit design fields survive itinerary migration too.
        var explicitLegacy = Read(saved); explicitLegacy.Version = 1;
        foreach (var property in explicitLegacy.Properties) property.SpaceIndex = Array.IndexOf(OldToNew, property.SpaceIndex);
        restored.LoadSave(JsonSerializer.Serialize(explicitLegacy, Json), Epoch);
        Require(restored.Snapshot.Players.Select(p => p.PieceIndex).SequenceEqual([7, 6]),
            "Legacy itinerary migration changed explicit piece identities.");
    }

    private static void PieceChoicesDoNotConsumeGameRandomness()
    {
        var plain = Setup(); var decorated = Setup();
        foreach (var game in new[] { plain, decorated }) { Act(game, "mp-human-minus"); Act(game, "mp-ai-plus"); }
        for (int pass = 0; pass < 37; pass++) Act(decorated, $"mp-piece-next-{pass % 2 + 1}");
        Act(plain, "mp-start"); Act(decorated, "mp-start");
        for (int tick = 0; tick < 160; tick++)
        {
            var now = Epoch.AddSeconds(tick + 1);
            Require(plain.Tick(now) == decorated.Tick(now), "Cosmetic choices changed AI timing.");
            Require(WithoutPieces(plain.ExportSave()) == WithoutPieces(decorated.ExportSave()),
                "Cosmetic piece selection consumed game RNG or changed dice, cards, money, ownership or AI decisions.");
        }
    }

    private static MonopolyGame Setup()
    {
        var game = new MonopolyGame(seed: 173); Act(game, "mp-start-game"); return game;
    }
    private static void Choose(MonopolyGame game, int slot, int desired)
    {
        for (int tries = 0; game.Snapshot.SetupPieces[slot] != desired && tries < 8; tries++) Act(game, $"mp-piece-next-{slot + 1}");
        Require(game.Snapshot.SetupPieces[slot] == desired, "A free design was unreachable.");
    }
    private static MonopolySaveData Read(string saved) => JsonSerializer.Deserialize<MonopolySaveData>(saved, Json)!;
    private static string WithoutPieces(string saved)
    {
        var node = JsonNode.Parse(saved)!;
        foreach (var player in node["players"]!.AsArray()) player!.AsObject().Remove("pieceIndex");
        return node.ToJsonString();
    }
    private static void Act(MonopolyGame game, string action) => Require(game.HandleAction(action, Epoch), "Rejected fixture action:" + action);
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException(message);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
