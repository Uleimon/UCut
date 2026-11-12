using System.Collections.ObjectModel;
using System.IO;
using UCut.Pipeline;
using UCut.App;

namespace UCut.Desktop.ViewModels;

internal sealed class QueueItem(string path) : ObservableObject
{
    private string _status = "wartet";

    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>Own clips: drop or pick videos, choose the game, let UCut cut, caption and finish them.</summary>
internal sealed class ImportViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _game = "";

    public ImportViewModel(MainViewModel main)
    {
        _main = main;
        AddFilesCommand = new RelayCommand(_ => Add(_main.Dialogs.PickFiles("Clips hinzufügen", WpfDialogs.VideoFilter, true)));
        AddFolderCommand = new RelayCommand(_ =>
        {
            if (_main.Dialogs.PickFolder("Ordner mit Clips wählen") is { } f)
            {
                Add([f]);
            }
        });
        RemoveCommand = new RelayCommand(p => { if (p is QueueItem q) { Items.Remove(q); Changed(); } }, _ => !_main.JobRunning);
        ClearCommand = new RelayCommand(_ => { Items.Clear(); Changed(); }, _ => !_main.JobRunning && Items.Count > 0);
        StartCommand = new AsyncCommand(StartAsync, () => Items.Any(i => i.Status != "fertig") && !_main.JobRunning);
    }

    public ObservableCollection<QueueItem> Items { get; } = [];
    public ObservableCollection<string> KnownGames { get; } = [];

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncCommand StartCommand { get; }

    /// <summary>Empty = automatic (fresh clips: running game, older ones: Unsortiert).</summary>
    public string Game { get => _game; set => Set(ref _game, value ?? ""); }

    public bool IsEmpty => Items.Count == 0;

    public void Add(IEnumerable<string> paths)
    {
        var files = ClipImport.Collect(paths, out _);
        foreach (var f in files)
        {
            if (!Items.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase)))
            {
                Items.Add(new QueueItem(f));
            }
        }

        KnownGames.Clear();
        foreach (var g in _main.Library.Games.Where(g => g != LibraryViewModel.AllGames))
        {
            KnownGames.Add(g);
        }

        Changed();
    }

    private void Changed() => OnPropertyChanged(nameof(IsEmpty));

    private async Task StartAsync()
    {
        var todo = Items.Where(i => i.Status != "fertig").ToList();
        foreach (var i in todo)
        {
            i.Status = "wartet";
        }

        var game = string.IsNullOrWhiteSpace(Game) ? null : Game.Trim();
        var engine = _main.Engine;
        void Finished(ClipResult r) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var i in todo.Where(i => string.Equals(i.Path, r.Metadata.SourceFile, StringComparison.OrdinalIgnoreCase)))
            {
                i.Status = "fertig";
            }
        });

        engine.ClipFinished += Finished;
        try
        {
            await _main.RunJobAsync($"{todo.Count} Clip(s) werden bearbeitet", (progress, ct) =>
            {
                var both = new Progress<BatchProgress>(p =>
                {
                    progress.Report(p);
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var stale in todo.Where(i => i.Status == "läuft"))
                        {
                            stale.Status = "Fehler (siehe Protokoll)";
                        }

                        if (todo.FirstOrDefault(i => i.Name == p.Current && i.Status == "wartet") is { } current)
                        {
                            current.Status = "läuft";
                        }
                    });
                });
                return engine.ProcessFilesAsync(todo.Select(t => t.Path), game, both, ct);
            }, r =>
            {
                foreach (var i in todo.Where(i => i.Status == "läuft"))
                {
                    i.Status = r.Cancelled ? "abgebrochen" : "Fehler (siehe Protokoll)";
                }

                if (r.Failed > 0)
                {
                    _main.Dialogs.Info("UCut", $"{r.Done} Clip(s) fertig, {r.Failed} mit Fehler. Details stehen im Protokoll.");
                }
                else if (r.Done > 0 && !r.Cancelled)
                {
                    _main.Page = "library";
                }
            });
        }
        finally
        {
            engine.ClipFinished -= Finished;
        }
    }
}
