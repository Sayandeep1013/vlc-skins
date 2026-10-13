using System.IO;
using System.Runtime.InteropServices;

namespace RedesignPlayer.Core;

/// <summary>
/// Access to libVLC's object variables, the same mechanism VLC's own "Effects and Filters"
/// window uses (audio filters live on the audio output, video filters on the video outputs).
/// libVLC's public API doesn't expose these, so this calls libvlccore directly. Layouts follow
/// VLC 3.0's headers: a libvlc_media_player_t *is* a vlc_object_t, every object starts with a
/// pointer to its type name, and vlc_list_t is { int type; int count; vlc_value_t *values }.
/// </summary>
public static class VlcVars
{
    public const int Bool = 0x20, Int = 0x30, String = 0x40, Float = 0x50;
    const int DoInherit = 0x8000;

    static string? _dir;

    /// <summary>Point the libvlccore import at the VLC install (call once at startup).</summary>
    public static void Init(string vlcDir)
    {
        if (_dir != null) return;
        _dir = vlcDir;
        NativeLibrary.SetDllImportResolver(typeof(VlcVars).Assembly, (name, asm, path) =>
            name == "libvlccore" ? NativeLibrary.Load(Path.Combine(_dir, "libvlccore.dll")) : IntPtr.Zero);
    }

    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    static extern int var_Create(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type);
    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    static extern int var_Type(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    static extern int var_SetChecked(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, long value);
    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    static extern int var_GetChecked(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, out long value);
    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr vlc_list_children(IntPtr obj);
    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    static extern void vlc_list_release(IntPtr list);

    public const string AudioOutput = "audio output", VideoOutput = "video output", Input = "input";

    /// <summary>
    /// Runs <paramref name="act"/> on every object of the given type in the player's object
    /// tree. The objects are only valid during the call (the child list holds references).
    /// </summary>
    public static int ForEach(IntPtr player, string type, Action<IntPtr> act) => Walk(player, type, act, 0);

    static int Walk(IntPtr obj, string type, Action<IntPtr> act, int depth)
    {
        if (obj == IntPtr.Zero || depth > 3) return 0;
        IntPtr list = vlc_list_children(obj);
        if (list == IntPtr.Zero) return 0;
        int n = 0;
        try
        {
            int count = Marshal.ReadInt32(list, 4);
            IntPtr values = Marshal.ReadIntPtr(list, 8);
            if (count < 0 || count > 256 || values == IntPtr.Zero) return 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr child = Marshal.ReadIntPtr(values, i * 8);
                if (child == IntPtr.Zero) continue;
                IntPtr typeName = Marshal.ReadIntPtr(child); // vlc_common_members.object_type
                if (typeName != IntPtr.Zero && Marshal.PtrToStringUTF8(typeName) == type) { act(child); n++; }
                n += Walk(child, type, act, depth + 1);
            }
        }
        finally { vlc_list_release(list); }
        return n;
    }

    public static bool Exists(IntPtr obj, string name) => var_Type(obj, name) != 0;

    /// <summary>Sets a variable, creating it first if the object doesn't have it yet.</summary>
    public static bool Set(IntPtr obj, string name, int type, object value)
    {
        if (var_Type(obj, name) == 0 && var_Create(obj, name, type | DoInherit) != 0) return false;
        // A variable keeps the type it was created with; convert to it.
        int actual = var_Type(obj, name) & 0xF0;
        if (actual != 0) type = actual;
        switch (type)
        {
            case String:
            {
                IntPtr s = Marshal.StringToCoTaskMemUTF8(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                try { return var_SetChecked(obj, name, String, (long)s) == 0; }
                finally { Marshal.FreeCoTaskMem(s); } // VLC copies strings
            }
            case Float:
                return var_SetChecked(obj, name, Float, BitConverter.SingleToInt32Bits(Convert.ToSingle(value)) & 0xFFFFFFFFL) == 0;
            case Bool:
                return var_SetChecked(obj, name, Bool, Convert.ToBoolean(value) ? 1 : 0) == 0;
            default:
                return var_SetChecked(obj, name, Int, Convert.ToInt64(value)) == 0;
        }
    }

    public static float? GetFloat(IntPtr obj, string name) =>
        (var_Type(obj, name) & 0xF0) == Float && var_GetChecked(obj, name, Float, out long v) == 0 ? BitConverter.Int32BitsToSingle((int)v) : null;

    public static long? GetInt(IntPtr obj, string name) =>
        (var_Type(obj, name) & 0xF0) is Int or Bool && var_GetChecked(obj, name, var_Type(obj, name) & 0xF0, out long v) == 0 ? (var_Type(obj, name) & 0xF0) == Bool ? v & 0xFF : v : null;

    /// <summary>Reads a string variable. The copy VLC returns is left to VLC's allocator (tiny leak, debug use only).</summary>
    public static string? GetStringDebug(IntPtr obj, string name)
    {
        if ((var_Type(obj, name) & 0xF0) != String || var_GetChecked(obj, name, String, out long v) != 0 || v == 0) return null;
        return Marshal.PtrToStringUTF8((IntPtr)v);
    }
}
