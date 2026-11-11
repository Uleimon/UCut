using System.Text.Json;
using System.Text.Json.Serialization;
using UCut.Analysis;

namespace UCut.Metadata;

/// <summary>
/// The info file next to every finished clip (same name, .json). Everything an upload needs, per platform.
/// Ulei can edit it before uploading; the uploader reads it 1:1.
/// </summary>
public sealed class ClipMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string Video { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Game { get; set; } = "";
    public string GameSource { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string? CutNote { get; set; }
    public string Transcript { get; set; } = "";
    public ScoreInfo Score { get; set; } = new();
    public FunninessInfo? Funniness { get; set; }
    public LayoutInfo? Layout { get; set; }

    /// <summary>Set for clips that were not recorded just now, e.g. old Twitch clips.</summary>
    public OriginInfo? Origin { get; set; }
    public string TextSource { get; set; } = "";
    public TikTokInfo TikTok { get; set; } = new();
    public YouTubeInfo YouTube { get; set; } = new();
    public InstagramInfo Instagram { get; set; } = new();

    /// <summary>Cut, layout and words of this clip, so it can be redone with corrections.</summary>
    public EditInfo? Edit { get; set; }

    /// <summary>Filled by the uploader later: platform to post id/URL.</summary>
    public Dictionary<string, string> Uploaded { get; set; } = [];

    public static ClipMetadata Create(string video, string source, string game, string gameSource, double duration, string? cutNote,
                                      string transcript, ClipScore score, ClipText text, DateTimeOffset now) => new()
    {
        Video = Path.GetFileName(video),
        SourceFile = source,
        CreatedAt = now,
        Game = game,
        GameSource = gameSource,
        DurationSeconds = Math.Round(duration, 2),
        CutNote = cutNote,
        Transcript = transcript,
        Score = new ScoreInfo
        {
            Total = score.Total,
            Parts = score.Parts.Select(p => new ScorePartInfo { Name = p.Name, Points = p.Points, Max = p.Max, Note = p.Note }).ToList(),
        },
        Funniness = text.Funniness is { } f ? new FunninessInfo { Score = f, Reason = text.FunninessReason ?? "" } : null,
        TextSource = text.Source,
        TikTok = new TikTokInfo { Caption = text.TikTokCaption },
        YouTube = new YouTubeInfo
        {
            Title = text.YouTubeTitle,
            Description = text.YouTubeDescription,
            Tags = text.Hashtags.Select(h => h.TrimStart('#')).ToList(),
        },
        Instagram = new InstagramInfo { Caption = text.TikTokCaption },
    };

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static ClipMetadata Load(string path) =>
        JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException(path);
}

public sealed class ScoreInfo
{
    public int Total { get; set; }
    public List<ScorePartInfo> Parts { get; set; } = [];
}

public sealed class ScorePartInfo
{
    public string Name { get; set; } = "";
    public int Points { get; set; }
    public int Max { get; set; }
    public string Note { get; set; } = "";
}

public sealed class LayoutInfo
{
    /// <summary>passt, angepasst, automatisch, keine Cam erkannt, Querformat umgewandelt.</summary>
    public string Status { get; set; } = "";

    /// <summary>Lower edge of the facecam as a fraction of the height.</summary>
    public double? CamEdge { get; set; }

    public List<string> Notes { get; set; } = [];
}

public sealed class FunninessInfo
{
    /// <summary>0-10, rated by Claude.</summary>
    public int Score { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class TikTokInfo
{
    /// <summary>TikTok has one text block: title and hashtags together.</summary>
    public string Caption { get; set; } = "";
}

public sealed class YouTubeInfo
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}

public sealed class InstagramInfo
{
    public string Caption { get; set; } = "";
}

public sealed class OriginInfo
{
    public string Platform { get; set; } = "";
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Views { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class EditInfo
{
    public double CutStart { get; set; }
    public double CutEnd { get; set; }

    /// <summary>Split, Crop, Blur or empty (already vertical).</summary>
    public string Mode { get; set; } = "";
    public UCut.Editing.NormRect? Cam { get; set; }

    /// <summary>Words in clip time (seconds after the cut).</summary>
    public List<WordInfo> Words { get; set; } = [];
}

public sealed class WordInfo
{
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
}
