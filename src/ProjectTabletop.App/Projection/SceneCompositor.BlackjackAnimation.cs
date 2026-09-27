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
            var flights = GetBlackjackFlights(_blackjackClock());
            return new { durationMilliseconds = BlackjackFlightDuration.TotalMilliseconds,
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
            if (_blackjackFlights.Count > 0) _blackjackFlightRevision++;
            _blackjackFlights.Clear();
            _blackjackFlightNavigationRevision = _boardSession.Revision;
        }
        if (_blackjackFlights.RemoveAll(hit => now - hit.StartedAt >= BlackjackFlightDuration ||
            hit.RoundNumber != game.RoundNumber || hit.HandIndex >= game.Hands.Count ||
            hit.CardIndex >= game.Hands[hit.HandIndex].Cards.Count ||
            game.Hands[hit.HandIndex].Cards[hit.CardIndex] != hit.Card) > 0)
            _blackjackFlightRevision++;
    }

    private bool TickBlackjackVisuals(DateTimeOffset now)
    {
        SynchronizeBlackjackFlights(now);
        // Let the last player card land before the dealer reveals or draws.
        return _blackjackFlights.Count == 0 && _boardSession.TickBlackjack(now);
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
            float t = 1 - MathF.Pow(1 - progress, 2); // Fast departure, gentle landing.
            var start = new Vector2(500, -160); // Entire rotated card begins above TABLETOP and outside the board.
            var end = new Vector2((float)(destination.X + destination.Width / 2),
                (float)(destination.Y + destination.Height / 2));
            float bend = end.X >= 500 ? 70 : -70;
            var first = new Vector2(500 + bend, 115);
            var second = new Vector2(end.X + bend, end.Y - 190);
            float remaining = 1 - t;
            var center = remaining * remaining * remaining * start + 3 * remaining * remaining * t * first +
                3 * remaining * t * t * second + t * t * t * end;
            float rotation = -MathF.PI * 1.25f * remaining * remaining;
            return new BlackjackFlightFrame(hit, destination, center, rotation, progress);
        }).ToArray();
    }

    private static IReadOnlyDictionary<int, IReadOnlySet<int>> HiddenBlackjackCards(
        IReadOnlyList<BlackjackFlightFrame> flights) => flights.GroupBy(frame => frame.Hit.HandIndex)
        .ToDictionary(group => group.Key, group => (IReadOnlySet<int>)group.Select(frame => frame.Hit.CardIndex).ToHashSet());

    // Only moving cards redraw each frame. The expensive felt, labels, controls
    // and settled cards remain cached on both the projector and laptop.
    private CanvasRenderTarget? DrawBlackjackFlightLayer(CanvasDevice device, IReadOnlyList<BlackjackFlightFrame> flights)
    {
        if (flights.Count == 0) return null;
        if (_blackjackFlightTarget is null || _blackjackFlightTarget.Device != device)
        {
            _blackjackFlightTarget?.Dispose();
            _blackjackFlightTarget = new CanvasRenderTarget(device, BoardSurfaceSize, BoardSurfaceSize, 96);
        }
        using var drawing = _blackjackFlightTarget.CreateDrawingSession();
        drawing.Clear(Colors.Transparent);
        foreach (var frame in flights)
        {
            var transform = drawing.Transform;
            drawing.Transform = Matrix3x2.CreateRotation(frame.Rotation) *
                Matrix3x2.CreateTranslation(frame.Center) * transform;
            try
            {
                DrawCasinoCard(drawing, frame.Hit.Card,
                    new Rect(-frame.Destination.Width / 2, -frame.Destination.Height / 2,
                        frame.Destination.Width, frame.Destination.Height));
            }
            finally { drawing.Transform = transform; }
        }
        return _blackjackFlightTarget;
    }
}
