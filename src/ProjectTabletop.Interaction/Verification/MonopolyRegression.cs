using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectTabletop.Interaction;

internal static class MonopolyRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private static int _milliseconds;

    public static void Run()
    {
        CheckSetupAndActionGating();
        CheckPurchaseRentAndGo();
        CheckDoublesAndJail();
        CheckAuction();
        CheckBuildingAndMortgages();
        CheckDebtAndBankruptcy();
        CheckCardsAndEliminatedAi();
        CheckSaveValidationAndIsolation();
        CheckAiAndSeededGames();
        MonopolyMigrationRegression.Run();
        MonopolyDevelopmentRegression.Run();
        Console.WriteLine("Monopoly game verification passed: player setup, purchases/rent/Crown Gate, doubles/jail, " +
            "auctions, even building/selling, mortgages, debt/bankruptcy, cards, save validation and immutable snapshots, and AI turns.");
    }

    private static void CheckSetupAndActionGating()
    {
        var game = new MonopolyGame(seed: 13);
        Require(game.Snapshot.Phase == MonopolyPhase.Landing && game.Snapshot.Players.Count == 0,
            "Monopoly must open on an unstarted board.");
        var initial = game.Snapshot;
        Require(!game.HandleAction("mp-roll", Next()) && !game.HandleAction("unknown", Next()) &&
            ReferenceEquals(initial, game.Snapshot), "An unavailable action changed the landing board.");
        Act(game, "mp-start-game");
        Require(game.Snapshot.Phase == MonopolyPhase.Setup && game.Snapshot.HumanPlayers == 1 &&
            game.Snapshot.AiPlayers == 1, "The setup did not default to one human and one AI.");
        Act(game, "mp-ai-minus");
        Require(!game.Snapshot.AvailableActions.Contains("mp-start") && !game.HandleAction("mp-start", Next()),
            "A one-player game started.");
        Act(game, "mp-human-plus");
        Act(game, "mp-start");
        Require(game.Snapshot.Phase == MonopolyPhase.AwaitingRoll && game.Snapshot.Players.Count == 2 &&
            game.Snapshot.Players.All(player => !player.IsAi && player.Money == 1500 && player.Position == 0) &&
            game.Snapshot.Players.Select(player => player.Id).Distinct().Count() == 2,
            "Starting two humans did not create distinct funded players at Crown Gate.");
        var started = game.Snapshot;
        foreach (string id in new[] { "mp-start", "mp-start-game", "mp-buy", "mp-end-turn", "mp-build", "mp-jail-pay" })
            Require(!game.HandleAction(id, Next()), $"Unavailable action {id} was accepted before a roll.");
        Require(ReferenceEquals(started, game.Snapshot), "Rejected game actions changed its revision or snapshot.");
        Require(!game.HandleAction("mp-roll", Epoch), "A backwards action timestamp was accepted.");
    }

    private static void CheckPurchaseRentAndGo()
    {
        var game = Started(new MonopolyDice(1, 2), new(1, 2));
        Act(game, "mp-roll");
        Require(game.Snapshot.Phase == MonopolyPhase.AwaitingPurchase && game.Snapshot.ActivePlayer!.Position == 3 &&
            game.Snapshot.PendingPropertyIndex == 3 && game.Snapshot.ActivePlayer.Money == 1500,
            "Landing on Copper Lane did not offer the unowned property.");
        Act(game, "mp-buy");
        Require(Property(game, 3).OwnerId == game.Snapshot.Players[0].Id && game.Snapshot.Players[0].Money == 1440 &&
            game.Snapshot.Phase == MonopolyPhase.AwaitingEndTurn, "Buying Copper Lane did not debit its $60 price exactly once.");
        var bought = game.Snapshot;
        Require(!game.HandleAction("mp-buy", Next()) && ReferenceEquals(bought, game.Snapshot), "A purchase was charged twice.");
        Act(game, "mp-end-turn");
        Act(game, "mp-roll");
        Require(game.Snapshot.ActivePlayerIndex == 1 && game.Snapshot.Players[1].Position == 3 &&
            game.Snapshot.Players[1].Money == 1496 && game.Snapshot.Players[0].Money == 1444,
            "An opponent paid the wrong Copper Lane rent or the owner did not receive it.");

        var passGo = Started(new MonopolyDice(3, 3));
        Change(passGo, data => data.Players[0].Position = 37);
        Act(passGo, "mp-roll");
        Require(passGo.Snapshot.ActivePlayer!.Position == 3 && passGo.Snapshot.ActivePlayer.Money == 1700,
            "Passing Crown Gate failed to pay exactly $200.");

        var mortgaged = Started(new MonopolyDice(1, 2));
        Change(mortgaged, data => { var property = data.Properties.Single(p => p.SpaceIndex == 3);
            property.OwnerId = data.Players[1].Id; property.Mortgaged = true; });
        Act(mortgaged, "mp-roll");
        Require(mortgaged.Snapshot.Players.All(player => player.Money == 1500), "A mortgaged property collected rent.");

        var monopoly = Started(new MonopolyDice(1, 2));
        Change(monopoly, data => { foreach (var property in data.Properties.Where(p => p.SpaceIndex is 1 or 3))
            property.OwnerId = data.Players[1].Id; });
        Act(monopoly, "mp-roll");
        Require(monopoly.Snapshot.Players[0].Money == 1492 && monopoly.Snapshot.Players[1].Money == 1508,
            "An undeveloped complete brown set did not double its base rent.");
    }

    private static void CheckDoublesAndJail()
    {
        var doubles = Started(new MonopolyDice(3, 3), new(2, 2), new(4, 4));
        Act(doubles, "mp-roll"); Act(doubles, "mp-buy");
        Require(doubles.Snapshot.ActivePlayerIndex == 0 && doubles.Snapshot.Phase == MonopolyPhase.AwaitingRoll,
            "The first double did not grant another roll to the same player.");
        Act(doubles, "mp-roll");
        Require(doubles.Snapshot.ActivePlayer!.Position == 10 && !doubles.Snapshot.ActivePlayer.InJail,
            "A levy landing after doubles was mistaken for custody.");
        Act(doubles, "mp-roll");
        Require(doubles.Snapshot.ActivePlayer!.InJail && doubles.Snapshot.ActivePlayer.Position == 13 &&
            doubles.Snapshot.Phase == MonopolyPhase.AwaitingEndTurn,
            "Three doubles did not send the player directly to Jail.");
        Act(doubles, "mp-end-turn");
        Require(doubles.Snapshot.ActivePlayerIndex == 1, "A third double granted another roll after Jail.");

        var visiting = Started(new MonopolyDice(1, 2));
        Change(visiting, data => data.Players[0].Position = 10);
        Act(visiting, "mp-roll");
        Require(visiting.Snapshot.ActivePlayer is { Position: 13, InJail: false },
            "Visiting Civic Watch incorrectly detained the player.");

        var jailDouble = Jailed(new MonopolyDice(2, 2));
        Act(jailDouble, "mp-roll");
        Require(!jailDouble.Snapshot.ActivePlayer!.InJail && jailDouble.Snapshot.ActivePlayer.Position == 17,
            "A jailed double failed to release and move the player.");
        Act(jailDouble, "mp-buy"); Act(jailDouble, "mp-end-turn");
        Require(jailDouble.Snapshot.ActivePlayerIndex == 1, "A double used to leave Jail incorrectly granted an extra roll.");

        var jailPay = Jailed(new MonopolyDice(1, 2));
        Act(jailPay, "mp-jail-pay");
        Require(!jailPay.Snapshot.ActivePlayer!.InJail && jailPay.Snapshot.ActivePlayer.Money == 1450 &&
            jailPay.Snapshot.Phase == MonopolyPhase.AwaitingRoll, "Paying Jail bail did not cost $50 and allow a roll.");
        Act(jailPay, "mp-roll");
        Require(jailPay.Snapshot.ActivePlayer!.Position == 16, "Bail payment itself moved the player or prevented its roll.");

        var jailFailure = Jailed(new MonopolyDice(1, 2));
        Act(jailFailure, "mp-roll");
        Require(jailFailure.Snapshot.ActivePlayer!.InJail && jailFailure.Snapshot.ActivePlayer.Position == 13 &&
            jailFailure.Snapshot.ActivePlayer.JailTurns == 1 && jailFailure.Snapshot.ActivePlayer.Money == 1500,
            "A failed Jail roll moved the player or deducted premature bail.");

        var thirdFailure = Jailed(new MonopolyDice(1, 2));
        Change(thirdFailure, data => data.Players[0].JailTurns = 2);
        Act(thirdFailure, "mp-roll");
        Require(!thirdFailure.Snapshot.ActivePlayer!.InJail && thirdFailure.Snapshot.ActivePlayer.Position == 16 &&
            thirdFailure.Snapshot.ActivePlayer.Money == 1450, "The third failed Jail roll did not charge bail and use that roll.");

        var jailCard = Jailed(new MonopolyDice(1, 2));
        Change(jailCard, data => { data.Players[0].GetOutOfJailCards = 1; data.ChanceFreeCardHolderId = data.Players[0].Id; });
        Act(jailCard, "mp-jail-card");
        Require(!jailCard.Snapshot.ActivePlayer!.InJail && jailCard.Snapshot.ActivePlayer.GetOutOfJailCards == 0 &&
            jailCard.Snapshot.ActivePlayer.Money == 1500, "The Jail card was not consumed exactly once without bail.");
    }

    private static void CheckAuction()
    {
        var game = Started(new MonopolyDice(1, 2));
        Act(game, "mp-roll"); Act(game, "mp-auction");
        Require(game.Snapshot.Phase == MonopolyPhase.Auction && game.Snapshot.Auction is { SpaceIndex: 3, Bid: 0 },
            "Declining a purchase did not auction that property.");
        int bidder = game.Snapshot.Auction!.CurrentBidderId;
        Act(game, "mp-bid-10");
        Require(game.Snapshot.Auction is { Bid: 10 } && game.Snapshot.Auction.BidderId == bidder &&
            game.Snapshot.Players.All(player => player.Money == 1500), "Bidding debited cash before auction settlement.");
        Act(game, "mp-pass");
        Require(Property(game, 3).OwnerId == bidder && game.Snapshot.Players.Single(p => p.Id == bidder).Money == 1490 &&
            game.Snapshot.Phase == MonopolyPhase.AwaitingEndTurn, "The sole bidder did not receive the auction at its winning bid.");
        Require(!game.HandleAction("mp-pass", Next()), "A completed auction could be settled twice.");

        var noBids = Started(new MonopolyDice(1, 2));
        Act(noBids, "mp-roll"); Act(noBids, "mp-auction"); Act(noBids, "mp-pass"); Act(noBids, "mp-pass");
        Require(Property(noBids, 3).OwnerId is null && noBids.Snapshot.Players.All(p => p.Money == 1500) &&
            noBids.Snapshot.Phase == MonopolyPhase.AwaitingEndTurn, "An auction with no bids charged cash or assigned an owner.");
    }

    private static void CheckBuildingAndMortgages()
    {
        var building = Started();
        Change(building, data => { foreach (var property in data.Properties.Where(p => p.SpaceIndex is 1 or 3))
            property.OwnerId = data.Players[0].Id; });
        Act(building, "mp-manage");
        Require(building.Snapshot.SelectedPropertyIndex == 1, "Property management did not select an owned property.");
        Act(building, "mp-build");
        Require(Property(building, 1).Houses == 1 && building.Snapshot.ActivePlayer!.Money == 1450 &&
            !building.HandleAction("mp-build", Next()), "Building ignored house cost or the even-building requirement.");
        Act(building, "mp-property-next"); Act(building, "mp-build");
        Require(Property(building, 3).Houses == 1 && building.Snapshot.ActivePlayer!.Money == 1400,
            "A second property could not bring the set to an even one-house level.");
        Require(!building.HandleAction("mp-mortgage", Next()), "A developed color set allowed a mortgage.");
        Act(building, "mp-sell");
        Require(Property(building, 3).Houses == 0 && building.Snapshot.ActivePlayer!.Money == 1425 &&
            !building.HandleAction("mp-sell", Next()), "Selling a house refunded the wrong amount or sold below zero.");
        Act(building, "mp-property-previous"); Act(building, "mp-sell");
        Require(Property(building, 1).Houses == 0 && building.Snapshot.ActivePlayer!.Money == 1450,
            "Even house selling failed to clear the complete set.");
        Act(building, "mp-manage-back");
        Require(building.Snapshot.Phase == MonopolyPhase.AwaitingRoll, "Property management returned to the wrong turn phase.");

        var mortgage = Started();
        Change(mortgage, data => data.Properties.Single(p => p.SpaceIndex == 1).OwnerId = data.Players[0].Id);
        Act(mortgage, "mp-manage");
        Require(!mortgage.HandleAction("mp-build", Next()), "An incomplete color set allowed building.");
        Act(mortgage, "mp-mortgage");
        Require(Property(mortgage, 1).Mortgaged && mortgage.Snapshot.ActivePlayer!.Money == 1530 &&
            !mortgage.HandleAction("mp-mortgage", Next()), "Mortgage failed to pay half its property price exactly once.");
        Act(mortgage, "mp-unmortgage");
        Require(!Property(mortgage, 1).Mortgaged && mortgage.Snapshot.ActivePlayer!.Money == 1497,
            "Unmortgaging omitted its 10% interest.");
        var utility = Started();
        Change(utility, data => data.Properties.Single(p => p.SpaceIndex == 4).OwnerId = data.Players[0].Id);
        Act(utility, "mp-manage"); Act(utility, "mp-mortgage"); Act(utility, "mp-unmortgage");
        Require(utility.Snapshot.ActivePlayer!.Money == 1492 && !Property(utility, 4).Mortgaged,
            "A utility mortgage failed to round its $7.50 redemption interest up to $8.");
    }

    private static void CheckDebtAndBankruptcy()
    {
        var game = Started(new MonopolyDice(1, 1));
        Change(game, data => { data.Players[0].Position = 20; data.Players[0].Money = 50;
            foreach (var property in data.Properties.Where(p => p.SpaceIndex is 21 or 22)) property.OwnerId = data.Players[1].Id;
            data.Properties.Single(p => p.SpaceIndex == 22).Houses = 5;
            data.Properties.Single(p => p.SpaceIndex == 21).Houses = 5;
        });
        Act(game, "mp-roll");
        Require(game.Snapshot.Phase == MonopolyPhase.Debt && game.Snapshot.DebtAmount == 2000 &&
            game.Snapshot.DebtPlayerId == game.Snapshot.Players[0].Id &&
            game.Snapshot.DebtCreditorId == game.Snapshot.Players[1].Id && !game.HandleAction("mp-end-turn", Next()),
            "Unpayable Royal Arcade rent did not enter creditor-specific debt.");
        Act(game, "mp-bankrupt");
        Require(game.Snapshot.Players[0].Bankrupt && game.Snapshot.Players[0].Money == 0 &&
            game.Snapshot.Players[1].Money == 1550 && game.Snapshot.Phase == MonopolyPhase.GameOver &&
            game.Snapshot.WinnerId == game.Snapshot.Players[1].Id,
            "Bankruptcy failed to transfer remaining cash and declare the surviving winner.");
        Require(!game.Tick(Next()) && !game.HandleAction("mp-roll", Next()), "A completed game continued taking turns.");

        var recovery = Started(new MonopolyDice(1, 2));
        Change(recovery, data => { data.Players[0].Position = 7; data.Players[0].Money = 100;
            data.Properties.Single(p => p.SpaceIndex == 22).OwnerId = data.Players[0].Id; });
        Act(recovery, "mp-roll");
        Require(recovery.Snapshot.Phase == MonopolyPhase.Debt && recovery.Snapshot.DebtAmount == 200,
            "Insufficient cash for Market Levy bypassed debt.");
        Act(recovery, "mp-manage"); Act(recovery, "mp-mortgage");
        Require(recovery.Snapshot.Phase == MonopolyPhase.AwaitingEndTurn && recovery.Snapshot.ActivePlayer!.Money == 100 &&
            Property(recovery, 22).Mortgaged && recovery.Snapshot.DebtAmount == 0,
            "Mortgage proceeds did not settle the tax debt once and resume the turn.");
    }

    private static void CheckSaveValidationAndIsolation()
    {
        var game = Started(new MonopolyDice(1, 2)); Act(game, "mp-roll"); Act(game, "mp-buy");
        string save = game.ExportSave();
        var restored = new MonopolyGame(seed: 13); restored.LoadSave(save, Next());
        Require(restored.ExportSave() == save && restored.Snapshot.Players.SequenceEqual(game.Snapshot.Players) &&
            restored.Snapshot.Properties.SequenceEqual(game.Snapshot.Properties) &&
            restored.Snapshot.AvailableActions.SequenceEqual(game.Snapshot.AvailableActions),
            "A save round trip changed the turn, ownership, cash or available actions.");
        var frozen = game.Snapshot;
        Throws<NotSupportedException>(() => ((IList<MonopolyPlayerSnapshot>)frozen.Players)[0] = frozen.Players[0] with { Money = 0 },
            "The public player snapshot was mutable.");
        Throws<NotSupportedException>(() => ((IList<MonopolyPropertySnapshot>)frozen.Properties)[0] = frozen.Properties[0] with { OwnerId = null },
            "The public property snapshot was mutable.");
        Act(game, "mp-end-turn");
        Require(frozen.ActivePlayerIndex == 0 && frozen.Players[0].Money == 1440 && frozen.Revision < game.Snapshot.Revision,
            "A previously obtained snapshot changed after the next turn.");

        InvalidSave(data => data.Version = 99);
        InvalidSave(data => data.Players[0].Money = -1);
        InvalidSave(data => data.Players[0].Position = 40);
        InvalidSave(data => data.Players[1].Id = data.Players[0].Id);
        InvalidSave(data => data.ActivePlayerIndex = data.Players.Count);
        InvalidSave(data => data.Properties[0].OwnerId = 999);
        InvalidSave(data => data.Properties[0].Houses = 6);
        InvalidSave(data => data.Properties.RemoveAt(0));
        InvalidSave(data => data.Phase = MonopolyPhase.Auction);
        InvalidSave(data => data.Dice = new(0, 6));
        InvalidSave(data => data.Players[0] = null!);
        InvalidSave(data => data.Properties[0] = null!);
        InvalidSave(data => data.Payments.Add(null!));
        InvalidSave(data => data.ChanceFreeCardHolderId = data.Players[0].Id);
        var before = restored.Snapshot;
        Throws<FormatException>(() => restored.LoadSave("{not-json}", Next()), "Malformed save JSON was accepted.");
        Require(ReferenceEquals(before, restored.Snapshot), "A malformed save partially replaced a valid game.");

        void InvalidSave(Action<MonopolySaveData> corrupt)
        {
            var data = JsonSerializer.Deserialize<MonopolySaveData>(save, Json)!;
            corrupt(data);
            var previous = restored.Snapshot;
            Throws<FormatException>(() => restored.LoadSave(JsonSerializer.Serialize(data, Json), Next()), "Invalid save data was accepted.");
            Require(ReferenceEquals(previous, restored.Snapshot), "Rejecting a save partially changed the live game.");
        }
    }

    private static void CheckCardsAndEliminatedAi()
    {
        var utility = Started(new MonopolyDice(1, 2), new(2, 3));
        Change(utility, data => { data.Players[0].Position = 4; data.ChanceDeck = Deck(7);
            data.Properties.Single(p => p.SpaceIndex == 27).OwnerId = data.Players[1].Id; });
        Act(utility, "mp-roll");
        Require(utility.Snapshot.Players[0].Position == 27 && utility.Snapshot.Players[0].Money == 1450 &&
            utility.Snapshot.Players[1].Money == 1550, "The nearest-utility City Charter card did not use a fresh roll at ten times its total.");
        var advance = Started(new MonopolyDice(1, 2));
        Change(advance, data => { data.Players[0].Position = 4; data.ChanceDeck = Deck(0); });
        Act(advance, "mp-roll");
        Require(advance.Snapshot.Players[0].Position == 0 && advance.Snapshot.Players[0].Money == 1700,
            "Advance to Crown Gate did not move to Crown Gate and pay $200 once.");
        var backThree = Started(new MonopolyDice(1, 3));
        Change(backThree, data => { data.Players[0].Position = 32; data.ChanceDeck = Deck(8); data.ChestDeck = Deck(1); });
        Act(backThree, "mp-roll");
        Require(backThree.Snapshot.Players[0].Position == 13 && backThree.Snapshot.Players[0].InJail && backThree.Snapshot.Players[0].Money == 1500,
            "The backward diversion failed to resolve Civic Review or incorrectly paid the circuit grant.");

        var heldCard = Started(new MonopolyDice(1, 1));
        Change(heldCard, data => data.ChestDeck = Deck(4));
        Act(heldCard, "mp-roll");
        var heldData = Data(heldCard);
        Require(heldCard.Snapshot.Players[0].GetOutOfJailCards == 1 && heldData.ChestFreeCardHolderId == heldData.Players[0].Id,
            "Drawing a Jail Free card did not preserve the card's player and deck ownership.");
        Change(heldCard, data => { data.Players[0].InJail = true; data.Players[0].Position = 13;
            data.Phase = MonopolyPhase.AwaitingRoll; data.ExtraRoll = false; data.DoublesCount = 0; });
        Act(heldCard, "mp-jail-card");
        var returned = Data(heldCard);
        Require(returned.ChestFreeCardHolderId is null && returned.Players[0].GetOutOfJailCards == 0 &&
            returned.ChestDeck[^1] == 4 && returned.ChestIndex == 0,
            "Using a held Jail Free card failed to return it to the bottom of its original deck.");

        var birthday = Started(new MonopolyDice(1, 1));
        Change(birthday, data => { data.Players[1].Money = 5; data.ChestDeck = Deck(8); });
        Act(birthday, "mp-roll");
        Require(birthday.Snapshot.Phase == MonopolyPhase.Debt && birthday.Snapshot.ActivePlayerIndex == 0 &&
            birthday.Snapshot.DebtPlayerId == birthday.Snapshot.Players[1].Id && birthday.Snapshot.DebtAmount == 10,
            "A guild-fair obligation was assigned to the active player instead of the owing opponent.");
        Act(birthday, "mp-bankrupt");
        Require(birthday.Snapshot.Players[1].Bankrupt && birthday.Snapshot.Players[0].Money == 1505 &&
            birthday.Snapshot.WinnerId == birthday.Snapshot.Players[0].Id,
            "An off-turn debtor's bankruptcy transferred cash to the wrong player.");

        var eliminated = new MonopolyGame(seed: 41);
        Act(eliminated, "mp-start-game"); Act(eliminated, "mp-human-minus");
        Act(eliminated, "mp-ai-plus"); Act(eliminated, "mp-ai-plus"); Act(eliminated, "mp-start");
        Change(eliminated, data => { data.Players[0].Money = 0; data.Phase = MonopolyPhase.Debt;
            data.DebtAmount = 200; data.DebtPlayerId = data.Players[0].Id; data.DebtCreditorId = null;
            data.Payments = [new() { PlayerId = data.Players[0].Id, Amount = 200 }];
            var asset = data.Properties.Single(p => p.SpaceIndex == 1); asset.OwnerId = data.Players[0].Id; asset.Mortgaged = true; });
        var time = Next();
        for (int step = 0; step < 35 && eliminated.Snapshot.ActivePlayerIndex == 0; step++)
        {
            time += TimeSpan.FromSeconds(2); Require(eliminated.Tick(time), "An eliminated AI stalled an active turn or bank auction.");
        }
        Require(eliminated.Snapshot.Players[0].Bankrupt && eliminated.Snapshot.ActivePlayerIndex != 0 &&
            eliminated.Snapshot.Phase == MonopolyPhase.AwaitingRoll && Property(eliminated, 1).OwnerId != eliminated.Snapshot.Players[0].Id,
            "An AI bankruptcy failed to auction bank assets and move play to a surviving player.");
    }

    private static void CheckAiAndSeededGames()
    {
        var first = Started(); var second = Started();
        Change(first, data => { foreach (var player in data.Players) player.IsAi = true; data.Humans = 0; data.Ais = 2; });
        Change(second, data => { foreach (var player in data.Players) player.IsAi = true; data.Humans = 0; data.Ais = 2; });
        var now = Next();
        int steps = 0;
        for (; steps < 500 && first.Snapshot.Phase != MonopolyPhase.GameOver; steps++)
        {
            now += TimeSpan.FromSeconds(2);
            bool changed = first.Tick(now);
            Require(second.Tick(now) == changed, "Identically seeded AI games advanced differently.");
            Require(first.ExportSave() == second.ExportSave(), "Identically seeded AI games made different game decisions.");
            Require(first.Snapshot.Players.All(p => p.Money >= 0 && p.Position is >= 0 and < 40),
                "AI play produced negative cash or an invalid board position.");
            Require(first.Snapshot.Properties.All(p => p.Houses is >= 0 and <= 5 && (!p.Mortgaged || p.Houses == 0)),
                "AI play produced an invalid development or mortgage state.");
            Require(first.Snapshot.Phase is not (MonopolyPhase.Landing or MonopolyPhase.Setup or MonopolyPhase.ExitConfirmation or MonopolyPhase.Saving),
                "AI gameplay wandered into setup or exit controls.");
            Require(!first.Tick(now), "The same frame advanced AI twice.");
        }
        Require(steps >= 20 && first.Snapshot.TurnNumber > 3 && first.Snapshot.Properties.Any(p => p.OwnerId is not null),
            "AI play stalled before completing turns and buying properties.");
    }

    private static MonopolyGame Jailed(params MonopolyDice[] rolls)
    {
        var game = Started(rolls);
        Change(game, data => { data.Players[0].InJail = true; data.Players[0].Position = 13; });
        return game;
    }
    private static MonopolyGame Started(params MonopolyDice[] rolls)
    {
        var game = new MonopolyGame(seed: 13, initialRolls: rolls);
        Act(game, "mp-start-game"); Act(game, "mp-ai-minus"); Act(game, "mp-human-plus"); Act(game, "mp-start");
        return game;
    }
    private static MonopolyPropertySnapshot Property(MonopolyGame game, int index) =>
        game.Snapshot.Properties.Single(property => property.SpaceIndex == index);
    private static void Change(MonopolyGame game, Action<MonopolySaveData> change)
    {
        var data = Data(game);
        change(data); game.LoadSave(JsonSerializer.Serialize(data, Json), Next());
    }
    private static MonopolySaveData Data(MonopolyGame game) => JsonSerializer.Deserialize<MonopolySaveData>(game.ExportSave(), Json)!;
    private static List<int> Deck(int first) => new[] { first }.Concat(Enumerable.Range(0, 16).Where(card => card != first)).ToList();
    private static DateTimeOffset Next() => Epoch.AddMilliseconds(++_milliseconds * 20);
    private static void Act(MonopolyGame game, string id) => Require(game.HandleAction(id, Next()),
        $"Legal Monopoly action {id} was rejected during {game.Snapshot.Phase}: {game.Snapshot.Status}");
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); options.Converters.Add(new JsonStringEnumConverter()); return options;
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
