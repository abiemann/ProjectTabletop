namespace ProjectTabletop.Interaction;

public sealed partial class MonopolyGame
{
    private void ReturnJailCard(bool chance)
    {
        var deck = chance ? _state.ChanceDeck : _state.ChestDeck;
        int index = chance ? _state.ChanceIndex : _state.ChestIndex;
        int cardId = chance ? 15 : 4;
        var returned = deck.Skip(index).Concat(deck.Take(index)).Where(card => card != cardId).Append(cardId).ToList();
        if (chance)
        {
            _state.ChanceDeck = returned;
            _state.ChanceIndex = 0;
            _state.ChanceFreeCardHolderId = null;
        }
        else
        {
            _state.ChestDeck = returned;
            _state.ChestIndex = 0;
            _state.ChestFreeCardHolderId = null;
        }
    }

    private void DrawCard(bool chance)
    {
        int card;
        do
        {
            if (chance)
            {
                card = _state.ChanceDeck[_state.ChanceIndex];
                _state.ChanceIndex = (_state.ChanceIndex + 1) % 16;
            }
            else
            {
                card = _state.ChestDeck[_state.ChestIndex];
                _state.ChestIndex = (_state.ChestIndex + 1) % 16;
            }
        }
        while (card == (chance ? 15 : 4) && (chance ? _state.ChanceFreeCardHolderId : _state.ChestFreeCardHolderId) is not null);

        if (chance) DrawChance(card); else DrawChest(card);
    }

    private void Card(string text)
    {
        _state.LastCard = text;
        _state.Status = $"{Active.Name}: {text}";
    }
    private void Receive(int amount)
    {
        Active.Money += amount;
        FinishLanding();
    }
    private void CardMove(int destination, string text)
    {
        Card(text);
        MoveTo(destination);
        ResolveLanding();
    }

    private void DrawChance(int card)
    {
        switch (card)
        {
            case 0: CardMove(CrownGateIndex, "The city opens a new trading season. Return to Crown Gate for 200 crowns."); break;
            case 1: CardMove(34, "A spice consortium invites you to Suncrest Avenue. Take the circuit grant if you cross Crown Gate."); break;
            case 2: CardMove(26, "A new garden market opens at Starling Gardens. Take the circuit grant if you cross Crown Gate."); break;
            case 3: CardMove(22, "Present your proposal at Royal Arcade. Travel there by the boulevard."); break;
            case 4: CardMove(5, "Your cargo awaits at North Ferry. Take the circuit grant if you cross Crown Gate."); break;
            case 5:
            case 6:
                Card("An express freight charter takes you to the next transit route. Its owner charges double passage.");
                MoveTo(NextSpaceOfKind(MonopolySpaceKind.Railroad));
                ResolveLanding(railroadMultiplier: 2);
                break;
            case 7:
                Card("An urgent city-service call takes you to the next service. If owned, roll fresh dice and pay ten crowns per pip.");
                MoveTo(NextSpaceOfKind(MonopolySpaceKind.Utility));
                ResolveLanding(specialUtility: true);
                break;
            case 8:
                Card("Roadworks divert your carriage three stops backward. Resolve the new stop.");
                MoveBy(-3, collectGo: false);
                ResolveLanding();
                break;
            case 9:
                Card("A charter review summons you directly to Civic Watch. No circuit grant is paid.");
                SendToJail(_state.LastCard);
                break;
            case 10: Card("Your ferry syndicate distributes a surplus. Receive 50 crowns."); Receive(50); break;
            case 11: Card("The guild repays your market-hall bond. Receive 150 crowns."); Receive(150); break;
            case 12:
                Card("Renew your fire wardens: 25 crowns per shop and 100 per grand hall.");
                Charge(Active.Id, RepairCost(25, 100), null, "finish");
                break;
            case 13: Card("A hurried delivery damaged a lantern. Pay 15 crowns for its repair."); Charge(Active.Id, 15, null, "finish"); break;
            case 14:
                Card("Sponsor the merchant assembly. Pay each other company 50 crowns.");
                foreach (var player in _state.Players.Where(p => !p.Bankrupt && p.Id != Active.Id))
                    _state.Payments.Add(new() { PlayerId = Active.Id, CreditorId = player.Id, Amount = 50 });
                _state.DebtContinuation = "finish";
                ProcessPayments();
                break;
            case 15:
                Card("Safe-Conduct Pass. Retain it to leave Civic Watch without paying clearance.");
                _state.ChanceFreeCardHolderId = Active.Id;
                Active.GetOutOfJailCards++;
                FinishLanding();
                break;
        }
    }

    private void DrawChest(int card)
    {
        switch (card)
        {
            case 0: CardMove(CrownGateIndex, "The city opens a new trading season. Return to Crown Gate for 200 crowns."); break;
            case 1: Card("A forgotten warehouse account is settled. Receive 200 crowns."); Receive(200); break;
            case 2: Card("Fund the harbor first-aid station. Pay 50 crowns."); Charge(Active.Id, 50, null, "finish"); break;
            case 3: Card("Your surplus silk sells at the night market. Receive 50 crowns."); Receive(50); break;
            case 4:
                Card("Safe-Conduct Pass. Retain it to leave Civic Watch without paying clearance.");
                _state.ChestFreeCardHolderId = Active.Id;
                Active.GetOutOfJailCards++;
                FinishLanding();
                break;
            case 5: Card("A charter review summons you directly to Civic Watch. No circuit grant is paid."); SendToJail(_state.LastCard); break;
            case 6: Card("The lantern festival commissions your stalls. Receive 100 crowns."); Receive(100); break;
            case 7: Card("The city returns an unused permit deposit. Receive 20 crowns."); Receive(20); break;
            case 8:
                Card("Your company hosts the guild fair. Each other company contributes 10 crowns.");
                foreach (var player in _state.Players.Where(p => !p.Bankrupt && p.Id != Active.Id))
                    _state.Payments.Add(new() { PlayerId = player.Id, CreditorId = Active.Id, Amount = 10 });
                _state.DebtContinuation = "finish";
                ProcessPayments();
                break;
            case 9: Card("A restored canal brings a civic reward. Receive 100 crowns."); Receive(100); break;
            case 10: Card("Repair the public footbridge beside your warehouses. Pay 100 crowns."); Charge(Active.Id, 100, null, "finish"); break;
            case 11: Card("Sponsor two craft apprentices. Pay 50 crowns."); Charge(Active.Id, 50, null, "finish"); break;
            case 12: Card("A visiting caravan buys your route maps. Receive 25 crowns."); Receive(25); break;
            case 13:
                Card("Resurface your shopfronts: 40 crowns per shop and 115 per grand hall.");
                Charge(Active.Id, RepairCost(40, 115), null, "finish");
                break;
            case 14: Card("Your window display earns a festival ribbon. Receive 10 crowns."); Receive(10); break;
            case 15: Card("An old trading partner settles a debt. Receive 100 crowns."); Receive(100); break;
        }
    }

    private int NextSpaceOfKind(MonopolySpaceKind kind) => Spaces
        .Where(space => space.Kind == kind)
        .OrderBy(space => (space.Index - Active.Position + Spaces.Count) % Spaces.Count)
        .First().Index;

    private int RepairCost(int houseCost, int hotelCost) => Owned(Active.Id).Sum(p => p.Houses == 5 ? hotelCost : p.Houses * houseCost);
}
