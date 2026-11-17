using System.Globalization;
using System.Text.Json;
using UCut.Settings;

namespace UCut.Twitch;

/// <summary>One clip from the channel as Twitch lists it.</summary>
public sealed record TwitchClip(
    string Id,
    string Url,
    string Title,
    int ViewCount,
    DateTimeOffset CreatedAt,
    double DurationSeconds,
    string GameId,
    string CreatorName)
{
    public string? GameName { get; init; }

    /// <summary>Preview picture from Twitch.</summary>
    public string ThumbnailUrl { get; init; } = "";
}

/// <summary>Lists all clips of the channel in a time range via Helix (GET /clips) and resolves game names.</summary>
public sealed class TwitchClipSearch(TwitchApi api, Action<string>? log = null)
{
    /// <summary>
    /// Asks month by month: Helix sorts by views and its paging gets unreliable over long ranges,
    /// smaller windows return every clip.
    /// </summary>
    public async Task<IReadOnlyList<TwitchClip>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var id = await api.GetBroadcasterIdAsync(ct).ConfigureAwait(false);
        var clips = new Dictionary<string, TwitchClip>(StringComparer.Ordinal);
        var lastYear = 0;
        foreach (var (start, end) in MonthWindows(from, to))
        {
            if (start.Year != lastYear)
            {
                lastYear = start.Year;
                log?.Invoke($"  {lastYear} ... ({clips.Count} Clips bisher)");
            }

            string? cursor = null;
            var pages = 0;
            do
            {
                var query = $"clips?broadcaster_id={id}&started_at={Rfc3339(start)}&ended_at={Rfc3339(end)}&first=100" +
                            (cursor is null ? "" : $"&after={Uri.EscapeDataString(cursor)}");
                var (page, next) = ParseClips(await api.GetAsync(query, ct).ConfigureAwait(false));
                foreach (var c in page)
                {
                    clips.TryAdd(c.Id, c);
                }

                cursor = page.Count == 0 ? null : next;
            }
            while (cursor is not null && ++pages < 50);
        }

        log?.Invoke($"{clips.Count} Twitch-Clip(s) zwischen {from:dd.MM.yyyy} und {to:dd.MM.yyyy} gefunden.");
        var names = await GameNamesAsync(clips.Values.Select(c => c.GameId), ct).ConfigureAwait(false);
        return clips.Values
            .Select(c => c with { GameName = names.TryGetValue(c.GameId, out var n) ? n : null })
            .OrderBy(c => c.CreatedAt)
            .ToList();
    }

    private async Task<Dictionary<string, string>> GameNamesAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in ids.Where(i => i.Length > 0).Distinct().Chunk(100))
        {
            var query = "games?" + string.Join('&', chunk.Select(i => "id=" + Uri.EscapeDataString(i)));
            foreach (var (gid, name) in ParseGames(await api.GetAsync(query, ct).ConfigureAwait(false)))
            {
                result[gid] = name;
            }
        }

        return result;
    }

    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> MonthWindows(DateTimeOffset from, DateTimeOffset to)
    {
        var start = from;
        while (start < to)
        {
            var end = start.AddMonths(1);
            if (end > to)
            {
                end = to;
            }

            yield return (start, end);
            start = end;
        }
    }

    private static string Rfc3339(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    internal static (List<TwitchClip> Clips, string? Cursor) ParseClips(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<TwitchClip>();
        foreach (var c in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            list.Add(new TwitchClip(
                c.GetProperty("id").GetString() ?? "",
                c.GetProperty("url").GetString() ?? "",
                c.GetProperty("title").GetString() ?? "",
                c.TryGetProperty("view_count", out var v) ? v.GetInt32() : 0,
                c.GetProperty("created_at").GetDateTimeOffset(),
                c.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
                c.TryGetProperty("game_id", out var g) ? g.GetString() ?? "" : "",
                c.TryGetProperty("creator_name", out var cr) ? cr.GetString() ?? "" : "")
            {
                ThumbnailUrl = c.TryGetProperty("thumbnail_url", out var th) ? th.GetString() ?? "" : "",
            });
        }

        string? cursor = null;
        if (doc.RootElement.TryGetProperty("pagination", out var p) && p.TryGetProperty("cursor", out var cur))
        {
            cursor = cur.GetString();
        }

        return (list, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    internal static IEnumerable<(string Id, string Name)> ParseGames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(g => (g.GetProperty("id").GetString() ?? "", g.GetProperty("name").GetString() ?? ""))
            .ToList();
    }
}

public sealed record ClipCandidate(TwitchClip Clip, double Hint, IReadOnlyList<string> Reasons);

/// <summary>
/// First, cheap guess which clips are funny, before anything is downloaded: words in the title and how often
/// the clip was watched compared to the other clips. The real funniness rating comes after the transcript.
/// </summary>
public static class FunnyFilter
{
    public static IReadOnlyList<ClipCandidate> Rank(IReadOnlyList<TwitchClip> clips, TwitchClipSettings settings)
    {
        if (clips.Count == 0)
        {
            return [];
        }

        var byViews = clips.OrderBy(c => c.ViewCount).ToList();

        // Twitch gives a clip the stream title unless the clipper types a new one. A title that several clips share is
        // the stream title ("Was spiel ich hier alter ... !clip") and says nothing about the moment, so its words do not count.
        var titleCounts = clips.GroupBy(c => c.Title.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<ClipCandidate>();
        foreach (var clip in clips)
        {
            if (clip.ViewCount < settings.MinViews)
            {
                continue;
            }

            var reasons = new List<string>();
            var hint = 0.0;

            var ownTitle = titleCounts.GetValueOrDefault(clip.Title.Trim()) < 3;
            var words = ownTitle
                ? settings.FunnyWords.Where(w => ContainsWord(clip.Title, w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (words.Count > 0)
            {
                hint += Math.Min(2, words.Count) * 3;
                reasons.Add(string.Join(", ", words.Select(w => $"„{w}“")) + " im Titel");
            }

            // Share of clips with fewer views (0 = least watched, 1 = most watched).
            var rank = clips.Count == 1 ? 1.0 : (double)byViews.FindIndex(c => c.ViewCount >= clip.ViewCount) / (clips.Count - 1);
            hint += rank * 4;
            var topViews = rank >= 1 - settings.TopViewsShare;
            if (topViews)
            {
                reasons.Add("gehört zu deinen meistgesehenen Clips");
            }

            if (!settings.OnlyFunny || words.Count > 0 || topViews)
            {
                result.Add(new ClipCandidate(clip, Math.Round(hint, 2), reasons));
            }
        }

        var ordered = result.OrderByDescending(c => c.Hint).ThenByDescending(c => c.Clip.ViewCount).ToList();
        return settings.MaxClips > 0 ? ordered.Take(settings.MaxClips).ToList() : ordered;
    }

    internal static bool ContainsWord(string title, string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        // Emojis and longer words: plain search. Short words (xd, lol): only as whole word, so "Holz" is not "lol".
        if (word.Length > 4 || !char.IsLetterOrDigit(word[0]))
        {
            return title.Contains(word, StringComparison.OrdinalIgnoreCase);
        }

        var i = 0;
        while ((i = title.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = i == 0 || !char.IsLetterOrDigit(title[i - 1]);
            var after = i + word.Length >= title.Length || !char.IsLetterOrDigit(title[i + word.Length]);
            if (before && after)
            {
                return true;
            }

            i += word.Length;
        }

        return false;
    }
}
