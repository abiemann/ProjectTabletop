using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProjectTabletop.Interaction;

internal static class CrownDeedMigrationRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 4, 13, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    // Independently specified itinerary correspondence, including every non-estate stop.
    private static readonly int[] OldToNew =
    [0, 1, 2, 3, 10, 5, 17, 7, 18, 20, 13, 26, 4, 28, 29, 16, 6, 15, 8, 9,
     23, 31, 19, 32, 34, 25, 11, 12, 27, 14, 33, 37, 38, 30, 39, 35, 36, 21, 24, 22];

    public static void Run()
    {
        EveryStopAndEstateSurvives();
        PendingDecisionsSurvive();
        DeckTargetsUseTheNewItinerary();
        InvalidAndCurrentSavesAreNotReinterpreted();
        Console.WriteLine("Crown & Deed save migration passed: all 40 stops, developed/mortgaged estates, custody, " +
            "pending purchase/management/auction/debt, deck order and held passes, original card destinations, " +
            "version-2 idempotence and atomic rejection of invalid or unversioned saves.");
    }

    private static void EveryStopAndEstateSurvives()
    {
        for (int old = 0; old < 40; old++)
        {
            var data = Data();
            string legacy = Legacy(data, save => save.Players[0].Position = old);
            var game = Load(legacy);
            Require(game.Snapshot.Players[0].Position == OldToNew[old], $"Legacy stop {old} migrated incorrectly.");
        }
        var assets = Data();
        foreach (var property in assets.Properties)
        {
            property.OwnerId = CrownDeedGame.Spaces[property.SpaceIndex].Group == CrownDeedGroup.Brown ? 1 : 2;
            property.Houses = CrownDeedGame.Spaces[property.SpaceIndex].Group == CrownDeedGroup.Brown ? 4 : 0;
            property.Mortgaged = property.SpaceIndex is 4 or 35;
        }
        assets.Players[0].Money = 732; assets.Players[1].Money = 2901;
        assets.Players[0].Position = CrownDeedGame.CivicWatchIndex;
        assets.Players[0].InJail = true; assets.Players[0].JailTurns = 2;
        assets.ChanceDeck.Reverse(); assets.ChestDeck = assets.ChestDeck.Skip(7).Concat(assets.ChestDeck.Take(7)).ToList();
        assets.ChanceIndex = 6; assets.ChestIndex = 11;
        assets.ChanceFreeCardHolderId = 1; assets.ChestFreeCardHolderId = 2;
        assets.Players[0].GetOutOfJailCards = 1; assets.Players[1].GetOutOfJailCards = 1;
        assets.Status = "Old itinerary status"; assets.LastCard = "A previously resolved legacy card";
        var restored = Load(Legacy(assets));
        var current = Read(restored.ExportSave());
        Require(current.Version == 2 && current.Players[0].Position == 13 && current.Players[0].InJail &&
            current.Players[0].JailTurns == 2 && current.Players[0].Money == 732 && current.Players[1].Money == 2901 &&
            current.Properties.All(p => assets.Properties.Any(original => original.SpaceIndex == p.SpaceIndex &&
                original.OwnerId == p.OwnerId && original.Houses == p.Houses && original.Mortgaged == p.Mortgaged)),
            "Migration changed an estate's economics, improvements, mortgage, owner, cash or custody.");
        Require(current.ChanceDeck.SequenceEqual(assets.ChanceDeck) && current.ChestDeck.SequenceEqual(assets.ChestDeck) &&
            current.ChanceIndex == 6 && current.ChestIndex == 11 && current.ChanceFreeCardHolderId == 1 &&
            current.ChestFreeCardHolderId == 2 && current.Players.All(p => p.GetOutOfJailCards == 1) &&
            !current.Status.Contains("Old itinerary") && !current.LastCard.Contains("legacy card"),
            "Migration shuffled a deck, lost a held pass or replayed obsolete public prose.");
        Require(CrownDeedGame.Spaces[22].Name == "Royal Arcade" && CrownDeedGame.Spaces[22].Price == 400 &&
            CrownDeedGame.Spaces[22].Rents[^1] == 2000 && CrownDeedGame.Spaces[26].Name == "Starling Gardens" &&
            CrownDeedGame.Spaces[34].Name == "Suncrest Avenue", "Migrated estates lost their named economic counterparts.");
    }

    private static void PendingDecisionsSurvive()
    {
        var purchase = Data();
        purchase.Phase = CrownDeedPhase.AwaitingPurchase; purchase.PendingPropertyIndex = 22; purchase.Players[0].Position = 22;
        var game = Load(Legacy(purchase));
        Require(game.Snapshot.PendingPropertyIndex == 22 && game.HandleAction("mp-buy", Epoch.AddSeconds(1)) &&
            game.Snapshot.Players[0].Money == 1100 && game.Snapshot.Properties.Single(p => p.SpaceIndex == 22).OwnerId == 1,
            "A legacy pending purchase bought the wrong estate or charged a changed price.");

        var manage = Data();
        manage.Phase = CrownDeedPhase.ManageProperties; manage.ManageReturnPhase = CrownDeedPhase.AwaitingRoll;
        manage.SelectedPropertyIndex = 26; manage.Properties.Single(p => p.SpaceIndex == 26).OwnerId = 1;
        game = Load(Legacy(manage));
        Require(game.Snapshot.SelectedPropertyIndex == 26 && game.HandleAction("mp-mortgage", Epoch.AddSeconds(1)) &&
            game.Snapshot.Players[0].Money == 1570, "Selected-estate migration changed the mortgage target or amount.");

        var auction = Data();
        auction.Phase = CrownDeedPhase.Auction; auction.PendingPropertyIndex = 34;
        auction.PendingBankAuctions = [16, 27];
        auction.Auction = new() { SpaceIndex = 34, Bid = 50, BidderId = 1, CurrentBidderId = 2 };
        game = Load(Legacy(auction));
        var migrated = Read(game.ExportSave());
        Require(migrated.Auction is { SpaceIndex: 34, Bid: 50, BidderId: 1, CurrentBidderId: 2 } &&
            migrated.PendingBankAuctions.SequenceEqual([16, 27]) && game.HandleAction("mp-pass", Epoch.AddSeconds(1)) &&
            game.Snapshot.Properties.Single(p => p.SpaceIndex == 34).OwnerId == 1 && game.Snapshot.Players[0].Money == 1450,
            "Legacy auction bids, pending bank assets or winner settlement changed during migration.");

        var debt = Data();
        debt.Phase = CrownDeedPhase.Debt; debt.Players[0].Money = 10;
        debt.DebtAmount = 200; debt.DebtPlayerId = 1; debt.DebtCreditorId = 2;
        debt.Payments = [new() { PlayerId = 1, CreditorId = 2, Amount = 200 }];
        game = Load(Legacy(debt));
        Require(game.Snapshot.DebtAmount == 200 && game.Snapshot.DebtPlayerId == 1 && game.Snapshot.DebtCreditorId == 2 &&
            game.HandleAction("mp-bankrupt", Epoch.AddSeconds(1)) && game.Snapshot.Players[1].Money == 1510,
            "Migration reassigned an outstanding creditor payment.");
    }

    private static void DeckTargetsUseTheNewItinerary()
    {
        foreach (var (card, target) in new (int, int)[] { (0, 0), (1, 34), (2, 26), (3, 22), (4, 5), (5, 16), (7, 27) })
        {
            var data = Data();
            data.Players[0].Position = 4;
            data.ChanceDeck = new[] { card }.Concat(Enumerable.Range(0, 16).Where(id => id != card)).ToList();
            var game = Load(Legacy(data), new CrownDeedDice(1, 2));
            Require(game.HandleAction("mp-roll", Epoch.AddSeconds(1)) && game.Snapshot.ActivePlayer!.Position == target,
                $"Retained Charter card {card} traveled to a stale square-board index.");
            Require(game.Snapshot.Players[0].Money == (target < 7 ? 1700 : 1500),
                $"Charter card {card} changed the forward circuit grant.");
        }
        var ledger = Data();
        ledger.ChestDeck = new[] { 4 }.Concat(Enumerable.Range(0, 16).Where(id => id != 4)).ToList();
        var pass = Load(Legacy(ledger), new CrownDeedDice(1, 1));
        Require(pass.HandleAction("mp-roll", Epoch.AddSeconds(1)) && pass.Snapshot.LastCard.StartsWith("Safe-Conduct Pass") &&
            pass.Snapshot.Players[0].GetOutOfJailCards == 1, "Legacy Ledger pass ID lost its retained effect or new content.");
    }

    private static void InvalidAndCurrentSavesAreNotReinterpreted()
    {
        var data = Data(); data.Players[0].Position = 10;
        string current = JsonSerializer.Serialize(data, Json);
        var game = Load(current);
        Require(game.Snapshot.Players[0].Position == 10 && game.ExportSave() == current,
            "A current city save was mistaken for the legacy itinerary.");
        var before = game.Snapshot;
        string invalid = Legacy(data, save => save.Players[0].Position = 40);
        Throws<FormatException>(() => game.LoadSave(invalid, Epoch.AddSeconds(1)), "Invalid legacy position was accepted.");
        var missingVersion = JsonNode.Parse(current)!.AsObject(); missingVersion.Remove("version");
        Throws<FormatException>(() => game.LoadSave(missingVersion.ToJsonString(), Epoch.AddSeconds(2)),
            "An unversioned save silently adopted the wrong itinerary.");
        Require(ReferenceEquals(before, game.Snapshot), "An invalid migration partially replaced the running game.");
    }

    private static CrownDeedSaveData Data()
    {
        var game = new CrownDeedGame(seed: 73);
        foreach (string action in new[] { "mp-start-game", "mp-ai-minus", "mp-human-plus", "mp-start" })
            Require(game.HandleAction(action, Epoch), "Save fixture setup failed.");
        return Read(game.ExportSave());
    }
    private static CrownDeedSaveData Read(string text) => JsonSerializer.Deserialize<CrownDeedSaveData>(text, Json)!;
    private static string Legacy(CrownDeedSaveData data, Action<CrownDeedSaveData>? amend = null)
    {
        var save = Read(JsonSerializer.Serialize(data, Json));
        static int Old(int value) => Array.IndexOf(OldToNew, value);
        save.Version = 1;
        foreach (var player in save.Players) player.Position = Old(player.Position);
        foreach (var property in save.Properties) property.SpaceIndex = Old(property.SpaceIndex);
        if (save.PendingPropertyIndex is { } pending) save.PendingPropertyIndex = Old(pending);
        if (save.SelectedPropertyIndex is { } selected) save.SelectedPropertyIndex = Old(selected);
        if (save.Auction is { } auction) auction.SpaceIndex = Old(auction.SpaceIndex);
        save.PendingBankAuctions = save.PendingBankAuctions.Select(Old).ToList();
        amend?.Invoke(save);
        return JsonSerializer.Serialize(save, Json);
    }
    private static CrownDeedGame Load(string save, params CrownDeedDice[] rolls)
    {
        var game = new CrownDeedGame(seed: 73, initialRolls: rolls);
        game.LoadSave(save, Epoch); return game;
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException(message);
    }
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
