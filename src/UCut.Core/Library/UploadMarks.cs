using System.Globalization;
using UCut.Metadata;

namespace UCut.Library;

/// <summary>"Hochgeladen" ticks per platform. Uploads by account write "TikTok: name", ticks by hand "TikTok: von Hand".</summary>
public static class UploadMarks
{
    public static readonly string[] Platforms = ["TikTok", "YouTube", "Instagram"];

    public static bool Has(ClipMetadata meta, string platform) =>
        meta.Uploaded.Keys.Any(k => k.StartsWith(platform, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns whether something changed.</summary>
    public static bool Set(ClipMetadata meta, string platform, bool on, DateTime now)
    {
        if (on == Has(meta, platform))
        {
            return false;
        }

        if (on)
        {
            meta.Uploaded[$"{platform}: von Hand"] = "hochgeladen am " + now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            foreach (var k in meta.Uploaded.Keys.Where(k => k.StartsWith(platform, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                meta.Uploaded.Remove(k);
            }
        }

        return true;
    }
}
