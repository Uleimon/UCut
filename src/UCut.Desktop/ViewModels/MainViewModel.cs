using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using UCut.App;
using UCut.Settings;

namespace UCut.Desktop.ViewModels;

/// <summary>The window: pages, the one running job (with progress and cancel), Aitum watching, live log.</summary>
internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _ui;
    private string _page = "library";
    private CancellationTokenSource? _jobCts;
    private CancellationTokenSource? _watchCts;
    private bool _jobRunning;
    private string _jobTitle = "";
    private string _jobDetail = "";
    private double _jobProgress;
    private bool _isWatching;
    private string _toolWarning = "";

    public MainViewModel(string settingsPath, IDialogs dialogs, Dispatcher ui)
    {
        SettingsPath = settingsPath;
        Dialogs = dialogs;
        _ui = ui;
        Log = new RunLog();
        Log.Line += line => _ = _ui.BeginInvoke(() => LogPage?.Add(line));

        var settings = UCutEngine.LoadOrCreateSettings(settingsPath, out var created);
        Engine = CreateEngine(settings);
        if (created)
        {
            Log.Write($"Einstellungen angelegt: {settingsPath}");
        }

        LogPage = new LogViewModel(this);
        Library = new LibraryViewModel(this);
        CutRoom = new CutRoomViewModel(this);
        Import = new ImportViewModel(this);
        Twitch = new TwitchViewModel(this);
        Settings = new SettingsViewModel(this);

        NavigateCommand = new RelayCommand(p => Page = p?.ToString() ?? "library");
        CancelJobCommand = new RelayCommand(_ => _jobCts?.Cancel(), _ => JobRunning);
        ToggleWatchCommand = new RelayCommand(_ => IsWatching = !IsWatching);
    }

    public string SettingsPath { get; }
    public IDialogs Dialogs { get; }
    public RunLog Log { get; }
    public UCutEngine Engine { get; private set; }

    public LibraryViewModel Library { get; }
    public CutRoomViewModel CutRoom { get; }
    public ImportViewModel Import { get; }
    public TwitchViewModel Twitch { get; }
    public SettingsViewModel Settings { get; }
    public LogViewModel LogPage { get; }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand CancelJobCommand { get; }
    public RelayCommand ToggleWatchCommand { get; }

    public string Version => typeof(UCutEngine).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>library, cutroom, import, twitch, settings, log</summary>
    public string Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    public object CurrentPage => Page switch
    {
        "cutroom" => CutRoom,
        "import" => Import,
        "twitch" => Twitch,
        "settings" => Settings,
        "log" => LogPage,
        _ => Library,
    };

    public bool JobRunning { get => _jobRunning; private set => Set(ref _jobRunning, value); }
    public string JobTitle { get => _jobTitle; private set => Set(ref _jobTitle, value); }
    public string JobDetail { get => _jobDetail; private set => Set(ref _jobDetail, value); }
    public double JobProgress { get => _jobProgress; private set => Set(ref _jobProgress, value); }
    public string ToolWarning { get => _toolWarning; private set => Set(ref _toolWarning, value); }

    public bool IsWatching
    {
        get => _isWatching;
        set
        {
            if (value == _isWatching)
            {
                return;
            }

            if (value)
            {
                StartWatching();
            }
            else
            {
                _watchCts?.Cancel();
            }
        }
    }

    public string WatchFolder => Engine.Settings.WatchFolder;

    private UCutEngine CreateEngine(UCutSettings settings)
    {
        var engine = new UCutEngine(settings, Log);
        engine.ClipFinished += r => _ = _ui.BeginInvoke(() => Library?.Refresh());
        return engine;
    }

    /// <summary>After the settings were saved: new engine with the new values.</summary>
    public void ReloadEngine()
    {
        var watching = IsWatching;
        _watchCts?.Cancel();
        var old = Engine;
        Engine = CreateEngine(SettingsStore.Load(SettingsPath));
        old.Dispose();
        OnPropertyChanged(nameof(Engine));
        OnPropertyChanged(nameof(WatchFolder));
        Library.Refresh();
        _ = CheckToolsAsync();
        if (watching)
        {
            _ui.BeginInvoke(StartWatching, DispatcherPriority.Background);
        }
    }

    public async Task CheckToolsAsync()
    {
        var t = await Engine.CheckToolsAsync();
        var missing = new List<string>();
        if (!t.Ffmpeg)
        {
            missing.Add("ffmpeg fehlt (wird zum Schneiden gebraucht)");
        }

        if (!t.YtDlp)
        {
            missing.Add("yt-dlp fehlt (nur für Twitch-Clips)");
        }

        ToolWarning = string.Join(" · ", missing);
        Settings.UpdateTools(t);
    }

    /// <summary>Runs one long job at a time on a background thread, with progress, cancel and keep-awake.</summary>
    public async Task RunJobAsync(string title, Func<IProgress<BatchProgress>, CancellationToken, Task<BatchResult>> job, Action<BatchResult>? done = null)
    {
        if (JobRunning)
        {
            Dialogs.Info("UCut", "Es läuft schon eine Aufgabe. Warte, bis sie fertig ist, oder brich sie unten ab.");
            return;
        }

        _jobCts = new CancellationTokenSource();
        JobRunning = true;
        JobTitle = title;
        JobDetail = "";
        JobProgress = 0;
        Shell.KeepAwake(true);
        var progress = new Progress<BatchProgress>(p =>
        {
            JobProgress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            JobDetail = p.Total == 0 ? "" : $"{Math.Min(p.Done + 1, p.Total)} von {p.Total}: {p.Current}";
        });
        try
        {
            var ct = _jobCts.Token;
            var result = await Task.Run(() => job(progress, ct), CancellationToken.None);
            done?.Invoke(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            Log.Write($"Fehler: {ex.Message}");
            if (ex is not OperationCanceledException)
            {
                Dialogs.Info("UCut – Fehler", ex.Message);
            }
        }
        finally
        {
            Shell.KeepAwake(false);
            JobRunning = false;
            JobDetail = "";
            _jobCts.Dispose();
            _jobCts = null;
            Library.Refresh();
        }
    }

    /// <summary>Files dropped on the window or passed on the command line.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        Import.Add(paths);
        Page = "import";
    }

    private void StartWatching()
    {
        if (!Directory.Exists(Engine.Settings.WatchFolder))
        {
            Dialogs.Info("Aitum-Ordner", $"Der Aitum-Ordner existiert nicht:\n{Engine.Settings.WatchFolder}\n\nStell ihn in den Einstellungen ein.");
            OnPropertyChanged(nameof(IsWatching));
            return;
        }

        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        var engine = Engine;
        _isWatching = true;
        OnPropertyChanged(nameof(IsWatching));
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.WatchAsync(ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
            {
                Log.Write($"Überwachung gestoppt: {ex.Message}");
            }
            finally
            {
                _ = _ui.BeginInvoke(() =>
                {
                    _isWatching = false;
                    OnPropertyChanged(nameof(IsWatching));
                });
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _watchCts?.Cancel();
        _jobCts?.Cancel();
        Shell.KeepAwake(false);
        Engine.Dispose();
    }
}

internal sealed class LogViewModel(MainViewModel main) : ObservableObject
{
    private const int MaxLines = 3000;

    public ObservableCollection<string> Lines { get; } = new(RunLog.Tail(Path.Combine(main.Engine.Settings.OutputFolder, UCutEngine.LogFileName), 300));

    public string? LastLine => Lines.Count > 0 ? Lines[^1] : null;

    public RelayCommand OpenFileCommand { get; } = new(_ =>
    {
        if (main.Log.FilePath is { } f && File.Exists(f))
        {
            Shell.Open(f);
        }
    });

    public RelayCommand OpenFolderCommand { get; } = new(_ => Shell.Open(main.Engine.Settings.OutputFolder));

    public void Add(string line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }

        OnPropertyChanged(nameof(LastLine));
    }
}
