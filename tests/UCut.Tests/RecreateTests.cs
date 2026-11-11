using UCut.App;
using UCut.Editing;
using UCut.Metadata;

namespace UCut.Tests;

public class RecreateTests
{
    [Fact]
    public void Recreate_nimmt_Aufbau_Cam_und_Woerter_von_vorher()
    {
        var meta = new ClipMetadata
        {
            Edit = new EditInfo
            {
                CutStart = 1, CutEnd = 9, Mode = "Split", Cam = new NormRect(0.7, 0.7, 0.25, 0.25),
                Words = [new WordInfo { Text = "Krass", Start = 0.5, End = 0.9 }],
            },
        };
        var o = UCutEngine.SameAsBefore(meta);
        Assert.Equal(ReframeMode.Split, o.Mode);
        Assert.Equal(new NormRect(0.7, 0.7, 0.25, 0.25), o.Cam);
        Assert.Equal("Krass", Assert.Single(o.Words!).Text);
    }

    [Fact]
    public void Recreate_alter_Clip_ohne_Infos_wird_ganz_normal_neu_gemacht()
    {
        var o = UCutEngine.SameAsBefore(new ClipMetadata());
        Assert.Null(o.Mode);
        Assert.Null(o.Cam);
        Assert.Null(o.Words);
    }

    [Fact]
    public void Recreate_Hochformat_Clip_bleibt_ohne_Umbau()
    {
        var o = UCutEngine.SameAsBefore(new ClipMetadata { Edit = new EditInfo { Mode = "", Cam = null } });
        Assert.Null(o.Mode);
        Assert.Null(o.Words);
    }
}
