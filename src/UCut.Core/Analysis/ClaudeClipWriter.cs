using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace UCut.Analysis;

/// <summary>
/// Lets Claude rate how funny the clip is (also without context) and write title, captions and hashtags.
/// Needs the ANTHROPIC_API_KEY environment variable. On any error UCut falls back to <see cref="TemplateClipWriter"/>.
/// </summary>
public sealed class ClaudeClipWriter(string model) : IClipWriter
{
    private const string SystemPrompt =
        """
        Du hilfst einem deutschen Twitch-Streamer, kurze vertikale Clips für TikTok, YouTube Shorts und Instagram Reels zu veröffentlichen.
        Du bekommst das Transkript eines Clips, das Spiel und die Länge.
        Bewerte ehrlich, wie lustig bzw. unterhaltsam der Clip für Fremde ohne Vorwissen ist (0 = gar nicht, 10 = sehr).
        Schreibe danach Texte im Ton des Streamers: locker, Deutsch, keine Clickbait-Lügen, nichts erfinden, was nicht im Transkript steht.
        - title: kurzer Titel, max. 60 Zeichen, gern mit einem passenden Emoji.
        - tiktok_caption: Titel plus 3 bis 6 Hashtags in einer Zeile, max. 150 Zeichen.
        - youtube_title: max. 90 Zeichen, endet mit #shorts.
        - youtube_description: 1 bis 2 Sätze, dann eine Zeile "Live auf Twitch: <Kanal>", dann die Hashtags.
        - hashtags: 3 bis 8 Hashtags inklusive #, kleingeschrieben, passend zu Spiel und Inhalt. Die vorgegebenen Spiel-Hashtags gehören dazu.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(new[]
            { "funniness", "funniness_reason", "title", "tiktok_caption", "youtube_title", "youtube_description", "hashtags" }),
        ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["funniness"] = new { type = "integer" },
            ["funniness_reason"] = new { type = "string" },
            ["title"] = new { type = "string" },
            ["tiktok_caption"] = new { type = "string" },
            ["youtube_title"] = new { type = "string" },
            ["youtube_description"] = new { type = "string" },
            ["hashtags"] = new { type = "array", items = new { type = "string" } },
        }),
    };

    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    public async Task<ClipText> WriteAsync(ClipContext context, CancellationToken ct = default)
    {
        AnthropicClient client = new();
        var prompt =
            $"""
            Spiel: {context.Game}
            Länge: {context.DurationSeconds:0} Sekunden
            Kanal: {context.ChannelName}
            Vorgegebene Spiel-Hashtags: {string.Join(' ', context.GameHashtags)}
            Allgemeine Hashtags: {string.Join(' ', context.CommonHashtags)}
            Gerade beliebte Hashtags zum Spiel (1 bis 2 davon mitnehmen, wenn sie passen): {(context.TrendHashtags is { Count: > 0 } trends ? string.Join(' ', trends) : "(keine)")}
            Lacher im Ton erkannt: {context.Laughs} (Hinweis für die Lustigkeit, Lachen im Clip ist meist ein gutes Zeichen)

            Transkript:
            {(string.IsNullOrWhiteSpace(context.Transcript) ? "(es wird nichts gesagt)" : context.Transcript)}
            """;

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 2000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = SystemPrompt,
            OutputConfig = new BetaOutputConfig
            {
                Effort = "low",
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },
            Messages = [new BetaUserMessageParam { Content = prompt }],
        }, ct).ConfigureAwait(false);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException("Claude hat die Anfrage abgelehnt.");
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return Parse(json);
    }

    internal static ClipText Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tags = r.GetProperty("hashtags").EnumerateArray()
            .Select(t => t.GetString() ?? "")
            .Where(t => t.Length > 1)
            .Select(t => t.StartsWith('#') ? t : "#" + t)
            .ToList();
        return new ClipText(
            r.GetProperty("title").GetString() ?? "",
            r.GetProperty("tiktok_caption").GetString() ?? "",
            r.GetProperty("youtube_title").GetString() ?? "",
            r.GetProperty("youtube_description").GetString() ?? "",
            tags,
            Math.Clamp(r.GetProperty("funniness").GetInt32(), 0, 10),
            r.GetProperty("funniness_reason").GetString(),
            "Claude");
    }
}
