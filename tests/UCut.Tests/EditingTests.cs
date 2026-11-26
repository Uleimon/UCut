using UCut.Editing;
using UCut.Settings;
using UCut.Transcription;

namespace UCut.Tests;

public class EditingTests
{
    [Fact]
    public void Toter_Anfang_wird_weggeschnitten()
    {
        var plan = CutPlanner.Plan(12, new Transcript([new Word("Ich", 2.8, 3.0)]), new CutSettings());
        Assert.Equal(2.4, plan.Start, 3);
        Assert.Equal(12, plan.End, 3);
        Assert.NotNull(plan.Reason);
    }

    [Fact]
    public void Kurzer_Anfang_bleibt()
    {
        var plan = CutPlanner.Plan(12, new Transcript([new Word("Ich", 0.5, 0.8)]), new CutSettings());
        Assert.Equal(0, plan.Start);
        Assert.Null(plan.Reason);
    }

    [Fact]
    public void Ohne_Sprache_wird_nur_die_Laenge_begrenzt()
    {
        var plan = CutPlanner.Plan(90, new Transcript([]), new CutSettings { MaxLengthSeconds = 60, KeepEndOfLongClips = false });
        Assert.Equal(0, plan.Start);
        Assert.Equal(60, plan.End);
    }

    [Fact]
    public void Langer_Backtrack_behaelt_das_Ende()
    {
        // 150 s Backtrack: der Moment ist am Schluss, nicht in der ersten Minute
        var plan = CutPlanner.Plan(150, new Transcript([]), new CutSettings { MaxLengthSeconds = 60 });
        Assert.Equal(90, plan.Start);
        Assert.Equal(150, plan.End);
        Assert.Contains("letzten", plan.Reason);
    }

    [Fact]
    public void Langer_Clip_beginnt_am_Satzanfang_statt_mitten_im_Wort()
    {
        var words = new Transcript([
            new Word("und", 89.6, 90.2),   // läuft über die 90-s-Grenze
            new Word("dann", 90.25, 90.6), // kein Satzanfang (keine Pause)
            new Word("Ja,", 92.0, 92.3),   // Pause davor: neuer Satz
            new Word("da", 92.35, 92.5),
        ]);
        var plan = CutPlanner.Plan(150, words, new CutSettings { MaxLengthSeconds = 60, LeadInSeconds = 0.25 });
        Assert.Equal(91.75, plan.Start, 3);
        Assert.Equal(150, plan.End);
    }

    [Fact]
    public void Kurzer_Clip_bleibt_wie_bisher()
    {
        var plan = CutPlanner.Plan(45, new Transcript([new Word("Ich", 0.5, 0.8)]), new CutSettings { MaxLengthSeconds = 60 });
        Assert.Equal(0, plan.Start);
        Assert.Equal(45, plan.End);
    }

    [Fact]
    public void Renderbefehl_deckt_ab_legt_Logo_drueber_und_brennt_Untertitel_ein()
    {
        var logo = new LogoSettings();
        var job = new RenderJob("in.mp4", "out.mp4", new CutPlan(2.4, 12, null), 910, 1620, "captions.ass", "logo.png", logo, null);
        var args = RenderCommand.Build(job);
        var graph = args[args.IndexOf("-filter_complex") + 1];

        Assert.Equal("2.4", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("9.6", args[args.IndexOf("-t") + 1]);
        Assert.Contains("drawbox=x=330:y=478:w=232:h=52:color=0x2B2122:t=fill", graph, StringComparison.Ordinal);
        Assert.Contains("[1:v]scale=300:-1[logo]", graph, StringComparison.Ordinal);
        Assert.Contains("overlay=x=446-w/2:y=505-h/2", graph, StringComparison.Ordinal);
        Assert.EndsWith("ass=captions.ass[v3]", graph, StringComparison.Ordinal);
        Assert.Equal("[v3]", args[args.IndexOf("-map") + 1]);
    }

    [Fact]
    public void Ohne_Logo_und_Untertitel_kein_Filter()
    {
        var job = new RenderJob("in.mp4", "out.mp4", new CutPlan(0, 5, null), 1080, 1920, null, null, new LogoSettings(), null);
        Assert.DoesNotContain("-filter_complex", RenderCommand.Build(job));
    }

    [Fact]
    public void Mehrere_Tonspuren_werden_zu_einer_gemischt()
    {
        var job = new RenderJob("in.mp4", "out.mp4", new CutPlan(0, 5, null), 1080, 1920, null, null, new LogoSettings(), null, AudioTracks: 3);
        var args = RenderCommand.Build(job);
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:a:0][0:a:1][0:a:2]amix=inputs=3", graph, StringComparison.Ordinal);
        Assert.Contains("[aout]", args);
        Assert.Contains("0:v", args);
        Assert.DoesNotContain("0:a?", args);
    }

