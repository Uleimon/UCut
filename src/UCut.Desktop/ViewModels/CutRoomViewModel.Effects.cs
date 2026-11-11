using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using UCut.CutRoom;

namespace UCut.Desktop.ViewModels;

/// <summary>One meme/effect in the list and on the effects row of the timeline. Changes go straight into the project.</summary>
internal sealed class EffectRow : ObservableObject
{
    public static IReadOnlyList<Choice<EffectAnimation>> AnimationChoices { get; } =
    [
        new(EffectAnimation.Pop, "Reinploppen"),
        new(EffectAnimation.SlideUp, "Von unten reinrutschen"),
        new(EffectAnimation.Fade, "Einblenden"),
        new(EffectAnimation.None, "Keine"),
    ];

    private readonly Action _before;
    private readonly Action _changed;
    private bool _selected;
    private double _left;
    private double _width;
    private double _top;

    public EffectRow(int index, EffectItem item, Action before, Action changed)
    {
        Index = index;
        Item = item;
        _before = before;
        _changed = changed;
    }

    public int Index { get; }
    public EffectItem Item { get; }
    public IReadOnlyList<Choice<EffectAnimation>> Animations => AnimationChoices;

    public string Label => Item.Label;
    public string TimeText => $"{CutRoomViewModel.Fmt(Item.Start)} · {Item.Duration:0.0} s";

    public string Color => Item.Kind switch
    {
        EffectKind.Meme => "#F59E0B",
        EffectKind.Sound => "#22C55E",
        EffectKind.Text => "#FACC15",
        EffectKind.Zoom => "#60A5FA",
        EffectKind.Shake => "#F472B6",
        _ => "#E5E7EB",
    };

    public bool ShowPosition => Item.Kind is EffectKind.Meme or EffectKind.Text or EffectKind.Zoom;
    public bool ShowText => Item.Kind == EffectKind.Text;
    public bool ShowAnimation => Item.Kind is EffectKind.Meme or EffectKind.Text;
    public bool ShowGreen => Item.Kind == EffectKind.Meme && !EffectItem.IsSound(Item.Path);
    public bool ShowVolume => Item.Kind == EffectKind.Sound || (Item.Kind == EffectKind.Meme && Item.HasAudio);

    public string SizeLabel => Item.Kind switch
    {
        EffectKind.Zoom => "Wie stark",
        EffectKind.Text => "Schriftgröße",
        _ => "Größe",
    };

    public double SizeMax => Item.Kind switch
    {
        EffectKind.Zoom => 1,
        EffectKind.Text => 0.25,
        _ => 1,
    };

    public double Start { get => Item.Start; set => Change(() => Item.Start = Math.Max(0, Math.Round(value, 2)), nameof(TimeText)); }
    public double Duration { get => Item.Duration; set => Change(() => Item.Duration = Math.Clamp(Math.Round(value, 2), 0.2, 60), nameof(TimeText)); }
    public double X { get => Item.X; set => Change(() => Item.X = value); }
    public double Y { get => Item.Y; set => Change(() => Item.Y = value); }
    public double Size { get => Item.Size; set => Change(() => Item.Size = value); }
    public double Volume { get => Item.Volume; set => Change(() => Item.Volume = Math.Round(value, 2)); }
    public string Text { get => Item.Text; set => Change(() => Item.Text = value, nameof(Label)); }
    public bool RemoveGreen { get => Item.RemoveGreen; set => Change(() => Item.RemoveGreen = value); }

