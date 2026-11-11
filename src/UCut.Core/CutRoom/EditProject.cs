using System.Text.Json;
using UCut.Media;
using UCut.Settings;

namespace UCut.CutRoom;

/// <summary>A piece of the source video that stays in the cut (source times in seconds).</summary>
public sealed class EditSegment
{
    public double In { get; set; }
    public double Out { get; set; }
    public double Duration => Math.Max(0, Out - In);
}

/// <summary>One audio stream of the source (OBS can record game, mic, Discord on separate tracks).</summary>
public sealed class AudioTrackSettings
{
    public string Name { get; set; } = "";
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
}

/// <summary>Extra music under the clip (mp3, wav, ...).</summary>
public sealed class MusicTrack
{
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public double Volume { get; set; } = 0.25;

    /// <summary>Where on the timeline the music starts.</summary>
    public double StartAt { get; set; }

    public double FadeOutSeconds { get; set; } = 1.5;
    public bool Loop { get; set; } = true;
    public bool Muted { get; set; }
}

/// <summary>
/// Non-destructive edit of one video: which parts stay, how loud each audio track is, optional music.
/// The source file is never changed; the export renders a new file with ffmpeg.
/// </summary>
public sealed class EditProject
{
    public string Source { get; set; } = "";
    public double SourceDuration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<EditSegment> Segments { get; set; } = [];
    public List<AudioTrackSettings> Tracks { get; set; } = [];
    public MusicTrack? Music { get; set; }

    /// <summary>Memes, sounds, texts, zooms ... on top (timeline times).</summary>
    public List<EffectItem> Effects { get; set; } = [];

    /// <summary>Fade from/to black at start and end of the cut, seconds (0 = off).</summary>
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }

    public double Duration => Segments.Sum(s => s.Duration);

    public static EditProject FromSource(string source, VideoInfo info) => new()
    {
        Source = Path.GetFullPath(source),
        SourceDuration = info.DurationSeconds,
        Width = info.Width,
        Height = info.Height,
        Segments = [new EditSegment { In = 0, Out = info.DurationSeconds }],
        Tracks = info.AudioTracks.Select(n => new AudioTrackSettings { Name = n }).ToList(),
    };

    /// <summary>Which segment and which source time a timeline position falls on.</summary>
    public (int Segment, double SourceTime) Locate(double timelineTime)
    {
        var t = Math.Clamp(timelineTime, 0, Duration);
        var start = 0.0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var d = Segments[i].Duration;
            if (t < start + d || i == Segments.Count - 1)
            {
                return (i, Segments[i].In + Math.Min(t - start, d));
            }

            start += d;
        }

        return (-1, 0);
    }

    /// <summary>Timeline position of a source time, null if that part was cut out.</summary>
    public double? ToTimeline(double sourceTime)
    {
        var start = 0.0;
        foreach (var s in Segments)
        {
            if (sourceTime >= s.In && sourceTime < s.Out)
            {
                return start + sourceTime - s.In;
            }

            start += s.Duration;
        }

        return null;
    }

    public double SegmentStart(int index) => Segments.Take(index).Sum(s => s.Duration);

    /// <summary>Cuts the segment under the playhead in two. Too close to an edge (under 0.1 s) does nothing.</summary>
    public bool SplitAt(double timelineTime)
    {
        var (i, src) = Locate(timelineTime);
        if (i < 0)
        {
            return false;
        }

        var seg = Segments[i];
        if (src - seg.In < MinPiece || seg.Out - src < MinPiece)
        {
            return false;
        }

        Segments.Insert(i + 1, new EditSegment { In = src, Out = seg.Out });
        seg.Out = src;
        return true;
    }

    public bool RemoveSegment(int index)
    {
        if (index < 0 || index >= Segments.Count || Segments.Count == 1)
        {
            return false;
        }

        var start = SegmentStart(index);
        var length = Segments[index].Duration;
        Segments.RemoveAt(index);
        RippleEffects(start, length);
        return true;
    }

    /// <summary>A stretch of the timeline was cut out: effects in it go, effects after it move up.</summary>
    private void RippleEffects(double from, double length)
    {
        Effects.RemoveAll(e => e.Start >= from && e.Start < from + length);
        foreach (var e in Effects.Where(e => e.Start >= from + length))
        {
            e.Start -= length;
        }
    }

    /// <summary>
    /// A copy with only <paramref name="length"/> seconds from <paramref name="from"/> on (for the quick preview), effects moved along.
    /// </summary>
    public EditProject PreviewPart(double from, double length)
    {
        var p = Clone();
        if (from > MinPiece)
        {
            p.TrimStartAt(from);
        }

        if (p.Duration > length)
        {
            p.TrimEndAt(length);
        }

        p.FadeIn = 0;
        p.FadeOut = 0;
        if (p.Music is { } m)
        {
            m.StartAt = Math.Max(0, m.StartAt - from);
        }

        return p;
    }

    /// <summary>Everything before the playhead goes (like "ripple trim start" in Premiere).</summary>
    public bool TrimStartAt(double timelineTime)
    {
        if (!SplitAt(timelineTime) && timelineTime <= MinPiece)
        {
            return false;
        }

        var keepFrom = Locate(timelineTime + 1e-6).Segment;
        if (keepFrom <= 0)
        {
            return false;
        }

        var removed = SegmentStart(keepFrom);
        Segments.RemoveRange(0, keepFrom);
        foreach (var e in Effects)
        {
            e.Start -= removed;
        }

        Effects.RemoveAll(e => e.End <= 0.05);
        foreach (var e in Effects.Where(e => e.Start < 0))
        {
            e.Duration = e.End;
            e.Start = 0;
        }

        return true;
    }

    /// <summary>Everything after the playhead goes.</summary>
    public bool TrimEndAt(double timelineTime)
    {
        if (!SplitAt(timelineTime) && Duration - timelineTime <= MinPiece)
        {
            return false;
        }

        var last = Locate(timelineTime - 1e-6).Segment;
        if (last < 0 || last >= Segments.Count - 1)
        {
            return false;
        }

        Segments.RemoveRange(last + 1, Segments.Count - last - 1);
        Effects.RemoveAll(e => e.Start >= Duration - 0.05);
        return true;
    }

    /// <summary>Moves a segment one place earlier or later.</summary>
    public bool MoveSegment(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= Segments.Count || target < 0 || target >= Segments.Count)
        {
            return false;
        }

        (Segments[index], Segments[target]) = (Segments[target], Segments[index]);
        return true;
    }

    private const double MinPiece = 0.1;

    public EditProject Clone() => JsonSerializer.Deserialize<EditProject>(JsonSerializer.Serialize(this, UCutSettings.JsonOptions), UCutSettings.JsonOptions)!;

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, UCutSettings.JsonOptions));

    public static EditProject Load(string path) =>
        JsonSerializer.Deserialize<EditProject>(File.ReadAllText(path), UCutSettings.JsonOptions) ?? throw new InvalidDataException(path);
}
