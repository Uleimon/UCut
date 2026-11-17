using System.Text.Json;
using UCut.Media;
using UCut.Metadata;

namespace UCut.Library;

/// <summary>One finished clip in the output folder: video, info file, preview picture.</summary>
public sealed class LibraryClip(string videoPath, string metadataPath, ClipMetadata metadata)
{
    public string VideoPath { get; } = videoPath;
    public string MetadataPath { get; } = metadataPath;
    public ClipMetadata Metadata { get; } = metadata;
    public string ThumbnailPath => Path.ChangeExtension(VideoPath, ".thumb.jpg");

    /// <summary>Preview picture for YouTube (frame at the funniest moment with the title on it).</summary>
    public string YouTubeThumbnailPath => Path.ChangeExtension(VideoPath, ".youtube.jpg");

    /// <summary>When it was recorded: Twitch date for old clips, otherwise when UCut made it.</summary>
    public DateTimeOffset Date => Metadata.Origin?.RecordedAt ?? Metadata.CreatedAt;

    public string Title => string.IsNullOrWhiteSpace(Metadata.YouTube.Title) ? Path.GetFileNameWithoutExtension(VideoPath) : Metadata.YouTube.Title;

    public void Save()
    {
        Metadata.Save(MetadataPath);
        ClipLibrary.Remember(this);
    }
}

/// <summary>Reads all finished clips (OutputFolder\&lt;Spiel&gt;\*.json next to *.mp4).</summary>
public static class ClipLibrary
{
    // Info files already read, by path, with the file's time and size. With a thousand clips, reading every
    // info file again after each change made the whole window stutter; unchanged files keep their clip object.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, long Size, LibraryClip Clip)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>After UCut itself saved the info file: the object in memory is the current one.</summary>
    internal static void Remember(LibraryClip clip)
    {
        try
        {
            var info = new FileInfo(clip.MetadataPath);
            if (info.Exists)
            {
                Cache[Path.GetFullPath(clip.MetadataPath)] = (info.LastWriteTimeUtc, info.Length, clip);
            }
        }
        catch (IOException)
        {
        }
    }

    public static List<LibraryClip> Load(string outputFolder)
    {
        var result = new List<LibraryClip>();
        if (!Directory.Exists(outputFolder))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(outputFolder))
        {
            if (Path.GetFileName(dir).StartsWith('_'))
            {
                continue; // _Twitch-Originale and other internal folders
            }

            foreach (var json in Directory.EnumerateFiles(dir, "*.json"))
            {
                var video = Path.ChangeExtension(json, ".mp4");
                if (!File.Exists(video))
                {
                    continue;
                }

                try
                {
                    var key = Path.GetFullPath(json);
                    var info = new FileInfo(json);
                    if (Cache.TryGetValue(key, out var hit) && hit.Stamp == info.LastWriteTimeUtc && hit.Size == info.Length)
                    {
                        result.Add(hit.Clip);
                        continue;
                    }

                    var clip = new LibraryClip(video, json, ClipMetadata.Load(json));
                    Cache[key] = (info.LastWriteTimeUtc, info.Length, clip);
                    result.Add(clip);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    // A broken or foreign json is skipped, the rest of the library still loads.
                }
            }
        }

        return result.OrderByDescending(c => c.Date).ToList();
    }

    /// <summary>Preview picture (first second, small JPEG), made once and kept next to the clip.</summary>
    public static async Task<string?> EnsureThumbnailAsync(LibraryClip clip, string ffmpegPath, CancellationToken ct = default)
    {
        if (File.Exists(clip.ThumbnailPath))
        {
            return clip.ThumbnailPath;
        }

        var at = Math.Min(1.0, clip.Metadata.DurationSeconds / 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpegPath,
                ["-v", "error", "-y", "-ss", at, "-i", clip.VideoPath, "-frames:v", "1", "-vf", "scale=360:-2", "-q:v", "4", clip.ThumbnailPath],
                null, ct).ConfigureAwait(false);
            return r.ExitCode == 0 && File.Exists(clip.ThumbnailPath) ? clip.ThumbnailPath : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Deletes video, info file and preview picture.</summary>
    public static void Delete(LibraryClip clip)
    {
        Cache.TryRemove(Path.GetFullPath(clip.MetadataPath), out _);
        foreach (var p in new[] { clip.VideoPath, clip.MetadataPath, clip.ThumbnailPath, clip.YouTubeThumbnailPath })
        {
            Media.FileRetry.Delete(p);
        }
    }
}
