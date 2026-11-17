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

public class InstantClipTests
{
    private sealed class CountingTranscriber(Transcript t) : ITranscriber
    {
        public int Calls { get; private set; }

        public Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(t);
        }
    }

    [Fact]
    public async Task Ohne_Instant_Clip_erst_ohne_Untertitel_dann_nachgeholt_mit_gleichem_Schnitt()
    {
        Assert.SkipUnless(EffectsTestsHelper.HasFfmpeg(), "ffmpeg nicht installiert");
        var dir = Directory.CreateTempSubdirectory("ucut-instant-");
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
            settings.Whisper.InstantImport = false;
            // Speech starts late: with a transcript the dead start would be cut away.
            var transcriber = new CountingTranscriber(new Transcript([new Word("Wer", 3.0, 3.3), new Word("hat", 3.3, 3.5), new Word("Kredit?", 3.5, 4.0)]));
            using var engine = new UCut.App.UCutEngine(settings, new UCut.App.RunLog(), transcriber: transcriber, writer: new TemplateClipWriter());

            var r = await engine.ProcessFilesAsync([input], "GTA V", null, TestContext.Current.CancellationToken);
            Assert.Equal(1, r.Done);
            Assert.Equal(0, transcriber.Calls);
            var clip = Assert.Single(UCut.Library.ClipLibrary.Load(settings.OutputFolder));
            Assert.True(clip.Metadata.SubtitlesPending);
            Assert.Empty(clip.Metadata.Edit!.Words);
            Assert.Equal(0, clip.Metadata.Edit.CutStart);
            var title = clip.Metadata.YouTube.Title;

            var s = await engine.AddSubtitlesAsync([clip], null, TestContext.Current.CancellationToken);
            Assert.Equal(1, s.Done);
            Assert.Equal(1, transcriber.Calls);
            var after = Assert.Single(UCut.Library.ClipLibrary.Load(settings.OutputFolder));
            Assert.False(after.Metadata.SubtitlesPending);
            Assert.Equal(3, after.Metadata.Edit!.Words.Count);
            Assert.Equal(0, after.Metadata.Edit.CutStart); // same cut as before
            Assert.Equal(clip.VideoPath, after.VideoPath);
            Assert.Equal(title, after.Metadata.YouTube.Title);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class MissedAitumClipTests
{
    [Fact]
    public void Nur_Clips_seit_dem_Start_ohne_fertigen_Clip_werden_nachgeholt()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-missed-").FullName;
        try
        {
            var watch = Path.Combine(dir, "aitum");
            Directory.CreateDirectory(watch);
            var old = Path.Combine(watch, "alt.mp4");
            var made = Path.Combine(watch, "schon-fertig.mp4");
            var missed2 = Path.Combine(watch, "verpasst-2.mp4");
            var missed1 = Path.Combine(watch, "verpasst-1.mp4");
            foreach (var f in new[] { old, made, missed1, missed2 })
            {
                File.WriteAllText(f, "x");
            }

            File.WriteAllText(Path.Combine(watch, "notiz.txt"), "x");
            var since = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(old, since.AddDays(-3));
            File.SetLastWriteTimeUtc(made, since.AddHours(1));
            File.SetLastWriteTimeUtc(missed1, since.AddHours(2));
            File.SetLastWriteTimeUtc(missed2, since.AddHours(3));

            var settings = new UCutSettings { WatchFolder = watch, OutputFolder = Path.Combine(dir, "out"), WatchSince = since };
            var gameDir = Path.Combine(settings.OutputFolder, "GTA V");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "clip.mp4"), "x");
            new ClipMetadata { Video = "clip.mp4", SourceFile = made, Game = "GTA V" }.Save(Path.Combine(gameDir, "clip.json"));

            using var engine = new UCut.App.UCutEngine(settings, new UCut.App.RunLog(), transcriber: new NoTranscriber(), writer: new TemplateClipWriter());
            Assert.Equal([Path.GetFullPath(missed1), Path.GetFullPath(missed2)], engine.MissedAitumClips());

            settings.WatchSince = null;
            Assert.Empty(engine.MissedAitumClips());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class NoTranscriber : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default) => Task.FromResult(new Transcript([]));
    }
}

