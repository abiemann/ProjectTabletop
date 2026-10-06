using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly TimeSpan CrownDeedDiceAnimationDuration = TimeSpan.FromSeconds(3.6);
    private CrownDeedRoll? _crownDeedDiceRoll;
    private CrownDeedDicePath[] _crownDeedDicePaths = [];
    private bool _crownDeedDiceSettled;
    private long _crownDeedDicePresentationRevision;

    private sealed record CrownDeedDicePath(int Edge, int Seed, Vector2 Origin, Vector2 Control,
        Vector2 Impact, Vector2 RollStart, Vector2 Destination, Vector3 Spin, float Phase);

    internal sealed record CrownDeedDiceFaceFrame(int Value, Vector3 Normal, IReadOnlyList<Vector2> Corners);
    internal sealed record CrownDeedDiceFrame(long Sequence, int DieIndex, int Result, int Edge, int Seed,
        Vector2 StartCenter, Vector2 GroundCenter, Vector2 Center, Vector2 Destination,
        float Height, float Size, Quaternion Rotation, float Progress, bool Settled,
        int FrontFace, IReadOnlyList<CrownDeedDiceFaceFrame> VisibleFaces);

    internal long CrownDeedDicePresentationRevision => _crownDeedDicePresentationRevision;

    // Raised once for a committed rules roll. Visual randomness is captured
    // here, independently of the game's dice/card random source. A preview,
    // projection, or diagnostic read of the same frame consumes no randomness.
    private void OnCrownDeedRoll(CrownDeedRoll roll)
    {
        if (roll.Current.Dice.First is < 1 or > 6 || roll.Current.Dice.Second is < 1 or > 6) return;
        _crownDeedDiceRoll = roll;
        _crownDeedDiceSettled = false;
        int firstEdge = Random.Shared.Next(4);
        _crownDeedDicePaths =
        [
            CreateCrownDeedDicePath(firstEdge, new Vector2(691, 576)),
            CreateCrownDeedDicePath((firstEdge + 1 + Random.Shared.Next(3)) % 4, new Vector2(737, 576))
        ];
        _crownDeedDicePresentationRevision++;
        _boardSession.HoldCrownDeedPresentationUntil(roll.StartedAt + CrownDeedDiceAnimationDuration);
    }

    private static CrownDeedDicePath CreateCrownDeedDicePath(int edge, Vector2 destination)
    {
        int seed = Random.Shared.Next();
        var random = new Random(seed);
        float Between(float low, float high) => low + (high - low) * (float)random.NextDouble();
        var origin = edge switch
        {
            0 => new Vector2(-105, Between(170, 820)),
            1 => new Vector2(1105, Between(170, 820)),
            2 => new Vector2(Between(170, 820), -105),
            _ => new Vector2(Between(170, 820), 1105)
        };
        var impact = new Vector2(Between(315, 640), Between(325, 525));
        var midpoint = Vector2.Lerp(origin, impact, .53f);
        var control = midpoint + new Vector2(Between(-105, 105), Between(-95, 95));
        var rollStart = destination + new Vector2(Between(-75, -34), Between(-24, 16));
        var spin = new Vector3(Between(3.5f, 5.5f), Between(2.5f, 4.5f), Between(1.2f, 2.6f));
        if (random.Next(2) == 0) spin.Y = -spin.Y;
        if (random.Next(2) == 0) spin.Z = -spin.Z;
        return new(edge, seed, origin, control, impact, rollStart, destination, spin, Between(0, MathF.Tau));
    }

    private void CancelCrownDeedDiceAnimation()
    {
        bool hadPresentation = _crownDeedDiceRoll is not null || _crownDeedDevelopment is not null;
        CancelCrownDeedDevelopment();
        RetireCrownDeedDicePresentation();
        if (hadPresentation) _boardSession.CancelCrownDeedPresentation(_crownDeedClock());
    }

    private void RetireCrownDeedDicePresentation()
    {
        if (_crownDeedDiceRoll is null) return;
        _crownDeedDiceRoll = null;
        _crownDeedDicePaths = [];
        _crownDeedDiceSettled = false;
        _crownDeedDicePresentationRevision++;
    }

    private void SynchronizeCrownDeedDicePresentation(DateTimeOffset now)
    {
        if (_crownDeedDiceRoll is null) return;
        var current = _boardSession.CrownDeedState;
        if (_boardSession.Screen != BoardScreen.CrownDeed ||
            current.Phase is CrownDeedPhase.Landing or CrownDeedPhase.Setup or CrownDeedPhase.ExitConfirmation or
                CrownDeedPhase.Saving or CrownDeedPhase.GameOver || current.Dice != _crownDeedDiceRoll.Current.Dice)
        {
            // Retiring an earlier roll must not erase a later accepted build or
            // release its presentation hold when actions arrive before redraw.
            RetireCrownDeedDicePresentation();
            if (GetCrownDeedDevelopmentFrame(now)?.Active != true)
                _boardSession.CancelCrownDeedPresentation(now);
            return;
        }
        if (!_crownDeedDiceSettled && now - _crownDeedDiceRoll.StartedAt >= CrownDeedDiceAnimationDuration)
        {
            _crownDeedDiceSettled = true;
            _crownDeedDicePresentationRevision++;
        }
    }

    private bool HasCrownDeedDiceAnimation(DateTimeOffset now)
    {
        SynchronizeCrownDeedDicePresentation(now);
        return _crownDeedDiceRoll is not null && !_crownDeedDiceSettled;
    }

    private bool HasCrownDeedDicePresentation(DateTimeOffset now)
    {
        SynchronizeCrownDeedDicePresentation(now);
        return _crownDeedDiceRoll is not null;
    }

    private CrownDeedSnapshot CrownDeedPresentedState(DateTimeOffset now)
    {
        return HasCrownDeedDiceAnimation(now)
            ? _crownDeedDiceRoll!.Previous with { Dice = default, Status = "Rolling the dice…", LastCard = "" }
            : _boardSession.CrownDeedState;
    }

    internal IReadOnlyList<CrownDeedDiceFrame> GetCrownDeedDiceFrames(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!HasCrownDeedDicePresentation(now)) return Array.Empty<CrownDeedDiceFrame>();
            var roll = _crownDeedDiceRoll!;
            float progress = Math.Clamp((float)((now - roll.StartedAt).TotalSeconds /
                CrownDeedDiceAnimationDuration.TotalSeconds), 0, 1);
            return _crownDeedDicePaths.Select((path, index) => CrownDeedDicePose(roll.Sequence, index,
                index == 0 ? roll.Current.Dice.First : roll.Current.Dice.Second, path, progress)).ToArray();
        }
    }

    public object GetCrownDeedDiceAnimationDiagnostics(DateTimeOffset now)
    {
        lock (_gate)
        {
            var frames = GetCrownDeedDiceFrames(now);
            return new
            {
                durationMilliseconds = CrownDeedDiceAnimationDuration.TotalMilliseconds,
                presentationRevision = _crownDeedDicePresentationRevision,
                active = HasCrownDeedDiceAnimation(now), present = _crownDeedDiceRoll is not null,
                sequence = _crownDeedDiceRoll?.Sequence,
                result = _crownDeedDiceRoll?.Current.Dice,
                frames = frames.Select(frame => new
                {
                    frame.DieIndex, frame.Result, frame.Edge, frame.Seed, frame.Progress, frame.Settled,
                    frame.Height, frame.Size, frame.FrontFace,
                    start = new { x = frame.StartCenter.X, y = frame.StartCenter.Y },
                    ground = new { x = frame.GroundCenter.X, y = frame.GroundCenter.Y },
                    center = new { x = frame.Center.X, y = frame.Center.Y },
                    destination = new { x = frame.Destination.X, y = frame.Destination.Y },
                    rotation = new { x = frame.Rotation.X, y = frame.Rotation.Y, z = frame.Rotation.Z, w = frame.Rotation.W },
                    faces = frame.VisibleFaces.Select(face => new
                    {
                        face.Value, normal = new { x = face.Normal.X, y = face.Normal.Y, z = face.Normal.Z },
                        corners = face.Corners.Select(point => new { x = point.X, y = point.Y }).ToArray()
                    }).ToArray()
                }).ToArray()
            };
        }
    }

    private static CrownDeedDiceFrame CrownDeedDicePose(long sequence, int index, int result,
        CrownDeedDicePath path, float progress)
    {
        // The retained, settled pose is an exact state rather than the tail of
        // floating-point easing. It is shared by both native canvases and never
        // jitters or retains a residual bounce after the presentation deadline.
        if (progress >= 1)
        {
            var finalRotation = CrownDeedDiceFinalRotation(result, index);
            var finalFaces = CrownDeedDiceFaces(path.Destination, 30, finalRotation);
            int finalFrontFace = finalFaces.OrderByDescending(face => face.Normal.Z).First().Value;
            return new(sequence, index, result, path.Edge, path.Seed, path.Origin,
                path.Destination, path.Destination, path.Destination, 0, 30, finalRotation, 1, true,
                finalFrontFace, finalFaces);
        }
        Vector2 ground;
        float height;
        if (progress < .25f)
        {
            float u = progress / .25f;
            float opposite = 1 - u;
            ground = path.Origin * (opposite * opposite) + path.Control * (2 * opposite * u) + path.Impact * (u * u);
            height = MathF.Sin(u * MathF.PI) * 105;
        }
        else if (progress < .72f)
        {
            float u = (progress - .25f) / .47f;
            ground = Vector2.Lerp(path.Impact, path.RollStart, 1 - (1 - u) * (1 - u));
            float bounce = Math.Min(2, MathF.Floor(u * 3));
            height = MathF.Abs(MathF.Sin(u * 3 * MathF.PI)) * (bounce switch { 0 => 51, 1 => 29, _ => 13 });
        }
        else
        {
            float u = (progress - .72f) / .28f;
            ground = Vector2.Lerp(path.RollStart, path.Destination, 1 - MathF.Pow(1 - u, 3));
            height = MathF.Sin(u * MathF.PI) * (1 - u) * 2.5f;
        }
        float settle = Math.Clamp((progress - .62f) / .38f, 0, 1);
        settle = 1 - MathF.Pow(1 - settle, 3);
        float size = 64 + (30 - 64) * settle;
        Quaternion orientation;
        if (progress < .72f) orientation = CrownDeedDiceTumble(path, progress);
        else
        {
            float u = (progress - .72f) / .28f;
            u = u * u * (3 - 2 * u);
            orientation = Quaternion.Slerp(CrownDeedDiceTumble(path, .72f), CrownDeedDiceFinalRotation(result, index), u);
        }
        var center = ground - new Vector2(0, height);
        var faces = CrownDeedDiceFaces(center, size, orientation);
        int frontFace = faces.OrderByDescending(face => face.Normal.Z).First().Value;
        return new(sequence, index, result, path.Edge, path.Seed, path.Origin, ground, center,
            path.Destination, height, size, orientation, progress, progress >= 1, frontFace, faces);
    }

    private static Quaternion CrownDeedDiceTumble(CrownDeedDicePath path, float progress) =>
        Quaternion.CreateFromYawPitchRoll(path.Phase + progress * path.Spin.Y * MathF.Tau,
            path.Phase * .63f + progress * path.Spin.X * MathF.Tau,
            path.Phase * .41f + progress * path.Spin.Z * MathF.Tau);

    private static Quaternion CrownDeedDiceFinalRotation(int value, int index)
    {
        var face = value switch
        {
            2 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),
            3 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2),
            4 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            5 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
            6 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
            _ => Quaternion.Identity
        };
        var tilt = Quaternion.CreateFromYawPitchRoll(index == 0 ? -.20f : .21f, -.17f, index == 0 ? -.07f : .08f);
        return Quaternion.Normalize(Quaternion.Concatenate(face, tilt));
    }

    private readonly record struct CrownDeedDieFace(int Value, Vector3 Normal, Vector3 Right, Vector3 Down);
    private static readonly CrownDeedDieFace[] CrownDeedCubeFaces =
    [
        new(1, Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY),
        new(6, -Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY),
        new(3, Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY),
        new(4, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY),
        new(2, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        new(5, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ)
    ];

    private static CrownDeedDiceFaceFrame[] CrownDeedDiceFaces(Vector2 center, float size, Quaternion rotation) =>
        CrownDeedCubeFaces.Select(face =>
        {
            var normal = Vector3.Transform(face.Normal, rotation);
            Vector2 Point(float x, float y) => CrownDeedDiceProject(
                Vector3.Transform((face.Normal + face.Right * x + face.Down * y) * (size / 2), rotation), center);
            return new CrownDeedDiceFaceFrame(face.Value, normal,
                new[] { Point(-1, -1), Point(1, -1), Point(1, 1), Point(-1, 1) });
        }).Where(face => face.Normal.Z > .015f).OrderBy(face => face.Normal.Z).ToArray();

    private static Vector2 CrownDeedDiceProject(Vector3 position, Vector2 center)
    {
        float perspective = 320 / Math.Max(180, 320 - position.Z);
        return center + new Vector2(position.X, -position.Y) * perspective;
    }

    private void DrawCrownDeedDiceAnimation(CanvasDrawingSession drawing, DateTimeOffset now, double boardAspect)
    {
        foreach (var frame in GetCrownDeedDiceFrames(now))
        {
            using (var aspect = new CrownDeedArtAspect(drawing, frame.GroundCenter, boardAspect))
            {
                float opacity = 1 / (1 + frame.Height * .026f);
                var shadow = frame.GroundCenter + new Vector2(3, 6);
                float rx = frame.Size * (.64f + frame.Height * .001f), ry = frame.Size * .21f;
                drawing.FillEllipse(shadow, rx + 4, ry + 2, ThemeColor(0, 8, 4, (byte)(24 * opacity)));
                drawing.FillEllipse(shadow, rx, ry, ThemeColor(0, 7, 3, (byte)(100 * opacity)));
            }
            using (var aspect = new CrownDeedArtAspect(drawing, frame.Center, boardAspect))
                DrawCrownDeedCube(drawing, frame);
        }
    }

    private static void DrawCrownDeedCube(CanvasDrawingSession drawing, CrownDeedDiceFrame frame)
    {
        var light = Vector3.Normalize(new Vector3(-.35f, .48f, .82f));
        foreach (var visible in frame.VisibleFaces)
        {
            var face = CrownDeedCubeFaces.Single(face => face.Value == visible.Value);
            float illumination = .54f + .46f * Math.Max(0, Vector3.Dot(visible.Normal, light));
            Color Shade(byte r, byte g, byte b, byte alpha = 255) => ThemeColor((byte)(r * illumination),
                (byte)(g * illumination), (byte)(b * illumination), alpha);
            using var shape = CrownDeedDiceRoundedFace(drawing.Device, visible.Corners, .10f);
            var top = visible.Corners.MinBy(point => point.Y);
            var bottom = visible.Corners.MaxBy(point => point.Y);
            using var ivory = new CanvasLinearGradientBrush(drawing.Device,
                Shade(253, 248, 229), Shade(229, 214, 178)) { StartPoint = top, EndPoint = bottom };
            drawing.FillGeometry(shape, ivory);
            drawing.DrawGeometry(shape, Shade(141, 113, 55), Math.Max(.7f, frame.Size * .022f));
            var faceCenter = visible.Corners.Aggregate(Vector2.Zero, (sum, corner) => sum + corner) / visible.Corners.Count;
            var inset = visible.Corners.Select(corner => Vector2.Lerp(faceCenter, corner, .90f)).ToArray();
            using var bevel = CrownDeedDiceRoundedFace(drawing.Device, inset, .09f);
            drawing.DrawGeometry(bevel, Shade(236, 207, 135, 170), Math.Max(.4f, frame.Size * .010f));

            foreach (var pip in CrownDeedDicePips(face.Value))
            {
                var points = new Vector2[16];
                float pipRadius = .133f;
                for (int point = 0; point < points.Length; point++)
                {
                    float angle = point * MathF.Tau / points.Length;
                    float x = pip.X + MathF.Cos(angle) * pipRadius;
                    float y = pip.Y + MathF.Sin(angle) * pipRadius;
                    var local = (face.Normal * 1.002f + face.Right * x + face.Down * y) * (frame.Size / 2);
                    points[point] = CrownDeedDiceProject(Vector3.Transform(local, frame.Rotation), frame.Center);
                }
                using var cavity = CanvasGeometry.CreatePolygon(drawing.Device, points);
                drawing.FillGeometry(cavity, Shade(23, 38, 29));
                drawing.DrawGeometry(cavity, Shade(112, 98, 62, 160), Math.Max(.3f, frame.Size * .009f));
            }
        }
    }

    private static CanvasGeometry CrownDeedDiceRoundedFace(CanvasDevice device,
        IReadOnlyList<Vector2> corners, float radius)
    {
        using var path = new CanvasPathBuilder(device);
        path.BeginFigure(Vector2.Lerp(corners[0], corners[1], radius));
        for (int index = 1; index <= corners.Count; index++)
        {
            int current = index % corners.Count;
            var corner = corners[current];
            path.AddLine(Vector2.Lerp(corner, corners[(current + corners.Count - 1) % corners.Count], radius));
            path.AddCubicBezier(corner, corner, Vector2.Lerp(corner, corners[(current + 1) % corners.Count], radius));
        }
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private static IEnumerable<Vector2> CrownDeedDicePips(int value)
    {
        if (value is 1 or 3 or 5) yield return Vector2.Zero;
        if (value >= 2) { yield return new(-.46f, -.46f); yield return new(.46f, .46f); }
        if (value >= 4) { yield return new(.46f, -.46f); yield return new(-.46f, .46f); }
        if (value == 6) { yield return new(-.46f, 0); yield return new(.46f, 0); }
    }
}