    [Fact]
    public async Task Clip_mit_stummer_erster_Tonspur_hat_trotzdem_Ton()
    {
        // wie Aitum Backtrack: Spur 1 fast still, Spur 2 hat den Ton, Spur 3 leer
        var dir = Directory.CreateTempSubdirectory("ucut-mix-");
        try
        {
            var src = Path.Combine(dir.FullName, "in.mp4");
            await UCut.Media.ProcessRunner.RunCheckedAsync("ffmpeg", [
                "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=gray:s=180x320:d=2", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-t", "2", "-c:v", "libx264", "-c:a", "aac", src]);
            var ff = new UCut.Media.Ffmpeg("ffmpeg", "ffprobe");
            var info = await ff.ProbeAsync(src);
            Assert.Equal(3, info.AudioTracks.Count);

            var outFile = Path.Combine(dir.FullName, "out.mp4");
            var job = new RenderJob(src, outFile, new CutPlan(0, 2, null), 180, 320, null, null, new LogoSettings(), null, AudioTracks: info.AudioTracks.Count);
            await UCut.Media.ProcessRunner.RunCheckedAsync("ffmpeg", RenderCommand.Build(job), dir.FullName);

            var outInfo = await ff.ProbeAsync(outFile);
            Assert.Single(outInfo.AudioTracks);
            var pcm = await ff.ReadPcmAsync(outFile, 0.2, 1.5, 8000);
            // sine at 1/8 full scale is about 4096; mono to stereo and back can take 3 dB off, so 1000 means "clearly not silent"
            var peak = pcm.Max(v => Math.Abs((int)v));
            Assert.True(peak > 1000, $"Ausgabe ist stumm (Spitze {peak})");

            var mixed = await ff.ReadPcmAsync(src, 0.2, 1.5, 8000, info.AudioTracks.Count);
            var mixedPeak = mixed.Max(v => Math.Abs((int)v));
            Assert.True(mixedPeak > 1000, $"Mischung für Whisper ist stumm (Spitze {mixedPeak})");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Automatische_Logo_Hoehe_ersetzt_die_feste()
    {
        var job = new RenderJob("in.mp4", "out.mp4", new CutPlan(0, 5, null), 1000, 2000, null, "logo.png",
                                new LogoSettings { Cover = null, CenterY = null }, 0.25);
        var graph = RenderCommand.Build(job)[RenderCommand.Build(job).IndexOf("-filter_complex") + 1];
        Assert.Contains("y=500-h/2", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("drawbox", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Cam_Kante_wird_gefunden()
    {
        // 90x160: textured "cam" on top until row 52, then a calm "game" area.
        const int w = 90, h = 160;
        var px = new byte[w * h];
        var rnd = new Random(1);
        for (var r = 0; r < h; r++)
        {
            for (var c = 0; c < w; c++)
            {
                px[r * w + c] = r <= 52 ? (byte)(30 + rnd.Next(20)) : (byte)(180 + rnd.Next(20));
            }
        }

        var edge = CamEdgeDetector.FindEdge(px, w, h);
        Assert.NotNull(edge);
        Assert.Equal(53.0 / 160, edge.Value, 3);
    }

    [Fact]
    public void Ohne_klare_Kante_kein_Ergebnis()
    {
        const int w = 90, h = 160;
        var px = new byte[w * h];
        new Random(2).NextBytes(px);
        Assert.Null(CamEdgeDetector.FindEdge(px, w, h));
    }

    [Fact]
    public void Logo_sitzt_knapp_ueber_der_Cam_Kante()
    {
        var logo = Path.Combine(AppContext.BaseDirectory, "assets", "uleimon-logo.png");
        Assert.Equal((1992, 552), LogoPlacement.ReadPngSize(logo));
        var y = LogoPlacement.CenterAboveEdge(0.331, logo, 0.33, 910, 1620);
        Assert.InRange(y, 0.29, 0.31);
    }
}
