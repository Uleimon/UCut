using System.Globalization;
using UCut.Media;

namespace UCut.Twitch;

/// <summary>
/// Downloads a Twitch clip with yt-dlp (same idea as ffmpeg: an external tool, installed once).
/// The Helix API lists clips but has no plain download link for app tokens.
/// </summary>
public sealed class ClipDownloader(string ytDlpPath)
{
    public async Task<string> DownloadAsync(TwitchClip clip, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(clip));
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        await ProcessRunner.RunCheckedAsync(ytDlpPath, Arguments(clip.Url, path), null, ct).ConfigureAwait(false);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"yt-dlp hat keine Datei für {clip.Url} geschrieben.");
        }

        return path;
    }

    internal static string FileName(TwitchClip clip)
    {
        var safeId = new string(clip.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        return $"{clip.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}_{safeId}.mp4";
    }

    internal static IReadOnlyList<string> Arguments(string url, string output) =>
    [
        "--no-progress", "--no-warnings", "--no-playlist",
        "-f", "best[ext=mp4]/best",
        "--remux-video", "mp4",
        "-o", output,
        url,
    ];
}
