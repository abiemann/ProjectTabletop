using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.App.Projection.CrownDeedRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<CrownDeedWaterShader>? _crownDeedWater;
    private CanvasDevice? _crownDeedWaterDevice;
    private CanvasGeometry? _crownDeedWaterGeometry;
    private CanvasRenderTarget? _crownDeedWaterMask;
    private CanvasRenderTarget? _crownDeedWaterTarget;
    private (long Frame, double Aspect)? _crownDeedWaterLayerState;
    private DateTimeOffset? _crownDeedWaterEpoch;

    // Traced in the original 1254-square city painting, expressed in board UVs.
    // Bridges, boats, vegetation and lamp plinths remain stationary cutouts.
    internal static readonly Vector2[][] CrownDeedWaterCanalOutlines =
    [
        [
            new(0.000f, 224.083f), new(18.341f, 224.880f), new(36.683f, 227.273f), new(56.619f, 232.855f),
            new(72.568f, 241.627f), new(84.530f, 251.994f), new(84.530f, 261.563f), new(78.947f, 276.715f),
            new(74.960f, 291.866f), new(71.770f, 307.018f), new(70.973f, 318.979f), new(67.783f, 334.928f),
            new(63.796f, 350.877f), new(61.404f, 366.029f), new(58.214f, 383.573f), new(57.416f, 398.724f),
            new(55.024f, 413.078f), new(55.821f, 418.660f), new(43.860f, 417.065f), new(35.885f, 416.268f),
            new(24.721f, 419.458f), new(11.962f, 421.053f), new(4.785f, 423.445f), new(7.974f, 414.673f),
            new(14.354f, 409.091f), new(17.544f, 398.724f), new(19.936f, 381.978f), new(20.734f, 366.029f),
            new(22.329f, 353.270f), new(27.911f, 340.510f), new(30.303f, 322.967f), new(31.898f, 307.815f),
            new(30.303f, 295.056f), new(27.113f, 283.094f), new(20.734f, 275.917f), new(11.962f, 267.943f),
            new(0.000f, 262.360f)
        ],
        [
            new(0.000f, 478.469f), new(8.772f, 473.684f), new(17.544f, 472.887f), new(23.923f, 476.874f),
            new(27.911f, 485.646f), new(34.290f, 488.038f), new(39.075f, 483.254f), new(43.062f, 480.064f),
            new(50.239f, 476.874f), new(57.416f, 479.266f), new(59.809f, 486.443f), new(59.011f, 500.000f),
            new(59.011f, 515.949f), new(59.809f, 531.898f), new(57.416f, 555.024f), new(56.619f, 574.163f),
            new(59.809f, 590.112f), new(62.998f, 598.086f), new(61.404f, 602.871f), new(55.821f, 599.681f),
            new(51.037f, 600.478f), new(44.657f, 608.453f), new(35.885f, 611.643f), new(29.506f, 602.871f),
            new(22.329f, 589.314f), new(17.544f, 574.163f), new(11.962f, 563.796f), new(9.569f, 553.429f),
            new(4.785f, 546.252f), new(0.000f, 541.467f)
        ],
        [
            new(913.876f, 244.019f), new(922.648f, 237.640f), new(933.014f, 231.260f), new(944.976f, 225.678f),
            new(958.533f, 222.488f), new(972.887f, 220.893f), new(1000.000f, 219.298f), new(1000.000f, 258.373f),
            new(992.026f, 263.955f), new(984.051f, 271.930f), new(979.266f, 279.107f), new(973.684f, 287.879f),
            new(970.494f, 299.043f), new(972.089f, 307.018f), new(972.887f, 320.574f), new(974.482f, 336.523f),
            new(976.077f, 350.080f), new(977.671f, 366.029f), new(980.064f, 382.775f), new(980.064f, 398.724f),
            new(982.456f, 410.686f), new(984.848f, 417.065f), new(977.671f, 418.660f), new(972.089f, 418.660f),
            new(972.089f, 412.281f), new(963.317f, 410.686f), new(961.722f, 417.065f), new(953.748f, 417.863f),
            new(946.571f, 421.053f), new(946.571f, 414.673f), new(950.558f, 406.699f), new(946.571f, 400.319f),
            new(946.571f, 392.344f), new(950.558f, 385.965f), new(952.153f, 379.585f), new(950.558f, 371.611f),
            new(946.571f, 364.434f), new(940.989f, 357.257f), new(938.596f, 346.093f), new(934.609f, 334.131f),
            new(933.812f, 321.372f), new(933.014f, 315.789f), new(933.014f, 307.815f), new(930.622f, 302.233f),
            new(935.407f, 294.258f), new(937.002f, 287.081f), new(933.812f, 279.904f), new(931.419f, 274.322f),
            new(931.419f, 263.955f), new(925.837f, 255.183f), new(915.470f, 249.601f)
        ],
        [
            new(943.381f, 486.443f), new(945.774f, 480.064f), new(950.558f, 476.874f), new(955.343f, 482.456f),
            new(959.330f, 488.836f), new(965.710f, 490.431f), new(972.089f, 484.848f), new(976.874f, 478.469f),
            new(984.051f, 476.077f), new(994.418f, 479.266f), new(1000.000f, 484.051f), new(1000.000f, 518.341f),
            new(988.836f, 523.923f), new(986.443f, 542.265f), new(980.861f, 557.416f), new(980.064f, 574.163f),
            new(981.659f, 590.112f), new(980.861f, 599.681f), new(966.507f, 602.073f), new(956.938f, 603.668f),
            new(952.153f, 617.225f), new(943.381f, 625.997f), new(939.394f, 621.212f), new(942.584f, 607.656f),
            new(945.774f, 595.694f), new(948.166f, 584.530f), new(948.963f, 570.175f), new(952.153f, 562.201f),
            new(956.938f, 552.632f), new(958.533f, 544.657f), new(958.533f, 536.683f), new(952.951f, 528.708f),
            new(948.166f, 523.923f), new(944.179f, 515.949f), new(943.381f, 501.595f)
        ]
    ];
    internal static readonly Vector2[][] CrownDeedWaterCanalHoles =
    [
        [
            new(35.885f, 304.625f), new(43.062f, 313.397f), new(45.455f, 330.941f), new(45.455f, 350.877f),
            new(40.670f, 365.231f), new(32.695f, 379.585f), new(27.113f, 366.029f), new(24.721f, 348.485f),
            new(27.911f, 330.144f), new(27.911f, 314.195f)
        ],
        [
            new(76.555f, 249.601f), new(85.327f, 253.589f), new(90.112f, 263.158f), new(87.719f, 272.727f),
            new(78.947f, 279.904f), new(72.568f, 276.715f), new(66.986f, 266.348f), new(69.378f, 255.981f)
        ],
        [
            new(70.973f, 278.309f), new(78.947f, 279.904f), new(82.137f, 288.676f), new(78.947f, 299.043f),
            new(77.352f, 306.220f), new(82.935f, 307.815f), new(84.530f, 318.182f), new(77.352f, 322.967f),
            new(68.581f, 318.979f), new(67.783f, 305.423f), new(70.175f, 296.651f), new(67.783f, 288.676f),
            new(67.783f, 281.499f)
        ],
        [
            new(57.416f, 362.839f), new(63.796f, 365.231f), new(67.783f, 375.598f), new(75.758f, 381.978f),
            new(78.947f, 394.737f), new(72.568f, 405.104f), new(66.986f, 419.458f), new(59.809f, 420.255f),
            new(55.024f, 407.496f), new(55.821f, 390.750f), new(51.834f, 381.180f), new(51.037f, 370.813f)
        ],
        [
            new(29.506f, 414.673f), new(39.075f, 413.876f), new(41.467f, 419.458f), new(39.872f, 426.635f),
            new(30.303f, 426.635f)
        ],
        [
            new(18.341f, 557.416f), new(24.721f, 562.201f), new(27.911f, 574.163f), new(25.518f, 587.719f),
            new(19.936f, 598.086f), new(13.557f, 591.707f), new(11.164f, 578.150f), new(11.962f, 565.391f)
        ],
        [
            new(0.000f, 523.126f), new(7.974f, 529.506f), new(15.152f, 541.467f), new(18.341f, 555.024f),
            new(11.164f, 566.188f), new(0.000f, 573.365f)
        ],
        [
            new(54.226f, 527.113f), new(61.404f, 529.506f), new(64.593f, 539.075f), new(62.201f, 547.847f),
            new(65.391f, 558.214f), new(62.998f, 567.783f), new(57.416f, 572.568f), new(52.632f, 562.998f),
            new(53.429f, 552.632f), new(50.239f, 543.860f), new(51.037f, 534.290f)
        ],
        [
            new(929.825f, 228.868f), new(937.799f, 232.057f), new(942.584f, 240.829f), new(944.179f, 253.589f),
            new(940.191f, 266.348f), new(935.407f, 271.132f), new(931.419f, 263.955f), new(926.635f, 252.791f),
            new(925.837f, 241.627f), new(927.432f, 234.450f)
        ],
        [
            new(961.722f, 306.220f), new(968.102f, 310.207f), new(971.292f, 321.372f), new(972.887f, 334.131f),
            new(970.494f, 344.498f), new(964.912f, 353.270f), new(957.735f, 344.498f), new(953.748f, 330.144f),
            new(954.545f, 315.789f)
        ],
        [
            new(967.305f, 347.687f), new(972.887f, 351.675f), new(977.671f, 361.244f), new(979.266f, 372.408f),
            new(975.279f, 381.180f), new(970.494f, 386.762f), new(964.912f, 380.383f), new(961.722f, 368.421f),
            new(961.722f, 357.257f)
        ],
        [
            new(979.266f, 543.062f), new(987.241f, 546.252f), new(992.823f, 554.226f), new(993.620f, 568.581f),
            new(991.228f, 583.732f), new(985.646f, 594.099f), new(979.266f, 598.086f), new(974.482f, 589.314f),
            new(972.089f, 574.163f), new(972.887f, 558.214f)
        ],
        [
            new(938.596f, 526.316f), new(945.774f, 527.113f), new(949.761f, 534.290f), new(948.963f, 543.860f),
            new(948.166f, 551.834f), new(945.774f, 565.391f), new(940.191f, 567.783f), new(935.407f, 561.404f),
            new(936.204f, 547.847f), new(935.407f, 537.480f)
        ]
    ];

    private readonly record struct CrownDeedReflection(Vector2 Seed, Vector2 Direction, float Length, float Width);
    private static readonly CrownDeedReflection[] CrownDeedReflections =
    [
        new(new(35.088f, 232.855f), new(0.040f, 1.000f), 38.278f, 11.164f),
        new(new(67.783f, 299.043f), new(-0.120f, 1.000f), 52.632f, 7.177f),
        new(new(51.834f, 384.370f), new(-0.100f, 1.000f), 31.898f, 6.380f),
        new(new(33.493f, 491.228f), new(0.020f, 1.000f), 47.847f, 12.759f),
        new(new(51.834f, 546.252f), new(-0.080f, 1.000f), 42.265f, 6.380f),
        new(new(969.697f, 231.260f), new(-0.030f, 1.000f), 41.467f, 11.962f),
        new(new(939.394f, 299.043f), new(0.100f, 1.000f), 50.239f, 6.380f),
        new(new(956.938f, 390.750f), new(0.080f, 1.000f), 31.100f, 6.380f),
        new(new(964.115f, 491.228f), new(-0.020f, 1.000f), 51.037f, 13.557f),
        new(new(964.115f, 551.834f), new(0.070f, 1.000f), 44.657f, 6.380f)
    ];
    private static readonly Rect[] CrownDeedWaterPatches = CrownDeedWaterCanalOutlines.Select(points =>
    {
        double x = Math.Max(0, points.Min(p => p.X) - 2), y = Math.Max(0, points.Min(p => p.Y) - 2);
        return new Rect(x, y, Math.Min(1000, points.Max(p => p.X) + 2) - x,
            Math.Min(1000, points.Max(p => p.Y) + 2) - y);
    }).ToArray();

    internal static CanvasGeometry CreateCrownDeedWaterGeometry(CanvasDevice device)
    {
        static CanvasGeometry Trace(CanvasDevice device, Vector2[] points)
        {
            using var path = new CanvasPathBuilder(device);
            path.BeginFigure(points[0]);
            foreach (var point in points.Skip(1)) path.AddLine(point);
            path.EndFigure(CanvasFigureLoop.Closed);
            return CanvasGeometry.CreatePath(path);
        }
        var result = Trace(device, CrownDeedWaterCanalOutlines[0]);
        try
        {
            foreach (var outline in CrownDeedWaterCanalOutlines.Skip(1))
            {
                using var part = Trace(device, outline);
                var combined = result.CombineWith(part, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                result.Dispose(); result = combined;
            }
            foreach (var hole in CrownDeedWaterCanalHoles)
            {
                using var part = Trace(device, hole);
                var combined = result.CombineWith(part, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                result.Dispose(); result = combined;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private bool EnsureCrownDeedWaterResources(CanvasDevice device)
    {
        if (EnsureCrownDeedCity(device) is not { } city) return false;
        if (_crownDeedWaterDevice != device) DisposeCrownDeedWater();
        _crownDeedWaterDevice = device;
        if (_crownDeedWaterMask is null)
        {
            var size = city.SizeInPixels;
            _crownDeedWaterGeometry = CreateCrownDeedWaterGeometry(device);
            _crownDeedWaterMask = new CanvasRenderTarget(device, size.Width, size.Height, 96);
            using var drawing = _crownDeedWaterMask.CreateDrawingSession();
            drawing.Transform = Matrix3x2.CreateScale(size.Width / 1000f, size.Height / 1000f);
            // Opaque metadata avoids interpreting coverage as premultiplied data.
            drawing.Clear(Colors.Black);
            drawing.FillGeometry(_crownDeedWaterGeometry, Colors.Red);
            drawing.Blend = CanvasBlend.Add;
            foreach (var lamp in CrownDeedReflections)
            {
                var direction = Vector2.Normalize(lamp.Direction);
                var center = lamp.Seed + direction * lamp.Length * .36f;
                using var light = new CanvasRadialGradientBrush(device,
                [
                    new() { Position = 0, Color = ThemeColor(0, 210, 0) },
                    new() { Position = .40f, Color = ThemeColor(0, 140, 0, 180) },
                    new() { Position = 1, Color = Colors.Transparent }
                ]) { Center = center, RadiusX = lamp.Width * .5f, RadiusY = lamp.Length * .62f };
                var previous = drawing.Transform;
                drawing.Transform = Matrix3x2.CreateRotation(-MathF.Atan2(direction.X, direction.Y), center) * previous;
                drawing.FillEllipse(center, light.RadiusX, light.RadiusY, light);
                drawing.Transform = previous;
            }
        }
        _crownDeedWater ??= new PixelShaderEffect<CrownDeedWaterShader>();
        _crownDeedWater.Sources[0] = city;
        _crownDeedWater.Sources[1] = _crownDeedWaterMask;
        return true;
    }

    private void ClearCrownDeedWater(CanvasDrawingSession drawing)
    {
        if (!EnsureCrownDeedWaterResources(drawing.Device)) return;
        var previous = drawing.Blend;
        try
        {
            drawing.Blend = CanvasBlend.Copy;
            drawing.FillGeometry(_crownDeedWaterGeometry!, Colors.Transparent);
        }
        finally { drawing.Blend = previous; }
    }

    private void DrawCrownDeedWater(CanvasDrawingSession drawing, DateTimeOffset now, double aspect)
    {
        if (!EnsureCrownDeedWaterResources(drawing.Device)) return;
        _crownDeedWaterEpoch ??= now;
        var size = _crownDeedCityBitmap!.SizeInPixels;
        var scale = new Float2(size.Width / 1000f, size.Height / 1000f);
        float time = (float)Math.Max(0, (now - _crownDeedWaterEpoch.Value).TotalSeconds);
        _crownDeedWater!.ConstantBuffer = new(scale, time, (float)aspect, 1);
        foreach (var patch in CrownDeedWaterPatches)
            drawing.DrawImage(_crownDeedWater, patch,
                new Rect(patch.X * scale.X, patch.Y * scale.Y, patch.Width * scale.X, patch.Height * scale.Y),
                1, CanvasImageInterpolation.Linear);
    }

    // Only the narrow canals redraw at native board density. The expensive city,
    // deeds, captions and entrance layers have no water time in their cache keys.
    private CanvasRenderTarget? DrawCrownDeedWaterLayer(CanvasDevice device, DateTimeOffset now, double aspect)
    {
        if (!EnsureCrownDeedWaterResources(device)) return null;
        _crownDeedWaterEpoch ??= now;
        if (EnsureBoardRenderTarget(ref _crownDeedWaterTarget, device)) _crownDeedWaterLayerState = null;
        long frame = Math.Max(0, (now - _crownDeedWaterEpoch.Value).Ticks) / (TimeSpan.TicksPerSecond / 60);
        var key = (frame, aspect);
        if (_crownDeedWaterLayerState != key)
        {
            using var drawing = _crownDeedWaterTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_crownDeedWaterTarget);
            drawing.Clear(Colors.Transparent);
            DrawCrownDeedWater(drawing, now, aspect);
            _crownDeedWaterLayerState = key;
        }
        return _crownDeedWaterTarget;
    }

    private void DisposeCrownDeedWater()
    {
        _crownDeedWater?.Dispose(); _crownDeedWater = null;
        _crownDeedWaterGeometry?.Dispose(); _crownDeedWaterGeometry = null;
        _crownDeedWaterMask?.Dispose(); _crownDeedWaterMask = null;
        _crownDeedWaterTarget?.Dispose(); _crownDeedWaterTarget = null;
        _crownDeedWaterDevice = null; _crownDeedWaterLayerState = null;
        // Recreating GPU resources or visiting another board never restarts time.
    }
}
