namespace UCut.Transcription;

/// <summary>One spoken word with its time in the clip (seconds).</summary>
public sealed record Word(string Text, double Start, double End);

public sealed record Transcript(IReadOnlyList<Word> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text)).Trim();

    public double? FirstWordStart => Words.Count > 0 ? Words[0].Start : null;

    public double? LastWordEnd => Words.Count > 0 ? Words[^1].End : null;

    /// <summary>Returns the transcript shifted by <paramref name="offset"/> seconds, dropping words that end before 0.</summary>
    public Transcript Shift(double offset) =>
        new(Words.Select(w => w with { Start = Math.Max(0, w.Start + offset), End = w.End + offset })
                 .Where(w => w.End > 0).ToList());
}

public interface ITranscriber
{
    Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default);
}
