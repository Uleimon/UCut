using System.Globalization;
using System.Text;
using System.Text.Json;
using UCut.Settings;

namespace UCut.Twitch;

/// <summary>One processed Twitch clip in the archive file.</summary>
public sealed class ArchivedClip
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Views { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Game { get; set; } = "";
    public string Video { get; set; } = "";
    public int Score { get; set; }
    public int? Funniness { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>
/// Remembers which Twitch clips were already loaded (twitch-clips.json in the output folder), so a second run
/// only takes new ones, and writes the overview list sorted by funniness.
/// </summary>
public sealed class ClipArchive
{
    public const string FileName = "twitch-clips.json";
    public const string OverviewName = "Twitch-Clips Übersicht.csv";

    private readonly string _path;

    public List<ArchivedClip> Clips { get; private set; } = [];

    private ClipArchive(string path) => _path = path;

    public static ClipArchive Load(string outputFolder)
    {
        var archive = new ClipArchive(Path.Combine(outputFolder, FileName));
        if (File.Exists(archive._path))
        {
            try
            {
                archive.Clips = JsonSerializer.Deserialize<List<ArchivedClip>>(File.ReadAllText(archive._path), UCutSettings.JsonOptions) ?? [];
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FileName} ist beschädigt: {ex.Message}", ex);
            }
        }

        return archive;
    }

    public bool Contains(string id) => Clips.Exists(c => c.Id == id);

    public void Add(ArchivedClip clip)
    {
        Clips.RemoveAll(c => c.Id == clip.Id);
        Clips.Add(clip);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Clips, UCutSettings.JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Semicolon CSV (opens directly in German Excel), funniest first, then score.</summary>
    public string WriteOverview()
    {
        var path = Path.Combine(Path.GetDirectoryName(_path)!, OverviewName);
        var sb = new StringBuilder();
        sb.AppendLine("Lustigkeit;Score;Aufrufe;Datum;Spiel;Titel auf Twitch;Datei;Twitch-Link");
        foreach (var c in Clips.OrderByDescending(c => c.Funniness ?? -1).ThenByDescending(c => c.Score).ThenByDescending(c => c.Views))
        {
            sb.Append(c.Funniness?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
              .Append(c.Score.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(c.Views.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(c.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append(';')
              .Append(Csv(c.Game)).Append(';')
              .Append(Csv(c.Title)).Append(';')
              .Append(Csv(c.Video)).Append(';')
              .AppendLine(Csv(c.Url));
        }

        // BOM so Excel reads umlauts correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    internal static string Csv(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
