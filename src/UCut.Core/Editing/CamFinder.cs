namespace UCut.Editing;

/// <summary>
/// Finds a facecam overlay in a landscape stream picture. A cam overlay is a rectangle whose border stays in the same place
/// in every frame while the game behind it moves; inside it there is skin colour and some movement.
/// Works on a few small RGB frames (e.g. 320x180).
/// </summary>
public static class CamFinder
{
    /// <summary>Minimum score for a rectangle to count as cam.</summary>
    internal const double MinScore = 0.45;

    /// <summary>Minimum Verify score for a known cam position.</summary>
    public const double VerifyMinScore = 0.45;

    private const int EdgeThreshold = 20;
    private const int MinEdgeThreshold = 8;
    private const double StableShare = 0.75;
    private const int MaxLines = 40;

    /// <param name="frames">RGB24 frames, all <paramref name="width"/> x <paramref name="height"/>.</param>
    public static NormRect? Find(IReadOnlyList<byte[]> frames, int width, int height)
    {
        if (frames.Count < 3 || frames.Any(f => f.Length < width * height * 3))
        {
            return null;
        }

        var a = Analyze(frames, width, height);
        return a.Search();
    }

    /// <summary>
    /// Checks a cam position known from other clips: is there a moving picture with a (partly) stable frame or skin colour?
    /// Used when the search finds nothing, e.g. in very dark games.
    /// </summary>
    public static double Verify(IReadOnlyList<byte[]> frames, int width, int height, NormRect cam)
    {
        if (frames.Count < 3 || frames.Any(f => f.Length < width * height * 3))
        {
            return 0;
        }

        return Analyze(frames, width, height).Check(cam);
    }

