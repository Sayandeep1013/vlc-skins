// VLC Redesign auto-hide helper.
//
// VLC skins can't react to the mouse hovering, so this tiny background program does it:
// while the "VLC Redesign" skin is active and VLC is windowed, it hides the bars after
// the mouse has been idle (or left the window) and shows them again as soon as the
// mouse moves over the player.
//
// It switches layouts by posting double-clicks to two hotspots the skin provides
// (hot_hide at 75,5 in "main"; hot_show at the bottom-left of "clean"). Messages go
// straight to VLC's window; the real cursor is never moved.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class AutoHide
{
    const int HideAfterIdleMs = 2500;
    const int HideAfterLeaveMs = 700;
    const int TopBar = 36, BottomBar = 48;

    static StreamWriter log;
    static void Log(string m) { if (log != null) { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + m); log.Flush(); } }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--log")
            log = new StreamWriter(Path.Combine(Path.GetTempPath(), "vlc-redesign-autohide.log"), true);
        bool created;
        using (var mutex = new Mutex(true, "VLCRedesignAutoHide", out created))
        {
            if (!created) return; // already running
            Run();
        }
    }

    enum Bars { Shown, Hidden }

    static void Run()
    {
        string vlcrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");
        bool skinActive = false;
        DateTime nextSkinCheck = DateTime.MinValue;
        IntPtr lastPlayer = IntPtr.Zero;
        Bars bars = Bars.Shown;
        POINT lastPt = new POINT();
        int lastActivity = Environment.TickCount;
        int leftAt = -1;
        int lastHideSent = 0;

        while (true)
        {
            if (DateTime.UtcNow >= nextSkinCheck)
            {
                skinActive = IsOurSkin(vlcrc);
                nextSkinCheck = DateTime.UtcNow.AddSeconds(3);
                Log("skinActive=" + skinActive);
            }

            IntPtr player = skinActive ? FindPlayer() : IntPtr.Zero;
            if (player == IntPtr.Zero || IsIconic(player) || VlcIsFullscreen())
            {
                if (lastPlayer != IntPtr.Zero || player != IntPtr.Zero) Log("skip player=" + player + " fs=" + (player != IntPtr.Zero && VlcIsFullscreen()));
                lastPlayer = IntPtr.Zero;
                Thread.Sleep(player == IntPtr.Zero ? 700 : 150);
                continue;
            }
            if (player != lastPlayer)
            {
                // New VLC window (or restarted VLC): its layout starts with bars shown.
                lastPlayer = player;
                Log("player " + player);
                bars = Bars.Shown;
                lastActivity = Environment.TickCount;
                leftAt = -1;
            }

            // Work in VLC's own DPI coordinate space so client coordinates line up.
            SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(player));

            POINT pt;
            GetCursorPos(out pt);
            bool moved = pt.X != lastPt.X || pt.Y != lastPt.Y;
            lastPt = pt;

            IntPtr under = GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
            bool over = under == player;
            RECT cr; GetClientRect(player, out cr);
            POINT cp = pt; ScreenToClient(player, ref cp);
            int now = Environment.TickCount;

            if (over)
            {
                leftAt = -1;
                if (moved)
                {
                    lastActivity = now;
                    if (bars == Bars.Hidden)
                    {
                        Click(player, 5, cr.Bottom - 6); // hot_show
                        Log("show at 5," + (cr.Bottom - 6));
                        bars = Bars.Shown;
                    }
                }
                bool onBars = bars == Bars.Shown && (cp.Y < TopBar || cp.Y > cr.Bottom - BottomBar);
                if (onBars) lastActivity = now;
            }
            else if (leftAt < 0)
            {
                leftAt = now;
            }

            bool idle = now - lastActivity > HideAfterIdleMs;
            bool gone = !over && leftAt >= 0 && now - leftAt > HideAfterLeaveMs;
            bool buttonDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if ((idle || gone) && !buttonDown)
            {
                // hot_hide only exists in the main layout while playing, so a hide can be a
                // no-op (e.g. sent before playback started). Re-send it every so often while
                // idle; when the bars are already hidden it lands on the hot_noop spot.
                if (bars == Bars.Shown || now - lastHideSent > 1500)
                {
                    Click(player, 75, 5);
                    lastHideSent = now;
                    Log("hide over=" + over + " idle=" + idle + " gone=" + gone + " client=" + cr.Right + "x" + cr.Bottom);
                }
                bars = Bars.Hidden;
            }

            Thread.Sleep(60);
        }
    }

    static void Click(IntPtr hwnd, int x, int y)
    {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessage(hwnd, 0x0200 /* WM_MOUSEMOVE */, IntPtr.Zero, lp);
        // skins2 images run their command on double-click only.
        PostMessage(hwnd, 0x0203 /* WM_LBUTTONDBLCLK */, (IntPtr)1, lp);
    }

    static bool IsOurSkin(string vlcrc)
    {
        try
        {
            using (var fs = new FileStream(vlcrc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
            {
                var m = Regex.Match(r.ReadToEnd(), @"^skins2-last=(.*)$", RegexOptions.Multiline);
                return m.Success && m.Groups[1].Value.IndexOf("VLC-Redesign", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }

    static HashSet<uint> VlcPids()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("vlc")) { pids.Add((uint)p.Id); p.Dispose(); }
        return pids;
    }

    // Largest visible skins2 top-level window of any vlc.exe = the player.
    static IntPtr FindPlayer()
    {
        var pids = VlcPids();
        if (pids.Count == 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        var cls = new StringBuilder(64);
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h) || IsIconic(h)) return true;
            cls.Clear(); GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "SkinWindowClass") return true;
            RECT r; GetWindowRect(h, out r);
            long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // In fullscreen the skin's own fullscreen controller does the hiding.
    static bool VlcIsFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint pid; GetWindowThreadProcessId(fg, out pid);
        if (!VlcPids().Contains(pid)) return false;
        RECT r; GetWindowRect(fg, out r);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
