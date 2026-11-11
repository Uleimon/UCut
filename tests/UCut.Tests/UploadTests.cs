using System.Net;
using System.Text;
using UCut.App;
using UCut.Library;
using UCut.Metadata;
using UCut.Settings;
using UCut.Upload;

namespace UCut.Tests;

public sealed class UploadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ucut-upload-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Records every request and answers by a rule list (first match wins).</summary>
    private sealed class FakeApi(params (Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Answer)[] rules) : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body, HttpRequestMessage Request)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : request.Content is ByteArrayContent or StreamContent && request.Content.Headers.ContentType?.MediaType == "video/mp4"
                ? $"<{(await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length} bytes>"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body, request));
            foreach (var (match, answer) in rules)
            {
                if (match(request))
                {
                    return answer(request);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no rule\"}") };
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Func<HttpRequestMessage, bool> Url(string part) => r => r.RequestUri!.ToString().Contains(part, StringComparison.Ordinal);

    private string Video(int bytes)
    {
        var path = Path.Combine(_dir, $"clip{bytes}.mp4");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static UploadAccount Fresh(UploadPlatform p) => new()
    {
        Platform = p, Name = "Uleimon", UserId = "u1", AccessToken = "AT", RefreshToken = "RT",
        IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Fact]
    public void Pkce_matches_rfc7636_example()
    {
        // Example from RFC 7636, appendix B.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeBase64Url(verifier));
        Assert.Equal(64, Pkce.ChallengeHex(verifier).Length);
        Assert.Matches("^[A-Za-z0-9._~-]{64}$", Pkce.NewVerifier());
    }

    [Fact]
    public async Task YouTube_upload_starts_resumable_session_and_sends_file()
    {
        var api = new FakeApi(
            (Url("upload/youtube/v3/videos?uploadType=resumable"), _ =>
            {
                var r = Json("{}");
                r.Headers.Location = new Uri("https://www.googleapis.com/upload/session/xyz");
                return r;
            }),
            (Url("upload/session/xyz"), _ => Json("""{"id":"VID123"}""")));
        var settings = new UploadSettings { YouTubeClientId = "cid", YouTubeClientSecret = "sec" };
        var uploader = new YouTubeUploader(settings, new HttpClient(api));
        var progress = new List<double>();
        var result = await uploader.UploadAsync(Fresh(UploadPlatform.YouTube),
            new UploadRequest(Video(3000), new string('T', 120), "Beschreibung", ["gta", "lustig"], ""), new SyncProgress(progress), default);

        Assert.Equal("https://youtube.com/shorts/VID123", result.Url);
        Assert.Contains("privat", result.Note, StringComparison.Ordinal);
        var start = api.Requests[0];
        Assert.Equal("POST", start.Method);
        Assert.Equal("Bearer AT", start.Request.Headers.Authorization!.ToString());
        Assert.Equal("3000", start.Request.Headers.GetValues("X-Upload-Content-Length").Single());
        Assert.Contains("\"privacyStatus\":\"private\"", start.Body, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"" + new string('T', 100) + "\"", start.Body, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[\"gta\",\"lustig\"]", start.Body, StringComparison.Ordinal);
        Assert.Equal("PUT", api.Requests[1].Method);
        Assert.Equal("<3000 bytes>", api.Requests[1].Body);
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task YouTube_refreshes_expired_token_first()
    {
        var api = new FakeApi(
            (Url("oauth2.googleapis.com/token"), _ => Json("""{"access_token":"NEW","expires_in":3599}""")),
            (Url("uploadType=resumable"), _ =>
            {
                var r = Json("{}");
                r.Headers.Location = new Uri("https://www.googleapis.com/upload/session/1");
                return r;
            }),
            (Url("upload/session/1"), _ => Json("""{"id":"V"}""")));
        var account = Fresh(UploadPlatform.YouTube);
        account.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await new YouTubeUploader(new UploadSettings { YouTubeClientId = "c", YouTubeClientSecret = "s" }, new HttpClient(api))
            .UploadAsync(account, new UploadRequest(Video(10), "t", "", [], ""), null, default);

        Assert.Contains("grant_type=refresh_token", api.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=RT", api.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("NEW", account.AccessToken);
        Assert.Equal("RT", account.RefreshToken); // Google sends no new refresh token: keep the old one
        Assert.Equal("Bearer NEW", api.Requests[1].Request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(3_000_000L, 3_000_000L, 1)]
    [InlineData(64L * 1024 * 1024, 64L * 1024 * 1024, 1)]
    [InlineData(100L * 1024 * 1024, 10L * 1024 * 1024, 10)]
    [InlineData(105L * 1024 * 1024 + 7, 10L * 1024 * 1024, 10)]
    public void TikTok_chunks_follow_the_rules(long size, long chunk, int count)
    {
        var (c, n) = TikTokUploader.Chunks(size);
        Assert.Equal(chunk, c);
        Assert.Equal(count, n);
        // The last piece takes the rest and stays under 128 MB.
        Assert.InRange(size - (n - 1) * c, 1, 128L * 1024 * 1024);
    }

    [Fact]
    public async Task TikTok_inbox_upload_sends_file_and_waits_for_inbox()
    {
        var polls = 0;
        var api = new FakeApi(
            (Url("post/publish/inbox/video/init/"), _ => Json("""{"data":{"publish_id":"P1","upload_url":"https://open-upload.tiktokapis.com/video/?upload_id=1"},"error":{"code":"ok"}}""")),
            (Url("open-upload.tiktokapis.com"), _ => new HttpResponseMessage(HttpStatusCode.Created)),
            (Url("post/publish/status/fetch/"), _ => Json(++polls < 2
                ? """{"data":{"status":"PROCESSING_UPLOAD"},"error":{"code":"ok"}}"""
                : """{"data":{"status":"SEND_TO_USER_INBOX"},"error":{"code":"ok"}}""")));
        var uploader = new TikTokUploader(new UploadSettings { TikTokClientKey = "k", TikTokClientSecret = "s" }, new HttpClient(api), (_, _) => Task.CompletedTask);
        var result = await uploader.UploadAsync(Fresh(UploadPlatform.TikTok), new UploadRequest(Video(5000), "", "", [], "lustig #gta"), null, default);

        Assert.Contains("Entwurf", result.Note, StringComparison.Ordinal);
        Assert.Contains("\"video_size\":5000", api.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"total_chunk_count\":1", api.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("post_info", api.Requests[0].Body, StringComparison.Ordinal);
        var put = api.Requests[1].Request;
        Assert.Equal("PUT", put.Method.Method);
        Assert.Equal("bytes 0-4999/5000", put.Content!.Headers.ContentRange!.ToString());
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task TikTok_direct_post_asks_privacy_options_and_reports_api_errors()
    {
        var api = new FakeApi(
            (Url("creator_info/query/"), _ => Json("""{"data":{"privacy_level_options":["FOLLOWER_OF_CREATOR","SELF_ONLY"]},"error":{"code":"ok"}}""")),
            (Url("post/publish/video/init/"), _ => Json("""{"error":{"code":"unaudited_client_can_only_post_to_private_accounts","message":"nope"}}""")));
        var uploader = new TikTokUploader(new UploadSettings { TikTokClientKey = "k", TikTokClientSecret = "s", TikTokMode = "direct" }, new HttpClient(api));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            uploader.UploadAsync(Fresh(UploadPlatform.TikTok), new UploadRequest(Video(100), "", "", [], "cap"), null, default));

        Assert.Contains("unaudited_client", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"privacy_level\":\"SELF_ONLY\"", api.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"cap\"", api.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instagram_reel_container_upload_wait_publish()
    {
        var status = 0;
        var api = new FakeApi(
            (r => r.RequestUri!.AbsolutePath.EndsWith("/u1/media", StringComparison.Ordinal), _ => Json("""{"id":"C1"}""")),
            (Url("rupload.facebook.com/ig-api-upload/v24.0/C1"), _ => Json("""{"success":true}""")),
            (Url("/C1?fields=status_code"), _ => Json(++status < 3 ? """{"status_code":"IN_PROGRESS"}""" : """{"status_code":"FINISHED"}""")),
            (Url("/u1/media_publish"), _ => Json("""{"id":"M9"}""")),
            (Url("/M9?fields=permalink"), _ => Json("""{"permalink":"https://www.instagram.com/reel/abc/"}""")));
        var uploader = new InstagramUploader(new UploadSettings { InstagramAppId = "a", InstagramAppSecret = "s" }, new HttpClient(api), (_, _) => Task.CompletedTask);
        var result = await uploader.UploadAsync(Fresh(UploadPlatform.Instagram), new UploadRequest(Video(777), "", "", [], "Reel #gaming"), null, default);

        Assert.Equal("https://www.instagram.com/reel/abc/", result.Url);
        Assert.Contains("media_type=REELS", api.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("upload_type=resumable", api.Requests[0].Body, StringComparison.Ordinal);
        var upload = api.Requests[1].Request;
        Assert.Equal("OAuth AT", upload.Headers.GetValues("Authorization").Single());
        Assert.Equal("777", upload.Headers.GetValues("file_size").Single());
        Assert.Equal("0", upload.Headers.GetValues("offset").Single());
        Assert.Contains("creation_id=C1", api.Requests.Single(r => r.Url.Contains("media_publish", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://localhost/?code=ABC123#_", "ABC123")]
    [InlineData("https://localhost/?code=ABC123&state=s1#_", "ABC123")]
    [InlineData("ABC123#_", "ABC123")]
    public void Instagram_code_is_taken_from_pasted_address(string pasted, string code) =>
        Assert.Equal(code, InstagramUploader.ParseCode(pasted, "s1"));

    [Fact]
    public void Instagram_rejects_address_of_other_sign_in() =>
        Assert.Throws<InvalidOperationException>(() => InstagramUploader.ParseCode("https://localhost/?code=X&state=other", "s1"));

    [Fact]
    public async Task Loopback_receiver_gets_code_from_browser()
    {
        var port = FreePort();
        using var receiver = new LoopbackReceiver(port);
        var wait = receiver.WaitAsync(TimeSpan.FromSeconds(10), default);
        using var browser = new HttpClient();
        var page = await browser.GetStringAsync(new Uri(receiver.RedirectUri + "?code=XYZ&state=abc"));
        var answer = await wait;

        Assert.Equal("XYZ", answer["code"]);
        Assert.Equal("abc", answer["state"]);
        Assert.Contains("geklappt", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_store_keeps_several_accounts_per_platform_and_replaces_reconnected_ones()
    {
        var path = Path.Combine(_dir, "accounts.json");
        var store = new AccountStore(path);
        store.AddOrUpdate(new UploadAccount { Platform = UploadPlatform.TikTok, Name = "Main", UserId = "a", AccessToken = "geheim1" });
        store.AddOrUpdate(new UploadAccount { Platform = UploadPlatform.TikTok, Name = "Zweit", UserId = "b", AccessToken = "geheim2" });
        store.AddOrUpdate(new UploadAccount { Platform = UploadPlatform.TikTok, Name = "Main neu", UserId = "a", AccessToken = "geheim3" });

        var loaded = new AccountStore(path);
        Assert.Equal(["Main neu", "Zweit"], loaded.Accounts.Select(a => a.Name));
        Assert.Equal("geheim3", loaded.Accounts[0].AccessToken);
        var file = File.ReadAllText(path);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("geheim3", file, StringComparison.Ordinal); // encrypted with DPAPI
        }
        else
        {
            Assert.Contains("plain:geheim3", file, StringComparison.Ordinal);
        }

        loaded.Remove(loaded.Accounts[1].Id);
        Assert.Single(new AccountStore(path).Accounts);
    }

    [Fact]
    public async Task Service_uploads_to_each_account_and_notes_results_in_info_file()
    {
        var video = Video(50);
        var json = Path.ChangeExtension(video, ".json");
        var meta = new ClipMetadata { Video = Path.GetFileName(video) };
        meta.TikTok.Caption = "tt";
        meta.YouTube.Title = "yt";
        meta.Save(json);
        var clip = new LibraryClip(video, json, meta);

        var good = new StubUploader(UploadPlatform.YouTube, null);
        var bad = new StubUploader(UploadPlatform.TikTok, "kaputt");
        var service = new UploadService(new UploadSettings(), new AccountStore(Path.Combine(_dir, "a.json")), new RunLog(), uploaders: [good, bad, new StubUploader(UploadPlatform.Instagram, null)]);
        var outcomes = await service.UploadAsync(clip, [Fresh(UploadPlatform.TikTok), Fresh(UploadPlatform.YouTube)]);

        Assert.Equal("kaputt", outcomes[0].Error);
        Assert.NotNull(outcomes[1].Result);
        Assert.Equal("tt", bad.Last!.Caption);
        Assert.Equal("yt", good.Last!.Title);
        var saved = ClipMetadata.Load(json);
        Assert.Equal("https://x/1", saved.Uploaded["YouTube: Uleimon"]);
        Assert.False(saved.Uploaded.ContainsKey("TikTok: Uleimon"));
    }

    private sealed class StubUploader(UploadPlatform platform, string? error) : IPlatformUploader
    {
        public UploadRequest? Last { get; private set; }
        public UploadPlatform Platform => platform;
        public bool IsConfigured => true;
        public Task<UploadAccount> ConnectAsync(IAuthUi ui, CancellationToken ct) => throw new NotSupportedException();

        public Task<UploadResult> UploadAsync(UploadAccount account, UploadRequest req, IProgress<double>? progress, CancellationToken ct)
        {
            Last = req;
            return error is null ? Task.FromResult(new UploadResult("1", "https://x/1", "ok")) : throw new InvalidOperationException(error);
        }
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
