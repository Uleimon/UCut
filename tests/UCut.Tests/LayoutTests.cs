using UCut.Editing;
using UCut.Settings;

namespace UCut.Tests;

public class LayoutTests
{
    private const double LogoHeight = 0.05;

    [Fact]
    public void Dein_Layout_passt_Logo_und_Abdeckung_wie_eingestellt()
    {
        var d = LayoutChecker.Decide([0.331, 0.331, 0.338], false, ReframeMode.Crop, new LogoSettings(), new CaptionSettings(), LogoHeight);
        Assert.Equal("passt", d.Status);
        Assert.True(d.UseCover);
        Assert.Equal(0.312, d.LogoCenterY, 3);
        Assert.Equal(0.62, d.CaptionY, 3);
    }

    [Fact]
    public void Anderes_Layout_Logo_wandert_an_die_Cam_Kante_und_nichts_wird_uebermalt()
    {
        var d = LayoutChecker.Decide([0.44, 0.44, null], false, ReframeMode.Crop, new LogoSettings(), new CaptionSettings(), LogoHeight);
        Assert.Equal("angepasst", d.Status);
        Assert.False(d.UseCover);
        Assert.Equal(0.44 - LogoHeight * 0.55, d.LogoCenterY, 3);
    }

    [Fact]
    public void Untertitel_rutschen_unter_eine_grosse_Cam()
    {
        var d = LayoutChecker.Decide([0.55, 0.55, 0.55], false, ReframeMode.Crop, new LogoSettings(), new CaptionSettings(), LogoHeight);
        Assert.Equal(0.67, d.CaptionY, 3);
        Assert.Contains(d.Notes, n => n.Contains("Untertitel", StringComparison.Ordinal));
    }

    [Fact]
    public void Ohne_Cam_wird_nichts_uebermalt()
    {
        var d = LayoutChecker.Decide([null, 0.4, 0.2], false, ReframeMode.Crop, new LogoSettings(), new CaptionSettings(), LogoHeight);
        Assert.Equal("keine Cam erkannt", d.Status);
        Assert.False(d.UseCover);
        Assert.Null(d.CamEdge);
    }

    [Fact]
    public void Querformat_bekommt_keine_Abdeckung_und_einen_Hinweis_zur_Cam()
    {
        var d = LayoutChecker.Decide([], true, ReframeMode.Crop, new LogoSettings(), new CaptionSettings(), LogoHeight);
        Assert.Equal("Querformat umgewandelt", d.Status);
        Assert.False(d.UseCover);
        Assert.Contains(d.Notes, n => n.Contains("blur", StringComparison.Ordinal));
    }

    [Fact]
    public void Automatische_Position_ohne_feste_Einstellung()
    {
        var d = LayoutChecker.Decide([0.331, 0.331, 0.331], false, ReframeMode.Crop, new LogoSettings { CenterY = null }, new CaptionSettings(), LogoHeight);
        Assert.Equal("automatisch", d.Status);
        Assert.False(d.UseCover);
    }

    [Theory]
    [InlineData(new double[] { 0.33, 0.335, 0.6 }, 0.333)]
    [InlineData(new double[] { 0.2, 0.4, 0.6 }, null)]
    public void Kante_muss_in_mehreren_Bildern_gleich_sein(double[] edges, double? expected)
    {
        var e = LayoutChecker.ConsistentEdge(edges.Select(x => (double?)x).ToList());
        if (expected is null)
        {
            Assert.Null(e);
        }
        else
        {
            Assert.Equal(expected.Value, e!.Value, 2);
        }
    }

    [Fact]
    public void Renderbefehl_ohne_Abdeckung_wenn_Layout_anders()
    {
        var job = new RenderJob("in.mp4", "out.mp4", new CutPlan(0, 5, null), 910, 1620, null, "logo.png", new LogoSettings(), 0.4, null, UseCover: false);
        var args = RenderCommand.Build(job);
        Assert.DoesNotContain("drawbox", args[args.IndexOf("-filter_complex") + 1], StringComparison.Ordinal);
    }
}
