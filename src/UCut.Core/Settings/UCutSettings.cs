using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCut.Settings;

/// <summary>All user settings of UCut. Stored as JSON (see <see cref="SettingsStore"/>).</summary>
public sealed class UCutSettings
{
    /// <summary>Folder where Aitum Vertical (or OBS) saves the vertical replays/clips.</summary>
    public string WatchFolder { get; set; } = "";

    /// <summary>Root folder for finished clips. Every clip lands in a subfolder named after the game.</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>Path to ffmpeg. "ffmpeg" means: take it from PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path to ffprobe. "ffprobe" means: take it from PATH.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    public WhisperSettings Whisper { get; set; } = new();
    public CaptionSettings Captions { get; set; } = new();
    public CutSettings Cut { get; set; } = new();
    public LogoSettings Logo { get; set; } = new();
    public ReframeSettings Reframe { get; set; } = new();
    public GameSettings Games { get; set; } = new();
    public WriterSettings Writer { get; set; } = new();
    public TwitchClipSettings TwitchClips { get; set; } = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public sealed class WhisperSettings
{
    /// <summary>Whisper model size: tiny, base, small, medium, largeV3Turbo. Larger = more accurate, slower.</summary>
    public string Model { get; set; } = "medium";

    /// <summary>Folder for downloaded Whisper models. Empty = %LOCALAPPDATA%\UCut\models.</summary>
    public string ModelFolder { get; set; } = "";

    /// <summary>Spoken language ("de", "en", ...) or "auto".</summary>
    public string Language { get; set; } = "de";
}

public sealed class CaptionSettings
{
    public bool Enabled { get; set; } = true;
    public string Font { get; set; } = "Arial Black";
    public int FontSize { get; set; } = 78;

    /// <summary>Words shown at the same time.</summary>
    public int WordsPerLine { get; set; } = 3;

    /// <summary>Highlight colour of the spoken word as #RRGGBB.</summary>
    public string HighlightColor { get; set; } = "#FFE500";

    /// <summary>Vertical position of the caption centre as a fraction of the video height (0 = top, 1 = bottom).</summary>
    public double VerticalPosition { get; set; } = 0.62;

    /// <summary>Write captions in capital letters.</summary>
    public bool Uppercase { get; set; } = true;
}

public sealed class CutSettings
{
    /// <summary>Cut away silence before the first spoken word when it is longer than this (seconds).</summary>
    public double MaxDeadStartSeconds { get; set; } = 0.8;

    /// <summary>Lead-in kept before the first word (seconds).</summary>
    public double LeadInSeconds { get; set; } = 0.4;

    /// <summary>Hard cap for the clip length (seconds). 0 = no cap.</summary>
    public double MaxLengthSeconds { get; set; } = 60;
}

public sealed class LogoSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>PNG with transparency. Empty = the bundled Uleimon logo next to ucut.exe.</summary>
    public string Path { get; set; } = "";

    /// <summary>Logo width as a fraction of the video width.</summary>
    public double WidthFraction { get; set; } = 0.33;

    /// <summary>
    /// Centre of the logo as fractions of the video size. Null = find the lower edge of the facecam automatically.
    /// For Ulei's Aitum layout the name plate sits at about x 0.49, y 0.312.
    /// </summary>
    public double? CenterX { get; set; } = 0.49;
    public double? CenterY { get; set; } = 0.312;

    /// <summary>
    /// Where the lower edge of the facecam (incl. name plate) is in Ulei's layout, as a fraction of the height.
    /// Every clip is checked against it: if the clip's cam edge is elsewhere, the logo moves to the real edge and nothing is painted over.
    /// </summary>
    public double ExpectedCamEdge { get; set; } = 0.331;

    public double CamEdgeTolerance { get; set; } = 0.03;

    /// <summary>
    /// Optional box (fractions of the video size) painted over before the logo is drawn,
    /// so an old name like KREATIVLOS cannot shine through.
    /// </summary>
    public CoverBox? Cover { get; set; } = new() { X = 0.363, Y = 0.295, Width = 0.255, Height = 0.032, Color = "#2B2122" };
}

