using UCut.Games;
using UCut.Settings;

namespace UCut.Tests;

public class GameTests
{
    private sealed class FixedSource(string? game, string name = "Test") : IGameSource
    {
        public string Name => name;
        public Task<string?> GetCurrentGameAsync(CancellationToken ct = default) => Task.FromResult(game);
    }

    private sealed class FailingSource : IGameSource
    {
        public string Name => "Kaputt";
        public Task<string?> GetCurrentGameAsync(CancellationToken ct = default) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Twitch_Kategorie_wird_ueber_Alias_zum_Ordnernamen()
    {
        var s = new GameSettings();
        var r = await new GameDetector(s, [new FixedSource("Grand Theft Auto V", "Twitch")]).DetectAsync();
        Assert.Equal("GTA V", r.FolderName);
        Assert.Equal("Twitch", r.Source);
    }

    [Fact]
    public async Task Faellt_auf_naechste_Quelle_zurueck_wenn_eine_ausfaellt()
    {
        var logs = new List<string>();
        var r = await new GameDetector(new GameSettings(), [new FailingSource(), new FixedSource(null), new FixedSource("Minecraft", "Prozess")], logs.Add).DetectAsync();
        Assert.Equal("Minecraft", r.FolderName);
        Assert.Single(logs);
    }

    [Fact]
    public async Task Ohne_Treffer_Ordner_Unsortiert()
    {
        var r = await new GameDetector(new GameSettings(), [new FixedSource(null)]).DetectAsync();
        Assert.Equal("Unsortiert", r.FolderName);
    }

    [Theory]
    [InlineData("Call of Duty: Warzone", "Call of Duty Warzone")]
    [InlineData("  Who? What?  ", "Who What")]
    [InlineData("...", "Unsortiert")]
    public void Ordnernamen_sind_unter_Windows_gueltig(string raw, string expected) =>
        Assert.Equal(expected, GameDetector.ToFolderName(raw, new GameSettings()));

    [Fact]
    public async Task Prozess_wird_erkannt()
    {
        var src = new ProcessGameSource(new GameSettings(), () => ["explorer", "obs64", "gta5"]);
        Assert.Equal("GTA V", await src.GetCurrentGameAsync());
    }

    [Fact]
    public void Twitch_Antworten_werden_gelesen()
    {
        Assert.Equal("1234", TwitchGameSource.ParseUserId("""{"data":[{"id":"1234","login":"uleimon"}]}"""));
        Assert.Null(TwitchGameSource.ParseUserId("""{"data":[]}"""));
        Assert.Equal("Grand Theft Auto V", TwitchGameSource.ParseGameName("""{"data":[{"broadcaster_id":"1234","game_name":"Grand Theft Auto V"}]}"""));
        Assert.Null(TwitchGameSource.ParseGameName("""{"data":[{"broadcaster_id":"1234","game_name":""}]}"""));
    }

    [Fact]
    public async Task Twitch_ohne_Zugangsdaten_wird_uebersprungen()
    {
        using var http = new HttpClient();
        var src = new TwitchGameSource(new GameSettings { TwitchChannel = "uleimon" }, http);
        Assert.Null(await src.GetCurrentGameAsync());
    }
}
