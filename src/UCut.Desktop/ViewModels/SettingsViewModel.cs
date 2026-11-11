using System.IO;
using UCut.App;
using UCut.Editing;
using UCut.Settings;

namespace UCut.Desktop.ViewModels;

/// <summary>The important settings as a form. Everything else stays in settings.json (button "Datei öffnen").</summary>
internal sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private UCutSettings _s = new();
    private string _claudeKey = "";
    private string _toolStatus = "";
    private bool _ffmpegOk;
    private bool _ytDlpOk;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Load();
        SaveCommand = new RelayCommand(_ => Save());
        ReloadCommand = new RelayCommand(_ => Load());
        OpenFileCommand = new RelayCommand(_ => Shell.Open(_main.SettingsPath));
        BrowseCommand = new RelayCommand(p =>
        {
            if (_main.Dialogs.PickFolder("Ordner wählen") is { } f)
            {
                if (p is "watch")
                {
                    WatchFolder = f;
                }
                else
                {
                    OutputFolder = f;
                }
            }
        });
        InstallCommand = new RelayCommand(p => Shell.RunInConsole(p is "ytdlp"
            ? "winget install --id yt-dlp.yt-dlp -e && echo. && echo Fertig. UCut einmal neu starten."
            : "winget install --id Gyan.FFmpeg -e && echo. && echo Fertig. UCut einmal neu starten."));
        TwitchConsoleCommand = new RelayCommand(_ => Shell.Open("https://dev.twitch.tv/console/apps"));
        AnthropicConsoleCommand = new RelayCommand(_ => Shell.Open("https://console.anthropic.com/settings/keys"));
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand TwitchConsoleCommand { get; }
    public RelayCommand AnthropicConsoleCommand { get; }

    public string SettingsPath => _main.SettingsPath;

    public string WatchFolder { get => _s.WatchFolder; set { _s.WatchFolder = value; OnPropertyChanged(); } }
    public string OutputFolder { get => _s.OutputFolder; set { _s.OutputFolder = value; OnPropertyChanged(); } }
    public string FfmpegPath { get => _s.FfmpegPath; set { _s.FfmpegPath = value; _s.FfprobePath = DeriveProbe(value); OnPropertyChanged(); } }
    public string YtDlpPath { get => _s.TwitchClips.YtDlpPath; set { _s.TwitchClips.YtDlpPath = value; OnPropertyChanged(); } }

    public string TwitchChannel { get => _s.Games.TwitchChannel; set { _s.Games.TwitchChannel = value.Trim(); OnPropertyChanged(); } }
    public string TwitchClientId { get => _s.Games.TwitchClientId; set { _s.Games.TwitchClientId = value.Trim(); OnPropertyChanged(); } }
    public string TwitchClientSecret { get => _s.Games.TwitchClientSecret; set { _s.Games.TwitchClientSecret = value.Trim(); OnPropertyChanged(); } }
    public string ClaudeKey { get => _claudeKey; set => Set(ref _claudeKey, value.Trim()); }
    public bool UseClaude { get => _s.Writer.UseClaude; set { _s.Writer.UseClaude = value; OnPropertyChanged(); } }
    public string ChannelName { get => _s.Writer.ChannelName; set { _s.Writer.ChannelName = value; OnPropertyChanged(); } }
    public string CommonHashtags { get => string.Join(" ", _s.Games.CommonHashtags); set { _s.Games.CommonHashtags = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); OnPropertyChanged(); } }

    public IReadOnlyList<Choice<string>> WhisperModels { get; } =
        [new("small", "Schnell (small)"), new("medium", "Genau (medium, Standard)"), new("largeV3Turbo", "Sehr genau (large turbo, braucht mehr Leistung)")];

    public Choice<string> WhisperModel
    {
        get => WhisperModels.FirstOrDefault(m => string.Equals(m.Value, _s.Whisper.Model, StringComparison.OrdinalIgnoreCase)) ?? WhisperModels[1];
        set { _s.Whisper.Model = value.Value; OnPropertyChanged(); }
    }

    public string Language { get => _s.Whisper.Language; set { _s.Whisper.Language = value.Trim(); OnPropertyChanged(); } }

    public IReadOnlyList<Choice<ReframeMode>> ReframeModes { get; } =
        [new(UCut.Editing.ReframeMode.Split, "Cam oben, Spiel unten (Cam wird gesucht)"), new(UCut.Editing.ReframeMode.Crop, "Zuschneiden (Mitte)"), new(UCut.Editing.ReframeMode.Blur, "Ganzes Bild, unscharfer Hintergrund")];

    public Choice<ReframeMode> ReframeMode
    {
        get => ReframeModes.First(m => m.Value == _s.Reframe.Mode);
        set { _s.Reframe.Mode = value.Value; OnPropertyChanged(); }
    }

    public bool CaptionsEnabled { get => _s.Captions.Enabled; set { _s.Captions.Enabled = value; OnPropertyChanged(); } }
    public int CaptionSize { get => _s.Captions.FontSize; set { _s.Captions.FontSize = Math.Clamp(value, 30, 160); OnPropertyChanged(); } }
    public int WordsPerLine { get => _s.Captions.WordsPerLine; set { _s.Captions.WordsPerLine = Math.Clamp(value, 1, 8); OnPropertyChanged(); } }
    public string HighlightColor { get => _s.Captions.HighlightColor; set { _s.Captions.HighlightColor = value.Trim(); OnPropertyChanged(); } }
    public bool Uppercase { get => _s.Captions.Uppercase; set { _s.Captions.Uppercase = value; OnPropertyChanged(); } }
    public bool LogoEnabled { get => _s.Logo.Enabled; set { _s.Logo.Enabled = value; OnPropertyChanged(); } }
    public double MaxDeadStart { get => _s.Cut.MaxDeadStartSeconds; set { _s.Cut.MaxDeadStartSeconds = Math.Max(0, value); OnPropertyChanged(); } }
    public double MaxLength { get => _s.Cut.MaxLengthSeconds; set { _s.Cut.MaxLengthSeconds = Math.Clamp(value, 5, 600); OnPropertyChanged(); } }

    public string ToolStatus { get => _toolStatus; private set => Set(ref _toolStatus, value); }
    public bool FfmpegOk { get => _ffmpegOk; private set => Set(ref _ffmpegOk, value); }
    public bool YtDlpOk { get => _ytDlpOk; private set => Set(ref _ytDlpOk, value); }

    public void UpdateTools(ToolCheck t)
    {
        FfmpegOk = t.Ffmpeg;
        YtDlpOk = t.YtDlp;
        ToolStatus = $"ffmpeg: {(t.Ffmpeg ? "gefunden" : "fehlt")} · yt-dlp: {(t.YtDlp ? "gefunden" : "fehlt")} · Twitch: {(t.Twitch ? "eingerichtet" : "nicht eingerichtet")} · Claude: {(t.Claude ? "aktiv" : "aus (kein Schlüssel)")}";
    }

    private void Load()
    {
        _s = SettingsStore.Load(_main.SettingsPath);
        _claudeKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        foreach (var p in GetType().GetProperties())
        {
            OnPropertyChanged(p.Name);
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_s.OutputFolder))
        {
            _main.Dialogs.Info("Einstellungen", "Bitte einen Ausgabeordner wählen.");
            return;
        }

        try
        {
            SettingsStore.Save(_s, _main.SettingsPath);
            var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
            if (!string.Equals(oldKey, ClaudeKey, StringComparison.Ordinal))
            {
                // Saved for the Windows user, so UCut and the command line find it after a restart too.
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", ClaudeKey.Length == 0 ? null : ClaudeKey, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", ClaudeKey.Length == 0 ? null : ClaudeKey);
            }

            _main.ReloadEngine();
            _main.Twitch.Refresh();
            _main.Log.Write("Einstellungen gespeichert.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _main.Dialogs.Info("Einstellungen", $"Speichern fehlgeschlagen: {ex.Message}");
        }
    }

    private static string DeriveProbe(string ffmpeg)
    {
        if (string.Equals(ffmpeg, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "ffprobe";
        }

        var dir = Path.GetDirectoryName(ffmpeg);
        return string.IsNullOrEmpty(dir) ? "ffprobe" : Path.Combine(dir, "ffprobe" + Path.GetExtension(ffmpeg));
    }
}
