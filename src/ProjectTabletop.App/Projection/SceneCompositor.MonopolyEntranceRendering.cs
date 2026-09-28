using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _monopolyEntranceBaseTarget;
    private CanvasRenderTarget? _monopolyEntranceTileAtlas;
    private CanvasRenderTarget? _monopolyEntranceLidTarget;
    private bool _monopolyEntranceBaseReady;
    private (long Game, double Aspect)? _monopolyEntranceTileState;
    private (long Game, long Session, double Aspect, bool HideDice, bool Rolling,
        bool Drawer, float DrawerProgress, string Hover, int Feedback)? _monopolyEntranceLidState;

    internal readonly record struct MonopolyEntrancePose(double Progress, bool Visible, bool Landed,
        float Elevation, float Scale, float RotationRadians);

    internal static double MonopolyEntranceTileProgress(MonopolyEntranceFrame frame, int index)
    {
        if (index is < 0 or > 39) throw new ArgumentOutOfRangeException(nameof(index));
        if (!frame.Active) return 1;
        double elapsed = double.IsFinite(frame.ElapsedMilliseconds) ? frame.ElapsedMilliseconds : 0;
        double start = MonopolyEntranceLeadInMilliseconds + index * MonopolyEntranceTileStaggerMilliseconds;
        return Math.Clamp((elapsed - start) / MonopolyEntranceTileDurationMilliseconds, 0, 1);
    }

    internal static MonopolyEntrancePose MonopolyEntranceTilePose(MonopolyEntranceFrame frame, int index)
    {
        double progress = MonopolyEntranceTileProgress(frame, index);
        double start = MonopolyEntranceLeadInMilliseconds + index * MonopolyEntranceTileStaggerMilliseconds;
        bool visible = !frame.Active || frame.ElapsedMilliseconds >= start;
        if (progress >= 1) return new(1, visible, true, 0, 1, 0);
        double fallFraction = MonopolyEntranceTileFallMilliseconds / MonopolyEntranceTileDurationMilliseconds;
        float height, angle;
        if (progress < fallFraction)
        {
            float falling = (float)(progress / fallFraction);
            height = 190 * (1 - falling * falling);
            angle = (index % 2 == 0 ? 1 : -1) * .052f * (1 - falling);
        }
        else
        {
            float settling = (float)((progress - fallFraction) / (1 - fallFraction));
            height = 12 * MathF.Sin(MathF.PI * settling) * (1 - settling);
            angle = (index % 2 == 0 ? 1 : -1) * .023f * MathF.Sin(settling * MathF.PI * 3) * (1 - settling);
        }
        return new(progress, visible, false, height, 1 + height / 190 * .22f, angle);
    }

    internal static MonopolyEntrancePose MonopolyEntranceCenterPose(MonopolyEntranceFrame frame)
    {
        double progress = double.IsFinite(frame.CenterProgress) ? Math.Clamp(frame.CenterProgress, 0, 1) : 0;
        bool visible = !frame.Active || frame.ElapsedMilliseconds >= MonopolyEntranceCenterStartMilliseconds;
        if (!frame.Active || progress >= 1) return new(1, visible, true, 0, 1, 0);
        float height, angle;
        if (progress < .76)
        {
            float falling = (float)(progress / .76);
            height = 205 * (1 - falling * falling);
            angle = -.014f * (1 - falling);
        }
        else
        {
            float settling = (float)((progress - .76) / .24);
            height = 10 * MathF.Sin(MathF.PI * settling) * (1 - settling);
            angle = .006f * MathF.Sin(settling * MathF.PI * 3) * (1 - settling);
        }
        return new(progress, visible, false, height, 1 + height / 205 * .11f, angle);
    }

    private void DrawMonopolyEntrance(CanvasDrawingSession ds, MonopolySnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, double aspect,
        bool hideDice, bool rolling, bool drawer, float drawerProgress, MonopolyEntranceFrame frame)
    {
        if (frame.LandedTiles >= 40 && frame.CenterProgress >= 1)
        {
            // The last contact frame and the normal cached board are identical.
            DrawMonopolyBoard(ds, game, buttons, hovered, feedback, aspect, hideDice, rolling, drawer, drawerProgress);
            return;
        }
        EnsureMonopolyEntranceLayers(ds.Device, game, buttons, hovered, feedback, aspect,
            hideDice, rolling, drawer, drawerProgress);
        DrawMonopolyEntranceImage(ds, _monopolyEntranceBaseTarget!, new Rect(0, 0, 1000, 1000));

        // Cast all moving shadows before the pieces. Settled pieces retain their
        // native atlas pixels, with no elevation, scaling, tilt, or residual glow.
        for (int index = 0; index < 40; index++)
        {
            var pose = MonopolyEntranceTilePose(frame, index);
            if (pose.Visible && !pose.Landed)
                DrawMonopolyEntranceContactShadow(ds, MonopolySpaceRectangle(index), pose.Elevation, false);
        }
        for (int index = 0; index < 40; index++)
        {
            var pose = MonopolyEntranceTilePose(frame, index);
            if (!pose.Visible) continue;
            var rectangle = MonopolySpaceRectangle(index);
            var previous = ds.Transform;
            ds.Transform = MonopolyEntranceTransform(rectangle, pose) * previous;
            try
            {
                if (!pose.Landed)
                    ds.FillRectangle(new Rect(rectangle.X, rectangle.Y + 3, rectangle.Width, rectangle.Height),
                        ThemeColor(106, 76, 42, 220));
                DrawMonopolyEntranceImage(ds, _monopolyEntranceTileAtlas!, rectangle);
            }
            finally { ds.Transform = previous; }
        }

        var center = MonopolyEntranceCenterPose(frame);
        if (!center.Visible) return;
        var lidBounds = new Rect(168, 168, 664, 664);
        DrawMonopolyEntranceContactShadow(ds, lidBounds, center.Elevation, true);
        var oldTransform = ds.Transform;
        ds.Transform = MonopolyEntranceTransform(lidBounds, center) * oldTransform;
        try
        {
            if (!center.Landed)
                ds.FillRectangle(new Rect(168, 173, 664, 664), ThemeColor(114, 82, 37, 240));
            DrawMonopolyEntranceImage(ds, _monopolyEntranceLidTarget!, lidBounds);
        }
        finally { ds.Transform = oldTransform; }
        // The rail remains fixed while its captions quietly join the landed lid.
        float captionOpacity = (float)Math.Clamp((center.Progress - .76) / .24, 0, 1);
        if (captionOpacity <= 0) return;
        using var captions = ds.CreateLayer(captionOpacity);
        DrawMonopolyRailTitle(ds);
        DrawMonopolyRailCaptions(ds, rolling, feedback);
    }

    private void EnsureMonopolyEntranceLayers(CanvasDevice device, MonopolySnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, double aspect,
        bool hideDice, bool rolling, bool drawer, float drawerProgress)
    {
        if (EnsureBoardRenderTarget(ref _monopolyEntranceBaseTarget, device)) _monopolyEntranceBaseReady = false;
        if (EnsureBoardRenderTarget(ref _monopolyEntranceTileAtlas, device)) _monopolyEntranceTileState = null;
        if (EnsureBoardRenderTarget(ref _monopolyEntranceLidTarget, device)) _monopolyEntranceLidState = null;
        if (!_monopolyEntranceBaseReady)
        {
            using var drawing = _monopolyEntranceBaseTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_monopolyEntranceBaseTarget);
            DrawMonopolyFrame(drawing, entranceBase: true);
            _monopolyEntranceBaseReady = true;
        }
        var tileKey = (game.Revision, aspect);
        if (_monopolyEntranceTileState != tileKey)
        {
            using var drawing = _monopolyEntranceTileAtlas!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_monopolyEntranceTileAtlas);
            drawing.Clear(Colors.Transparent);
            for (int index = 0; index < 40; index++)
                DrawMonopolySpace(drawing, MonopolyGame.Spaces[index], game, aspect);
            _monopolyEntranceTileState = tileKey;
        }
        var lidKey = (game.Revision, _boardSession.Revision, aspect, hideDice, rolling, drawer,
            drawerProgress, string.Join(",", hovered), FingerSelectionRenderStep(feedback));
        if (_monopolyEntranceLidState != lidKey)
        {
            using var drawing = _monopolyEntranceLidTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_monopolyEntranceLidTarget);
            drawing.Clear(Colors.Transparent);
            using var clip = drawing.CreateLayer(1, new Rect(168, 168, 664, 664));
            DrawMonopolyFelt(drawing, new Rect(168, 168, 664, 664));
            DrawMonopolyCenterDecoration(drawing);
            DrawMonopolyCenterContents(drawing, game, buttons, hovered, feedback, aspect,
                hideDice, rolling, drawer, drawerProgress);
            _monopolyEntranceLidState = lidKey;
        }
    }

    private static Matrix3x2 MonopolyEntranceTransform(Rect bounds, MonopolyEntrancePose pose)
    {
        if (pose.Landed) return Matrix3x2.Identity;
        var center = new Vector2((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2));
        return Matrix3x2.CreateScale(pose.Scale, center) *
            Matrix3x2.CreateRotation(pose.RotationRadians, center) *
            Matrix3x2.CreateTranslation(0, -pose.Elevation);
    }

    private static void DrawMonopolyEntranceImage(CanvasDrawingSession ds, CanvasRenderTarget image, Rect logicalBounds)
    {
        var pixels = image.SizeInPixels;
        var source = new Rect(logicalBounds.X * pixels.Width / BoardSurfaceSize,
            logicalBounds.Y * pixels.Height / BoardSurfaceSize,
            logicalBounds.Width * pixels.Width / BoardSurfaceSize,
            logicalBounds.Height * pixels.Height / BoardSurfaceSize);
        ds.DrawImage(image, logicalBounds, source, 1, CanvasImageInterpolation.Linear);
    }

    private static void DrawMonopolyEntranceContactShadow(CanvasDrawingSession ds, Rect bounds, float elevation, bool lid)
    {
        float separation = Math.Clamp(elevation / (lid ? 205 : 190), 0, 1);
        float spread = (lid ? 9 : 4) + separation * (lid ? 22 : 11);
        for (int ring = 4; ring >= 0; ring--)
        {
            float outset = spread * ring / 4;
            byte alpha = (byte)((lid ? 20 : 22) * (1 - separation * .65f));
            ds.FillRoundedRectangle(new Rect(bounds.X - outset, bounds.Y + 3 - outset,
                bounds.Width + outset * 2, bounds.Height + outset * 2),
                lid ? 4 + outset : 2 + outset, lid ? 4 + outset : 2 + outset,
                ThemeColor(0, 8, 4, alpha));
        }
    }

    private void DisposeMonopolyEntranceLayers()
    {
        _monopolyEntranceBaseTarget?.Dispose();
        _monopolyEntranceTileAtlas?.Dispose();
        _monopolyEntranceLidTarget?.Dispose();
        _monopolyEntranceBaseTarget = _monopolyEntranceTileAtlas = _monopolyEntranceLidTarget = null;
        _monopolyEntranceBaseReady = false;
        _monopolyEntranceTileState = null;
        _monopolyEntranceLidState = null;
    }
}
