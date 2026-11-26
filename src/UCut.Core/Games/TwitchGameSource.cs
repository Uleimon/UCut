using System.Text.Json;
using UCut.Settings;
using UCut.Twitch;

namespace UCut.Games;

/// <summary>
/// Reads the channel category via the Twitch Helix API (app access token, no login of the user needed).
/// Works also while offline: Twitch keeps the last category.
/// </summary>
public sealed class TwitchGameSource(GameSettings settings, HttpClient http) : IGameSource
{
    private readonly TwitchApi _api = new(settings, http);

    public string Name => "Twitch";

    public bool IsConfigured => _api.IsConfigured;

    public async Task<string?> GetCurrentGameAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var id = await _api.GetBroadcasterIdAsync(ct).ConfigureAwait(false);
        return ParseGameName(await _api.GetAsync($"channels?broadcaster_id={id}", ct).ConfigureAwait(false));
    }

    internal static string? ParseUserId(string json) => TwitchApi.ParseUserId(json);

    internal static string? ParseGameName(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
        {
            return null;
        }

        var name = data[0].GetProperty("game_name").GetString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
