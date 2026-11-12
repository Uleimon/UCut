using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UCut.Library;
using UCut.Settings;

namespace UCut.Tablet;

/// <summary>One clip as the tablet sees it.</summary>
public sealed record TabletClip(
    string Id, string Title, string Game, DateTimeOffset Date, double Duration, int? Funny, string? FunnyReason, int Score,
    IReadOnlyList<string> Uploaded, string TikTokCaption, string YouTubeTitle, string YouTubeDescription, string YouTubeTags,
    string InstagramCaption, long Size);

/// <summary>Texts sent back by the tablet; null = unchanged.</summary>
public sealed record TabletTexts(string? TikTokCaption, string? YouTubeTitle, string? YouTubeDescription, string? YouTubeTags, string? InstagramCaption);

public sealed record TabletMark(string Platform, bool On);

/// <summary>
/// Shares the clip library in the home network for the tablet companion app (and any browser):
/// list, thumbnails, video (with seeking), edit texts, tick "hochgeladen". Everything needs the 6-digit code.
/// </summary>
public sealed class TabletServer(Func<UCutSettings> settings, TimeProvider? time = null) : IAsyncDisposable
{
    public const string CookieName = "ucut";
    public const int DiscoveryPort = 53791;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private WebApplication? _app;
    private TabletDiscovery? _discovery;
    private string _code = "";

    /// <summary>Raised (on a worker thread) after the tablet changed a clip.</summary>
    public event Action? ClipsChanged;

