using System.Threading.Channels;

namespace UCut.Pipeline;

/// <summary>
/// Watches the Aitum/OBS output folder. A new video is handed on once it is completely written
/// (size stable and the file can be opened exclusively).
/// </summary>
public sealed class ClipWatcher(string folder, Action<string> log, TimeSpan? stableFor = null) : IDisposable
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".mov"];
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly TimeSpan _stableFor = stableFor ?? TimeSpan.FromSeconds(2);
    private FileSystemWatcher? _watcher;

    /// <summary>Invoked as soon as a file shows up (before it is finished). UCut detects the game at this moment.</summary>
    public event Action<string>? FileAppeared;

    public void Start()
    {
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = false };
        _watcher.Created += (_, e) => Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        _watcher.EnableRaisingEvents = true;
        log($"Überwache {folder}");
    }

    private void Enqueue(string path)
    {
        if (Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            FileAppeared?.Invoke(path);
            _queue.Writer.TryWrite(path);
        }
    }

    public async IAsyncEnumerable<string> ReadFinishedAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var path in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (await WaitUntilCompleteAsync(path, _stableFor, ct).ConfigureAwait(false))
            {
                yield return path;
            }
        }
    }

    public static async Task<bool> WaitUntilCompleteAsync(string path, TimeSpan stableFor, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));
        long lastSize = -1;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var size = new FileInfo(path).Length;
            if (size != lastSize)
            {
                lastSize = size;
                stableSince = DateTime.UtcNow;
            }
            else if (size > 0 && DateTime.UtcNow - stableSince >= stableFor && CanOpenExclusive(path))
            {
                return true;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _queue.Writer.TryComplete();
    }
}
