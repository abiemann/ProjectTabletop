using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal readonly record struct SlotWildRun(int Reel, int Row, int Count, double Age);

    // Presentation only: a tall guardian covers precisely the adjacent WILD
    // cells already evaluated by SlotGame. It never expands a winning area.
    internal static IReadOnlyList<SlotWildRun> SlotsWildPresentation(SlotSnapshot game, DateTimeOffset now)
    {
        if (game.InRespins || game.Phase is SlotPhase.VaultIntro or SlotPhase.VaultPicking or SlotPhase.VaultOutro) return [];
        var runs = new List<SlotWildRun>();
        for (int reel = 0; reel < SlotGame.Reels; reel++)
        {
            double age = game.SpinStartedAt == DateTimeOffset.MinValue ? 1000
                : (now - game.SpinStartedAt - SlotGame.ReelStop(reel, game.FreeSpin)).TotalSeconds;
            if (age < .22) continue;
            for (int row = SlotGame.BaseFirstRow; row < SlotGame.BaseFirstRow + SlotGame.BaseRowCount; row++)
            {
                if (game.Cell(reel, row).Symbol != SlotSymbol.Wild) continue;
                int start = row;
                while (row + 1 < SlotGame.BaseFirstRow + SlotGame.BaseRowCount && game.Cell(reel, row + 1).Symbol == SlotSymbol.Wild) row++;
                runs.Add(new(reel, start, row - start + 1, age));
            }
        }
        return runs;
    }

    private void DrawSlotWildRun(CanvasDrawingSession ds, SlotSnapshot game, SlotWildRun run, SlotLayout layout)
    {
        var first = layout.Cell(run.Reel, run.Row, 1, 3);
        var rect = new Rect(first.X + 3 / layout.Aspect, first.Y + 2,
            first.Width - 6 / layout.Aspect, first.Height * run.Count - 4);
        bool winning = game.Phase == SlotPhase.LineWins && game.LineWins.Any(line => line.Cells.Any(cell =>
            cell.Reel == run.Reel && cell.Row >= run.Row && cell.Row < run.Row + run.Count));
        float winPulse = winning ? .5f + .5f * MathF.Sin(_slotVfxTime * 6) : 0;
        if (run.Count == 1)
        {
            DrawSlotWildTile(ds, rect, layout.Aspect, run.Age, 1, winPulse);
            return;
        }

        // Each mouth gets a sustained, distinct breath before the fire joins.
        // Hide the heads only inside the dense wall, then reveal the guardian
        // as that wall clears. The spin clock continues across game phases.
        float coalesce = (float)Ease((run.Age - 1.30) / .32);
        float headOpacity = (float)(1 - Ease((run.Age - 1.56) / .10));
        float merge = (float)Ease((run.Age - 1.95) / .52);
        DrawSlotWildFrame(ds, rect, layout.Aspect, Math.Max(coalesce, merge), coalesce * (1 - merge) + winPulse * merge);
        if (headOpacity > .001f)
        {
            for (int offset = 0; offset < run.Count; offset++)
            {
                var cell = layout.Cell(run.Reel, run.Row + offset, 1, 3);
                var head = new Rect(cell.X + 3 / layout.Aspect, cell.Y + 2, cell.Width - 6 / layout.Aspect, cell.Height - 4);
                DrawSlotWildFrame(ds, head, layout.Aspect, headOpacity, 0);
            }
            for (int offset = 0; offset < run.Count; offset++)
            {
                var cell = layout.Cell(run.Reel, run.Row + offset, 1, 3);
                var head = new Rect(cell.X + 3 / layout.Aspect, cell.Y + 2, cell.Width - 6 / layout.Aspect, cell.Height - 4);
                DrawSlotWildTile(ds, head, layout.Aspect, run.Age, headOpacity, 0, merging: true, frame: false);
            }
        }
        if (run.Age >= 1.95)
        {
            float breathe = MathF.Sin(_slotVfxTime * 2.1f + run.Reel);
            float inset = 3 / layout.Aspect;
            var art = new Rect(rect.X + inset, rect.Y + 4,
                rect.Width - 2 * inset, rect.Height - 29 + breathe * 1.4);
            float roar = (float)(1 - Ease((run.Age - 2.54) / .28));
            float pose = Math.Max(roar * 2, winning ? .6f * winPulse : 0);
            // The actor is opaque underneath the fire. A moving clearance
            // front reveals its head, chest and feet rather than fading it in.
            DrawSlotWildActor(ds, _slotWildColossus, art, layout.Aspect, pose, 1, tall: true);
            DrawSlotWildLegend(ds, rect, layout.Aspect, 1);
        }
        DrawSlotWildCorona(ds, rect, layout.Aspect, run.Reel * 17 + run.Row, merge * (.32f + winPulse * .65f));
    }

    private void DrawSlotWildRunFire(CanvasDrawingSession ds, SlotWildRun run, SlotLayout layout)
    {
        if (run.Count < 2) return;
        var first = layout.Cell(run.Reel, run.Row, 1, 3);
        var rect = new Rect(first.X + 3 / layout.Aspect, first.Y + 2,
            first.Width - 6 / layout.Aspect, first.Height * run.Count - 4);
        float breath = (float)Ease((run.Age - .64) / .21);
        float swell = (float)Ease((run.Age - 1.08) / .27);
        float coalesce = (float)Ease((run.Age - 1.30) / .32);
        float merge = (float)Ease((run.Age - 1.95) / .52);
        float decayProgress = (float)Math.Clamp((run.Age - 3.8) / 1.4, 0, 1);
        float edgeDecay = decayProgress * decayProgress * (3 - 2 * decayProgress);
        if (breath > .001f && edgeDecay < 1)
        {
            var head = new Rect(first.X + 3 / layout.Aspect, first.Y + 2, first.Width - 6 / layout.Aspect, first.Height - 4);
            var visual = SlotWildHeadVisual(head, layout.Aspect, run.Age, merging: true, 0);
            float mouthY = (float)((visual.Mouth.Y - rect.Y) / first.Height);
            // Fire is a foreground effect: its crown and side tongues can lick
            // over the frame without widening the actual WILD or its awards.
            var fire = new Rect(rect.X - rect.Width * .12, rect.Y - rect.Height * .065,
                rect.Width * 1.24, rect.Height * 1.09);
            DrawSlotWildFire(ds, fire, layout.Aspect, (float)run.Age, run.Reel * 2.3f + 1,
                breath, run.Count, swell, coalesce, mouthY, merge, (float)(first.Height / rect.Height),
                edgeDecay, rect);
        }
        float finalBreath = (float)(Ease((run.Age - 2.10) / .14)
            * (1 - Ease((run.Age - 2.72) / .16)));
        if (finalBreath > .001f)
        {
            float breathe = MathF.Sin(_slotVfxTime * 2.1f + run.Reel);
            float inset = 3 / layout.Aspect;
            var art = new Rect(rect.X + inset, rect.Y + 4, rect.Width - 2 * inset, rect.Height - 29 + breathe * 1.4);
            var mouth = SlotWildTallMouth(art, layout.Aspect);
            float mouthY = (float)((mouth.Y - rect.Y) / rect.Height);
            DrawSlotWildFire(ds, rect, layout.Aspect, (float)run.Age, run.Reel * 2.3f + 1,
                finalBreath, 1, 0, 0, mouthY, (float)Ease((run.Age - 2.54) / .34));
        }
    }

    private void DrawSlotWildTile(CanvasDrawingSession ds, Rect rect, float aspect, double age, float opacity,
        float winPulse = 0, bool merging = false, bool frame = true)
    {
        if (opacity <= 0) return;
        // Negative age is the quiet cached icon used on a moving reel. No clock
        // dependent decoration may enter the static symbol atlas.
        var visual = SlotWildHeadVisual(rect, aspect, age, merging, winPulse);
        if (frame) DrawSlotWildFrame(ds, rect, aspect, opacity, visual.Impact + winPulse * .4f);
        var previous = ds.Transform;
        ds.Transform = visual.Transform * previous;
        try
        {
            DrawSlotWildActor(ds, _slotWildPortraits, visual.Art, aspect, visual.Pose, opacity,
                tall: false, portraitCrop: visual.Crop);
        }
        finally { ds.Transform = previous; }
        DrawSlotWildLegend(ds, rect, aspect, opacity);
        if (age >= 0) DrawSlotWildCorona(ds, rect, aspect, (int)rect.X, opacity * (.2f + visual.Impact * .8f + winPulse * .5f));
    }

    private readonly record struct SlotWildHeadGeometry(Rect Art, Matrix3x2 Transform, Vector2 Mouth,
        float Pose, float Crop, float Impact);

    private static SlotWildHeadGeometry SlotWildHeadVisual(Rect rect, float aspect, double age, bool merging, float winPulse)
    {
        bool live = age >= 0;
        float impact = live ? (float)(Math.Sin(Math.Clamp((age - .22) / .46, 0, 1) * Math.PI)
            * Math.Exp(-Math.Max(0, age - .22) * .6)) : 0;
        float approach = live ? (float)(Ease((age - .30) / .28)
            * (merging ? 1 : 1 - Ease((age - .92) / .32))) : 0;
        float roar = live ? (float)(Ease((age - .39) / .22)
            * (1 - Ease((age - (merging ? 1.56 : .88)) / .23))) : 0;
        float pose = Math.Max(roar * 2, winPulse * .75f);
        var art = new Rect(rect.X - rect.Width * .01, rect.Y - rect.Height * .03,
            rect.Width * 1.02, rect.Height * .90);
        double scale = Math.Min(art.Width * aspect / 600, art.Height / 600);
        float height = (float)(600 * scale);
        float top = (float)(art.Y + (art.Height - height) * .5);
        var pivot = new Vector2((float)(art.X + art.Width / 2), top + height * .82f);
        float push = 1 + approach * .42f + impact * .055f + winPulse * .035f;
        var transform = Matrix3x2.CreateScale(push, push, pivot)
            * Matrix3x2.CreateTranslation(0, height * approach * .10f);
        var mouth = Vector2.Transform(new Vector2(pivot.X, top + (float)((300 + pose * 15) * scale)), transform);
        // Discard the lower bust as the face approaches, keeping the registered
        // horns and jaw in place. The same transform drives the mouth fire.
        return new(art, transform, mouth, pose, 600 - approach * 135, impact);
    }

    private static Vector2 SlotWildTallMouth(Rect art, float aspect)
    {
        double scale = Math.Min(art.Width * aspect / 570, art.Height / 1240);
        double height = 1240 * scale;
        double top = art.Y + (art.Height - height) * .25;
        if (height < art.Height - 1)
        {
            double torso = Math.Min(art.Height - 770 * scale, 470 * scale * 1.75);
            top = art.Y + (art.Height - 770 * scale - torso) / 2;
        }
        return new((float)(art.X + art.Width / 2), (float)(top + 320 * scale));
    }

    private static void DrawSlotWildFrame(CanvasDrawingSession ds, Rect box, float aspect, float opacity, float power)
    {
        using var dark = new CanvasLinearGradientBrush(ds.Device,
            ThemeColor(6, 13, 35, (byte)(opacity * 245)), ThemeColor(31, 12, 19, (byte)(opacity * 248)))
        { StartPoint = new((float)box.X, (float)box.Y), EndPoint = new((float)box.Right, (float)box.Bottom) };
        ds.FillRectangle(box, dark);
        using var aura = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(72, 177, 255, (byte)(opacity * Math.Min(120, 35 + power * 65))), ThemeColor(22, 50, 150, 0))
        { Center = Center(box), RadiusX = (float)box.Width * .65f, RadiusY = (float)box.Height * .65f };
        ds.FillRectangle(box, aura);
        using var metal = SlotAntiqueMetal(ds.Device, box);
        using (ds.CreateLayer(opacity))
        {
            ds.DrawRectangle(box, metal, 2.4f);
            ds.DrawRectangle(SlotInset(box, 1.7), ThemeColor(255, 239, 163, 165), .65f);
            foreach (var corner in new Vector2[] { new((float)box.X, (float)box.Y), new((float)box.Right, (float)box.Y),
                new((float)box.X, (float)box.Bottom), new((float)box.Right, (float)box.Bottom) })
            {
                float sx = corner.X < box.X + box.Width / 2 ? 1 : -1;
                float sy = corner.Y < box.Y + box.Height / 2 ? 1 : -1;
                ds.DrawLine(corner, corner + new Vector2(sx * 10 / aspect, 0), SlotIvory, 1.2f);
                ds.DrawLine(corner, corner + new Vector2(0, sy * 10), SlotGold, 1.2f);
            }
        }
    }

    private static void DrawSlotWildLegend(CanvasDrawingSession ds, Rect rect, float aspect, float opacity)
    {
        float size = Math.Min(25, (float)rect.Width * aspect * .235f);
        var label = new Rect(rect.X - 4 / aspect, rect.Bottom - size * 1.2, rect.Width + 8 / aspect, size * 1.17);
        using (ds.CreateLayer(opacity))
        {
            SlotText(ds, "WILD", new Rect(label.X + 1 / aspect, label.Y + 1.5, label.Width, label.Height), size,
                ThemeColor(36, 10, 2), aspect, "Georgia", true);
            SlotText(ds, "WILD", label, size, ThemeColor(255, 224, 126), aspect, "Georgia", true, fire: true);
        }
    }

    private void DrawSlotWildActor(CanvasDrawingSession ds, CanvasBitmap? atlas, Rect box, float aspect, float pose,
        float opacity, bool tall, float portraitCrop = 600)
    {
        if (atlas is null) return;
        int last = tall ? 1 : 2;
        pose = Math.Clamp(tall ? pose * .5f : pose, 0, last);
        int lower = (int)pose, upper = Math.Min(last, lower + 1);
        float blend = Math.Clamp(pose - lower, 0, 1);
        if (blend < .015f || lower == upper)
        {
            DrawPose(ds, lower, opacity);
            return;
        }
        using var first = new CanvasCommandList(ds.Device);
        using (var drawing = first.CreateDrawingSession()) DrawPose(drawing, lower, 1);
        using var second = new CanvasCommandList(ds.Device);
        using (var drawing = second.CreateDrawingSession()) DrawPose(drawing, upper, 1);
        using var transition = new CrossFadeEffect { Source1 = first, Source2 = second, CrossFade = blend };
        using (ds.CreateLayer(opacity)) ds.DrawImage(transition);

        void DrawPose(CanvasDrawingSession drawing, int index, float alpha)
        {
            // Crops share a registered baseline and silhouette so jaw poses
            // dissolve without moving the horns, wing edges or paws.
            var source = tall ? new Rect(index == 0 ? 56 : 630, 6, 570, 1240)
                : new Rect(96 + index * 690, 59, 600, portraitCrop);
            double fittedHeight = tall ? source.Height : 600;
            double scale = Math.Min(box.Width * aspect / source.Width, box.Height / fittedHeight);
            double width = source.Width * scale / aspect, height = source.Height * scale;
            if (tall && height < box.Height - 1)
            {
                // Extend the plated torso for taller runs while keeping the
                // face, crown of horns and feet in their original proportions.
                // This also fills narrow boards without cropping the wings.
                double head = 430 * scale, feet = 340 * scale;
                // Very narrow portrait boards cannot fit a full-height body
                // without a grotesquely stretched middle. Cap that extension
                // and let the live aura occupy the remaining framed space.
                double torso = Math.Min(box.Height - head - feet, 470 * scale * 1.75);
                double top = box.Y + (box.Height - head - torso - feet) / 2;
                double x = box.X + (box.Width - width) / 2;
                DrawSlice(0, 430, top, head);
                DrawSlice(430, 470, top + head, torso);
                DrawSlice(900, 340, top + head + torso, feet);
                return;

                void DrawSlice(double offset, double sourceHeight, double y, double targetHeight) =>
                    drawing.DrawImage(atlas, new Rect(x, y, width, targetHeight),
                        new Rect(source.X, source.Y + offset, source.Width, sourceHeight), alpha,
                        CanvasImageInterpolation.HighQualityCubic);
            }
            var destination = new Rect(box.X + (box.Width - width) / 2,
                box.Y + (box.Height - fittedHeight * scale) * (tall ? .25 : .5), width, height);
            if (!tall && portraitCrop < 599)
            {
                // Recede the lower neck softly as the face leans out; cropping
                // the bust must not leave a straight cut beneath the jaw.
                using var actor = new CanvasCommandList(drawing.Device);
                using (var image = actor.CreateDrawingSession())
                    image.DrawImage(atlas, destination, source, 1, CanvasImageInterpolation.HighQualityCubic);
                using var mask = new CanvasCommandList(drawing.Device);
                using (var masking = mask.CreateDrawingSession())
                {
                    float feather = (float)Math.Max(1, Math.Min(height * .14, (600 - portraitCrop) * scale * .38));
                    using var fade = new CanvasLinearGradientBrush(drawing.Device, ThemeColor(255, 255, 255), ThemeColor(255, 255, 255, 0))
                    {
                        StartPoint = new((float)destination.X, (float)destination.Bottom - feather),
                        EndPoint = new((float)destination.X, (float)destination.Bottom)
                    };
                    masking.FillRectangle(destination, fade);
                }
                using var faded = new AlphaMaskEffect { Source = actor, AlphaMask = mask };
                using (drawing.CreateLayer(alpha)) drawing.DrawImage(faded);
            }
            else drawing.DrawImage(atlas, destination, source, alpha, CanvasImageInterpolation.HighQualityCubic);
        }
    }

    private void DrawSlotWildCorona(CanvasDrawingSession ds, Rect box, float aspect, int seed, float opacity)
    {
        if (opacity <= .005f) return;
        opacity = Math.Min(1, opacity);
        float width = (float)box.Width, height = (float)box.Height;
        var center = Center(box);
        for (int trail = 0; trail < 3; trail++)
        {
            float angle = _slotVfxTime * (trail % 2 == 0 ? 2.4f : -1.8f) + trail * 2.1f + seed;
            Vector2 previous = default;
            for (int segment = 0; segment < 17; segment++)
            {
                float along = segment / 16f;
                float theta = angle + along * 1.6f;
                float wave = MathF.Sin(theta * 5 + _slotVfxTime * 6) * 1.5f;
                var point = center + new Vector2(MathF.Cos(theta) * (width * .44f + wave / aspect),
                    MathF.Sin(theta) * height * .46f);
                if (segment > 0)
                {
                    float alpha = MathF.Sin(along * MathF.PI) * opacity;
                    ds.DrawLine(previous, point, ThemeColor(35, 99, 255, (byte)(alpha * 85)), 5 / MathF.Sqrt(aspect));
                    ds.DrawLine(previous, point, ThemeColor(116, 211, 255, (byte)(alpha * 235)), 1.15f);
                    ds.DrawLine(previous, point, ThemeColor(240, 252, 255, (byte)(alpha * 190)), .45f);
                }
                previous = point;
            }
        }
        for (int spark = 0; spark < 7; spark++)
        {
            float phase = (_slotVfxTime * .48f + SlotRandom(seed + spark * 37)) % 1;
            float x = center.X + (SlotRandom(seed + spark * 91) - .5f) * width * .96f;
            float y = (float)box.Bottom - phase * height;
            float alpha = MathF.Sin(phase * MathF.PI) * opacity;
            ds.DrawLine(x, y, x + .8f / aspect, y + 2.5f, ThemeColor(191, 229, 255, (byte)(alpha * 220)), .85f);
        }
    }
}
