using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly Color DuckHuntScoreColor = Color.FromArgb(255, 255, 210, 110);

    private static void DrawDuckHuntHitScore(CanvasDrawingSession drawing, OutlinedBoardText? caption,
        BoardAim impact, double elapsed, double aspect, string text = "100")
    {
        const double duration = 1.35;
        double age = Math.Max(0, elapsed);
        if (age >= duration) return;
        double progress = age / duration;
        double rise = 70 * (1 - (1 - progress) * (1 - progress));
        float opacity = (float)Math.Clamp((duration - age) / .35, 0, 1);
        caption?.Draw(drawing, text, new Rect(impact.U * BoardSurfaceSize - 75,
            impact.V * BoardSurfaceSize - 28 - rise, 150, 56), aspect, opacity);
    }
}
