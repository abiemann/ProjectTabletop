using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public IReadOnlyList<BoardFingerSelectionFeedback> CurrentFingerSelectionFeedback
    {
        get
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                return _handFrameTime <= now && now - _handFrameTime <= TimeSpan.FromMilliseconds(350)
                    ? _boardSession.FingerSelectionFeedback.ToArray() : Array.Empty<BoardFingerSelectionFeedback>();
            }
        }
    }

    // Both board and laptop textures redraw for gesture-stage changes and small
    // confirmation steps. No elapsed-time countdown can activate a button.
    internal static int FingerSelectionRenderStep(IReadOnlyList<BoardFingerSelectionFeedback> feedback)
    {
        if (feedback.Count == 0) return 0;
        unchecked
        {
            uint hash = 2166136261;
            foreach (var item in feedback.OrderBy(item => item.ButtonId, StringComparer.Ordinal))
            {
                foreach (char character in item.ButtonId)
                    hash = (hash ^ character) * 16777619;
                int step = (int)item.Stage * 32 + (int)(FingerSelectionProgress(item.Progress) * 20);
                hash = (hash ^ (uint)step) * 16777619;
            }
            return (int)hash;
        }
    }

    private static double FingerSelectionProgress(double progress) =>
        double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;

    private static string FingerSelectionCaption(IReadOnlyList<BoardFingerSelectionFeedback> feedback, string idle,
        bool includeGroupingInstruction = true)
    {
        if (feedback.Count == 0) return idle;
        // A currently selecting/ready hand takes priority over another hand's
        // latched selection so the shared instruction describes an actionable pose.
        var stage = feedback.Any(item => item.Stage == BoardFingerSelectionStage.Separating)
            ? BoardFingerSelectionStage.Separating
            : feedback.Any(item => item.Stage == BoardFingerSelectionStage.Armed)
                ? BoardFingerSelectionStage.Armed
                : feedback.Any(item => item.Stage == BoardFingerSelectionStage.Arming)
                    ? BoardFingerSelectionStage.Arming : BoardFingerSelectionStage.Selected;
        return stage == BoardFingerSelectionStage.Arming && !includeGroupingInstruction
            ? idle : FingerSelectionStageCaption(stage, includeGroupingInstruction);
    }

    private static string FingerSelectionStageCaption(BoardFingerSelectionStage stage,
        bool includeGroupingInstruction = true) => stage switch
    {
        BoardFingerSelectionStage.Armed => "Ready · separate index",
        BoardFingerSelectionStage.Separating => "Selecting",
        BoardFingerSelectionStage.Selected => includeGroupingInstruction ? "Selected · bring fingers together" : "Selected",
        _ => includeGroupingInstruction ? "Bring fingers together" : string.Empty
    };

    private static void DrawButtonFingerSelectionFeedback(CanvasDrawingSession ds, BoardButton button,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, Color accent, bool showCaption = false)
    {
        if (!button.Enabled) return;
        foreach (var item in feedback)
        {
            if (item.ButtonId != button.Id) continue;
            var bounds = button.Bounds;
            var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
                bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
            var track = new Rect(rect.X + 18, rect.Bottom - 9, Math.Max(0, rect.Width - 36), 4);
            double progress = item.Stage switch
            {
                BoardFingerSelectionStage.Armed or BoardFingerSelectionStage.Selected => 1,
                BoardFingerSelectionStage.Separating => FingerSelectionProgress(item.Progress),
                _ => 0
            };
            ds.FillRoundedRectangle(track, 2, 2, Color.FromArgb(65, accent.R, accent.G, accent.B));
            if (progress > 0)
                ds.FillRoundedRectangle(new Rect(track.X, track.Y, Math.Max(4, track.Width * progress), track.Height),
                    2, 2, accent);
            if (showCaption)
            {
                using var format = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI", FontSize = 14, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    WordWrapping = CanvasWordWrapping.NoWrap
                };
                ds.DrawText(FingerSelectionStageCaption(item.Stage),
                    new Rect(rect.X + 18, rect.Bottom - 33, rect.Width - 36, 20), accent, format);
            }
            return;
        }
    }
}
