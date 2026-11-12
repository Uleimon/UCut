using System.Net;
using System.Text;
using UCut.Settings;
using UCut.Twitch;

namespace UCut.Tests;

public class TwitchClipTests
{
    private static TwitchClip Clip(string id, string title, int views, string game = "32982", string created = "2020-06-01T18:00:00Z") =>
        new(id, $"https://clips.twitch.tv/{id}", title, views, DateTimeOffset.Parse(created, System.Globalization.CultureInfo.InvariantCulture), 30, game, "zuschauer");

    /// <summary>Answers like Helix, records every request.</summary>
    private sealed class FakeTwitch : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var body = url switch
            {
                _ when url.StartsWith("https://id.twitch.tv/oauth2/token", StringComparison.Ordinal) => """{"access_token":"t","expires_in":3600}""",
                _ when url.Contains("/helix/users?login=uleimon", StringComparison.Ordinal) => """{"data":[{"id":"42"}]}""",
                _ when url.Contains("/helix/clips", StringComparison.Ordinal) && url.Contains("started_at=2020-01", StringComparison.Ordinal) && !url.Contains("after=", StringComparison.Ordinal) =>
                    """{"data":[{"id":"A","url":"https://clips.twitch.tv/A","title":"Ausraster beim Tanken XD","view_count":120,"created_at":"2020-01-05T20:00:00Z","duration":28.5,"game_id":"32982","creator_name":"x"}],"pagination":{"cursor":"c1"}}""",
                _ when url.Contains("/helix/clips", StringComparison.Ordinal) && url.Contains("after=c1", StringComparison.Ordinal) =>
                    """{"data":[{"id":"B","url":"https://clips.twitch.tv/B","title":"Zweiter","view_count":3,"created_at":"2020-01-20T20:00:00Z","duration":12,"game_id":"509658","creator_name":"y"}],"pagination":{}}""",
                _ when url.Contains("/helix/clips", StringComparison.Ordinal) && url.Contains("started_at=2020-02", StringComparison.Ordinal) =>
                    """{"data":[{"id":"A","url":"https://clips.twitch.tv/A","title":"Ausraster beim Tanken XD","view_count":120,"created_at":"2020-01-05T20:00:00Z","duration":28.5,"game_id":"32982","creator_name":"x"}],"pagination":{}}""",
                _ when url.Contains("/helix/games?", StringComparison.Ordinal) =>
                    """{"data":[{"id":"32982","name":"Grand Theft Auto V"},{"id":"509658","name":"Just Chatting"}]}""",
                _ => """{"data":[],"pagination":{}}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Clips_werden_monatsweise_mit_Seiten_und_Spielnamen_geladen()
    {
        var fake = new FakeTwitch();
        using var http = new HttpClient(fake);
        var api = new TwitchApi(new GameSettings { TwitchChannel = "uleimon", TwitchClientId = "id", TwitchClientSecret = "secret" }, http);
        var from = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clips = await new TwitchClipSearch(api).ListAsync(from, from.AddMonths(3));

        Assert.Equal(["A", "B"], clips.Select(c => c.Id));
        Assert.Equal("Grand Theft Auto V", clips[0].GameName);
        Assert.Equal("Just Chatting", clips[1].GameName);
        Assert.Equal(28.5, clips[0].DurationSeconds);
        Assert.Contains(fake.Requests, r => r.Contains("broadcaster_id=42", StringComparison.Ordinal) && r.Contains("first=100", StringComparison.Ordinal));
        Assert.Equal(3, fake.Requests.Count(r => r.Contains("started_at=", StringComparison.Ordinal) && !r.Contains("after=", StringComparison.Ordinal)));
        Assert.Single(fake.Requests, r => r.Contains("/helix/games?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ohne_Zugangsdaten_kommt_eine_klare_Meldung()
    {
        using var http = new HttpClient(new FakeTwitch());
        var api = new TwitchApi(new GameSettings { TwitchChannel = "uleimon" }, http);
        Assert.False(api.IsConfigured);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetAsync("clips"));
        Assert.Contains("twitchClientId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zeitfenster_sind_Monate_bis_zum_Ende()
    {
        var w = TwitchClipSearch.MonthWindows(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 3, 15, 0, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Equal(3, w.Count);
        Assert.Equal(new DateTimeOffset(2020, 3, 15, 0, 0, 0, TimeSpan.Zero), w[^1].End);
        Assert.Equal(w[0].End, w[1].Start);
    }

    [Theory]
    [InlineData("Ich bin raus XD", "xd", true)]
    [InlineData("Holzfäller lollipop", "lol", false)]
    [InlineData("LOL was war das", "lol", true)]
    [InlineData("Der totale Ausraster", "ausraster", true)]
    [InlineData("Tanken 😂", "😂", true)]
    public void Witzige_Woerter_im_Titel(string title, string word, bool expected) =>
        Assert.Equal(expected, FunnyFilter.ContainsWord(title, word));

    [Fact]
    public void Lustig_aussehende_und_meistgesehene_Clips_kommen_zuerst()
    {
        var clips = new List<TwitchClip>
        {
            Clip("1", "Normale Runde", 5),
            Clip("2", "Ausraster XD", 10),
            Clip("3", "Einfach nur fahren", 500),
            Clip("4", "Langweilig", 2),
        };
        var ranked = FunnyFilter.Rank(clips, new TwitchClipSettings());
        Assert.Equal(["2", "3"], ranked.Select(c => c.Clip.Id));
        Assert.Contains(ranked[0].Reasons, r => r.Contains("ausraster", StringComparison.OrdinalIgnoreCase));

        var all = FunnyFilter.Rank(clips, new TwitchClipSettings { OnlyFunny = false, MaxClips = 3 });
        Assert.Equal(3, all.Count);

        Assert.Empty(FunnyFilter.Rank(clips, new TwitchClipSettings { MinViews = 1000 }));
    }

    [Fact]
    public void Stream_Titel_zaehlt_nicht_als_lustig()
    {
        var clips = new List<TwitchClip>
        {
            Clip("1", "Was spiel ich hier alter !clip", 1),
            Clip("2", "Was spiel ich hier alter !clip", 2),
            Clip("3", "Was spiel ich hier alter !clip", 1),
            Clip("4", "Der Ausraster XD", 1),
            Clip("5", "Normal", 2),
            Clip("6", "Normal 2", 2),
            Clip("7", "Viel geschaut", 900),
        };
        var ranked = FunnyFilter.Rank(clips, new TwitchClipSettings());
        Assert.Equal(["4", "7"], ranked.Select(c => c.Clip.Id).Order());
    }

    [Fact]
    public void Archiv_merkt_sich_geladene_Clips_und_schreibt_Uebersicht()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-archiv-");
        try
        {
            var a = ClipArchive.Load(dir.FullName);
            a.Add(new ArchivedClip { Id = "A", Title = "Langweilig", Score = 90, Funniness = 2, Url = "u1", Game = "GTA V" });
            a.Add(new ArchivedClip { Id = "B", Title = "Tanken; \"Ausraster\"", Score = 70, Funniness = 9, Url = "u2", Game = "GTA V" });

            var b = ClipArchive.Load(dir.FullName);
            Assert.True(b.Contains("A"));
            Assert.False(b.Contains("C"));

            var lines = File.ReadAllLines(b.WriteOverview());
            Assert.StartsWith("Lustigkeit;Score", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("9;70;", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"Tanken; \"\"Ausraster\"\"\"", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("2;90;", lines[2], StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Download_mit_yt_dlp()
    {
        var clip = Clip("Brave/Cool-Clip_1", "x", 1);
        Assert.EndsWith("_Brave_Cool-Clip_1.mp4", ClipDownloader.FileName(clip), StringComparison.Ordinal);
        var args = ClipDownloader.Arguments(clip.Url, "out.mp4");
        Assert.Equal(clip.Url, args[^1]);
        Assert.Contains("--no-playlist", args);
        Assert.Equal("out.mp4", args[args.ToList().IndexOf("-o") + 1]);
    }
}
