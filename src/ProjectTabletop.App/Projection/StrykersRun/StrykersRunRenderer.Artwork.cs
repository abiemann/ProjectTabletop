using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private static void FindSpriteCells(CanvasBitmap bitmap, int rows, Rect[] cells, int columns = 4,
        bool sharedVerticalBounds = true)
    {
        int width = (int)bitmap.SizeInPixels.Width, height = (int)bitmap.SizeInPixels.Height;
        byte[] pixels = bitmap.GetPixelBytes();
        int[] rowInk = new int[height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (pixels[(y * width + x) * 4 + 3] >= 32) rowInk[y]++;
        int[] rowCuts = new int[rows + 1];
        rowCuts[rows] = height;
        for (int row = 1; row < rows; row++) rowCuts[row] = FindCut(rowInk, row * height / rows, height / rows / 3);

        for (int row = 0; row < rows; row++)
        {
            int top = rowCuts[row], bottom = rowCuts[row + 1];
            while (top < bottom && rowInk[top] == 0) top++;
            while (bottom > top && rowInk[bottom - 1] == 0) bottom--;
            if (bottom <= top) throw new InvalidDataException("Stryker's Run sprite atlas has an empty animation row.");
            top = Math.Max(rowCuts[row], top - 3);
            bottom = Math.Min(rowCuts[row + 1], bottom + 3);
            int[] columnInk = new int[width];
            for (int y = top; y < bottom; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] >= 32) columnInk[x]++;
            int[] cuts = new int[columns + 1];
            cuts[columns] = width;
            for (int column = 1; column < columns; column++)
                cuts[column] = FindCut(columnInk, column * width / columns, width / columns / 3);
            for (int column = 0; column < columns; column++)
            {
                int left = cuts[column], right = cuts[column + 1];
                while (left < right && columnInk[left] == 0) left++;
                while (right > left && columnInk[right - 1] == 0) right--;
                if (right <= left) throw new InvalidDataException("Stryker's Run sprite atlas has an empty animation frame.");
                left = Math.Max(cuts[column], left - 3);
                right = Math.Min(cuts[column + 1], right + 3);
                int frameTop = top, frameBottom = bottom;
                if (!sharedVerticalBounds)
                {
                    bool HasInk(int y)
                    {
                        for (int x = left; x < right; x++)
                            if (pixels[(y * width + x) * 4 + 3] >= 32) return true;
                        return false;
                    }
                    while (frameTop < frameBottom && !HasInk(frameTop)) frameTop++;
                    while (frameBottom > frameTop && !HasInk(frameBottom - 1)) frameBottom--;
                    frameTop = Math.Max(top, frameTop - 3);
                    frameBottom = Math.Min(bottom, frameBottom + 3);
                }
                // Animation poses share a baseline. Independent boss forms use
                // their own alpha bounds so every pair of feet meets the road.
                cells[row * columns + column] = new Rect(left, frameTop, right - left, frameBottom - frameTop);
            }
        }
    }

    private static int FindCut(int[] ink, int expected, int radius)
    {
        int best = expected, bestInk = int.MaxValue;
        int start = Math.Max(1, expected - radius), end = Math.Min(ink.Length - 2, expected + radius);
        for (int index = start; index <= end; index++)
        {
            int amount = ink[index - 1] + ink[index] + ink[index + 1];
            if (amount > bestInk || amount == bestInk && Math.Abs(index - expected) >= Math.Abs(best - expected)) continue;
            best = index; bestInk = amount;
        }
        return best;
    }
}
