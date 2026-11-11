using System.Net.Http.Headers;
using System.Text.Json;
using UCut.Settings;

namespace UCut.Upload;

/// <summary>What to post. Each platform takes the fields it uses.</summary>
public sealed record UploadRequest(string VideoPath, string Title, string Description, IReadOnlyList<string> Tags, string Caption);

/// <summary>Result of one upload. Url is empty when the platform gives none (TikTok drafts).</summary>
public sealed record UploadResult(string Id, string Url, string Note);

public interface IPlatformUploader
{
    UploadPlatform Platform { get; }
    bool IsConfigured { get; }

    /// <summary>Sign-in in the browser; returns the new account with tokens.</summary>
    Task<UploadAccount> ConnectAsync(IAuthUi ui, CancellationToken ct);

    /// <summary>Uploads the clip. Refreshes the token first if needed (the account object is updated; save the store afterwards).</summary>
    Task<UploadResult> UploadAsync(UploadAccount account, UploadRequest req, IProgress<double>? progress, CancellationToken ct);
}

internal static class UploaderHelpers
{
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public static string CheckState(IReadOnlyDictionary<string, string> answer, string state, string platform)
    {
        if (answer.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException($"{platform}: Anmeldung abgelehnt ({answer.GetValueOrDefault("error_description") ?? error}).");
        }

        if (answer.GetValueOrDefault("state") != state)
        {
            throw new InvalidOperationException($"{platform}: Antwort der Anmeldung passt nicht (state).");
        }

        return answer["code"];
    }

    public static async Task WaitAsync(TimeSpan delay, CancellationToken ct) => await Task.Delay(delay, ct).ConfigureAwait(false);
}

/// <summary>YouTube Data API v3: Google sign-in (loopback + PKCE) and resumable upload.</summary>
public sealed class YouTubeUploader(UploadSettings settings, HttpClient http) : IPlatformUploader
{
    internal const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    internal const string TokenUrl = "https://oauth2.googleapis.com/token";
    internal const string ApiUrl = "https://www.googleapis.com/youtube/v3/";
    internal const string UploadUrl = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
    private const string Scopes = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    public UploadPlatform Platform => UploadPlatform.YouTube;
    public bool IsConfigured => settings.YouTubeClientId.Length > 0 && settings.YouTubeClientSecret.Length > 0;

    public async Task<UploadAccount> ConnectAsync(IAuthUi ui, CancellationToken ct)
    {
        using var receiver = new LoopbackReceiver(settings.CallbackPort);
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        ui.OpenBrowser(AuthUrl + "?" + Query(
            ("client_id", settings.YouTubeClientId), ("redirect_uri", receiver.RedirectUri), ("response_type", "code"),
            ("scope", Scopes), ("access_type", "offline"), ("prompt", "consent select_account"), ("state", state),
            ("code_challenge", Pkce.ChallengeBase64Url(verifier)), ("code_challenge_method", "S256")));
        var code = UploaderHelpers.CheckState(await receiver.WaitAsync(UploaderHelpers.LoginTimeout, ct).ConfigureAwait(false), state, "YouTube");

        var token = await HttpJson.PostFormAsync(http, TokenUrl,
        [
            new("code", code), new("client_id", settings.YouTubeClientId), new("client_secret", settings.YouTubeClientSecret),
            new("redirect_uri", receiver.RedirectUri), new("grant_type", "authorization_code"), new("code_verifier", verifier),
        ], "YouTube-Anmeldung", ct).ConfigureAwait(false);
        var account = new UploadAccount { Platform = UploadPlatform.YouTube };
        ApplyToken(account, token);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiUrl + "channels?part=snippet&mine=true"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var channels = await HttpJson.SendAsync(http, request, "YouTube-Kanal", ct).ConfigureAwait(false);
        var items = channels.Obj("items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Dieses Google-Konto hat keinen YouTube-Kanal.");
        }

