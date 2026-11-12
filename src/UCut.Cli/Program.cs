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
using UCut.App;

// ucut – automatische Shorts aus OBS/Aitum-Clips.
// Befehle:
//   ucut init [--settings <datei>]                 Einstellungen anlegen
//   ucut watch [--settings <datei>]                Ordner überwachen und jeden neuen Clip verarbeiten
//   ucut process <video|ordner>... [--game <name>]  eigene Clips verarbeiten (auch Drag & Drop auf ucut.exe)
//   ucut preview <video> [--out <bild.png>] [...]  Standbild mit Logo und Beispiel-Untertitel zum Einstellen
//   ucut game [...]                                aktuelles Spiel anzeigen
//   ucut twitch [--seit 2020-01-01] [--bis ...] [--alle] [--max N] [--liste]  alte Twitch-Clips laden und verarbeiten

if (args.Length > 0 && args[0] == WhisperWorker.Argument)
{
    return WhisperWorker.Run(args);
}

WhisperWorker.ExecutablePath = Environment.ProcessPath;
var arguments = args.ToList();
var settingsPath = TakeOption(arguments, "--settings") ?? SettingsStore.DefaultPath;
// Drag & drop onto ucut.exe: Windows starts it with the dropped paths as arguments.
var droppedFiles = arguments.Count > 0 && (File.Exists(arguments[0]) || Directory.Exists(arguments[0]));
if (droppedFiles)
{
    arguments.Insert(0, "process");
}

var command = arguments.Count > 0 ? arguments[0].ToLowerInvariant() : "help";

// Shown live and, once the settings are loaded, also written to UCut-Protokoll.txt in the output folder.
var runLog = new RunLog();
runLog.Line += Console.WriteLine;
void Log(string message) => runLog.Write(message);

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
    Log($"Fehler: {ex.Message}");
    return 1;
}

int Init()
{
    if (File.Exists(settingsPath))
    {
        Console.WriteLine($"Einstellungen existieren schon: {settingsPath}");
        return 0;
    }

    UCutEngine.LoadOrCreateSettings(settingsPath, out _);
    Console.WriteLine($"Vorlage angelegt: {settingsPath}");
    Console.WriteLine("Bitte darin watchFolder (Aitum-Ausgabeordner) und outputFolder prüfen, für Twitch-Clips unter \"games\" twitchClientId und twitchClientSecret eintragen.");
    return 0;
}

UCutEngine Build()
{
    var engine = new UCutEngine(SettingsStore.Load(settingsPath), runLog);
    runLog.Write($"--- UCut gestartet: {string.Join(' ', args)} ---");
    if (engine.Settings.Writer.UseClaude && !engine.UsesClaude)
    {
        runLog.Write("Hinweis: ANTHROPIC_API_KEY ist nicht gesetzt, Titel und Hashtags kommen aus der Vorlage (ohne Lustigkeits-Bewertung).");
    }

    return engine;
}

async Task<int> ProcessManyAsync(IReadOnlyList<string> inputs, string? gameOverride)
{
    using var engine = Build();
    var r = await engine.ProcessFilesAsync(inputs, gameOverride);
    return r.Failed == 0 && r.Done > 0 ? 0 : 1;
}

async Task<int> WatchAsync()
{
    using var engine = Build();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Log("Strg+C beendet die Überwachung.");
    await engine.WatchAsync(cts.Token);
    return 0;
}

async Task<int> ShowGameAsync()
{
    using var engine = Build();
    var g = await engine.Games.DetectAsync();
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
    using var engine = Build();
    var opts = engine.Settings.TwitchClips;
    if (!engine.Twitch.IsConfigured)
    {
        Console.Error.WriteLine("Für Twitch-Clips braucht UCut deinen Kanal und eine Twitch-App:");
        Console.Error.WriteLine("  1. https://dev.twitch.tv/console/apps -> Anwendung registrieren (Redirect-URL: https://localhost, Kategorie: Application Integration)");
        Console.Error.WriteLine("  2. In den Einstellungen unter \"games\": twitchChannel, twitchClientId und twitchClientSecret eintragen");
        return 1;
    }

    var from = ParseDate(since ?? opts.Since, "--seit");
    var to = string.IsNullOrWhiteSpace(until ?? opts.Until) ? DateTimeOffset.Now : ParseDate(until ?? opts.Until, "--bis").AddDays(1);
    var maxClips = max is null ? opts.MaxClips : int.Parse(max, CultureInfo.InvariantCulture);
    var search = await engine.SearchTwitchAsync(from, to, opts.OnlyFunny && !all, maxClips);

    if (listOnly)
    {
        foreach (var c in search.Candidates)
        {
            Console.WriteLine($"  {c.Clip.CreatedAt.ToLocalTime():dd.MM.yyyy}  {c.Clip.ViewCount,6} Aufrufe  {c.Clip.GameName ?? "?",-20}  {c.Clip.Title}  ({string.Join("; ", c.Reasons)})");
        }

        return 0;
    }

    var r = await engine.ProcessTwitchAsync(search.Candidates.Select(c => c.Clip).ToList());
    return r.Failed == 0 ? 0 : 1;
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
