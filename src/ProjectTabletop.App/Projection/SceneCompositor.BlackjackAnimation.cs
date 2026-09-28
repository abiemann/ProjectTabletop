using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly TimeSpan BlackjackFlightDuration = TimeSpan.FromMilliseconds(720);
    private readonly Func<DateTimeOffset> _blackjackClock;
    private readonly List<BlackjackHit> _blackjackFlights = [];
    private long _blackjackFlightNavigationRevision = -1;
    private long _blackjackFlightRevision;
    private CanvasRenderTarget? _blackjackFlightTarget;

    internal sealed record BlackjackFlightFrame(BlackjackHit Hit, Rect Destination,
        Vector2 Center, float Rotation, float Progress);

    internal IReadOnlyList<BlackjackFlightFrame> BlackjackAnimation
    {
        get { lock (_gate) return GetBlackjackFlights(_blackjackClock()); }
    }

    public object GetBlackjackAnimationDiagnostics()
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            var flights = GetBlackjackFlights(now);
            var deal = GetBlackjackDealFrame(now);
            return new { durationMilliseconds = BlackjackFlightDuration.TotalMilliseconds,
                dealCardMilliseconds = BlackjackDealCardDuration.TotalMilliseconds,
                sweepMilliseconds = BlackjackSweepDuration.TotalMilliseconds,
                deal = deal is null ? null : new
                {
                    deal.Phase, deal.LandedCards,
                    cards = deal.Cards.Select(frame => new
                    {
                        frame.Dealer, frame.HandIndex, frame.CardIndex, frame.Departing, frame.Progress,
                        center = new { x = frame.Center.X, y = frame.Center.Y },
                        rotationDegrees = frame.Rotation * 180 / MathF.PI,
                        destination = new { frame.Destination.X, frame.Destination.Y,
                            frame.Destination.Width, frame.Destination.Height }
                    }).ToArray()
                },
                flights = flights.Select(frame => new
                {
                    frame.Hit.Sequence, frame.Hit.HandIndex, frame.Hit.CardIndex, frame.Progress,
                    center = new { x = frame.Center.X, y = frame.Center.Y },
                    rotationDegrees = frame.Rotation * 180 / MathF.PI,
                    destination = new { frame.Destination.X, frame.Destination.Y,
                        frame.Destination.Width, frame.Destination.Height }
                }).ToArray() };
        }
    }

    // BoardSession raises this only for an accepted HIT, after updating the
    // model. All caller paths hold _gate; rendering never changes the rules.
    private void OnBlackjackHit(BlackjackHit hit)
    {
        SynchronizeBlackjackFlights(hit.StartedAt);
        _blackjackFlights.Add(hit);
        _blackjackFlightRevision++;
    }

    private void SynchronizeBlackjackFlights(DateTimeOffset now)
    {
        var game = _boardSession.BlackjackState;
        if (_blackjackFlightNavigationRevision != _boardSession.Revision ||
            _boardSession.Screen != BoardScreen.Blackjack)
        {
            if (_blackjackFlights.Count > 0 || _blackjackDeal is not null) _blackjackFlightRevision++;
            _blackjackFlights.Clear();
            _blackjackDeal = null;
            _blackjackDealStage = -1;
            _blackjackFlightNavigationRevision = _boardSession.Revision;
        }
        if (_blackjackFlights.RemoveAll(hit => now - hit.StartedAt >= BlackjackFlightDuration ||
            hit.RoundNumber != game.RoundNumber || hit.HandIndex >= game.Hands.Count ||
            hit.CardIndex >= game.Hands[hit.HandIndex].Cards.Count ||
            game.Hands[hit.HandIndex].Cards[hit.CardIndex] != hit.Card) > 0)
            _blackjackFlightRevision++;
        SynchronizeBlackjackDeal(now);
    }

    private bool TickBlackjackVisuals(DateTimeOffset now)
    {
        SynchronizeBlackjackFlights(now);
        // Let the last player card land before the dealer reveals or draws.
        return _blackjackFlights.Count == 0 && _blackjackDeal is null && _boardSession.TickBlackjack(now);
    }

    private BlackjackFlightFrame[] GetBlackjackFlights(DateTimeOffset now)
    {
        SynchronizeBlackjackFlights(now);
        var game = _boardSession.BlackjackState;
        return _blackjackFlights.Select(hit =>
        {
            var lane = CasinoPlayerLane(hit.HandIndex, game.Hands.Count);
            var destination = CasinoCardLayout(game.Hands[hit.HandIndex].Cards.Count, lane)[hit.CardIndex];
            float progress = Math.Clamp((float)((now - hit.StartedAt).TotalMilliseconds /
                BlackjackFlightDuration.TotalMilliseconds), 0, 1);
            var pose = BlackjackFlightPose(destination, progress);
            return new BlackjackFlightFrame(hit, destination, pose.Center, pose.Rotation, progress);
        }).ToArray();
    }

    private static IReadOnlyDictionary<int, IReadOnlySet<int>> HiddenBlackjackCards(
        IReadOnlyList<BlackjackFlightFrame> flights) => flights.GroupBy(frame => frame.Hit.HandIndex)
        .ToDictionary(group => group.Key, group => (IReadOnlySet<int>)group.Select(frame => frame.Hit.CardIndex).ToHashSet());

    // Only moving cards redraw each frame. The expensive felt, labels, controls
    // and settled cards remain cached on both the projector and laptop.
    private CanvasRenderTarget? DrawBlackjackFlightLayer(CanvasDevice device, IReadOnlyList<BlackjackFlightFrame> flights,
        BlackjackDealFrame? deal)
    {
        if (flights.Count == 0 && deal is null) return null;
        EnsureBoardRenderTarget(ref _blackjackFlightTarget, device);
        using var drawing = _blackjackFlightTarget!.CreateDrawingSession();
        drawing.Transform = BoardRasterTransform(_blackjackFlightTarget);
        drawing.Clear(Colors.Transparent);
        var cards = flights.Select(frame => new BlackjackMovingCard(frame.Hit.Card, false,
            frame.Hit.HandIndex, frame.Hit.CardIndex, frame.Destination, frame.Center, frame.Rotation,
            frame.Progress, false)).Concat(deal?.Cards ?? Array.Empty<BlackjackMovingCard>());
        foreach (var frame in cards)
        {
            var transform = drawing.Transform;
            drawing.Transform = Matrix3x2.CreateRotation(frame.Rotation) *
                Matrix3x2.CreateTranslation(frame.Center) * transform;
            try
            {
                DrawCasinoCard(drawing, frame.Card,
                    new Rect(-frame.Destination.Width / 2, -frame.Destination.Height / 2,
                        frame.Destination.Width, frame.Destination.Height));
            }
            finally { drawing.Transform = transform; }
        }
        return _blackjackFlightTarget;
    }
}