        account.UserId = items[0].Str("id");
        account.Name = items[0].Obj("snippet").Str("title");
        return account;
    }

    public async Task<UploadResult> UploadAsync(UploadAccount account, UploadRequest req, IProgress<double>? progress, CancellationToken ct)
    {
        await EnsureTokenAsync(account, ct).ConfigureAwait(false);
        var size = new FileInfo(req.VideoPath).Length;
        var title = req.Title.Length > 100 ? req.Title[..100] : req.Title;
        var meta = new
        {
            snippet = new { title, description = req.Description, tags = req.Tags, categoryId = settings.YouTubeCategory },
            status = new { privacyStatus = settings.YouTubePrivacy, selfDeclaredMadeForKids = false },
        };

        using var start = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadUrl)) { Content = HttpJson.Json(meta) };
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        start.Headers.Add("X-Upload-Content-Type", "video/mp4");
        start.Headers.Add("X-Upload-Content-Length", size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var started = await http.SendAsync(start, ct).ConfigureAwait(false);
        if (!started.IsSuccessStatusCode || started.Headers.Location is null)
        {
            await HttpJson.ReadAsync(started, "YouTube-Upload starten", ct).ConfigureAwait(false);
            throw new InvalidOperationException("YouTube hat keine Upload-Adresse geschickt.");
        }

        using var file = new ProgressStream(File.OpenRead(req.VideoPath), size, progress);
        using var put = new HttpRequestMessage(HttpMethod.Put, started.Headers.Location) { Content = new StreamContent(file) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        put.Content.Headers.ContentLength = size;
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var video = await HttpJson.SendAsync(http, put, "YouTube-Upload", ct).ConfigureAwait(false);
        var id = video.Str("id");
        var note = settings.YouTubePrivacy == "private"
            ? "privat hochgeladen, in YouTube Studio auf öffentlich stellen"
            : $"hochgeladen ({settings.YouTubePrivacy})";
        return new UploadResult(id, $"https://youtube.com/shorts/{id}", note);
    }

    private async Task EnsureTokenAsync(UploadAccount account, CancellationToken ct)
    {
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return;
        }

        if (account.RefreshToken.Length == 0)
        {
            throw new InvalidOperationException($"YouTube-Konto {account.Name} muss neu verbunden werden.");
        }

        var token = await HttpJson.PostFormAsync(http, TokenUrl,
        [
            new("client_id", settings.YouTubeClientId), new("client_secret", settings.YouTubeClientSecret),
            new("refresh_token", account.RefreshToken), new("grant_type", "refresh_token"),
        ], $"YouTube-Konto {account.Name} (Anmeldung abgelaufen? neu verbinden)", ct).ConfigureAwait(false);
        ApplyToken(account, token);
    }

    private static void ApplyToken(UploadAccount account, JsonElement token)
    {
        account.AccessToken = token.Str("access_token");
        var refresh = token.Str("refresh_token");
        if (refresh.Length > 0)
        {
            account.RefreshToken = refresh;
        }

        account.IssuedAt = DateTimeOffset.UtcNow;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.Num("expires_in")));
    }

    internal static string Query(params (string Key, string Value)[] items) =>
        string.Join("&", items.Select(i => $"{Uri.EscapeDataString(i.Key)}={Uri.EscapeDataString(i.Value)}"));
}

/// <summary>TikTok Content Posting API: Login Kit for desktop (loopback + PKCE hex) and FILE_UPLOAD in chunks.</summary>
public sealed class TikTokUploader(UploadSettings settings, HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null) : IPlatformUploader
{
    internal const string AuthUrl = "https://www.tiktok.com/v2/auth/authorize/";
    internal const string ApiUrl = "https://open.tiktokapis.com/v2/";
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? UploaderHelpers.WaitAsync;

    public UploadPlatform Platform => UploadPlatform.TikTok;
    public bool IsConfigured => settings.TikTokClientKey.Length > 0 && settings.TikTokClientSecret.Length > 0;
    private bool Direct => string.Equals(settings.TikTokMode, "direct", StringComparison.OrdinalIgnoreCase);

    public async Task<UploadAccount> ConnectAsync(IAuthUi ui, CancellationToken ct)
    {
        using var receiver = new LoopbackReceiver(settings.CallbackPort);
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        var scope = Direct ? "user.info.basic,video.upload,video.publish" : "user.info.basic,video.upload";
        ui.OpenBrowser(AuthUrl + "?" + YouTubeUploader.Query(
            ("client_key", settings.TikTokClientKey), ("scope", scope), ("response_type", "code"),
            ("redirect_uri", receiver.RedirectUri), ("state", state),
            ("code_challenge", Pkce.ChallengeHex(verifier)), ("code_challenge_method", "S256")));
        var code = UploaderHelpers.CheckState(await receiver.WaitAsync(UploaderHelpers.LoginTimeout, ct).ConfigureAwait(false), state, "TikTok");

        var token = await HttpJson.PostFormAsync(http, ApiUrl + "oauth/token/",
        [
            new("client_key", settings.TikTokClientKey), new("client_secret", settings.TikTokClientSecret), new("code", code),
            new("grant_type", "authorization_code"), new("redirect_uri", receiver.RedirectUri), new("code_verifier", verifier),
        ], "TikTok-Anmeldung", ct).ConfigureAwait(false);
        var account = new UploadAccount { Platform = UploadPlatform.TikTok };
        ApplyToken(account, token);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiUrl + "user/info/?fields=open_id,display_name"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var info = await HttpJson.SendAsync(http, request, "TikTok-Profil", ct).ConfigureAwait(false);
        var user = info.Obj("data").Obj("user");
        account.Name = user.Str("display_name");
        if (account.Name.Length == 0)
        {
            account.Name = "TikTok-Konto";
        }

        return account;
    }

