using UCut.CutRoom;
using UCut.Media;

namespace UCut.Tests;

public class EffectsTests
{
    private static bool HasFfmpeg()
    {
        try
        {
            return ProcessRunner.RunAsync("ffmpeg", ["-version"]).Result.ExitCode == 0;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    [Fact]
    public void Text_Effekte_werden_eine_ASS_Datei_mit_Animation()
    {
        var ass = EffectFilters.TextAss([new EffectItem { Kind = EffectKind.Text, Text = "BRUH", Start = 1.5, Duration = 1, Size = 0.1, Y = 0.3 }], 1080, 1920, "Arial");
        Assert.NotNull(ass);
        Assert.Contains("Dialogue: 1,0:00:01.50,0:00:02.50,Fx,", ass, StringComparison.Ordinal);
        Assert.Contains("\\pos(540,576)", ass, StringComparison.Ordinal);
        Assert.Contains("\\fs192}BRUH", ass, StringComparison.Ordinal);
        Assert.Null(EffectFilters.TextAss([new EffectItem { Kind = EffectKind.Zoom }], 1080, 1920, "Arial"));
    }

    [Fact]
    public void Meme_Dateien_kommen_als_Eingaenge_in_der_richtigen_Reihenfolge()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-fx-").FullName;
        var png = Path.Combine(dir, "a.png");
        var mp3 = Path.Combine(dir, "b.mp3");
        File.WriteAllBytes(png, [1]);
        File.WriteAllBytes(mp3, [1]);
        var fx = new List<EffectItem>
        {
            new() { Kind = EffectKind.Zoom },
            new() { Kind = EffectKind.Meme, Path = png, Duration = 2 },
            new() { Kind = EffectKind.Meme, Path = Path.Combine(dir, "fehlt.gif") },
            new() { Kind = EffectKind.Sound, Path = mp3 },
        };
        Assert.Equal(["-loop", "1", "-t", "2", "-i", png, "-i", mp3], EffectFilters.Inputs(fx));
        var (graph, labels) = EffectFilters.Audio(fx, 3);
        Assert.Equal(["[sfx1]"], labels);
        Assert.Contains("[4:a]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_mit_Meme_Sound_Text_Zoom_Wackeln_und_Blitz()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg nicht installiert");
        var dir = Directory.CreateTempSubdirectory("ucut-fx-");
        try
        {
            string P(string n) => Path.Combine(dir.FullName, n);
            await ProcessRunner.RunCheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:size=540x960:rate=30:duration=5",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", P("src.mp4")]);
            await ProcessRunner.RunCheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:size=200x200:d=1", "-frames:v", "1", P("sticker.png")]);
            await ProcessRunner.RunCheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x160:rate=10:duration=1", P("tanz.gif")]);
            await ProcessRunner.RunCheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x00FF00:size=320x240:rate=30:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=300:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", P("greenscreen.mp4")]);
            await ProcessRunner.RunCheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:duration=0.5", P("boom.mp3")]);

            var ff = new Ffmpeg("ffmpeg", "ffprobe");
            var p = EditProject.FromSource(P("src.mp4"), await ff.ProbeAsync(P("src.mp4")));
            p.Effects =
            [
                new() { Kind = EffectKind.Meme, Path = P("sticker.png"), Start = 0.5, Duration = 1.5, X = 0.5, Y = 0.25, Size = 0.4, Animation = EffectAnimation.Pop },
                new() { Kind = EffectKind.Meme, Path = P("tanz.gif"), Start = 2.0, Duration = 2.5, X = 0.25, Y = 0.75, Size = 0.3 },
                new() { Kind = EffectKind.Meme, Path = P("greenscreen.mp4"), Start = 3.0, Duration = 1.5, RemoveGreen = true, HasAudio = true, Animation = EffectAnimation.SlideUp },
                new() { Kind = EffectKind.Sound, Path = P("boom.mp3"), Start = 1.0, Duration = 0.5 },
                new() { Kind = EffectKind.Text, Text = "BRUH", Start = 1.0, Duration = 1.0, Size = 0.08 },
                new() { Kind = EffectKind.Zoom, Start = 2.0, Duration = 1.0, Size = 0.3 },
                new() { Kind = EffectKind.Shake, Start = 3.5, Duration = 0.5 },
                new() { Kind = EffectKind.Flash, Start = 4.2, Duration = 0.3 },
            ];

            var mp4 = P("out.mp4");
            await EditRenderer.RenderAsync("ffmpeg", p, new ExportOptions(Quality: ExportQuality.Small, TextFont: "DejaVu Sans"), mp4);
            var info = await ff.ProbeAsync(mp4);
            Assert.Equal((540, 960), (info.Width, info.Height));
            Assert.InRange(info.DurationSeconds, 4.8, 5.3);
            Assert.True(info.HasAudio);

            // At 1.2 s the red sticker sits in the upper middle of the blue picture.
            var (px, w, _) = await ff.GrabFrameAsync(mp4, 1.6, 54, 96, "rgb24");
            var i = (24 * w + 27) * 3;
            Assert.True(px[i] > 180 && px[i + 2] < 90, $"kein Sticker: {px[i]},{px[i + 1]},{px[i + 2]}");

            // Green screen removed: at 3.8 s the middle is blue again, not green.
            (px, w, _) = await ff.GrabFrameAsync(mp4, 3.8, 54, 96, "rgb24");
            i = (36 * w + 27) * 3;
            Assert.True(px[i + 1] < 150, $"Grün nicht entfernt: {px[i]},{px[i + 1]},{px[i + 2]}");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
