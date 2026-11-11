using System.IO.Compression;
using System.Net;
using UCut.App;

namespace UCut.Tests;

public class ToolInstallerTests
{
    private sealed class Files(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] FfmpegZip(bool withProbe = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }

            Add("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", "FFMPEG");
            if (withProbe)
            {
                Add("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", "FFPROBE");
            }

            Add("ffmpeg-master-latest-win64-gpl/doc/readme.txt", "x");
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task Laedt_ffmpeg_und_yt_dlp_in_den_eigenen_Ordner()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-tools-").FullName;
        var api = new Files(new()
        {
            [ToolInstaller.YtDlpUrl] = "YTDLP"u8.ToArray(),
            [ToolInstaller.FfmpegUrl] = FfmpegZip(),
        });
        var installer = new ToolInstaller(new HttpClient(api), dir);
        Assert.Equal(new InstalledTools(null, null, null), installer.Existing());

        var steps = new List<string>();
        var result = await installer.InstallAsync(true, true, new SyncProgress<(string Step, double Share)>(p => steps.Add(p.Step)));

        Assert.Equal(Path.Combine(dir, "ffmpeg.exe"), result.Ffmpeg);
        Assert.Equal(Path.Combine(dir, "ffprobe.exe"), result.Ffprobe);
        Assert.Equal(Path.Combine(dir, "yt-dlp.exe"), result.YtDlp);
        Assert.Equal("FFMPEG", File.ReadAllText(result.Ffmpeg!));
        Assert.Equal("FFPROBE", File.ReadAllText(result.Ffprobe!));
        Assert.Equal("YTDLP", File.ReadAllText(result.YtDlp!));
        Assert.False(File.Exists(Path.Combine(dir, "ffmpeg-download.zip")));
        Assert.Contains("ffmpeg wird entpackt", steps);
    }

    [Fact]
    public async Task Nur_was_fehlt_wird_geladen()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-tools-").FullName;
        var api = new Files(new() { [ToolInstaller.YtDlpUrl] = "YTDLP"u8.ToArray() });
        var result = await new ToolInstaller(new HttpClient(api), dir).InstallAsync(false, true);

        Assert.Equal([ToolInstaller.YtDlpUrl], api.Asked);
        Assert.Null(result.Ffmpeg);
        Assert.NotNull(result.YtDlp);
    }

    [Fact]
    public async Task Kaputter_ffmpeg_Download_gibt_eine_klare_Meldung()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-tools-").FullName;
        var api = new Files(new() { [ToolInstaller.FfmpegUrl] = FfmpegZip(withProbe: false) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ToolInstaller(new HttpClient(api), dir).InstallAsync(true, false));
        Assert.False(File.Exists(Path.Combine(dir, "ffmpeg-download.zip")));
    }

    [Fact]
    public async Task Fehlende_Datei_auf_dem_Server_bricht_ab()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-tools-").FullName;
        await Assert.ThrowsAsync<HttpRequestException>(() => new ToolInstaller(new HttpClient(new Files([])), dir).InstallAsync(false, true));
        Assert.Null(new ToolInstaller(new HttpClient(new Files([])), dir).Existing().YtDlp);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