    public bool Running => _app is not null;
    public string Code => _code;
    public int Port { get; private set; }

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stable short id of a clip (from its path).</summary>
    public static string ClipId(string videoPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(videoPath).ToUpperInvariant())))[..16].ToLowerInvariant();

    /// <summary>The PC's addresses in the home network (what the tablet has to reach).</summary>
    public static List<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <param name="port">0 = any free port (tests).</param>
    /// <param name="discovery">Answer the app's "where is UCut?" broadcast.</param>
    public async Task StartAsync(int port, string code, bool discovery = true, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _code = code;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "UCut.Tablet" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, port));
        var app = builder.Build();
        app.Use(Guard);
        Map(app);
        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
        Port = new Uri(app.Urls.First().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)).Port;
        if (discovery)
        {
            try
            {
                _discovery = new TabletDiscovery(DiscoveryPort, () => $"UCUT|{Environment.MachineName}|{Port}");
            }
            catch (SocketException)
            {
                // Port taken: the app can still connect with the typed-in address.
            }
        }
    }

    public async Task StopAsync()
    {
        _discovery?.Dispose();
        _discovery = null;
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>The page is open for everyone (it asks for the code); data only with the code (cookie, header or ?code=).</summary>
    private async Task Guard(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path is "/" or "/index.html" or "/api/hello" or "/api/login")
        {
            if (ctx.Request.Query.TryGetValue("code", out var q) && CodeOk(q.ToString()))
            {
                SetCookie(ctx);
            }

            await next().ConfigureAwait(false);
            return;
        }

        var given = ctx.Request.Cookies[CookieName] ?? ctx.Request.Headers["X-UCut-Code"].ToString();
        if (string.IsNullOrEmpty(given) && ctx.Request.Query.TryGetValue("code", out var c))
        {
            given = c.ToString();
        }

        if (!CodeOk(given))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Falscher Code").ConfigureAwait(false);
            return;
        }

        await next().ConfigureAwait(false);
    }

    private bool CodeOk(string? given) =>
        _code.Length > 0 && given is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given.Trim()), Encoding.UTF8.GetBytes(_code));

    private static void SetCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Append(CookieName, ctx.Request.Query["code"].ToString().Trim(),
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(3650) });

    private void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(Page(), "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => Results.Content(Page(), "text/html; charset=utf-8"));
        app.MapGet("/api/hello", (HttpContext ctx) => new { name = "UCut", pc = Environment.MachineName, version = Version(), loggedIn = CodeOk(ctx.Request.Cookies[CookieName]) || CodeOk(ctx.Request.Query["code"].ToString()) });
        app.MapPost("/api/login", (HttpContext ctx, LoginRequest r) =>
        {
            if (!CodeOk(r.Code))
            {
                return Results.Unauthorized();
            }

            ctx.Response.Cookies.Append(CookieName, r.Code.Trim(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(3650) });
            return Results.Ok();
        });

        app.MapGet("/api/clips", () => Clips().Select(ToDto));
        app.MapGet("/api/clips/{id}/video", (string id) =>
            Find(id) is { } c ? Results.File(c.VideoPath, "video/mp4", Path.GetFileName(c.VideoPath), enableRangeProcessing: true) : Results.NotFound());
        app.MapGet("/api/clips/{id}/thumb", async (string id, CancellationToken ct) =>
        {
            if (Find(id) is not { } c)
            {
                return Results.NotFound();
            }

            var thumb = File.Exists(c.ThumbnailPath) ? c.ThumbnailPath : await ClipLibrary.EnsureThumbnailAsync(c, settings().FfmpegPath, ct).ConfigureAwait(false);
            return thumb is not null && File.Exists(thumb) ? Results.File(thumb, "image/jpeg") : Results.NotFound();
        });
        app.MapPut("/api/clips/{id}/texts", (string id, TabletTexts t) =>
        {
            if (Find(id) is not { } c)
            {
                return Results.NotFound();
            }

            var m = c.Metadata;
            m.TikTok.Caption = t.TikTokCaption ?? m.TikTok.Caption;
            m.YouTube.Title = t.YouTubeTitle ?? m.YouTube.Title;
            m.YouTube.Description = t.YouTubeDescription ?? m.YouTube.Description;
            if (t.YouTubeTags is { } tags)
            {
                m.YouTube.Tags = tags.Split([' ', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.TrimStart('#')).Where(x => x.Length > 0).ToList();
            }

            m.Instagram.Caption = t.InstagramCaption ?? m.Instagram.Caption;
            c.Save();
            ClipsChanged?.Invoke();
            return Results.Ok(ToDto(c));
        });
        app.MapPost("/api/clips/{id}/uploaded", (string id, TabletMark mark) =>
        {
            if (Find(id) is not { } c || !UploadMarks.Platforms.Contains(mark.Platform, StringComparer.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var platform = UploadMarks.Platforms.First(p => p.Equals(mark.Platform, StringComparison.OrdinalIgnoreCase));
            if (UploadMarks.Set(c.Metadata, platform, mark.On, _time.GetLocalNow().DateTime))
            {
                c.Save();
                ClipsChanged?.Invoke();
            }

            return Results.Ok(ToDto(c));
        });
    }

    private List<LibraryClip> Clips() => ClipLibrary.Load(settings().OutputFolder);

    private LibraryClip? Find(string id) => Clips().FirstOrDefault(c => ClipId(c.VideoPath) == id);

    private static TabletClip ToDto(LibraryClip c) => new(
        ClipId(c.VideoPath), c.Title, c.Metadata.Game, c.Date, c.Metadata.DurationSeconds, c.Metadata.Funniness?.Score, c.Metadata.Funniness?.Reason,
        c.Metadata.Score.Total, UploadMarks.Platforms.Where(p => UploadMarks.Has(c.Metadata, p)).ToList(),
        c.Metadata.TikTok.Caption, c.Metadata.YouTube.Title, c.Metadata.YouTube.Description,
        string.Join(" ", c.Metadata.YouTube.Tags.Select(t => "#" + t.TrimStart('#'))), c.Metadata.Instagram.Caption, FileSize(c.VideoPath));

    private static long FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Version() => typeof(TabletServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    private static string? _page;

    private static string Page()
    {
        if (_page is null)
        {
            using var s = typeof(TabletServer).Assembly.GetManifestResourceStream("UCut.Tablet.tablet.html")!;
            using var r = new StreamReader(s);
            _page = r.ReadToEnd();
        }

        return _page;
    }

    private sealed record LoginRequest(string Code);
}

/// <summary>Answers the app's UDP broadcast "UCUT?" with "UCUT|pc name|port".</summary>
internal sealed class TabletDiscovery : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();

    public TabletDiscovery(int port, Func<string> answer)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _ = Task.Run(() => LoopAsync(answer));
    }

    private async Task LoopAsync(Func<string> answer)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(r.Buffer).StartsWith("UCUT?", StringComparison.Ordinal))
                {
                    var bytes = Encoding.UTF8.GetBytes(answer());
                    await _udp.SendAsync(bytes, r.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // One bad packet or a closed socket: keep listening unless disposed.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
