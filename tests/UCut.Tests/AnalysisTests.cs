using UCut.Analysis;
using UCut.Media;
using UCut.Metadata;
using UCut.Pipeline;
using UCut.Transcription;

namespace UCut.Tests;

public class AnalysisTests
{
    private static LoudnessProfile Loudness(params double[] values) =>
        new(values.Select((v, i) => (i * 0.1, v)).ToList());

    [Fact]
    public void Guter_Clip_bekommt_hohen_Score()
    {
        var words = Enumerable.Range(0, 20).Select(i => new Word("w", 0.3 + i * 0.4, 0.6 + i * 0.4)).ToList();
        var loud = Loudness([.. Enumerable.Repeat(-30.0, 40), .. Enumerable.Repeat(-18.0, 5)]);
        var s = ClipScorer.Score(10, new Transcript(words), loud, hasCaptions: true);
        Assert.Equal(100, s.Total);
    }

    [Fact]
    public void Langer_stiller_Anfang_kostet_Punkte()
    {
        var s = ClipScorer.Score(10, new Transcript([new Word("spät", 4, 4.5)]), Loudness(-30, -30, -30, -30, -30, -30), hasCaptions: true);
        Assert.Equal(5, s.Parts.Single(p => p.Name == "Hook").Points);
        Assert.True(s.Total < 60);
    }

    [Fact]
    public async Task Template_nimmt_ersten_Satz_und_Hashtags()
    {
        var t = await new TemplateClipWriter().WriteAsync(new ClipContext(
            "Wer hat noch Kredit am Laufen? Ich brauch das Geld.", "GTA V", 9, ["#gta5"], ["#fyp"], "Uleimon"));
        Assert.Equal("Wer hat noch Kredit am Laufen?", t.Title);
        Assert.Equal("Wer hat noch Kredit am Laufen? #gta5 #fyp", t.TikTokCaption);
        Assert.EndsWith("#shorts", t.YouTubeTitle, StringComparison.Ordinal);
        Assert.Contains("Uleimon", t.YouTubeDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_Antwort_wird_gelesen()
    {
        var t = ClaudeClipWriter.Parse("""
            {"funniness":12,"funniness_reason":"Pointe sitzt","title":"Kredit? 😂","tiktok_caption":"Kredit? 😂 #gta5",
             "youtube_title":"Kredit? 😂 #shorts","youtube_description":"Live auf Twitch: Uleimon","hashtags":["gta5","#fyp"]}
            """);
        Assert.Equal(10, t.Funniness); // clamped
        Assert.Equal(["#gta5", "#fyp"], t.Hashtags);
        Assert.Equal("Claude", t.Source);
    }

    [Fact]
    public void Infodatei_speichern_und_laden()
    {
        var text = new ClipText("T", "T #a", "T #shorts", "D", ["#a", "#b"], 7, "lustig", "Claude");
        var score = new ClipScore(80, [new ScorePart("Hook", 25, 25, "ok")]);
        var meta = ClipMetadata.Create("/x/GTA V/clip.mp4", "/in/c.mp4", "GTA V", "Twitch", 9.5, null, "hallo", score, text, DateTimeOffset.UnixEpoch);
        var path = Path.Combine(Path.GetTempPath(), $"ucut-test-{Guid.NewGuid():N}.json");
        try
        {
            meta.Save(path);
            var json = File.ReadAllText(path);
            Assert.Contains("\"caption\": \"T #a\"", json, StringComparison.Ordinal);
            var back = ClipMetadata.Load(path);
            Assert.Equal("clip.mp4", back.Video);
            Assert.Equal(["a", "b"], back.YouTube.Tags);
            Assert.Equal(7, back.Funniness!.Score);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Wer hat noch Kredit am Laufen? 😂", "wer-hat-noch-kredit-am-laufen")]
    [InlineData("Größter Fail überhaupt!!!", "groesster-fail-ueberhaupt")]
    [InlineData("😂😂", "")]
    public void Dateinamen_aus_Titel(string title, string slug) => Assert.Equal(slug, OutputNaming.Slug(title));

    [Fact]
    public void Clip_Pfad_liegt_im_Spielordner()
    {
        var p = OutputNaming.ClipPath("/out", "GTA V", new DateTimeOffset(2026, 11, 10, 15, 7, 0, TimeSpan.Zero), "Kredit?");
        Assert.Equal(Path.Combine("/out", "GTA V", "2026-11-10_150700_kredit.mp4"), p);
    }
}
