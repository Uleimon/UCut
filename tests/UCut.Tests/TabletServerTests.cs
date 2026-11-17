using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UCut.Metadata;
using UCut.Settings;
using UCut.Tablet;

namespace UCut.Tests;

public class TabletServerTests
{
    private static (string Folder, string Video) Library()
    {
        var root = Directory.CreateTempSubdirectory("ucut-tab-").FullName;
        var game = Path.Combine(root, "GTA V");
        Directory.CreateDirectory(game);
        var video = Path.Combine(game, "2021-11-02_012037_zwei-muellen.mp4");
        File.WriteAllBytes(video, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(Path.ChangeExtension(video, ".thumb.jpg"), [0xFF, 0xD8, 0xFF]);
        var meta = new ClipMetadata { Video = Path.GetFileName(video), Game = "GTA V", DurationSeconds = 11, CreatedAt = DateTimeOffset.Now };
        meta.YouTube.Title = "Ich brauch das Geld JETZT!";
        meta.TikTok.Caption = "POV: Zombieapokalypse #gta5";
        meta.Save(Path.ChangeExtension(video, ".json"));
        return (root, video);
    }

    [Fact]
    public async Task Ohne_Code_gibt_es_nur_die_Seite_mit_Code_alles()
    {
        var (folder, video) = Library();
        var ct = TestContext.Current.CancellationToken;
        await using var server = new TabletServer(() => new UCutSettings { OutputFolder = folder });
        await server.StartAsync(0, "123456", discovery: false, ct);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        Assert.Contains("<title>UCut</title>", await http.GetStringAsync("/", ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/clips", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/login", new { code = "000000" }, ct)).StatusCode);

        http.DefaultRequestHeaders.Add("X-UCut-Code", "123456");
        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/clips", ct));
        var clip = doc.RootElement[0];
        var id = clip.GetProperty("id").GetString();
        Assert.Equal(TabletServer.ClipId(video), id);
        Assert.Equal("Ich brauch das Geld JETZT!", clip.GetProperty("title").GetString());
        Assert.Equal("POV: Zombieapokalypse #gta5", clip.GetProperty("tikTokCaption").GetString());
        Assert.Equal(0, clip.GetProperty("uploaded").GetArrayLength());

        // Seeking in the video needs ranges.
        using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/clips/{id}/video");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        var part = await http.SendAsync(range, ct);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i), await part.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal([0xFF, 0xD8, 0xFF], await http.GetByteArrayAsync($"/api/clips/{id}/thumb", ct));
    }

    [Fact]
    public async Task Texte_und_Haken_vom_Tablet_landen_in_der_Infodatei()
    {
        var (folder, video) = Library();
        var ct = TestContext.Current.CancellationToken;
        await using var server = new TabletServer(() => new UCutSettings { OutputFolder = folder });
        var changed = 0;
        server.ClipsChanged += () => Interlocked.Increment(ref changed);
        await server.StartAsync(0, "654321", discovery: false, ct);
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var login = await http.PostAsJsonAsync("/api/login", new { code = "654321" }, ct);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode} {await login.Content.ReadAsStringAsync(ct)}");

        var id = TabletServer.ClipId(video);
        var put = await http.PutAsJsonAsync($"/api/clips/{id}/texts", new { tikTokCaption = "Neu #dacity", youTubeTags = "#gta5 #shorts" }, ct);
        Assert.True(put.IsSuccessStatusCode, $"{put.StatusCode} {await put.Content.ReadAsStringAsync(ct)}");
        var mark = await http.PostAsJsonAsync($"/api/clips/{id}/uploaded", new { platform = "tiktok", on = true }, ct);
        Assert.True(mark.IsSuccessStatusCode, $"{mark.StatusCode} {await mark.Content.ReadAsStringAsync(ct)}");

        var meta = ClipMetadata.Load(Path.ChangeExtension(video, ".json"));
        Assert.Equal("Neu #dacity", meta.TikTok.Caption);
        Assert.Equal("Ich brauch das Geld JETZT!", meta.YouTube.Title);
        Assert.Equal(["gta5", "shorts"], meta.YouTube.Tags);
        Assert.Contains("TikTok: von Hand", meta.Uploaded.Keys);
        Assert.Equal(2, changed);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/clips/nichtda/uploaded", new { platform = "TikTok", on = true }, ct)).StatusCode);
    }

    [Fact]
    public async Task Die_App_findet_den_PC_per_Rundruf()
    {
        var ct = TestContext.Current.CancellationToken;
        using var udpPort = new UdpClient(0);
        var port = ((IPEndPoint)udpPort.Client.LocalEndPoint!).Port;
        udpPort.Dispose();
        using var discovery = new TabletDiscovery(port, () => "UCUT|PC|53790");
        using var client = new UdpClient(AddressFamily.InterNetwork);
        await client.SendAsync(Encoding.ASCII.GetBytes("UCUT?"), new IPEndPoint(IPAddress.Loopback, port), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(5000);
        var answer = await client.ReceiveAsync(timeout.Token);
        Assert.Equal("UCUT|PC|53790", Encoding.UTF8.GetString(answer.Buffer));
    }

    [Fact]
    public void Code_hat_sechs_Ziffern()
    {
        var code = TabletServer.NewCode();
        Assert.Matches("^[0-9]{6}$", code);
    }
}
