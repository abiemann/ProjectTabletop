using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasTextFormat? _galleryHitFormat;
    private OutlinedBoardText? _galleryHit50, _galleryHit100;

    private void PrepareGalleryHitScores()
    {
        _galleryHitFormat ??= new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 40,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        // Keep one geometry per award so mixed rapid hits reuse both glyphs.
        _galleryHit50 ??= new(_galleryHitFormat, DuckHuntScoreColor);
        _galleryHit100 ??= new(_galleryHitFormat, DuckHuntScoreColor);
    }

    private void DisposeGalleryHitScores()
    {
        _galleryHit50?.Dispose(); _galleryHit100?.Dispose();
        _galleryHit50 = _galleryHit100 = null;
        _galleryHitFormat?.Dispose(); _galleryHitFormat = null;
    }

    internal void DrawDuckHuntGalleryHitScores(CanvasDrawingSession drawing, double aspect)
    {
        var awards = _boardSession.GalleryScorePopups;
        for (int i = 0; i < awards.Count; i++)
            DrawDuckHuntGalleryHitScore(drawing, awards[i], aspect);
    }

    internal void DrawDuckHuntGalleryHitScore(CanvasDrawingSession drawing, DuckHuntGalleryScorePopup award, double aspect)
    {
        if (award.Points == 50)
            DrawDuckHuntHitScore(drawing, _galleryHit50, award.Impact, award.Elapsed, aspect, "50");
        else if (award.Points == 100)
            DrawDuckHuntHitScore(drawing, _galleryHit100, award.Impact, award.Elapsed, aspect);
    }
}
