namespace UCut.Media;

/// <summary>
/// Moving or deleting a clip can briefly fail on Windows while a player, the Explorer preview or a virus scanner still
/// holds the file. Tries again for a few seconds, then says in plain words which file is blocked.
/// </summary>
public static class FileRetry
{
    public static void Move(string from, string to) => Run(to, () => File.Move(from, to, overwrite: true));

    public static void Delete(string path) => Run(path, () =>
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    });

    internal static void Run(string path, Action action, int attempts = 10, int waitMs = 500)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < attempts)
            {
                Thread.Sleep(waitMs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Die Datei „{Path.GetFileName(path)}“ ist gerade in einem anderen Programm offen (z. B. Video-Player, Explorer-Vorschau oder Upload im Browser). "
                    + "Schließ das Programm und versuch es nochmal.", ex);
            }
        }
    }
}
