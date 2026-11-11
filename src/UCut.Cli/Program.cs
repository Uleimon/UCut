using System.Globalization;
using UCut.Analysis;
using UCut.Editing;
using UCut.Games;
using UCut.Media;
using UCut.Pipeline;
using UCut.Settings;
using UCut.Transcription;
using UCut.Twitch;
using UCut.Cli;

// ucut – automatische Shorts aus OBS/Aitum-Clips.
// Befehle:
//   ucut init [--settings <datei>]                 Einstellungen anlegen
//   ucut watch [--settings <datei>]                Ordner überwachen und jeden neuen Clip verarbeiten
//   ucut process <video|ordner>... [--game <name>]  eigene Clips verarbeiten (auch Drag & Drop auf ucut.exe)
//   ucut preview <video> [--out <bild.png>] [...]  Standbild mit Logo und Beispiel-Untertitel zum Einstellen
//   ucut game [...]                                aktuelles Spiel anzeigen
//   ucut twitch [--seit 2020-01-01] [--bis ...] [--alle] [--max N] [--liste]  alte Twitch-Clips laden und verarbeiten

var arguments = args.ToList();
var settingsPath = TakeOption(arguments, "--settings") ?? SettingsStore.DefaultPath;
// Drag & drop onto ucut.exe: Windows starts it with the dropped paths as arguments.
var droppedFiles = arguments.Count > 0 && (File.Exists(arguments[0]) || Directory.Exists(arguments[0]));
if (droppedFiles)
{
    arguments.Insert(0, "process");
}

var command = arguments.Count > 0 ? arguments[0].ToLowerInvariant() : "help";

void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

try
{
    switch (command)
    {
        case "init":
            return Init();
        case "watch":
            return await WatchAsync();
        case "process" when arguments.Count >= 2:
        {
            var gameOption = TakeOption(arguments, "--game");
            var code = await ProcessManyAsync(arguments.Skip(1).ToList(), gameOption);
            if (droppedFiles && !Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("Fertig. Taste drücken zum Schließen ...");
                Console.ReadKey(intercept: true);
            }

            return code;
        }
        case "preview" when arguments.Count >= 2:
            return await PreviewAsync(arguments[1], TakeOption(arguments, "--out") ?? "ucut-vorschau.png");
        case "game":
            return await ShowGameAsync();
        case "twitch":
        {
            var since = TakeOption(arguments, "--seit");
            var until = TakeOption(arguments, "--bis");
            var max = TakeOption(arguments, "--max");
            var all = TakeFlag(arguments, "--alle");
            var listOnly = TakeFlag(arguments, "--liste");
            KeepAwake.On();
            try
            {
                return await TwitchAsync(since, until, max, all, listOnly);
            }
            finally
            {
                KeepAwake.Off();
            }
        }
        default:
            PrintHelp();
            return command is "help" or "--help" or "-h" ? 0 : 2;
    }
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 1;
}

int Init()
{
    if (File.Exists(settingsPath))
    {
        Console.WriteLine($"Einstellungen existieren schon: {settingsPath}");
        return 0;
    }

    var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    var s = new UCutSettings
    {
        WatchFolder = Path.Combine(videos, "Aitum Vertical"),
        OutputFolder = Path.Combine(videos, "UCut"),
        Games = new GameSettings { TwitchChannel = "uleimon" },
    };
    SettingsStore.Save(s, settingsPath);
    Console.WriteLine($"Vorlage angelegt: {settingsPath}");
    Console.WriteLine("Bitte darin watchFolder (Aitum-Ausgabeordner) und outputFolder prüfen, für Twitch-Clips unter \"games\" twitchClientId und twitchClientSecret eintragen.");
    return 0;
}

