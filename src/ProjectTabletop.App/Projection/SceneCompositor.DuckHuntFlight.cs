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
        if (!state.StartVisible) return;
        if (state.Phase == DuckHuntFlightPhase.Finished)
        {
            DrawCaption("ROUND COMPLETE", new Rect(220, 270, 560, 66), heading);
            DrawCaption($"{state.Score} OF 10 BIRDS", new Rect(240, 342, 520, 45), caption);
        }
        DrawDuckHuntStartTarget(drawing, aspect);
        DrawCaption("AIM AT START AND CLICK", new Rect(160, 590, 680, 45), caption);

        void DrawCaption(string text, Rect rectangle, CanvasTextFormat format)
        {
            DrawBoardAspectText(drawing, text,
                new Rect(rectangle.X, rectangle.Y + 2, rectangle.Width, rectangle.Height),
                Color.FromArgb(230, 14, 35, 30), format);
            DrawBoardAspectText(drawing, text, rectangle, Color.FromArgb(255, 255, 248, 224), format);
        }
    }

    private static void DrawDuckHuntStartTarget(CanvasDrawingSession drawing, double aspect)
    {
        var bounds = DuckHuntFlightLayout.StartBounds;
        var target = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        float radiusX = (float)(16 / aspect);
        // A shot target needs colour headroom for added laser light. Keep this
        // sage plate darker than the camera-lit hand controls in the drawer.
        drawing.FillRoundedRectangle(target, radiusX, 16, Color.FromArgb(255, 164, 184, 144));
        drawing.DrawRoundedRectangle(target, radiusX, 16, Color.FromArgb(255, 31, 76, 53), 3);
        // A visual shot target, deliberately absent from BoardSession.Buttons:
        // neither laptop clicks nor covering its lettering can start a round.
        DrawHoldButtonCaption(drawing, "START", target, Color.FromArgb(255, 26, 59, 40), aspect);
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
            // The photographed feather root is off-centre. Map that exact source
            // point to the shoulder and preserve the feather's physical aspect.
            float scale = (float)(flap.Length / (DuckHuntFlightLayout.WingRootV - DuckHuntFlightLayout.WingTopV));
            drawing.Transform = Matrix3x2.CreateTranslation((float)-DuckHuntFlightLayout.WingRootU,
                (float)-DuckHuntFlightLayout.WingRootV) *
                Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation((float)flap.Angle) *
                Matrix3x2.CreateTranslation((float)flap.PivotU, (float)flap.PivotV) * pose;
            drawing.DrawImage(wing, new Rect(0, 0, wing.Size.Width, wing.Size.Height),
                new Rect(0, 0, wing.Size.Width, wing.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
        }
    }
}
