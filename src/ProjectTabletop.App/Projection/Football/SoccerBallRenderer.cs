using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ProjectTabletop.App.Projection.Football;

/// <summary>
/// Native port of Alexander Biemann's SpotBop SoccerBallRenderer: the same twelve
/// spherical pentagons, thirty seams and hemisphere clipping, driven by the
/// simulation's rolling quaternion. The caller owns height and the cast shadow.
/// </summary>
internal sealed class SoccerBallRenderer
{
    private const int PanelCount = 12;
    private const int PanelVertices = 5;
    private const int PanelStride = 6;
    private const int HorizonSegments = 4;
    private const float RingRadius = .894427191f;
    private const float RingZ = .4472135955f;
    private static readonly Vector3[] Surface = BuildSurface();
    private static readonly Vector3[] Seams = BuildSeams();
    private readonly Vector3[] _rotated = new Vector3[Surface.Length];
    private readonly Vector3[] _rotatedSeams = new Vector3[Seams.Length];
    private readonly Vector2[] _clipped = new Vector2[PanelVertices + HorizonSegments];

    internal void Draw(CanvasDrawingSession ds, Vector2 center, float radius, Quaternion rotation)
    {
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(radius) * Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            ds.FillCircle(Vector2.Zero, 1, Color.FromArgb(255, 255, 253, 240));
            for (int i = 0; i < Surface.Length; i++) _rotated[i] = Vector3.Transform(Surface[i], rotation);
            for (int i = 0; i < Seams.Length; i++) _rotatedSeams[i] = Vector3.Transform(Seams[i], rotation);

            var seam = Color.FromArgb(150, 143, 160, 160);
            for (int i = 0; i < _rotatedSeams.Length; i += 3)
            {
                DrawVisibleLine(ds, _rotatedSeams[i], _rotatedSeams[i + 1], seam, .035f);
                DrawVisibleLine(ds, _rotatedSeams[i + 1], _rotatedSeams[i + 2], seam, .035f);
            }
            for (int panel = 0; panel < PanelCount; panel++)
            {
                float fade = Math.Clamp((_rotated[panel * PanelStride].Z - .02f) / .34f, 0, 1);
                if (fade <= 0) continue;
                fade = fade * fade * (3 - 2 * fade);
                int count = ClipPanel(panel * PanelStride, out int edgeCount, out bool closes);
                if (count < 3) continue;
                var ink = Color.FromArgb((byte)(255 * fade), 38, 52, 58);
                using var path = new CanvasPathBuilder(ds.Device);
                path.BeginFigure(_clipped[0]);
                for (int v = 1; v < count; v++) path.AddLine(_clipped[v]);
                path.EndFigure(CanvasFigureLoop.Closed);
                using var geometry = CanvasGeometry.CreatePath(path);
                ds.FillGeometry(geometry, ink);
                // The limb is already the sphere edge: stroke only real panel edges.
                for (int v = 0; v < edgeCount; v++)
                    ds.DrawLine(_clipped[v], _clipped[closes ? (v + 1) % count : v + 1], ink, .025f);
            }

            using var sphereShade = new CanvasRadialGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = Color.FromArgb(0, 10, 25, 32) },
                new() { Position = .48f, Color = Color.FromArgb(0, 10, 25, 32) },
                new() { Position = .82f, Color = Color.FromArgb(25, 10, 25, 32) },
                new() { Position = 1, Color = Color.FromArgb(110, 10, 25, 32) }
            ]) { Center = new(-.3f, -.4f), RadiusX = 1.55f, RadiusY = 1.55f };
            ds.FillCircle(Vector2.Zero, 1, sphereShade);
            using var shine = new CanvasRadialGradientBrush(ds.Device,
                Color.FromArgb(135, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
                { Center = new(-.34f, -.47f), RadiusX = .29f, RadiusY = .19f };
            ds.FillEllipse(new(-.34f, -.47f), .29f, .19f, shine);
            ds.DrawCircle(Vector2.Zero, .995f, Color.FromArgb(205, 28, 37, 34), .045f);
        }
        finally { ds.Transform = previous; }
    }

    private int ClipPanel(int start, out int edgeCount, out bool closes)
    {
        int insideCount = 0, entry = -1;
        for (int v = 0; v < PanelVertices; v++)
        {
            if (_rotated[start + 1 + v].Z < 0) continue;
            insideCount++;
            if (_rotated[start + 1 + (v + PanelVertices - 1) % PanelVertices].Z < 0) entry = v;
        }
        closes = insideCount == PanelVertices;
        edgeCount = 0;
        if (insideCount == 0) return 0;
        if (closes)
        {
            for (int v = 0; v < PanelVertices; v++) _clipped[v] = XY(_rotated[start + 1 + v]);
            edgeCount = PanelVertices;
            return PanelVertices;
        }
        int count = 0;
        var before = _rotated[start + 1 + (entry + PanelVertices - 1) % PanelVertices];
        var current = _rotated[start + 1 + entry];
        _clipped[count++] = Horizon(before, current);
        int index = entry;
        while (current.Z >= 0)
        {
            _clipped[count++] = XY(current);
            before = current;
            index = (index + 1) % PanelVertices;
            current = _rotated[start + 1 + index];
        }
        _clipped[count++] = Horizon(before, current);
        edgeCount = count - 1;
        var exit = _clipped[count - 1];
        for (int segment = 1; segment < HorizonSegments; segment++)
            _clipped[count++] = Vector2.Normalize(Vector2.Lerp(exit, _clipped[0], (float)segment / HorizonSegments));
        return count;
    }

    private static void DrawVisibleLine(CanvasDrawingSession ds, Vector3 a, Vector3 b, Color color, float width)
    {
        if (a.Z < 0 && b.Z < 0) return;
        var from = XY(a);
        var to = XY(b);
        if ((a.Z < 0) != (b.Z < 0))
        {
            var limb = Horizon(a, b);
            if (a.Z < 0) from = limb; else to = limb;
        }
        ds.DrawLine(from, to, color, width);
    }

    private static Vector2 Horizon(Vector3 a, Vector3 b) =>
        Vector2.Normalize(XY(Vector3.Lerp(a, b, a.Z / (a.Z - b.Z))));
    private static Vector2 XY(Vector3 point) => new(point.X, point.Y);

    private static Vector3[] BuildSurface()
    {
        var output = new Vector3[PanelCount * PanelStride];
        int panel = 0;
        WritePanel(output, panel++, Vector3.UnitZ);
        for (int i = 0; i < 5; i++)
        {
            float angle = -MathF.PI / 2 + i * MathF.Tau / 5;
            WritePanel(output, panel++, new(MathF.Cos(angle) * RingRadius, MathF.Sin(angle) * RingRadius, RingZ));
        }
        for (int i = 0; i < 5; i++)
        {
            float angle = -MathF.PI / 2 + MathF.PI / 5 + i * MathF.Tau / 5;
            WritePanel(output, panel++, new(MathF.Cos(angle) * RingRadius, MathF.Sin(angle) * RingRadius, -RingZ));
        }
        WritePanel(output, panel, -Vector3.UnitZ);
        return output;
    }

    private static void WritePanel(Vector3[] output, int panel, Vector3 center)
    {
        int start = panel * PanelStride;
        output[start] = center;
        float radial = new Vector2(center.X, center.Y).Length();
        var u = radial > .0001f ? new Vector3(-center.Y / radial, center.X / radial, 0) : Vector3.UnitX;
        var v = radial > .0001f ? Vector3.Cross(center, u) : center.Z >= 0 ? Vector3.UnitY : -Vector3.UnitY;
        for (int i = 0; i < PanelVertices; i++)
        {
            float angle = -MathF.PI / 2 + i * MathF.Tau / PanelVertices;
            output[start + 1 + i] = center * MathF.Cos(.255f) + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * MathF.Sin(.255f);
        }
    }

    private static Vector3[] BuildSeams()
    {
        var points = new List<Vector3>(90);
        for (int first = 0; first < PanelCount; first++)
        for (int second = first + 1; second < PanelCount; second++)
        {
            var a = Surface[first * PanelStride];
            var b = Surface[second * PanelStride];
            if (MathF.Abs(Vector3.Dot(a, b) - RingZ) > .0001f) continue;
            points.Add(a);
            points.Add(Vector3.Normalize(a + b));
            points.Add(b);
        }
        return points.ToArray();
    }
}
