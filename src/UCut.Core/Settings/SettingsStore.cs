using System.Text.Json;

namespace UCut.Settings;

/// <summary>Loads and saves <see cref="UCutSettings"/> as JSON.</summary>
public static class SettingsStore
{
    /// <summary>Default location: %APPDATA%\UCut\settings.json (Linux: ~/.config/UCut/settings.json).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCut", "settings.json");

    public static UCutSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Keine Einstellungen gefunden: {path}. Mit 'ucut init' wird eine Vorlage angelegt.", path);
        }

        var json = File.ReadAllText(path);
        UCutSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<UCutSettings>(json, UCutSettings.JsonOptions)
                       ?? throw new InvalidDataException($"Einstellungsdatei ist leer: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Einstellungsdatei ist kein gültiges JSON ({path}): {ex.Message}", ex);
        }

        // JSON loses the case-insensitive comparer; "gta5" and "GTA5" must both match.
        var g = settings.Games;
        g.Processes = new Dictionary<string, string>(g.Processes, StringComparer.OrdinalIgnoreCase);
        g.Aliases = new Dictionary<string, string>(g.Aliases, StringComparer.OrdinalIgnoreCase);
        g.Hashtags = new Dictionary<string, string[]>(g.Hashtags, StringComparer.OrdinalIgnoreCase);
        return settings;
    }

    public static void Save(UCutSettings settings, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(settings, UCutSettings.JsonOptions));
    }
}
