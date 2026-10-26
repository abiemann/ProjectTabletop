namespace ProjectTabletop.Interaction;

public readonly record struct DuckHuntGalleryMiddlePose(DuckHuntGalleryQuad Quad,
    BoardAim BaseContact, double YawDegrees, bool Visible);

public static partial class DuckHuntGalleryLayout
{
    public static readonly BoardRect MiddlePanelSceneBounds = new(208.0 / 1672, 350.0 / 941, 1256.0 / 1672, 170.0 / 941);

    // Opaque exterior of the existing paper/metal assembly. Transparent corners
    // remain background; a clip, rim, stem or pedestal never closes the row.
    private static readonly BoardAim[] AssemblySilhouette =
    [
        new(547, 20), new(629, 20), new(630, 30), new(699, 45), new(763, 65), new(835, 100),
        new(904, 150), new(971, 220), new(1024, 300), new(1064, 400), new(1081, 485),
        new(1101, 495), new(1103, 550), new(1100, 575), new(1081, 585), new(1071, 650),
        new(1034, 750), new(966, 850), new(879, 930), new(770, 990), new(708, 1010),
        new(663, 1030), new(654, 1045), new(676, 1060), new(690, 1080), new(762, 1100),
        new(829, 1120), new(900, 1140), new(925, 1160), new(941, 1180), new(942, 1200),
        new(943, 1240), new(911, 1270), new(819, 1290), new(722, 1300), new(627, 1304),
        new(550, 1304), new(455, 1300), new(356, 1290), new(264, 1270), new(232, 1240),
        new(234, 1200), new(234, 1180), new(250, 1160), new(276, 1140), new(348, 1120),
        new(420, 1100), new(488, 1080), new(503, 1060), new(525, 1045), new(514, 1030),
        new(469, 1010), new(406, 990), new(296, 930), new(208, 850), new(140, 750),
        new(102, 650), new(91, 585), new(73, 575), new(71, 550), new(73, 495),
        new(91, 485), new(108, 400), new(149, 300), new(202, 220), new(272, 150),
        new(342, 100), new(416, 65), new(482, 45), new(546, 30)
    ];

    public static bool ContainsAssembly(BoardAim local) => BoardPolygon.Contains(AssemblySilhouette,
        new(local.U * 1177, (local.V - .5) * 1177 / TargetAspect + 1337.0 / 2));

    public static bool ContainsLowerMiss(BoardAim point, double boardAspect = 1)
    {
        if (!TryScenePoint(point, boardAspect, out var local)) return false;
        return new BoardRect(.126, .579, .744, .125).Contains(local.U, local.V) ||
            new BoardRect(.095, .690, .813, .100).Contains(local.U, local.V);
    }

    public static bool ContainsUpperMiss(BoardAim point, double boardAspect = 1)
    {
        if (!TryScenePoint(point, boardAspect, out var local)) return false;
        double x = local.U * 1672, y = local.V * 941;
        if (x is < 192 or > 1463 || y is < 164 or > 322) return false;
        // The curved top is the same two cubic segments used by the renderer.
        if (x < 342) return y >= ArchHeight(x, 192, 196, 274, 342);
        if (x > 1325) return y >= ArchHeight(1463 - x, 0, 0, 71, 138);
        return true;

        static double ArchHeight(double x, double a, double b, double c, double d)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 20; i++)
            {
                double t = (lo + hi) / 2, s = 1 - t;
                double curveX = s * s * s * a + 3 * s * s * t * b + 3 * s * t * t * c + t * t * t * d;
                if (curveX < x) lo = t; else hi = t;
            }
            double u = (lo + hi) / 2, v = 1 - u;
            return v * v * v * 273 + 3 * v * v * u * 215 + 3 * v * u * u * 165 + u * u * u * 164;
        }
    }

    public static DuckHuntGalleryQuad MiddleTargetQuad(DuckHuntGallerySnapshot state, double boardAspect = 1) =>
        MiddleTargetPose(state, boardAspect).Quad;

    public static DuckHuntGalleryMiddlePose MiddleTargetPose(DuckHuntGallerySnapshot state, double boardAspect = 1)
    {
        if (!double.IsFinite(boardAspect) || boardAspect <= 0 ||
            state.MiddleTargetIndex is < 0 or >= DuckHuntGalleryGame.MiddleTargetCount) return default;
        bool visible = state.MiddlePhase is DuckHuntGalleryMiddlePhase.Entering or DuckHuntGalleryMiddlePhase.TurningToFace or
            DuckHuntGalleryMiddlePhase.WaitingForHit or DuckHuntGalleryMiddlePhase.TurningAway or DuckHuntGalleryMiddlePhase.Leaving;
        const double height = .17, shelfV = 520.0 / 941;
        double progress = Math.Clamp(state.MiddleMotionProgress, 0, 1);
        double center = state.MiddlePhase switch
        {
            DuckHuntGalleryMiddlePhase.Entering => Lerp(.93, .5, progress),
            DuckHuntGalleryMiddlePhase.Leaving => Lerp(.5, .07, progress),
            _ => .5
        };
        double yaw = state.MiddlePhase switch
        {
            DuckHuntGalleryMiddlePhase.Entering => 90,
            DuckHuntGalleryMiddlePhase.TurningToFace => 90 * (1 - progress),
            DuckHuntGalleryMiddlePhase.TurningAway => -90 * progress,
            DuckHuntGalleryMiddlePhase.Leaving => -90,
            _ => 0
        };
        double width = height * TargetAspect / SceneAspect * Math.Max(0, Math.Cos(yaw * Math.PI / 180));
        BoardAim Project(double u, double v) => ScenePoint(center + (u - TargetBaseU) * width,
            shelfV + (v - TargetBaseV) * height, boardAspect);
        return new(new(Project(0, 0), Project(1, 0), Project(1, 1), Project(0, 1)),
            ScenePoint(center, shelfV, boardAspect), yaw, visible);
    }

    private static bool TryScenePoint(BoardAim point, double aspect, out BoardAim local)
    {
        local = default;
        if (!double.IsFinite(point.U) || !double.IsFinite(point.V) || !double.IsFinite(aspect) || aspect <= 0) return false;
        var bounds = SceneBounds(aspect);
        local = new((point.U - bounds.X) / bounds.Width,
            LayoutYToSourceY((point.V - bounds.Y) / bounds.Height));
        return true;
    }
}
