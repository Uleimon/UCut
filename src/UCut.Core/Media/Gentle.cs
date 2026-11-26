using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UCut.Media;

/// <summary>
/// Keeps the PC usable while UCut works: helper programs (ffmpeg, yt-dlp, Whisper) run at lower priority
/// and the video encoder uses only half the cores. Ulei's mouse lagged badly while clips were being made.
/// </summary>
public static partial class Gentle
{
    /// <summary>Threads for x264: half the logical cores, at least 2.</summary>
    public static string EncoderThreads => Math.Max(2, Environment.ProcessorCount / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Lowers a started helper program to "below normal", so mouse, games and OBS come first.</summary>
    public static void Lower(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Already finished or not allowed: it then just runs at normal priority.
        }
    }

    /// <summary>Windows background mode for this process (low CPU, disk and memory priority). Used by the Whisper worker.</summary>
    public static void BackgroundModeForThisProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundBegin);
        }
        else
        {
            Lower(Process.GetCurrentProcess());
        }
    }

    private const uint ProcessModeBackgroundBegin = 0x00100000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(IntPtr process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();
}
