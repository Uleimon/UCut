using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UCut.Settings;

namespace UCut.Twitch;

/// <summary>
/// Minimal Twitch Helix client with an app access token (client credentials, no login of the user needed).
/// </summary>
public sealed class TwitchApi(GameSettings settings, HttpClient http)
{
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private string? _broadcasterId;

    internal const string BaseUrl = "https://api.twitch.tv/helix/";

    private string ClientSecret =>
        string.IsNullOrWhiteSpace(settings.TwitchClientSecret)
            ? Environment.GetEnvironmentVariable("UCUT_TWITCH_SECRET") ?? ""
            : settings.TwitchClientSecret;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.TwitchChannel) && !string.IsNullOrWhiteSpace(settings.TwitchClientId) && ClientSecret.Length > 0;

    public string Channel => settings.TwitchChannel;

    /// <summary>User id of the configured channel. Stays the same when the channel is renamed (KREATIVLOS to Uleimon).</summary>
    public async Task<string> GetBroadcasterIdAsync(CancellationToken ct = default)
    {
        _broadcasterId ??= ParseUserId(await GetAsync($"users?login={Uri.EscapeDataString(settings.TwitchChannel)}", ct).ConfigureAwait(false))
                           ?? throw new InvalidOperationException($"Twitch-Kanal '{settings.TwitchChannel}' nicht gefunden.");
        return _broadcasterId;
    }

    /// <summary>GET on a Helix path relative to /helix/, returns the JSON body.</summary>
    public async Task<string> GetAsync(string pathAndQuery, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Twitch ist nicht eingerichtet: games.twitchChannel, games.twitchClientId und games.twitchClientSecret (oder UCUT_TWITCH_SECRET) setzen.");
        }

        await EnsureTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl + pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Add("Client-Id", settings.TwitchClientId);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Twitch-API antwortet mit {(int)response.StatusCode} auf {pathAndQuery.Split('?')[0]}.");
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Twitch-Anmeldung fehlgeschlagen ({(int)response.StatusCode}). Client-ID und Secret prüfen.");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        _token = json.GetProperty("access_token").GetString();
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 60);
    }

    internal static string? ParseUserId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetString() : null;
    }
}
