using UCut.Media;
using UCut.Transcription;

namespace UCut.Analysis;

public sealed record ScorePart(string Name, int Points, int Max, string Note);

/// <summary>
/// Heuristic score 0-100 for "does this clip have what short-video platforms reward".
/// No platform publishes its algorithm; these are the commonly known factors (hook, length, talking, energy, captions).
/// </summary>
public sealed record ClipScore(int Total, IReadOnlyList<ScorePart> Parts);

public static class ClipScorer
{
    /// <param name="transcript">Transcript with times relative to the FINAL (cut) clip.</param>
    public static ClipScore Score(double duration, Transcript transcript, LoudnessProfile loudness, bool hasCaptions)
    {
        var parts = new List<ScorePart>();

        // Hook: something is said right at the start.
        var first = transcript.FirstWordStart;
        parts.Add(first switch
        {
            null => new ScorePart("Hook", 5, 25, "Es wird nichts gesagt"),
            <= 0.6 => new ScorePart("Hook", 25, 25, $"Sprache ab {first:0.0} s"),
            <= 1.5 => new ScorePart("Hook", 15, 25, $"Sprache erst ab {first:0.0} s"),
            _ => new ScorePart("Hook", 5, 25, $"Sprache erst ab {first:0.0} s, Zuschauer wischen vorher weg"),
        });

        // Length: short clips are watched to the end more often.
        parts.Add(duration switch
        {
            < 5 => new ScorePart("Länge", 8, 20, $"{duration:0} s ist sehr kurz"),
            <= 35 => new ScorePart("Länge", 20, 20, $"{duration:0} s, ideal"),
            <= 60 => new ScorePart("Länge", 12, 20, $"{duration:0} s, eher lang"),
            _ => new ScorePart("Länge", 5, 20, $"{duration:0} s, zu lang für Shorts"),
        });

        // Talking density.
        var wps = duration > 0 ? transcript.Words.Count / duration : 0;
        parts.Add(wps switch
        {
            >= 1.2 => new ScorePart("Sprache", 20, 20, $"{wps:0.0} Wörter/s"),
            >= 0.6 => new ScorePart("Sprache", 12, 20, $"{wps:0.0} Wörter/s"),
            _ => new ScorePart("Sprache", 4, 20, $"wenig Sprache ({wps:0.0} Wörter/s)"),
        });

        // Energy: loud peaks (shouting, laughing, explosions) compared to the typical level.
        parts.Add(EnergyPart(loudness));

        parts.Add(hasCaptions
            ? new ScorePart("Untertitel", 15, 15, "eingebrannt")
            : new ScorePart("Untertitel", 0, 15, "keine Untertitel"));

        return new ClipScore(parts.Sum(p => p.Points), parts);
    }

    internal static ScorePart EnergyPart(LoudnessProfile loudness)
    {
        var values = loudness.Points.Select(p => p.Lufs).Where(v => v > -70).OrderBy(v => v).ToList();
        if (values.Count < 5)
        {
            return new ScorePart("Energie", 5, 20, "kaum Ton");
        }

        var median = values[values.Count / 2];
        var peak = values[(int)(values.Count * 0.95)];
        var range = peak - median;
        return range switch
        {
            >= 8 => new ScorePart("Energie", 20, 20, $"deutliche Ausschläge (+{range:0.0} LU)"),
            >= 4 => new ScorePart("Energie", 12, 20, $"leichte Ausschläge (+{range:0.0} LU)"),
            _ => new ScorePart("Energie", 6, 20, "gleichmäßiger Ton ohne Höhepunkt"),
        };
    }
}
