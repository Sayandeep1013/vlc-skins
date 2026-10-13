using System.Runtime.InteropServices;
using System.Windows.Input;

namespace RedesignPlayer.Core;

/// <summary>
/// Matches WPF key presses against VLC hotkey strings such as "Space", "Ctrl+o",
/// "Shift+b", "Alt+Right", "[" or "Media Play Pause".
/// </summary>
public sealed class HotKeys
{
    readonly Dictionary<string, string> _keyToAction = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _actionToKeys;

    public HotKeys(Dictionary<string, List<string>> bindings)
    {
        _actionToKeys = bindings;
        foreach (var (action, keys) in bindings)
            foreach (var k in keys)
                _keyToAction.TryAdd(Normalize(k), action);
    }

    /// <summary>Display label of the first binding for an action ("" if unbound).</summary>
    public string Label(string action) =>
        _actionToKeys.TryGetValue(action, out var keys) && keys.Count > 0 ? Pretty(keys[0]) : "";

    public IEnumerable<(string Action, string Keys)> All() =>
        _actionToKeys.OrderBy(p => p.Key).Select(p => (p.Key, string.Join("  ", p.Value.Select(Pretty))));

    public string? Match(KeyEventArgs e, out string pressed)
    {
        pressed = "";
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = KeyName(key);
        if (name == null) return null;
        var mods = Keyboard.Modifiers;
        // Shifted characters like "+" or "?" are bound without "Shift+" in VLC.
        bool charKey = name.Length == 1 && !char.IsLetterOrDigit(name[0]);
        pressed = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
                  (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
                  (mods.HasFlag(ModifierKeys.Shift) && !charKey ? "Shift+" : "") + name;
        if (_keyToAction.TryGetValue(Normalize(pressed), out var action)) return action;
        // e.g. Shift+= typed as "+" on US layouts
        if (mods.HasFlag(ModifierKeys.Shift) && charKey)
        {
            var shifted = ShiftedChar(key);
            if (shifted != null && _keyToAction.TryGetValue(Normalize(shifted), out action)) { pressed = shifted; return action; }
        }
        return null;
    }

    // Modifiers in a fixed order, key name lower-case.
    static string Normalize(string s)
    {
        var parts = s.Split('+');
        // "+" itself, possibly with modifiers: "Ctrl++"
        string key = s.EndsWith("++") || s == "+" ? "+" : parts[^1];
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Take(parts.Length - (key == "+" && s != "+" ? 2 : 1)))
            if (p.Length > 0) mods.Add(p.ToLowerInvariant());
        var order = new[] { "ctrl", "alt", "shift", "meta", "command" };
        return string.Concat(order.Where(mods.Contains).Select(m => m + "+")) + key.ToLowerInvariant();
    }

    static string Pretty(string k) =>
        k == "+" ? "+" : k.EndsWith("++") ? k[..^1] + "Plus"
            : string.Join("+", k.Split('+').Select(p => p.Length == 1 ? p.ToUpperInvariant() : p));

    static string? KeyName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return ((char)('a' + (k - Key.A))).ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        if (k >= Key.F1 && k <= Key.F12) return "F" + (k - Key.F1 + 1);
        switch (k)
        {
            case Key.Space: return "Space";
            case Key.Left: return "Left";
            case Key.Right: return "Right";
            case Key.Up: return "Up";
            case Key.Down: return "Down";
            case Key.Enter: return "Enter";
            case Key.Escape: return "Esc";
            case Key.Tab: return "Tab";
            case Key.Back: return "Backspace";
            case Key.Delete: return "Delete";
            case Key.Insert: return "Insert";
            case Key.Home: return "Home";
            case Key.End: return "End";
            case Key.PageUp: return "Page Up";
            case Key.PageDown: return "Page Down";
            case Key.Add: return "+";
            case Key.Subtract: return "-";
            case Key.Multiply: return "*";
            case Key.Divide: return "/";
            case Key.MediaPlayPause: return "Media Play Pause";
            case Key.MediaNextTrack: return "Media Next Track";
            case Key.MediaPreviousTrack: return "Media Prev Track";
            case Key.MediaStop: return "Media Stop";
            case Key.VolumeUp: return "Volume Up";
            case Key.VolumeDown: return "Volume Down";
            case Key.VolumeMute: return "Volume Mute";
            case Key.BrowserBack: return "Browser Back";
            case Key.BrowserForward: return "Browser Forward";
            case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt:
            case Key.LeftShift: case Key.RightShift: case Key.LWin: case Key.RWin:
                return null;
        }
        // Punctuation: ask the keyboard layout which character the key produces.
        var ch = LayoutChar(k, shifted: false);
        return ch;
    }

    static string? ShiftedChar(Key k) => LayoutChar(k, shifted: true);

    static string? LayoutChar(Key k, bool shifted)
    {
        int vk = KeyInterop.VirtualKeyFromKey(k);
        var state = new byte[256];
        if (shifted) state[0x10] = 0x80;
        var buf = new System.Text.StringBuilder(4);
        int n = ToUnicode((uint)vk, MapVirtualKey((uint)vk, 0), state, buf, buf.Capacity, 4 /* don't change kbd state */);
        return n == 1 && !char.IsControl(buf[0]) ? buf[0].ToString() : null;
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ToUnicode(uint vk, uint scan, byte[] state, System.Text.StringBuilder buf, int cch, uint flags);
}
