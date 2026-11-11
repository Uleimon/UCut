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
        foreach (var old in Directory.EnumerateFiles(folder))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in the browser upload: leave it.
            }
        }

        var target = Path.Combine(folder, FileName(text, Path.GetExtension(video)));
        File.Copy(video, target, overwrite: true);
        return target;
    }
}
