using System.Collections.Concurrent;
using UCut.Analysis;
using UCut.Games;
using UCut.Media;
using UCut.Pipeline;
using UCut.Settings;
using UCut.Transcription;
using UCut.Twitch;

namespace UCut.App;

public sealed record BatchProgress(int Done, int Total, string Current);

public sealed record BatchResult(int Done, int Failed, bool Cancelled);

public sealed record TwitchSearchResult(IReadOnlyList<ClipCandidate> Candidates, int Found, int AlreadyLoaded);

/// <summary>What is installed and set up; shown on the start page.</summary>
public sealed record ToolCheck(bool Ffmpeg, bool YtDlp, bool Twitch, bool Claude, bool WatchFolder);

/// <summary>
/// Everything UCut can do, for the window and the command line: process files, watch the Aitum folder,
/// search and load old Twitch clips. One engine per loaded settings file.
/// </summary>
public sealed class UCutEngine : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public UCutEngine(UCutSettings settings, RunLog log, HttpClient? http = null, ITranscriber? transcriber = null, IClipWriter? writer = null)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
        {
            throw new InvalidDataException("outputFolder ist in den Einstellungen nicht gesetzt.");
        }

        Settings = settings;
        Log = log;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        Directory.CreateDirectory(settings.OutputFolder);
        log.FilePath = Path.Combine(settings.OutputFolder, LogFileName);

        Ffmpeg = new Ffmpeg(settings.FfmpegPath, settings.FfprobePath);
        writer ??= settings.Writer.UseClaude && ClaudeClipWriter.IsAvailable ? new ClaudeClipWriter(settings.Writer.Model) : new TemplateClipWriter();
        UsesClaude = writer is not TemplateClipWriter;
        Pipeline = new ClipPipeline(settings, Ffmpeg, transcriber ?? (WhisperWorker.ExecutablePath is { } worker
            ? new IsolatedTranscriber(settings.Whisper, worker, log.Write)
            : new WhisperTranscriber(settings.Whisper, log.Write)), writer, log.Write)
        {
            Trends = UsesClaude && settings.Writer.TrendHashtags
                ? new ClaudeTrendHashtags(settings.Writer.Model, ClaudeTrendHashtags.DefaultPath(settings.OutputFolder))
                : null,
        };
        Twitch = new TwitchApi(settings.Games, _http);
        Games = new GameDetector(settings.Games, [new TwitchGameSource(settings.Games, _http), new ProcessGameSource(settings.Games)], log.Write);
    }

    public const string LogFileName = "UCut-Protokoll.txt";

    public UCutSettings Settings { get; }
    public RunLog Log { get; }
    public Ffmpeg Ffmpeg { get; }
    public ClipPipeline Pipeline { get; }
    public GameDetector Games { get; }
    public TwitchApi Twitch { get; }

    /// <summary>Upload to TikTok/YouTube/Instagram; accounts.json next to settings.json.</summary>
    public UCut.Upload.UploadService Uploads => _uploads ??= new UCut.Upload.UploadService(
        Settings.Upload, new UCut.Upload.AccountStore(UCut.Upload.AccountStore.DefaultPath()), Log);

    private UCut.Upload.UploadService? _uploads;
    public bool UsesClaude { get; }

    /// <summary>Raised after each finished clip (library refresh).</summary>
    public event Action<ClipResult>? ClipFinished;

    /// <summary>Settings file; created with sensible defaults (Videos\Aitum Vertical, Videos\UCut, channel uleimon) if missing.</summary>
    public static UCutSettings LoadOrCreateSettings(string path, out bool created)
    {
        created = !File.Exists(path);
        if (created)
        {
            SettingsStore.Save(DefaultSettings(), path);
        }

        return SettingsStore.Load(path);
    }

    public static UCutSettings DefaultSettings()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
        {
            videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        }

        return new UCutSettings
        {
            WatchFolder = Path.Combine(videos, "Aitum Vertical"),
            OutputFolder = Path.Combine(videos, "UCut"),
            Games = new GameSettings { TwitchChannel = "uleimon" },
        };
    }

    public async Task<ToolCheck> CheckToolsAsync(CancellationToken ct = default) => new(
        await CanRunAsync(Settings.FfmpegPath, "-version", ct).ConfigureAwait(false),
        await CanRunAsync(Settings.TwitchClips.YtDlpPath, "--version", ct).ConfigureAwait(false),
        Twitch.IsConfigured,
        UsesClaude,
        Directory.Exists(Settings.WatchFolder));

    private static async Task<bool> CanRunAsync(string exe, string arg, CancellationToken ct)
    {
        try
        {
            return (await ProcessRunner.RunAsync(exe, [arg], null, ct).ConfigureAwait(false)).ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Own clips (files or folders): cut, captions, logo, texts. Errors of one clip do not stop the others.</summary>
    public async Task<BatchResult> ProcessFilesAsync(IEnumerable<string> inputs, string? gameOverride, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var files = ClipImport.Collect(inputs, out var missing);
        foreach (var m in missing)
        {
            Log.Write($"Nicht gefunden oder kein Video: {m}");
        }

        Log.Write($"{files.Count} Clip(s) werden verarbeitet.");
        var done = 0;
        var failed = 0;
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            progress?.Report(new BatchProgress(done + failed, files.Count, Path.GetFileName(file)));
            try
            {
                var game = await ClipImport.ChooseGameAsync(file, gameOverride, Settings.Games, Games, DateTime.Now).ConfigureAwait(false);
                ClipFinished?.Invoke(await Pipeline.ProcessAsync(file, game, ct: ct).ConfigureAwait(false));
                done++;
            }
            catch (Exception ex) when (IsClipError(ex, ct))
            {
                failed++;
                Log.Write($"Fehler bei {Path.GetFileName(file)}: {ex.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        return Finish(done, failed, files.Count, ct, progress);
    }

    /// <summary>Watches the Aitum folder until cancelled. The game is detected the moment a clip appears.</summary>
    public async Task WatchAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Settings.WatchFolder))
        {
            throw new InvalidDataException($"Der Aitum-Ordner existiert nicht: {Settings.WatchFolder}");
        }

        var detected = new ConcurrentDictionary<string, Task<GameResult>>(StringComparer.OrdinalIgnoreCase);
        using var watcher = new ClipWatcher(Settings.WatchFolder, Log.Write);
        watcher.FileAppeared += path => detected.TryAdd(path, Games.DetectAsync(ct));
        watcher.Start();
        Log.Write("Bereit. Neue Clips werden automatisch verarbeitet.");
        try
        {
            await foreach (var file in watcher.ReadFinishedAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var game = detected.TryRemove(file, out var t) ? await t.ConfigureAwait(false) : await Games.DetectAsync(ct).ConfigureAwait(false);
                    ClipFinished?.Invoke(await Pipeline.ProcessAsync(file, game, ct: ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (IsClipError(ex, ct))
                {
                    Log.Write($"Fehler bei {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Write("Überwachung beendet.");
        }
    }

    /// <summary>All clips of the channel in the range, minus already loaded ones, funny-looking first.</summary>
    public async Task<TwitchSearchResult> SearchTwitchAsync(DateTimeOffset from, DateTimeOffset to, bool onlyFunny, int maxClips, CancellationToken ct = default)
    {
        if (!Twitch.IsConfigured)
        {
            throw new InvalidOperationException("Twitch ist noch nicht eingerichtet. Trag in den Einstellungen Kanal, Client-ID und Secret ein.");
        }

        Log.Write($"Suche Twitch-Clips von {Twitch.Channel} seit {from:dd.MM.yyyy} ...");
        var clips = await new TwitchClipSearch(Twitch, Log.Write).ListAsync(from, to, ct).ConfigureAwait(false);
        var archive = ClipArchive.Load(Settings.OutputFolder);
        var options = new TwitchClipSettings
        {
            OnlyFunny = onlyFunny,
            MaxClips = maxClips,
            MinViews = Settings.TwitchClips.MinViews,
            TopViewsShare = Settings.TwitchClips.TopViewsShare,
            FunnyWords = Settings.TwitchClips.FunnyWords,
        };
        var fresh = clips.Where(c => !archive.Contains(c.Id)).ToList();
        var candidates = FunnyFilter.Rank(fresh, options);
        Log.Write($"{candidates.Count} Clip(s) {(onlyFunny ? "sehen lustig aus" : "gefunden")}, {clips.Count - fresh.Count} schon früher geladen.");
        return new TwitchSearchResult(candidates, clips.Count, clips.Count - fresh.Count);
    }

    /// <summary>The original recording of a finished clip (Aitum file or Twitch download), or null when it is gone.</summary>
    public string? FindOriginal(UCut.Library.LibraryClip clip)
    {
        var meta = clip.Metadata;
        if (File.Exists(meta.SourceFile))
        {
            return meta.SourceFile;
        }

        if (meta.Origin is { Platform: "Twitch" } o && Directory.Exists(TwitchDownloadFolder))
        {
            var safeId = new string(o.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
            return Directory.EnumerateFiles(TwitchDownloadFolder, "*_" + safeId + ".mp4").FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// Makes a finished clip again from its original: with corrected words, another layout or a cam set by hand.
    /// Texts, uploads and the file name stay.
    /// </summary>
    public async Task<ClipResult> RedoAsync(UCut.Library.LibraryClip clip, ProcessOptions options, CancellationToken ct = default)
    {
        var meta = clip.Metadata;
        var source = FindOriginal(clip)
                     ?? throw new InvalidOperationException($"Das Original ist nicht mehr da ({meta.SourceFile}). Ohne Original kann der Clip nicht neu gemacht werden.");

        var origin = meta.Origin is { } og ? new ClipOrigin(og.RecordedAt, og.Platform, og.Id, og.Url, og.Title, og.Views) : null;
        var game = new GameResult(meta.Game, meta.Game, meta.GameSource);
        var cut = meta.Edit is { CutEnd: > 0 } e ? new UCut.Editing.CutPlan(e.CutStart, e.CutEnd, meta.CutNote) : null;
        Log.Write($"\"{clip.Title}\" wird neu gemacht ...");
        var result = await Pipeline.ProcessAsync(source, game, origin, options with
        {
            Cut = options.Cut ?? cut,
            Previous = meta,
            ReplacePath = clip.VideoPath,
        }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// "Recreate": renders the clips again from their originals, exactly as they were (same cut, layout, cam and words).
    /// Clips from older versions without that info get a fresh transcript, so their words can be edited afterwards.
    /// Texts, funniness and uploads stay. One failed clip does not stop the others.
    /// </summary>
    public async Task<BatchResult> RecreateAsync(IReadOnlyList<UCut.Library.LibraryClip> clips, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                return new BatchResult(done, failed, true);
            }

            var clip = clips[i];
            progress?.Report(new BatchProgress(i, clips.Count, clip.Title));
            try
            {
                await RedoAsync(clip, SameAsBefore(clip.Metadata), ct).ConfigureAwait(false);
                done++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
            {
                failed++;
                Log.Write($"\"{clip.Title}\" übersprungen: {ex.Message}");
            }
        }

        Log.Write($"Recreate fertig: {done} neu gerendert, {failed} übersprungen.");
        return new BatchResult(done, failed, false);
    }

    /// <summary>Options that rebuild a clip the way it was made last time.</summary>
    public static ProcessOptions SameAsBefore(UCut.Metadata.ClipMetadata meta)
    {
        var edit = meta.Edit;
        if (edit is null)
        {
            return new ProcessOptions();
        }

        UCut.Editing.ReframeMode? mode = Enum.TryParse<UCut.Editing.ReframeMode>(edit.Mode, out var m) ? m : null;
        return new ProcessOptions
        {
            Mode = mode,
            Cam = mode == UCut.Editing.ReframeMode.Split ? edit.Cam : null,
            Words = edit.Words.Count > 0 ? edit.Words.Select(w => new Word(w.Text, w.Start, w.End)).ToList() : null,
        };
    }

    /// <summary>Folder for the original Twitch downloads (kept, so a preview is not downloaded twice).</summary>
    public string TwitchDownloadFolder => string.IsNullOrWhiteSpace(Settings.TwitchClips.DownloadFolder)
        ? Path.Combine(Settings.OutputFolder, "_Twitch-Originale")
        : Settings.TwitchClips.DownloadFolder;

    /// <summary>Downloads one Twitch clip (or returns the file already there). Used for the preview and before processing.</summary>
    public Task<string> DownloadTwitchClipAsync(TwitchClip clip, CancellationToken ct = default) =>
        new ClipDownloader(Settings.TwitchClips.YtDlpPath).DownloadAsync(clip, TwitchDownloadFolder, ct);

    /// <summary>Downloads (yt-dlp) and processes the chosen Twitch clips. The overview CSV is updated after every clip.</summary>
    public async Task<BatchResult> ProcessTwitchAsync(IReadOnlyList<TwitchClip> clips, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var archive = ClipArchive.Load(Settings.OutputFolder);
        var done = 0;
        var failed = 0;
        foreach (var clip in clips)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            progress?.Report(new BatchProgress(done + failed, clips.Count, clip.Title));
            try
            {
                Log.Write($"[{done + failed + 1}/{clips.Count}] \"{clip.Title}\" vom {clip.CreatedAt.ToLocalTime():dd.MM.yyyy} ({clip.ViewCount} Aufrufe) wird geladen ...");
                var file = await DownloadTwitchClipAsync(clip, ct).ConfigureAwait(false);
                var game = clip.GameName is { Length: > 0 } raw
                    ? new GameResult(GameDetector.ToFolderName(raw, Settings.Games), raw, "Twitch-Clip")
                    : new GameResult(Settings.Games.UnknownFolder, null, "Twitch-Clip");
                var origin = new ClipOrigin(clip.CreatedAt, "Twitch", clip.Id, clip.Url, clip.Title, clip.ViewCount);
                var result = await Pipeline.ProcessAsync(file, game, origin, ct).ConfigureAwait(false);
                archive.Add(new ArchivedClip
                {
                    Id = clip.Id, Url = clip.Url, Title = clip.Title, Views = clip.ViewCount, CreatedAt = clip.CreatedAt,
                    Game = game.FolderName, Video = result.VideoPath, Score = result.Metadata.Score.Total,
                    Funniness = result.Metadata.Funniness?.Score, ProcessedAt = DateTimeOffset.Now,
                });
                archive.WriteOverview();
                ClipFinished?.Invoke(result);
                done++;
            }
            catch (Exception ex) when (IsClipError(ex, ct))
            {
                failed++;
                Log.Write($"Fehler bei {clip.Url}: {ex.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        if (archive.Clips.Count > 0)
        {
            Log.Write($"Übersicht (lustigste zuerst): {archive.WriteOverview()}");
        }

        return Finish(done, failed, clips.Count, ct, progress);
    }

    private BatchResult Finish(int done, int failed, int total, CancellationToken ct, IProgress<BatchProgress>? progress)
    {
        progress?.Report(new BatchProgress(done + failed, total, ""));
        var cancelled = ct.IsCancellationRequested;
        Log.Write(cancelled
            ? $"Abgebrochen: {done} fertig, {failed} mit Fehler."
            : failed == 0 ? $"Fertig: {done} Clip(s)." : $"Fertig: {done} Clip(s), {failed} mit Fehler.");
        return new BatchResult(done, failed, cancelled);
    }

    private static bool IsClipError(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested
        && ex is InvalidOperationException or IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or TaskCanceledException;

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
