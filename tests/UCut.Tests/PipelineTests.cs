using UCut.Analysis;
using UCut.Games;
using UCut.Media;
using UCut.Metadata;
using UCut.Pipeline;
using UCut.Settings;
using UCut.Transcription;

namespace UCut.Tests;

/// <summary>End-to-end with real ffmpeg on a generated test video; the transcriber is faked.</summary>
public class PipelineTests
{
    private sealed class FakeTranscriber(Transcript t) : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default) => Task.FromResult(t);
    }

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
    public async Task Clip_wird_geschnitten_untertitelt_und_mit_Infodatei_abgelegt()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg nicht installiert");
        var dir = Directory.CreateTempSubdirectory("ucut-e2e-");
        try
        {
            var input = Path.Combine(dir.FullName, "replay.mp4");
            await ProcessRunner.RunCheckedAsync("ffmpeg",
            [
                "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=540x960:rate=30:duration=6",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input,
            ]);

            var settings = new UCutSettings { OutputFolder = Path.Combine(dir.FullName, "out") };
            settings.Logo.Path = Path.Combine(AppContext.BaseDirectory, "assets", "uleimon-logo.png");
            settings.Captions.Font = "DejaVu Sans";
            var transcript = new Transcript([new Word("Wer", 2.0, 2.3), new Word("hat", 2.3, 2.5), new Word("Kredit?", 2.5, 3.0)]);
            var logs = new List<string>();
            var pipeline = new ClipPipeline(settings, new Ffmpeg("ffmpeg", "ffprobe"), new FakeTranscriber(transcript), new TemplateClipWriter(), logs.Add);

            var result = await pipeline.ProcessAsync(input, new GameResult("GTA V", "Grand Theft Auto V", "Test"));

            Assert.True(File.Exists(result.VideoPath));
            Assert.Equal(Path.Combine(settings.OutputFolder, "GTA V"), Path.GetDirectoryName(result.VideoPath));
            Assert.EndsWith("_wer-hat-kredit.mp4", result.VideoPath, StringComparison.Ordinal);
            var info = await new Ffmpeg("ffmpeg", "ffprobe").ProbeAsync(result.VideoPath);
            Assert.Equal((540, 960), (info.Width, info.Height));
            Assert.InRange(info.DurationSeconds, 4.2, 4.6); // 1.6 s dead start removed (2.0 - 0.4 lead-in)

            var meta = ClipMetadata.Load(result.MetadataPath);
            Assert.Equal("GTA V", meta.Game);
            Assert.Equal("Wer hat Kredit?", meta.Transcript);
            Assert.Contains("#gta5", meta.TikTok.Caption, StringComparison.Ordinal);
            Assert.Equal(25, meta.Score.Parts.Single(p => p.Name == "Hook").Points);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("crop")]
    [InlineData("blur")]
    public async Task Querformat_Clip_wird_ins_Hochformat_gebracht(string mode)
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg nicht installiert");
        var dir = Directory.CreateTempSubdirectory("ucut-e2e-");
        try
        {
            var input = Path.Combine(dir.FullName, "quer.mp4");
            await ProcessRunner.RunCheckedAsync("ffmpeg",
            [
                "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=3",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input,
            ]);

            var settings = new UCutSettings { OutputFolder = Path.Combine(dir.FullName, "out") };
            settings.Logo.Path = Path.Combine(AppContext.BaseDirectory, "assets", "uleimon-logo.png");
            settings.Captions.Font = "DejaVu Sans";
            settings.Reframe.Mode = mode == "crop" ? UCut.Editing.ReframeMode.Crop : UCut.Editing.ReframeMode.Blur;
            var pipeline = new ClipPipeline(settings, new Ffmpeg("ffmpeg", "ffprobe"),
                new FakeTranscriber(new Transcript([new Word("Hallo", 0.2, 0.8)])), new TemplateClipWriter(), _ => { });

            var result = await pipeline.ProcessAsync(input, new GameResult("Unsortiert", null, "Test"));

            var info = await new Ffmpeg("ffmpeg", "ffprobe").ProbeAsync(result.VideoPath);
            Assert.Equal((1080, 1920), (info.Width, info.Height));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
