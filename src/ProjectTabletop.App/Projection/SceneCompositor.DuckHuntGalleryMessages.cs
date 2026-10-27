using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // file:// is supported by Win2D for unpackaged desktop apps; ms-appx://
    // requires package identity and cannot locate this bundled font here.
    internal static readonly Uri GalleryMessageFontUri =
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Rye-Regular.ttf"));
    internal static readonly string GalleryMessageFontFamily = GalleryMessageFontUri.AbsoluteUri + "#Rye";
    private CanvasGeometry? _galleryPressPlayLetters, _galleryGameOverLetters;
    private Rect _galleryPressPlayInk, _galleryGameOverInk;

    internal static string? GalleryMiddleMessage(DuckHuntGallerySnapshot state) => !state.PlayVisible ? null :
        state.MiddlePhase == DuckHuntGalleryMiddlePhase.Complete ? "GAME OVER" :
        state.Cycle == 0 ? "PRESS PLAY" : null;

    private void PrepareGalleryMessages(CanvasDevice device)
    {
        if (_galleryPressPlayLetters is not null) return;
        using var format = new CanvasTextFormat
        {
            FontFamily = GalleryMessageFontFamily, FontSize = 200,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        _galleryPressPlayLetters = CreateLetters("PRESS PLAY", out _galleryPressPlayInk);
        _galleryGameOverLetters = CreateLetters("GAME OVER", out _galleryGameOverInk);

        CanvasGeometry CreateLetters(string text, out Rect ink)
        {
            using var layout = new CanvasTextLayout(device, text, format, 2400, 300);
            var letters = CanvasGeometry.CreateText(layout);
            ink = letters.ComputeBounds();
            return letters;
        }
    }

    private void DisposeGalleryMessages()
    {
        _galleryPressPlayLetters?.Dispose(); _galleryGameOverLetters?.Dispose();
        _galleryPressPlayLetters = _galleryGameOverLetters = null;
    }

    private void DrawGalleryMessages(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect)
    {
        if (GalleryMiddleMessage(state) is not { } message ||
            _galleryPressPlayLetters is null || _galleryGameOverLetters is null) return;
        var panel = GallerySceneRect(DuckHuntGalleryLayout.MiddlePanelSceneBounds, aspect);
        if (message == "GAME OVER")
        {
            DrawWord(_galleryGameOverLetters, _galleryGameOverInk, .16, .36);
            DrawWord(_galleryPressPlayLetters, _galleryPressPlayInk, .66, .16);
        }
        else
            DrawWord(_galleryPressPlayLetters, _galleryPressPlayInk, .29, .42);

        void DrawWord(CanvasGeometry letters, Rect ink, double top, double height)
        {
            var box = new Rect(panel.X + panel.Width * .08, panel.Y + panel.Height * top,
                panel.Width * .84, panel.Height * height);
            // Compensate for the board's aspect so the bundled Western letters
            // retain their proportions on portrait, square and wide projectors.
            float scale = (float)Math.Min(box.Width * aspect / ink.Width, box.Height / ink.Height);
            float scaleX = scale / (float)aspect;
            float x = (float)(box.X + (box.Width - ink.Width * scaleX) / 2 - ink.X * scaleX);
            float y = (float)(box.Y + (box.Height - ink.Height * scale) / 2 - ink.Y * scale);
            var previous = drawing.Transform;
            try
            {
                drawing.Transform = Matrix3x2.CreateScale(scaleX, scale) *
                    Matrix3x2.CreateTranslation(x, y) * previous;
                drawing.DrawGeometry(letters, Color.FromArgb(255, 143, 106, 61), 3);
                drawing.FillGeometry(letters, Color.FromArgb(255, 63, 43, 28));
            }
            finally { drawing.Transform = previous; }
        }
    }
}
