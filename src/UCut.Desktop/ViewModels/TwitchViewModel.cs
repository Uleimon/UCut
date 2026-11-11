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
    public string Reasons => string.Join(" · ", candidate.Reasons);
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
