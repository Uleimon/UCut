using System.Globalization;
using UCut.Media;

namespace UCut.CutRoom;

/// <summary>Pictures for the timeline: a strip of frames for the video track and one waveform per audio track.</summary>
public static class TimelineImages
{
    /// <summary>Frames across the whole source, side by side in one image.</summary>
    public static async Task<string?> FilmstripAsync(string ffmpeg, string source, double duration, string output, int frames = 24, CancellationToken ct = default)
    {
        var fps = (frames / Math.Max(0.5, duration)).ToString("0.#####", CultureInfo.InvariantCulture);
        return await RunAsync(ffmpeg, ["-v", "error", "-y", "-i", source, "-vf", $"fps={fps},scale=-2:96,tile={frames}x1", "-frames:v", "1", "-q:v", "5", output], output, ct).ConfigureAwait(false);
    }

    /// <summary>Waveform of one audio stream as a PNG with transparent background.</summary>
    public static async Task<string?> WaveformAsync(string ffmpeg, string source, int track, string output, string color = "#5eead4", CancellationToken ct = default) =>
        await RunAsync(ffmpeg, ["-v", "error", "-y", "-i", source, "-filter_complex",
            $"[0:a:{track}]aformat=channel_layouts=mono,showwavespic=s=2400x120:colors={color}", "-frames:v", "1", output], output, ct).ConfigureAwait(false);

    private static async Task<string?> RunAsync(string ffmpeg, IReadOnlyList<string> args, string output, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, args, null, ct).ConfigureAwait(false);
            return r.ExitCode == 0 && File.Exists(output) ? output : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
