using System.IO;
using System.Text.Json;

namespace RedesignPlayer.Core;

/// <summary>The player's own state (window placement, volume). Never touches vlcrc.</summary>
public sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1120;
    public double Height { get; set; } = 680;
    public bool Maximized { get; set; }
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public string LastFolder { get; set; } = "";
    public bool Waveform { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public EffectsState Effects { get; set; } = new();

    // VLC_REDESIGN_SETTINGS points test runs at a scratch file so they never touch real settings.
    static string FilePath => Environment.GetEnvironmentVariable("VLC_REDESIGN_SETTINGS") is { Length: > 0 } custom ? custom : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VLC-Redesign", "player.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
