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
    public bool SubtitlesPending => Clip.Metadata.SubtitlesPending;
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

    /// <summary>The texts or ticks were changed elsewhere (tablet): show them again.</summary>
    public void Rebind() => OnPropertyChanged(string.Empty);

    public string UploadedText => Clip.Metadata.Uploaded.Count == 0
        ? "Noch nirgends hochgeladen."
        : string.Join("\n", Clip.Metadata.Uploaded.Select(u => $"{u.Key}: {u.Value}"));

    public bool IsUploaded => Clip.Metadata.Uploaded.Count > 0;

    public string UploadedPlatforms => "Hochgeladen auf: " + string.Join(", ",
        UploadMarks.Platforms.Where(HasUpload).DefaultIfEmpty("(anderswo)"));

    /// <summary>Tick "hochgeladen" by hand (after uploading in the browser); upload by account sets it by itself.</summary>
    public bool UploadedTikTok { get => HasUpload("TikTok"); set => MarkUploaded("TikTok", value); }
    public bool UploadedYouTube { get => HasUpload("YouTube"); set => MarkUploaded("YouTube", value); }
    public bool UploadedInstagram { get => HasUpload("Instagram"); set => MarkUploaded("Instagram", value); }

    private bool HasUpload(string platform) => UploadMarks.Has(Clip.Metadata, platform);

    private void MarkUploaded(string platform, bool on)
    {
        if (!UploadMarks.Set(Clip.Metadata, platform, on, DateTime.Now))
        {
            return;
        }

        try
        {
            Clip.Save();
        }
        catch (IOException)
        {
            Dirty = true;
        }

        RefreshUploads();
    }

    public void RefreshUploads()
    {
        OnPropertyChanged(nameof(UploadedText));
        OnPropertyChanged(nameof(IsUploaded));
        OnPropertyChanged(nameof(UploadedPlatforms));
        OnPropertyChanged(nameof(UploadedTikTok));
        OnPropertyChanged(nameof(UploadedYouTube));
        OnPropertyChanged(nameof(UploadedInstagram));
    }
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
    private List<ClipCard> _filtered = [];
    private int _shown = PageSize;

    /// <summary>Cards drawn at once; more with "Mehr anzeigen". Drawing every clip made each change slow.</summary>
    public const int PageSize = 120;

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
        DownloadCommand = new RelayCommand(async _ =>
        {
            if (Selected is not { } c)
            {
                return;
            }

            try
            {
                var (video, title) = (c.Clip.VideoPath, c.Title);
                var copy = await Task.Run(() => UCut.Upload.UploadCopy.SaveCopy(video, title, UCut.Upload.UploadCopy.DownloadsFolder));
                _main.Log.Write($"Heruntergeladen: {copy}");
                Shell.ShowInExplorer(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _main.Dialogs.Info("Herunterladen", $"Das ging nicht: {ex.Message}");
            }
        }, _ => Selected is not null);
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
        UploadCommand = new RelayCommand(async p =>
        {
            if (Selected is not { } c)
            {
                return;
            }

            // Half-automatic upload: text into the clipboard, clip shown in Explorer, upload page in the browser.
            // The user drags the video into the page and pastes the text (Ctrl+V).
            // TikTok Studio and YouTube Studio take the file name as text, so a copy is named after the title/caption.
            SaveSelected();
            CopyCommand.Execute(p);
            var nameText = p switch
            {
                "youtube" => c.YouTubeTitle,
                "instagram" => c.Title,
                _ => c.TikTokCaption,
            };
            if (p is "youtube")
            {
                // The title comes from the file name; the clipboard only needs what goes into the description.
                try
                {
                    Clipboard.SetText($"{c.YouTubeDescription}\n\n{c.YouTubeTags}");
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }
            }

            var file = c.Clip.VideoPath;
            try
            {
                // Copying a whole video takes a moment: not on the window's thread.
                var source = file;
                file = await Task.Run(() => UCut.Upload.UploadCopy.Create(source, nameText, UCut.Upload.UploadCopy.DefaultFolder));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _main.Log.Write($"Hochladen: Kopie mit Titel ging nicht ({ex.Message}), nehme die Originaldatei.");
            }

            Shell.ShowInExplorer(file);
            Shell.Open(p switch
            {
                "youtube" => "https://www.youtube.com/upload",
                "instagram" => "https://www.instagram.com/",
                _ => "https://www.tiktok.com/tiktokstudio/upload",
            });
            _main.Log.Write("Hochladen: Clip in den Browser ziehen, Text mit Strg+V einfügen.");
            if (_hideUploadHint || !_main.Engine.Settings.Upload.ShowUploadHint)
            {
                return;
            }

            var answer = _main.Dialogs.Hint("Hochladen", p switch
            {
                "youtube" => "1. Zieh die markierte Datei aus dem Explorer in YouTube Studio.\n2. Der Titel steht dann schon drin (die Datei heißt so).\n3. Klick in „Beschreibung“ und drück Strg+V. Da kommen Beschreibung und Hashtags rein.\n\nWenn es online ist: in UCut bei YouTube den Haken „Hochgeladen“ setzen.",
                "instagram" => "1. Zieh die markierte Datei aus dem Explorer in Instagram.\n2. Klick in die Bildunterschrift und drück Strg+V.\n\nWenn es online ist: in UCut bei Instagram den Haken „Hochgeladen“ setzen.",
                _ => "1. Zieh die markierte Datei aus dem Explorer in TikTok Studio.\n2. Die Beschreibung mit Hashtags steht dann schon drin (die Datei heißt so).\n3. Falls etwas fehlt: in „Beschreibung“ klicken, Strg+A, dann Strg+V.\n\nWenn es online ist: in UCut bei TikTok den Haken „Hochgeladen“ setzen.",
            });
            if (answer == HintAnswer.HideThisSession)
            {
                _hideUploadHint = true;
            }
            else if (answer == HintAnswer.HideForever)
            {
                _main.SetShowUploadHint(false);
            }
        }, _ => Selected is not null);
        RedoCommand = new RelayCommand(_ => Redo(), _ => Selected is not null && !_main.JobRunning);
        RecreateAllCommand = new RelayCommand(_ => RecreateAll(), _ => _filtered.Count > 0 && !_main.JobRunning);
        ShowMoreCommand = new RelayCommand(_ =>
        {
            _shown += PageSize;
            ShowPage();
        });
        SubtitlesNowCommand = new RelayCommand(_ => { if (Selected is { SubtitlesPending: true } c) { AddSubtitles([c]); } }, _ => Selected is { SubtitlesPending: true } && !_main.JobRunning);
        SubtitlesAllCommand = new RelayCommand(_ => AddSubtitles(_all.Where(c => c.SubtitlesPending).ToList()), _ => HasPendingSubtitles && !_main.JobRunning);
        UploadNowCommand = new AsyncCommand(UploadNowAsync, () => Selected is not null && !_main.JobRunning && UploadTargets.Any(t => t.IsChecked));
        ManageAccountsCommand = new RelayCommand(_ => _main.Page = "settings");
        ClearUploadFolderCommand = new RelayCommand(_ =>
        {
            var (deleted, locked) = UCut.Upload.UploadCopy.Clear(UCut.Upload.UploadCopy.DefaultFolder);
            _main.Log.Write($"Hochlad-Ordner geleert: {deleted} Datei(en) gelöscht" + (locked > 0 ? $", {locked} noch offen." : "."));
            _main.Dialogs.Info("Hochlad-Ordner", deleted == 0 && locked == 0
                ? "Der Hochlad-Ordner ist schon leer."
                : $"{deleted} Kopie(n) gelöscht. Deine Original-Clips bleiben, wo sie sind."
                  + (locked > 0 ? $"\n\n{locked} Datei(en) sind noch offen (lädt der Browser sie noch hoch?). Danach nochmal drücken." : ""));
        });
        OpenUploadFolderCommand = new RelayCommand(_ =>
        {
            System.IO.Directory.CreateDirectory(UCut.Upload.UploadCopy.DefaultFolder);
            Shell.Open(UCut.Upload.UploadCopy.DefaultFolder);
        });
        DeleteCommand = new RelayCommand(_ =>
        {
            if (Selected is { } c && _main.Dialogs.Confirm("Clip löschen", $"„{c.Title}“ wirklich löschen?\nVideo, Infodatei und Vorschaubild werden entfernt.", true))
            {
                _ = DeleteAsync(c);
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
    public RelayCommand DownloadCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand RecreateAllCommand { get; }
    public RelayCommand ShowMoreCommand { get; }
    public bool HasMore => Clips.Count < _filtered.Count;
    public string MoreText => $"Mehr anzeigen ({_filtered.Count - Clips.Count} weitere)";
    public RelayCommand SubtitlesNowCommand { get; }
    public RelayCommand SubtitlesAllCommand { get; }
    public bool HasPendingSubtitles => _all.Any(c => c.SubtitlesPending);
    public string PendingSubtitlesText => $"Untertitel nachholen ({_all.Count(c => c.SubtitlesPending)})";
    public RelayCommand ClearUploadFolderCommand { get; }
    private bool _hideUploadHint;
    public RelayCommand OpenUploadFolderCommand { get; }
    public AsyncCommand UploadNowCommand { get; }
    public RelayCommand ManageAccountsCommand { get; }
    public ObservableCollection<UploadTarget> UploadTargets { get; } = [];
    public bool HasAccounts => UploadTargets.Count > 0;
    public RelayCommand DeleteCommand { get; }

    public string Game { get => _game; set { if (Set(ref _game, value ?? AllGames)) { _shown = PageSize; ApplyFilter(); } } }

    /// <summary>new, funny, score</summary>
    public string Sort { get => _sort; set { if (Set(ref _sort, value ?? "new")) { _shown = PageSize; ApplyFilter(); } } }

    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) { _shown = PageSize; ApplyFilter(); } } }

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
    public string CountText => $"{_filtered.Count} Clip{(_filtered.Count == 1 ? "" : "s")}";

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

    /// <summary>All clips shown right now (so a game filter recreates only that game), rendered again as they were.</summary>
    private void RecreateAll()
    {
        SaveSelected();
        var engine = _main.Engine;
        var clips = _filtered.Select(c => c.Clip).ToList();
        var missing = clips.Count(c => engine.FindOriginal(c) is null);
        var which = Game == AllGames && Search.Length == 0 ? "Alle" : "Alle angezeigten";
        var text = $"{which} {clips.Count} Clips werden genau wie vorher neu gerendert (gleicher Schnitt, gleicher Aufbau, gleiche Wörter). "
                 + "Titel, Texte und Uploads bleiben. Alte Clips bekommen dabei Wortzeiten, danach kannst du ihre Untertitel bearbeiten.\n\n"
                 + (missing > 0 ? $"{missing} davon haben kein Original mehr und werden übersprungen.\n\n" : "")
                 + "Das kann dauern (etwa so lange wie beim ersten Mal). Du kannst unten jederzeit abbrechen.";
        if (!_main.Dialogs.Confirm("Recreate", text, false))
        {
            return;
        }

        Selected = null;
        _ = _main.RunJobAsync($"Recreate: {clips.Count} Clips", (progress, ct) => engine.RecreateAsync(clips, progress, ct), r =>
        {
            Selected = null;
            _main.Dialogs.Info("Recreate", $"{r.Done} Clips neu gerendert" + (r.Failed > 0 ? $", {r.Failed} übersprungen (siehe Protokoll)." : ".")
                                          + (r.Cancelled ? "\nAbgebrochen, der Rest ist unverändert." : ""));
        });
    }

    /// <summary>
    /// The card disappears at once; the files are deleted in the background (a file still held by the player
    /// is retried for some seconds, which used to freeze the window).
    /// </summary>
    private async Task DeleteAsync(ClipCard c)
    {
        c.Dirty = false; // nothing to save any more
        Selected = null;
        _all.Remove(c);
        ApplyFilter();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasPendingSubtitles));
        OnPropertyChanged(nameof(PendingSubtitlesText));
        try
        {
            await Task.Run(() => ClipLibrary.Delete(c.Clip));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _main.Log.Write($"Löschen ging nicht: {ex.Message}");
            _main.Dialogs.Info("Clip löschen", ex.Message);
            Refresh();
        }
    }

    /// <summary>Clips made with Instant Clip off get their subtitles now; cut, layout and texts stay.</summary>
    private void AddSubtitles(List<ClipCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        SaveSelected();
        var engine = _main.Engine;
        var clips = cards.Select(c => c.Clip).ToList();
        Selected = null; // the player lets go of the video, so it can be overwritten
        _ = _main.RunJobAsync($"Untertitel für {clips.Count} Clip(s)", (progress, ct) => engine.AddSubtitlesAsync(clips, progress, ct), r =>
        {
            foreach (var c in cards)
            {
                _all.Remove(c);
            }

            Refresh();
            if (cards.Count > 1)
            {
                _main.Dialogs.Info("Untertitel", $"{r.Done} Clip(s) haben jetzt Untertitel" + (r.Failed > 0 ? $", {r.Failed} gingen nicht (siehe Protokoll)." : ".")
                                                 + (r.Cancelled ? "\nAbgebrochen, der Rest bleibt ohne Untertitel." : ""));
            }
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
        Selected = null; // the player lets go of the video, so it can be overwritten
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

    /// <summary>
    /// Reads the output folder again. Clips whose info file did not change keep their card, so the gallery is not
    /// rebuilt and a clip that is playing keeps playing. <paramref name="rebind"/>: texts were changed from the tablet.
    /// </summary>
    public void Refresh(bool rebind = false)
    {
        var selected = Selected;
        var clips = ClipLibrary.Load(_main.Engine.Settings.OutputFolder);
        var old = _all.ToDictionary(c => c.Clip.VideoPath, StringComparer.OrdinalIgnoreCase);
        _all = clips.Select(c =>
        {
            if (!old.TryGetValue(c.VideoPath, out var o))
            {
                return new ClipCard(c);
            }

            if (ReferenceEquals(o.Clip, c))
            {
                if (rebind)
                {
                    o.Rebind();
                }

                return o;
            }

            return o.Dirty ? new ClipCard(c) : new ClipCard(c) { Thumbnail = o.Thumbnail };
        }).ToList();

        var games = _all.Select(c => c.Game).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!games.SequenceEqual(Games.Skip(1), StringComparer.Ordinal))
        {
            var game = _game;
            Games.Clear();
            Games.Add(AllGames);
            foreach (var g in games)
            {
                Games.Add(g);
            }

            // Clearing the list empties the combo box; put the choice back.
            _game = Games.Contains(game) ? game : AllGames;
            OnPropertyChanged(nameof(Game));
        }

        ApplyFilter();
        if (selected is not null)
        {
            Selected = _all.Contains(selected)
                ? selected
                : _all.FirstOrDefault(c => string.Equals(c.Clip.VideoPath, selected.Clip.VideoPath, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasPendingSubtitles));
        OnPropertyChanged(nameof(PendingSubtitlesText));
        RefreshTargets();
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
        _filtered = q.ToList();
        ShowPage();
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Brings the shown cards in line with the first page(s) of the filtered list, touching only what differs.</summary>
    private void ShowPage()
    {
        var target = _filtered.Take(_shown).ToList();
        if (!target.SequenceEqual(Clips))
        {
            var keep = new HashSet<ClipCard>(target);
            for (var i = Clips.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(Clips[i]))
                {
                    Clips.RemoveAt(i);
                }
            }

            for (var i = 0; i < target.Count; i++)
            {
                if (i < Clips.Count && ReferenceEquals(Clips[i], target[i]))
                {
                    continue;
                }

                var at = Clips.IndexOf(target[i]);
                if (at >= 0)
                {
                    Clips.Move(at, i);
                }
                else
                {
                    Clips.Insert(i, target[i]);
                }
            }
        }

        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
        _ = LoadThumbnailsAsync(++_loadGeneration);
    }

    private async Task LoadThumbnailsAsync(int generation)
    {
        var ffmpeg = _main.Engine.Settings.FfmpegPath;
        foreach (var card in Clips.Where(c => c.Thumbnail is null).ToList())
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            // Decoded on a background thread (the picture is frozen), so scrolling stays smooth.
            card.Thumbnail = await Task.Run(async () => Shell.LoadImage(await ClipLibrary.EnsureThumbnailAsync(card.Clip, ffmpeg), 200));
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
