using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using UCut.CutRoom;

namespace UCut.Desktop.ViewModels;

/// <summary>A kept piece on the timeline. Viewbox = which part of the filmstrip/waveform image belongs to it.</summary>
internal sealed class SegmentItem(int index, double left, double width, Rect viewbox, string label, ImageSource? image) : ObservableObject
{
    private bool _selected;

    public int Index { get; } = index;
    public double Left { get; } = left;
    public double Width { get; } = width;
    public Rect Viewbox { get; } = viewbox;
    public string Label { get; } = label;

    /// <summary>Filmstrip or waveform; the Viewbox picks the part of it that belongs to this piece.</summary>
    public ImageSource? Image { get; } = image;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>One audio row of the timeline (source track or music).</summary>
internal sealed class TrackItem : ObservableObject
{
    private readonly Action _changed;
    private ImageSource? _waveform;

    public TrackItem(string name, bool isMusic, Func<double> getVolume, Action<double> setVolume, Func<bool> getMuted, Action<bool> setMuted, Action changed)
    {
        Name = name;
        IsMusic = isMusic;
        _get = getVolume;
        _set = setVolume;
        _getMuted = getMuted;
        _setMuted = setMuted;
        _changed = changed;
    }

    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly Func<bool> _getMuted;
    private readonly Action<bool> _setMuted;

    public string Name { get; }
    public bool IsMusic { get; }

    /// <summary>0..2 (0..200 %).</summary>
    public double Volume
    {
        get => _get();
        set
        {
            _set(Math.Round(value, 2));
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            _changed();
        }
    }

    public string VolumeText => $"{Volume * 100:0} %";

    public bool Muted
    {
        get => _getMuted();
        set
        {
            _setMuted(value);
            OnPropertyChanged();
            _changed();
        }
    }

    public ImageSource? Waveform { get => _waveform; set => Set(ref _waveform, value); }

    private IReadOnlyList<SegmentItem> _pieces = [];

    /// <summary>The kept pieces drawn in this row.</summary>
    public IReadOnlyList<SegmentItem> Pieces { get => _pieces; set => Set(ref _pieces, value); }

}

internal sealed record Tick(double X, string Label);

internal sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The cut room: one video, a timeline with video and audio rows, split/delete/trim like Premiere (S, Entf, Q, W),
/// volume per track, music, fades, export to MP4 (H.264) or MP3. Non-destructive: the source stays untouched.
/// </summary>
internal sealed partial class CutRoomViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Stack<EditProject> _undo = new();
    private readonly Stack<EditProject> _redo = new();
    private EditProject? _project;
    private double _pps = 60;
    private double _playhead;
    private int _selectedSegment = -1;
    private ImageSource? _filmstrip;
    private bool _exporting;
    private double _exportProgress;
    private string _status = "";
    private CancellationTokenSource? _exportCts;
    private string? _musicWaveFor;
    private Choice<ExportFormat> _format;
    private Choice<ExportSize> _size;
    private Choice<ExportQuality> _quality;

