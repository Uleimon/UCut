using System.Diagnostics;
using UCut.Settings;

namespace UCut.Games;

public sealed record GameResult(string FolderName, string? RawName, string Source);

public interface IGameSource
{
    string Name { get; }

    /// <summary>Returns the current game/category, or null when this source does not know it.</summary>
    Task<string?> GetCurrentGameAsync(CancellationToken ct = default);
}

/// <summary>Asks the sources in order (Twitch first, then running processes) and maps the answer to a folder name.</summary>
public sealed class GameDetector(GameSettings settings, IReadOnlyList<IGameSource> sources, Action<string>? log = null)
{
    public async Task<GameResult> DetectAsync(CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            try
            {
                var raw = await source.GetCurrentGameAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return new GameResult(ToFolderName(raw, settings), raw, source.Name);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                log?.Invoke($"Spielerkennung über {source.Name} fehlgeschlagen: {ex.Message}");
            }
        }

        return new GameResult(settings.UnknownFolder, null, "keine");
    }

    /// <summary>Applies the alias table and removes characters Windows does not allow in folder names.</summary>
    public static string ToFolderName(string raw, GameSettings settings)
    {
        var name = settings.Aliases.TryGetValue(raw.Trim(), out var alias) ? alias : raw.Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '*', '?', '"', '<', '>', '|', '/', '\\']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return clean.Length == 0 ? settings.UnknownFolder : clean;
    }
}

/// <summary>Looks at the running processes (works also when the Twitch category is wrong or the stream is offline).</summary>
public sealed class ProcessGameSource(GameSettings settings, Func<IEnumerable<string>>? processNames = null) : IGameSource
{
    public string Name => "Prozess";

    public Task<string?> GetCurrentGameAsync(CancellationToken ct = default)
    {
        var names = processNames?.Invoke() ?? Process.GetProcesses().Select(p =>
        {
            using (p)
            {
                return p.ProcessName;
            }
        });
        foreach (var n in names)
        {
            if (settings.Processes.TryGetValue(n, out var game))
            {
                return Task.FromResult<string?>(game);
            }
        }

        return Task.FromResult<string?>(null);
    }
}
