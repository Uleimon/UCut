using System.Globalization;
using System.Text;

namespace UCut.CutRoom;

public enum EffectKind
{
    /// <summary>Picture, GIF or video (meme, sticker, emoji) on top of the clip.</summary>
    Meme,

    /// <summary>Sound effect (vine boom, bruh, airhorn ...).</summary>
    Sound,

    /// <summary>Big text popping up ("BRUH", "XD", "NEIN").</summary>
    Text,

    /// <summary>Quick zoom into the picture (reaction zoom).</summary>
    Zoom,

    /// <summary>The picture shakes (explosion, scream).</summary>
    Shake,

    /// <summary>Short white flash.</summary>
    Flash,
}

public enum EffectAnimation
{
    None,
    Pop,
    Fade,
    SlideUp,
}

/// <summary>One meme/effect on the timeline. Times are timeline seconds (after the cut), position as fractions of the picture.</summary>
public sealed class EffectItem
{
    public EffectKind Kind { get; set; }
    public double Start { get; set; }
    public double Duration { get; set; } = 1.5;

    /// <summary>Meme or sound file.</summary>
    public string Path { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Center of the meme/text, 0..1.</summary>
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.35;

    /// <summary>Meme width as share of the picture width; text size as share of the picture height; zoom amount.</summary>
    public double Size { get; set; } = 0.5;

    public double Volume { get; set; } = 1.0;
    public EffectAnimation Animation { get; set; } = EffectAnimation.Pop;

    /// <summary>Make green (green screen memes) transparent.</summary>
    public bool RemoveGreen { get; set; }

    /// <summary>Meme videos with sound play their sound too (set when the file is added).</summary>
    public bool HasAudio { get; set; }

    public double End => Start + Duration;

    public string Label => Kind switch
    {
        EffectKind.Meme => "Meme: " + System.IO.Path.GetFileNameWithoutExtension(Path),
        EffectKind.Sound => "Sound: " + System.IO.Path.GetFileNameWithoutExtension(Path),
        EffectKind.Text => "Text: " + Text,
        EffectKind.Zoom => "Zoom",
        EffectKind.Shake => "Wackeln",
        _ => "Blitz",
    };

    public static bool IsImage(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
    public static bool IsGif(string path) => System.IO.Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
    public static bool IsSound(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav" or ".ogg" or ".m4a" or ".aac" or ".flac";
    public static bool IsVideo(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".webm" or ".mov" or ".mkv";
}

/// <summary>Turns the effects into ffmpeg inputs, filter steps and an ASS file for the texts.</summary>
public static class EffectFilters
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.###", Ci);

    /// <summary>Extra "-i" arguments, in the order the filter graph refers to them.</summary>
    public static List<string> Inputs(IEnumerable<EffectItem> effects)
    {
        var args = new List<string>();
        foreach (var e in WithFile(effects))
        {
            if (e.Kind == EffectKind.Meme && EffectItem.IsImage(e.Path))
            {
                args.AddRange(["-loop", "1", "-t", F(e.Duration)]);
            }
            else if (e.Kind == EffectKind.Meme && EffectItem.IsGif(e.Path))
            {
                args.AddRange(["-ignore_loop", "0", "-t", F(e.Duration)]);
            }
            else if (e.Kind == EffectKind.Meme)
            {
                args.AddRange(["-stream_loop", "-1", "-t", F(e.Duration)]);
            }

            args.AddRange(["-i", e.Path]);
        }

        return args;
    }

    /// <summary>Effects that bring their own file, in input order.</summary>
    public static List<EffectItem> WithFile(IEnumerable<EffectItem> effects) =>
        effects.Where(e => e.Kind is EffectKind.Meme or EffectKind.Sound && File.Exists(e.Path)).ToList();

    /// <summary>
    /// Video steps from [in] to [out]: zoom, shake and flash on the picture, then the memes on top.
    /// <paramref name="firstInput"/> is the ffmpeg input index of the first effect file.
    /// </summary>
    public static string Video(IReadOnlyList<EffectItem> effects, int firstInput, int width, int height, string input, string output, string? assFile)
    {
        var g = new StringBuilder();
        var cur = input;
        var step = 0;
        string Next() => $"[fx{step++}]";

        foreach (var z in effects.Where(e => e.Kind == EffectKind.Zoom))
        {
            // Eases in for 0.15 s, holds, eases out. Size = how much bigger (0.3 = 30 %).
            var amount = Math.Clamp(z.Size, 0.05, 1.5);
            var ramp = $"min(1,min((t-{F(z.Start)})/0.15,({F(z.End)}-t)/0.15))";
            var f = $"(1+{F(amount)}*between(t,{F(z.Start)},{F(z.End)})*{ramp})";
            var cx = F(Math.Clamp(z.X, 0, 1));
            var cy = F(Math.Clamp(z.Y, 0, 1));
            var o = Next();
            g.Append(Ci, $"{cur}scale=w='trunc(iw*{f}/2)*2':h='trunc(ih*{f}/2)*2':eval=frame,crop={width}:{height}:'min(max(iw*{cx}-{width}/2,0),iw-{width})':'min(max(ih*{cy}-{height}/2,0),ih-{height})'{o};");
            cur = o;
        }

        var shakes = effects.Where(e => e.Kind == EffectKind.Shake).ToList();
        if (shakes.Count > 0)
        {
            var pad = Math.Max(8, width / 40);
            var on = string.Join("+", shakes.Select(s => $"between(t,{F(s.Start)},{F(s.End)})"));
            var o = Next();
            g.Append(Ci, $"{cur}crop=w=iw-{2 * pad}:h=ih-{2 * pad}:x='{pad}+{pad}*sin(t*71)*min(1,{on})':y='{pad}+{pad}*cos(t*53)*min(1,{on})',scale={width}:{height}{o};");
            cur = o;
        }

        foreach (var fl in effects.Where(e => e.Kind == EffectKind.Flash))
        {
            var d = Math.Min(fl.Duration, 0.6);
            var o = Next();
            g.Append(Ci, $"{cur}eq=brightness='0.8*max(0,1-(t-{F(fl.Start)})/{F(d)})*between(t,{F(fl.Start)},{F(fl.Start + d)})':eval=frame{o};");
            cur = o;
        }

        var files = WithFile(effects);
        for (var i = 0; i < files.Count; i++)
        {
            var m = files[i];
            if (m.Kind != EffectKind.Meme)
            {
                continue;
            }

            var w = Math.Max(16, (int)(width * Math.Clamp(m.Size, 0.05, 1.0)) / 2 * 2);
            var key = m.RemoveGreen ? ",chromakey=0x00FF00:0.25:0.08" : "";
            var fade = m.Animation is EffectAnimation.Fade or EffectAnimation.Pop or EffectAnimation.SlideUp
                ? $",fade=t=in:st=0:d=0.15:alpha=1,fade=t=out:st={F(Math.Max(0, m.Duration - 0.15))}:d=0.15:alpha=1"
                : "";
            var meme = $"[m{i}]";
            g.Append(Ci, $"[{firstInput + i}:v]scale={w}:-2,format=rgba{key}{fade},setpts=PTS-STARTPTS+{F(m.Start)}/TB{meme};");

            var x = $"W*{F(Math.Clamp(m.X, 0, 1))}-w/2";
            var y = $"H*{F(Math.Clamp(m.Y, 0, 1))}-h/2";
            if (m.Animation == EffectAnimation.SlideUp)
            {
                y += $"+(1-min(1,(t-{F(m.Start)})/0.25))*H*0.15";
            }
            else if (m.Animation == EffectAnimation.Pop)
            {
                // A short bounce: starts a bit lower, overshoots, settles.
                y += $"+H*0.04*exp(-(t-{F(m.Start)})*9)*cos((t-{F(m.Start)})*25)";
            }

            var o = Next();
            g.Append(Ci, $"{cur}{meme}overlay=x='{x}':y='{y}':eval=frame:enable='between(t,{F(m.Start)},{F(m.End)})'{o};");
            cur = o;
        }

        if (assFile is not null)
        {
            var o = Next();
            g.Append(Ci, $"{cur}ass={assFile}{o};");
            cur = o;
        }

        g.Append(Ci, $"{cur}null{output};");
        return g.ToString();
    }

    /// <summary>Audio steps for sound effects and the sound of meme videos; returns the labels to mix.</summary>
    public static (string Graph, List<string> Labels) Audio(IReadOnlyList<EffectItem> effects, int firstInput)
    {
        var g = new StringBuilder();
        var labels = new List<string>();
        var files = WithFile(effects);
        for (var i = 0; i < files.Count; i++)
        {
            var e = files[i];
            if (e.Kind == EffectKind.Meme && !e.HasAudio)
            {
                continue;
            }

            var ms = (int)Math.Round(Math.Max(0, e.Start) * 1000);
            var label = $"[sfx{i}]";
            g.Append(Ci, $"[{firstInput + i}:a]aresample=48000,atrim=0:{F(e.Duration)},volume={F(e.Volume)},adelay={ms}:all=1{label};");
            labels.Add(label);
        }

        return (g.ToString(), labels);
    }

    /// <summary>ASS subtitle file with the text pop-ups (white, thick black outline, pop/slide animation).</summary>
    public static string? TextAss(IReadOnlyList<EffectItem> effects, int width, int height, string font)
    {
        var texts = effects.Where(e => e.Kind == EffectKind.Text && e.Text.Trim().Length > 0).ToList();
        if (texts.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine(Ci, $"PlayResX: {width}");
        sb.AppendLine(Ci, $"PlayResY: {height}");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine(Ci, $"Style: Fx,{font},60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H96000000,-1,0,0,0,100,100,0,0,1,6,3,5,20,20,20,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        foreach (var t in texts)
        {
            var size = (int)Math.Round(height * Math.Clamp(t.Size, 0.02, 0.3));
            var x = (int)(width * Math.Clamp(t.X, 0, 1));
            var y = (int)(height * Math.Clamp(t.Y, 0, 1));
            var anim = t.Animation switch
            {
                EffectAnimation.Pop => $"\\pos({x},{y})\\fscx30\\fscy30\\t(0,120,\\fscx115\\fscy115)\\t(120,220,\\fscx100\\fscy100)\\fad(0,150)",
                EffectAnimation.Fade => $"\\pos({x},{y})\\fad(200,200)",
                EffectAnimation.SlideUp => $"\\move({x},{y + height / 10},{x},{y},0,250)\\fad(100,150)",
                _ => $"\\pos({x},{y})",
            };
            var text = t.Text.Replace("{", "(", StringComparison.Ordinal).Replace("}", ")", StringComparison.Ordinal)
                             .Replace("\\", "/", StringComparison.Ordinal).Replace("\n", "\\N", StringComparison.Ordinal);
            sb.AppendLine(Ci, $"Dialogue: 1,{Time(t.Start)},{Time(t.End)},Fx,,0,0,0,,{{{anim}\\fs{size}}}{text}");
        }

        return sb.ToString();
    }

    internal static string Time(double s)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, s));
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }
}
