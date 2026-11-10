namespace UCut.Analysis;

/// <summary>Titles, captions and the funniness rating for one clip.</summary>
public sealed record ClipText(
    string Title,
    string TikTokCaption,
    string YouTubeTitle,
    string YouTubeDescription,
    IReadOnlyList<string> Hashtags,
    int? Funniness,
    string? FunninessReason,
    string Source);

public sealed record ClipContext(string Transcript, string Game, double DurationSeconds, IReadOnlyList<string> GameHashtags, IReadOnlyList<string> CommonHashtags, string ChannelName);

public interface IClipWriter
{
    Task<ClipText> WriteAsync(ClipContext context, CancellationToken ct = default);
}

/// <summary>Works without any AI: first sentence as title, hashtags from the settings.</summary>
public sealed class TemplateClipWriter : IClipWriter
{
    public Task<ClipText> WriteAsync(ClipContext context, CancellationToken ct = default)
    {
        var title = FirstSentence(context.Transcript);
        if (title.Length == 0)
        {
            title = $"{context.Game} Clip";
        }

        var tags = context.GameHashtags.Concat(context.CommonHashtags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tagLine = string.Join(' ', tags);
        return Task.FromResult(new ClipText(
            title,
            $"{title} {tagLine}".Trim(),
            Truncate($"{title} #shorts", 100),
            $"Live auf Twitch: {context.ChannelName}\n\n{tagLine}",
            tags,
            null,
            null,
            "Vorlage"));
    }

    internal static string FirstSentence(string text)
    {
        var t = text.Trim();
        var end = t.IndexOfAny(['.', '?', '!']);
        var sentence = end >= 0 ? t[..(end + 1)] : t;
        return Truncate(sentence, 80);
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
