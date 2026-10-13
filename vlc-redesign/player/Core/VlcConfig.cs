using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RedesignPlayer.Core;

/// <summary>
/// Read-only view of the user's VLC install and vlcrc. VLC writes every option to vlcrc,
/// commented out ("#key=default") when unchanged and uncommented when the user changed it,
/// so the file gives us both the defaults and the user's overrides.
/// </summary>
public sealed class VlcConfig
{
    public string InstallDir { get; }
    public string ConfigPath { get; }
    readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    readonly HashSet<string> _overridden = new(StringComparer.Ordinal);

    VlcConfig(string installDir, string configPath)
    {
        InstallDir = installDir;
        ConfigPath = configPath;
        if (!File.Exists(configPath)) return;

        var line = new Regex(@"^(#?)([\w+-]+)=(.*)$"); // "+" appears in e.g. key-jump+short
        foreach (var raw in File.ReadLines(configPath))
        {
            var m = line.Match(raw);
            if (!m.Success) continue;
            var key = m.Groups[2].Value;
            bool commented = m.Groups[1].Value == "#";
            // Uncommented (user) values win over commented defaults.
            if (commented && _overridden.Contains(key)) continue;
            _values[key] = m.Groups[3].Value;
            if (!commented) _overridden.Add(key);
        }
    }

    public sealed class VlcMissingException() : Exception(
        "VLC Redesign plays media with VLC media player's engine, but VLC isn't installed (or isn't the 64-bit version 3).");

    public static VlcConfig Load()
    {
        var dir = FindInstallDir() ?? throw new VlcMissingException();
        var rc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");
        return new VlcConfig(dir, rc);
    }

    static string? FindInstallDir()
    {
        static bool Ok(string? d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "libvlc.dll"));
        var env = Environment.GetEnvironmentVariable("VLC_DIR");
        if (Ok(env)) return env;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            var d = hk?.GetValue("InstallDir") as string;
            if (Ok(d)) return d;
        }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC");
        return Ok(pf) ? pf : null;
    }

    public bool IsUserSet(string key) => _overridden.Contains(key);

    public string Get(string key, string fallback = "") =>
        _values.TryGetValue(key, out var v) ? v : fallback;

    public double GetDouble(string key, double fallback) =>
        double.TryParse(Get(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>All hotkey bindings: action name (without "key-") -> list of key strings.</summary>
    public Dictionary<string, List<string>> HotKeys()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (k, v) in _values)
        {
            if (!k.StartsWith("key-", StringComparison.Ordinal) || k.StartsWith("key-mouse", StringComparison.Ordinal)) continue;
            // VLC separates multiple bindings for one action with tabs.
            var keys = v.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (keys.Count > 0) map[k[4..]] = keys;
        }
        return map;
    }
}