    public async Task<UploadResult> UploadAsync(UploadAccount account, UploadRequest req, IProgress<double>? progress, CancellationToken ct)
    {
        await EnsureTokenAsync(account, ct).ConfigureAwait(false);
        var size = new FileInfo(req.VideoPath).Length;
        var (chunkSize, chunks) = Chunks(size);
        var source = new { source = "FILE_UPLOAD", video_size = size, chunk_size = chunkSize, total_chunk_count = chunks };

        object body;
        string path;
        if (Direct)
        {
            body = new
            {
                post_info = new { title = req.Caption, privacy_level = await PrivacyAsync(account, ct).ConfigureAwait(false), disable_comment = false, disable_duet = false, disable_stitch = false },
                source_info = source,
            };
            path = "post/publish/video/init/";
        }
        else
        {
            body = new { source_info = source };
            path = "post/publish/inbox/video/init/";
        }

        var init = await Api(account, path, body, "TikTok-Upload starten", ct).ConfigureAwait(false);
        var uploadUrl = init.Obj("data").Str("upload_url");
        var publishId = init.Obj("data").Str("publish_id");

        await using (var file = File.OpenRead(req.VideoPath))
        {
            for (var i = 0; i < chunks; i++)
            {
                var first = i * chunkSize;
                var length = i == chunks - 1 ? size - first : chunkSize;
                var buffer = new byte[length];
                await file.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
                using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(uploadUrl)) { Content = new ByteArrayContent(buffer) };
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                put.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, size);
                using var response = await http.SendAsync(put, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await HttpJson.ReadAsync(response, $"TikTok-Upload Teil {i + 1}/{chunks}", ct).ConfigureAwait(false);
                }

                progress?.Report((double)(first + length) / size);
            }
        }

        // TikTok processes the video; wait until it is in the inbox or published.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var status = await Api(account, "post/publish/status/fetch/", new { publish_id = publishId }, "TikTok-Status", ct).ConfigureAwait(false);
            var data = status.Obj("data");
            switch (data.Str("status"))
            {
                case "SEND_TO_USER_INBOX":
                    return new UploadResult(publishId, "", "als Entwurf in deiner TikTok-App, dort posten");
                case "PUBLISH_COMPLETE":
                    return new UploadResult(publishId, "", "auf TikTok gepostet");
                case "FAILED":
                    throw new InvalidOperationException($"TikTok hat den Clip abgelehnt: {data.Str("fail_reason")}");
            }

            await _delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        return new UploadResult(publishId, "", "hochgeladen, TikTok verarbeitet noch");
    }

    /// <summary>TikTok rules: one piece up to 64 MB; above that 10 MB pieces, the last one takes the rest.</summary>
    internal static (long ChunkSize, int Count) Chunks(long size)
    {
        const long max = 64L * 1024 * 1024;
        const long piece = 10L * 1024 * 1024;
        return size <= max ? (size, 1) : (piece, (int)(size / piece));
    }

    private async Task<string> PrivacyAsync(UploadAccount account, CancellationToken ct)
    {
        var info = await Api(account, "post/publish/creator_info/query/", new { }, "TikTok-Kontoinfo", ct).ConfigureAwait(false);
        var options = info.Obj("data").Obj("privacy_level_options");
        var list = options.ValueKind == JsonValueKind.Array ? options.EnumerateArray().Select(o => o.GetString() ?? "").ToList() : [];
        return list.Contains("PUBLIC_TO_EVERYONE") ? "PUBLIC_TO_EVERYONE" : list.Contains("SELF_ONLY") ? "SELF_ONLY" : list.FirstOrDefault() ?? "SELF_ONLY";
    }

    private async Task<JsonElement> Api(UploadAccount account, string path, object body, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiUrl + path)) { Content = HttpJson.Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var json = await HttpJson.SendAsync(http, request, what, ct).ConfigureAwait(false);
        var error = json.Obj("error");
        var code = error.Str("code");
        if (code.Length > 0 && code != "ok")
        {
            throw new InvalidOperationException($"{what}: {code} {error.Str("message")}");
        }

        return json;
    }

    private async Task EnsureTokenAsync(UploadAccount account, CancellationToken ct)
    {
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return;
        }

        var token = await HttpJson.PostFormAsync(http, ApiUrl + "oauth/token/",
        [
            new("client_key", settings.TikTokClientKey), new("client_secret", settings.TikTokClientSecret),
            new("grant_type", "refresh_token"), new("refresh_token", account.RefreshToken),
        ], $"TikTok-Konto {account.Name} (Anmeldung abgelaufen? neu verbinden)", ct).ConfigureAwait(false);
        ApplyToken(account, token);
    }

    private static void ApplyToken(UploadAccount account, JsonElement token)
    {
        if (token.Str("access_token").Length == 0)
        {
            throw new InvalidOperationException($"TikTok-Anmeldung: {token.Str("error")} {token.Str("error_description")}");
        }

        account.AccessToken = token.Str("access_token");
        account.RefreshToken = token.Str("refresh_token") is { Length: > 0 } r ? r : account.RefreshToken;
        account.UserId = token.Str("open_id") is { Length: > 0 } id ? id : account.UserId;
        account.IssuedAt = DateTimeOffset.UtcNow;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.Num("expires_in")));
    }
}

