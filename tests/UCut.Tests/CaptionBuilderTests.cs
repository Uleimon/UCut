using UCut.Captions;
using UCut.Settings;
using UCut.Transcription;

namespace UCut.Tests;

public class CaptionBuilderTests
{
    // Word times from Ulei's sample clip (the case where captions overlapped in the first test).
    private static readonly Transcript Sample = new(
    [
        new Word("Ich", 2.50, 2.96), new Word("muss", 2.96, 3.30), new Word("die", 3.34, 3.53),
        new Word("Frage", 3.68, 3.80), new Word("kurz", 3.94, 4.10), new Word("stellen,", 4.24, 4.41),
        new Word("wer", 4.56, 4.60), new Word("von", 4.66, 4.70), new Word("euch", 4.78, 4.89),
        new Word("hat", 5.04, 5.05), new Word("noch", 5.20, 5.25), new Word("Kredit", 5.40, 5.53),
        new Word("am", 5.68, 5.73), new Word("Laufen?", 5.88, 6.18),
        new Word("Ich", 6.64, 6.66), new Word("brauch", 6.66, 6.80), new Word("das", 6.80, 6.98),
        new Word("Geld", 6.98, 7.16), new Word("jetzt?", 7.16, 7.60),
    ]);

    [Fact]
    public void Untertitel_ueberlappen_sich_nie()
    {
        var events = new CaptionBuilder(new CaptionSettings()).BuildEvents(Sample);
        for (var i = 0; i < events.Count - 1; i++)
        {
            Assert.True(events[i].End <= events[i + 1].Start, $"Event {i} endet {events[i].End} nach Start von {i + 1} ({events[i + 1].Start})");
        }
    }

    [Fact]
    public void Ueberlappung_bleibt_auch_nach_Rundung_auf_Hundertstel_aus()
    {
        var ass = new CaptionBuilder(new CaptionSettings()).BuildAss(Sample, 910, 1620);
        var times = ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal))
            .Select(l => l.Split(',')).Select(p => (Start: TimeSpan.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), End: TimeSpan.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(Sample.Words.Count, times.Count);
        for (var i = 0; i < times.Count - 1; i++)
        {
            Assert.True(times[i].End <= times[i + 1].Start);
        }
    }

    [Fact]
    public void Kurze_Pausen_werden_ueberbrueckt_lange_nicht()
    {
        var t = new Transcript([new Word("a", 0, 0.5), new Word("b.", 0.6, 1.0), new Word("c", 3.0, 3.5)]);
        var e = new CaptionBuilder(new CaptionSettings()).BuildEvents(t);
        Assert.Equal(0.6, e[0].End, 3);           // gap 0.1 s bridged
        Assert.Equal(1.15, e[1].End, 3);          // long pause: shown 0.15 s longer, then gone
        Assert.Equal(3.0, e[2].Start, 3);
    }

    [Fact]
    public void Gruppen_enden_nach_Satzzeichen_oder_maximaler_Wortzahl()
    {
        var groups = CaptionBuilder.Group(Sample.Words, 3);
        Assert.Equal(["Ich", "muss", "die"], groups[0].Select(w => w.Text));
        Assert.Equal(["Frage", "kurz", "stellen,"], groups[1].Select(w => w.Text));
        Assert.Equal(["am", "Laufen?"], groups[4].Select(w => w.Text));
    }

    [Fact]
    public void Aktuelles_Wort_ist_hervorgehoben_und_gross_geschrieben()
    {
        var ass = new CaptionBuilder(new CaptionSettings()).BuildAss(new Transcript([new Word("hallo", 0, 1), new Word("du", 1, 2)]), 1080, 1920);
        Assert.Contains(@"{\c&H00E5FF&\fscx110\fscy110}HALLO{\r} DU", ass, StringComparison.Ordinal);
        Assert.Contains("PlayResY: 1920", ass, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0:00:00.00")]
    [InlineData(3.456, "0:00:03.46")]
    [InlineData(61.5, "0:01:01.50")]
    [InlineData(3725.01, "1:02:05.01")]
    public void Zeitformat(double s, string expected) => Assert.Equal(expected, CaptionBuilder.FormatTime(s));

    [Fact]
    public void Farbe_wird_ins_ASS_Format_umgedreht() => Assert.Equal("&H00E5FF&", CaptionBuilder.ToAssColor("#FFE500"));
}
