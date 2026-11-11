using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using UCut.Library;

namespace UCut.Desktop.ViewModels;

/// <summary>One clip card in the gallery, and the editable texts of the details panel.</summary>
internal sealed class ClipCard(LibraryClip clip) : ObservableObject
{
    private ImageSource? _thumbnail;

    public LibraryClip Clip { get; } = clip;
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public string Title => Clip.Title;
    public string Game => Clip.Metadata.Game;
    public string DateText => Clip.Date.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
    public string DurationText => TimeSpan.FromSeconds(Clip.Metadata.DurationSeconds).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    public int Score => Clip.Metadata.Score.Total;
    public int? Funny => Clip.Metadata.Funniness?.Score;
    public string FunnyText => Funny is { } f ? $"😂 {f}/10" : "";
    public string FunnyReason => Clip.Metadata.Funniness?.Reason ?? "";
    public string LaughText => Clip.Metadata.Laughs.Count switch
    {
        0 => "",
        1 => $"1 Lacher erkannt (bei {Clip.Metadata.Laughs[0]:0.0} s)",
        var n => $"{n} Lacher erkannt (bei {string.Join(", ", Clip.Metadata.Laughs.Select(l => l.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))))} s)",
    };
    public bool HasYouTubeThumbnail => File.Exists(Clip.YouTubeThumbnailPath);
    public string SourceText => Clip.Metadata.Origin is { } o ? $"{o.Platform}-Clip · {o.Views} Aufrufe" : "Aufnahme";
    public string Layout => Clip.Metadata.Layout?.Status ?? "";
    public string Transcript => Clip.Metadata.Transcript;
    public Uri VideoUri => new(Clip.VideoPath);

