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
    /// <summary>A gap between words at least this long counts as the start of a new sentence.</summary>
    internal const double PauseSeconds = 0.35;

    /// <summary>How far the start may move forward to begin at a sentence instead of mid-sentence.</summary>
    internal const double MaxSnapSeconds = 8;

    /// <summary>
    /// Short clips: cuts away a dead start (silence before the first word).
    /// Clips longer than the cap: an Aitum Backtrack saves the minutes before the hotkey, so the
    /// moment is at the end. Keeps the last part and lets it begin at a sentence, not in the middle of a word. With KeepEndOfLongClips off the start is kept.
    /// </summary>
    public static CutPlan Plan(double sourceDuration, Transcript transcript, CutSettings settings)
    {
        var max = settings.MaxLengthSeconds;
        if (max > 0 && sourceDuration > max && settings.KeepEndOfLongClips)
        {
            return PlanEnd(sourceDuration, transcript, settings, max);
        }

        double start = 0;
        string? reason = null;
        if (transcript.FirstWordStart is { } first && first > settings.MaxDeadStartSeconds)
        {
            start = Math.Max(0, first - settings.LeadInSeconds);
            reason = $"Toter Anfang ({first:0.0} s ohne Sprache) weggeschnitten";
        }

        var end = sourceDuration;
        if (max > 0 && end - start > max)
        {
            end = start + max;
            reason = reason is null ? $"Auf {max:0} s gekürzt" : reason + $", auf {max:0} s gekürzt";
        }

        return new CutPlan(start, end, reason);
    }

    private static CutPlan PlanEnd(double duration, Transcript transcript, CutSettings settings, double max)
    {
        var words = transcript.Words;
        var end = duration;
        var windowStart = Math.Max(0, end - max);
        var start = windowStart;
        if (words.Count > 0)
        {
            // first word inside the window that begins a sentence (pause before it)
            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (w.Start < windowStart)
                {
                    continue;
                }

                if (w.Start > windowStart + MaxSnapSeconds)
                {
                    break;
                }

                var sentenceStart = i == 0 || w.Start - words[i - 1].End >= PauseSeconds;
                if (sentenceStart)
                {
                    start = Math.Max(windowStart, w.Start - settings.LeadInSeconds);
                    break;
                }
            }
        }

        return new CutPlan(start, end, $"Langer Clip ({duration:0} s): die letzten {end - start:0} s behalten (Backtrack, der Moment ist am Ende)");
    }
}
