using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace UCut.Upload;

/// <summary>What the uploaders need from the program during sign-in.</summary>
public interface IAuthUi
{
    /// <summary>Opens the sign-in page in the browser.</summary>
    void OpenBrowser(string url);

    /// <summary>Asks the user to paste something (Instagram: the address the browser ended on). Null = cancelled.</summary>
    Task<string?> AskAsync(string title, string message);
}

/// <summary>PKCE (RFC 7636) values. TikTok wants the challenge as hex, Google as base64url.</summary>
public static class Pkce
{
    public static string NewVerifier()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        return RandomNumberGenerator.GetString(chars, 64);
    }

    public static string NewState() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string ChallengeBase64Url(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string ChallengeHex(string verifier) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>
/// Receives the sign-in answer on http://127.0.0.1:PORT/callback/ (the "loopback" way for desktop programs).
/// The redirect address must be registered in the developer app exactly like that.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly HttpListener _listener = new();

    public LoopbackReceiver(int port)
    {
        RedirectUri = $"http://127.0.0.1:{port}/callback/";
        _listener.Prefixes.Add(RedirectUri);
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"Port {port} ist belegt oder gesperrt, die Anmeldung kann die Antwort nicht empfangen ({ex.Message}).", ex);
        }
    }

    public string RedirectUri { get; }

    /// <summary>Waits for the browser to come back and returns the query parameters (code, state, error).</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            var ctxTask = _listener.GetContextAsync();
            var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
            if (done != ctxTask)
            {
                throw new OperationCanceledException("Die Anmeldung im Browser wurde nicht abgeschlossen.");
            }

            var ctx = await ctxTask.ConfigureAwait(false);
            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            var result = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k] ?? "");
            var ok = result.ContainsKey("code");
            var html = ok
                ? "<h2>UCut: Anmeldung geklappt.</h2><p>Du kannst dieses Fenster schließen.</p>"
                : $"<h2>UCut: Anmeldung abgebrochen.</h2><p>{WebUtility.HtmlEncode(result.GetValueOrDefault("error_description") ?? result.GetValueOrDefault("error") ?? "")}</p>";
            var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><body style=\"font-family:Segoe UI,sans-serif;background:#0d0e12;color:#eee;padding:40px\">{html}</body>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            ctx.Response.Close();
            if (ok || result.ContainsKey("error"))
            {
                return result;
            }

            // Something else (favicon): keep waiting.
        }
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();
}

internal static class HttpJson
{
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{what}: {(int)response.StatusCode} {Short(body)}");
        }

        try
        {
            return body.Length == 0 ? default : JsonElement.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{what}: unerwartete Antwort {Short(body)}");
        }
    }

    public static async Task<JsonElement> PostFormAsync(HttpClient http, string url, IEnumerable<KeyValuePair<string, string>> form, string what, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);
        return await ReadAsync(response, what, ct).ConfigureAwait(false);
    }

    public static async Task<JsonElement> SendAsync(HttpClient http, HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, what, ct).ConfigureAwait(false);
    }

    public static JsonContent Json(object value) => JsonContent.Create(value);

    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""
            : "";

    public static long Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public static JsonElement Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string Short(string body) => body.Length > 400 ? body[..400] : body;
}

/// <summary>Stream wrapper that reports how many bytes were read (upload progress).</summary>
internal sealed class ProgressStream(Stream inner, long total, IProgress<double>? progress) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    private int Count(int n)
    {
        _read += n;
        progress?.Report(total == 0 ? 1 : Math.Min(1, (double)_read / total));
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
