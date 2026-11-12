using System.Diagnostics;
using System.Text;

namespace UCut.Media;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs external tools (ffmpeg, ffprobe) with an argument list, no shell involved.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"'{fileName}' konnte nicht gestartet werden. Ist es installiert und in den Einstellungen bzw. im PATH eingetragen?", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public static async Task<ProcessResult> RunCheckedAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, CancellationToken ct = default)
    {
        var result = await RunAsync(fileName, arguments, workingDirectory, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var tail = result.StdErr.Length > 2000 ? result.StdErr[^2000..] : result.StdErr;
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} ist mit Code {result.ExitCode} fehlgeschlagen:\n{tail}");
        }

        return result;
    }
}
