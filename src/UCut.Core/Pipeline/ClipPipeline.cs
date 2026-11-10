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

    public async Task<ClipResult> ProcessAsync(string sourceFile, GameResult game, CancellationToken ct = default)
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

            var reframe = Reframer.Plan(info.Width, info.Height, settings.Reframe);
            var (outWidth, outHeight) = reframe is null ? (info.Width, info.Height) : (reframe.TargetWidth, reframe.TargetHeight);
            if (reframe is not null)
            {
                log(reframe.Mode == ReframeMode.Crop
                    ? $"  Querformat ({info.Width}x{info.Height}): wird auf 9:16 zugeschnitten"
                    : $"  Querformat ({info.Width}x{info.Height}): wird mit unscharfem Hintergrund ins Hochformat gesetzt");
            }

            string? assName = null;
            var withCaptions = settings.Captions.Enabled && clipTranscript.Words.Count > 0;
            if (withCaptions)
            {
                assName = "captions.ass";
                var ass = new CaptionBuilder(settings.Captions).BuildAss(clipTranscript, outWidth, outHeight);
                await File.WriteAllTextAsync(Path.Combine(work.FullName, assName), ass, ct).ConfigureAwait(false);
            }

            var logoPath = ResolveLogo();
            double? autoY = null;
            if (logoPath is not null && reframe is null && settings.Logo.CenterY is null)
            {
                var edge = await DetectCamEdgeAsync(sourceFile, cut, ct).ConfigureAwait(false);
                if (edge is { } e)
                {
                    autoY = LogoPlacement.CenterAboveEdge(e, logoPath, settings.Logo.WidthFraction, outWidth, outHeight);
                    log($"  Cam-Kante bei {e:P0} der Höhe, Logo sitzt direkt darüber");
                }
                else
                {
                    log("  Cam-Kante nicht gefunden, Logo bei 1/3 der Höhe");
                }
            }

            // Text first: the title goes into the file name.
            var context = new ClipContext(
                clipTranscript.Text, game.FolderName, cut.Duration,
                settings.Games.Hashtags.TryGetValue(game.FolderName, out var gameTags) ? gameTags : [],
                settings.Games.CommonHashtags, settings.Writer.ChannelName);
            var text = await WriteTextAsync(context, ct).ConfigureAwait(false);

            var now = _time.GetLocalNow();
            var output = OutputNaming.ClipPath(settings.OutputFolder, game.FolderName, now, text.Title);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var tmpOut = Path.Combine(work.FullName, "out.mp4");

            var job = new RenderJob(Path.GetFullPath(sourceFile), tmpOut, cut, outWidth, outHeight, assName, logoPath, settings.Logo, autoY, reframe);
            log("  Video wird gerendert ...");
            await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath, RenderCommand.Build(job), work.FullName, ct).ConfigureAwait(false);
            File.Move(tmpOut, output);

            var loudness = await ffmpeg.MeasureLoudnessAsync(output, ct).ConfigureAwait(false);
            var score = ClipScorer.Score(cut.Duration, clipTranscript, loudness, withCaptions);

            var meta = ClipMetadata.Create(output, Path.GetFullPath(sourceFile), game.FolderName, game.Source, cut.Duration, cut.Reason,
                                           clipTranscript.Text, score, text, now);
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

    private async Task<double?> DetectCamEdgeAsync(string video, CutPlan cut, CancellationToken ct)
    {
        // Look at three frames; take the edge that is found most often.
        var edges = new List<double>();
        foreach (var f in new[] { 0.25, 0.5, 0.75 })
        {
            var (px, w, h) = await ffmpeg.GrabGrayFrameAsync(video, cut.Start + cut.Duration * f, 90, 160, ct).ConfigureAwait(false);
            if (CamEdgeDetector.FindEdge(px, w, h) is { } e)
            {
                edges.Add(Math.Round(e, 2));
            }
        }

        return edges.Count == 0 ? null : edges.GroupBy(e => e).OrderByDescending(g => g.Count()).First().Key;
    }
}
