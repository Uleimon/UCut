using UCut.Analysis;
using UCut.Media;
using UCut.Transcription;

namespace UCut.CutRoom;

/// <summary>
/// The meme folder (%APPDATA%\UCut\Memes): the user's own pictures, GIFs, meme videos and sounds.
/// Everything in it shows up as a quick button in the cut room.
/// </summary>
public static class MemeLibrary
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCut", "Memes");

    public static bool IsMemeFile(string path) =>
        EffectItem.IsImage(path) || EffectItem.IsGif(path) || EffectItem.IsVideo(path) || EffectItem.IsSound(path);

    /// <summary>All usable files in the folder and its subfolders, sorted by name.</summary>
    public static List<string> List(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                            .Where(IsMemeFile)
                            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The starter sounds: made by ffmpeg itself, so they are free to use.</summary>
    internal static readonly (string Name, string Source)[] Starter =
    [
        ("boom.mp3", "aevalsrc='0.9*sin(2*PI*(45+90*exp(-7*t))*t)*exp(-2.5*t)':d=1.4"),
        ("ding.mp3", "aevalsrc='0.5*(sin(2*PI*1320*t)+0.5*sin(2*PI*2640*t))*exp(-4*t)':d=1.2"),
        ("boing.mp3", "aevalsrc='0.6*sin(2*PI*(180+90*sin(2*PI*11*t))*t)*exp(-3*t)':d=0.9"),
        ("whoosh.mp3", "anoisesrc=d=0.7:c=pink:a=0.6,bandpass=f=1400:w=1200,afade=t=in:d=0.3,afade=t=out:st=0.35:d=0.35"),
        ("fail.mp3", "aevalsrc='0.4*sin(2*PI*(392-150*t)*t)*between(t,0,0.35)+0.4*sin(2*PI*(330-170*(t-0.4))*t)*between(t,0.4,1.4)*exp(-1.2*(t-0.4))':d=1.5"),
    ];

    /// <summary>Creates the folder and writes the starter sounds that are missing. Returns how many were written.</summary>
    public static async Task<int> CreateStarterPackAsync(string ffmpegPath, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var written = 0;
        foreach (var (name, source) in Starter)
        {
            var target = Path.Combine(folder, name);
            if (File.Exists(target))
            {
                continue;
            }

            await ProcessRunner.RunCheckedAsync(ffmpegPath,
                ["-v", "error", "-y", "-f", "lavfi", "-i", source, "-ac", "2", "-ar", "48000", "-c:a", "libmp3lame", "-b:a", "160k", target], ct: ct)
                .ConfigureAwait(false);
            written++;
        }

        var readme = Path.Combine(folder, "LIES MICH.txt");
        if (!File.Exists(readme))
        {
            await File.WriteAllTextAsync(readme,
                """
                Das ist dein Meme-Ordner für UCut.

                Alles, was du hier reinlegst, erscheint im Schnittraum unter "Memes & Effekte" als Knopf:
                - Bilder und Sticker: PNG, JPG, WEBP (PNG mit durchsichtigem Hintergrund sieht am besten aus)
                - GIFs: GIF
                - Meme-Videos: MP4, WEBM, MOV (Green-Screen-Memes: im Schnittraum "Grün entfernen" anhaken)
                - Sounds: MP3, WAV, OGG

                Unterordner gehen auch. Nach dem Reinlegen im Schnittraum auf "Neu laden" drücken.
                Die mitgelieferten Sounds (boom, ding, boing, whoosh, fail) hat UCut selbst gemacht, die darfst du überall benutzen.
                Bei fremden Memes und Sounds: nur nehmen, was du benutzen darfst.
                """, ct).ConfigureAwait(false);
        }

        return written;
    }
}

/// <summary>Puts memes on the funny moments automatically (like Eklipse): zoom, a big word and a sound at each laugh.</summary>
public static class AutoMemes
{
    public static readonly string[] Words = ["BRUH", "XD", "LOL", "NEIN!", "WAS?!", "OMG"];

    /// <summary>Laughs in the kept pieces, as timeline seconds.</summary>
    public static async Task<List<double>> FindLaughsAsync(Ffmpeg ffmpeg, EditProject project, CancellationToken ct = default)
    {
        const int rate = 16000;
        var found = new List<double>();
        for (var i = 0; i < project.Segments.Count; i++)
        {
            var seg = project.Segments[i];
            if (seg.Duration < 1.5)
            {
                continue;
            }

            var pcm = await ffmpeg.ReadPcmAsync(project.Source, seg.In, seg.Duration, rate, ct).ConfigureAwait(false);
            var laughs = LaughDetector.Detect(pcm, rate, new Transcript([]));
            var offset = project.SegmentStart(i);
            found.AddRange(laughs.Moments.Select(t => offset + t));
        }

        return found;
    }

    /// <summary>
    /// Effects for up to <paramref name="max"/> laughs at least 3 s apart: a reaction zoom, a word or a picture from the
    /// meme folder, and a sound from the meme folder when there is one.
    /// </summary>
    public static List<EffectItem> ForLaughs(IReadOnlyList<double> laughs, double duration, IReadOnlyList<string> library, Random random, int max = 3)
    {
        var picked = new List<double>();
        foreach (var t in laughs.Order())
        {
            if (t < duration - 0.5 && (picked.Count == 0 || t - picked[^1] >= 3))
            {
                picked.Add(t);
            }
        }

        var pictures = library.Where(f => EffectItem.IsImage(f) || EffectItem.IsGif(f)).ToList();
        var sounds = library.Where(EffectItem.IsSound).ToList();
        var effects = new List<EffectItem>();
        var n = 0;
        foreach (var t in picked.Take(max))
        {
            var start = Math.Max(0, t - 0.2);
            var len = Math.Min(1.6, duration - start);
            effects.Add(new EffectItem { Kind = EffectKind.Zoom, Start = start, Duration = len, Size = 0.25, Y = 0.4 });
            if (pictures.Count > 0 && n % 2 == 1)
            {
                effects.Add(new EffectItem
                {
                    Kind = EffectKind.Meme, Path = pictures[random.Next(pictures.Count)], Start = start, Duration = len,
                    Size = 0.45, X = 0.5, Y = 0.3, Animation = EffectAnimation.Pop,
                });
            }
            else
            {
                effects.Add(new EffectItem
                {
                    Kind = EffectKind.Text, Text = Words[random.Next(Words.Length)], Start = start, Duration = len,
                    Size = 0.09, X = 0.5, Y = 0.3, Animation = EffectAnimation.Pop,
                });
            }

            if (sounds.Count > 0)
            {
                effects.Add(new EffectItem { Kind = EffectKind.Sound, Path = sounds[random.Next(sounds.Count)], Start = start, Duration = len, Volume = 0.8 });
            }

            n++;
        }

        return effects;
    }
}
