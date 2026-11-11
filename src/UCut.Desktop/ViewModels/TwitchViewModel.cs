using System.Collections.ObjectModel;
using UCut.Twitch;

namespace UCut.Desktop.ViewModels;

internal sealed class TwitchItem(ClipCandidate candidate) : ObservableObject
{
    private bool _selected = true;

    public TwitchClip Clip => candidate.Clip;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Title => Clip.Title;
    public string Thumbnail => Clip.ThumbnailUrl;
    public string DateText => Clip.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
    public string Views => $"{Clip.ViewCount:N0} Aufrufe";
    public string Game => Clip.GameName ?? "?";
    public string Duration => $"{Clip.DurationSeconds:0} s";
    public string Reasons => candidate.Reasons.Count == 0 ? "" : "Vorgeschlagen, weil: " + string.Join(" und ", candidate.Reasons);
}

/// <summary>Old Twitch clips: search, pick, load overnight.</summary>
internal sealed class TwitchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private DateTime _from = new(2020, 1, 1);
    private DateTime _to = DateTime.Today;
    private bool _onlyFunny = true;
    private bool _shutdownAfter;
    private string _summary = "";
    private bool _searching;
    private TwitchItem? _current;
    private Uri? _previewUri;
    private string _previewStatus = "";
    private CancellationTokenSource? _previewCts;

    public TwitchViewModel(MainViewModel main)
    {
        _main = main;
        if (DateTime.TryParse(main.Engine.Settings.TwitchClips.Since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var since))
        {
            _from = since;
        }

        SearchCommand = new AsyncCommand(SearchAsync, () => !_searching && !_main.JobRunning);
        LoadCommand = new AsyncCommand(LoadAsync, () => Items.Any(i => i.Selected) && !_main.JobRunning);
        SelectAllCommand = new RelayCommand(p => { foreach (var i in Items) { i.Selected = p is "true"; } });
        OpenClipCommand = new RelayCommand(p => { if (p is TwitchItem t) { Shell.Open(t.Clip.Url); } });
        OpenSettingsCommand = new RelayCommand(_ => _main.Page = "settings");
        OpenDevConsoleCommand = new RelayCommand(_ => Shell.Open("https://dev.twitch.tv/console/apps"));
    }

    public ObservableCollection<TwitchItem> Items { get; } = [];
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand LoadCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand OpenClipCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenDevConsoleCommand { get; }

    public bool IsConfigured => _main.Engine.Twitch.IsConfigured;
    public string Channel => _main.Engine.Settings.Games.TwitchChannel;
    public DateTime From { get => _from; set => Set(ref _from, value); }
    public DateTime To { get => _to; set => Set(ref _to, value); }
    public bool OnlyFunny { get => _onlyFunny; set => Set(ref _onlyFunny, value); }
    public bool ShutdownAfter { get => _shutdownAfter; set => Set(ref _shutdownAfter, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool Searching { get => _searching; private set => Set(ref _searching, value); }

    /// <summary>The clip shown in the preview panel (click on a card).</summary>
    public TwitchItem? Current
    {
        get => _current;
        set
        {
            if (Set(ref _current, value))
            {
                OnPropertyChanged(nameof(HasCurrent));
                _ = LoadPreviewAsync(value);
            }
        }
    }

    public bool HasCurrent => _current is not null;
    public Uri? PreviewUri { get => _previewUri; private set => Set(ref _previewUri, value); }
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(Channel));
    }

    private async Task SearchAsync()
    {
        Searching = true;
        Summary = "Suche läuft, Twitch wird Monat für Monat gefragt ...";
        try
        {
            var engine = _main.Engine;
            var from = new DateTimeOffset(From.Date);
            var to = new DateTimeOffset(To.Date.AddDays(1));
            var r = await Task.Run(() => engine.SearchTwitchAsync(from, to, OnlyFunny, 0));
            Current = null;
            Items.Clear();
            foreach (var c in r.Candidates)
            {
                Items.Add(new TwitchItem(c));
            }

            Summary = $"{r.Found} Clips gefunden, {r.Candidates.Count} {(OnlyFunny ? "sehen lustig aus" : "neu")}, {r.AlreadyLoaded} schon geladen.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            Summary = ex.Message;
        }
        finally
        {
            Searching = false;
        }
    }

    /// <summary>Downloads the clip into the Twitch originals folder (reused later when loading) and shows it.</summary>
    private async Task LoadPreviewAsync(TwitchItem? item)
    {
        if (_previewCts is not null)
        {
            await _previewCts.CancelAsync();
            _previewCts.Dispose();
        }

        PreviewUri = null;
        if (item is null)
        {
            _previewCts = null;
            PreviewStatus = "";
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        PreviewStatus = "Vorschau wird geladen ...";
        try
        {
            var engine = _main.Engine;
            var file = await Task.Run(() => engine.DownloadTwitchClipAsync(item.Clip, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(item, _current))
            {
                PreviewUri = new Uri(file);
                PreviewStatus = "";
            }
        }
        catch (OperationCanceledException)
        {
            // Another clip was clicked.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            if (ReferenceEquals(item, _current))
            {
                PreviewStatus = "Vorschau ging nicht: " + ex.Message + " (yt-dlp in den Einstellungen prüfen)";
            }
        }
    }

    private async Task LoadAsync()
    {
        var chosen = Items.Where(i => i.Selected).ToList();
        var engine = _main.Engine;
        await _main.RunJobAsync($"{chosen.Count} Twitch-Clip(s) werden geladen", (progress, ct) =>
            engine.ProcessTwitchAsync(chosen.Select(c => c.Clip).ToList(), progress, ct), r =>
        {
            foreach (var c in chosen)
            {
                Items.Remove(c);
            }

            if (Current is not null && !Items.Contains(Current))
            {
                Current = null;
            }

            if (ShutdownAfter && !r.Cancelled)
            {
                _main.Log.Write("Fertig, der PC wird in 2 Minuten ausgeschaltet (abbrechen: shutdown /a).");
                Shell.ShutdownIn(120);
            }
            else if (r.Done > 0)
            {
                _main.Page = "library";
            }
        });
    }
}