    public CutRoomViewModel(MainViewModel main)
    {
        _main = main;
        Formats = [new(ExportFormat.Mp4H264, "MP4 (H.264, Video + Ton)"), new(ExportFormat.Mp3, "MP3 (nur Ton)")];
        Sizes = [new(ExportSize.Original, "Original"), new(ExportSize.Vertical1080, "Hochkant 1080×1920 (TikTok, Shorts, Reels)"),
                 new(ExportSize.Vertical720, "Hochkant 720×1280 (klein)"), new(ExportSize.Landscape1080, "Querformat 1920×1080 (YouTube)")];
        Qualities = [new(ExportQuality.High, "Hoch (beste Qualität)"), new(ExportQuality.Medium, "Mittel"), new(ExportQuality.Small, "Klein (schnell, kleine Datei)")];
        _format = Formats[0];
        _size = Sizes[0];
        _quality = Qualities[0];
        InitEffects();

        OpenCommand = new AsyncCommand(async () =>
        {
            var f = _main.Dialogs.PickFiles("Video für den Schnittraum öffnen", WpfDialogs.VideoFilter, false);
            if (f.Count == 1)
            {
                await OpenAsync(f[0]);
            }
        });
        SplitCommand = new RelayCommand(_ => Edit(p => p.SplitAt(Playhead)), _ => HasProject);
        DeleteCommand = new RelayCommand(_ => Edit(p => p.RemoveSegment(SelectedSegmentOrPlayhead())), _ => HasProject);
        TrimStartCommand = new RelayCommand(_ => { if (Edit(p => p.TrimStartAt(Playhead))) { Playhead = 0; } }, _ => HasProject);
        TrimEndCommand = new RelayCommand(_ => Edit(p => p.TrimEndAt(Playhead)), _ => HasProject);
        MoveLeftCommand = new RelayCommand(_ => MoveSelected(-1), _ => HasProject);
        MoveRightCommand = new RelayCommand(_ => MoveSelected(1), _ => HasProject);
        UndoCommand = new RelayCommand(_ => Undo(), _ => _undo.Count > 0);
        RedoCommand = new RelayCommand(_ => Redo(), _ => _redo.Count > 0);
        ZoomInCommand = new RelayCommand(_ => PixelsPerSecond *= 1.4);
        ZoomOutCommand = new RelayCommand(_ => PixelsPerSecond /= 1.4);
        AddMusicCommand = new AsyncCommand(AddMusicAsync, () => HasProject);
        RemoveMusicCommand = new RelayCommand(_ => Edit(p => { p.Music = null; return true; }), _ => _project?.Music is not null);
        MusicHereCommand = new RelayCommand(_ => Edit(p => { if (p.Music is null) { return false; } p.Music.StartAt = Playhead; return true; }), _ => _project?.Music is not null);
        ExportCommand = new AsyncCommand(ExportAsync, () => HasProject && !Exporting);
        CancelExportCommand = new RelayCommand(_ => _exportCts?.Cancel(), _ => Exporting);
        ResetCommand = new RelayCommand(_ =>
        {
            if (_project is not null && _main.Dialogs.Confirm("Schnitt zurücksetzen", "Alle Schnitte, Lautstärken, Musik und Memes verwerfen?"))
            {
                Edit(p =>
                {
                    p.Segments = [new EditSegment { In = 0, Out = p.SourceDuration }];
                    p.Tracks.ForEach(t => { t.Volume = 1; t.Muted = false; });
                    p.Music = null;
                    p.Effects.Clear();
                    p.FadeIn = p.FadeOut = 0;
                    return true;
                });
            }
        }, _ => HasProject);
    }

    public IReadOnlyList<Choice<ExportFormat>> Formats { get; }
    public IReadOnlyList<Choice<ExportSize>> Sizes { get; }
    public IReadOnlyList<Choice<ExportQuality>> Qualities { get; }
    public Choice<ExportFormat> Format { get => _format; set { Set(ref _format, value); OnPropertyChanged(nameof(IsVideoExport)); } }
    public Choice<ExportSize> Size { get => _size; set => Set(ref _size, value); }
    public Choice<ExportQuality> Quality { get => _quality; set => Set(ref _quality, value); }
    public bool IsVideoExport => Format.Value == ExportFormat.Mp4H264;

    public AsyncCommand OpenCommand { get; }
    public RelayCommand SplitCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand TrimStartCommand { get; }
    public RelayCommand TrimEndCommand { get; }
    public RelayCommand MoveLeftCommand { get; }
    public RelayCommand MoveRightCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public AsyncCommand AddMusicCommand { get; }
    public RelayCommand RemoveMusicCommand { get; }
    public RelayCommand MusicHereCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public RelayCommand CancelExportCommand { get; }
    public RelayCommand ResetCommand { get; }

    public ObservableCollection<SegmentItem> Segments { get; } = [];
    public ObservableCollection<TrackItem> Tracks { get; } = [];
    public ObservableCollection<TrackItem> MusicTracks { get; } = [];
    public ObservableCollection<Tick> Ticks { get; } = [];

    public EditProject? Project => _project;
    public bool HasProject => _project is not null;
    public string SourceName => _project is null ? "" : Path.GetFileName(_project.Source);
    public Uri? SourceUri => _project is null ? null : new Uri(_project.Source);
    public Uri? MusicUri => _project?.Music is { } m && File.Exists(m.Path) ? new Uri(m.Path) : null;
    public double MusicVolume => _project?.Music is { Muted: false } m ? Math.Min(1, m.Volume) : 0;
    public double MusicStart => _project?.Music?.StartAt ?? 0;
    public ImageSource? Filmstrip { get => _filmstrip; private set { if (Set(ref _filmstrip, value)) { Rebuild(); } } }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public double PixelsPerSecond
    {
        get => _pps;
        set
        {
            if (Set(ref _pps, Math.Clamp(value, 8, 600)))
            {
                Rebuild();
            }
        }
    }

    public double Duration => _project?.Duration ?? 0;
    public double TimelineWidth => Duration * _pps + 200;

