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
        var plan = CutPlanner.Plan(90, new Transcript([]), new CutSettings { MaxLengthSeconds = 60 });
        Assert.Equal(0, plan.Start);
        Assert.Equal(60, plan.End);
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
