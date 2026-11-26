namespace UCut.Editing;

/// <summary>
/// Finds the lower edge of the facecam in a vertical clip: the strongest horizontal edge in the upper part of the frame.
/// Used when no fixed logo position is configured.
/// </summary>
public static class CamEdgeDetector
{
    /// <param name="gray">8-bit grey pixels, row by row.</param>
    /// <returns>The edge as a fraction of the height, or null when no clear edge exists.</returns>
    public static double? FindEdge(ReadOnlySpan<byte> gray, int width, int height, double searchFrom = 0.15, double searchTo = 0.65)
    {
        if (gray.Length < width * height || height < 4)
        {
            return null;
        }

        var from = Math.Max(1, (int)(height * searchFrom));
        var to = Math.Min(height - 2, (int)(height * searchTo));
        double best = 0, second = 0;
        var bestRow = -1;
        var scores = new double[height];
        for (var r = from; r <= to; r++)
        {
            long sum = 0;
            var a = gray.Slice(r * width, width);
            var b = gray.Slice((r + 1) * width, width);
            for (var c = 0; c < width; c++)
            {
                sum += Math.Abs(a[c] - b[c]);
            }

            scores[r] = (double)sum / width;
        }

        for (var r = from; r <= to; r++)
        {
            if (scores[r] > best)
            {
                best = scores[r];
                bestRow = r;
            }
        }

        // The edge must stand out clearly from the rest of the picture (ignore rows right next to the best one).
        for (var r = from; r <= to; r++)
        {
            if (Math.Abs(r - bestRow) > 2 && scores[r] > second)
            {
                second = scores[r];
            }
        }

        if (bestRow < 0 || best < 12 || best < second * 1.3)
        {
            return null;
        }

        return (bestRow + 1.0) / height;
    }
}
