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

/// <param name="TrendHashtags">Hashtags popular right now for the game (may be empty).</param>
/// <param name="Laughs">Laughs found in the sound or the transcript.</param>
public sealed record ClipContext(string Transcript, string Game, double DurationSeconds, IReadOnlyList<string> GameHashtags, IReadOnlyList<string> CommonHashtags, string ChannelName,
    IReadOnlyList<string>? TrendHashtags = null, int Laughs = 0, int Seed = 0);

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
            title = Hook(context.Game, context.Laughs, context.Seed);
        }

        // Two trending tags of the game ride along (the first ones are the most specific).
        var tags = context.GameHashtags.Concat((context.TrendHashtags ?? []).Take(2)).Concat(context.CommonHashtags)
                          .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>Hooks for clips without words (Instant Clip off, or nobody talks). Picked by the seed, so clips differ.</summary>
    internal static readonly string[] Hooks =
    [
        "Das hat keiner kommen sehen 😳",
        "Warte bis zum Ende 💀",
        "Wie konnte das passieren?! 😂",
        "Ich hab nur kurz nicht aufgepasst…",
        "Das war so nicht geplant 💀",
        "Bester Moment im ganzen Stream",
        "{game} ist einfach kaputt 💀",
        "Das Spiel hasst mich 😤",
        "Ich glaub, ich spinne 😳",
        "Das ist mir wirklich passiert…",
    ];

    internal static readonly string[] LaughHooks =
    [
        "Ich konnte nicht mehr 😂",
        "Lachflash im Stream 😂",
        "Ich lag auf dem Boden 😂",
        "Das war zu lustig 😂",
    ];

    public static string Hook(string game, int laughs, int seed)
    {
        var pool = laughs > 0 ? LaughHooks : Hooks;
        var hook = pool[(int)((uint)seed % (uint)pool.Length)];
        return hook.Replace("{game}", ShortGame(game), StringComparison.Ordinal);
    }

    /// <summary>"Grand Theft Auto VI" → "GTA 6": the way people write it.</summary>
    public static string ShortGame(string game) => game switch
    {
        "Grand Theft Auto VI" or "GTA VI" => "GTA 6",
        "Grand Theft Auto V" or "GTA V" => "GTA 5",
        _ => game,
    };

    /// <summary>A title UCut made without words ("GTA V Clip", a hook from the list): may be replaced once there is a transcript.</summary>
    public static bool IsPlaceholderTitle(string title, string game)
    {
        var t = title.Replace("#shorts", "", StringComparison.OrdinalIgnoreCase).Trim();
        return t.Length == 0
            || t.Equals($"{game} Clip", StringComparison.OrdinalIgnoreCase)
            || Hooks.Concat(LaughHooks).Any(h => t.Equals(h.Replace("{game}", ShortGame(game), StringComparison.Ordinal), StringComparison.Ordinal));
    }

    /// <summary>Stable number from a text (string.GetHashCode differs per start).</summary>
    public static int StableSeed(string text)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in text)
            {
                h = h * 31 + c;
            }

            return h & int.MaxValue;
        }
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
