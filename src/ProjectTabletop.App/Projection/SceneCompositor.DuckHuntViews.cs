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
    private CanvasTextLayout? _duckHuntWelcomeTitle;
    private Rect _duckHuntWelcomeTitleBounds;

    private static bool IsDuckHuntModeButton(BoardButton button) =>
        button.Id is "duck-hunt-target-practice" or "duck-hunt-hunt" or "duck-hunt-clay-pigeon";

    private void DrawDuckHuntSceneView(CanvasDrawingSession drawing, DateTimeOffset now)
    {
        bool menu = _boardSession.CurrentDuckHuntView == DuckHuntView.Menu;
        bool clay = _boardSession.CurrentDuckHuntView == DuckHuntView.ClayPigeon;
        drawing.Clear(Color.FromArgb(255, 88, 151, 195));
        string file = menu ? "arcade-welcome-v1.png" : clay ? "clay-valley-v1.png" : "hunt-field-v1.png";
        var images = menu ? _duckHuntImages : clay ? _duckHuntClayImages : _duckHuntFieldImages;
        if (images?.Image(file) is { } image)
            // Tiny backlit holes in the valley's far-left tree look like laser
            // cores at 4K. A natural centred crop excludes those source pixels;
            // the original photograph and the shared detector remain untouched.
            DrawDuckHuntScenery(drawing, image, PaintBoardAspect(), clay ? .04 : 0);
        if (menu) DrawDuckHuntWelcomeTitle(drawing, PaintBoardAspect());
        else if (clay) DrawDuckHuntClay(drawing, _boardSession.ClayState, PaintBoardAspect());
        else DrawDuckHuntFlight(drawing, _boardSession.HuntFlightState, PaintBoardAspect());
        DrawDuckHuntControls(drawing, now);
    }

    private static void DrawDuckHuntScenery(CanvasDrawingSession drawing, CanvasBitmap image, double aspect, double inset = 0)
    {
        // Crop rather than stretch: trees, clouds and the distant field retain
        // their physical proportions on either orientation of the tabletop.
        double width = image.Size.Width * (1 - 2 * inset), height = image.Size.Height * (1 - 2 * inset);
        if (width / height > aspect) width = height * aspect;
        else height = width / aspect;
        var source = new Rect((image.Size.Width - width) / 2, (image.Size.Height - height) / 2,
            width, height);
        drawing.DrawImage(image, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize), source,
            1, CanvasImageInterpolation.HighQualityCubic);
    }

    private void PrepareDuckHuntWelcomeTitle(CanvasDevice device)
    {
        if (_duckHuntWelcomeTitle is not null) return;
        using var format = new CanvasTextFormat
        {
            FontFamily = "Bahnschrift", FontWeight = FontWeights.Bold, FontSize = 100,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        _duckHuntWelcomeTitle = new CanvasTextLayout(device, "Duck Hunt", format, 1200, 180);
        _duckHuntWelcomeTitleBounds = _duckHuntWelcomeTitle.DrawBounds;
    }

    private void DrawDuckHuntWelcomeTitle(CanvasDrawingSession drawing, double aspect)
    {
        if (_duckHuntWelcomeTitle is not { } title) return;
        var ink = _duckHuntWelcomeTitleBounds;
        // Cache native text once, retaining its proper overlapping glyph fills;
        // scale in physical board coordinates so the title is never stretched.
        float scale = (float)Math.Min(760 * aspect / ink.Width, 170 / ink.Height);
        var previous = drawing.Transform;
        var origin = Matrix3x2.CreateTranslation((float)(-ink.X - ink.Width / 2),
            (float)(-ink.Y - ink.Height / 2)) * Matrix3x2.CreateScale(scale);
        var placement = Matrix3x2.CreateScale((float)(1 / aspect), 1) *
            Matrix3x2.CreateTranslation(500, 230) * previous;
        try
        {
            drawing.Transform = origin * Matrix3x2.CreateTranslation(0, 6) * placement;
            drawing.DrawTextLayout(title, Vector2.Zero, Color.FromArgb(230, 17, 43, 35));
            drawing.Transform = origin * placement;
            drawing.DrawTextLayout(title, Vector2.Zero, Color.FromArgb(255, 255, 207, 75));
        }
        finally { drawing.Transform = previous; }
    }

    private void DisposeDuckHuntWelcomeTitle()
    {
        _duckHuntWelcomeTitle?.Dispose();
        _duckHuntWelcomeTitle = null;
        _duckHuntWelcomeTitleBounds = default;
    }
}