    private static Analysis Analyze(IReadOnlyList<byte[]> frames, int width, int height)
    {
        int w = width, h = height, n = frames.Count;
        var gray = frames.Select(f => ToGray(f, w, h)).ToList();

        // Dark games have weak edges everywhere: lower the edge threshold with the picture contrast.
        var threshold = AdaptiveThreshold(gray[0]);

        // Stable edges: present in at least 75 % of the frames.
        var hEdge = new bool[(h - 1) * w]; // between row y and y+1
        var vEdge = new bool[h * (w - 1)]; // between column x and x+1
        var hCount = new int[(h - 1) * w];
        var vCount = new int[h * (w - 1)];
        foreach (var g in gray)
        {
            for (var y = 0; y < h - 1; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (Math.Abs(g[(y + 1) * w + x] - g[y * w + x]) > threshold)
                    {
                        hCount[y * w + x]++;
                    }
                }
            }

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w - 1; x++)
                {
                    if (Math.Abs(g[y * w + x + 1] - g[y * w + x]) > threshold)
                    {
                        vCount[y * (w - 1) + x]++;
                    }
                }
            }
        }

        var need = (int)Math.Ceiling(n * StableShare);
        for (var i = 0; i < hEdge.Length; i++)
        {
            hEdge[i] = hCount[i] >= need;
        }

        for (var i = 0; i < vEdge.Length; i++)
        {
            vEdge[i] = vCount[i] >= need;
        }

        // Prefix sums along the lines for fast coverage checks.
        var hPrefix = new int[(h - 1) * (w + 1)];
        for (var y = 0; y < h - 1; y++)
        {
            for (var x = 0; x < w; x++)
            {
                hPrefix[y * (w + 1) + x + 1] = hPrefix[y * (w + 1) + x] + (hEdge[y * w + x] ? 1 : 0);
            }
        }

        var vPrefix = new int[(h + 1) * (w - 1)];
        for (var x = 0; x < w - 1; x++)
        {
            for (var y = 0; y < h; y++)
            {
                vPrefix[(y + 1) * (w - 1) + x] = vPrefix[y * (w - 1) + x] + (vEdge[y * (w - 1) + x] ? 1 : 0);
            }
        }

        // Candidate lines: rows/columns with a long stable edge. The picture border counts as a line (cam flush in a corner).
        var rows = Enumerable.Range(0, h - 1)
            .Select(y => (y, len: hPrefix[y * (w + 1) + w]))
            .Where(r => r.len >= 0.08 * w).OrderByDescending(r => r.len).Take(MaxLines).Select(r => r.y).ToList();
        var cols = Enumerable.Range(0, w - 1)
            .Select(x => (x, len: vPrefix[h * (w - 1) + x]))
            .Where(c => c.len >= 0.08 * h).OrderByDescending(c => c.len).Take(MaxLines).Select(c => c.x).ToList();
        rows = [-1, .. rows.Order(), h - 1];
        cols = [-1, .. cols.Order(), w - 1];

        var motion = TemporalStd(gray, w, h);
        var skin = SkinMask(frames, w, h);
        return new Analysis(w, h, rows, cols, hPrefix, vPrefix, motion, skin);
    }

    private sealed class Analysis(int w, int h, List<int> rows, List<int> cols, int[] hPrefix, int[] vPrefix, float[] motion, bool[] skin)
    {
        public double Check(NormRect cam)
        {
            int x0 = Math.Clamp((int)Math.Round(cam.X * w), 0, w - 1), y0 = Math.Clamp((int)Math.Round(cam.Y * h), 0, h - 1);
            int x1 = Math.Clamp((int)Math.Round((cam.X + cam.Width) * w), x0 + 1, w), y1 = Math.Clamp((int)Math.Round((cam.Y + cam.Height) * h), y0 + 1, h);

            // Best border coverage within ±2 px of each side (the stored rectangle is rounded).
            double Side(Func<int, double> cov, int at, int min, int max) =>
                Enumerable.Range(at - 2, 5).Where(v => v >= min && v <= max).Select(cov).DefaultIfEmpty(0).Max();
            var top = Side(y => HCov(y, x0, x1), y0 - 1, -1, h - 1);
            var bottom = Side(y => HCov(y, x0, x1), y1 - 1, -1, h - 1);
            var left = Side(x => VCov(x, y0, y1), x0 - 1, -1, w - 1);
            var right = Side(x => VCov(x, y0, y1), x1 - 1, -1, w - 1);
            var cov = (top + bottom + left + right) / 4;
            var (moving, skinShare) = Inside(motion, skin, w, x0, x1, y0, y1);
            return (moving > 0.5 ? 1 : 0.2) * (0.5 * cov + Math.Min(skinShare, 0.1) * 5);
        }

        public NormRect? Search()
        {
        double best = 0;
        NormRect? result = null;
        for (var i = 0; i < rows.Count; i++)
        {
            for (var i2 = i + 1; i2 < rows.Count; i2++)
            {
                int top = rows[i], bottom = rows[i2], rh = bottom - top;
                if (rh < 0.12 * h || rh > 0.6 * h)
                {
                    continue;
                }

                for (var j = 0; j < cols.Count; j++)
                {
                    for (var j2 = j + 1; j2 < cols.Count; j2++)
                    {
                        int left = cols[j], right = cols[j2], rw = right - left;
                        if (rw < 0.12 * w || rw > 0.5 * w)
                        {
                            continue;
                        }

                        var aspect = (double)rw / rh;
                        if (aspect is < 1.0 or > 2.4)
                        {
                            continue;
                        }

                        // A real border on at least two sides (a rectangle made only of picture borders is the whole frame).
                        var realSides = (top >= 0 ? 1 : 0) + (bottom < h - 1 ? 1 : 0) + (left >= 0 ? 1 : 0) + (right < w - 1 ? 1 : 0);
                        if (realSides < 2)
                        {
                            continue;
                        }

                        int x0 = left + 1, x1 = right + 1, y0 = top + 1, y1 = bottom + 1;
                        var cov = Math.Min(Math.Min(HCov(top, x0, x1), HCov(bottom, x0, x1)), Math.Min(VCov(left, y0, y1), VCov(right, y0, y1)));
                        if (cov < 0.6)
                        {
                            continue;
                        }

                        var (moving, skinShare) = Inside(motion, skin, w, x0, x1, y0, y1);
                        // More real border sides win a tie, so the box ends at the cam frame and not at the picture edge behind it.
                        var score = cov * (0.3 + Math.Min(skinShare, 0.2) * 5) * (moving > 0.8 ? 1 : 0.3) * (0.9 + 0.025 * realSides);
                        if (score > best)
                        {
                            best = score;
                            result = new NormRect((double)x0 / w, (double)y0 / h, (double)rw / w, (double)rh / h);
                        }
                    }
                }
            }
        }

        return best >= MinScore ? result : null;
        }

        private double HCov(int y, int x0, int x1) => y < 0 || y >= h - 1 ? 1 : (double)(hPrefix[y * (w + 1) + x1] - hPrefix[y * (w + 1) + x0]) / Math.Max(1, x1 - x0);

        private double VCov(int x, int y0, int y1) => x < 0 || x >= w - 1 ? 1 : (double)(vPrefix[y1 * (w - 1) + x] - vPrefix[y0 * (w - 1) + x]) / Math.Max(1, y1 - y0);
    }

    /// <summary>Half the median gradient-free contrast: 20 for normal pictures, down to 8 for dark ones.</summary>
    private static int AdaptiveThreshold(float[] gray)
    {
        var sorted = gray.Where((_, i) => i % 7 == 0).Order().ToList();
        if (sorted.Count == 0)
        {
            return EdgeThreshold;
        }

        var spread = sorted[(int)(sorted.Count * 0.9)] - sorted[(int)(sorted.Count * 0.1)];
        return (int)Math.Clamp(spread / 6, MinEdgeThreshold, EdgeThreshold);
    }

    private static float[] ToGray(byte[] rgb, int w, int h)
    {
        var g = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            g[i] = (rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2]) / 3f;
        }

        return g;
    }

    private static float[] TemporalStd(List<float[]> gray, int w, int h)
    {
        var std = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            double sum = 0, sq = 0;
            foreach (var g in gray)
            {
                sum += g[i];
                sq += g[i] * g[i];
            }

            var mean = sum / gray.Count;
            std[i] = (float)Math.Sqrt(Math.Max(0, sq / gray.Count - mean * mean));
        }

        return std;
    }

    /// <summary>Skin colour in YCbCr on the average frame.</summary>
    private static bool[] SkinMask(IReadOnlyList<byte[]> frames, int w, int h)
    {
        var mask = new bool[w * h];
        for (var i = 0; i < w * h; i++)
        {
            double r = 0, g = 0, b = 0;
            foreach (var f in frames)
            {
                r += f[i * 3];
                g += f[i * 3 + 1];
                b += f[i * 3 + 2];
            }

            r /= frames.Count;
            g /= frames.Count;
            b /= frames.Count;
            var y = 0.299 * r + 0.587 * g + 0.114 * b;
            var cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
            var cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
            mask[i] = cr is > 135 and < 180 && cb is > 85 and < 135 && y > 40;
        }

        return mask;
    }

    private static (double Moving, double Skin) Inside(float[] motion, bool[] skin, int w, int x0, int x1, int y0, int y1)
    {
        double m = 0;
        var s = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                m += motion[y * w + x];
                s += skin[y * w + x] ? 1 : 0;
                count++;
            }
        }

        return count == 0 ? (0, 0) : (m / count, (double)s / count);
    }
}
