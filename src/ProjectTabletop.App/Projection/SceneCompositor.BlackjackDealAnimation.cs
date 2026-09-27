using System.Numerics;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal static readonly TimeSpan BlackjackSweepDuration = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan BlackjackDealCardDuration = TimeSpan.FromMilliseconds(450);
    private BlackjackDeal? _blackjackDeal;
    private int _blackjackDealStage = -1;

    internal sealed record BlackjackMovingCard(BlackjackCard? Card, bool Dealer, int HandIndex,
        int CardIndex, Rect Destination, Vector2 Center, float Rotation, float Progress, bool Departing);
    internal sealed record BlackjackDealFrame(string Phase, int LandedCards,
        IReadOnlyList<BlackjackMovingCard> Cards);

    internal BlackjackDealFrame? BlackjackDealAnimation
    {
        get { lock (_gate) return GetBlackjackDealFrame(_blackjackClock()); }
    }

    private void OnBlackjackDeal(BlackjackDeal deal)
    {
        SynchronizeBlackjackFlights(deal.StartedAt);
        _blackjackFlights.Clear();
        _blackjackDeal = deal;
        _blackjackDealStage = -1;
        _blackjackFlightRevision++;
        _boardSession.HoldBlackjackPresentationUntil(deal.StartedAt + BlackjackDealClearDuration(deal) +
            BlackjackDealCardDuration * 4);
    }

    private static TimeSpan BlackjackDealClearDuration(BlackjackDeal deal) =>
        deal.Previous.DealerCards.Count > 0 || deal.Previous.Hands.Any(hand => hand.Cards.Count > 0)
            ? BlackjackSweepDuration : TimeSpan.Zero;

    private void CancelBlackjackDeal()
    {
        if (_blackjackDeal is null) return;
        _blackjackDeal = null;
        _blackjackDealStage = -1;
        _blackjackFlightRevision++;
    }

    private void SynchronizeBlackjackDeal(DateTimeOffset now)
    {
        if (_blackjackDeal is not { } deal) return;
        var elapsed = now - deal.StartedAt - BlackjackDealClearDuration(deal);
        if (_boardSession.BlackjackState.Revision != deal.Current.Revision ||
            elapsed >= BlackjackDealCardDuration * 4)
        {
            CancelBlackjackDeal();
            return;
        }
        // The static table changes only at the clearing/dealing boundary and
        // each landing. Continuous movement stays in the transparent overlay.
        int stage = elapsed < TimeSpan.Zero ? -1 : (int)(elapsed / BlackjackDealCardDuration);
        if (stage != _blackjackDealStage)
        {
            _blackjackDealStage = stage;
            _blackjackFlightRevision++;
        }
    }

    private BlackjackDealFrame? GetBlackjackDealFrame(DateTimeOffset now)
    {
        SynchronizeBlackjackFlights(now);
        if (_blackjackDeal is not { } deal) return null;
        var elapsed = now > deal.StartedAt ? now - deal.StartedAt : TimeSpan.Zero;
        var clearing = BlackjackDealClearDuration(deal);
        if (elapsed < clearing)
        {
            float progress = Math.Clamp((float)(elapsed / clearing), 0, 1);
            float eased = progress * progress * (3 - 2 * progress);
            var cards = BlackjackSnapshotCards(deal.Previous);
            float distance = (float)cards.Max(card => card.Destination.Right) + 32;
            return new("clearing", 0, cards.Select(card => card with
            {
                Center = card.Center - new Vector2(distance * eased, 0),
                Progress = progress, Departing = true
            }).ToArray());
        }

        var order = BlackjackSnapshotCards(deal.Current)
            .OrderBy(card => card.CardIndex * 2 + (card.Dealer ? 1 : 0)).ToArray();
        double position = (elapsed - clearing) / BlackjackDealCardDuration;
        int landed = Math.Clamp((int)position, 0, order.Length);
        if (landed >= order.Length) return null;
        var next = order[landed];
        float amount = Math.Clamp((float)(position - landed), 0, 1);
        var pose = BlackjackFlightPose(next.Destination, amount);
        return new("dealing", landed, [next with
        {
            Center = pose.Center, Rotation = pose.Rotation, Progress = amount
        }]);
    }

    private static BlackjackMovingCard[] BlackjackSnapshotCards(BlackjackSnapshot game)
    {
        var cards = new List<BlackjackMovingCard>();
        Add(game.DealerCards, new Rect(215, 224, 570, 159), true, -1);
        for (int handIndex = 0; handIndex < game.Hands.Count; handIndex++)
            Add(game.Hands[handIndex].Cards, CasinoPlayerLane(handIndex, game.Hands.Count), false, handIndex);
        return cards.ToArray();

        void Add(IReadOnlyList<BlackjackCard?> values, Rect lane, bool dealer, int handIndex)
        {
            var layout = CasinoCardLayout(values.Count, lane);
            for (int i = 0; i < values.Count; i++)
            {
                var rect = layout[i];
                cards.Add(new(values[i], dealer, handIndex, i, rect,
                    new((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2)), 0, 0, false));
            }
        }
    }

    private static (Vector2 Center, float Rotation) BlackjackFlightPose(Rect destination, float progress)
    {
        float t = 1 - MathF.Pow(1 - progress, 2);
        var start = new Vector2(500, -160);
        var end = new Vector2((float)(destination.X + destination.Width / 2),
            (float)(destination.Y + destination.Height / 2));
        float bend = end.X >= 500 ? 70 : -70;
        var first = new Vector2(500 + bend, 115);
        var second = new Vector2(end.X + bend, end.Y - 190);
        float remaining = 1 - t;
        var center = remaining * remaining * remaining * start + 3 * remaining * remaining * t * first +
            3 * remaining * t * t * second + t * t * t * end;
        return (center, -MathF.PI * 1.25f * remaining * remaining);
    }

    private bool HasBlackjackCardAnimation(DateTimeOffset now)
    {
        SynchronizeBlackjackFlights(now);
        return _blackjackFlights.Count > 0 || _blackjackDeal is not null;
    }
}
