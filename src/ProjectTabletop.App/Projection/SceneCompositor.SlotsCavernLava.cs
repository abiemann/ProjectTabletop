using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection.SlotsRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<SlotCavernLavaShader>? _slotCavernLava;
    private CanvasDevice? _slotCavernLavaDevice;
    private CanvasGeometry? _slotCavernLavaClip;
    private double _slotCavernLavaAspect;
    private DateTimeOffset? _slotCavernLavaEpoch;

    // Traced in the original 1536x1024 painting. The foreground rock cuts off
    // the fall at its foot; neither that rock nor the dragon claw is displaced.
    private static readonly Vector2[] SlotCavernFallOutline =
    [
        new(1314, 440), new(1313, 446), new(1314, 454), new(1313, 468),
        new(1313, 480), new(1311, 492), new(1313, 503), new(1315, 514),
        new(1315, 531), new(1317, 550), new(1313, 557), new(1315, 561),
        new(1321, 558), new(1327, 560), new(1328, 556), new(1325, 550),
        new(1325, 535), new(1323, 522), new(1321, 508), new(1321, 497),
        new(1319, 485), new(1319, 471), new(1318, 458), new(1316, 447)
    ];
    private static readonly Vector2[] SlotCavernPoolOutline =
    [
        new(1297, 557), new(1307, 555), new(1314, 552), new(1323, 552),
        new(1331, 555), new(1340, 557), new(1345, 560), new(1343, 563),
        new(1334, 561), new(1325, 561), new(1320, 558), new(1313, 561),
        new(1304, 563), new(1298, 565)
    ];
    private static readonly Vector2[] SlotCavernVentOutline =
    [
        new(1295, 204), new(1301, 209), new(1305, 216), new(1309, 226),
        new(1315, 232), new(1313, 239), new(1310, 245), new(1309, 249),
        new(1317, 251), new(1317, 255), new(1306, 256), new(1299, 254),
        new(1293, 255), new(1291, 247), new(1288, 241), new(1284, 240),
        new(1283, 233), new(1285, 225), new(1288, 218), new(1291, 211)
    ];

    // Separate exposed left falls and heat pockets: gaps retain their original
    // foreground rock, column edges and dragon scales.
    private static readonly Vector2[][] SlotLeftCavernOutlines =
    [
        [
            new(178, 338), new(181, 346), new(185, 350), new(185, 356),
            new(189, 362), new(189, 368), new(186, 375), new(183, 383),
            new(183, 391), new(176, 399), new(173, 406), new(177, 415),
            new(176, 425), new(176, 434), new(177, 442), new(178, 450),
            new(176, 459), new(177, 469), new(173, 475), new(174, 480),
            new(180, 478), new(188, 480), new(194, 479), new(191, 473),
            new(189, 465), new(188, 454), new(187, 443), new(188, 432),
            new(186, 420), new(182, 411), new(181, 405), new(187, 398),
            new(190, 388), new(190, 376), new(196, 369), new(199, 367),
            new(194, 360), new(193, 349), new(187, 346), new(186, 337),
        ],
        [
            new(178, 472), new(183, 471), new(187, 473), new(190, 477),
            new(194, 478), new(190, 479), new(186, 478), new(182, 477),
            new(178, 479), new(175, 478), new(175, 476),
        ],
        [
            new(192, 482), new(197, 483), new(201, 483), new(207, 480),
            new(210, 481), new(211, 487), new(215, 489), new(222, 490),
            new(226, 492), new(218, 491), new(208, 494), new(201, 492),
            new(198, 490), new(193, 489), new(190, 487), new(190, 484),
        ],
        [
            new(224, 497), new(228, 494), new(234, 491), new(238, 492),
            new(239, 499), new(245, 501), new(252, 504), new(264, 507),
            new(264, 509), new(247, 509), new(234, 507), new(228, 506),
            new(224, 504), new(221, 502), new(222, 500),
        ],
        [
            new(165, 560), new(169, 563), new(172, 567), new(175, 573),
            new(174, 582), new(176, 591), new(177, 600), new(176, 612),
            new(177, 625), new(180, 638), new(178, 650), new(182, 664),
            new(182, 680), new(183, 691), new(176, 689), new(170, 686),
            new(166, 685), new(162, 687), new(162, 674), new(164, 663),
            new(161, 652), new(162, 640), new(161, 632), new(163, 620),
            new(162, 610), new(162, 601), new(164, 590), new(164, 578),
            new(166, 570), new(164, 565),
        ],
        [
            new(162, 682), new(168, 680), new(174, 682), new(180, 686),
            new(183, 690), new(176, 689), new(170, 686), new(166, 685),
            new(162, 687),
        ],
        [
            new(209, 675), new(215, 679), new(223, 679), new(229, 678),
            new(230, 689), new(229, 702), new(230, 715), new(229, 730),
            new(231, 747), new(225, 746), new(219, 743), new(215, 743),
            new(208, 746), new(200, 747), new(195, 749), new(190, 747),
            new(188, 740), new(188, 729), new(185, 720), new(186, 707),
            new(188, 696), new(193, 683), new(198, 680), new(205, 679),
            new(208, 679),
        ],
        [
            new(189, 741), new(195, 738), new(201, 742), new(209, 741),
            new(216, 737), new(222, 741), new(228, 744), new(230, 747),
            new(224, 746), new(219, 743), new(215, 743), new(208, 746),
            new(201, 747), new(195, 749), new(190, 747),
        ],
        [
            new(298, 713), new(301, 714), new(303, 719), new(301, 729),
            new(301, 741), new(300, 750), new(302, 759), new(304, 768),
            new(302, 778), new(303, 790), new(303, 802), new(306, 815),
            new(306, 829), new(296, 829), new(295, 818), new(296, 807),
            new(294, 795), new(296, 782), new(294, 770), new(295, 760),
            new(295, 749), new(296, 737), new(296, 726), new(297, 719),
        ]
    ];


    private void DrawSlotCavernLava(CanvasDrawingSession ds, DateTimeOffset now, double aspect, SlotLayout layout)
    {
        EnsureSlotArtwork(ds.Device);
        if (_slotBackdrop is null) return;
        var source = SlotBackdropSource(aspect);
        float sx = (float)_slotBackdrop.Size.Width / 1536, sy = (float)_slotBackdrop.Size.Height / 1024;
        bool rightVisible = source.Right > 1283 * sx && source.X < 1345 * sx
            && source.Bottom > 204 * sy && source.Y < 565 * sy;
        bool leftVisible = source.Right > 161 * sx && source.X < 306 * sx
            && source.Bottom > 337 * sy && source.Y < 829 * sy;
        if (!rightVisible && !leftVisible) return;
        _slotCavernLavaEpoch ??= now;
        if (_slotCavernLavaDevice != ds.Device)
        {
            DisposeSlotCavernLava();
            _slotCavernLavaDevice = ds.Device;
        }
        if (_slotCavernLavaClip is null || _slotCavernLavaAspect != aspect)
        {
            _slotCavernLavaClip?.Dispose();
            _slotCavernLavaClip = CreateSlotCavernLavaClip(ds.Device, source, sx, sy, layout);
            _slotCavernLavaAspect = aspect;
        }
        _slotCavernLava ??= new PixelShaderEffect<SlotCavernLavaShader>();
        _slotCavernLava.Sources[0] = _slotBackdrop;
        float time = (float)Math.Max(0, (now - _slotCavernLavaEpoch.Value).TotalSeconds);
        _slotCavernLava.ConstantBuffer = new SlotCavernLavaShader(new Float2(sx, sy), time,
            (float)(source.Y / sy), (float)(source.Height / sy));
        using (ds.CreateLayer(1, _slotCavernLavaClip))
        {
            if (rightVisible) DrawPatch(new Rect(1278 * sx, 198 * sy, 74 * sx, 374 * sy));
            if (leftVisible) DrawPatch(new Rect(156 * sx, 330 * sy, 156 * sx, 506 * sy));
        }
        void DrawPatch(Rect patch)
        {
            var box = new Rect((patch.X - source.X) / source.Width * 1000,
                (patch.Y - source.Y) / source.Height * 1000,
                patch.Width / source.Width * 1000, patch.Height / source.Height * 1000);
            ds.DrawImage(_slotCavernLava, box, patch);
        }
    }

    private static CanvasGeometry CreateSlotCavernLavaClip(CanvasDevice device, Rect source,
        float sx, float sy, SlotLayout layout)
    {
        CanvasGeometry Trace(Vector2[] points)
        {
            using var path = new CanvasPathBuilder(device);
            Vector2 Map(Vector2 p) => new((float)((p.X * sx - source.X) / source.Width * 1000),
                (float)((p.Y * sy - source.Y) / source.Height * 1000));
            path.BeginFigure(Map(points[0]));
            foreach (var point in points.Skip(1)) path.AddLine(Map(point));
            path.EndFigure(CanvasFigureLoop.Closed);
            return CanvasGeometry.CreatePath(path);
        }
        using var fall = Trace(SlotCavernFallOutline);
        using var pool = Trace(SlotCavernPoolOutline);
        using var vent = Trace(SlotCavernVentOutline);
        using var foot = fall.CombineWith(pool, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        using var right = foot.CombineWith(vent, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        CanvasGeometry TraceLeft()
        {
            var result = Trace(SlotLeftCavernOutlines[0]);
            try
            {
                foreach (var points in SlotLeftCavernOutlines.Skip(1))
                {
                    using var part = Trace(points);
                    var next = result.CombineWith(part, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    result.Dispose();
                    result = next;
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        using var left = TraceLeft();
        using var lava = right.CombineWith(left, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        using var interior = SlotCutPanel(device, new Rect(20, 20, 960, 960), 22);
        using var upper = CanvasGeometry.CreateRectangle(device, new Rect(0, 0, 1000, 800));
        using var bounded = interior.CombineWith(upper, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        using var background = lava.CombineWith(bounded, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        // The live layer is above the cached cabinet: explicitly retain its
        // stationary foreground and the camera-observed control references.
        using var marquee = SlotCutPanel(device, new Rect(193, 15, 614, 78), 17);
        using var crownClip = background.CombineWith(marquee, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var reels = CanvasGeometry.CreateRectangle(device,
            new Rect(layout.Left - 25, layout.Top - 25, layout.Width + 50, layout.Height + 50));
        using var reelClip = crownClip.CombineWith(reels, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var leftMeter = CanvasGeometry.CreateRectangle(device,
            new Rect(500 - 222 / layout.Aspect, 94, 144 / layout.Aspect, 46));
        using var centerMeter = CanvasGeometry.CreateRectangle(device,
            new Rect(500 - 72 / layout.Aspect, 94, 144 / layout.Aspect, 46));
        using var rightMeter = CanvasGeometry.CreateRectangle(device,
            new Rect(500 + 78 / layout.Aspect, 94, 144 / layout.Aspect, 46));
        using var a = reelClip.CombineWith(leftMeter, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var b = a.CombineWith(centerMeter, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var headers = b.CombineWith(rightMeter, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var keyRail = CanvasGeometry.CreateRectangle(device,
            new Rect(layout.Left - 25, 630, layout.Width + 50, 55));
        using var keys = headers.CombineWith(keyRail, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var keyCaption = CanvasGeometry.CreateRectangle(device,
            new Rect(layout.Left - 65, 684, layout.Width + 130, 22));
        using var counters = keys.CombineWith(keyCaption, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var status = CanvasGeometry.CreateRectangle(device, new Rect(30, 700, 940, 37));
        using var statusClip = counters.CombineWith(status, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var credits = CanvasGeometry.CreateRectangle(device, new Rect(54, 734, 282, 106));
        using var bet = CanvasGeometry.CreateRectangle(device, new Rect(359, 734, 282, 106));
        using var win = CanvasGeometry.CreateRectangle(device, new Rect(664, 734, 282, 106));
        using var c = statusClip.CombineWith(credits, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var d = c.CombineWith(bet, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        return d.CombineWith(win, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
    }

    private void DisposeSlotCavernLava()
    {
        _slotCavernLava?.Dispose();
        _slotCavernLava = null;
        _slotCavernLavaClip?.Dispose();
        _slotCavernLavaClip = null;
        _slotCavernLavaDevice = null;
    }
}
