namespace UCut.App;

/// <summary>
/// Everything UCut reports: shown live (console or window) and appended to UCut-Protokoll.txt in the output
/// folder, so a run overnight can be read in the morning.
/// </summary>
public sealed class RunLog
{
    private readonly Lock _gate = new();

    public event Action<string>? Line;

    /// <summary>Log file; null = only live.</summary>
    public string? FilePath { get; set; }

    public void Write(string message)
    {
        var now = DateTime.Now;
        Line?.Invoke($"[{now:HH:mm:ss}] {message}");
        if (FilePath is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"[{now:dd.MM.yyyy HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Last lines of the log file (for the log page).</summary>
    public static IReadOnlyList<string> Tail(string path, int lines)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                all.Enqueue(line);
                if (all.Count > lines)
                {
                    all.Dequeue();
                }
            }

            return [.. all];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
