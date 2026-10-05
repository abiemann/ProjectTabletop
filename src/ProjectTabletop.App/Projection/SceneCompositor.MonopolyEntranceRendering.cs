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
    private readonly (CanvasRenderTarget Image, Rect Bounds)?[] _monopolyEntranceParcels = new (CanvasRenderTarget, Rect)?[40];
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
        // The city and plaza stay anchored while the deeds assemble around them.
        // CenterProgress still determines the shared input release deadline.
        return new(1, true, true, 0, 1, 0);
    }

    private void DrawMonopolyEntrance(CanvasDrawingSession ds, MonopolySnapshot game,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback, double aspect,
        bool hideDice, bool rolling, bool drawer, float drawerProgress, MonopolyEntranceFrame frame,
        bool renderCityAnimations = true)
    {
        if (frame.LandedTiles >= 40 && frame.CenterProgress >= 1)
        {
            // The last contact frame and the normal cached board are identical.
            DrawMonopolyBoard(ds, game, buttons, hovered, feedback, aspect, hideDice, rolling, drawer, drawerProgress,
                renderCityAnimations: renderCityAnimations);
            return;
        }
        EnsureMonopolyEntranceLayers(ds.Device, game, buttons, hovered, feedback, aspect,
            hideDice, rolling, drawer, drawerProgress);
        ds.Clear(Colors.Transparent);
        if (renderCityAnimations)
        {
            var now = _monopolyClock();
            DrawCrownDeedWater(ds, now, aspect);
            DrawCrownDeedWindows(ds, now);
        }
        DrawMonopolyEntranceImage(ds, _monopolyEntranceBaseTarget!, new Rect(0, 0, 1000, 1000));

        // Cast all moving shadows before the pieces. Each transparent parcel
        // owns its native pixels; its rotated AABB never copies a neighbour.
        for (int index = 0; index < 40; index++)
        {
            var pose = MonopolyEntranceTilePose(frame, index);
            if (pose.Visible && !pose.Landed)
                DrawMonopolyEntranceContactShadow(ds, CrownDeedParcelPose(index), pose.Elevation);
        }
        for (int index = 0; index < 40; index++)
        {
            var pose = MonopolyEntranceTilePose(frame, index);
            if (!pose.Visible) continue;
            var parcel = CrownDeedParcelPose(index);
            var piece = _monopolyEntranceParcels[index]!.Value;
            var rectangle = piece.Bounds;
            var previous = ds.Transform;
            ds.Transform = MonopolyEntranceTransform(MonopolySpaceRectangle(index), pose) * previous;
            try
            {
                if (!pose.Landed)
                {
                    var world = ds.Transform;
                    ds.Transform = parcel.Transform * world;
                    ds.FillRoundedRectangle(new Rect(0, 3, parcel.Width, parcel.Depth), 2, 2,
                        ThemeColor(106, 76, 42, 220));
                    ds.Transform = world;
                }
                ds.DrawImage(piece.Image, rectangle,
                    new Rect(0, 0, piece.Image.SizeInPixels.Width, piece.Image.SizeInPixels.Height),
                    1, CanvasImageInterpolation.Linear);
            }
            finally { ds.Transform = previous; }
        }
        // Keep every stationary control caption protected above airborne deeds.
        DrawMonopolyEntranceImage(ds, _monopolyEntranceLidTarget!, new Rect(0, 0, 1000, 1000));
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
            DrawMonopolyFrame(drawing, entranceBase: true, renderCityAnimations: false);
            _monopolyEntranceBaseReady = true;
        }
        var tileKey = (game.Revision, aspect);
        if (_monopolyEntranceTileState != tileKey)
        {
            using var drawing = _monopolyEntranceTileAtlas!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_monopolyEntranceTileAtlas);
            drawing.Clear(Colors.Transparent);
            for (int index = 0; index < 40; index++)
            {
                DrawMonopolySpace(drawing, MonopolyGame.Spaces[index], game, aspect);
                CacheMonopolyEntranceParcel(device, index, game, aspect);
            }
            _monopolyEntranceTileState = tileKey;
        }
        var lidKey = (game.Revision, _boardSession.Revision, aspect, hideDice, rolling, drawer,
            drawerProgress, string.Join(",", hovered), FingerSelectionRenderStep(feedback));
        if (_monopolyEntranceLidState != lidKey)
        {
            using var drawing = _monopolyEntranceLidTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_monopolyEntranceLidTarget);
            drawing.Clear(Colors.Transparent);
            DrawMonopolyCenterContents(drawing, game, buttons, hovered, feedback, aspect,
                hideDice, rolling, drawer, drawerProgress);
            DrawMonopolyRailCaptions(drawing, rolling, feedback);
            _monopolyEntranceLidState = lidKey;
        }
    }

    private void CacheMonopolyEntranceParcel(CanvasDevice device, int index, MonopolySnapshot game, double aspect)
    {
        // Align each crop to the full-board raster so drawing it back at rest is
        // one-to-one, including anti-aliased boundaries. Padding retains the
        // building silhouettes and token shadows belonging to this deed alone.
        var raster = _monopolyEntranceTileAtlas!.SizeInPixels;
        double densityX = raster.Width / BoardSurfaceSize, densityY = raster.Height / BoardSurfaceSize;
        var bounds = MonopolySpaceRectangle(index);
        const double padding = 40;
        int left = (int)Math.Floor((bounds.X - padding) * densityX);
        int top = (int)Math.Floor((bounds.Y - padding) * densityY);
        int right = (int)Math.Ceiling((bounds.Right + padding) * densityX);
        int bottom = (int)Math.Ceiling((bounds.Bottom + padding) * densityY);
        int width = Math.Max(1, right - left), height = Math.Max(1, bottom - top);
        var logicalBounds = new Rect(left / densityX, top / densityY, width / densityX, height / densityY);
        var image = _monopolyEntranceParcels[index]?.Image;
        if (image is null || image.Device != device || image.SizeInPixels.Width != width || image.SizeInPixels.Height != height)
        {
            image?.Dispose();
            image = new CanvasRenderTarget(device, width, height, 96);
        }
        using (var parcelDrawing = image.CreateDrawingSession())
        {
            parcelDrawing.Clear(Colors.Transparent);
            parcelDrawing.Transform = Matrix3x2.CreateScale((float)densityX, (float)densityY) *
                Matrix3x2.CreateTranslation(-left, -top);
            DrawMonopolySpace(parcelDrawing, MonopolyGame.Spaces[index], game, aspect);
        }
        _monopolyEntranceParcels[index] = (image, logicalBounds);
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

    private static void DrawMonopolyEntranceContactShadow(CanvasDrawingSession ds, CrownDeedParcel parcel, float elevation)
    {
        float separation = Math.Clamp(elevation / 190, 0, 1);
        float spread = 3 + separation * 8;
        var previous = ds.Transform;
        ds.Transform = parcel.Transform * previous;
        try
        {
            for (int ring = 3; ring >= 0; ring--)
            {
                float outset = spread * ring / 3;
                byte alpha = (byte)(18 * (1 - separation * .65f));
                ds.FillRoundedRectangle(new Rect(-outset, 3 - outset,
                    parcel.Width + outset * 2, parcel.Depth + outset * 2),
                    2 + outset, 2 + outset, ThemeColor(0, 8, 4, alpha));
            }
        }
        finally { ds.Transform = previous; }
    }

    private void DisposeMonopolyEntranceLayers()
    {
        _monopolyEntranceBaseTarget?.Dispose();
        _monopolyEntranceTileAtlas?.Dispose();
        _monopolyEntranceLidTarget?.Dispose();
        for (int index = 0; index < _monopolyEntranceParcels.Length; index++)
        {
            _monopolyEntranceParcels[index]?.Image.Dispose();
            _monopolyEntranceParcels[index] = null;
        }
        _monopolyEntranceBaseTarget = _monopolyEntranceTileAtlas = _monopolyEntranceLidTarget = null;
        _monopolyEntranceBaseReady = false;
        _monopolyEntranceTileState = null;
        _monopolyEntranceLidState = null;
    }
}
