using System.Globalization;
using UCut.Settings;

namespace UCut.Editing;

public enum ReframeMode
{
    /// <summary>Cut the sides away so the picture fills the whole 9:16 frame.</summary>
    Crop,

    /// <summary>Keep the whole picture in the middle, blurred copy of it fills the rest.</summary>
    Blur,
}

/// <summary>How a non-vertical source is turned into 9:16.</summary>
public sealed record ReframePlan(ReframeMode Mode, int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight, double FocusX)
{
    /// <summary>ffmpeg filter that reads [in] and writes [out].</summary>
    public string Filter(string input, string output)
    {
        var ci = CultureInfo.InvariantCulture;
        var w = TargetWidth;
        var h = TargetHeight;
        return Mode switch
        {
            // Crop a 9:16 window out of the full height; FocusX picks where (0 = left edge, 0.5 = centre, 1 = right edge).
            ReframeMode.Crop =>
                $"{input}crop=w=trunc(ih*{w}/{h}/2)*2:h=ih:x=(iw-ow)*{FocusX.ToString("0.###", ci)}:y=0,scale={w}:{h},setsar=1{output}",
            _ =>
                $"{input}split=2[rf_bg][rf_fg];" +
                $"[rf_bg]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur=20:2,eq=brightness=-0.08[rf_bgb];" +
                $"[rf_fg]scale={w}:-2[rf_fgs];" +
                $"[rf_bgb][rf_fgs]overlay=(W-w)/2:(H-h)/2,setsar=1{output}",
        };
    }
}

public static class Reframer
{
    /// <summary>Aspect ratios up to this count as "already vertical" (9:16 = 0.5625). 3:4 (0.75) and wider are reframed.</summary>
    internal const double VerticalLimit = 0.66;

    /// <summary>Returns null when the clip is already vertical and nothing has to be changed.</summary>
    public static ReframePlan? Plan(int width, int height, ReframeSettings settings)
    {
        if (!settings.Enabled || width <= 0 || height <= 0 || (double)width / height <= VerticalLimit)
        {
            return null;
        }

        return new ReframePlan(settings.Mode, width, height, settings.TargetWidth, settings.TargetHeight, Math.Clamp(settings.FocusX, 0, 1));
    }
}
