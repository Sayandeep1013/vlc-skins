using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32;

namespace RedesignPlayer.Core;

/// <summary>
/// Adds "VLC Redesign" to Explorer's right-click "Open with" list for media files. Per-user
/// (HKCU) only, no admin rights, and it never takes over the default app for any file type.
/// </summary>
public static class ShellIntegration
{
    const string ProgId = "VLCRedesign.Media";
    // Keyed by the exe's file name, whatever the download was renamed to.
    static string AppKey => $@"Software\Classes\Applications\{Path.GetFileName(Exe)}";

    public static readonly string[] Extensions =
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".ogv", ".vob",
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".m3u", ".m3u8", ".pls", ".xspf",
    };

    static string Exe => Environment.ProcessPath!;

    /// <summary>
    /// True when running as an MSIX/Store package. Packages declare their file types in the
    /// manifest instead (and their registry writes are virtualized), so registration is skipped.
    /// </summary>
    public static bool IsPackaged
    {
        get
        {
            try { uint len = 0; return GetCurrentPackageFullName(ref len, null) != 15700 /* APPMODEL_ERROR_NO_PACKAGE */; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref uint length, System.Text.StringBuilder? name);

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return k?.GetValue("") is string cmd && cmd.Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        string command = $"\"{Exe}\" \"%1\"";
        using (var p = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            p.SetValue("", "Media file");
            p.SetValue("FriendlyTypeName", "Media file");
            using (var icon = p.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
            using (var open = p.CreateSubKey(@"shell\open"))
            {
                open.SetValue("FriendlyAppName", "VLC Redesign");
                using var cmd = open.CreateSubKey("command");
                cmd.SetValue("", command);
            }
        }
        using (var a = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            a.SetValue("FriendlyAppName", "VLC Redesign");
            using (var icon = a.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
            using (var cmd = a.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            using var types = a.CreateSubKey("SupportedTypes");
            foreach (var ext in Extensions) types.SetValue(ext, "");
        }
        foreach (var ext in Extensions)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        Notify();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
        foreach (var ext in Extensions)
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            k?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        Notify();
    }

    // Tell Explorer the associations changed so the menu updates without a sign-out.
    static void Notify() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
