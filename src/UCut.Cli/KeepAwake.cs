using System.Runtime.InteropServices;

namespace UCut.Cli;

/// <summary>Stops Windows from going to sleep while UCut works (long Twitch runs overnight). Screen may still turn off.</summary>
internal static partial class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    public static void On()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        }
    }

    public static void Off()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsContinuous);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
