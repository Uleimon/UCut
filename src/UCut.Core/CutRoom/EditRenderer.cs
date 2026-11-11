using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace UCut.CutRoom;

public enum ExportFormat
{
    /// <summary>Video with H.264 and AAC sound, plays everywhere (TikTok, YouTube, Instagram).</summary>
    Mp4H264,

    /// <summary>Only the sound as MP3.</summary>
    Mp3,
}

public enum ExportSize
{
    Original,
    Vertical1080,
    Vertical720,
    Landscape1080,
}

public enum ExportQuality
{
    High,
    Medium,
    Small,
}

public sealed record ExportOptions(ExportFormat Format = ExportFormat.Mp4H264, ExportSize Size = ExportSize.Original,
                                   ExportQuality Quality = ExportQuality.High, int AudioKbps = 192)
{
    public string Extension => Format == ExportFormat.Mp3 ? ".mp3" : ".mp4";
}

/// <summary>Builds and runs the ffmpeg command that renders an <see cref="EditProject"/>.</summary>
public static class EditRenderer
{
    public static List<string> BuildArguments(EditProject p, ExportOptions o, string output)
    {
        if (p.Segments.Count == 0 || p.Duration <= 0)
        {
            throw new InvalidOperationException("Im Schnitt ist nichts mehr übrig.");
        }

        var ci = CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("0.###", ci);
        var args = new List<string> { "-hide_banner", "-y", "-i", p.Source };
        var music = p.Music is { Muted: false, Volume: > 0 } m && File.Exists(m.Path) ? m : null;
        if (music is not null)
        {
            if (music.Loop)
            {
                args.AddRange(["-stream_loop", "-1"]);
            }

            args.AddRange(["-i", music.Path]);
        }

        var n = p.Segments.Count;
        var g = new StringBuilder();
        var video = o.Format == ExportFormat.Mp4H264;
        var total = p.Duration;

        if (video)
        {
            for (var i = 0; i < n; i++)
            {
                g.Append(ci, $"[0:v]trim=start={F(p.Segments[i].In)}:end={F(p.Segments[i].Out)},setpts=PTS-STARTPTS[v{i}];");
            }

            for (var i = 0; i < n; i++)
            {
                g.Append(ci, $"[v{i}]");
            }

            g.Append(ci, $"concat=n={n}:v=1:a=0,{SizeFilter(o.Size, p.Width, p.Height)}");
            if (p.FadeIn > 0)
            {
                g.Append(ci, $",fade=t=in:st=0:d={F(p.FadeIn)}");
            }

            if (p.FadeOut > 0)
            {
                g.Append(ci, $",fade=t=out:st={F(Math.Max(0, total - p.FadeOut))}:d={F(p.FadeOut)}");
            }

            g.Append(",format=yuv420p[vout];");
        }

        var mixInputs = new List<string>();
        for (var k = 0; k < p.Tracks.Count; k++)
        {
            var t = p.Tracks[k];
            if (t.Muted || t.Volume <= 0)
            {
                continue;
            }

            for (var i = 0; i < n; i++)
            {
                g.Append(ci, $"[0:a:{k}]atrim=start={F(p.Segments[i].In)}:end={F(p.Segments[i].Out)},asetpts=PTS-STARTPTS[a{k}s{i}];");
            }

            for (var i = 0; i < n; i++)
            {
                g.Append(ci, $"[a{k}s{i}]");
            }

            g.Append(ci, $"concat=n={n}:v=0:a=1,volume={F(t.Volume)}[a{k}];");
            mixInputs.Add($"[a{k}]");
        }

        if (music is not null)
        {
            var delayMs = (int)Math.Round(Math.Max(0, music.StartAt) * 1000);
            var fade = music.FadeOutSeconds > 0 ? $",afade=t=out:st={F(Math.Max(0, total - music.FadeOutSeconds))}:d={F(music.FadeOutSeconds)}" : "";
            g.Append(ci, $"[1:a]aresample=48000,volume={F(music.Volume)},adelay={delayMs}:all=1,atrim=0:{F(total)}{fade}[mus];");
            mixInputs.Add("[mus]");
        }

        var hasAudio = mixInputs.Count > 0;
        if (hasAudio)
        {
            g.Append(string.Concat(mixInputs));
            g.Append(mixInputs.Count == 1 ? "anull" : $"amix=inputs={mixInputs.Count}:normalize=0:duration=longest");
            g.Append(ci, $",atrim=0:{F(total)}");
            if (p.FadeIn > 0)
            {
                g.Append(ci, $",afade=t=in:st=0:d={F(p.FadeIn)}");
            }

            if (p.FadeOut > 0)
            {
                g.Append(ci, $",afade=t=out:st={F(Math.Max(0, total - p.FadeOut))}:d={F(p.FadeOut)}");
            }

            g.Append("[aout]");
        }
        else if (!video)
        {
            throw new InvalidOperationException("Für MP3 muss mindestens eine Tonspur an sein.");
        }

        args.AddRange(["-filter_complex", g.ToString().TrimEnd(';')]);
        if (video)
        {
            var (crf, preset) = o.Quality switch
            {
                ExportQuality.High => ("18", "slow"),
                ExportQuality.Medium => ("22", "medium"),
                _ => ("27", "veryfast"),
            };
            args.AddRange(["-map", "[vout]"]);
            if (hasAudio)
            {
                args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", $"{o.AudioKbps}k"]);
            }

            args.AddRange(["-c:v", "libx264", "-preset", preset, "-crf", crf, "-movflags", "+faststart"]);
        }
        else
        {
            args.AddRange(["-map", "[aout]", "-vn", "-c:a", "libmp3lame", "-b:a", $"{o.AudioKbps}k"]);
        }

        args.AddRange(["-t", F(total), "-progress", "pipe:1", "-nostats", output]);
        return args;
    }

    internal static string SizeFilter(ExportSize size, int srcW, int srcH)
    {
        var (w, h) = size switch
        {
            ExportSize.Vertical1080 => (1080, 1920),
            ExportSize.Vertical720 => (720, 1280),
            ExportSize.Landscape1080 => (1920, 1080),
            _ => (srcW - (srcW % 2), srcH - (srcH % 2)),
        };
        return size == ExportSize.Original
            ? $"scale={w}:{h},setsar=1"
            : $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1";
    }

    /// <summary>Renders and reports progress 0..1 (from ffmpeg's -progress output).</summary>
    public static async Task RenderAsync(string ffmpegPath, EditProject project, ExportOptions options, string output,
                                         IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var args = BuildArguments(project, options, output);
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"'{ffmpegPath}' konnte nicht gestartet werden. Ist ffmpeg installiert?", ex);
        }

        var stderr = proc.StandardError.ReadToEndAsync(ct);
        var total = project.Duration;
        try
        {
            while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && total > 0)
                {
                    progress?.Report(Math.Clamp(us / 1e6 / total, 0, 1));
                }
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            TryDelete(output);
            throw;
        }

        if (proc.ExitCode != 0)
        {
            var err = await stderr.ConfigureAwait(false);
            TryDelete(output);
            throw new InvalidOperationException($"Export fehlgeschlagen (ffmpeg {proc.ExitCode}):\n{(err.Length > 1500 ? err[^1500..] : err)}");
        }

        progress?.Report(1);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
