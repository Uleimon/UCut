using System.Text.RegularExpressions;
using UCut.Transcription;

namespace UCut.Analysis;

/// <summary>Laughs found in a clip: when (seconds from the clip start) and how they were found.</summary>
public sealed record LaughResult(IReadOnlyList<double> Moments, int FromAudio, int FromText)
{
    public static LaughResult None { get; } = new([], 0, 0);
    public int Count => Moments.Count;
}

/// <summary>
/// Finds laughter without an AI model:
/// 1. Words in the transcript ("haha", "hahaha", "lol", "xD", "(lacht)", "[Lachen]" ...).
/// 2. The sound: laughing is a loud burst of short, evenly spaced pulses ("ha-ha-ha", about 3 to 8 per second).
///    Speech is quieter and less regular, music and game sound is smooth. So: loud windows with a strong rhythm
///    in that range where the transcript has (almost) no words count as a laugh.
/// </summary>
public static partial class LaughDetector
{
    internal const int FrameMs = 20;
    private const int WindowFrames = 60;     // 1.2 s
    private const int StepFrames = 15;       // 0.3 s
    private const int MinLag = 6;            // 120 ms between pulses = 8 per second
    private const int MaxLag = 17;           // 340 ms = 3 per second
    internal const double MinRhythm = 0.45;
    internal const double MinLouder = 5;     // dB above the typical level of the clip
    private const double MergeSeconds = 1.0;

    public static bool IsLaughWord(string word) => LaughWord().IsMatch(word.Trim());

    /// <param name="pcm">Mono 16-bit samples of the clip.</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="transcript">Words with times relative to the same clip.</param>
    public static LaughResult Detect(ReadOnlySpan<short> pcm, int sampleRate, Transcript transcript)
    {
        var text = transcript.Words.Where(w => IsLaughWord(w.Text)).Select(w => w.Start).ToList();
        var audio = FromSound(Envelope(pcm, sampleRate), transcript);

        var all = new List<double>();
        var fromAudio = 0;
        var fromText = 0;
        foreach (var (t, isText) in text.Select(t => (t, true)).Concat(audio.Select(t => (t, false))).OrderBy(x => x.t))
        {
            if (all.Count > 0 && t - all[^1] < MergeSeconds)
            {
                continue;
            }

            all.Add(Math.Round(t, 2));
            if (isText)
            {
                fromText++;
            }
            else
            {
                fromAudio++;
            }
        }

        return new LaughResult(all, fromAudio, fromText);
    }

    /// <summary>Loudness in dB per 20 ms frame.</summary>
    internal static double[] Envelope(ReadOnlySpan<short> pcm, int sampleRate)
    {
        var frame = Math.Max(1, sampleRate * FrameMs / 1000);
        var n = pcm.Length / frame;
        var env = new double[n];
        for (var i = 0; i < n; i++)
        {
            double sum = 0;
            var part = pcm.Slice(i * frame, frame);
            foreach (var s in part)
            {
                sum += (double)s * s;
            }

            env[i] = 20 * Math.Log10(Math.Sqrt(sum / frame) + 1);
        }

        return env;
    }

    /// <summary>Start times (seconds) of laugh-like windows, merged into events.</summary>
    internal static List<double> FromSound(double[] env, Transcript transcript)
    {
        var events = new List<double>();
        if (env.Length < WindowFrames)
        {
            return events;
        }

        // Typical level = median of the frames that are not silent.
        var voiced = env.Where(v => v > 30).OrderBy(v => v).ToList();
        if (voiced.Count < WindowFrames / 2)
        {
            return events;
        }

        var typical = voiced[voiced.Count / 2];
        double? runStart = null;
        double lastHit = double.MinValue;
        for (var start = 0; start + WindowFrames <= env.Length; start += StepFrames)
        {
            var window = env.AsSpan(start, WindowFrames);
            var t0 = start * FrameMs / 1000.0;
            var t1 = t0 + WindowFrames * FrameMs / 1000.0;
            var hit = Average(window) >= typical + MinLouder
                && Rhythm(window) >= MinRhythm
                && WordsIn(transcript, t0, t1) <= 1;
            if (hit)
            {
                runStart ??= t0;
                lastHit = t1;
            }
            else if (runStart is { } s && t0 > lastHit)
            {
                events.Add(s);
                runStart = null;
            }
        }

        if (runStart is { } open)
        {
            events.Add(open);
        }

        return events;
    }

    private static double Average(ReadOnlySpan<double> v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += x;
        }

        return sum / v.Length;
    }

    /// <summary>How regular the pulses are: highest autocorrelation of the loudness curve at 3-8 pulses per second (0..1).</summary>
    internal static double Rhythm(ReadOnlySpan<double> window)
    {
        var mean = Average(window);
        var d = new double[window.Length];
        double energy = 0;
        for (var i = 0; i < d.Length; i++)
        {
            d[i] = window[i] - mean;
            energy += d[i] * d[i];
        }

        // A flat sound (music, engine noise) has no pulses at all.
        if (energy / d.Length < 4)
        {
            return 0;
        }

        var best = 0.0;
        for (var lag = MinLag; lag <= MaxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < d.Length; i++)
            {
                sum += d[i] * d[i + lag];
            }

            best = Math.Max(best, sum / energy * d.Length / (d.Length - lag));
        }

        return best;
    }

    private static int WordsIn(Transcript transcript, double from, double to) =>
        transcript.Words.Count(w => w.End > from && w.Start < to && !IsLaughWord(w.Text));

    [GeneratedRegex(@"^[\(\[\*]*(?:(?:ha|he|hi)(?:ha|he|hi|h|a)+|a?(?:ha){2,}h*|lo+l+|lmao|x+d+|lacht|lachen|gelächter|laughs?|laughter|laughing)[\)\]\*!.,?…]*$", RegexOptions.IgnoreCase)]
    private static partial Regex LaughWord();
}