/// <summary>What happens with clips that are not vertical (e.g. 16:9 clips you load into UCut yourself).</summary>
public sealed class ReframeSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// split = find the cam, put it on top and the game below; crop = fill the screen and cut the sides away;
    /// blur = whole picture with blurred background.
    /// </summary>
    public UCut.Editing.ReframeMode Mode { get; set; } = UCut.Editing.ReframeMode.Split;

    /// <summary>What split does when no cam is found: crop or blur.</summary>
    public UCut.Editing.ReframeMode FallbackMode { get; set; } = UCut.Editing.ReframeMode.Crop;

    /// <summary>
    /// Fixed cam position in your landscape layout (fractions of the picture). Null = find it automatically in every clip.
    /// Example bottom right: { "x": 0.74, "y": 0.72, "width": 0.25, "height": 0.26 }.
    /// </summary>
    public UCut.Editing.NormRect? CamRect { get; set; }

    /// <summary>The cam strip on top takes at most this share of the height.</summary>
    public double MaxCamHeight { get; set; } = 0.45;

    /// <summary>For crop and the game part of split: which part stays (0 = left, 0.5 = middle, 1 = right).</summary>
    public double FocusX { get; set; } = 0.5;

    public int TargetWidth { get; set; } = 1080;
    public int TargetHeight { get; set; } = 1920;
}

public sealed class CoverBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Color { get; set; } = "#000000";
}

public sealed class GameSettings
{
    /// <summary>Twitch login name of the channel whose category is used (e.g. "uleimon").</summary>
    public string TwitchChannel { get; set; } = "";

    /// <summary>Twitch app client id (dev.twitch.tv). Without id and secret the Twitch check is skipped.</summary>
    public string TwitchClientId { get; set; } = "";

    /// <summary>Twitch app client secret. Can also be set via the UCUT_TWITCH_SECRET environment variable.</summary>
    public string TwitchClientSecret { get; set; } = "";

    /// <summary>Running process (without .exe) to game name. Checked when Twitch gives no answer.</summary>
    public Dictionary<string, string> Processes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GTA5"] = "GTA V",
        ["GTA5_Enhanced"] = "GTA V",
        ["FiveM"] = "GTA V",
        ["FiveM_GTAProcess"] = "GTA V",
        ["javaw"] = "Minecraft",
        ["Minecraft.Windows"] = "Minecraft",
        ["VALORANT-Win64-Shipping"] = "Valorant",
        ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["cs2"] = "Counter-Strike 2",
        ["RocketLeague"] = "Rocket League",
    };

    /// <summary>Twitch category name to a shorter folder name.</summary>
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grand Theft Auto V"] = "GTA V",
        ["Just Chatting"] = "Just Chatting",
    };

    /// <summary>Hashtags per game folder name, used in titles and descriptions.</summary>
    public Dictionary<string, string[]> Hashtags { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GTA V"] = ["#gta5", "#gtarp", "#gtaonline"],
        ["Minecraft"] = ["#minecraft"],
    };

    /// <summary>Hashtags that go under every clip.</summary>
    public string[] CommonHashtags { get; set; } = ["#twitchclips", "#streamer", "#fyp"];

    /// <summary>Folder for clips whose game is unknown.</summary>
    public string UnknownFolder { get; set; } = "Unsortiert";
}

public sealed class WriterSettings
{
    /// <summary>Use Claude for funniness score, titles and hashtags (needs ANTHROPIC_API_KEY).</summary>
    public bool UseClaude { get; set; } = true;

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Channel name used in descriptions.</summary>
    public string ChannelName { get; set; } = "Uleimon";
}

/// <summary>Old Twitch clips of the channel: search, pre-filter for funny ones, download and process (ucut twitch).</summary>
public sealed class TwitchClipSettings
{
    /// <summary>Oldest clip date (yyyy-MM-dd).</summary>
    public string Since { get; set; } = "2020-01-01";

    /// <summary>Newest clip date (yyyy-MM-dd), empty = today.</summary>
    public string Until { get; set; } = "";

    /// <summary>Only load clips that look funny (title words or many views). false = load every clip.</summary>
    public bool OnlyFunny { get; set; } = true;

    /// <summary>Share of the most watched clips that always count as candidates (0.25 = top quarter).</summary>
    public double TopViewsShare { get; set; } = 0.25;

    /// <summary>Clips with fewer views are skipped.</summary>
    public int MinViews { get; set; }

    /// <summary>At most this many clips per run, best candidates first (0 = no limit).</summary>
    public int MaxClips { get; set; }

    /// <summary>Path to yt-dlp. "yt-dlp" means: take it from PATH.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Where the original downloads are kept. Empty = outputFolder\_Twitch-Originale.</summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>Words in a clip title that hint at a funny moment (case does not matter).</summary>
    public List<string> FunnyWords { get; set; } =
    [
        "lol", "xd", "haha", "hahaha", "lach", "lustig", "witzig", "fail", "wtf", "bruh", "rage", "ausraster", "eskal",
        "digga", "alter", "bro", "cringe", "peinlich", "troll", "prank", "unnötig", "random", "sus", "omg", "nein",
        "warum", "was zur", "😂", "🤣", "💀", "😭",
    ];
}
