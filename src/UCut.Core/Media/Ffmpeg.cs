using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UCut.Media;

public sealed record VideoInfo(int Width, int Height, double DurationSeconds, bool HasAudio)
{
    /// <summary>One name per audio stream (OBS track title or "Audio 1", "Audio 2", ...).</summary>
    public IReadOnlyList<string> AudioTracks { get; init; } = [];
}

/// <summary>Loudness of the clip over time (momentary loudness in LUFS, one value per 0.1 s).</summary>
public sealed record LoudnessProfile(IReadOnlyList<(double Time, double Lufs)> Points);

/// <summary>Thin wrapper around ffmpeg/ffprobe.</summary>
public sealed partial class Ffmpeg(string ffmpegPath, string ffprobePath)
{
    public string FfmpegPath { get; } = ffmpegPath;

    public async Task<VideoInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunCheckedAsync(ffprobePath,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height:stream_tags=title,handler_name", "-of", "json", file], ct: ct).ConfigureAwait(false);
        return ParseProbe(r.StdOut);
    }

    internal static VideoInfo ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int w = 0, h = 0;
        var audioTitles = new List<string>();
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            if (type == "video" && w == 0)
            {
                w = s.GetProperty("width").GetInt32();
                h = s.GetProperty("height").GetInt32();
            }
            else if (type == "audio")
            {
                // MKV keeps track titles in "title", MP4 in "handler_name" (default names like SoundHandler mean: no name).
                string? title = null;
                if (s.TryGetProperty("tags", out var tags))
                {
                    title = tags.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title) && tags.TryGetProperty("handler_name", out var hn)
                        && hn.GetString() is { } handler && !handler.Contains("Handler", StringComparison.OrdinalIgnoreCase))
                    {
                        title = handler;
                    }
                }

                audioTitles.Add(string.IsNullOrWhiteSpace(title) ? $"Audio {audioTitles.Count + 1}" : title!);
            }
        }

        var duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new VideoInfo(w, h, duration, audioTitles.Count > 0) { AudioTracks = audioTitles };
    }

    /// <summary>Extracts 16 kHz mono 16-bit WAV (the format Whisper needs).</summary>
    public Task ExtractWhisperAudioAsync(string video, string wavOut, CancellationToken ct = default) =>
        ProcessRunner.RunCheckedAsync(FfmpegPath,
            ["-v", "error", "-y", "-i", video, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavOut], ct: ct);

    public async Task<LoudnessProfile> MeasureLoudnessAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunCheckedAsync(FfmpegPath,
            ["-hide_banner", "-nostats", "-i", file, "-vn", "-af", "ebur128", "-f", "null", "-"], ct: ct).ConfigureAwait(false);
        return ParseEbur128(r.StdErr);
    }

    internal static LoudnessProfile ParseEbur128(string stderr)
    {
        var points = new List<(double, double)>();
        foreach (Match m in Ebur128Line().Matches(stderr))
        {
            var t = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
            var lufs = double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            points.Add((t, lufs));
        }

        return new LoudnessProfile(points);
    }

    /// <summary>Grabs one frame as raw 8-bit grey pixels, scaled to the given width (height keeps the aspect ratio).</summary>
    public Task<(byte[] Pixels, int Width, int Height)> GrabGrayFrameAsync(string video, double atSeconds, int width, int height, CancellationToken ct = default) =>
        GrabFrameAsync(video, atSeconds, width, height, "gray", ct);

    /// <summary>Grabs one frame as raw pixels (pixFmt "gray" = 1 byte, "rgb24" = 3 bytes per pixel), scaled to width x height.</summary>
    public async Task<(byte[] Pixels, int Width, int Height)> GrabFrameAsync(string video, double atSeconds, int width, int height, string pixFmt, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ucut-frame-{Guid.NewGuid():N}.gray");
        try
        {
            await ProcessRunner.RunCheckedAsync(FfmpegPath,
            [
                "-v", "error", "-y", "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", video,
                "-frames:v", "1", "-vf", $"scale={width}:{height}", "-f", "rawvideo", "-pix_fmt", pixFmt, tmp,
            ], ct: ct).ConfigureAwait(false);
            return (await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false), width, height);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Mono 16-bit samples of a stretch of the sound (for the laugh detector).</summary>
    public async Task<short[]> ReadPcmAsync(string file, double start, double duration, int sampleRate, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ucut-pcm-{Guid.NewGuid():N}.raw");
        try
        {
            var ci = CultureInfo.InvariantCulture;
            await ProcessRunner.RunCheckedAsync(FfmpegPath,
            [
                "-v", "error", "-y", "-ss", start.ToString("0.###", ci), "-t", duration.ToString("0.###", ci), "-i", file,
                "-vn", "-ac", "1", "-ar", sampleRate.ToString(ci), "-f", "s16le", tmp,
            ], ct: ct).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return samples;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [GeneratedRegex(@"t:\s*(?<t>[0-9.]+)\s+TARGET:.*?M:\s*(?<m>-?[0-9.]+|-inf)", RegexOptions.CultureInvariant)]
    private static partial Regex Ebur128Line();
}
