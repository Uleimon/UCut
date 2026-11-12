using System.Text;

namespace UCut.Upload;

/// <summary>
/// For uploading by hand: TikTok Studio and YouTube Studio take the file name as description/title.
/// So the clip is copied once under a name that IS the text (with look-alike characters for : ? " / etc.,
/// which Windows does not allow in file names).
/// </summary>
public static class UploadCopy
{
    /// <summary>Longest name; leaves room for the folder in Windows' 260-character path limit.</summary>
    public const int MaxLength = 150;

    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        [':'] = '꞉', ['?'] = '？', ['"'] = '＂', ['/'] = '∕', ['\\'] = '⧵', ['*'] = '∗', ['<'] = '‹', ['>'] = '›', ['|'] = '∣',
    };

    /// <summary>Where the copies go: %TEMP%\UCut-Hochladen.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "UCut-Hochladen");

    /// <summary>Deletes all copies. Returns how many were deleted and how many are still open somewhere (browser upload).</summary>
    public static (int Deleted, int Locked) Clear(string folder)
    {
        var deleted = 0;
        var locked = 0;
        if (!Directory.Exists(folder))
        {
            return (0, 0);
        }

        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                File.Delete(f);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                locked++;
            }
        }

        return (deleted, locked);
    }

    public static string FileName(string text, string extension = ".mp4")
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (LookAlikes.TryGetValue(c, out var alike))
            {
                sb.Append(alike);
            }
            else if (char.IsControl(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxLength)
        {
            var cut = name.LastIndexOf(' ', MaxLength);
            name = name[..(cut > MaxLength / 2 ? cut : MaxLength)];
        }

        // Windows drops trailing dots and spaces.
        name = name.TrimEnd('.', ' ');
        return (name.Length == 0 ? "clip" : name) + extension;
    }

    /// <summary>Copies the clip into <paramref name="folder"/> under the text as name (old copies there are removed). Returns the copy.</summary>
    public static string Create(string video, string text, string folder)
    {
        Directory.CreateDirectory(folder);
        Clear(folder); // a copy still open in the browser upload stays

        var target = Path.Combine(folder, FileName(text, Path.GetExtension(video)));
        File.Copy(video, target, overwrite: true);
        return target;
    }
}
