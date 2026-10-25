using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Text;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal static readonly BoardRect GalleryScoreSceneBounds = new(.441, .886, .118, .070);
    private const int GalleryScoreCardWidth = 144, GalleryScoreCardHeight = 208;
    private const double GalleryScoreFlipSeconds = .072, GalleryScoreDigitDelay = .055;
    private CanvasRenderTarget? _galleryScoreDigits;
    private CanvasLinearGradientBrush? _galleryScoreFlapShade;
    private readonly GalleryScoreReel[] _galleryScoreReels = new GalleryScoreReel[4];
    private long _galleryScoreRoundRevision = -1;
    private int _galleryScoreTarget;
    private double _galleryScoreObservedSeconds;

    private struct GalleryScoreReel
    {
        public int Current, Target;
        public double Started;
        public bool Moving;
    }

    private void PrepareGalleryScore(CanvasDevice device)
    {
        if (_galleryScoreDigits is not null) return;
        _galleryScoreDigits = new CanvasRenderTarget(device, GalleryScoreCardWidth * 10, GalleryScoreCardHeight, 96);
        using var draw = _galleryScoreDigits.CreateDrawingSession();
        using var text = new CanvasTextFormat
        {
            FontFamily = "Bahnschrift SemiCondensed", FontSize = 212,
            FontWeight = FontWeights.SemiBold, HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var face = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(255, 35, 36, 35) },
            new() { Position = .48f, Color = Color.FromArgb(255, 20, 22, 21) },
            new() { Position = .52f, Color = Color.FromArgb(255, 30, 32, 30) },
            new() { Position = 1, Color = Color.FromArgb(255, 18, 20, 19) }
        ]) { StartPoint = Vector2.Zero, EndPoint = new(0, GalleryScoreCardHeight) };
        draw.Clear(Color.FromArgb(0, 0, 0, 0));
        for (int digit = 0; digit < 10; digit++)
        {
            float x = digit * GalleryScoreCardWidth;
            var card = new Rect(x + 1, 1, GalleryScoreCardWidth - 2, GalleryScoreCardHeight - 2);
            draw.FillRoundedRectangle(card, 8, 8, face);
            draw.DrawRoundedRectangle(card, 8, 8, Color.FromArgb(255, 67, 65, 57), 1.5f);
            draw.DrawText(digit.ToString(CultureInfo.InvariantCulture),
                new Rect(x, -2, GalleryScoreCardWidth, GalleryScoreCardHeight), Color.FromArgb(255, 247, 248, 244), text);
        }
        _galleryScoreFlapShade = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(0, 0, 0, 0) },
            new() { Position = 1, Color = Color.FromArgb(210, 0, 0, 0) }
        ]);
    }

    private void DisposeGalleryScore()
    {
        _galleryScoreDigits?.Dispose(); _galleryScoreFlapShade?.Dispose();
        _galleryScoreDigits = null; _galleryScoreFlapShade = null;
        _galleryScoreRoundRevision = -1;
    }

    internal long GalleryScoreVisualFrame(DateTimeOffset now)
    {
        if (_boardSession.Screen != BoardScreen.DuckHunt || _boardSession.CurrentDuckHuntView != DuckHuntView.TargetPractice)
        {
            _galleryScoreRoundRevision = -1;
            return 0;
        }
        GalleryLampVisualFrame(now);
        var state = _boardSession.GalleryState;
        double seconds = GalleryLampSeconds;
        if (_galleryScoreRoundRevision != _boardSession.DuckHuntRoundRevision)
        {
            Array.Clear(_galleryScoreReels);
            _galleryScoreTarget = 0;
            _galleryScoreObservedSeconds = seconds;
            _galleryScoreRoundRevision = _boardSession.DuckHuntRoundRevision;
        }
        bool changed = state.Score != _galleryScoreTarget;
        double awardSeconds = Math.Max(_galleryScoreObservedSeconds, seconds - state.ScoreAnimationSeconds);
        for (int position = 0; position < _galleryScoreReels.Length; position++)
        {
            ref var reel = ref _galleryScoreReels[position];
            if (changed)
            {
                // Catch up only to the award under the old target. A sparse
                // preview must not spend an old idle interval on the new score.
                AdvanceGalleryScoreReel(ref reel, awardSeconds);
                int divisor = position switch { 0 => 1000, 1 => 100, 2 => 10, _ => 1 };
                reel.Target = Math.Clamp(state.Score, 0, 9999) / divisor % 10;
                if (reel.Moving && awardSeconds < reel.Started && reel.Current == reel.Target)
                    reel.Moving = false;
                if (!reel.Moving && reel.Current != reel.Target)
                {
                    reel.Started = awardSeconds + (3 - position) * GalleryScoreDigitDelay;
                    reel.Moving = true;
                }
            }
            // A new award redirects the reel, but never swaps the leaf already
            // falling. Finish that leaf, then take the shortest forward cycle
            // to the latest digit without accumulating a queue of old awards.
            AdvanceGalleryScoreReel(ref reel, seconds);
        }
        _galleryScoreTarget = state.Score;
        _galleryScoreObservedSeconds = seconds;
        if (state.Cycle == 0 || _boardSession.DuckHuntDrawerOpen) return 0;
        long frame = 0;
        for (int position = 0; position < _galleryScoreReels.Length; position++)
        {
            var digit = GalleryScoreDigit(position);
            int code = digit.From | digit.To << 4 | (int)Math.Round(digit.Progress * 31) << 8;
            frame |= (long)code << (position * 13);
        }
        return frame;
    }

    private static void AdvanceGalleryScoreReel(ref GalleryScoreReel reel, double seconds)
    {
        while (reel.Moving && seconds >= reel.Started + GalleryScoreFlipSeconds)
        {
            reel.Current = (reel.Current + 1) % 10;
            reel.Started += GalleryScoreFlipSeconds;
            if (reel.Current == reel.Target) reel.Moving = false;
        }
    }

    internal (int From, int To, double Progress) GalleryScoreDigit(int position)
    {
        var reel = _galleryScoreReels[position];
        if (!reel.Moving || GalleryLampSeconds < reel.Started) return (reel.Current, reel.Current, 1);
        return (reel.Current, (reel.Current + 1) % 10,
            Math.Clamp((GalleryLampSeconds - reel.Started) / GalleryScoreFlipSeconds, 0, 1));
    }

    private void DrawGalleryScore(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect)
    {
        GalleryScoreVisualFrame(_blackjackClock());
        if (state.Cycle == 0 || _boardSession.DuckHuntDrawerOpen || _galleryScoreDigits is null) return;
        var bounds = GallerySceneRect(GalleryScoreSceneBounds, aspect);
        double gap = bounds.Width * .025, width = (bounds.Width - 3 * gap) / 4;
        for (int place = 0; place < 4; place++)
        {
            var card = new Rect(bounds.X + place * (width + gap), bounds.Y, width, bounds.Height);
            float cornerX = (float)width * .055f, cornerY = (float)card.Height * .038f;
            drawing.FillRoundedRectangle(new Rect(card.X, card.Y + card.Height * .025, card.Width, card.Height),
                cornerX, cornerY, Color.FromArgb(115, 0, 0, 0));
            var digit = GalleryScoreDigit(place);
            if (digit.From == digit.To)
            {
                DrawGalleryScoreCard(drawing, digit.To, card, 0, 1);
            }
            else
            {
                double half = card.Height / 2, hinge = card.Y + half;
                // The next upper card is behind the falling old upper leaf;
                // the old lower card remains until the reverse face lands.
                DrawGalleryScoreCard(drawing, digit.To, new Rect(card.X, card.Y, width, half), 0, .5);
                DrawGalleryScoreCard(drawing, digit.From, new Rect(card.X, hinge, width, half), .5, .5);
                bool fallingTop = digit.Progress < .5;
                double angle = digit.Progress * Math.PI;
                double leafHeight = half * Math.Abs(Math.Cos(angle));
                double depth = Math.Sin(angle);
                if (leafHeight > .05)
                {
                    var leaf = new Rect(card.X, fallingTop ? hinge - leafHeight : hinge, width, leafHeight);
                    double edgeDepth = card.Height * .016 * depth;
                    drawing.FillRectangle(new Rect(leaf.X, leaf.Y + edgeDepth, leaf.Width, leaf.Height),
                        Color.FromArgb(160, 0, 0, 0));
                    DrawGalleryScoreCard(drawing, fallingTop ? digit.From : digit.To, leaf, fallingTop ? 0 : .5, .5);
                    if (_galleryScoreFlapShade is { } shade)
                    {
                        shade.StartPoint = new((float)leaf.X, (float)(fallingTop ? leaf.Y : leaf.Bottom));
                        shade.EndPoint = new((float)leaf.X, (float)(fallingTop ? leaf.Bottom : leaf.Y));
                        shade.Opacity = (float)(depth * .60);
                        drawing.FillRectangle(leaf, shade);
                    }
                    float edgeY = (float)(fallingTop ? leaf.Y : leaf.Bottom);
                    drawing.DrawLine((float)card.X, edgeY, (float)card.Right, edgeY,
                        Color.FromArgb(190, 104, 103, 94), (float)(card.Height * .007));
                }
            }
            // Individual card hinges, rather than a broad scoreboard plaque,
            // preserve the cabinet wood surrounding the four white numerals.
            float midY = (float)(card.Y + card.Height / 2);
            drawing.DrawLine((float)card.X, midY, (float)card.Right, midY,
                Color.FromArgb(255, 6, 8, 7), (float)(card.Height * .014));
            drawing.DrawLine((float)card.X, midY + (float)(card.Height * .008), (float)card.Right,
                midY + (float)(card.Height * .008), Color.FromArgb(160, 92, 89, 78), (float)(card.Height * .005));
            float pinX = (float)card.Width * .025f, pinY = (float)card.Height * .017f;
            drawing.FillEllipse((float)card.X, midY, pinX, pinY, Color.FromArgb(255, 100, 94, 78));
            drawing.FillEllipse((float)card.Right, midY, pinX, pinY, Color.FromArgb(255, 100, 94, 78));
        }
    }

    private void DrawGalleryScoreCard(CanvasDrawingSession drawing, int digit, Rect destination, double top, double height)
    {
        drawing.DrawImage(_galleryScoreDigits!, destination,
            new Rect(digit * GalleryScoreCardWidth, top * GalleryScoreCardHeight,
                GalleryScoreCardWidth, height * GalleryScoreCardHeight), 1, CanvasImageInterpolation.HighQualityCubic);
    }
}
