using System.Text.Json;

namespace UCut.Editing;

/// <summary>
/// Cam positions seen in earlier clips (found automatically or set by hand), per source size.
/// When the search finds nothing, these are checked first: Ulei's cam sits in the same few places most of the time.
/// Stored in OutputFolder\_UCut\cam-positionen.json.
/// </summary>
public sealed class CamMemory(string path)
{
    public sealed class Entry
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public int Count { get; set; }
        public DateTimeOffset LastSeen { get; set; }

        public NormRect Rect => new(X, Y, Width, Height);
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lock FileLock = new();

    public static CamMemory ForOutput(string outputFolder) => new(Path.Combine(outputFolder, "_UCut", "cam-positionen.json"));

    public List<Entry> Load()
    {
        lock (FileLock)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Options) ?? [] : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return [];
            }
        }
    }

    /// <summary>Known positions for this source size, most used first.</summary>
    public IReadOnlyList<NormRect> Candidates(int sourceWidth, int sourceHeight) =>
        Load().Where(e => SameAspect(e, sourceWidth, sourceHeight)).OrderByDescending(e => e.Count).ThenByDescending(e => e.LastSeen)
              .Select(e => e.Rect).Take(12).ToList();

    public void Remember(NormRect cam, int sourceWidth, int sourceHeight, int weight = 1)
    {
        lock (FileLock)
        {
            var list = Load();
            var hit = list.FirstOrDefault(e => SameAspect(e, sourceWidth, sourceHeight) && Close(e.Rect, cam));
            if (hit is null)
            {
                hit = new Entry { X = cam.X, Y = cam.Y, Width = cam.Width, Height = cam.Height, SourceWidth = sourceWidth, SourceHeight = sourceHeight };
                list.Add(hit);
            }
            else if (weight > 1)
            {
                // Set by hand: exactly this rectangle.
                (hit.X, hit.Y, hit.Width, hit.Height) = (cam.X, cam.Y, cam.Width, cam.Height);
            }

            hit.Count += weight;
            hit.LastSeen = DateTimeOffset.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(list.OrderByDescending(e => e.Count).Take(50).ToList(), Options));
        }
    }

    internal static bool Close(NormRect a, NormRect b) =>
        Math.Abs(a.X - b.X) < 0.02 && Math.Abs(a.Y - b.Y) < 0.02 && Math.Abs(a.Width - b.Width) < 0.03 && Math.Abs(a.Height - b.Height) < 0.03;

    private static bool SameAspect(Entry e, int w, int h) =>
        e.SourceWidth > 0 && e.SourceHeight > 0 && h > 0 && Math.Abs((double)e.SourceWidth / e.SourceHeight - (double)w / h) < 0.02;
}
