using System.IO.Compression;

namespace UCut.App;

/// <summary>Where the downloaded programs ended up.</summary>
public sealed record InstalledTools(string? Ffmpeg, string? Ffprobe, string? YtDlp);

/// <summary>
/// Downloads ffmpeg/ffprobe and yt-dlp into %LOCALAPPDATA%\UCut\tools, so nobody has to install anything by hand.
/// No admin rights, no winget, no PATH change: UCut just points its settings at the files.
/// </summary>
public sealed class ToolInstaller(HttpClient http, string folder)
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public string Folder { get; } = folder;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCut", "tools");

    public string FfmpegExe => Path.Combine(Folder, "ffmpeg.exe");
    public string FfprobeExe => Path.Combine(Folder, "ffprobe.exe");
    public string YtDlpExe => Path.Combine(Folder, "yt-dlp.exe");

    /// <summary>Files already downloaded earlier (null where not there).</summary>
    public InstalledTools Existing() => new(
        File.Exists(FfmpegExe) ? FfmpegExe : null,
        File.Exists(FfprobeExe) ? FfprobeExe : null,
        File.Exists(YtDlpExe) ? YtDlpExe : null);

    public async Task<InstalledTools> InstallAsync(bool ffmpeg, bool ytDlp, IProgress<(string Step, double Share)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        if (ytDlp)
        {
            await DownloadAsync(YtDlpUrl, YtDlpExe, "yt-dlp wird heruntergeladen", progress, ct).ConfigureAwait(false);
        }

        if (ffmpeg)
        {
            var zip = Path.Combine(Folder, "ffmpeg-download.zip");
            try
            {
                await DownloadAsync(FfmpegUrl, zip, "ffmpeg wird heruntergeladen", progress, ct).ConfigureAwait(false);
                progress?.Report(("ffmpeg wird entpackt", 1));
                ExtractFfmpeg(zip, Folder);
            }
            finally
            {
                File.Delete(zip);
            }
        }

        return Existing();
    }

    /// <summary>Takes bin\ffmpeg.exe and bin\ffprobe.exe out of the build zip, wherever they sit in it.</summary>
    internal static void ExtractFfmpeg(string zipPath, string folder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var found = 0;
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            var target = Path.Combine(folder, name);
            var tmp = target + ".part";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            found++;
        }

        if (found < 2)
        {
            throw new InvalidDataException("Im ffmpeg-Download fehlen ffmpeg.exe oder ffprobe.exe.");
        }
    }

    private async Task DownloadAsync(string url, string target, string step, IProgress<(string Step, double Share)>? progress, CancellationToken ct)
    {
        progress?.Report((step, 0));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        var tmp = target + ".part";
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long done = 0;
            var lastReport = 0.0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                var share = total > 0 ? (double)done / total : 0;
                if (share - lastReport >= 0.01)
                {
                    lastReport = share;
                    progress?.Report((step, share));
                }
            }
        }

        File.Move(tmp, target, overwrite: true);
    }
}
