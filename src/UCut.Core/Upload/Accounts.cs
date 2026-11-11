using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCut.Upload;

public enum UploadPlatform
{
    YouTube,
    TikTok,
    Instagram,
}

/// <summary>One connected account. Several per platform are allowed (e.g. two TikTok channels).</summary>
public sealed class UploadAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public UploadPlatform Platform { get; set; }

    /// <summary>Channel or user name as the platform shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>Platform user id (YouTube channel id, TikTok open_id, Instagram user id).</summary>
    public string UserId { get; set; } = "";

    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the token was issued (Instagram may only refresh tokens older than 24 h).</summary>
    public DateTimeOffset IssuedAt { get; set; }

    [JsonIgnore]
    public string Label => $"{PlatformName(Platform)}: {Name}";

    public static string PlatformName(UploadPlatform p) => p switch
    {
        UploadPlatform.YouTube => "YouTube",
        UploadPlatform.TikTok => "TikTok",
        _ => "Instagram",
    };
}

/// <summary>
/// The connected accounts, in accounts.json next to settings.json. Tokens are encrypted for the Windows user (DPAPI),
/// so copying the file to another PC or user does not leak them.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();

    public AccountStore(string path)
    {
        Path = path;
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<List<UploadAccount>>(File.ReadAllText(path), JsonOptions) ?? [];
            foreach (var a in stored)
            {
                a.AccessToken = Unprotect(a.AccessToken);
                a.RefreshToken = Unprotect(a.RefreshToken);
            }

            Accounts = stored;
        }
    }

    public string Path { get; }
    public List<UploadAccount> Accounts { get; } = [];

    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCut", "accounts.json");

    /// <summary>Adds the account, or replaces the one with the same platform and user id (connected again).</summary>
    public void AddOrUpdate(UploadAccount account)
    {
        lock (_lock)
        {
            var existing = Accounts.FindIndex(a => a.Platform == account.Platform && a.UserId == account.UserId && account.UserId.Length > 0);
            if (existing >= 0)
            {
                account.Id = Accounts[existing].Id;
                Accounts[existing] = account;
            }
            else
            {
                Accounts.Add(account);
            }
        }

        Save();
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            Accounts.RemoveAll(a => a.Id == id);
        }

        Save();
    }

    public void Save()
    {
        List<UploadAccount> copy;
        lock (_lock)
        {
            copy = Accounts.Select(a => new UploadAccount
            {
                Id = a.Id, Platform = a.Platform, Name = a.Name, UserId = a.UserId,
                AccessToken = Protect(a.AccessToken), RefreshToken = Protect(a.RefreshToken),
                ExpiresAt = a.ExpiresAt, IssuedAt = a.IssuedAt,
            }).ToList();
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    private const string ProtectedPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    internal static string Protect(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }

        if (OperatingSystem.IsWindows())
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(bytes);
        }

        return PlainPrefix + value;
    }

    internal static string Unprotect(string value)
    {
        if (value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                return "";
            }

            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[ProtectedPrefix.Length..]), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                // Other user or PC: the account has to be connected again.
                return "";
            }
        }

        return value.StartsWith(PlainPrefix, StringComparison.Ordinal) ? value[PlainPrefix.Length..] : value;
    }
}
