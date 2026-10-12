using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.Football;

internal sealed partial class FootballRenderer
{
    internal const double VictoryDurationSeconds = 10;
    private const int ConfettiCount = 2400;
    private static readonly ConfettiPiece[] Confetti = MakeConfetti();
    private static readonly Vector4[] ConfettiColors =
    [
        new(1, .14f, .19f, 1), new(.97f, .98f, 1, 1),
        new(.10f, .39f, 1, 1), new(1, .73f, .14f, 1)
    ];
    private CanvasRenderTarget? _confettiPaper;
    private int _confettiWinner = -1;
    private double _confettiLastSeconds = -1;
    private Vector2 _confettiCar;

    private readonly record struct ConfettiPiece(Vector2 Landing, float Launch, float Flight,
        float Height, float Phase, float Spin, float Width, float Length, int Color, int Cannon, bool AtCar);

    private void DrawVictory(CanvasDrawingSession ds, FootballSnapshot state, double seconds)
    {
        int winner = state.Score1 >= FootballGame.WinningScore ? 0 :
            state.Score2 >= FootballGame.WinningScore ? 1 : -1;
        if (state.Phase != FootballPhase.Finished || winner < 0 || seconds < 0 || !double.IsFinite(seconds))
        {
            _confettiWinner = -1;
            _confettiLastSeconds = -1;
            return;
        }
        seconds = Math.Min(seconds, VictoryDurationSeconds);
        float left = winner * 800;
        if (_confettiWinner != winner || seconds < _confettiLastSeconds)
        {
            _confettiWinner = winner;
            _confettiCar = FieldPoint(state.Kickers[winner].Position);
            _confettiCar = Vector2.Clamp(_confettiCar, new(left + 80, 85), new(left + 720, 915));
        }
        _confettiLastSeconds = seconds;
        EnsureConfettiPaper();

        // These are physical half-pitch bounds, not a decorative opacity fade.
        // Both launch trajectories and their final pile belong to the winner.
        using var half = ds.CreateLayer(1, new Rect(left + 3, 3, 794, 994));

        // One immutable particle set and two GPU sprite batches. No per-piece
        // paths, brushes, images or frame-history allocations in the draw loop.
        // Ground shadows first; the paper pass is after cars and goal frames.
        for (int pass = 0; pass < 2; pass++)
        {
            using var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
            foreach (ref readonly var piece in Confetti.AsSpan())
            {
                float age = (float)seconds - piece.Launch;
                if (age < 0) continue;
                float p = Math.Clamp(age / piece.Flight, 0, 1);
                float flightAge = Math.Min(age, piece.Flight);
                float loft = 4 * piece.Height * p * (1 - p);
                // Opposing jets enter from beyond the turf; only the paper is visible.
                var launch = new Vector2(left + (piece.Cannon == 0 ? 390 : 410), piece.Cannon == 0 ? -95 : 1095);
                var landing = piece.AtCar
                    ? _confettiCar + piece.Landing * new Vector2(58, 50)
                    : new Vector2(left + 30 + piece.Landing.X * 740, 36 + piece.Landing.Y * 928);
                landing = Vector2.Clamp(landing, new(left + 24, 28), new(left + 776, 972));
                // Drag slows the sideways jet; a parabolic rise and fall plus
                // irregular flutter gives each scrap a different landing path.
                float travel = (1 - MathF.Exp(-2.1f * p)) / (1 - MathF.Exp(-2.1f));
                var ground = Vector2.Lerp(launch, landing, travel);
                float flutter = MathF.Sin(MathF.PI * p);
                ground += new Vector2(MathF.Sin(piece.Phase + flightAge * 4.1f) * 13,
                    MathF.Cos(piece.Phase + flightAge * 3.3f) * 9) * flutter;
                float angle = piece.Phase + piece.Spin * flightAge;
                float roll = .24f + .76f * MathF.Abs(MathF.Cos(piece.Phase + flightAge * 7.2f));
                if (p >= 1) roll = .76f;
                float length = piece.Length * (1 + loft * .0011f);
                float width = piece.Width * roll;
                Vector2 center;
                Vector4 tint;
                if (pass == 0)
                {
                    center = ground + new Vector2(3 + loft * .15f, 4 + loft * .20f);
                    tint = new(0, 0, 0, p >= 1 ? .21f : .08f);
                }
                else
                {
                    center = ground + new Vector2(-loft * .10f, -loft * .28f);
                    float glint = .73f + .27f * MathF.Abs(MathF.Sin(piece.Phase + flightAge * 6));
                    tint = ConfettiColors[piece.Color] * new Vector4(glint, glint, glint, 1);
                }
                var transform = Matrix3x2.CreateTranslation(-16, -16) *
                    Matrix3x2.CreateScale(length / 28, width / 22) *
                    Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(center);
                batch.Draw(_confettiPaper!, transform, tint);
            }
        }
    }

    private void EnsureConfettiPaper()
    {
        if (_confettiPaper is not null) return;
        _confettiPaper = new CanvasRenderTarget(_device, 32, 32, 96);
        using var ds = _confettiPaper.CreateDrawingSession();
        ds.Clear(Ink(0, 0, 0, 0));
        ds.FillRoundedRectangle(new Rect(2, 5, 28, 22), .7f, .7f, Ink(236, 239, 242));
        ds.FillRectangle(new Rect(2, 5, 28, 9), Ink(255, 255, 255));
        ds.DrawLine(3, 15, 29, 16, Ink(178, 184, 193), 1);
        ds.DrawLine(3, 25.5f, 29, 25.5f, Ink(198, 204, 214), 1);
    }

    private static ConfettiPiece[] MakeConfetti()
    {
        var random = new Random(41873);
        var pieces = new ConfettiPiece[ConfettiCount];
        float Next(float min, float max) => min + (max - min) * random.NextSingle();
        for (int i = 0; i < pieces.Length; i++)
        {
            // A large opening salvo followed by two dense follow-up volleys.
            int salvo = i < 1400 ? 0 : i < 2000 ? 1 : 2;
            bool atCar = i % 5 == 0;
            var landing = new Vector2(Next(0, 1), Next(0, 1));
            if (atCar)
            {
                // A natural mound with scattered outliers, without a box-shaped edge.
                float radius = MathF.Sqrt(-2 * MathF.Log(MathF.Max(.0001f, landing.X)));
                float angle = landing.Y * MathF.Tau;
                landing = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            }
            pieces[i] = new(landing, salvo * .78f + Next(0, .36f),
                Next(2.5f, 5.8f), Next(150, 420), Next(0, MathF.Tau), Next(-7, 7),
                Next(3.5f, 7), i % 9 == 0 ? Next(22, 36) : Next(7, 13),
                (i / 2) % 4, i % 2, atCar);
        }
        return pieces;
    }

}
