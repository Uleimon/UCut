using System.Globalization;
using UCut.Settings;

namespace UCut.Editing;

public enum ReframeMode
{
    /// <summary>Find the facecam, put it on top and the game below (falls back to <see cref="Crop"/> when no cam is found).</summary>
    Split,

    /// <summary>Cut the sides away so the picture fills the whole 9:16 frame.</summary>
    Crop,

    /// <summary>Keep the whole picture in the middle, blurred copy of it fills the rest.</summary>
    Blur,
}

/// <summary>Rectangle as fractions of the picture size (0..1).</summary>
public sealed record NormRect(double X, double Y, double Width, double Height)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"x {X:0.###}, y {Y:0.###}, b {Width:0.###}, h {Height:0.###}");
}

/// <summary>How a non-vertical source is turned into 9:16.</summary>
public sealed record ReframePlan(ReframeMode Mode, int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight, double FocusX,
                                 NormRect? Cam = null, double MaxCamHeight = 0.45)
{
    /// <summary>Height of the cam strip on top (pixels of the output), 0 when not in split mode.</summary>
    public int CamStripHeight
    {
        get
        {
            if (Mode != ReframeMode.Split || Cam is null)
            {
                return 0;
            }

            var camPxW = Cam.Width * SourceWidth;
            var camPxH = Cam.Height * SourceHeight;
            var h = TargetWidth * camPxH / camPxW;
            return Even(Math.Min(h, TargetHeight * MaxCamHeight));
        }
    }

    /// <summary>ffmpeg filter that reads [in] and writes [out].</summary>
    public string Filter(string input, string output)
    {
        var ci = CultureInfo.InvariantCulture;
        var w = TargetWidth;
        var h = TargetHeight;
        var focus = FocusX.ToString("0.###", ci);
        switch (Mode)
        {
            case ReframeMode.Split when Cam is not null:
            {
                var camH = CamStripHeight;
                var gameH = h - camH;

                // Cam crop in source pixels; if the cam would be taller than allowed, cut its top and bottom evenly.
                var cx = EvenOffset(Cam.X * SourceWidth);
                var cy = EvenOffset(Cam.Y * SourceHeight);
                var cw = Even(Cam.Width * SourceWidth);
                var ch = Even(Cam.Height * SourceHeight);
                var neededCh = Even((double)cw * camH / w);
                if (neededCh < ch)
                {
                    cy += EvenOffset((ch - neededCh) / 2.0);
                    ch = neededCh;
                }

                return $"{input}split=2[rf_cam][rf_game];" +
                       $"[rf_cam]crop={cw}:{ch}:{cx}:{cy},scale={w}:{camH},setsar=1[rf_camt];" +
                       $"[rf_game]crop=w=min(iw\\,trunc(ih*{w}/{gameH}/2)*2):h=ih:x=(iw-ow)*{focus}:y=0,scale={w}:{gameH},setsar=1[rf_gamet];" +
                       $"[rf_camt][rf_gamet]vstack=inputs=2{output}";
            }

            case ReframeMode.Blur:
                return $"{input}split=2[rf_bg][rf_fg];" +
                       $"[rf_bg]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur=20:2,eq=brightness=-0.08[rf_bgb];" +
                       $"[rf_fg]scale={w}:-2[rf_fgs];" +
                       $"[rf_bgb][rf_fgs]overlay=(W-w)/2:(H-h)/2,setsar=1{output}";

            default:
                // Crop a 9:16 window out of the full height; FocusX picks where (0 = left edge, 0.5 = centre, 1 = right edge).
                return $"{input}crop=w=trunc(ih*{w}/{h}/2)*2:h=ih:x=(iw-ow)*{focus}:y=0,scale={w}:{h},setsar=1{output}";
        }
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    private static int EvenOffset(double v) => Math.Max(0, (int)Math.Round(v / 2) * 2);
}

public static class Reframer
{
    /// <summary>Aspect ratios up to this count as "already vertical" (9:16 = 0.5625). 3:4 (0.75) and wider are reframed.</summary>
    internal const double VerticalLimit = 0.66;

    public static bool NeedsReframe(int width, int height, ReframeSettings settings) =>
        settings.Enabled && width > 0 && height > 0 && (double)width / height > VerticalLimit;

    /// <summary>
    /// Returns null when the clip is already vertical. In split mode <paramref name="cam"/> is the found or configured cam;
    /// without one the plan falls back to <see cref="ReframeSettings.FallbackMode"/>.
    /// </summary>
    public static ReframePlan? Plan(int width, int height, ReframeSettings settings, NormRect? cam = null)
    {
        if (!NeedsReframe(width, height, settings))
        {
            return null;
        }

        var mode = settings.Mode;
        if (mode == ReframeMode.Split && cam is null)
        {
            mode = settings.FallbackMode == ReframeMode.Split ? ReframeMode.Crop : settings.FallbackMode;
        }

        return new ReframePlan(mode, width, height, settings.TargetWidth, settings.TargetHeight, Math.Clamp(settings.FocusX, 0, 1),
                               mode == ReframeMode.Split ? cam : null, Math.Clamp(settings.MaxCamHeight, 0.15, 0.7));
    }
}
