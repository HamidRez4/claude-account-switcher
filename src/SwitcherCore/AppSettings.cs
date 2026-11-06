using System.Text.Json;

namespace SwitcherCore;

/// <summary>The app's own state, kept in <c>%APPDATA%\ClaudeAccountSwitcher\settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>Labels/emails the user typed in by hand. Always win.</summary>
    public Dictionary<string, AccountIdentity> Manual { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Identities discovered on earlier runs, so an email stays known after Claude's caches are cleared.</summary>
    public Dictionary<string, AccountIdentity> Discovered { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SessionsRoot { get; set; }
    public string? LastSourceAccount { get; set; }
    public string? LastTargetSpace { get; set; }
    public bool ShowArchived { get; set; } = true;

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeAccountSwitcher");

    public static string BackupsDir => Path.Combine(AppDataDir, "backups");

    static string FilePath => Path.Combine(AppDataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {
                    s.Manual = new(s.Manual ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.Discovered = new(s.Discovered ?? new(), StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
