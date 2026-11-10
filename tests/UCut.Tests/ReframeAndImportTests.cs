using UCut.Editing;
using UCut.Games;
using UCut.Pipeline;
using UCut.Settings;

namespace UCut.Tests;

public class ReframeAndImportTests
{
    [Theory]
    [InlineData(1080, 1920)] // 9:16
    [InlineData(910, 1620)]  // Aitum layout from the sample clip
    [InlineData(720, 1280)]
    public void Hochformat_bleibt_wie_es_ist(int w, int h) => Assert.Null(Reframer.Plan(w, h, new ReframeSettings()));

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1080)]
    [InlineData(1080, 1440)] // 3:4 would get black bars on TikTok
    public void Querformat_und_Quadrat_werden_umgewandelt(int w, int h)
    {
        var plan = Reframer.Plan(w, h, new ReframeSettings());
        Assert.NotNull(plan);
        Assert.Equal((1080, 1920), (plan.TargetWidth, plan.TargetHeight));
    }

    [Fact]
    public void Abgeschaltet_heisst_nichts_aendern() =>
        Assert.Null(Reframer.Plan(1920, 1080, new ReframeSettings { Enabled = false }));

    [Fact]
    public void Zuschneiden_nimmt_einen_9_16_Ausschnitt_an_der_gewaehlten_Stelle()
    {
        var plan = Reframer.Plan(1920, 1080, new ReframeSettings { Mode = ReframeMode.Crop, FocusX = 0.25 })!;
        Assert.Equal("[0:v]crop=w=trunc(ih*1080/1920/2)*2:h=ih:x=(iw-ow)*0.25:y=0,scale=1080:1920,setsar=1[v1]", plan.Filter("[0:v]", "[v1]"));
    }

    [Fact]
    public void Unscharfer_Hintergrund_legt_das_ganze_Bild_in_die_Mitte()
    {
        var f = Reframer.Plan(1920, 1080, new ReframeSettings { Mode = ReframeMode.Blur })!.Filter("[0:v]", "[v1]");
        Assert.StartsWith("[0:v]split=2", f, StringComparison.Ordinal);
        Assert.Contains("boxblur", f, StringComparison.Ordinal);
        Assert.EndsWith("overlay=(W-w)/2:(H-h)/2,setsar=1[v1]", f, StringComparison.Ordinal);
    }

    [Fact]
    public void Umgewandelte_Clips_bekommen_keine_Abdeckflaeche()
    {
        var reframe = Reframer.Plan(1920, 1080, new ReframeSettings());
        var job = new RenderJob("in.mp4", "out.mp4", new CutPlan(0, 5, null), 1080, 1920, "captions.ass", "logo.png", new LogoSettings(), null, reframe);
        var args = RenderCommand.Build(job);
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.StartsWith("[0:v]crop=", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("drawbox", graph, StringComparison.Ordinal);
        Assert.Contains("[1:v]scale=356:-1[logo]", graph, StringComparison.Ordinal);
        Assert.EndsWith("ass=captions.ass[v3]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordner_und_Dateien_werden_eingesammelt()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-import-");
        try
        {
            foreach (var name in new[] { "b.mp4", "a.MKV", "notiz.txt", "c.mov" })
            {
                File.WriteAllText(Path.Combine(dir.FullName, name), "x");
            }

            var files = ClipImport.Collect([dir.FullName, Path.Combine(dir.FullName, "b.mp4"), "/gibt/es/nicht.mp4"], out var missing);
            Assert.Equal(["a.MKV", "b.mp4", "c.mov"], files.Select(Path.GetFileName));
            Assert.Equal(["/gibt/es/nicht.mp4"], missing);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class FixedSource(string game) : IGameSource
    {
        public string Name => "Twitch";
        public Task<string?> GetCurrentGameAsync(CancellationToken ct = default) => Task.FromResult<string?>(game);
    }

    [Fact]
    public async Task Spiel_bei_eigenen_Clips()
    {
        var file = Path.GetTempFileName();
        try
        {
            var settings = new GameSettings();
            var detector = new GameDetector(settings, [new FixedSource("Minecraft")]);
            var now = File.GetLastWriteTime(file);

            Assert.Equal("GTA V", (await ClipImport.ChooseGameAsync(file, "Grand Theft Auto V", settings, detector, now)).FolderName);
            Assert.Equal("Minecraft", (await ClipImport.ChooseGameAsync(file, null, settings, detector, now.AddMinutes(5))).FolderName);
            Assert.Equal("Unsortiert", (await ClipImport.ChooseGameAsync(file, null, settings, detector, now.AddDays(3))).FolderName);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Split_ohne_gefundene_Cam_faellt_auf_Zuschneiden_zurueck()
    {
        Assert.Equal(ReframeMode.Crop, Reframer.Plan(1920, 1080, new ReframeSettings())!.Mode);
        Assert.Equal(ReframeMode.Blur, Reframer.Plan(1920, 1080, new ReframeSettings { FallbackMode = ReframeMode.Blur })!.Mode);
    }

    [Fact]
    public void Split_setzt_Cam_oben_und_Spiel_darunter()
    {
        // Cam 480x284 bottom right in 1920x1080 -> strip 1080x640 on top, game 1080x1280 below.
        var plan = Reframer.Plan(1920, 1080, new ReframeSettings(), new NormRect(1416 / 1920.0, 772 / 1080.0, 480 / 1920.0, 284 / 1080.0))!;
        Assert.Equal(ReframeMode.Split, plan.Mode);
        Assert.Equal(640, plan.CamStripHeight);
        var f = plan.Filter("[0:v]", "[v1]");
        Assert.Contains("[rf_cam]crop=480:284:1416:772,scale=1080:640", f, StringComparison.Ordinal);
        Assert.Contains("scale=1080:1280", f, StringComparison.Ordinal);
        Assert.EndsWith("vstack=inputs=2[v1]", f, StringComparison.Ordinal);
    }

    [Fact]
    public void Sehr_hohe_Cam_wird_auf_die_maximale_Hoehe_begrenzt()
    {
        var plan = Reframer.Plan(1920, 1080, new ReframeSettings(), new NormRect(0, 0, 0.25, 0.6))!;
        Assert.Equal(864, plan.CamStripHeight); // 45 % of 1920
        Assert.Contains("crop=480:384:0:132", plan.Filter("[0:v]", "[v1]"), StringComparison.Ordinal);
    }
}
