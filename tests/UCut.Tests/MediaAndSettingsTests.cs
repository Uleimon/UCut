using UCut.Media;
using UCut.Settings;

namespace UCut.Tests;

public class MediaAndSettingsTests
{
    [Fact]
    public void Probe_Ausgabe_wird_gelesen()
    {
        var info = Ffmpeg.ParseProbe("""
            {"streams":[{"codec_type":"video","width":910,"height":1620},{"codec_type":"audio"},{"codec_type":"audio","tags":{"handler_name":"SoundHandler"}},{"codec_type":"audio","tags":{"title":"Mikro"}}],"format":{"duration":"12.005011"}}
            """);
        Assert.Equal((910, 1620, 12.005011, true), (info.Width, info.Height, info.DurationSeconds, info.HasAudio));
        Assert.Equal(["Audio 1", "Audio 2", "Mikro"], info.AudioTracks);
    }

    [Fact]
    public void Lautheit_wird_gelesen()
    {
        var p = Ffmpeg.ParseEbur128("""
            [Parsed_ebur128_0 @ 0x1] t: 0.1        TARGET:-23 LUFS    M: -120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU
            [Parsed_ebur128_0 @ 0x1] t: 0.599977   TARGET:-23 LUFS    M: -33.7 S:-120.7     I: -33.7 LUFS       LRA:   0.0 LU
            """);
        Assert.Equal(2, p.Points.Count);
        Assert.Equal(-33.7, p.Points[1].Lufs, 3);
    }

    [Fact]
    public void Einstellungen_speichern_und_laden_mit_automatischer_Logo_Hoehe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ucut-settings-{Guid.NewGuid():N}.json");
        try
        {
            SettingsStore.Save(new UCutSettings { WatchFolder = "C:\\Aitum" }, path);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("C:\\Aitum", loaded.WatchFolder);
            Assert.Equal("GTA V", loaded.Games.Processes["gta5"]); // case-insensitive after loading

            File.WriteAllText(path, """{ "logo": { "centerY": null, "cover": null } }""");
            loaded = SettingsStore.Load(path);
            Assert.Null(loaded.Logo.CenterY);
            Assert.Null(loaded.Logo.Cover);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fehlende_Einstellungen_geben_Hinweis_auf_init() =>
        Assert.Contains("ucut init", Assert.Throws<FileNotFoundException>(() => SettingsStore.Load("/gibt/es/nicht.json")).Message, StringComparison.Ordinal);
}