(UCutSettings Settings, Ffmpeg Ffmpeg, ClipPipeline Pipeline, GameDetector Games) Build(HttpClient http)
{
    var s = SettingsStore.Load(settingsPath);
    if (string.IsNullOrWhiteSpace(s.OutputFolder))
    {
        throw new InvalidDataException("outputFolder ist in den Einstellungen nicht gesetzt.");
    }

    var ffmpeg = new Ffmpeg(s.FfmpegPath, s.FfprobePath);
    IClipWriter writer = s.Writer.UseClaude && ClaudeClipWriter.IsAvailable
        ? new ClaudeClipWriter(s.Writer.Model)
        : new TemplateClipWriter();
    if (s.Writer.UseClaude && writer is TemplateClipWriter)
    {
        Log("Hinweis: ANTHROPIC_API_KEY ist nicht gesetzt, Titel und Hashtags kommen aus der Vorlage (ohne Lustigkeits-Bewertung).");
    }

    var pipeline = new ClipPipeline(s, ffmpeg, new WhisperTranscriber(s.Whisper, Log), writer, Log);
    var games = new GameDetector(s.Games, [new TwitchGameSource(s.Games, http), new ProcessGameSource(s.Games)], Log);
    return (s, ffmpeg, pipeline, games);
}

async Task<int> ProcessManyAsync(IReadOnlyList<string> inputs, string? gameOverride)
{
    using var http = new HttpClient();
    var (s, _, pipeline, games) = Build(http);
    var files = ClipImport.Collect(inputs, out var missing);
    foreach (var m in missing)
    {
        Log($"Nicht gefunden oder kein Video: {m}");
    }

    if (files.Count == 0)
    {
        Log("Keine Videos zum Verarbeiten gefunden (.mp4, .mkv, .mov).");
        return 1;
    }

    Log($"{files.Count} Clip(s) werden verarbeitet.");
    var failed = 0;
    foreach (var file in files)
    {
        try
        {
            var game = await ClipImport.ChooseGameAsync(file, gameOverride, s.Games, games, DateTime.Now);
            await pipeline.ProcessAsync(file, game);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            failed++;
            Log($"Fehler bei {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    Log(failed == 0 ? "Alle Clips fertig." : $"{files.Count - failed} fertig, {failed} mit Fehler.");
    return failed == 0 ? 0 : 1;
}

async Task<int> WatchAsync()
{
    using var http = new HttpClient();
    var (s, _, pipeline, games) = Build(http);
    if (!Directory.Exists(s.WatchFolder))
    {
        throw new InvalidDataException($"watchFolder existiert nicht: {s.WatchFolder}");
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // The game is detected the moment the clip appears, not after rendering.
    var detected = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<GameResult>>(StringComparer.OrdinalIgnoreCase);
    using var watcher = new ClipWatcher(s.WatchFolder, Log);
    watcher.FileAppeared += path => detected.TryAdd(path, games.DetectAsync(cts.Token));
    watcher.Start();
    Log("Bereit. Neue Clips werden automatisch verarbeitet (Strg+C beendet).");
    try
    {
        await foreach (var file in watcher.ReadFinishedAsync(cts.Token))
        {
            try
            {
                var game = detected.TryRemove(file, out var t) ? await t : await games.DetectAsync(cts.Token);
                await pipeline.ProcessAsync(file, game, ct: cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Fehler bei {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        Log("Beendet.");
    }

    return 0;
}

async Task<int> ShowGameAsync()
{
    using var http = new HttpClient();
    var (_, _, _, games) = Build(http);
    var g = await games.DetectAsync();
    Console.WriteLine($"Spiel: {g.FolderName} (Quelle: {g.Source}{(g.RawName is not null && g.RawName != g.FolderName ? $", Twitch/Prozess: {g.RawName}" : "")})");
    return 0;
}

async Task<int> PreviewAsync(string video, string outPng)
{
    var s = SettingsStore.Load(settingsPath);
    var ffmpeg = new Ffmpeg(s.FfmpegPath, s.FfprobePath);
    var info = await ffmpeg.ProbeAsync(video);
    var work = Directory.CreateTempSubdirectory("ucut-preview-");
    try
    {
        var sample = new Transcript([new Word("So", 0, 0.3), new Word("sehen", 0.3, 0.6), new Word("Untertitel", 0.6, 3)]);
        await File.WriteAllTextAsync(Path.Combine(work.FullName, "captions.ass"),
            new UCut.Captions.CaptionBuilder(s.Captions).BuildAss(sample, info.Width, info.Height));
        var logo = string.IsNullOrWhiteSpace(s.Logo.Path) ? Path.Combine(AppContext.BaseDirectory, "assets", "uleimon-logo.png") : s.Logo.Path;
        var at = Math.Min(1, info.DurationSeconds / 2);
        var job = new RenderJob(Path.GetFullPath(video), Path.GetFullPath(outPng), new CutPlan(at, at + 1, null), info.Width, info.Height,
                                "captions.ass", File.Exists(logo) ? Path.GetFullPath(logo) : null, s.Logo, null);
        var cmd = RenderCommand.Build(job);
        // Only one frame as PNG instead of a video.
        var outIndex = cmd.IndexOf("-c:v");
        cmd.RemoveRange(outIndex, cmd.Count - outIndex);
        cmd.AddRange(["-frames:v", "1", Path.GetFullPath(outPng)]);
        await ProcessRunner.RunCheckedAsync(s.FfmpegPath, cmd, work.FullName);
        Console.WriteLine($"Vorschau gespeichert: {Path.GetFullPath(outPng)}");
        Console.WriteLine("Logo-Position und Abdeckfläche lassen sich in den Einstellungen unter \"logo\" anpassen.");
        return 0;
    }
    finally
    {
        work.Delete(recursive: true);
    }
}

async Task<int> TwitchAsync(string? since, string? until, string? max, bool all, bool listOnly)
{
    using var http = new HttpClient();
    var (s, _, pipeline, _) = Build(http);
    var opts = s.TwitchClips;
    if (all)
    {
        opts.OnlyFunny = false;
    }

    if (max is not null)
    {
        opts.MaxClips = int.Parse(max, CultureInfo.InvariantCulture);
    }

    var api = new TwitchApi(s.Games, http);
    if (!api.IsConfigured)
    {
        Console.Error.WriteLine("Für Twitch-Clips braucht UCut deinen Kanal und eine Twitch-App:");
        Console.Error.WriteLine("  1. https://dev.twitch.tv/console/apps -> Anwendung registrieren (Redirect-URL: http://localhost, Kategorie: Application Integration)");
        Console.Error.WriteLine("  2. In den Einstellungen unter \"games\": twitchChannel, twitchClientId und twitchClientSecret eintragen");
        return 1;
    }

    var from = ParseDate(since ?? opts.Since, "--seit");
    var to = string.IsNullOrWhiteSpace(until ?? opts.Until) ? DateTimeOffset.Now : ParseDate(until ?? opts.Until, "--bis").AddDays(1);
    Log($"Suche Twitch-Clips von {api.Channel} seit {from:dd.MM.yyyy} ...");
    var clips = await new TwitchClipSearch(api, Log).ListAsync(from, to);

    var archive = ClipArchive.Load(s.OutputFolder);
    var candidates = FunnyFilter.Rank(clips.Where(c => !archive.Contains(c.Id)).ToList(), opts);
    var skipped = clips.Count(c => archive.Contains(c.Id));
    Log($"{candidates.Count} Clip(s) {(opts.OnlyFunny ? "sehen lustig aus" : "werden geladen")}" + (skipped > 0 ? $", {skipped} schon früher geladen." : "."));

    if (listOnly)
    {
        foreach (var c in candidates)
        {
            Console.WriteLine($"  {c.Clip.CreatedAt.ToLocalTime():dd.MM.yyyy}  {c.Clip.ViewCount,6} Aufrufe  {c.Clip.GameName ?? "?",-20}  {c.Clip.Title}  ({string.Join("; ", c.Reasons)})");
        }

        return 0;
    }

    var downloader = new ClipDownloader(opts.YtDlpPath);
    var downloadFolder = string.IsNullOrWhiteSpace(opts.DownloadFolder) ? Path.Combine(s.OutputFolder, "_Twitch-Originale") : opts.DownloadFolder;
    var failed = 0;
    var n = 0;
    foreach (var c in candidates)
    {
        n++;
        var clip = c.Clip;
        try
        {
            Log($"[{n}/{candidates.Count}] \"{clip.Title}\" vom {clip.CreatedAt.ToLocalTime():dd.MM.yyyy} ({clip.ViewCount} Aufrufe) wird geladen ...");
            var file = await downloader.DownloadAsync(clip, downloadFolder);
            var game = clip.GameName is { Length: > 0 } raw
                ? new GameResult(GameDetector.ToFolderName(raw, s.Games), raw, "Twitch-Clip")
                : new GameResult(s.Games.UnknownFolder, null, "Twitch-Clip");
            var origin = new ClipOrigin(clip.CreatedAt, "Twitch", clip.Id, clip.Url, clip.Title, clip.ViewCount);
            var result = await pipeline.ProcessAsync(file, game, origin);
            archive.Add(new ArchivedClip
            {
                Id = clip.Id, Url = clip.Url, Title = clip.Title, Views = clip.ViewCount, CreatedAt = clip.CreatedAt,
                Game = game.FolderName, Video = result.VideoPath, Score = result.Metadata.Score.Total,
                Funniness = result.Metadata.Funniness?.Score, ProcessedAt = DateTimeOffset.Now,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            failed++;
            Log($"Fehler bei {clip.Url}: {ex.Message}");
        }
    }

    if (archive.Clips.Count > 0)
    {
        Log($"Übersicht (lustigste zuerst): {archive.WriteOverview()}");
    }

    Log(failed == 0 ? "Alle Twitch-Clips fertig." : $"{candidates.Count - failed} fertig, {failed} mit Fehler.");
    return failed == 0 ? 0 : 1;
}

static DateTimeOffset ParseDate(string value, string name) =>
    DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd", "dd.MM.yyyy", "yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
        ? d
        : throw new ArgumentException($"{name}: Datum '{value}' nicht verstanden (Beispiel: 2020-01-01 oder 01.01.2020).");

static bool TakeFlag(List<string> list, string name)
{
    var i = list.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    if (i < 0)
    {
        return false;
    }

    list.RemoveAt(i);
    return true;
}

static string? TakeOption(List<string> list, string name)
{
    var i = list.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= list.Count)
    {
        return null;
    }

    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static void PrintHelp() => Console.WriteLine(
    """
    UCut – automatische Shorts aus deinen OBS/Aitum-Clips

      ucut init                      Einstellungen anlegen (%APPDATA%\UCut\settings.json)
      ucut watch                     Aitum-Ordner überwachen, jeder neue Clip wird automatisch verarbeitet
      ucut process <video|ordner>... eigene Clips verarbeiten, auch mehrere oder ganze Ordner
                                     (--game "GTA V" setzt das Spiel von Hand)
                                     Einfacher: Videos im Explorer auf ucut.exe ziehen.
      ucut preview <video>           Standbild mit Logo und Beispiel-Untertiteln (--out bild.png)
      ucut game                      zeigt, welches Spiel gerade erkannt wird
      ucut twitch                    alte Twitch-Clips (seit 2020) nach lustigen durchsuchen, laden und fertig machen
                                     --seit 2021-01-01 / --bis 2022-12-31  Zeitraum
                                     --liste  nur anzeigen, was geladen würde
                                     --alle   jeden Clip laden, nicht nur die lustig aussehenden
                                     --max 20 höchstens so viele Clips (die besten zuerst)

      --settings <datei>             andere Einstellungsdatei verwenden
    """);