    public string TikTokCaption { get => Clip.Metadata.TikTok.Caption; set { Clip.Metadata.TikTok.Caption = value; Dirty = true; } }
    public string YouTubeTitle { get => Clip.Metadata.YouTube.Title; set { Clip.Metadata.YouTube.Title = value; Dirty = true; OnPropertyChanged(nameof(Title)); } }
    public string YouTubeDescription { get => Clip.Metadata.YouTube.Description; set { Clip.Metadata.YouTube.Description = value; Dirty = true; } }
    public string YouTubeTags
    {
        get => string.Join(" ", Clip.Metadata.YouTube.Tags.Select(t => "#" + t.TrimStart('#')));
        set
        {
            Clip.Metadata.YouTube.Tags = value.Split([' ', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.TrimStart('#')).ToList();
            Dirty = true;
        }
    }

    public string InstagramCaption { get => Clip.Metadata.Instagram.Caption; set { Clip.Metadata.Instagram.Caption = value; Dirty = true; } }

    public bool Dirty { get; set; }

    public string UploadedText => Clip.Metadata.Uploaded.Count == 0
        ? "Noch nirgends hochgeladen."
        : string.Join("\n", Clip.Metadata.Uploaded.Select(u => $"{u.Key}: {u.Value}"));

    public void RefreshUploads() => OnPropertyChanged(nameof(UploadedText));
}

/// <summary>A connected account in the upload box of the details panel.</summary>
internal sealed class UploadTarget(UCut.Upload.UploadAccount account, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    public UCut.Upload.UploadAccount Account { get; } = account;
    public string Label => Account.Label;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>Gallery of finished clips: filter by game, sort, search, details with player and texts.</summary>
internal sealed class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private List<ClipCard> _all = [];
    private string _game = AllGames;
    private string _sort = "new";
    private string _search = "";
    private ClipCard? _selected;
    private int _loadGeneration;

    public const string AllGames = "Alle";

    public LibraryViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new RelayCommand(_ => Refresh());
        AddClipsCommand = new RelayCommand(_ =>
        {
            var files = _main.Dialogs.PickFiles("Clips hinzufügen", WpfDialogs.VideoFilter, true);
            if (files.Count > 0)
            {
                _main.AddFiles(files);
            }
        });
        PlayCommand = new RelayCommand(_ => { if (Selected is { } c) { Shell.Open(c.Clip.VideoPath); } }, _ => Selected is not null);
        ShowInFolderCommand = new RelayCommand(_ => { if (Selected is { } c) { Shell.ShowInExplorer(c.Clip.VideoPath); } }, _ => Selected is not null);
        OpenYouTubeThumbnailCommand = new RelayCommand(_ => { if (Selected is { HasYouTubeThumbnail: true } c) { Shell.Open(c.Clip.YouTubeThumbnailPath); } }, _ => Selected is { HasYouTubeThumbnail: true });
        OpenOutputCommand = new RelayCommand(_ => Shell.Open(_main.Engine.Settings.OutputFolder));
        EditCommand = new RelayCommand(_ =>
        {
            if (Selected is { } c)
            {
                _ = _main.CutRoom.OpenAsync(c.Clip.VideoPath);
                _main.Page = "cutroom";
            }
        }, _ => Selected is not null);
        SaveTextsCommand = new RelayCommand(_ => SaveSelected(), _ => Selected is not null);
        CopyCommand = new RelayCommand(p =>
        {
            if (Selected is not { } c)
            {
                return;
            }

            var text = p switch
            {
                "youtube" => $"{c.YouTubeTitle}\n\n{c.YouTubeDescription}\n\n{c.YouTubeTags}",
                "instagram" => c.InstagramCaption,
                _ => c.TikTokCaption,
            };
            try
            {
                Clipboard.SetText(text);
                _main.Log.Write("Text in die Zwischenablage kopiert.");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }, _ => Selected is not null);
        UploadCommand = new RelayCommand(p =>
        {
            if (Selected is not { } c)
            {
                return;
            }

            // Half-automatic upload: text into the clipboard, clip shown in Explorer, upload page in the browser.
            // The user drags the video into the page and pastes the text (Ctrl+V).
            SaveSelected();
            CopyCommand.Execute(p);
            Shell.ShowInExplorer(c.Clip.VideoPath);
            Shell.Open(p switch
            {
                "youtube" => "https://www.youtube.com/upload",
                "instagram" => "https://www.instagram.com/",
                _ => "https://www.tiktok.com/tiktokstudio/upload",
            });
            _main.Log.Write("Hochladen: Clip in den Browser ziehen, Text mit Strg+V einfügen.");
        }, _ => Selected is not null);
        RedoCommand = new RelayCommand(_ => Redo(), _ => Selected is not null && !_main.JobRunning);
        UploadNowCommand = new AsyncCommand(UploadNowAsync, () => Selected is not null && !_main.JobRunning && UploadTargets.Any(t => t.IsChecked));
        ManageAccountsCommand = new RelayCommand(_ => _main.Page = "settings");
        DeleteCommand = new RelayCommand(_ =>
        {
            if (Selected is { } c && _main.Dialogs.Confirm("Clip löschen", $"„{c.Title}“ wirklich löschen?\nVideo, Infodatei und Vorschaubild werden entfernt.", true))
            {
                Selected = null;
                ClipLibrary.Delete(c.Clip);
                Refresh();
            }
        }, _ => Selected is not null);
    }

    public ObservableCollection<ClipCard> Clips { get; } = [];
    public ObservableCollection<string> Games { get; } = [AllGames];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddClipsCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand OpenYouTubeThumbnailCommand { get; }
    public RelayCommand ShowInFolderCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand SaveTextsCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand RedoCommand { get; }
    public AsyncCommand UploadNowCommand { get; }
    public RelayCommand ManageAccountsCommand { get; }
    public ObservableCollection<UploadTarget> UploadTargets { get; } = [];
    public bool HasAccounts => UploadTargets.Count > 0;
    public RelayCommand DeleteCommand { get; }

    public string Game { get => _game; set { if (Set(ref _game, value ?? AllGames)) { ApplyFilter(); } } }

    /// <summary>new, funny, score</summary>
    public string Sort { get => _sort; set { if (Set(ref _sort, value ?? "new")) { ApplyFilter(); } } }

    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) { ApplyFilter(); } } }

    public ClipCard? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (Set(ref _selected, value))
            {
                if (old is { Dirty: true })
                {
                    Save(old);
                }

                OnPropertyChanged(nameof(HasSelection));
                RefreshTargets();
            }
        }
    }

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => _all.Count == 0;
    public string CountText => $"{Clips.Count} Clip{(Clips.Count == 1 ? "" : "s")}";

    /// <summary>Connected accounts as checkboxes; what was ticked stays ticked for the next clip.</summary>
    public void RefreshTargets()
    {
        var ticked = UploadTargets.Where(t => t.IsChecked).Select(t => t.Account.Id).ToHashSet();
        UploadTargets.Clear();
        foreach (var a in _main.Engine.Uploads.Accounts.Accounts.OrderBy(a => a.Platform).ThenBy(a => a.Name))
        {
            UploadTargets.Add(new UploadTarget(a, ticked.Contains(a.Id)));
        }

        OnPropertyChanged(nameof(HasAccounts));
    }

    private async Task UploadNowAsync()
    {
        if (Selected is not { } card)
        {
            return;
        }

        SaveSelected();
        var targets = UploadTargets.Where(t => t.IsChecked).Select(t => t.Account).ToList();
        var engine = _main.Engine;
        IReadOnlyList<UCut.Upload.UploadOutcome> outcomes = [];
        await _main.RunJobAsync($"\"{card.Title}\" wird hochgeladen", async (progress, ct) =>
        {
            var p = new Progress<(UCut.Upload.UploadAccount Account, double Share)>(x =>
                progress.Report(new UCut.App.BatchProgress(targets.IndexOf(x.Account), targets.Count, $"{x.Account.Label} {x.Share:P0}")));
            outcomes = await engine.Uploads.UploadAsync(card.Clip, targets, p, ct);
            return new UCut.App.BatchResult(outcomes.Count(o => o.Error is null), outcomes.Count(o => o.Error is not null), false);
        }, _ =>
        {
            card.RefreshUploads();
            var lines = outcomes.Select(o => o.Error is null
                ? $"✓ {o.Account.Label}: {o.Result!.Note}"
                : $"✗ {o.Account.Label}: {o.Error}");
            _main.Dialogs.Info("Hochladen", string.Join("\n\n", lines));
        });
    }

    private void Redo()
    {
        if (Selected is not { } card)
        {
            return;
        }

        SaveSelected();
        var vm = new RedoViewModel(_main, card.Clip);
        var window = new Views.RedoWindow(vm) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var options = vm.BuildOptions();
        var engine = _main.Engine;
        _ = _main.RunJobAsync($"\"{card.Title}\" wird neu gemacht", async (progress, ct) =>
        {
            progress.Report(new UCut.App.BatchProgress(0, 1, card.Title));
            await engine.RedoAsync(card.Clip, options, ct);
            return new UCut.App.BatchResult(1, 0, false);
        }, _ =>
        {
            // New picture: forget the old card (and its thumbnail) so it is loaded fresh.
            _all.Remove(card);
            Refresh();
        });
    }

    public void Refresh()
    {
        var selectedPath = Selected?.Clip.VideoPath;
        var clips = ClipLibrary.Load(_main.Engine.Settings.OutputFolder);
        var old = _all.ToDictionary(c => c.Clip.VideoPath, StringComparer.OrdinalIgnoreCase);
        _all = clips.Select(c => old.TryGetValue(c.VideoPath, out var o) && !o.Dirty ? new ClipCard(c) { Thumbnail = o.Thumbnail } : new ClipCard(c)).ToList();

        var games = _all.Select(c => c.Game).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Games.Clear();
        Games.Add(AllGames);
        foreach (var g in games)
        {
            Games.Add(g);
        }

        if (!Games.Contains(_game))
        {
            _game = AllGames;
            OnPropertyChanged(nameof(Game));
        }

        ApplyFilter();
        Selected = selectedPath is null ? null : Clips.FirstOrDefault(c => string.Equals(c.Clip.VideoPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(IsEmpty));
        RefreshTargets();
        _ = LoadThumbnailsAsync(++_loadGeneration);
    }

    private void ApplyFilter()
    {
        IEnumerable<ClipCard> q = _all;
        if (_game != AllGames)
        {
            q = q.Where(c => string.Equals(c.Game, _game, StringComparison.OrdinalIgnoreCase));
        }

        if (_search.Length > 0)
        {
            q = q.Where(c => c.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) || c.Transcript.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }

        q = _sort switch
        {
            "funny" => q.OrderByDescending(c => c.Funny ?? -1).ThenByDescending(c => c.Score),
            "score" => q.OrderByDescending(c => c.Score),
            _ => q.OrderByDescending(c => c.Clip.Date),
        };
        Clips.Clear();
        foreach (var c in q)
        {
            Clips.Add(c);
        }

        OnPropertyChanged(nameof(CountText));
    }

    private async Task LoadThumbnailsAsync(int generation)
    {
        var ffmpeg = _main.Engine.Settings.FfmpegPath;
        foreach (var card in _all.Where(c => c.Thumbnail is null).ToList())
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            var path = await Task.Run(() => ClipLibrary.EnsureThumbnailAsync(card.Clip, ffmpeg));
            card.Thumbnail = Shell.LoadImage(path, 200);
        }
    }

    private void SaveSelected()
    {
        if (Selected is { } c)
        {
            Save(c);
            _main.Log.Write($"Texte gespeichert: {c.Title}");
        }
    }

    private void Save(ClipCard c)
    {
        try
        {
            c.Clip.Save();
            c.Dirty = false;
        }
        catch (IOException ex)
        {
            _main.Log.Write($"Konnte Infodatei nicht speichern: {ex.Message}");
        }
    }
}
