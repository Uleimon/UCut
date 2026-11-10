using UCut.Settings;
using UCut.Transcription;

namespace UCut.Editing;

/// <summary>Which part of the source clip is kept (seconds in the source).</summary>
public sealed record CutPlan(double Start, double End, string? Reason)
{
    public double Duration => End - Start;
}

public static class CutPlanner
{
    /// <summary>Cuts away a dead start (silence before the first word) and caps the length.</summary>
    public static CutPlan Plan(double sourceDuration, Transcript transcript, CutSettings settings)
    {
        double start = 0;
        string? reason = null;
        if (transcript.FirstWordStart is { } first && first > settings.MaxDeadStartSeconds)
        {
            start = Math.Max(0, first - settings.LeadInSeconds);
            reason = $"Toter Anfang ({first:0.0} s ohne Sprache) weggeschnitten";
        }

        var end = sourceDuration;
        if (settings.MaxLengthSeconds > 0 && end - start > settings.MaxLengthSeconds)
        {
            end = start + settings.MaxLengthSeconds;
            reason = reason is null ? $"Auf {settings.MaxLengthSeconds:0} s gekürzt" : reason + $", auf {settings.MaxLengthSeconds:0} s gekürzt";
        }

        return new CutPlan(start, end, reason);
    }
}
