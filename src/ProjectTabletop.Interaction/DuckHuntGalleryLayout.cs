namespace ProjectTabletop.Interaction;

/// <summary>A paper plane with horizontal top and bottom edges. Local coordinates
/// use its projective surface so punctures stay attached throughout the fall.</summary>
public readonly record struct DuckHuntGalleryQuad(
    BoardAim TopLeft, BoardAim TopRight, BoardAim BottomRight, BoardAim BottomLeft)
{
    public BoardRect Bounds
    {
        get
        {
            double left = Math.Min(TopLeft.U, BottomLeft.U), right = Math.Max(TopRight.U, BottomRight.U);
            double top = Math.Min(TopLeft.V, BottomLeft.V), bottom = Math.Max(TopLeft.V, BottomLeft.V);
            return new(left, top, right - left, bottom - top);
        }
    }

    public BoardAim Map(double u, double v)
    {
        double topWidth = TopRight.U - TopLeft.U, bottomWidth = BottomRight.U - BottomLeft.U;
        double t = topWidth * v / (bottomWidth + (topWidth - bottomWidth) * v);
        double left = TopLeft.U + (BottomLeft.U - TopLeft.U) * t;
        double width = topWidth + (bottomWidth - topWidth) * t;
        return new(left + width * u, TopLeft.V + (BottomLeft.V - TopLeft.V) * t);
    }

    public bool TryMapToLocal(BoardAim point, out BoardAim local)
    {
        local = default;
        double height = BottomLeft.V - TopLeft.V;
        double topWidth = TopRight.U - TopLeft.U, bottomWidth = BottomRight.U - BottomLeft.U;
        if (!double.IsFinite(point.U) || !double.IsFinite(point.V) || Math.Abs(height) < 1e-10 ||
            topWidth <= 0 || bottomWidth <= 0) return false;
        double t = (point.V - TopLeft.V) / height;
        if (t is < 0 or > 1) return false;
        double width = topWidth + (bottomWidth - topWidth) * t;
        double u = (point.U - TopLeft.U - (BottomLeft.U - TopLeft.U) * t) / width;
        double v = bottomWidth * t / (topWidth + (bottomWidth - topWidth) * t);
        if (u is < 0 or > 1 || v is < 0 or > 1) return false;
        local = new(u, v);
        return true;
    }
}

public readonly record struct DuckHuntGalleryTargetPose(DuckHuntGalleryQuad Quad,
    BoardAim BaseContact, double TiltDegrees, bool Supported, bool InFrontOfLip);

public readonly record struct DuckHuntGallerySkullPose(BoardRect Bounds, BoardAim BaseContact);

/// <summary>Scene anchors for the approved 16:9 saloon artwork. The entire scene
/// is contained on every board aspect so all five papers remain reachable.</summary>
public static class DuckHuntGalleryLayout
{
    public const double SceneAspect = 1672.0 / 941;
    public const double TargetAspect = .88;
    public const double SkullAspect = 1, SkullBaseU = .5, SkullBaseV = 1207.0 / 1254;
    private static readonly BoardAim[] SkullSilhouette =
    [
        new(610, 44), new(644, 44), new(711, 53), new(789, 80), new(899, 150), new(984, 250),
        new(1029, 350), new(1048, 450), new(1042, 550), new(1011, 650), new(1011, 690),
        new(1029, 730), new(1006, 770), new(986, 810), new(967, 821), new(960, 850), new(932, 900),
        new(916, 908), new(912, 1000), new(900, 1100), new(803, 1170), new(764, 1190),
        new(738, 1200), new(712, 1205), new(672, 1208), new(582, 1208), new(541, 1205),
        new(516, 1200), new(489, 1190), new(451, 1170), new(354, 1100), new(341, 1000),
        new(338, 908), new(321, 900), new(294, 850), new(285, 821), new(267, 810),
        new(247, 770), new(224, 730), new(242, 690), new(242, 650), new(212, 550),
        new(205, 450), new(224, 350), new(269, 250), new(355, 150), new(465, 80), new(546, 53)
    ];
    // Measured in the 1177×1337 assembly, contained in the existing 0.88 surface.
    public const double PaperCenterU = 585.5 / 1177;
    public const double PaperCenterV = .5 + (536.5 - 1337.0 / 2) * TargetAspect / 1177;
    public const double PaperRadiusU = 432.0 / 1177;
    public const double PaperRadiusV = PaperRadiusU * TargetAspect;
    // Centre of the opaque pedestal bottom, excluding the transparent canvas margin.
    public const double TargetBaseU = 585.5 / 1177;
    public const double TargetBaseV = .5 + (1304.5 - 1337.0 / 2) * TargetAspect / 1177;
    public static readonly IReadOnlyList<BoardRect> PaperClipBounds = Array.AsReadOnly(new[]
    {
        SourceRect(542, 8, 92, 119),
        SourceRect(545, 916, 86, 130),
        SourceRect(64, 490, 113, 93),
        SourceRect(997, 490, 114, 93)
    });
    public static readonly BoardRect PlayBounds = new(.39, .40, .22, .12);
    public static readonly BoardRect FlapSceneBounds = new(.124, .577, .748, .129);
    public static readonly BoardRect UpperPanelSceneBounds = new(.114, .172, .762, .170);
    public const double BeltBackV = .708, BeltFrontV = .795;

