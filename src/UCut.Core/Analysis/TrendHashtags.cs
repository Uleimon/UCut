using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace UCut.Analysis;

/// <summary>Hashtags that are popular right now for a game.</summary>
public interface ITrendSource
{
    Task<IReadOnlyList<string>> GetAsync(string game, CancellationToken ct = default);
}

/// <summary>
/// Asks Claude with web search which hashtags are trending this week for clips of a game (TikTok, Shorts, Reels).
/// Cached per game for 3 days in OutputFolder\_UCut\trend-hashtags.json, so it costs one search per game, not per clip.
/// Any error: the last known list (or nothing), never a failed clip.
/// </summary>
public sealed partial class ClaudeTrendHashtags(string model, string cachePath, Func<string, CancellationToken, Task<string>>? ask = null, TimeProvider? time = null) : ITrendSource
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public sealed class Entry
    {
        public List<string> Tags { get; set; } = [];
        public DateTimeOffset At { get; set; }
    }

    public static string DefaultPath(string outputFolder) => Path.Combine(outputFolder, "_UCut", "trend-hashtags.json");

    public async Task<IReadOnlyList<string>> GetAsync(string game, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = Load();
            if (cache.TryGetValue(game, out var hit) && _time.GetUtcNow() - hit.At < MaxAge)
            {
                return hit.Tags;
            }

            try
            {
                var answer = await (ask ?? AskClaudeAsync)(game, ct).ConfigureAwait(false);
                var tags = Parse(answer);
                if (tags.Count > 0)
                {
                    cache[game] = new Entry { Tags = tags, At = _time.GetUtcNow() };
                    Save(cache);
                    return tags;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Offline, no credit, search failed: old list or none.
            }

            return hit?.Tags ?? [];
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>All hashtags from the line "HASHTAGS:" (or, if missing, from the whole answer), lower case, max. 6.</summary>
    internal static List<string> Parse(string answer)
    {
        var line = answer.Split('\n').LastOrDefault(l => l.Contains("HASHTAGS:", StringComparison.OrdinalIgnoreCase)) ?? answer;
        return Tag().Matches(line).Select(m => m.Value.ToLowerInvariant())
                    .Where(t => t.Length is > 2 and <= 30)
                    .Distinct(StringComparer.Ordinal).Take(6).ToList();
    }

    private async Task<string> AskClaudeAsync(string game, CancellationToken ct)
    {
        AnthropicClient client = new();
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 4000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = "low" },
            Tools = [new BetaToolUnion(new BetaWebSearchTool20260209 { MaxUses = 3 })],
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content =
                        $"""
                        Welche Hashtags werden diese Woche für kurze Gaming-Clips zum Spiel "{game}" auf TikTok, YouTube Shorts und Instagram Reels viel benutzt?
                        Such kurz im Web nach aktuellen Trends (Updates, Events, Memes zum Spiel). Nur Hashtags, die wirklich zum Spiel passen, keine allgemeinen wie #fyp.
                        Antworte am Ende mit genau einer Zeile: HASHTAGS: #tag1 #tag2 ... (3 bis 6 Stück, kleingeschrieben).
                        """,
                },
            ],
        }, ct).ConfigureAwait(false);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException("Claude hat die Anfrage abgelehnt.");
        }

        return string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(cachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(cachePath), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, Entry> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, Options));
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"#[\p{L}\p{N}_]+")]
    private static partial Regex Tag();
}
