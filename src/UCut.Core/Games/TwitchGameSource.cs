using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UCut.Settings;

namespace UCut.Games;

/// <summary>
/// Reads the channel category via the Twitch Helix API (app access token, no login of the user needed).
/// Works also while offline: Twitch keeps the last category.
/// </summary>
public sealed class TwitchGameSource(GameSettings settings, HttpClient http) : IGameSource
{
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private string? _broadcasterId;

    public string Name => "Twitch";

    private string ClientSecret =>
        string.IsNullOrWhiteSpace(settings.TwitchClientSecret)
            ? Environment.GetEnvironmentVariable("UCUT_TWITCH_SECRET") ?? ""
            : settings.TwitchClientSecret;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.TwitchChannel) && !string.IsNullOrWhiteSpace(settings.TwitchClientId) && ClientSecret.Length > 0;

    public async Task<string?> GetCurrentGameAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        await EnsureTokenAsync(ct).ConfigureAwait(false);
        _broadcasterId ??= ParseUserId(await GetAsync($"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(settings.TwitchChannel)}", ct).ConfigureAwait(false));
        if (_broadcasterId is null)
        {
            throw new InvalidOperationException($"Twitch-Kanal '{settings.TwitchChannel}' nicht gefunden.");
        }

        return ParseGameName(await GetAsync($"https://api.twitch.tv/helix/channels?broadcaster_id={_broadcasterId}", ct).ConfigureAwait(false));
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires)
        {
            return;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.TwitchClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "client_credentials",
        });
        using var response = await http.PostAsync(new Uri("https://id.twitch.tv/oauth2/token"), content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        _token = json.GetProperty("access_token").GetString();
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 60);
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Add("Client-Id", settings.TwitchClientId);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    internal static string? ParseUserId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetString() : null;
    }

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
