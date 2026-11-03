using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasTextFormat? _duckHuntFlightHeading, _duckHuntFlightCaption;
    private int _duckHuntFlightDrawnScore = -1, _duckHuntFlightDrawnCount = -1;
    private string _duckHuntFlightScoreText = string.Empty, _duckHuntFlightCountText = string.Empty;

    private void PrepareDuckHuntFlightResources()
    {
        _duckHuntFlightHeading ??= new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 40,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        _duckHuntFlightCaption ??= new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 23,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
    }

    private void DisposeDuckHuntFlightResources()
    {
        _duckHuntFlightHeading?.Dispose();
        _duckHuntFlightCaption?.Dispose();
        _duckHuntFlightHeading = _duckHuntFlightCaption = null;
    }

    private void DrawDuckHuntFlight(CanvasDrawingSession drawing, DuckHuntFlightSnapshot state, double aspect)
    {
        if (_duckHuntFlightHeading is not { } heading || _duckHuntFlightCaption is not { } caption) return;
        DrawDuckHuntFlyingMallard(drawing, state, aspect);
        if (_duckHuntFlightDrawnScore != state.Score)
        {
            _duckHuntFlightDrawnScore = state.Score;
            _duckHuntFlightScoreText = $"SCORE  {state.Score}";
        }
        if (_duckHuntFlightDrawnCount != state.BirdsLaunched)
        {
            _duckHuntFlightDrawnCount = state.BirdsLaunched;
            _duckHuntFlightCountText = $"BIRDS  {state.BirdsLaunched} / 10";
        }
        DrawCaption(_duckHuntFlightScoreText, new Rect(30, 25, 210, 52), caption);
        DrawCaption(_duckHuntFlightCountText, new Rect(710, 25, 260, 52), caption);
        if (state.Phase == DuckHuntFlightPhase.Finished)
        {
            DrawCaption("ROUND COMPLETE", new Rect(220, 270, 560, 66), heading);
            DrawCaption($"{state.Score} OF 10 BIRDS", new Rect(240, 342, 520, 45), caption);
        }

        void DrawCaption(string text, Rect rectangle, CanvasTextFormat format)
        {
            DrawBoardAspectText(drawing, text,
                new Rect(rectangle.X, rectangle.Y + 2, rectangle.Width, rectangle.Height),
                Color.FromArgb(230, 14, 35, 30), format);
            DrawBoardAspectText(drawing, text, rectangle, Color.FromArgb(255, 255, 248, 224), format);
        }
    }

    internal void DrawDuckHuntFlyingMallard(CanvasDrawingSession drawing, DuckHuntFlightSnapshot bird, double aspect)
    {
        if (!bird.Visible || _duckHuntFieldImages?.Image("flying-mallard-body.png") is not { } body ||
            _duckHuntFieldImages.Image("flying-mallard-wing.png") is not { } wing) return;
        var bounds = DuckHuntFlightLayout.SpriteBounds(bird, aspect);
        var previous = drawing.Transform;
        var pose = Matrix3x2.CreateTranslation(-.5f, -.5f) *
            Matrix3x2.CreateScale((float)bounds.Width * BoardSurfaceSize * bird.Direction,
                (float)bounds.Height * BoardSurfaceSize) *
            Matrix3x2.CreateTranslation((float)(bounds.X + bounds.Width / 2) * BoardSurfaceSize,
                (float)(bounds.Y + bounds.Height / 2) * BoardSurfaceSize) * previous;
        try
        {
            DrawWing(DuckHuntFlightLayout.FarWingPose(bird));
            var source = DuckHuntFlightLayout.BodySourceBounds;
            var destination = DuckHuntFlightLayout.BodyArtworkBounds;
            drawing.Transform = Matrix3x2.CreateTranslation((float)-source.X, (float)-source.Y) *
                Matrix3x2.CreateScale((float)(destination.Width / source.Width), (float)(destination.Height / source.Height)) *
                Matrix3x2.CreateTranslation((float)destination.X, (float)destination.Y) * pose;
            drawing.DrawImage(body, new Rect(0, 0, body.Size.Width, body.Size.Height),
                new Rect(0, 0, body.Size.Width, body.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
            DrawWing(DuckHuntFlightLayout.NearWingPose(bird));
        }
        finally { drawing.Transform = previous; }

        void DrawWing(DuckHuntFlightWingPose flap)
        {
            // The same two native-pixel transforms drive the visible feathers
            // and hit silhouette. The wrist stays joined as the outer flight
            // feathers lag the shoulder and fold during the recovery stroke.
            DrawSection(flap.Inner, new Rect(0, DuckHuntFlightLayout.WingWristV,
                wing.Size.Width, wing.Size.Height - DuckHuntFlightLayout.WingWristV));
            DrawSection(flap.Outer, new Rect(0, 0, wing.Size.Width, DuckHuntFlightLayout.WingWristV));

            void DrawSection(DuckHuntFlightWingTransform section, Rect source)
            {
                drawing.Transform = new Matrix3x2((float)section.M11, (float)section.M12,
                    (float)section.M21, (float)section.M22, (float)section.M31, (float)section.M32) * pose;
                drawing.DrawImage(wing, source, source, (float)flap.Opacity, CanvasImageInterpolation.HighQualityCubic);
            }
        }
    }
}
