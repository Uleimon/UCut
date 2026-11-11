using UCut.App;
using UCut.Library;
using UCut.Settings;

namespace UCut.Upload;

/// <summary>One finished upload, for the log and the clip's info file.</summary>
public sealed record UploadOutcome(UploadAccount Account, UploadResult? Result, string? Error);

/// <summary>Connects accounts and uploads clips to several accounts in a row.</summary>
public sealed class UploadService
{
    private readonly RunLog _log;
    private readonly Dictionary<UploadPlatform, IPlatformUploader> _uploaders;

    public UploadService(UploadSettings settings, AccountStore accounts, RunLog log, HttpClient? http = null, IEnumerable<IPlatformUploader>? uploaders = null)
    {
        _log = log;
        Accounts = accounts;

        // Uploads can take minutes; the default 100 s timeout of HttpClient is too short.
        http ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _uploaders = (uploaders ?? [new YouTubeUploader(settings, http), new TikTokUploader(settings, http), new InstagramUploader(settings, http)])
            .ToDictionary(u => u.Platform);
    }

    public AccountStore Accounts { get; }

    public bool IsConfigured(UploadPlatform platform) => _uploaders[platform].IsConfigured;

    public async Task<UploadAccount> ConnectAsync(UploadPlatform platform, IAuthUi ui, CancellationToken ct = default)
    {
        var uploader = _uploaders[platform];
        if (!uploader.IsConfigured)
        {
            throw new InvalidOperationException($"{UploadAccount.PlatformName(platform)} ist noch nicht eingerichtet: zuerst die App-Daten in den Einstellungen eintragen.");
        }

        var account = await uploader.ConnectAsync(ui, ct).ConfigureAwait(false);
        Accounts.AddOrUpdate(account);
        _log.Write($"{account.Label} verbunden.");
        return account;
    }

    /// <summary>Uploads the clip to every chosen account. One failure does not stop the others.</summary>
    public async Task<IReadOnlyList<UploadOutcome>> UploadAsync(LibraryClip clip, IReadOnlyList<UploadAccount> targets, IProgress<(UploadAccount Account, double Share)>? progress = null, CancellationToken ct = default)
    {
        var outcomes = new List<UploadOutcome>();
        foreach (var account in targets)
        {
            ct.ThrowIfCancellationRequested();
            var request = RequestFor(clip, account.Platform);
            _log.Write($"{account.Label}: \"{clip.Title}\" wird hochgeladen ...");
            try
            {
                var p = progress is null ? null : new Progress<double>(v => progress.Report((account, v)));
                var result = await _uploaders[account.Platform].UploadAsync(account, request, p, ct).ConfigureAwait(false);
                clip.Metadata.Uploaded[account.Label] = result.Url.Length > 0 ? result.Url : result.Note;
                clip.Save();
                _log.Write($"{account.Label}: {result.Note}{(result.Url.Length > 0 ? " " + result.Url : "")}");
                outcomes.Add(new UploadOutcome(account, result, null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or System.Text.Json.JsonException)
            {
                _log.Write($"{account.Label}: Upload fehlgeschlagen: {ex.Message}");
                outcomes.Add(new UploadOutcome(account, null, ex.Message));
            }
            finally
            {
                // Tokens may have been renewed.
                Accounts.Save();
            }
        }

        return outcomes;
    }

    internal static UploadRequest RequestFor(LibraryClip clip, UploadPlatform platform)
    {
        var m = clip.Metadata;
        return platform switch
        {
            UploadPlatform.YouTube => new UploadRequest(clip.VideoPath, m.YouTube.Title, m.YouTube.Description, m.YouTube.Tags, m.YouTube.Title),
            UploadPlatform.TikTok => new UploadRequest(clip.VideoPath, m.YouTube.Title, "", [], m.TikTok.Caption),
            _ => new UploadRequest(clip.VideoPath, m.YouTube.Title, "", [], m.Instagram.Caption),
        };
    }
}
