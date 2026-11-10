using System.Globalization;
using UCut.Settings;

namespace UCut.Editing;

/// <summary>Everything needed for the final ffmpeg run. Width/Height are the size of the OUTPUT video.</summary>
public sealed record RenderJob(
    string Input,
    string Output,
    CutPlan Cut,
    int Width,
    int Height,
    string? AssFileName,
    string? LogoPath,
    LogoSettings Logo,
    double? LogoCenterYOverride,
    ReframePlan? Reframe = null);

public static class RenderCommand
{
    /// <summary>Builds the ffmpeg arguments. The ASS file is referenced by name only, ffmpeg runs in its folder (no path escaping trouble on Windows).</summary>
    public static List<string> Build(RenderJob job)
    {
        var ci = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-v", "error", "-y",
            "-ss", job.Cut.Start.ToString("0.###", ci),
            "-t", job.Cut.Duration.ToString("0.###", ci),
            "-i", job.Input,
        };

        var useLogo = job.Logo.Enabled && job.LogoPath is not null;
        if (useLogo)
        {
            args.AddRange(["-i", job.LogoPath!]);
        }

        var filters = new List<string>();
        var current = "[0:v]";
        var step = 0;
        string Next() => $"[v{++step}]";

        if (job.Reframe is { } reframe)
        {
            var n = Next();
            filters.Add(reframe.Filter(current, n));
            current = n;
        }

        // The cover box belongs to the vertical Aitum layout; a reframed clip has no name plate at that spot.
        if (useLogo && job.Reframe is null && job.Logo.Cover is { } cover)
        {
            var n = Next();
            filters.Add($"{current}drawbox=x={Px(cover.X, job.Width)}:y={Px(cover.Y, job.Height)}:w={Px(cover.Width, job.Width)}:h={Px(cover.Height, job.Height)}:color=0x{cover.Color.TrimStart('#')}:t=fill{n}");
            current = n;
        }

        if (useLogo)
        {
            var logoWidth = Even(job.Logo.WidthFraction * job.Width);
            var cx = Px(job.Logo.CenterX ?? 0.5, job.Width);
            var cy = Px(job.LogoCenterYOverride ?? job.Logo.CenterY ?? 0.33, job.Height);
            filters.Add($"[1:v]scale={logoWidth}:-1[logo]");
            var n = Next();
            filters.Add($"{current}[logo]overlay=x={cx}-w/2:y={cy}-h/2{n}");
            current = n;
        }

        if (job.AssFileName is not null)
        {
            var n = Next();
            filters.Add($"{current}ass={job.AssFileName}{n}");
            current = n;
        }

        if (filters.Count > 0)
        {
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", current, "-map", "0:a?"]);
        }

        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
                       "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", job.Output]);
        return args;
    }

    private static int Px(double fraction, int size) => (int)Math.Round(fraction * size);

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
}
