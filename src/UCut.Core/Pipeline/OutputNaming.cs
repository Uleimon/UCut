using System.Globalization;
using System.Text;

namespace UCut.Pipeline;

public static class OutputNaming
{
    /// <summary>OutputFolder\Game\2026-11-10_150700_wer-hat-noch-kredit.mp4 (never overwrites an existing file).</summary>
    public static string ClipPath(string outputFolder, string gameFolder, DateTimeOffset time, string title)
    {
        var dir = Path.Combine(outputFolder, gameFolder);
        var stamp = time.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var slug = Slug(title);
        var baseName = slug.Length > 0 ? $"{stamp}_{slug}" : stamp;
        var path = Path.Combine(dir, baseName + ".mp4");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(dir, $"{baseName}_{i}.mp4");
        }

        return path;
    }

    internal static string Slug(string title)
    {
        var map = new Dictionary<char, string> { ['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['ß'] = "ss" };
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (map.TryGetValue(ch, out var rep))
            {
                sb.Append(rep);
            }
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var s = sb.ToString().Trim('-');
        if (s.Length > 40)
        {
            s = s[..40].TrimEnd('-');
        }

        return s;
    }
}
