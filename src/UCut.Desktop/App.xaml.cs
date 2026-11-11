using System.IO;
using System.Windows;
using UCut.Desktop.ViewModels;
using UCut.Settings;

namespace UCut.Desktop;

public partial class App : Application
{
    private MainViewModel? _main;
    private bool _showingError;
    private bool _selfTest;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _selfTest = e.Args.Contains("--ui-selftest");
        DispatcherUnhandledException += (_, args) =>
        {
            if (_selfTest)
            {
                Console.Error.WriteLine(args.Exception);
                args.Handled = true;
                Shutdown(3);
                return;
            }

            if (_showingError)
            {
                args.Handled = false;
                return;
            }

            _showingError = true;
            MessageBox.Show(args.Exception.ToString(), "UCut – unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            _showingError = false;
            args.Handled = true;
        };

        // Self-test runs against its own settings in a temp folder, so CI never touches real data.
        var settingsPath = SettingsStore.DefaultPath;
        if (_selfTest)
        {
            var dir = Directory.CreateTempSubdirectory("ucut-selftest-").FullName;
            settingsPath = Path.Combine(dir, "settings.json");
            var s = UCut.App.UCutEngine.DefaultSettings();
            s.OutputFolder = Path.Combine(dir, "out");
            s.WatchFolder = Path.Combine(dir, "watch");
            SettingsStore.Save(s, settingsPath);
        }

        _main = new MainViewModel(settingsPath, new WpfDialogs(), Dispatcher) { AutoInstallTools = !_selfTest };
        var window = new MainWindow { DataContext = _main };
        window.Show();
        _main.Library.Refresh();
        _ = _main.CheckToolsAsync();

        // Videos dropped onto UCut.exe (or "Öffnen mit") land in the queue.
        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (files.Count > 0)
        {
            _main.AddFiles(files);
        }

        if (_selfTest)
        {
            _ = RunSelfTestAsync(window, _main);
        }
    }

    /// <summary>For CI: shows every page once (forcing layout and bindings) and exits with 0; any exception gives code 3.</summary>
    private async Task RunSelfTestAsync(Window window, MainViewModel main)
    {
        try
        {
            foreach (var page in new[] { "library", "cutroom", "import", "twitch", "settings", "log", "library" })
            {
                main.Page = page;
                window.UpdateLayout();
                await Task.Delay(700);
                Console.Out.WriteLine($"page ok: {page}");
            }

            var video = Environment.GetEnvironmentVariable("UCUT_SELFTEST_VIDEO");
            if (!string.IsNullOrEmpty(video) && File.Exists(video))
            {
                await main.CutRoom.OpenAsync(video);
                window.UpdateLayout();
                main.CutRoom.SplitCommand.Execute(null);
                main.CutRoom.ClickTimeline(10, true);
                main.CutRoom.UndoCommand.Execute(null);
                window.UpdateLayout();
                await Task.Delay(1500);
                Console.Out.WriteLine($"cutroom ok: {main.CutRoom.Segments.Count} segment(s), {main.CutRoom.Tracks.Count} track(s)");

                main.AddFiles([video]);
                window.UpdateLayout();
                await Task.Delay(500);
                Console.Out.WriteLine($"import ok: {main.Import.Items.Count} item(s)");

                // A finished clip in the gallery: details with upload box, then the "Create" dialog with a cam drawn.
                var gameDir = Path.Combine(main.Engine.Settings.OutputFolder, "Test");
                Directory.CreateDirectory(gameDir);
                var clipPath = Path.Combine(gameDir, "2026-01-01_120000_selbsttest.mp4");
                File.Copy(video, clipPath, overwrite: true);
                var meta = new UCut.Metadata.ClipMetadata
                {
                    Video = Path.GetFileName(clipPath), SourceFile = video, Game = "Test", CreatedAt = DateTimeOffset.Now, DurationSeconds = 4,
                    Edit = new UCut.Metadata.EditInfo { CutStart = 0, CutEnd = 4, Mode = "Crop", Words = [new() { Text = "Hallo", Start = 0.2, End = 0.6 }] },
                };
                meta.YouTube.Title = "Selbsttest";
                meta.Uploaded["YouTube: Test"] = "https://youtube.com/shorts/x";
                meta.Save(Path.ChangeExtension(clipPath, ".json"));
                main.Page = "library";
                main.Library.Refresh();
                main.Library.Selected = main.Library.Clips.FirstOrDefault();
                window.UpdateLayout();
                await Task.Delay(800);
                Console.Out.WriteLine($"library ok: {main.Library.Clips.Count} clip(s), {main.Library.UploadTargets.Count} konto(s)");

                var redoVm = new RedoViewModel(main, main.Library.Selected!.Clip);
                var redo = new Views.RedoWindow(redoVm) { Owner = window };
                redo.Show();
                for (var i = 0; i < 40 && redoVm.Frame is null && redoVm.Status.Length == 0; i++)
                {
                    await Task.Delay(250);
                }

                redo.SetCamForTest(new UCut.Editing.NormRect(0.7, 0.65, 0.28, 0.3));
                redo.UpdateLayout();
                var ready = redoVm.CanRun(out var why);
                redo.Close();
                Console.Out.WriteLine($"redo ok: landscape={redoVm.IsLandscape}, frame={redoVm.Frame is not null}, words={redoVm.Words.Count}, ready={ready} {why} {redoVm.Status}");
                if (redoVm.Frame is null || !ready)
                {
                    throw new InvalidOperationException("Neu-machen-Dialog: kein Standbild oder nicht bereit");
                }
            }

            // Twitch preview panel with a fake clip; yt-dlp is missing here, so it must show an error text, not crash.
            main.Page = "twitch";
            var fake = new UCut.Twitch.TwitchClip("selftest", "https://clips.twitch.tv/selftest", "Selbsttest xD", 42, DateTimeOffset.Now, 12, "0", "test")
            {
                GameName = "Test",
            };
            var item = new TwitchItem(new UCut.Twitch.ClipCandidate(fake, 1, ["„xd“ im Titel"]));
            main.Twitch.Items.Add(item);
            main.Twitch.Current = item;
            window.UpdateLayout();
            for (var i = 0; i < 20 && main.Twitch.PreviewStatus.EndsWith("...", StringComparison.Ordinal); i++)
            {
                await Task.Delay(250);
            }

            window.UpdateLayout();
            Console.Out.WriteLine($"twitch preview ok: {main.Twitch.PreviewStatus}");

            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Shutdown(3);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        base.OnExit(e);
    }
}