    public static bool ContainsPaper(BoardAim local)
    {
        double x = (local.U - PaperCenterU) / PaperRadiusU;
        double y = (local.V - PaperCenterV) / PaperRadiusV;
        // Keep the exact rim inclusive despite projective round-trip rounding.
        if (!(x * x + y * y <= 1 + 1e-10)) return false;
        for (int i = 0; i < PaperClipBounds.Count; i++)
            if (PaperClipBounds[i].Contains(local.U, local.V)) return false;
        return true;
    }

    public static BoardRect SceneBounds(double boardAspect)
    {
        if (!double.IsFinite(boardAspect) || boardAspect <= 0) return default;
        double width = Math.Min(1, SceneAspect / boardAspect);
        double height = Math.Min(1, boardAspect / SceneAspect);
        return new((1 - width) / 2, (1 - height) / 2, width, height);
    }

    public static BoardAim ScenePoint(double u, double v, double boardAspect)
    {
        var scene = SceneBounds(boardAspect);
        return new(scene.X + u * scene.Width, scene.Y + v * scene.Height);
    }

    public static BoardRect SkullBounds(int index, double boardAspect = 1) => SkullPose(index, boardAspect).Bounds;

    public static DuckHuntGallerySkullPose SkullPose(int index, double boardAspect = 1)
    {
        if (index is < 0 or >= DuckHuntGalleryGame.SkullCount ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return default;
        const double height = .135, shelfV = .342;
        double width = height * SkullAspect / SceneAspect;
        double center = .5 + (index - 2) * .14;
        var scene = SceneBounds(boardAspect);
        var topLeft = ScenePoint(center - width * SkullBaseU, shelfV - height * SkullBaseV, boardAspect);
        return new(new(topLeft.U, topLeft.V, width * scene.Width, height * scene.Height),
            ScenePoint(center, shelfV, boardAspect));
    }

    public static bool ContainsSkull(int index, BoardAim point, double boardAspect = 1)
    {
        if (index is < 0 or >= DuckHuntGalleryGame.SkullCount ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        var bounds = SkullBounds(index, boardAspect);
        return BoardPolygon.Contains(SkullSilhouette, new(
            (point.U - bounds.X) / bounds.Width * 1254, (point.V - bounds.Y) / bounds.Height * 1254));
    }

    public static DuckHuntGalleryQuad TargetQuad(int index, DuckHuntGallerySnapshot state, double boardAspect = 1) =>
        TargetPose(index, state, boardAspect).Quad;

    public static DuckHuntGalleryTargetPose TargetPose(int index, DuckHuntGallerySnapshot state, double boardAspect = 1)
    {
        if (index is < 0 or >= DuckHuntGalleryGame.TargetCount ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return default;
        const double rollerStart = 1.7, rollerV = .738, rollerRadius = .04;
        const double releaseAngle = Math.PI / 3;
        bool supported = state.Phase is not (DuckHuntGalleryPhase.Falling or DuckHuntGalleryPhase.Closing);
        double position = Math.Clamp(state.ConveyorPosition, 0, 2);
        if (!supported) position = 2;
        double baseV, height, spread, angle = 0;
        if (position <= 1)
        {
            baseV = Lerp(.697, .732, position);
            height = Lerp(.101, .135, position);
            spread = Lerp(1, 1.10, position);
        }
        else
        {
            double departure = position - 1;
            double growth = departure * (2 - departure);
            height = Lerp(.135, .152, growth);
            spread = Lerp(1.10, 1.16, growth);
            if (position <= rollerStart)
            {
                double deck = departure / (rollerStart - 1);
                baseV = Lerp(.732, rollerV, deck * (2 - deck));
            }
            else
            {
                double roll = (position - rollerStart) / (2 - rollerStart);
                angle = releaseAngle * roll * roll * (2 - roll);
                baseV = rollerV + rollerRadius * (Math.Sin(angle) + 1 - Math.Cos(angle));
            }
        }
        double width = height / SceneAspect * TargetAspect;
        double center = .5 + (index - 2) * .14 * spread;
        if (!supported)
        {
            double seconds = Math.Clamp(state.FallProgress, 0, 1) * DuckHuntGalleryGame.FallDuration.TotalSeconds;
            double angularVelocity = releaseAngle /
                ((2 - rollerStart) * DuckHuntGalleryGame.DepartDuration.TotalSeconds);
            double releaseVelocity = rollerRadius * (Math.Cos(releaseAngle) + Math.Sin(releaseAngle)) * angularVelocity;
            baseV += releaseVelocity * seconds + .6 * seconds * seconds;
            angle = releaseAngle + angularVelocity * seconds;
        }
        var quad = new DuckHuntGalleryQuad(Project(0, 0), Project(1, 0), Project(1, 1), Project(0, 1));
        return new(quad, ScenePoint(center, baseV, boardAspect), angle * 180 / Math.PI,
            supported, angle > 0 || !supported);

        BoardAim Project(double u, double v)
        {
            double aboveBase = TargetBaseV - v;
            // Forward tipping brings the upper edge closer. Project about the
            // actual foot, so its point stays exactly on the belt/roller arc.
            double perspective = 1 - .30 * aboveBase * Math.Sin(angle);
            return ScenePoint(center + (u - TargetBaseU) * width / perspective,
                baseV - aboveBase * height * Math.Cos(angle) / perspective, boardAspect);
        }
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;

    private static BoardRect SourceRect(double x, double y, double width, double height) => new(
        x / 1177, .5 + (y - 1337.0 / 2) * TargetAspect / 1177,
        width / 1177, height * TargetAspect / 1177);
}
