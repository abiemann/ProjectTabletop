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
            case 0: CardMove(0, "Advance to GO. Collect $200."); break;
            case 1: CardMove(24, "Advance to Illinois Avenue. Collect $200 if you pass GO."); break;
            case 2: CardMove(11, "Advance to St. Charles Place. Collect $200 if you pass GO."); break;
            case 3: CardMove(39, "Advance to Boardwalk."); break;
            case 4: CardMove(5, "Take a trip to Reading Railroad. Collect $200 if you pass GO."); break;
            case 5:
            case 6:
                Card("Advance to the nearest Railroad. Pay twice the usual rent if owned.");
                int railroad = new[] { 5, 15, 25, 35 }.FirstOrDefault(i => i > Active.Position);
                MoveTo(railroad == 0 ? 5 : railroad);
                ResolveLanding(railroadMultiplier: 2);
                break;
            case 7:
                Card("Advance to the nearest Utility. If owned, roll and pay ten times the total.");
                MoveTo(Active.Position < 12 || Active.Position >= 28 ? 12 : 28);
                ResolveLanding(specialUtility: true);
                break;
            case 8:
                Card("Go back three spaces.");
                MoveBy(-3, collectGo: false);
                ResolveLanding();
                break;
            case 9:
                Card("Go directly to Jail. Do not collect $200.");
                SendToJail(_state.LastCard);
                break;
            case 10: Card("Bank dividend. Collect $50."); Receive(50); break;
            case 11: Card("Your building loan matures. Collect $150."); Receive(150); break;
            case 12:
                Card("General repairs: pay $25 per house and $100 per hotel.");
                Charge(Active.Id, RepairCost(25, 100), null, "finish");
                break;
            case 13: Card("Speeding fine. Pay $15."); Charge(Active.Id, 15, null, "finish"); break;
            case 14:
                Card("Elected chairman of the board. Pay each player $50.");
                foreach (var player in _state.Players.Where(p => !p.Bankrupt && p.Id != Active.Id))
                    _state.Payments.Add(new() { PlayerId = Active.Id, CreditorId = player.Id, Amount = 50 });
                _state.DebtContinuation = "finish";
                ProcessPayments();
                break;
            case 15:
                Card("Get Out of Jail Free. Keep this card until used.");
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
            case 0: CardMove(0, "Advance to GO. Collect $200."); break;
            case 1: Card("Bank error in your favor. Collect $200."); Receive(200); break;
            case 2: Card("Doctor's fee. Pay $50."); Charge(Active.Id, 50, null, "finish"); break;
            case 3: Card("Sale of stock. Collect $50."); Receive(50); break;
            case 4:
                Card("Get Out of Jail Free. Keep this card until used.");
                _state.ChestFreeCardHolderId = Active.Id;
                Active.GetOutOfJailCards++;
                FinishLanding();
                break;
            case 5: Card("Go directly to Jail. Do not collect $200."); SendToJail(_state.LastCard); break;
            case 6: Card("Holiday fund matures. Collect $100."); Receive(100); break;
            case 7: Card("Income tax refund. Collect $20."); Receive(20); break;
            case 8:
                Card("It's your birthday. Collect $10 from every player.");
                foreach (var player in _state.Players.Where(p => !p.Bankrupt && p.Id != Active.Id))
                    _state.Payments.Add(new() { PlayerId = player.Id, CreditorId = Active.Id, Amount = 10 });
                _state.DebtContinuation = "finish";
                ProcessPayments();
                break;
            case 9: Card("Life insurance matures. Collect $100."); Receive(100); break;
            case 10: Card("Hospital fees. Pay $100."); Charge(Active.Id, 100, null, "finish"); break;
            case 11: Card("School fees. Pay $50."); Charge(Active.Id, 50, null, "finish"); break;
            case 12: Card("Consultancy fee. Collect $25."); Receive(25); break;
            case 13:
                Card("Street repairs: pay $40 per house and $115 per hotel.");
                Charge(Active.Id, RepairCost(40, 115), null, "finish");
                break;
            case 14: Card("Second prize in a beauty contest. Collect $10."); Receive(10); break;
            case 15: Card("You inherit $100."); Receive(100); break;
        }
    }

    private int RepairCost(int houseCost, int hotelCost) => Owned(Active.Id).Sum(p => p.Houses == 5 ? hotelCost : p.Houses * houseCost);
}
