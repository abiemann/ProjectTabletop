#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed record StrykersRunSceneryRenderResult(string[] Images, int CityTravelPixels,
    double ShiftMeanError, double RepeatMeanError, bool FrozenWithRoad,
    int ScarfRunFrames, bool ScarfChangesSide, bool HeroBodyUnmirrored);

/// <summary>Camera-free native checks of the painted scenery's actual GPU travel.</summary>
internal static class StrykersRunSceneryVerification
{
    internal static async Task<StrykersRunSceneryRenderResult> RunAsync(CanvasDevice device, string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new StrykersRunRenderer(device);
        await renderer.EnsureReadyAsync();
        Require(renderer.Ready && renderer.Error is null, "City artwork did not load.");
        using var target = new CanvasRenderTarget(device, 900, 1600, 96);
        var images = new List<string>();
        byte[] first = City(0);
        await Save("city-start");
        byte[] advanced = City(20);
        await Save("city-travel-88px");
        // The unchanged road travels 400 screen pixels over this distance;
        // the distant city must travel downward by exactly 22% of that (88 px).
        double shifted = MeanError(first, advanced, 88);
        // Fractional source-pixel sampling can differ slightly after scaling.
        Require(shifted < 2 && MeanError(first, advanced, 0) > shifted + 2 &&
            MeanError(first, advanced, -88) > shifted + 2,
            "The city did not travel downward at 22% of the road's texture speed.");
        byte[] repeated = City(1 / (.0125f * .22f));
        double repeatError = MeanError(first, repeated, 0);
        Require(repeatError < 1.25, "The city did not repeat the same pixels after one full travel cycle.");
        foreach (float phase in new[] { .48f, .50f, .52f })
        {
            City(phase / (.0125f * .22f));
            await Save($"city-seam-{(int)(phase * 100):00}");
        }
        var paused = new StrykersRunSnapshot(StrykersRunPhase.WaitingForInput,
            .5f, 1, 0, 480, 1, 60, 0, false, "", 1, [], [], [], [])
        { Level = 1, LevelSeconds = 60, Stage = StrykersRunStage.Boss };
        byte[] frozen = Scene(paused, 13, 72);
        Require(frozen.AsSpan().SequenceEqual(Scene(paused, 42, 72)),
            "Wall-clock time moved the city while the road was stopped.");
        await Save("city-paused-scene");
        Scene(paused with { Phase = StrykersRunPhase.Playing, Stage = StrykersRunStage.Running }, 13,
            .50f / (.0125f * .22f));
        await Save("city-seam-in-scene");
        byte[]? previousRun = null;
        for (int frame = 0; frame < 4; frame++)
        {
            // Stay away from frame boundaries when converting the run clock to
            // the four sprite poses. Only the cloth direction changes below.
            var running = paused with { Phase = StrykersRunPhase.Playing,
                Stage = StrykersRunStage.Running, RunSeconds = 60 + (frame + .25f) / 10 };
            byte[] edgeOn = Scene(running with { ScarfSide = 0 }, 13, 72);
            await Save($"scarf-edge-on-frame-{frame:00}");
            byte[] left = Scene(running with { ScarfSide = -1 }, 13, 72);
            await Save($"scarf-left-frame-{frame:00}");
            byte[] right = Scene(running with { ScarfSide = 1 }, 13, 72);
            await Save($"scarf-right-frame-{frame:00}");
            var leftCloth = Changes(edgeOn, left, 300, 1070, 300, 90);
            var rightCloth = Changes(edgeOn, right, 300, 1070, 300, 90);
            Require(leftCloth.Count > 50 && rightCloth.Count > 50 &&
                rightCloth.CenterX - leftCloth.CenterX > 25,
                $"Run frame {frame} did not show the scarf distinctly on each side of its edge-on pose.");
            Require(Changes(left, right, 350, 1160, 200, 80).Count == 0,
                $"Changing scarf side mirrored or moved the hero's body in run frame {frame}.");
            if (previousRun is not null)
                Require(Changes(previousRun, edgeOn, 350, 1090, 200, 160).Count > 100,
                    "Successive hero run frames did not visibly animate.");
            previousRun = edgeOn;
        }
        return new(images.ToArray(), 88, shifted, repeatError, true, 4, true, true);

        byte[] City(float distance)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                renderer.DrawCityBackground(drawing, distance);
            }
            return target.GetPixelBytes();
        }
        byte[] Scene(StrykersRunSnapshot state, double elapsed, float distance)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                renderer.Draw(drawing, new(0, 0, 900, 1600), state, elapsed, distance);
            }
            return target.GetPixelBytes();
        }
        async Task Save(string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask();
            images.Add(path);
        }
    }

    private static double MeanError(byte[] before, byte[] after, int shift)
    {
        long total = 0, count = 0;
        for (int y = Math.Max(8, 8 - shift); y < Math.Min(1592, 1592 - shift); y += 7)
        for (int x = 8; x < 892; x += 7)
        {
            int a = (y * 900 + x) * 4, b = ((y + shift) * 900 + x) * 4;
            for (int channel = 0; channel < 3; channel++)
            { total += Math.Abs(before[a + channel] - after[b + channel]); count++; }
        }
        return total / (double)count;
    }

    private static (int Count, double CenterX) Changes(byte[] before, byte[] after,
        int left, int top, int width, int height)
    {
        int count = 0;
        long sumX = 0;
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int i = (y * 900 + x) * 4;
            if (Math.Abs(before[i] - after[i]) <= 8 && Math.Abs(before[i + 1] - after[i + 1]) <= 8 &&
                Math.Abs(before[i + 2] - after[i + 2]) <= 8) continue;
            count++;
            sumX += x;
        }
        return (count, count == 0 ? 0 : sumX / (double)count);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
