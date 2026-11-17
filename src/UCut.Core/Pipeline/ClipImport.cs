using UCut.Games;
using UCut.Settings;

namespace UCut.Pipeline;

/// <summary>Clips that Ulei loads into UCut by hand (command line or drag &amp; drop onto ucut.exe).</summary>
public static class ClipImport
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".mov"];

    /// <summary>Clips newer than this are treated as "just recorded": the current Twitch category/game applies to them.</summary>
    internal static readonly TimeSpan FreshClipAge = TimeSpan.FromMinutes(15);

    /// <summary>Expands folders (not recursive) and keeps only video files, in a stable order, without duplicates.</summary>
    public static List<string> Collect(IEnumerable<string> inputs, out List<string> missing)
    {
        missing = [];
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input).Where(IsVideo).Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(input) && IsVideo(input))
            {
                files.Add(input);
            }
            else
            {
                missing.Add(input);
            }
        }

        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !HasMp4Twin(f)).ToList();
    }

    /// <summary>
    /// OBS records an MKV and remuxes it to an MP4 with the same name: the same clip twice. Only the MP4 counts;
    /// the MKV (or MOV) next to it is left alone.
    /// </summary>
    public static bool HasMp4Twin(string path) =>
        !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.ChangeExtension(path, ".mp4"));

    /// <summary>Same clip, whatever the container: full path without the extension.</summary>
    public static string ClipKey(string path) => Path.ChangeExtension(Path.GetFullPath(path), null);

    /// <summary>
    /// For a fresh MKV/MOV: waits a little for OBS to write the MP4 next to it. True = an MP4 is there, skip this file.
    /// </summary>
    public static async Task<bool> WaitForMp4TwinAsync(string path, TimeSpan wait, CancellationToken ct = default)
    {
        if (string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (HasMp4Twin(path))
            {
                return true;
            }

            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// --game wins. A fresh clip gets the game that is running right now.
    /// An older clip would get the wrong game that way, so it goes to the "unsorted" folder.
    /// </summary>
    public static async Task<GameResult> ChooseGameAsync(string file, string? gameOverride, GameSettings settings, GameDetector detector, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(gameOverride))
        {
            return new GameResult(GameDetector.ToFolderName(gameOverride, settings), gameOverride, "von Hand");
        }

        if (now - File.GetLastWriteTime(file) <= FreshClipAge)
        {
            return await detector.DetectAsync().ConfigureAwait(false);
        }

        return new GameResult(settings.UnknownFolder, null, "älterer Clip, Spiel unbekannt (--game setzt es)");
    }
}
