using UCut.Analysis;
using UCut.Captions;
using UCut.Editing;
using UCut.Games;
using UCut.Media;
using UCut.Metadata;
using UCut.Settings;
using UCut.Transcription;

namespace UCut.Pipeline;

public sealed record ClipResult(string VideoPath, string MetadataPath, ClipMetadata Metadata);

/// <summary>Where a clip came from when it was not recorded just now (e.g. an old Twitch clip).</summary>
public sealed record ClipOrigin(DateTimeOffset RecordedAt, string Platform, string Id, string Url, string Title, int Views);

/// <summary>One clip from start to finish: transcript, cut, captions, logo, score, texts, info file.</summary>
public sealed class ClipPipeline(
    UCutSettings settings,
    Ffmpeg ffmpeg,
    ITranscriber transcriber,
    IClipWriter writer,
    Action<string> log,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<ClipResult> ProcessAsync(string sourceFile, GameResult game, ClipOrigin? origin = null, CancellationToken ct = default)
    {
        var work = Directory.CreateTempSubdirectory("ucut-");
        try
        {
            log($"Clip: {Path.GetFileName(sourceFile)} (Spiel: {game.FolderName}, erkannt über {game.Source})");
            var info = await ffmpeg.ProbeAsync(sourceFile, ct).ConfigureAwait(false);

            var transcript = new Transcript([]);
            if (info.HasAudio)
            {
                var wav = Path.Combine(work.FullName, "audio.wav");
                await ffmpeg.ExtractWhisperAudioAsync(sourceFile, wav, ct).ConfigureAwait(false);
                log("  Transkript wird erstellt ...");
                transcript = await transcriber.TranscribeAsync(wav, ct).ConfigureAwait(false);
                log($"  Transkript: {(transcript.Words.Count == 0 ? "(keine Sprache)" : transcript.Text)}");
            }

            var cut = CutPlanner.Plan(info.DurationSeconds, transcript, settings.Cut);
            if (cut.Reason is not null)
            {
                log($"  Schnitt: {cut.Reason}");
            }

            var clipTranscript = transcript.Shift(-cut.Start);
            clipTranscript = new Transcript(clipTranscript.Words.Where(w => w.Start < cut.Duration).ToList());

            NormRect? cam = null;
            if (Reframer.NeedsReframe(info.Width, info.Height, settings.Reframe) && settings.Reframe.Mode == ReframeMode.Split)
            {
                cam = settings.Reframe.CamRect ?? await FindCamAsync(sourceFile, cut, ct).ConfigureAwait(false);
            }

            var reframe = Reframer.Plan(info.Width, info.Height, settings.Reframe, cam);
            var (outWidth, outHeight) = reframe is null ? (info.Width, info.Height) : (reframe.TargetWidth, reframe.TargetHeight);
            if (reframe is not null)
            {
                log(reframe.Mode switch
                {
                    ReframeMode.Split => $"  Querformat ({info.Width}x{info.Height}): Cam {(settings.Reframe.CamRect is null ? "gefunden" : "aus den Einstellungen")}, kommt nach oben, Spiel darunter",
                    ReframeMode.Crop => $"  Querformat ({info.Width}x{info.Height}): wird auf 9:16 zugeschnitten",
                    _ => $"  Querformat ({info.Width}x{info.Height}): wird mit unscharfem Hintergrund ins Hochformat gesetzt",
                });
            }

            var logoPath = ResolveLogo();
            var layout = await CheckLayoutAsync(sourceFile, cut, reframe, logoPath, outWidth, outHeight, ct).ConfigureAwait(false);
            foreach (var note in layout.Notes)
            {
                log($"  Layout: {note}");
            }

            string? assName = null;
            var withCaptions = settings.Captions.Enabled && clipTranscript.Words.Count > 0;
            if (withCaptions)
            {
                assName = "captions.ass";
                var ass = new CaptionBuilder(settings.Captions).BuildAss(clipTranscript, outWidth, outHeight, layout.CaptionY);
                await File.WriteAllTextAsync(Path.Combine(work.FullName, assName), ass, ct).ConfigureAwait(false);
            }

            // Text first: the title goes into the file name.
            var context = new ClipContext(
                clipTranscript.Text, game.FolderName, cut.Duration,
                settings.Games.Hashtags.TryGetValue(game.FolderName, out var gameTags) ? gameTags : [],
                settings.Games.CommonHashtags, settings.Writer.ChannelName);
            var text = await WriteTextAsync(context, ct).ConfigureAwait(false);

            var now = _time.GetLocalNow();
            // Old clips are named after the day they were recorded, so the game folder stays in order.
            var output = OutputNaming.ClipPath(settings.OutputFolder, game.FolderName, origin?.RecordedAt.ToLocalTime() ?? now, text.Title);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var tmpOut = Path.Combine(work.FullName, "out.mp4");

            var job = new RenderJob(Path.GetFullPath(sourceFile), tmpOut, cut, outWidth, outHeight, assName, logoPath, settings.Logo, layout.LogoCenterY, reframe, layout.UseCover);
            log("  Video wird gerendert ...");
            await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath, RenderCommand.Build(job), work.FullName, ct).ConfigureAwait(false);
            File.Move(tmpOut, output);

            var loudness = await ffmpeg.MeasureLoudnessAsync(output, ct).ConfigureAwait(false);
            var score = ClipScorer.Score(cut.Duration, clipTranscript, loudness, withCaptions);

            var meta = ClipMetadata.Create(output, Path.GetFullPath(sourceFile), game.FolderName, game.Source, cut.Duration, cut.Reason,
                                           clipTranscript.Text, score, text, now);
            if (origin is not null)
            {
                meta.Origin = new OriginInfo
                {
                    Platform = origin.Platform, Id = origin.Id, Url = origin.Url, Title = origin.Title, Views = origin.Views, RecordedAt = origin.RecordedAt,
                };
            }

            meta.Layout = new LayoutInfo { Status = layout.Status, CamEdge = layout.CamEdge, Notes = [.. layout.Notes] };
            var metaPath = Path.ChangeExtension(output, ".json");
            meta.Save(metaPath);

            var fun = text.Funniness is { } f ? $", Lustigkeit {f}/10" : "";
            log($"  Fertig: {output} (Score {score.Total}/100{fun})");
            return new ClipResult(output, metaPath, meta);
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private async Task<ClipText> WriteTextAsync(ClipContext context, CancellationToken ct)
    {
        try
        {
            return await writer.WriteAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && writer is not TemplateClipWriter)
        {
            log($"  Claude nicht erreichbar ({ex.Message}), nutze Vorlage für Titel und Hashtags");
            return await new TemplateClipWriter().WriteAsync(context, ct).ConfigureAwait(false);
        }
    }

    private string? ResolveLogo()
    {
        if (!settings.Logo.Enabled)
        {
            return null;
        }

        var path = string.IsNullOrWhiteSpace(settings.Logo.Path)
            ? Path.Combine(AppContext.BaseDirectory, "assets", "uleimon-logo.png")
            : settings.Logo.Path;
        if (!File.Exists(path))
        {
            log($"  Logo nicht gefunden: {path} (Clip wird ohne Logo gerendert)");
            return null;
        }

        return Path.GetFullPath(path);
    }

    private async Task<NormRect?> FindCamAsync(string video, CutPlan cut, CancellationToken ct)
    {
        const int w = 320, h = 180, n = 8;
        var frames = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            var (px, _, _) = await ffmpeg.GrabFrameAsync(video, cut.Start + cut.Duration * (i + 0.5) / n, w, h, "rgb24", ct).ConfigureAwait(false);
            frames.Add(px);
        }

        return CamFinder.Find(frames, w, h);
    }

    private async Task<LayoutDecision> CheckLayoutAsync(string video, CutPlan cut, ReframePlan? reframe, string? logoPath,
                                                         int outWidth, int outHeight, CancellationToken ct)
    {
        var logoHeight = 0.05;
        if (logoPath is not null)
        {
            var (lw, lh) = LogoPlacement.ReadPngSize(logoPath);
            logoHeight = settings.Logo.WidthFraction * outWidth * lh / lw / outHeight;
        }

        var edges = new List<double?>();
        if (reframe is null)
        {
            // Look at three moments of the clip; the cam frame stays put while the game picture moves.
            foreach (var f in new[] { 0.25, 0.5, 0.75 })
            {
                var (px, w, h) = await ffmpeg.GrabGrayFrameAsync(video, cut.Start + cut.Duration * f, 90, 160, ct).ConfigureAwait(false);
                edges.Add(CamEdgeDetector.FindEdge(px, w, h));
            }
        }

        return LayoutChecker.Decide(edges, reframe, settings.Logo, settings.Captions, logoHeight);
    }
}
