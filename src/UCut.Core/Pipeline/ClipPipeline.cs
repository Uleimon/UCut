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

/// <summary>
/// Redo of a finished clip: keeps the cut, uses corrected words instead of a new transcript,
/// forces the layout or a cam position set by hand, and replaces the existing file.
/// </summary>
public sealed record ProcessOptions
{
    /// <summary>Split (cam on top), Crop (no cam, fills the screen) or Blur. Null = as in the settings.</summary>
    public ReframeMode? Mode { get; init; }

    /// <summary>Cam position set by hand (fractions of the source picture).</summary>
    public NormRect? Cam { get; init; }

    /// <summary>Corrected words in clip time (after the cut). Null = transcribe again.</summary>
    public IReadOnlyList<Word>? Words { get; init; }

    /// <summary>Keep this cut (source seconds) instead of planning again.</summary>
    public CutPlan? Cut { get; init; }

    /// <summary>Info file of the clip being redone: its texts, uploads and origin are kept.</summary>
    public ClipMetadata? Previous { get; init; }

    /// <summary>Existing clip file to replace (same name and folder).</summary>
    public string? ReplacePath { get; init; }
}

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

    /// <summary>Trending hashtags per game (null = off).</summary>
    public ITrendSource? Trends { get; init; }

    public Task<ClipResult> ProcessAsync(string sourceFile, GameResult game, ClipOrigin? origin = null, CancellationToken ct = default) =>
        ProcessAsync(sourceFile, game, origin, null, ct);

    public async Task<ClipResult> ProcessAsync(string sourceFile, GameResult game, ClipOrigin? origin, ProcessOptions? options, CancellationToken ct = default)
    {
        var work = Directory.CreateTempSubdirectory("ucut-");
        try
        {
            log($"Clip: {Path.GetFileName(sourceFile)} (Spiel: {game.FolderName}, erkannt über {game.Source})");
            var info = await ffmpeg.ProbeAsync(sourceFile, ct).ConfigureAwait(false);

            var transcript = new Transcript([]);
            if (options?.Words is not null)
            {
                log("  Korrigierte Untertitel werden verwendet");
            }
            else if (info.HasAudio)
            {
                var wav = Path.Combine(work.FullName, "audio.wav");
                await ffmpeg.ExtractWhisperAudioAsync(sourceFile, wav, ct).ConfigureAwait(false);
                log("  Transkript wird erstellt ...");
                transcript = await transcriber.TranscribeAsync(wav, ct).ConfigureAwait(false);
                log($"  Transkript: {(transcript.Words.Count == 0 ? "(keine Sprache)" : transcript.Text)}");
            }

            var cut = options?.Cut is { } keep && keep.End <= info.DurationSeconds + 0.5
                ? keep
                : CutPlanner.Plan(info.DurationSeconds, transcript, settings.Cut);
            if (cut.Reason is not null)
            {
                log($"  Schnitt: {cut.Reason}");
            }

            var clipTranscript = options?.Words is { } edited
                ? new Transcript(edited.Where(w => w.Start < cut.Duration && w.Text.Trim().Length > 0).ToList())
                : transcript.Shift(-cut.Start);
            clipTranscript = new Transcript(clipTranscript.Words.Where(w => w.Start < cut.Duration).ToList());

            var reframeSettings = options?.Mode is { } forced ? WithMode(settings.Reframe, forced) : settings.Reframe;
            NormRect? cam = null;
            var camSource = "";
            if (Reframer.NeedsReframe(info.Width, info.Height, reframeSettings) && reframeSettings.Mode == ReframeMode.Split)
            {
                (cam, camSource) = await LocateCamAsync(sourceFile, info.Width, info.Height, cut, options?.Cam, ct).ConfigureAwait(false);
            }

            var reframe = Reframer.Plan(info.Width, info.Height, reframeSettings, cam);
            var (outWidth, outHeight) = reframe is null ? (info.Width, info.Height) : (reframe.TargetWidth, reframe.TargetHeight);
            if (reframe is not null)
            {
                log(reframe.Mode switch
                {
                    ReframeMode.Split => $"  Querformat ({info.Width}x{info.Height}): Cam {camSource}, kommt nach oben, Spiel darunter",
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

            var laughs = info.HasAudio ? await FindLaughsAsync(sourceFile, cut, clipTranscript, ct).ConfigureAwait(false) : LaughResult.None;

            // Text first: the title goes into the file name.
            var previous = options?.Previous;
            IReadOnlyList<string> trends = [];
            if (previous is null && Trends is not null)
            {
                trends = await Trends.GetAsync(game.FolderName, ct).ConfigureAwait(false);
                if (trends.Count > 0)
                {
                    log($"  Trend-Hashtags ({game.FolderName}): {string.Join(' ', trends)}");
                }
            }

            var context = new ClipContext(
                clipTranscript.Text, game.FolderName, cut.Duration,
                settings.Games.Hashtags.TryGetValue(game.FolderName, out var gameTags) ? gameTags : [],
                settings.Games.CommonHashtags, settings.Writer.ChannelName, trends, laughs.Count);
            var text = previous is null
                ? await WriteTextAsync(context, ct).ConfigureAwait(false)
                : FromPrevious(previous);
            if (text.Funniness is null && laughs.Count > 0)
            {
                // Without Claude: laughs are the best hint we have.
                text = text with { Funniness = Math.Min(10, 3 + 2 * laughs.Count), FunninessReason = $"{laughs.Count} Lacher erkannt (ohne Claude geschätzt)" };
            }

            var now = _time.GetLocalNow();
            // Old clips are named after the day they were recorded, so the game folder stays in order.
            var output = options?.ReplacePath is { Length: > 0 } replace
                ? Path.GetFullPath(replace)
                : OutputNaming.ClipPath(settings.OutputFolder, game.FolderName, origin?.RecordedAt.ToLocalTime() ?? now, text.Title);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var tmpOut = Path.Combine(work.FullName, "out.mp4");

            var job = new RenderJob(Path.GetFullPath(sourceFile), tmpOut, cut, outWidth, outHeight, assName, logoPath, settings.Logo, layout.LogoCenterY, reframe, layout.UseCover);
            log("  Video wird gerendert ...");
            await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath, RenderCommand.Build(job), work.FullName, ct).ConfigureAwait(false);
            FileRetry.Move(tmpOut, output);
            foreach (var stale in new[] { ".thumb.jpg", ".youtube.jpg" })
            {
                FileRetry.Delete(Path.ChangeExtension(output, stale));
            }

            var loudness = await ffmpeg.MeasureLoudnessAsync(output, ct).ConfigureAwait(false);
            if (settings.Writer.YouTubeThumbnail)
            {
                await MakeYouTubeThumbnailAsync(output, text.Title, laughs, loudness, cut.Duration, outWidth, outHeight, work.FullName, ct).ConfigureAwait(false);
            }
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

            meta.Laughs = [.. laughs.Moments];
            meta.Layout = new LayoutInfo { Status = layout.Status, CamEdge = layout.CamEdge, Notes = [.. layout.Notes] };
            meta.Edit = new EditInfo
            {
                CutStart = cut.Start, CutEnd = cut.End,
                Mode = reframe?.Mode.ToString() ?? "", Cam = reframe?.Cam,
                Words = clipTranscript.Words.Select(w => new WordInfo { Text = w.Text, Start = Math.Round(w.Start, 3), End = Math.Round(w.End, 3) }).ToList(),
            };
            if (previous is not null)
            {
                meta.CreatedAt = previous.CreatedAt;
                meta.Funniness = previous.Funniness;
                meta.TextSource = previous.TextSource;
                meta.Uploaded = previous.Uploaded;
                meta.Instagram = previous.Instagram;
                meta.Origin ??= previous.Origin;
                meta.SourceFile = previous.SourceFile.Length > 0 ? previous.SourceFile : meta.SourceFile;
            }
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

    private async Task<LaughResult> FindLaughsAsync(string source, CutPlan cut, Transcript clipTranscript, CancellationToken ct)
    {
        try
        {
            const int rate = 8000;
            var pcm = await ffmpeg.ReadPcmAsync(source, cut.Start, cut.Duration, rate, ct).ConfigureAwait(false);
            var laughs = LaughDetector.Detect(pcm, rate, clipTranscript);
            if (laughs.Count > 0)
            {
                log($"  Lacher: {laughs.Count} (bei {string.Join(", ", laughs.Moments.Select(m => m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"))})");
            }

            return laughs;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            log($"  Lacher-Erkennung übersprungen: {ex.Message}");
            return LaughResult.None;
        }
    }

    private async Task MakeYouTubeThumbnailAsync(string output, string title, LaughResult laughs, LoudnessProfile loudness, double duration, int width, int height, string work, CancellationToken ct)
    {
        try
        {
            var at = YouTubeThumbnail.PickMoment(laughs, loudness, duration);
            await File.WriteAllTextAsync(Path.Combine(work, "thumb.ass"), YouTubeThumbnail.BuildAss(title, width, height, settings.Captions.Font), ct).ConfigureAwait(false);
            await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath, YouTubeThumbnail.Command(output, at, "thumb.ass", Path.ChangeExtension(output, ".youtube.jpg")), work, ct).ConfigureAwait(false);
            log($"  YouTube-Vorschaubild bei {at:0.0} s");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            log($"  YouTube-Vorschaubild ging nicht: {ex.Message}");
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

    /// <summary>Cam by hand, from the settings, found in the picture, or a known position from earlier clips (in that order).</summary>
    private async Task<(NormRect? Cam, string Source)> LocateCamAsync(string video, int width, int height, CutPlan cut, NormRect? manual, CancellationToken ct)
    {
        var memory = CamMemory.ForOutput(settings.OutputFolder);
        if (manual is not null)
        {
            memory.Remember(manual, width, height, weight: 5);
            return (manual, "von Hand gesetzt");
        }

        if (settings.Reframe.CamRect is { } fixedCam)
        {
            return (fixedCam, "aus den Einstellungen");
        }

        const int w = 320, h = 180, n = 8;
        var frames = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            var (px, _, _) = await ffmpeg.GrabFrameAsync(video, cut.Start + cut.Duration * (i + 0.5) / n, w, h, "rgb24", ct).ConfigureAwait(false);
            frames.Add(px);
        }

        if (CamFinder.Find(frames, w, h) is { } found)
        {
            memory.Remember(found, width, height);
            return (found, "gefunden");
        }

        var best = memory.Candidates(width, height)
            .Select(c => (Cam: c, Score: CamFinder.Verify(frames, w, h, c)))
            .Where(c => c.Score >= CamFinder.VerifyMinScore)
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();
        return best.Cam is not null ? (best.Cam, "an einer bekannten Stelle aus früheren Clips gefunden") : (null, "");
    }

    private static ReframeSettings WithMode(ReframeSettings s, ReframeMode mode) => new()
    {
        Enabled = true, Mode = mode, FallbackMode = s.FallbackMode, CamRect = s.CamRect, MaxCamHeight = s.MaxCamHeight,
        FocusX = s.FocusX, TargetWidth = s.TargetWidth, TargetHeight = s.TargetHeight,
    };

    private static ClipText FromPrevious(ClipMetadata m) => new(
        m.YouTube.Title, m.TikTok.Caption, m.YouTube.Title, m.YouTube.Description, m.YouTube.Tags.Select(t => "#" + t).ToList(),
        m.Funniness?.Score, m.Funniness?.Reason, m.TextSource);

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
