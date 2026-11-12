using System.Diagnostics;
using System.Text.Json;
using UCut.Settings;

namespace UCut.Transcription;

/// <summary>
/// Runs whisper.cpp in its own process. whisper.cpp is native code: when it fails on a clip it can end the whole
/// program without any error message (Ulei lost a run of 398 Twitch clips that way). In a second process only
/// that process dies; UCut retries without word alignment and otherwise carries on without subtitles.
/// </summary>
public static class WhisperWorker
{
    public const string Argument = "--whisper-worker";

    /// <summary>The program that understands <see cref="Argument"/> (UCut.exe or ucut-cli.exe); null = run in-process.</summary>
    public static string? ExecutablePath { get; set; }

    internal sealed record Request(string Wav, string Model, string ModelFolder, string Language, bool Dtw);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Entry point in the worker process: <c>--whisper-worker request.json result.json</c>. Returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || args[0] != Argument)
        {
            return 2;
        }

        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[1]), Json)
            ?? throw new InvalidDataException("Leere Anfrage");
        var settings = new WhisperSettings { Model = request.Model, ModelFolder = request.ModelFolder, Language = request.Language };
        var transcript = Task.Run(() => new WhisperTranscriber(settings, dtw: request.Dtw).TranscribeAsync(request.Wav)).GetAwaiter().GetResult();
        var tmp = args[2] + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(transcript.Words, Json));
        File.Move(tmp, args[2], overwrite: true);
        return 0;
    }

    /// <summary>Runs one transcription in a worker process. Null when the worker crashed or gave no result.</summary>
    internal static async Task<(Transcript? Transcript, int ExitCode)> RunProcessAsync(string exe, Request request, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("ucut-whisper-");
        try
        {
            var requestPath = Path.Combine(dir.FullName, "request.json");
            var resultPath = Path.Combine(dir.FullName, "result.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, Json), ct).ConfigureAwait(false);
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Argument);
            info.ArgumentList.Add(requestPath);
            info.ArgumentList.Add(resultPath);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Untertitel-Erkennung konnte nicht gestartet werden.");
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            if (process.ExitCode != 0 || !File.Exists(resultPath))
            {
                return (null, process.ExitCode);
            }

            var words = JsonSerializer.Deserialize<List<Word>>(await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false), Json) ?? [];
            return (new Transcript(words), 0);
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Whisper in a separate process; a crash costs only the subtitles of that one clip.</summary>
public sealed class IsolatedTranscriber(WhisperSettings settings, string workerExe, Action<string>? log = null) : ITranscriber
{
    public async Task<Transcript> TranscribeAsync(string wav16kMono, CancellationToken ct = default)
    {
        // Download the model here, so its progress shows up in the log.
        await new WhisperTranscriber(settings, log).EnsureModelAsync(ct).ConfigureAwait(false);
        foreach (var dtw in new[] { true, false })
        {
            var request = new WhisperWorker.Request(Path.GetFullPath(wav16kMono), settings.Model, settings.ModelFolder, settings.Language, dtw);
            Transcript? transcript;
            int code;
            try
            {
                (transcript, code) = await WhisperWorker.RunProcessAsync(workerExe, request, ct).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log?.Invoke($"  Eigener Prozess für die Untertitel ging nicht ({ex.Message}), sie laufen direkt in UCut.");
                return await new WhisperTranscriber(settings, log).TranscribeAsync(wav16kMono, ct).ConfigureAwait(false);
            }

            if (transcript is not null)
            {
                return transcript;
            }

            log?.Invoke(dtw
                ? $"  Untertitel-Erkennung ist abgestürzt (Code {code}), zweiter Versuch ohne genaue Wortzeiten ..."
                : $"  Untertitel-Erkennung ist wieder abgestürzt (Code {code}). Dieser Clip bekommt keine Untertitel, es geht mit dem nächsten weiter.");
        }

        return new Transcript([]);
    }
}
