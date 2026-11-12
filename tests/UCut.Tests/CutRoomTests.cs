using UCut.CutRoom;
using UCut.Media;

namespace UCut.Tests;

public class CutRoomTests
{
    private static EditProject Ten() => EditProject.FromSource("clip.mp4", new VideoInfo(1080, 1920, 10, true) { AudioTracks = ["Spiel", "Mikro"] });

    [Fact]
    public void Teilen_und_Loeschen_veraendert_die_Laenge()
    {
        var p = Ten();
        Assert.Equal(["Spiel", "Mikro"], p.Tracks.Select(t => t.Name));
        Assert.True(p.SplitAt(3));
        Assert.True(p.SplitAt(5)); // timeline 5 = source 5
        Assert.Equal(3, p.Segments.Count);
        Assert.True(p.RemoveSegment(1)); // 3..5 raus
        Assert.Equal(8, p.Duration, 3);
        Assert.Equal((1, 6.0), p.Locate(4));
        Assert.Null(p.ToTimeline(4));
        Assert.Equal(3.0, p.ToTimeline(5)!.Value, 3);
        Assert.False(p.SplitAt(3.05)); // zu nah an der Kante
    }

    [Fact]
    public void Anfang_und_Ende_abschneiden()
    {
        var p = Ten();
        Assert.True(p.TrimStartAt(2));
        Assert.Equal(8, p.Duration, 3);
        Assert.Equal(2, p.Segments[0].In, 3);
        Assert.True(p.TrimEndAt(5));
        Assert.Equal(5, p.Duration, 3);
        Assert.Equal(7, p.Segments[^1].Out, 3);
    }

    [Fact]
    public void Letztes_Stueck_bleibt_und_Rueckgaengig_per_Kopie()
    {
        var p = Ten();
        Assert.False(p.RemoveSegment(0));
        var before = p.Clone();
        p.SplitAt(4);
        Assert.Single(before.Segments);
        Assert.Equal(2, p.Segments.Count);
    }

    [Fact]
    public void Befehl_fuer_Mp4_mit_zwei_Spuren_und_Musik()
    {
        var music = Path.GetTempFileName();
        try
        {
            var p = Ten();
            p.SplitAt(4);
            p.Tracks[1].Volume = 1.5;
            p.Music = new MusicTrack { Path = music, Volume = 0.2, StartAt = 1 };
            var args = EditRenderer.BuildArguments(p, new ExportOptions(Size: ExportSize.Vertical720), "out.mp4");
            var graph = args[args.IndexOf("-filter_complex") + 1];
            Assert.Contains("concat=n=2:v=1:a=0", graph, StringComparison.Ordinal);
            Assert.Contains("[0:a:1]atrim=start=4:end=10", graph, StringComparison.Ordinal);
            Assert.Contains("volume=1.5[a1]", graph, StringComparison.Ordinal);
            Assert.Contains("adelay=1000:all=1", graph, StringComparison.Ordinal);
            Assert.Contains("amix=inputs=3", graph, StringComparison.Ordinal);
            Assert.Contains("scale=720:1280:force_original_aspect_ratio=decrease", graph, StringComparison.Ordinal);
            Assert.Contains("libx264", args);
            Assert.Contains("-stream_loop", args);

            p.Tracks[0].Muted = true;
            p.Music.Muted = true;
            graph = string.Join(" ", EditRenderer.BuildArguments(p, new ExportOptions(ExportFormat.Mp3), "out.mp3"));
            Assert.DoesNotContain("[0:v]", graph, StringComparison.Ordinal);
            Assert.DoesNotContain("[0:a:0]", graph, StringComparison.Ordinal);
            Assert.Contains("libmp3lame", graph, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(music);
        }
    }

    [Fact]
    public void Mp3_ohne_Ton_gibt_klare_Meldung()
    {
        var p = Ten();
        p.Tracks.ForEach(t => t.Muted = true);
        var ex = Assert.Throws<InvalidOperationException>(() => EditRenderer.BuildArguments(p, new ExportOptions(ExportFormat.Mp3), "x.mp3"));
        Assert.Contains("Tonspur", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_rendert_wirklich_mit_ffmpeg()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-cut-");
        try
        {
            var src = Path.Combine(dir.FullName, "src.mp4");
            var mus = Path.Combine(dir.FullName, "musik.mp3");
            // 6 s video with two audio tracks (like OBS: game + mic).
            await ProcessRunner.RunCheckedAsync("ffmpeg", [
                "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=6",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-f", "lavfi", "-i", "sine=frequency=880:duration=6",
                "-map", "0", "-map", "1", "-map", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
                "-metadata:s:a:0", "handler_name=Spiel", "-metadata:s:a:1", "handler_name=Mikro", src]);
            await ProcessRunner.RunCheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=220:duration=2", mus]);

            var ff = new Ffmpeg("ffmpeg", "ffprobe");
            var info = await ff.ProbeAsync(src);
            Assert.Equal(["Spiel", "Mikro"], info.AudioTracks);

            var p = EditProject.FromSource(src, info);
            p.SplitAt(2);
            p.SplitAt(3);
            p.RemoveSegment(1); // 1 s weniger
            p.Tracks[1].Muted = true;
            p.Music = new MusicTrack { Path = mus, Volume = 0.3, Loop = true };
            p.FadeOut = 0.5;

            var progress = new List<double>();
            var mp4 = Path.Combine(dir.FullName, "out.mp4");
            await EditRenderer.RenderAsync("ffmpeg", p, new ExportOptions(Size: ExportSize.Vertical720, Quality: ExportQuality.Small), mp4, new Progress<double>(progress.Add));
            var outInfo = await ff.ProbeAsync(mp4);
            Assert.Equal((720, 1280), (outInfo.Width, outInfo.Height));
            Assert.InRange(outInfo.DurationSeconds, 4.8, 5.3);
            Assert.True(outInfo.HasAudio);

            var mp3 = Path.Combine(dir.FullName, "out.mp3");
            await EditRenderer.RenderAsync("ffmpeg", p, new ExportOptions(ExportFormat.Mp3), mp3);
            var mp3Info = await ff.ProbeAsync(mp3);
            Assert.Equal(0, mp3Info.Width);
            Assert.InRange(mp3Info.DurationSeconds, 4.8, 5.3);

            Assert.NotNull(await TimelineImages.FilmstripAsync("ffmpeg", src, 6, Path.Combine(dir.FullName, "strip.jpg")));
            Assert.NotNull(await TimelineImages.WaveformAsync("ffmpeg", src, 1, Path.Combine(dir.FullName, "wave.png")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
