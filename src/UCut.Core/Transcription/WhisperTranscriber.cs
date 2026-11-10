using Whisper.net;
using Whisper.net.Ggml;
using UCut.Settings;

namespace UCut.Transcription;

/// <summary>Local speech recognition with whisper.cpp (Whisper.net). The model is downloaded on first use.</summary>
public sealed class WhisperTranscriber(WhisperSettings settings, Action<string>? log = null) : ITranscriber
{
    public async Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default)
    {
        var type = ParseModel(settings.Model);
        var modelPath = await EnsureModelAsync(type, ct).ConfigureAwait(false);

        // DTW timestamps give usable per-word times; the plain token timestamps of whisper.cpp drift by seconds.
        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
        {
            UseDtwTimeStamps = true,
            HeadsPreset = HeadsFor(type),
        });

        // Beam search: with greedy decoding whisper.cpp tends to invent things like *Kreis* "..." on game audio.
        var builder = factory.CreateBuilder().WithTokenTimestamps();
        builder = (WhisperProcessorBuilder)builder.WithBeamSearchSamplingStrategy(_ => { });
        builder = settings.Language is "" or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(settings.Language);

        await using var processor = builder.Build();
        await using var stream = File.OpenRead(wav16kMono);
        var tokens = new List<RawToken>();
        await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
        {
            foreach (var t in seg.Tokens)
            {
                var start = t.DtwTimestamp >= 0 ? t.DtwTimestamp / 100.0 : t.Start / 100.0;
                tokens.Add(new RawToken(t.Text ?? "", start));
            }
        }

        return new Transcript(BuildWords(tokens));
    }

    internal sealed record RawToken(string Text, double Time);

    /// <summary>DTW marks the time a token is spoken a little late; shift words slightly earlier.</summary>
    internal const double DtwLeadSeconds = 0.12;

    /// <summary>
    /// Glues whisper tokens into words: a token starting with a space starts a new word, other tokens
    /// ("redit" after " K") continue the current word. Special tokens ([_BEG_], [_TT_123]) and
    /// non-speech markers (*Lachen*, [Musik], (lacht)) are dropped.
    /// </summary>
    internal static List<Word> BuildWords(IReadOnlyList<RawToken> tokens)
    {
        var words = new List<(string Text, double Start, double LastTokenTime)>();
        foreach (var token in tokens)
        {
            if (token.Text.StartsWith("[_", StringComparison.Ordinal) || token.Text.Length == 0)
            {
                continue;
            }

            var startsWord = token.Text.StartsWith(' ') || words.Count == 0;
            var text = token.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (startsWord)
            {
                words.Add((text, Math.Max(0, token.Time - DtwLeadSeconds), token.Time));
            }
            else if (words.Count > 0)
            {
                var last = words[^1];
                words[^1] = (last.Text + text, last.Start, token.Time);
            }
        }

        var result = new List<Word>();
        for (var i = 0; i < words.Count; i++)
        {
            var (text, start, lastTime) = words[i];
            var clean = text.Trim('"', '\u201E', '\u201C');
            if (IsNonSpeechMarker(clean))
            {
                continue;
            }

            var end = i + 1 < words.Count ? Math.Min(words[i + 1].Start, lastTime + 0.4) : lastTime + 0.3;
            if (result.Count > 0 && start < result[^1].Start)
            {
                start = result[^1].Start; // keep times monotonic
            }

            result.Add(new Word(clean, start, Math.Max(end, start + 0.05)));
        }

        return result;
    }

    private static bool IsNonSpeechMarker(string text) =>
        text.Length == 0 || IsPunctuationOnly(text) ||
        (text.StartsWith('*') && text.EndsWith('*')) || text.StartsWith('[') || text.StartsWith('(') || text.StartsWith('*');

    private static bool IsPunctuationOnly(string s) => s.All(char.IsPunctuation);

    private static WhisperAlignmentHeadsPreset HeadsFor(GgmlType type) => type switch
    {
        GgmlType.Tiny => WhisperAlignmentHeadsPreset.Tiny,
        GgmlType.Base => WhisperAlignmentHeadsPreset.Base,
        GgmlType.Small => WhisperAlignmentHeadsPreset.Small,
        GgmlType.Medium => WhisperAlignmentHeadsPreset.Medium,
        GgmlType.LargeV3 => WhisperAlignmentHeadsPreset.LargeV3,
        GgmlType.LargeV3Turbo => WhisperAlignmentHeadsPreset.LargeV3Turbo,
        _ => WhisperAlignmentHeadsPreset.None,
    };

    private async Task<string> EnsureModelAsync(GgmlType type, CancellationToken ct)
    {
        var folder = string.IsNullOrWhiteSpace(settings.ModelFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCut", "models")
            : settings.ModelFolder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        log?.Invoke($"Lade Whisper-Modell '{type}' herunter (einmalig) ...");
        var tmp = path + ".part";
        await using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: ct).ConfigureAwait(false))
        await using (var file = File.Create(tmp))
        {
            await model.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
        return path;
    }

    internal static GgmlType ParseModel(string name) => name.Trim().ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "base" => GgmlType.Base,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        "large" or "largev3" or "large-v3" => GgmlType.LargeV3,
        "turbo" or "largev3turbo" or "large-v3-turbo" => GgmlType.LargeV3Turbo,
        _ => throw new ArgumentException($"Unbekanntes Whisper-Modell '{name}'. Erlaubt: tiny, base, small, medium, large, turbo."),
    };
}
