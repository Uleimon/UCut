using System.Globalization;
using System.Text;
using UCut.Media;

namespace UCut.Analysis;

/// <summary>
/// Preview picture for YouTube: the frame at the funniest moment (first laugh, otherwise the loudest moment)
/// with the title big at the top, like Shorts thumbnails usually look. Saved as &lt;clip&gt;.youtube.jpg.
/// </summary>
public static class YouTubeThumbnail
{
    /// <summary>Where to take the picture: just after the first laugh, else at the loudest moment, else at a third.</summary>
    public static double PickMoment(LaughResult laughs, LoudnessProfile loudness, double duration)
    {
        double at;
        if (laughs.Count > 0)
        {
            // The face reacts a moment after the laugh starts.
            at = laughs.Moments[0] + 0.4;
        }
        else
        {
            var loud = loudness.Points.Where(p => p.Lufs > -70 && p.Time > 0.5).OrderByDescending(p => p.Lufs).FirstOrDefault();
            at = loud.Time > 0 ? loud.Time - 0.2 : duration / 3;
        }

        return Math.Clamp(at, 0, Math.Max(0, duration - 0.1));
    }

    /// <summary>ASS file with the title as a big outlined line at the top (same subtitle engine as the captions).</summary>
    public static string BuildAss(string title, int width, int height, string font)
    {
        var ci = CultureInfo.InvariantCulture;
        var size = (int)Math.Round(height * 0.055);
        var clean = Clean(title).ToUpperInvariant();
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine(ci, $"PlayResX: {width}");
        sb.AppendLine(ci, $"PlayResY: {height}");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine(ci, $"Style: Title,{font},{size},&H00FFFFFF,&H00FFFFFF,&H00000000,&H96000000,-1,0,0,0,100,100,0,0,1,{Math.Max(4, size / 9)},4,8,{width / 14},{width / 14},{(int)(height * 0.07)},1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        sb.AppendLine(ci, $"Dialogue: 0,0:00:00.00,9:59:59.00,Title,,0,0,0,,{clean}");
        return sb.ToString();
    }

    /// <summary>ffmpeg arguments: one frame of <paramref name="video"/> at <paramref name="at"/> with the title file burnt in.</summary>
    public static IReadOnlyList<string> Command(string video, double at, string assFileName, string output) =>
    [
        "-v", "error", "-y", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", video,
        "-frames:v", "1", "-vf", $"ass={assFileName}", "-q:v", "3", output,
    ];

    /// <summary>Keeps letters, digits, punctuation and emoji; drops what would break the subtitle file; max. 50 characters.</summary>
    internal static string Clean(string title)
    {
        var t = title.Replace("{", "(", StringComparison.Ordinal).Replace("}", ")", StringComparison.Ordinal)
                     .Replace("\\", "/", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith('#')));
        return t.Length <= 50 ? t : t[..49].TrimEnd() + "…";
    }
}
