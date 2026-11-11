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

            // Preview picture for YouTube next to the clip; a steady tone is no laugh.
            var thumb = Path.ChangeExtension(result.VideoPath, ".youtube.jpg");
            Assert.True(File.Exists(thumb));
            var pic = await new Ffmpeg("ffmpeg", "ffprobe").ProbeAsync(thumb);
            Assert.Equal((540, 960), (pic.Width, pic.Height));
            Assert.Empty(meta.Laughs);
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

            var recorded = new DateTimeOffset(2020, 7, 3, 21, 15, 0, TimeSpan.Zero);
            var origin = new ClipOrigin(recorded, "Twitch", "Clip1", "https://clips.twitch.tv/Clip1", "Alter Clip", 77);
            var result = await pipeline.ProcessAsync(input, new GameResult("Unsortiert", null, "Test"), origin);

            // Old Twitch clips are named after the day they were recorded and keep their link in the info file.
            Assert.StartsWith(recorded.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(result.VideoPath), StringComparison.Ordinal);
            Assert.Equal("https://clips.twitch.tv/Clip1", result.Metadata.Origin?.Url);
            Assert.Equal(77, result.Metadata.Origin?.Views);

            var info = await new Ffmpeg("ffmpeg", "ffprobe").ProbeAsync(result.VideoPath);
            Assert.Equal((1080, 1920), (info.Width, info.Height));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Clip_wird_mit_korrigierten_Woertern_und_Cam_von_Hand_neu_gemacht()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg nicht installiert");
        var dir = Directory.CreateTempSubdirectory("ucut-e2e-");
        try
        {
            var input = Path.Combine(dir.FullName, "quer.mp4");
            await ProcessRunner.RunCheckedAsync("ffmpeg",
            [
                "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input,
            ]);

            var settings = new UCutSettings { OutputFolder = Path.Combine(dir.FullName, "out") };
            settings.Logo.Path = Path.Combine(AppContext.BaseDirectory, "assets", "uleimon-logo.png");
            settings.Captions.Font = "DejaVu Sans";
            settings.Reframe.Mode = UCut.Editing.ReframeMode.Crop;
            var pipeline = new ClipPipeline(settings, new Ffmpeg("ffmpeg", "ffprobe"),
                new FakeTranscriber(new Transcript([new Word("Kreis", 0.2, 0.8), new Word("leise", 0.9, 1.4)])), new TemplateClipWriter(), _ => { });
            var first = await pipeline.ProcessAsync(input, new GameResult("GTA V", null, "Test"));
            Assert.Equal("Crop", first.Metadata.Edit?.Mode);
            Assert.Equal(["Kreis", "leise"], first.Metadata.Edit!.Words.Select(w => w.Text));
            first.Metadata.YouTube.Title = "Von Hand geändert";
            first.Metadata.Uploaded["YouTube: Uleimon"] = "https://youtube.com/shorts/x";

            var cam = new UCut.Editing.NormRect(0.7, 0.65, 0.28, 0.3);
            var words = first.Metadata.Edit.Words.Select(w => new Word(w.Text == "Kreis" ? "Krass" : w.Text, w.Start, w.End)).ToList();
            var redo = await pipeline.ProcessAsync(input, new GameResult("GTA V", null, "Test"), null, new ProcessOptions
            {
                Mode = UCut.Editing.ReframeMode.Split,
                Cam = cam,
                Words = words,
                Cut = new UCut.Editing.CutPlan(first.Metadata.Edit.CutStart, first.Metadata.Edit.CutEnd, null),
                Previous = first.Metadata,
                ReplacePath = first.VideoPath,
            });

            Assert.Equal(first.VideoPath, redo.VideoPath); // same file, replaced
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.VideoPath)!, "*.mp4"));
            Assert.Equal("Krass leise", redo.Metadata.Transcript);
            Assert.Equal("Split", redo.Metadata.Edit?.Mode);
            Assert.Equal(cam, redo.Metadata.Edit?.Cam);
            Assert.Equal("Von Hand geändert", redo.Metadata.YouTube.Title); // texts kept
            Assert.Equal("https://youtube.com/shorts/x", redo.Metadata.Uploaded["YouTube: Uleimon"]);

            // The cam set by hand is remembered for later clips.
            var known = UCut.Editing.CamMemory.ForOutput(settings.OutputFolder).Candidates(1280, 720);
            Assert.Equal(cam, known[0]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