public class RemuxTwinTests
{
    [Fact]
    public async Task Von_MKV_und_MP4_mit_gleichem_Namen_zaehlt_nur_die_MP4()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-remux-").FullName;
        try
        {
            var mkv = Path.Combine(dir, "Replay 1.mkv");
            var mp4 = Path.Combine(dir, "Replay 1.mp4");
            var alone = Path.Combine(dir, "Replay 2.mkv");
            foreach (var f in new[] { mkv, mp4, alone })
            {
                File.WriteAllText(f, "x");
            }

            Assert.True(ClipImport.HasMp4Twin(mkv));
            Assert.False(ClipImport.HasMp4Twin(mp4));
            Assert.False(ClipImport.HasMp4Twin(alone));
            Assert.Equal(ClipImport.ClipKey(mkv), ClipImport.ClipKey(mp4));
            Assert.Equal([mp4, alone], ClipImport.Collect([dir], out _));

            // A fresh MKV: the MP4 shows up a moment later.
            var late = Path.Combine(dir, "Replay 3.mkv");
            File.WriteAllText(late, "x");
            var wait = ClipImport.WaitForMp4TwinAsync(late, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            File.WriteAllText(Path.ChangeExtension(late, ".mp4"), "x");
            Assert.True(await wait);
            Assert.False(await ClipImport.WaitForMp4TwinAsync(alone, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Download_Kopie_heisst_wie_der_Titel_und_ueberschreibt_nichts()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-dl-").FullName;
        try
        {
            var video = Path.Combine(dir, "2026-01-01_clip.mp4");
            File.WriteAllText(video, "x");
            var target = Path.Combine(dir, "Downloads");
            var first = UCut.Upload.UploadCopy.SaveCopy(video, "Wer hat Kredit", target);
            var second = UCut.Upload.UploadCopy.SaveCopy(video, "Wer hat Kredit", target);
            Assert.Equal("Wer hat Kredit.mp4", Path.GetFileName(first));
            Assert.Equal("Wer hat Kredit (2).mp4", Path.GetFileName(second));
            Assert.True(File.Exists(first) && File.Exists(second) && File.Exists(video));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Galerie_liest_unveraenderte_Infodateien_nicht_neu()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-cache-").FullName;
        try
        {
            var game = Path.Combine(dir, "GTA V");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "a.mp4"), "x");
            new ClipMetadata { Video = "a.mp4", Game = "GTA V" }.Save(Path.Combine(game, "a.json"));

            var first = Assert.Single(UCut.Library.ClipLibrary.Load(dir));
            Assert.Same(first, Assert.Single(UCut.Library.ClipLibrary.Load(dir)));

            // Saved by UCut itself: still the same object, with the new text.
            first.Metadata.YouTube.Title = "Neu";
            first.Save();
            Assert.Same(first, Assert.Single(UCut.Library.ClipLibrary.Load(dir)));

            // Written by someone else (a clip made again): read anew.
            var other = new ClipMetadata { Video = "a.mp4", Game = "GTA V" };
            other.YouTube.Title = "Von außen geändert";
            other.Save(Path.Combine(game, "a.json"));
            var again = Assert.Single(UCut.Library.ClipLibrary.Load(dir));
            Assert.NotSame(first, again);
            Assert.Equal("Von außen geändert", again.Metadata.YouTube.Title);

            UCut.Library.ClipLibrary.Delete(again);
            Assert.Empty(UCut.Library.ClipLibrary.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class UnloadTests
{
    [Fact]
    public void Entladen_nimmt_den_Clip_raus_ohne_zu_loeschen_und_zurueckholen_bringt_ihn_wieder()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-unload-").FullName;
        try
        {
            var game = Path.Combine(dir, "GTA V");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "a.mp4"), "video");
            File.WriteAllText(Path.Combine(game, "a.thumb.jpg"), "bild");
            new ClipMetadata { Video = "a.mp4", Game = "GTA V", SourceFile = Path.Combine(dir, "aitum", "Replay.mp4") }.Save(Path.Combine(game, "a.json"));

            var clip = Assert.Single(UCut.Library.ClipLibrary.Load(dir));
            UCut.Library.ClipLibrary.Unload(clip, dir);
            Assert.Empty(UCut.Library.ClipLibrary.Load(dir));
            var unloaded = Assert.Single(UCut.Library.ClipLibrary.LoadUnloaded(dir));
            Assert.Equal(Path.Combine(dir, "_Entladen", "GTA V", "a.mp4"), unloaded.VideoPath);
            Assert.True(File.Exists(Path.Combine(dir, "_Entladen", "GTA V", "a.thumb.jpg")));

            // The Aitum catch-up must not make an unloaded clip again.
            var settings = new UCutSettings { OutputFolder = dir, WatchFolder = Path.Combine(dir, "aitum"), WatchSince = DateTimeOffset.Now.AddDays(-1) };
            Directory.CreateDirectory(settings.WatchFolder);
            File.WriteAllText(Path.Combine(settings.WatchFolder, "Replay.mp4"), "x");
            using (var engine = new UCut.App.UCutEngine(settings, new UCut.App.RunLog(), transcriber: new NoTranscriber(), writer: new TemplateClipWriter()))
            {
                Assert.Empty(engine.MissedAitumClips());
            }

            UCut.Library.ClipLibrary.Reload(unloaded, dir);
            var back = Assert.Single(UCut.Library.ClipLibrary.Load(dir));
            Assert.Equal(Path.Combine(game, "a.mp4"), back.VideoPath);
            Assert.Equal("video", File.ReadAllText(back.VideoPath));
            Assert.Empty(UCut.Library.ClipLibrary.LoadUnloaded(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class NoTranscriber : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default) => Task.FromResult(new Transcript([]));
    }
}
