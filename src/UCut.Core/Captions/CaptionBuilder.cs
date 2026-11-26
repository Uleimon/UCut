using System.Globalization;
using System.Text;
using UCut.Settings;
using UCut.Transcription;

namespace UCut.Captions;

/// <summary>One caption shown on screen: a group of words with one of them highlighted.</summary>
public sealed record CaptionEvent(double Start, double End, IReadOnlyList<string> Words, int HighlightIndex);

/// <summary>
/// Builds TikTok-style word-by-word captions as an ASS subtitle file.
/// Rule from Ulei's first test: captions must never overlap. Every caption ends exactly when the next one starts.
/// </summary>
public sealed class CaptionBuilder(CaptionSettings settings)
{
    /// <summary>Gaps shorter than this are bridged so the text does not flicker.</summary>
    internal const double BridgeGapSeconds = 0.3;

    /// <summary>How long the last word of a group stays visible after it was spoken.</summary>
    internal const double HoldSeconds = 0.15;

    public IReadOnlyList<CaptionEvent> BuildEvents(Transcript transcript)
    {
        var groups = Group(transcript.Words, Math.Max(1, settings.WordsPerLine));
        var events = new List<CaptionEvent>();
        foreach (var group in groups)
        {
            var texts = group.Select(w => settings.Uppercase ? w.Text.ToUpperInvariant() : w.Text).ToList();
            for (var i = 0; i < group.Count; i++)
            {
                events.Add(new CaptionEvent(group[i].Start, group[i].End + HoldSeconds, texts, i));
            }
        }

        // No overlap: an event ends when the next one starts; small gaps are bridged.
        for (var k = 0; k < events.Count - 1; k++)
        {
            var next = events[k + 1].Start;
            if (events[k].End > next || next - events[k].End < BridgeGapSeconds)
            {
                events[k] = events[k] with { End = next };
            }
        }

        return events.Where(e => e.End > e.Start).ToList();
    }

    /// <summary>Splits words into groups of at most <paramref name="max"/>; a group also ends after . , ? ! …</summary>
    internal static List<List<Word>> Group(IReadOnlyList<Word> words, int max)
    {
        var groups = new List<List<Word>>();
        var current = new List<Word>();
        foreach (var w in words)
        {
            current.Add(w);
            if (current.Count >= max || EndsPhrase(w.Text))
            {
                groups.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    private static bool EndsPhrase(string text) => text.Length > 0 && ".,?!…:;".Contains(text[^1], StringComparison.Ordinal);

    /// <summary>
    /// The title as a hook line: hashtags and "#shorts" go, long titles break into two lines (\N).
    /// Null when nothing is left.
    /// </summary>
    public static string? HookText(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith('#')).ToList();
        if (words.Count == 0)
        {
            return null;
        }

        var text = string.Join(' ', words);
        if (text.Length <= 22 || words.Count < 3)
        {
            return text;
        }

        // break where both lines are about the same length
        var best = 1;
        var bestDiff = int.MaxValue;
        for (var i = 1; i < words.Count; i++)
        {
            var diff = Math.Abs(string.Join(' ', words.Take(i)).Length - string.Join(' ', words.Skip(i)).Length);
            if (diff < bestDiff)
            {
                (best, bestDiff) = (i, diff);
            }
        }

        return string.Join(' ', words.Take(best)) + "\\N" + string.Join(' ', words.Skip(best));
    }

    public string BuildAss(Transcript transcript, int videoWidth, int videoHeight, double? verticalPosition = null, string? hook = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine(ci, $"PlayResX: {videoWidth}");
        sb.AppendLine(ci, $"PlayResY: {videoHeight}");
        sb.AppendLine("WrapStyle: 2");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        var fontSize = (int)Math.Round(settings.FontSize * videoWidth / 910.0);
        sb.AppendLine(ci, $"Style: Cap,{settings.Font},{fontSize},&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,6,3,5,40,40,0,1");
        var hookSize = (int)Math.Round(settings.HookFontSize * videoWidth / 910.0);
        sb.AppendLine(ci, $"Style: Hook,Arial,{hookSize},&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,5,2,5,40,40,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var x = videoWidth / 2;
        var y = (int)Math.Round(videoHeight * (verticalPosition ?? settings.VerticalPosition));
        var highlight = ToAssColor(settings.HighlightColor);
        if (hook is not null)
        {
            var hookY = (int)Math.Round(videoHeight * settings.HookVerticalPosition);
            var text = string.Join("\\N", hook.Split("\\N").Select(Escape));
            sb.AppendLine(ci, $"Dialogue: 1,{FormatTime(0)},{FormatTime(settings.HookSeconds)},Hook,,0,0,0,,{{\\pos({x},{hookY})\\fad(0,250)}}{text}");
        }

        foreach (var e in BuildEvents(transcript))
        {
            var parts = e.Words.Select((w, i) => i == e.HighlightIndex
                ? $"{{\\c{highlight}\\fscx110\\fscy110}}{Escape(w)}{{\\r}}"
                : Escape(w));
            sb.AppendLine(ci, $"Dialogue: 0,{FormatTime(e.Start)},{FormatTime(e.End)},Cap,,0,0,0,,{{\\pos({x},{y})}}{string.Join(' ', parts)}");
        }

        return sb.ToString();
    }

    internal static string FormatTime(double seconds)
    {
        // ASS uses centiseconds; round once so consecutive events share the exact same boundary.
        var cs = (long)Math.Round(Math.Max(0, seconds) * 100, MidpointRounding.AwayFromZero);
        var h = cs / 360000;
        var m = cs / 6000 % 60;
        var s = cs / 100 % 60;
        var c = cs % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{c:00}");
    }

    /// <summary>#RRGGBB to ASS &amp;HBBGGRR&amp;.</summary>
    internal static string ToAssColor(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6)
        {
            throw new ArgumentException($"Farbe muss im Format #RRGGBB sein: {hex}");
        }

        return $"&H{h[4..6]}{h[2..4]}{h[0..2]}&".ToUpperInvariant();
    }

    private static string Escape(string text) => text.Replace("{", "(", StringComparison.Ordinal).Replace("}", ")", StringComparison.Ordinal).Replace("\\", "/", StringComparison.Ordinal);
}
