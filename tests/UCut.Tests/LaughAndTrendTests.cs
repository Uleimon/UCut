using UCut.Analysis;
using UCut.Media;
using UCut.Transcription;

namespace UCut.Tests;

public class LaughAndTrendTests
{
    private const int Rate = 8000;

    /// <summary>Quiet talk-like noise with, from <paramref name="laughAt"/> for 1.5 s, loud pulses 5 times a second ("ha-ha-ha").</summary>
    private static short[] Sound(double seconds, double? laughAt, double pulsesPerSecond = 5, int seed = 1)
    {
        var rnd = new Random(seed);
        var pcm = new short[(int)(seconds * Rate)];
        for (var i = 0; i < pcm.Length; i++)
        {
            var t = (double)i / Rate;
            var amp = 600.0;
            if (laughAt is { } at && t >= at && t < at + 1.5)
            {
                var phase = (t - at) * pulsesPerSecond % 1.0;
                amp = phase < 0.45 ? 9000 : 400;
            }

            pcm[i] = (short)((rnd.NextDouble() * 2 - 1) * amp);
        }

        return pcm;
    }

    [Fact]
    public void Lachen_im_Ton_wird_gefunden()
    {
        var r = LaughDetector.Detect(Sound(8, 4.0), Rate, new Transcript([]));
        Assert.Equal(1, r.Count);
        Assert.Equal(1, r.FromAudio);
        Assert.InRange(r.Moments[0], 3.0, 4.6);
    }

    [Fact]
    public void Gleichmaessiger_Ton_ist_kein_Lachen()
    {
        Assert.Empty(LaughDetector.Detect(Sound(8, null), Rate, new Transcript([])).Moments);
    }

    [Fact]
    public void Lauter_Ausschlag_waehrend_gesprochen_wird_zaehlt_nicht()
    {
        var talk = new Transcript(Enumerable.Range(0, 12).Select(i => new Word("wort", 3.8 + i * 0.15, 3.9 + i * 0.15)).ToList());
        Assert.Empty(LaughDetector.FromSound(LaughDetector.Envelope(Sound(8, 4.0), Rate), talk));
    }

    [Theory]
    [InlineData("Hahaha", true)]
    [InlineData("haha!", true)]
    [InlineData("HEHE", true)]
    [InlineData("xD", true)]
    [InlineData("(lacht)", true)]
    [InlineData("[Lachen]", true)]
    [InlineData("lol", true)]
    [InlineData("hallo", false)]
    [InlineData("hier", false)]
    [InlineData("Haus", false)]
    [InlineData("ha", false)]
    public void Lach_Woerter(string word, bool laugh) => Assert.Equal(laugh, LaughDetector.IsLaughWord(word));

    [Fact]
    public void Lach_Wort_und_Ton_an_derselben_Stelle_zaehlen_einmal()
    {
        var t = new Transcript([new Word("Hahaha", 4.2, 4.8)]);
        var r = LaughDetector.Detect(Sound(8, 4.0), Rate, t);
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Vorschaubild_kommt_kurz_nach_dem_ersten_Lacher_sonst_an_die_lauteste_Stelle()
    {
        var loud = new LoudnessProfile([(1.0, -30), (2.0, -12), (3.0, -25)]);
        Assert.Equal(3.4, YouTubeThumbnail.PickMoment(new LaughResult([3.0, 6.0], 1, 1), loud, 10), 3);
        Assert.Equal(1.8, YouTubeThumbnail.PickMoment(LaughResult.None, loud, 10), 3);
        Assert.Equal(4.9, YouTubeThumbnail.PickMoment(new LaughResult([4.9], 1, 0), loud, 5), 3);
    }

    [Fact]
    public void Vorschaubild_Titel_ohne_Hashtags_und_ohne_Klammern()
    {
        Assert.Equal("Wer hat {Kredit}?".Replace("{", "(", StringComparison.Ordinal).Replace("}", ")", StringComparison.Ordinal),
            YouTubeThumbnail.Clean("Wer hat {Kredit}? #gta5 #fyp"));
        var ass = YouTubeThumbnail.BuildAss("Wer hat Kredit? 😂", 1080, 1920, "Arial");
        Assert.Contains("PlayResY: 1920", ass, StringComparison.Ordinal);
        Assert.Contains(",WER HAT KREDIT? 😂", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_Hashtags_aus_der_Antwortzeile()
    {
        var tags = ClaudeTrendHashtags.Parse("Ich habe gesucht ... #falsch im Text\nHASHTAGS: #GTA6 #gtaonline #gta5clips #x #fivem #gtarp #gta #zuviel");
        Assert.Equal(["#gta6", "#gtaonline", "#gta5clips", "#fivem", "#gtarp", "#gta"], tags);
    }

    [Fact]
    public async Task Trend_Hashtags_werden_pro_Spiel_drei_Tage_gemerkt()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-trend-").FullName;
        var path = Path.Combine(dir, "trend.json");
        var asked = 0;
        var time = new FakeTime(DateTimeOffset.Parse("2026-11-11T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var source = new ClaudeTrendHashtags("m", path, (game, _) =>
        {
            asked++;
            return Task.FromResult($"HASHTAGS: #{game.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)}trend #neu{asked}");
        }, time);

        Assert.Equal(["#gtavtrend", "#neu1"], await source.GetAsync("GTA V"));
        Assert.Equal(["#gtavtrend", "#neu1"], await source.GetAsync("GTA V"));
        Assert.Equal(1, asked);
        time.Now += TimeSpan.FromDays(4);
        Assert.Equal(["#gtavtrend", "#neu2"], await source.GetAsync("GTA V"));
    }

    [Fact]
    public async Task Trend_Hashtags_Fehler_gibt_die_alte_Liste()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-trend-").FullName;
        var path = Path.Combine(dir, "trend.json");
        var time = new FakeTime(DateTimeOffset.UtcNow);
        await new ClaudeTrendHashtags("m", path, (_, _) => Task.FromResult("HASHTAGS: #alt"), time).GetAsync("Minecraft");
        time.Now += TimeSpan.FromDays(10);
        var failing = new ClaudeTrendHashtags("m", path, (_, _) => throw new HttpRequestException("offline"), time);
        Assert.Equal(["#alt"], await failing.GetAsync("Minecraft"));
        Assert.Empty(await failing.GetAsync("Fortnite"));
    }

    [Fact]
    public async Task Vorlage_nimmt_zwei_Trend_Hashtags_mit()
    {
        var text = await new TemplateClipWriter().WriteAsync(new ClipContext("Hallo.", "GTA V", 10, ["#gta5"], ["#gaming"], "Uleimon", ["#gtaonline", "#fivem", "#dritter"], 2));
        Assert.Equal(["#gta5", "#gtaonline", "#fivem", "#gaming"], text.Hashtags);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