    public Choice<EffectAnimation> Animation
    {
        get => AnimationChoices.First(a => a.Value == Item.Animation);
        set => Change(() => Item.Animation = value.Value);
    }

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public double Left { get => _left; set => Set(ref _left, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Top { get => _top; set => Set(ref _top, value); }

    private void Change(Action apply, string? also = null, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _before();
        apply();
        OnPropertyChanged(name);
        if (also is not null)
        {
            OnPropertyChanged(also);
        }

        _changed();
    }
}

/// <summary>A file from the meme folder, shown as a quick button.</summary>
internal sealed record MemeFile(string Path, string Name, string Glyph);

/// <summary>Memes, sounds and animations (like Eklipse): buttons, list, editor, auto-memes at laughs, quick preview.</summary>
internal sealed partial class CutRoomViewModel
{
    public const string MemeFilter = "Memes, GIFs, Sounds|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.webm;*.mov;*.mp3;*.wav;*.ogg;*.m4a|Alle Dateien|*.*";

    private EffectRow? _selectedEffect;
    private EffectRow? _undoArmedFor;
    private bool _effectBusy;
    private string _libraryFolder = MemeLibrary.DefaultFolder;
    private bool _starterTried;

    public ObservableCollection<EffectRow> EffectRows { get; } = [];
    public ObservableCollection<MemeFile> Library { get; } = [];

    public AsyncCommand AddMemeFileCommand { get; private set; } = null!;
    public AsyncCommand AddFromLibraryCommand { get; private set; } = null!;
    public RelayCommand AddTextCommand { get; private set; } = null!;
    public RelayCommand AddZoomCommand { get; private set; } = null!;
    public RelayCommand AddShakeCommand { get; private set; } = null!;
    public RelayCommand AddFlashCommand { get; private set; } = null!;
    public AsyncCommand AutoMemesCommand { get; private set; } = null!;
    public AsyncCommand OpenMemeFolderCommand { get; private set; } = null!;
    public RelayCommand ReloadLibraryCommand { get; private set; } = null!;
    public RelayCommand DeleteEffectCommand { get; private set; } = null!;
    public RelayCommand EffectHereCommand { get; private set; } = null!;
    public AsyncCommand PreviewCommand { get; private set; } = null!;

    public bool HasLibrary => Library.Count > 0;
    public bool HasEffects => EffectRows.Count > 0;

    public bool EffectBusy { get => _effectBusy; private set => Set(ref _effectBusy, value); }

    public EffectRow? SelectedEffect
    {
        get => _selectedEffect;
        set
        {
            var old = _selectedEffect;
            if (Set(ref _selectedEffect, value))
            {
                old?.Selected = false;
                value?.Selected = true;
                _undoArmedFor = value;
                OnPropertyChanged(nameof(HasSelectedEffect));
            }
        }
    }

    public bool HasSelectedEffect => _selectedEffect is not null;

    private void InitEffects()
    {
        AddMemeFileCommand = new AsyncCommand(async () =>
        {
            foreach (var f in _main.Dialogs.PickFiles("Meme, GIF oder Sound hinzufügen", MemeFilter, true))
            {
                await AddFileEffectAsync(f);
            }
        }, () => HasProject);
        AddFromLibraryCommand = new AsyncCommand(async p =>
        {
            if (p is string path)
            {
                await AddFileEffectAsync(path);
            }
        }, _ => HasProject);
        AddTextCommand = new RelayCommand(_ =>
        {
            var text = _main.Dialogs.Ask("Text einblenden", "Welcher Text soll groß aufploppen? (z. B. BRUH, XD, NEIN!, WAS?!)");
            if (!string.IsNullOrWhiteSpace(text))
            {
                AddEffect(new EffectItem { Kind = EffectKind.Text, Text = text.Trim(), Duration = 1.5, Size = 0.09, Y = 0.3 });
            }
        }, _ => HasProject);
        AddZoomCommand = new RelayCommand(_ => AddEffect(new EffectItem { Kind = EffectKind.Zoom, Duration = 1.2, Size = 0.3, Y = 0.4 }), _ => HasProject);
        AddShakeCommand = new RelayCommand(_ => AddEffect(new EffectItem { Kind = EffectKind.Shake, Duration = 0.8 }), _ => HasProject);
        AddFlashCommand = new RelayCommand(_ => AddEffect(new EffectItem { Kind = EffectKind.Flash, Duration = 0.4 }), _ => HasProject);
        AutoMemesCommand = new AsyncCommand(AutoMemesAsync, () => HasProject && !EffectBusy);
        OpenMemeFolderCommand = new AsyncCommand(async () =>
        {
            await EnsureStarterPackAsync();
            Shell.Open(_libraryFolder);
        });
        ReloadLibraryCommand = new RelayCommand(_ => ReloadLibrary());
        DeleteEffectCommand = new RelayCommand(_ =>
        {
            if (_selectedEffect is { } row)
            {
                Edit(p =>
                {
                    p.Effects.RemoveAt(row.Index);
                    return true;
                });
            }
        }, _ => HasSelectedEffect);
        EffectHereCommand = new RelayCommand(_ =>
        {
            if (_selectedEffect is { } row)
            {
                Edit(p =>
                {
                    p.Effects[row.Index].Start = Math.Round(Math.Min(Playhead, Math.Max(0, Duration - 0.2)), 2);
                    return true;
                });
            }
        }, _ => HasSelectedEffect);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => HasProject && !EffectBusy && !Exporting);
        Library.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLibrary));
        EffectRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEffects));
    }

    /// <summary>Reads the meme folder; the first time also writes the starter sounds.</summary>
    private async Task LoadLibraryAsync()
    {
        ReloadLibrary();
        if (Library.Count == 0)
        {
            await EnsureStarterPackAsync();
        }
    }

    private async Task EnsureStarterPackAsync()
    {
        if (_starterTried)
        {
            return;
        }

        _starterTried = true;
        try
        {
            var made = await MemeLibrary.CreateStarterPackAsync(_main.Engine.Settings.FfmpegPath, _libraryFolder);
            if (made > 0)
            {
                _main.Log.Write($"Meme-Ordner angelegt ({made} Start-Sounds): {_libraryFolder}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _main.Log.Write($"Meme-Ordner: {ex.Message}");
        }

        ReloadLibrary();
    }

    private void ReloadLibrary()
    {
        Library.Clear();
        foreach (var f in MemeLibrary.List(_libraryFolder))
        {
            var glyph = EffectItem.IsSound(f) ? "" : EffectItem.IsVideo(f) ? "" : "";
            Library.Add(new MemeFile(f, Path.GetFileNameWithoutExtension(f), glyph));
        }
    }

    private async Task AddFileEffectAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var sound = EffectItem.IsSound(path);
        var item = new EffectItem { Kind = sound ? EffectKind.Sound : EffectKind.Meme, Path = path, Duration = 2, Size = 0.45, Y = 0.3 };
        if (!EffectItem.IsImage(path))
        {
            try
            {
                var info = await _main.Engine.Ffmpeg.ProbeAsync(path);
                item.Duration = Math.Clamp(info.DurationSeconds, 0.3, sound ? 15 : 8);
                item.HasAudio = !sound && info.HasAudio;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or FormatException)
            {
                // Unknown length: keep 2 s.
            }
        }

        if (EffectItem.IsVideo(path))
        {
            item.Size = 0.7;
            item.Y = 0.5;
            item.Animation = EffectAnimation.Fade;
        }

        AddEffect(item);
    }

    /// <summary>Puts the effect at the playhead and selects it.</summary>
    private void AddEffect(EffectItem item)
    {
        if (_project is null)
        {
            return;
        }

        item.Start = Math.Round(Math.Max(0, Math.Min(Playhead, Duration - 0.2)), 2);
        item.Duration = Math.Max(0.2, Math.Min(item.Duration, Duration - item.Start));
        if (Edit(p =>
            {
                p.Effects.Add(item);
                return true;
            }))
        {
            SelectedEffect = EffectRows.LastOrDefault();
        }
    }

    private async Task AutoMemesAsync()
    {
        if (_project is null)
        {
            return;
        }

        EffectBusy = true;
        Status = "Suche lustige Stellen (Lacher) …";
        try
        {
            var project = _project.Clone();
            var ffmpeg = _main.Engine.Ffmpeg;
            var laughs = await Task.Run(() => AutoMemes.FindLaughsAsync(ffmpeg, project));
            if (laughs.Count == 0)
            {
                Status = "";
                _main.Dialogs.Info("Auto-Memes", "Ich habe in diesem Video keine Lacher gefunden.\n\nDu kannst Memes trotzdem selbst setzen: Abspielkopf an die Stelle, dann auf einen Knopf unter „Memes & Effekte“ klicken.");
                return;
            }

            var effects = AutoMemes.ForLaughs(laughs, Duration, Library.Select(m => m.Path).ToList(), Random.Shared);
            Edit(p =>
            {
                p.Effects.AddRange(effects);
                return true;
            });
            var count = effects.Count(e => e.Kind == EffectKind.Zoom);
            Status = $"{count} Auto-Meme(s) gesetzt";
            _main.Log.Write($"Auto-Memes: {laughs.Count} Lacher gefunden, {count} Stellen mit Zoom, Text/Meme und Sound versehen.");
        }
        catch (InvalidOperationException ex)
        {
            Status = "";
            _main.Dialogs.Info("Auto-Memes", ex.Message);
        }
        finally
        {
            EffectBusy = false;
        }
    }

    /// <summary>Renders 6 s from the playhead (small and fast) and shows them fullscreen.</summary>
    private async Task PreviewAsync()
    {
        if (_project is null)
        {
            return;
        }

        var size = Size.Value == ExportSize.Original ? ExportSize.Vertical720 : Size.Value;
        if (Size.Value == ExportSize.Original && _project.Width > _project.Height)
        {
            size = ExportSize.Original;
        }

        var options = new ExportOptions(ExportFormat.Mp4H264, size, ExportQuality.Small, TextFont: _main.Engine.Settings.Captions.Font);
        var part = _project.PreviewPart(Playhead, 6);
        var target = Path.Combine(Path.GetTempPath(), $"ucut-vorschau-{Guid.NewGuid():N}.mp4");
        var ffmpeg = _main.Engine.Settings.FfmpegPath;
        EffectBusy = true;
        Status = "Vorschau wird gerendert …";
        try
        {
            await Task.Run(() => EditRenderer.RenderAsync(ffmpeg, part, options, target));
            Status = "";
            Views.FullscreenPlayer.Show(Application.Current?.MainWindow, new Uri(target), TimeSpan.Zero);
        }
        catch (InvalidOperationException ex)
        {
            Status = "";
            _main.Dialogs.Info("Vorschau", ex.Message);
        }
        finally
        {
            EffectBusy = false;
            try
            {
                File.Delete(target);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>New rows after a change (undo/redo, add, delete); keeps the selection by position.</summary>
    private void BuildEffects()
    {
        var keep = _selectedEffect?.Index ?? -1;
        _selectedEffect = null;
        EffectRows.Clear();
        if (_project is not null)
        {
            for (var i = 0; i < _project.Effects.Count; i++)
            {
                var row = new EffectRow(i, _project.Effects[i], () => ArmUndo(), OnEffectEdited);
                EffectRows.Add(row);
            }
        }

        SelectedEffect = keep >= 0 && keep < EffectRows.Count ? EffectRows[keep] : null;
        OnPropertyChanged(nameof(SelectedEffect));
        LayoutEffects();
    }

    /// <summary>Slider changes have no undo step each; the first change after selecting an effect saves one.</summary>
    private void ArmUndo()
    {
        if (_project is not null && _undoArmedFor is not null && _undoArmedFor == _selectedEffect)
        {
            _undo.Push(_project.Clone());
            _redo.Clear();
            _undoArmedFor = null;
        }
    }

    private void OnEffectEdited()
    {
        LayoutEffects();
        SaveProject();
    }

    /// <summary>Position on the effects row; overlapping effects go into a second line.</summary>
    private void LayoutEffects()
    {
        var lineEnds = new double[2];
        foreach (var row in EffectRows.OrderBy(r => r.Item.Start))
        {
            var line = lineEnds[0] <= row.Item.Start + 0.001 ? 0 : lineEnds[1] <= row.Item.Start + 0.001 ? 1 : lineEnds[0] <= lineEnds[1] ? 0 : 1;
            lineEnds[line] = row.Item.End;
            row.Left = row.Item.Start * _pps;
            row.Width = Math.Max(6, row.Item.Duration * _pps - 2);
            row.Top = line * 21;
        }
    }

    /// <summary>Click on an effect on the timeline.</summary>
    public void SelectEffect(EffectRow row) => SelectedEffect = row;
}