/// <summary>
/// Instagram API with Instagram Login (professional accounts): Reels via resumable upload.
/// Instagram only allows https redirect addresses, so the user pastes the address the browser ends on.
/// </summary>
public sealed class InstagramUploader(UploadSettings settings, HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null) : IPlatformUploader
{
    internal const string AuthUrl = "https://www.instagram.com/oauth/authorize";
    internal const string TokenUrl = "https://api.instagram.com/oauth/access_token";
    internal const string GraphUrl = "https://graph.instagram.com/";
    internal const string RuploadUrl = "https://rupload.facebook.com/ig-api-upload/";
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? UploaderHelpers.WaitAsync;

    public UploadPlatform Platform => UploadPlatform.Instagram;
    public bool IsConfigured => settings.InstagramAppId.Length > 0 && settings.InstagramAppSecret.Length > 0;
    private string V => settings.GraphApiVersion;

    public async Task<UploadAccount> ConnectAsync(IAuthUi ui, CancellationToken ct)
    {
        var state = Pkce.NewState();
        ui.OpenBrowser(AuthUrl + "?" + YouTubeUploader.Query(
            ("client_id", settings.InstagramAppId), ("redirect_uri", settings.InstagramRedirectUri), ("response_type", "code"),
            ("scope", "instagram_business_basic,instagram_business_content_publish"), ("state", state)));
        var pasted = await ui.AskAsync("Instagram verbinden",
            "Melde dich im Browser bei Instagram an und erlaube den Zugriff. Danach zeigt der Browser eine Fehlerseite (das ist normal). " +
            "Kopiere die komplette Adresse aus der Adresszeile und füge sie hier ein.").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pasted))
        {
            throw new OperationCanceledException("Instagram-Anmeldung abgebrochen.");
        }

        var code = ParseCode(pasted, state);
        var shortToken = await HttpJson.PostFormAsync(http, TokenUrl,
        [
            new("client_id", settings.InstagramAppId), new("client_secret", settings.InstagramAppSecret),
            new("grant_type", "authorization_code"), new("redirect_uri", settings.InstagramRedirectUri), new("code", code),
        ], "Instagram-Anmeldung", ct).ConfigureAwait(false);
        var first = shortToken.Obj("data") is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 ? arr[0] : shortToken;

        var longToken = await Get(GraphUrl + "access_token?" + YouTubeUploader.Query(
            ("grant_type", "ig_exchange_token"), ("client_secret", settings.InstagramAppSecret), ("access_token", first.Str("access_token"))),
            "Instagram-Anmeldung (Langzeit-Token)", ct).ConfigureAwait(false);
        var account = new UploadAccount { Platform = UploadPlatform.Instagram };
        ApplyToken(account, longToken);

        var me = await Get($"{GraphUrl}{V}/me?fields=user_id,username&access_token={Uri.EscapeDataString(account.AccessToken)}", "Instagram-Profil", ct).ConfigureAwait(false);
        account.UserId = me.Str("user_id") is { Length: > 0 } uid ? uid : first.Str("user_id");
        account.Name = me.Str("username");
        return account;
    }

    public async Task<UploadResult> UploadAsync(UploadAccount account, UploadRequest req, IProgress<double>? progress, CancellationToken ct)
    {
        await EnsureTokenAsync(account, ct).ConfigureAwait(false);
        var container = await HttpJson.PostFormAsync(http, $"{GraphUrl}{V}/{account.UserId}/media",
        [
            new("media_type", "REELS"), new("upload_type", "resumable"), new("caption", req.Caption), new("access_token", account.AccessToken),
        ], "Instagram-Reel anlegen", ct).ConfigureAwait(false);
        var containerId = container.Str("id");

        var size = new FileInfo(req.VideoPath).Length;
        using (var file = new ProgressStream(File.OpenRead(req.VideoPath), size, progress))
        using (var upload = new HttpRequestMessage(HttpMethod.Post, new Uri($"{RuploadUrl}{V}/{containerId}")) { Content = new StreamContent(file) })
        {
            upload.Headers.TryAddWithoutValidation("Authorization", "OAuth " + account.AccessToken);
            upload.Headers.Add("offset", "0");
            upload.Headers.Add("file_size", size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            upload.Content.Headers.ContentLength = size;
            await HttpJson.SendAsync(http, upload, "Instagram-Upload", ct).ConfigureAwait(false);
        }

        for (var attempt = 0; ; attempt++)
        {
            var status = await Get($"{GraphUrl}{V}/{containerId}?fields=status_code,status&access_token={Uri.EscapeDataString(account.AccessToken)}", "Instagram-Status", ct).ConfigureAwait(false);
            var code = status.Str("status_code");
            if (code == "FINISHED")
            {
                break;
            }

            if (code is "ERROR" or "EXPIRED" || attempt >= 60)
            {
                throw new InvalidOperationException($"Instagram hat das Reel nicht verarbeitet: {code} {status.Str("status")}");
            }

            await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        var published = await HttpJson.PostFormAsync(http, $"{GraphUrl}{V}/{account.UserId}/media_publish",
            [new("creation_id", containerId), new("access_token", account.AccessToken)], "Instagram-Reel veröffentlichen", ct).ConfigureAwait(false);
        var mediaId = published.Str("id");
        var link = await Get($"{GraphUrl}{V}/{mediaId}?fields=permalink&access_token={Uri.EscapeDataString(account.AccessToken)}", "Instagram-Link", ct).ConfigureAwait(false);
        return new UploadResult(mediaId, link.Str("permalink"), "als Reel veröffentlicht");
    }

    /// <summary>Takes the code from the pasted address (or the bare code). Instagram appends "#_".</summary>
    internal static string ParseCode(string pasted, string state)
    {
        pasted = pasted.Trim();
        if (!pasted.Contains("code=", StringComparison.Ordinal))
        {
            return pasted.Replace("#_", "", StringComparison.Ordinal);
        }

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(pasted.Split('#')[0]).Query);
        if (query["state"] is { } s && s != state)
        {
            throw new InvalidOperationException("Instagram: Die eingefügte Adresse gehört zu einer anderen Anmeldung. Bitte noch einmal verbinden.");
        }

        return query["code"] ?? throw new InvalidOperationException("Instagram: In der Adresse steht kein Code.");
    }

    private async Task EnsureTokenAsync(UploadAccount account, CancellationToken ct)
    {
        // Long-lived tokens last 60 days and can be renewed once they are a day old.
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddDays(7) || account.IssuedAt > DateTimeOffset.UtcNow.AddDays(-1))
        {
            if (account.ExpiresAt < DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException($"Instagram-Konto {account.Name} muss neu verbunden werden.");
            }

            return;
        }

        var token = await Get(GraphUrl + "refresh_access_token?" + YouTubeUploader.Query(
            ("grant_type", "ig_refresh_token"), ("access_token", account.AccessToken)),
            $"Instagram-Konto {account.Name} (neu verbinden)", ct).ConfigureAwait(false);
        ApplyToken(account, token);
    }

    private static void ApplyToken(UploadAccount account, JsonElement token)
    {
        account.AccessToken = token.Str("access_token");
        account.IssuedAt = DateTimeOffset.UtcNow;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(3600, token.Num("expires_in")));
    }

    private async Task<JsonElement> Get(string url, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        return await HttpJson.SendAsync(http, request, what, ct).ConfigureAwait(false);
    }
}
