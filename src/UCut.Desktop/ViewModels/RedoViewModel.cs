using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using UCut.Editing;
using UCut.Library;
using UCut.Media;
using UCut.Pipeline;
using UCut.Transcription;

namespace UCut.Desktop.ViewModels;

/// <summary>One word of the captions, editable. Empty text removes the word.</summary>
internal sealed class WordItem(double start, double end, string text) : ObservableObject
{
    private string _text = text;

    public double Start { get; } = start;
    public double End { get; } = end;
    public string TimeText => $"{Start:0.0} s";
    public string Text { get => _text; set => Set(ref _text, value); }
}

/// <summary>
/// "Create": layout (cam on top with a rectangle drawn by hand, no cam, blurred edges) and corrected caption words.
/// The clip is rendered again from its original and replaces the old file; texts and uploads stay.
/// </summary>
internal sealed class RedoViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly LibraryClip _clip;
    private string _mode;
    private NormRect? _cam;
    private ImageSource? _frame;
    private string _status = "";

    public RedoViewModel(MainViewModel main, LibraryClip clip)
    {
        _main = main;
        _clip = clip;
        Original = main.Engine.FindOriginal(clip);
        var edit = clip.Metadata.Edit;
        _mode = edit?.Mode is { Length: > 0 } m ? m : "auto";
        _cam = edit?.Cam;
        if (edit is not null)
        {
            foreach (var w in edit.Words)
            {
                Words.Add(new WordItem(w.Start, w.End, w.Text));
            }
        }

        HasWords = Words.Count > 0;
        Title = clip.Title;
    }

    public string Title { get; }
    public string? Original { get; }
    public bool HasOriginal => Original is not null;
    public ObservableCollection<WordItem> Words { get; } = [];
    public bool HasWords { get; }
    public bool IsLandscape { get; private set; }
    public double FrameWidth => 640;
    public double FrameHeight { get; private set; } = 360;
    public ImageSource? Frame { get => _frame; private set => Set(ref _frame, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>auto, Split, Crop, Blur.</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                OnPropertyChanged(nameof(CamHint));
            }
        }
    }

    /// <summary>Cam rectangle as fractions of the picture (drawn with the mouse).</summary>
    public NormRect? Cam
    {
        get => _cam;
        set
        {
            if (Set(ref _cam, value))
            {
                OnPropertyChanged(nameof(CamHint));
                if (value is not null)
                {
                    Mode = "Split";
                }
            }
        }
    }

    public string CamHint => Mode switch
    {
        "Split" when Cam is null => "Zieh mit der Maus ein Rechteck um deine Cam.",
        "Split" => "Cam gesetzt. Neu aufziehen, um sie zu ändern. UCut merkt sich die Stelle für künftige Clips.",
        "Crop" => "Ohne Cam: das Spiel füllt den ganzen Bildschirm, das Logo kommt nach oben.",
        "Blur" => "Ganzes Bild in der Mitte, oben und unten unscharfer Rand.",
        _ => "Automatisch: UCut sucht die Cam selbst.",
    };

    public string WordsHint => HasWords
        ? "Falsche Wörter einfach überschreiben. Ein leeres Feld löscht das Wort."
        : "Für diesen Clip gibt es noch keine Wortzeiten (er ist mit einer älteren Version gemacht). Mach ihn einmal neu, danach kannst du hier die Wörter bearbeiten.";

    /// <summary>Loads a still picture of the original (middle of the cut) for drawing the cam.</summary>
    public async Task LoadFrameAsync()
    {
        if (Original is null)
        {
            Status = "Das Original ist nicht mehr da, der Clip kann nicht neu gemacht werden.";
            return;
        }

        try
        {
            var engine = _main.Engine;
            var info = await engine.Ffmpeg.ProbeAsync(Original);
            IsLandscape = Reframer.NeedsReframe(info.Width, info.Height, engine.Settings.Reframe);
            FrameHeight = info.Width > 0 ? Math.Round(FrameWidth * info.Height / info.Width) : 360;
            OnPropertyChanged(nameof(IsLandscape));
            OnPropertyChanged(nameof(FrameHeight));

            var edit = _clip.Metadata.Edit;
            var at = edit is { CutEnd: > 0 } ? (edit.CutStart + edit.CutEnd) / 2 : info.DurationSeconds / 2;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCut", "cache");
            Directory.CreateDirectory(dir);
            var jpg = Path.Combine(dir, "redo-frame.jpg");
            await ProcessRunner.RunCheckedAsync(engine.Settings.FfmpegPath,
            [
                "-v", "error", "-y", "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", Original,
                "-frames:v", "1", "-vf", "scale=1280:-2", jpg,
            ]);
            Frame = Shell.LoadImage(jpg, 1280);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Status = "Standbild ging nicht: " + ex.Message;
        }
    }

    public ProcessOptions BuildOptions() => new()
    {
        Mode = Mode switch
        {
            "Split" => ReframeMode.Split,
            "Crop" => ReframeMode.Crop,
            "Blur" => ReframeMode.Blur,
            _ => null,
        },
        Cam = Mode == "Split" ? Cam : null,
        Words = HasWords ? Words.Select(w => new Word(w.Text.Trim(), w.Start, w.End)).Where(w => w.Text.Length > 0).ToList() : null,
    };

    public bool CanRun(out string why)
    {
        why = Original is null ? "Das Original ist nicht mehr da."
            : Mode == "Split" && Cam is null && IsLandscape ? "Bitte zuerst die Cam mit der Maus aufziehen (oder einen anderen Aufbau wählen)."
            : "";
        return why.Length == 0;
    }
}