    /// <summary>Playhead on the (edited) timeline, seconds.</summary>
    public double Playhead
    {
        get => _playhead;
        set
        {
            if (Set(ref _playhead, Math.Clamp(value, 0, Duration)))
            {
                OnPropertyChanged(nameof(PlayheadX));
                OnPropertyChanged(nameof(PlayheadText));
            }
        }
    }

    public double PlayheadX => _playhead * _pps;
    public string PlayheadText => $"{Fmt(_playhead)} / {Fmt(Duration)}";

    public bool FadeIn
    {
        get => _project?.FadeIn > 0;
        set => Edit(p => { p.FadeIn = value ? 0.5 : 0; return true; });
    }

    public bool FadeOut
    {
        get => _project?.FadeOut > 0;
        set => Edit(p => { p.FadeOut = value ? 0.8 : 0; return true; });
    }

    public bool Exporting { get => _exporting; private set => Set(ref _exporting, value); }
    public double ExportProgress { get => _exportProgress; private set => Set(ref _exportProgress, value); }

    /// <summary>Asks the view to move the player (source seconds).</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Music player: seek (music seconds) and whether it should play.</summary>
    public event Action? MusicChanged;

    public async Task OpenAsync(string path)
    {
        try
        {
            var info = await _main.Engine.Ffmpeg.ProbeAsync(path);
            var project = EditProject.FromSource(path, info);
            var saved = ProjectFile(path);
            if (File.Exists(saved))
            {
                try
                {
                    var old = EditProject.Load(saved);
                    if (Math.Abs(old.SourceDuration - project.SourceDuration) < 0.05 && old.Tracks.Count == project.Tracks.Count)
                    {
                        project = old;
                    }
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
                {
                }
            }

            _project = project;
            _undo.Clear();
            _redo.Clear();
            _selectedSegment = -1;
            Playhead = 0;
            Filmstrip = null;
            Status = $"{info.Width}×{info.Height}, {Fmt(info.DurationSeconds)}, {project.Tracks.Count} Tonspur(en)";
            OnPropertyChanged(nameof(SourceUri));
            OnPropertyChanged(nameof(SourceName));
            OnPropertyChanged(nameof(HasProject));
            OnPropertyChanged(nameof(MusicUri));
            BuildTracks();
            Rebuild();
            BuildEffects();
            _main.Page = "cutroom";
            await LoadLibraryAsync();
            await LoadImagesAsync(project);
        }
        catch (InvalidOperationException ex)
        {
            _main.Dialogs.Info("Schnittraum", $"Das Video lässt sich nicht öffnen:\n{ex.Message}");
        }
    }

    private async Task LoadImagesAsync(EditProject project)
    {
        var cache = CacheFolder(project.Source);
        var ffmpeg = _main.Engine.Settings.FfmpegPath;
        var strip = await TimelineImages.FilmstripAsync(ffmpeg, project.Source, project.SourceDuration, Path.Combine(cache, "film.jpg"), 40);
        if (_project != project)
        {
            return;
        }

        Filmstrip = Shell.LoadImage(strip);
        for (var k = 0; k < project.Tracks.Count && k < Tracks.Count; k++)
        {
            var wave = await TimelineImages.WaveformAsync(ffmpeg, project.Source, k, Path.Combine(cache, $"wave{k}.png"));
            if (_project != project)
            {
                return;
            }

            Tracks[k].Waveform = Shell.LoadImage(wave);
            Rebuild();
        }

        await LoadMusicWaveAsync();
    }

    private async Task LoadMusicWaveAsync()
    {
        if (_project?.Music is { } m && MusicTracks.Count == 1 && File.Exists(m.Path))
        {
            var wave = await TimelineImages.WaveformAsync(_main.Engine.Settings.FfmpegPath, m.Path, 0,
                Path.Combine(CacheFolder(m.Path), "music.png"), "#c4b5fd");
            if (MusicTracks.Count == 1)
            {
                MusicTracks[0].Waveform = Shell.LoadImage(wave);
                _musicWaveFor = m.Path;
                Rebuild();
            }
        }
    }

    private static string CacheFolder(string file)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file + File.GetLastWriteTimeUtc(file).Ticks)))[..16];
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCut", "cache", key);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ProjectFile(string source) => Path.Combine(CacheFolder(source), "projekt.json");

    /// <summary>Runs one change with undo. Returns whether something changed.</summary>
    private bool Edit(Func<EditProject, bool> change)
    {
        if (_project is null)
        {
            return false;
        }

        var before = _project.Clone();
        if (!change(_project))
        {
            return false;
        }

        _undo.Push(before);
        _redo.Clear();
        AfterChange();
        return true;
    }

    private void Undo()
    {
        if (_project is null || _undo.Count == 0)
        {
            return;
        }

        _redo.Push(_project);
        _project = _undo.Pop();
        BuildTracks();
        AfterChange();
    }

    private void Redo()
    {
        if (_project is null || _redo.Count == 0)
        {
            return;
        }

        _undo.Push(_project);
        _project = _redo.Pop();
        BuildTracks();
        AfterChange();
    }

    private void AfterChange()
    {
        if (_selectedSegment >= (_project?.Segments.Count ?? 0))
        {
            _selectedSegment = -1;
        }

        Playhead = Math.Min(Playhead, Duration);
        Rebuild();
        BuildEffects();
        OnPropertyChanged(nameof(FadeIn));
        OnPropertyChanged(nameof(FadeOut));
        OnPropertyChanged(nameof(MusicUri));
        OnPropertyChanged(nameof(MusicVolume));
        MusicChanged?.Invoke();
        SaveProject();
    }

    private void SaveProject()
    {
        if (_project is null)
        {
            return;
        }

        try
        {
            _project.Save(ProjectFile(_project.Source));
        }
        catch (IOException)
        {
        }
    }

    private void BuildTracks()
    {
        // Keep the already rendered waveforms (undo/redo rebuilds the rows).
        var waves = Tracks.Select(t => t.Waveform).ToList();
        var musicWave = MusicTracks.FirstOrDefault()?.Waveform;
        Tracks.Clear();
        MusicTracks.Clear();
        if (_project is null)
        {
            return;
        }

        var p = _project;
        for (var k = 0; k < p.Tracks.Count; k++)
        {
            var t = p.Tracks[k];
            Tracks.Add(new TrackItem(t.Name, false, () => t.Volume, v => t.Volume = v, () => t.Muted, m => t.Muted = m, SaveProject)
            {
                Waveform = k < waves.Count ? waves[k] : null,
            });
        }

        if (p.Music is { } music)
        {
            MusicTracks.Add(new TrackItem("♪ " + music.Name, true, () => music.Volume, v => music.Volume = v, () => music.Muted, m => music.Muted = m, () =>
            {
                SaveProject();
                OnPropertyChanged(nameof(MusicVolume));
                MusicChanged?.Invoke();
            })
            {
                Waveform = string.Equals(_musicWaveFor, music.Path, StringComparison.OrdinalIgnoreCase) ? musicWave : null,
            });
            if (MusicTracks[0].Waveform is null)
            {
                _ = LoadMusicWaveAsync();
            }
        }
    }

    /// <summary>Recomputes everything that depends on segments or zoom.</summary>
    private void Rebuild()
    {
        Segments.Clear();
        Ticks.Clear();
        if (_project is null)
        {
            OnPropertyChanged(nameof(TimelineWidth));
            return;
        }

        var p = _project;
        var x = 0.0;
        for (var i = 0; i < p.Segments.Count; i++)
        {
            var s = p.Segments[i];
            var w = s.Duration * _pps;
            var vb = new Rect(s.In / p.SourceDuration, 0, s.Duration / p.SourceDuration, 1);
            Segments.Add(new SegmentItem(i, x, Math.Max(2, w - 2), vb, $"{Fmt(s.In)}–{Fmt(s.Out)}", _filmstrip) { Selected = i == _selectedSegment });
            x += w;
        }

        foreach (var track in Tracks)
        {
            track.Pieces = Segments.Select(seg => new SegmentItem(seg.Index, seg.Left, seg.Width, seg.Viewbox, "", track.Waveform) { Selected = seg.Selected }).ToList();
        }

        var step = _pps >= 120 ? 1 : _pps >= 40 ? 2 : _pps >= 15 ? 5 : 10;
        for (var t = 0.0; t <= p.Duration + 0.001; t += step)
        {
            Ticks.Add(new Tick(t * _pps, Fmt(t)));
        }

        if (p.Music is not null && MusicTracks.Count == 1)
        {
            var m = MusicTracks[0];
            var left = p.Music.StartAt * _pps;
            m.Pieces = [new SegmentItem(0, left, Math.Max(4, (p.Duration - p.Music.StartAt) * _pps), new Rect(0, 0, 1, 1), m.Name, m.Waveform)];
        }

        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(TimelineWidth));
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(PlayheadText));
        OnPropertyChanged(nameof(MusicStart));
        LayoutEffects();
    }

    /// <summary>Click on the timeline: move the playhead there and select the piece under it.</summary>
    public void ClickTimeline(double x, bool select)
    {
        if (_project is null)
        {
            return;
        }

        Playhead = x / _pps;
        if (select)
        {
            _selectedSegment = _project.Locate(Playhead).Segment;
            foreach (var s in Segments.Concat(Tracks.SelectMany(t => t.Pieces)))
            {
                s.Selected = s.Index == _selectedSegment;
            }
        }

        SeekRequested?.Invoke(_project.Locate(Playhead).SourceTime);
    }

    /// <summary>
    /// Called by the player while it plays (source seconds). Returns where the player must jump
    /// (the next kept piece), -1 to stop at the end, or null to keep playing.
    /// </summary>
    public double? OnPlayback(double sourceTime)
    {
        if (_project is null)
        {
            return null;
        }

        // Follow the piece the playhead is in (pieces can be reordered, so the source time alone is not enough).
        var cur = _project.Locate(Playhead).Segment;
        if (cur < 0)
        {
            return -1;
        }

        var seg = _project.Segments[cur];
        if (sourceTime >= seg.In - 0.08 && sourceTime < seg.Out)
        {
            Playhead = _project.SegmentStart(cur) + Math.Max(0, sourceTime - seg.In);
            return null;
        }

        if (sourceTime >= seg.Out && cur + 1 < _project.Segments.Count)
        {
            Playhead = _project.SegmentStart(cur + 1) + 0.001;
            return _project.Segments[cur + 1].In;
        }

        if (sourceTime >= seg.Out)
        {
            Playhead = Duration;
            return -1;
        }

        // Player is somewhere else (e.g. still before the piece after a seek): bring it to the playhead.
        return _project.Locate(Playhead).SourceTime;
    }

    public double PlayheadSourceTime => _project?.Locate(Playhead).SourceTime ?? 0;

    private int SelectedSegmentOrPlayhead() => _selectedSegment >= 0 ? _selectedSegment : _project?.Locate(Playhead).Segment ?? -1;

    private void MoveSelected(int delta)
    {
        var i = SelectedSegmentOrPlayhead();
        if (Edit(p => p.MoveSegment(i, delta)))
        {
            _selectedSegment = i + delta;
            Rebuild();
        }
    }

    private async Task AddMusicAsync()
    {
        var f = _main.Dialogs.PickFiles("Musik hinzufügen", WpfDialogs.AudioFilter, false);
        if (f.Count == 1)
        {
            await AddMusicAsync(f[0]);
        }
    }

    public async Task AddMusicAsync(string path)
    {
        if (!Edit(p =>
            {
                p.Music = new MusicTrack { Path = path, StartAt = 0 };
                return true;
            }))
        {
            return;
        }

        BuildTracks();
        Rebuild();
        await LoadMusicWaveAsync();
    }

    private async Task ExportAsync()
    {
        if (_project is null)
        {
            return;
        }

        var options = new ExportOptions(Format.Value, Size.Value, Quality.Value, TextFont: _main.Engine.Settings.Captions.Font);
        var name = Path.GetFileNameWithoutExtension(_project.Source) + "_schnitt" + options.Extension;
        var filter = options.Format == ExportFormat.Mp3 ? "MP3|*.mp3" : "MP4-Video|*.mp4";
        var target = _main.Dialogs.SaveFile("Exportieren als", name, filter);
        if (target is null)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(_project.Source), StringComparison.OrdinalIgnoreCase))
        {
            _main.Dialogs.Info("Export", "Bitte unter einem anderen Namen speichern, das Original wird nicht überschrieben.");
            return;
        }

        var project = _project.Clone();
        var ffmpeg = _main.Engine.Settings.FfmpegPath;
        _exportCts = new CancellationTokenSource();
        Exporting = true;
        ExportProgress = 0;
        Shell.KeepAwake(true);
        _main.Log.Write($"Export gestartet: {target}");
        try
        {
            var progress = new Progress<double>(v => ExportProgress = v);
            var ct = _exportCts.Token;
            await Task.Run(() => EditRenderer.RenderAsync(ffmpeg, project, options, target, progress, ct), CancellationToken.None);
            _main.Log.Write($"Export fertig: {target}");
            if (_main.Dialogs.Confirm("Export fertig", $"Gespeichert:\n{target}\n\nIm Ordner anzeigen?"))
            {
                Shell.ShowInExplorer(target);
            }
        }
        catch (OperationCanceledException)
        {
            _main.Log.Write("Export abgebrochen.");
        }
        catch (InvalidOperationException ex)
        {
            _main.Log.Write($"Export fehlgeschlagen: {ex.Message}");
            _main.Dialogs.Info("Export fehlgeschlagen", ex.Message);
        }
        finally
        {
            Shell.KeepAwake(false);
            Exporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    internal static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }
}
