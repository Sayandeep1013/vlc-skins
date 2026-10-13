using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using RedesignPlayer.Core;
using RedesignPlayer.UI;

namespace RedesignPlayer;

public partial class MainWindow : Window
{
    readonly VlcConfig _cfg;
    readonly HotKeys _keys;
    readonly Player _player;
    readonly AppSettings _settings;
    readonly string[] _startupArgs;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    IntPtr _hwnd;
    Window? _overlayWindow;
    bool _chromeShown = true;
    POINT _lastCursor = new() { X = int.MinValue };
    bool _fullscreen;
    Rect _restoreBounds;
    WindowState _restoreState;
    bool _updatingUi;
    int _volume;

    static readonly string[] MediaExts = { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".3gp", ".ogv", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".m3u", ".m3u8", ".pls", ".xspf", ".iso", ".vob" };
    static readonly string[] SubExts = { ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".smi", ".txt" };
    static readonly float[] CoarseRates = { 0.25f, 1f / 3, 0.5f, 2f / 3, 1f, 1.5f, 2f, 3f, 4f };
    static readonly (string Label, string? Value)[] Aspects =
        { ("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("2.35:1", "2.35:1"), ("16:10", "16:10"), ("1:1", "1:1"), ("5:4", "5:4") };

    public MainWindow(string[] args)
    {
        _startupArgs = args;
        _cfg = VlcConfig.Load();
        _keys = new HotKeys(_cfg.HotKeys());
        _settings = AppSettings.Load();
        InitializeComponent();
        _player = new Player(_cfg, Dispatcher);

        RestorePlacement();
        _volume = _settings.Volume;
        Volume.Max = Math.Max(100, _cfg.GetDouble("qt-max-volume", 125));
        Volume.Value = _volume;
        Volume.Changed += v => SetVolume((int)v, osd: false);
        Seek.Seek += OnSeek;
        Seek.Preview = SeekPreviewAt;
        _thumbDwell.Tick += (_, _) => { _thumbDwell.Stop(); _thumbs?.Request(_thumbWant); };
        QueueList.ItemsSource = _player.Queue;
        _player.Queue.CollectionChanged += (_, e) =>
        {
            // Durations are filled in asynchronously; keep the header total current.
            foreach (QueueItem q in e.NewItems ?? Array.Empty<QueueItem>())
                q.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(QueueItem.DurationText)) UpdateQueueHeader(); };
            UpdateQueueHeader();
        };
        _player.MediaChanged += OnMediaChanged;
        _player.StateChanged += UpdateUi;
        _player.Mp.Playing += (_, _) => Dispatcher.BeginInvoke(() => { ApplyVolume(); LiftCurtainSoon(); });
        // A new video output window appears per video; darken it as soon as it exists.
        _player.Mp.Vout += (_, _) => Dispatcher.BeginInvoke(DarkenVideoWindows);
        _player.Mp.ESAdded += (_, _) => Dispatcher.BeginInvoke(() => { if (Side.Visibility == Visibility.Visible) RefreshSidebar(); });

        _tick.Tick += (_, _) => { UpdateUi(); DarkenVideoWindows(); };
        _idle.Tick += (_, _) => { _idle.Stop(); if (CanAutoHide()) HideChrome(); };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); Fade(Osd, 0, 400); };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += (_, _) => SaveAndDispose();
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => UpdateWindowButtons();
        Overlay.Loaded += (_, _) => HookOverlayWindow();
        Overlay.SizeChanged += (_, _) => { FitDock(); FitCover(); };
        UpdateTooltips();
        UpdateQueueHeader();
    }

    // ------------------------------------------------------------------ window

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_hwnd)!;
        src.AddHook(WndProc);
        // WPF clears a new window to white before its first frame; make that clear colour dark.
        src.CompositionTarget.BackgroundColor = Colors.Black;
        // Windows 11: real, anti-aliased rounded corners and a dark frame.
        SetCorners(round: true);
        int dark = 1;
        DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        // The default Win11 frame border is a light 1px line; match it to the bars instead.
        int border = 0x00181818; // COLORREF
        DwmSetWindowAttribute(_hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
    }

    void SetCorners(bool round)
    {
        int pref = round ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkenVideoWindows();
        Video.MediaPlayer = _player.Mp;
        var files = _startupArgs.Where(a => !a.StartsWith("--")).ToList();
        if (files.Count > 0) _player.ReplaceQueue(files);
        _tick.Start();
        UpdateUi();
        StartDebugBridge();
    }

    // ------------------------------------------------------------------ debug bridge
    // With VLC_REDESIGN_DEBUG=<file>, commands written to that file are executed (one per
    // line), so UI states can be inspected without simulating the real mouse/keyboard.
    readonly DispatcherTimer _dbgTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    bool _pinChrome;
    readonly List<string> _dbgLog = new();

    void StartDebugBridge()
    {
        var file = Environment.GetEnvironmentVariable("VLC_REDESIGN_DEBUG");
        if (string.IsNullOrEmpty(file)) return;
        // Polling (not a FileSystemWatcher): writers drop the file in atomically and we consume it.
        _dbgTimer.Tick += (_, _) =>
        {
            if (!File.Exists(file)) return;
            string[] lines;
            try { lines = File.ReadAllLines(file); File.Delete(file); } catch { return; }
            foreach (var l in lines.Where(l => l.Trim().Length > 0)) DebugCommand(l.Trim());
        };
        _dbgTimer.Start();
    }

    static void RenderPng(FrameworkElement el, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(el);
        int w = (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    string MenuScreenPos(ContextMenu m)
    {
        try
        {
            var p = m.PointToScreen(new Point(0, 0));
            var o = Overlay.PointToScreen(new Point(0, 0));
            return $"{p.X - o.X:0} {p.Y - o.Y:0}";
        }
        catch { return "0 0"; }
    }

    void DebugCommand(string line)
    {
        var parts = line.Split(' ', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "pin": _pinChrome = true; ShowChrome(); break;
            case "unpin": _pinChrome = false; break;
            case "hide": _pinChrome = false; HideChrome(); break;
            case "menu": ShowMenu(arg == "ctx" ? BuildContextMenu() : BuildMainMenu(), arg == "ctx" ? null : MoreButton); break;
            case "audio": AudioMenu_Click(this, new RoutedEventArgs()); break;
            case "subs": SubsMenu_Click(this, new RoutedEventArgs()); break;
            case "closemenu": if (_openMenu != null) _openMenu.IsOpen = false; break;
            case "side": OpenSide(arg switch { "queue" => QueuePane, "keys" => KeysPane, _ => SettingsPane }); break;
            case "tab": (arg switch { "subs" => TabSubs, "audio" => TabAudio, "video" => TabVideo, _ => TabPlayback }).IsChecked = true; break;
            case "closeside": CloseSide(); break;
            case "do": Do(arg, "Test"); break;
            case "url": ShowUrlBox(); break;
            case "closeurl": UrlBox.Visibility = Visibility.Collapsed; break;
            case "fs": ToggleFullscreen(); break;
            case "max": ToggleMaximize(); break;
            case "seek": _player.Mp.Position = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
            case "pause": _player.Mp.SetPause(true); break;
            case "stop": _player.Stop(); break;
            case "enqueue": _player.Enqueue(arg.Split('|'), playFirst: false); break;
            case "render":
                // Exact overlay (and open menu) pixels, for composing with a capture of the video.
                RenderPng(Overlay, arg + "_overlay.png");
                if (_openMenu is { IsOpen: true } om) RenderPng(om, arg + "_menu.png");
                if (Seek.OpenCard is { } card) RenderPng(card, arg + "_card.png");
                if (_openMenu != null) File.WriteAllText(arg + "_menu.txt", MenuScreenPos(_openMenu));
                break;
            case "hover": Seek.DebugHover(arg == "off" ? null : double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "key":
                // "key Right": runs a key press through the same handler as real keyboard input.
                var kk = (Key)Enum.Parse(typeof(Key), arg);
                OnPreviewKeyDown(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, kk) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                break;
            case "filmstrip":
                // "filmstrip <base>": renders the overlay every 60ms (8 frames) to check animations.
                int n = 0;
                var ft = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                ft.Tick += (_, _) =>
                {
                    RenderPng(Overlay, $"{arg}_{n}.png");
                    File.AppendAllText(arg + "_values.txt", $"{n} {DateTime.Now:ss.fff} op={Chrome.Opacity:0.000} top={TopShift.Y:0.0} bottom={BottomShift.Y:0.0}{Environment.NewLine}");
                    if (++n == 8) ft.Stop();
                };
                ft.Start();
                break;
            case "dump":
                var mp = _player.Mp;
                File.WriteAllText(Environment.GetEnvironmentVariable("VLC_REDESIGN_DEBUG") + ".out", string.Join(Environment.NewLine, new[]
                {
                    $"cmdlog={string.Join(",", _dbgLog)}",
                    $"chromeShown={_chromeShown} opacity={Chrome.Opacity:0.00} hitTest={Chrome.IsHitTestVisible} pinned={_pinChrome}",
                    $"state={mp.State} playing={mp.IsPlaying} time={mp.Time} len={mp.Length} rate={mp.Rate}",
                    $"volume field={_volume} mp={mp.Volume} cone={Volume.Value}/{Volume.Max} mute={mp.Mute}",
                    $"window={Left:0},{Top:0} {ActualWidth:0}x{ActualHeight:0} state={WindowState} fs={_fullscreen} snapped={IsSnapped()} square={_squareCorners}",
                    $"overlayWindow={_overlayWindow?.GetType().Name} at {_overlayWindow?.Left:0},{_overlayWindow?.Top:0} {_overlayWindow?.Width:0}x{_overlayWindow?.Height:0} actual {_overlayWindow?.ActualWidth:0}x{_overlayWindow?.ActualHeight:0} side={Side.Visibility} menu={_openMenu?.IsOpen}",
                    $"overlayGrid={Overlay.ActualWidth:0}x{Overlay.ActualHeight:0} chrome={Chrome.ActualWidth:0}x{Chrome.ActualHeight:0} video={Video.ActualWidth:0}x{Video.ActualHeight:0} main={ActualWidth:0}x{ActualHeight:0}",
                    $"card={(Seek.OpenCard is { } oc ? $"{oc.PointToScreen(new Point(0, 0)).X:0}..{oc.PointToScreen(new Point(oc.ActualWidth, 0)).X:0} w={oc.ActualWidth:0}" : "closed")} seek0={Seek.PointToScreen(new Point(0, 0)).X:0} seekW={Seek.ActualWidth:0} handle={Seek.PointToScreen(new Point(6 + Seek.Value * (Seek.ActualWidth - 12), 0)).X:0}",
                    $"thumbs={_thumbs?.Count} {_thumbs?.PixelWidth}x{_thumbs?.PixelHeight} chapters={_chapters.Length}",
                    $"spu={mp.Spu} spuCount={mp.SpuCount} audio={mp.AudioTrack} audioCount={mp.AudioTrackCount} dev={mp.OutputDevice}",
                }));
                break;
        }
        _dbgLog.Add(parts[0]);
    }

    // LibVLCSharp hosts the video in a plain Win32 "Static" control. Statics paint the system
    // face colour (light grey) and, without WS_CLIPCHILDREN, paint over libvlc's (black) video
    // window wherever the picture isn't -- that was the white bars. So: clip children, and answer
    // the Static's WM_CTLCOLORSTATIC (sent to us, its parent) with a dark brush (see WndProc).
    // Pure black, the same as libvlc's own video window and letterboxing, so the two never show as
    // slightly different strips (e.g. above a widescreen movie after a resize while paused).
    static readonly IntPtr DarkBrush = CreateSolidBrush(0x00000000); // COLORREF 0x00BBGGRR

    void DarkenVideoWindows()
    {
        if (_hwnd == IntPtr.Zero) return;
        EnumChildWindows(_hwnd, (h, _) =>
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "Static")
            {
                long style = (long)GetWindowLongPtr(h, -16 /* GWL_STYLE */);
                if ((style & 0x02000000 /* WS_CLIPCHILDREN */) == 0)
                {
                    SetWindowLongPtr(h, -16, (IntPtr)(style | 0x02000000));
                    InvalidateRect(h, IntPtr.Zero, true);
                }
            }
            return true;
        }, IntPtr.Zero);
    }

    void HookOverlayWindow()
    {
        _overlayWindow = Window.GetWindow(Overlay);
        if (_overlayWindow == null || _overlayWindow == this) return;
        _overlayWindow.PreviewKeyDown += OnPreviewKeyDown;
    }

    void RestorePlacement()
    {
        Width = _settings.Width;
        Height = _settings.Height;
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) && IsOnScreen(_settings.Left, _settings.Top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    static bool IsOnScreen(double x, double y) =>
        x >= SystemParameters.VirtualScreenLeft - 50 && y >= SystemParameters.VirtualScreenTop - 50 &&
        x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    void SaveAndDispose()
    {
        var b = _fullscreen ? _restoreBounds : RestoreBounds;
        if (!b.IsEmpty) { _settings.Left = b.Left; _settings.Top = b.Top; _settings.Width = b.Width; _settings.Height = b.Height; }
        _settings.Maximized = (_fullscreen ? _restoreState : WindowState) == WindowState.Maximized;
        _settings.Volume = _volume;
        _settings.Save();
        _tick.Stop();
        DropThumbnails();
        _player.Dispose();
    }

    void UpdateWindowButtons()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxIcon.Data = (Geometry)FindResource(max ? "Icon.Restore" : "Icon.Maximize");
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
        FsIcon.Data = (Geometry)FindResource(_fullscreen ? "Icon.ExitFullscreen" : "Icon.Fullscreen");
        FsButton.ToolTip = "Fullscreen" + KeyHint(_fullscreen ? "leave-fullscreen" : "toggle-fullscreen");
        DockExitFs.ToolTip = "Exit fullscreen" + KeyHint("leave-fullscreen");
        ApplyBarLayout();
    }

    // Windowed: full-width bars glued to the edges. Fullscreen: no top bar, and the controls
    // float in a centred pill above the bottom edge (the redesign skin's fullscreen controller).
    void ApplyBarLayout()
    {
        // The bars live in a separate overlay window that Windows doesn't round, so their corners
        // must follow the main window's: Windows squares the corners of maximized and snapped
        // windows, and a rounded bar over a square window leaves a black notch in each corner.
        bool square = WindowState == WindowState.Maximized || IsSnapped();
        _squareCorners = square;
        TopBar.CornerRadius = square ? new CornerRadius(0) : new CornerRadius(8, 8, 0, 0);
        BottomBar.CornerRadius = square ? new CornerRadius(0) : new CornerRadius(0, 0, 8, 8);
        // Fullscreen has no top bar at all: just the video and the floating controller.
        TopBar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Seek.PreviewAbove = _fullscreen ? Dock : BottomBar;
        if (_fullscreen == (Dock.Visibility == Visibility.Visible)) return;
        if (_fullscreen)
        {
            MoveTo(DockSeekRow, TimeText, DurationText, Seek);
            MoveTo(DockCenter, PrevButton, PlayButton, NextButton);
            MoveTo(DockRight, MuteButton, Volume, AudioButton, SubsButton, GearButton, QueueButton, DockExitFs);
            BottomBar.Visibility = Visibility.Collapsed;
            Dock.RenderTransform = BottomShift;
            Dock.Visibility = Visibility.Visible;
        }
        else
        {
            MoveTo(BarLeft, PrevButton, PlayButton, NextButton, TimeText);
            MoveTo(BarGrid, Seek);
            MoveTo(BarRight, DurationText, MuteButton, Volume, AudioButton, SubsButton, GearButton, QueueButton);
            Dock.Visibility = Visibility.Collapsed;
            BottomBar.Visibility = Visibility.Visible;
        }
        // In the dock the times hug the ends of the seek bar.
        TimeText.TextAlignment = _fullscreen ? TextAlignment.Left : TextAlignment.Center;
        DurationText.TextAlignment = _fullscreen ? TextAlignment.Right : TextAlignment.Center;
        TimeText.Width = DurationText.Width = _fullscreen ? 66 : 74;
        TimeText.Margin = _fullscreen ? new Thickness(0) : new Thickness(6, 0, 8, 0);
        PlayButton.Margin = _fullscreen ? new Thickness(10, 0, 10, 0) : new Thickness(4, 0, 4, 0);
        Volume.Margin = _fullscreen ? new Thickness(2, 0, 10, 0) : new Thickness(2, 0, 12, 0);
        FitDock();
    }

    static void MoveTo(Panel to, params UIElement[] els)
    {
        foreach (var el in els)
        {
            (VisualTreeHelper.GetParent(el) as Panel)?.Children.Remove(el);
            to.Children.Add(el);
        }
    }

    bool _squareCorners;

    bool IsSnapped()
    {
        if (_hwnd == IntPtr.Zero || _fullscreen || WindowState != WindowState.Normal) return false;
        try { return IsWindowArranged(_hwnd); } // Windows 10 2004+ / 11
        catch (EntryPointNotFoundException) { return false; }
    }

    // 1000px like the skin's controller, but never wider than the screen allows.
    void FitDock()
    {
        Dock.Width = Math.Min(1000, Math.Max(640, Overlay.ActualWidth - 80));
        // The title takes what's left of the left half, so it never runs into the centred transport.
        DockTitle.MaxWidth = Math.Max(80, Dock.Width / 2 - 110);
    }


    void ToggleMaximize()
    {
        if (_fullscreen) { ToggleFullscreen(); return; }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _fullscreen = true;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            SetCorners(round: false);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(_hwnd, 2), ref mi);
            var r = mi.rcMonitor;
            SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0040 /* SHOWWINDOW */);
        }
        else
        {
            _fullscreen = false;
            SetCorners(round: true);
            Left = _restoreBounds.Left; Top = _restoreBounds.Top; Width = _restoreBounds.Width; Height = _restoreBounds.Height;
            if (_restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
        }
        CloseToolTips();
        UpdateWindowButtons();
        ShowChrome();
    }

    // A tooltip that is open when the window changes size stays where its button used to be
    // (WPF only closes it on the next mouse move) -- a stray grey box over the video.
    static void CloseToolTips()
    {
        foreach (PresentationSource src in PresentationSource.CurrentSources)
            if (src.RootVisual is Visual root)
                foreach (var tip in Descendants<ToolTip>(root)) tip.IsOpen = false;
    }

    static IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    // Maximize to the monitor's work area (not over the taskbar, no off-screen overhang).
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0138 /* WM_CTLCOLORSTATIC */)
        {
            // The video host Static asks its parent (us) what to paint its background with.
            SetBkColor(wParam, 0x00000000);
            handled = true;
            return DarkBrush;
        }
        if (msg == 0x0047 /* WM_WINDOWPOSCHANGED */ && _hwnd != IntPtr.Zero && _squareCorners != (WindowState == WindowState.Maximized || IsSnapped()))
            Dispatcher.BeginInvoke(ApplyBarLayout); // snapped or unsnapped
        if (msg == 0x0024 /* WM_GETMINMAXINFO */ && !_fullscreen)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var mon = MonitorFromWindow(hwnd, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                var w = mi.rcWork; var m = mi.rcMonitor;
                mmi.ptMaxPosition = new POINT { X = w.Left - m.Left, Y = w.Top - m.Top };
                mmi.ptMaxSize = new POINT { X = w.Right - w.Left, Y = w.Bottom - w.Top };
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        return IntPtr.Zero;
    }

    // Lets the overlay (which covers the whole window) move and resize the real window.
    void StartNonClient(int hit)
    {
        ReleaseCapture();
        SendMessage(_hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)hit, IntPtr.Zero);
    }

    int EdgeHit(Point p)
    {
        if (WindowState != WindowState.Normal || _fullscreen) return 0;
        const double e = 6;
        double w = Overlay.ActualWidth, h = Overlay.ActualHeight;
        bool l = p.X < e, r = p.X > w - e, t = p.Y < e, b = p.Y > h - e;
        if (t && l) return 13; if (t && r) return 14; if (b && l) return 16; if (b && r) return 17;
        if (l) return 10; if (r) return 11; if (t) return 12; if (b) return 15;
        return 0;
    }

    static Cursor? EdgeCursor(int hit) => hit switch
    {
        10 or 11 => Cursors.SizeWE, 12 or 15 => Cursors.SizeNS, 13 or 17 => Cursors.SizeNWSE, 14 or 16 => Cursors.SizeNESW, _ => null,
    };

    // ------------------------------------------------------------------ auto-hide

    bool AnyMenuOpen => ContextMenuService.GetContextMenu(Overlay)?.IsOpen == true || _openMenu?.IsOpen == true;

    bool CanAutoHide() =>
        !_pinChrome && !_musicMode && _player.Mp.IsPlaying && Side.Visibility != Visibility.Visible && UrlBox.Visibility != Visibility.Visible &&
        !AnyMenuOpen && !TopBar.IsMouseOver && !BottomBar.IsMouseOver && !Dock.IsMouseOver && Mouse.LeftButton != MouseButtonState.Pressed;

    void ShowChrome()
    {
        Overlay.Cursor = null;
        _idle.Stop(); _idle.Start();
        if (_chromeShown) return;
        _chromeShown = true;
        Chrome.IsHitTestVisible = true;
        AnimateChrome(show: true);
    }

    void HideChrome()
    {
        if (!_chromeShown) return;
        _chromeShown = false;
        Chrome.IsHitTestVisible = false;
        CloseToolTips();
        Seek.HidePreview();
        CursorMoved(); // remember where the pointer rests; only moving it shows the bars again
        AnimateChrome(show: false);
        if (Overlay.IsMouseOver) Overlay.Cursor = Cursors.None;
    }

    // Bars glide in from (and back out to) the window edges while fading. In fullscreen the dock
    // only sinks a little and fades out slowly, so it melts away instead of sliding off-screen.
    void AnimateChrome(bool show)
    {
        // Show: glide in quickly and settle (ease-out). Hide: start gently and accelerate away,
        // staying visible long enough that the slide reads as a slide rather than a blink.
        int ms = show ? 300 : _fullscreen ? 900 : 450;
        IEasingFunction slide = show ? new CubicEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseIn };
        IEasingFunction fade = show ? new QuadraticEase { EasingMode = EasingMode.EaseOut }
            : _fullscreen ? new SineEase { EasingMode = EasingMode.EaseInOut } : new QuadraticEase { EasingMode = EasingMode.EaseIn };
        double topTo = show ? 0 : _fullscreen ? -24 : -TopBar.ActualHeight;
        double bottomTo = show ? 0 : _fullscreen ? 18 : BottomBar.ActualHeight;
        var d = TimeSpan.FromMilliseconds(ms);
        TopShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(topTo, d) { EasingFunction = slide });
        BottomShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(bottomTo, d) { EasingFunction = slide });
        Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : ms)) { EasingFunction = fade });
    }

    static void Fade(UIElement el, double to, int ms) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() });

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        TryStartDrag(e);
        var p = e.GetPosition(Overlay);
        int hit = EdgeHit(p);
        Overlay.Cursor = EdgeCursor(hit) ?? (_chromeShown ? null : Overlay.Cursor);
        // Only a real move of the pointer counts. WPF also raises MouseMove when the layout under
        // a resting pointer changes (bars hiding, fullscreen), which used to bring the bars back.
        if (!CursorMoved()) return;
        ShowChrome();
    }

    bool CursorMoved()
    {
        GetCursorPos(out var c);
        bool moved = _lastCursor.X == int.MinValue || Math.Abs(c.X - _lastCursor.X) >= 2 || Math.Abs(c.Y - _lastCursor.Y) >= 2;
        if (moved) _lastCursor = c;
        return moved;
    }

    void Overlay_MouseLeave(object sender, MouseEventArgs e)
    {
        // Leaving the window hides the bars sooner, like the redesign describes.
        _idle.Stop();
        _idle.Interval = TimeSpan.FromMilliseconds(700);
        _idle.Start();
        Dispatcher.BeginInvoke(() => _idle.Interval = TimeSpan.FromMilliseconds(2500), DispatcherPriority.Background);
    }

    void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        int hit = EdgeHit(e.GetPosition(Overlay));
        if (hit != 0) { StartNonClient(hit); e.Handled = true; }
    }

    // Window moves start only once the mouse has actually been dragged a few pixels. Starting
    // Windows' move loop on every press would swallow clicks, and Windows turns a double-click
    // on a move area into "maximize" -- which broke double-click-to-fullscreen on the video.
    Point? _dragFrom;

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Overlay && e.OriginalSource is not Panel && e.OriginalSource != Curtain) return;
        _dragFrom = null;
        if (e.ClickCount == 2) { ToggleFullscreen(); e.Handled = true; return; }
        if (Side.Visibility == Visibility.Visible) { CloseSide(); return; }
        if (UrlBox.Visibility == Visibility.Visible) { UrlBox.Visibility = Visibility.Collapsed; return; }
        // Dragging the video moves the window, like VLC.
        if (WindowState == WindowState.Normal && !_fullscreen) _dragFrom = e.GetPosition(Overlay);
    }

    void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragFrom = null;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        // Maximized windows can be dragged too: Windows restores them (and Snap works).
        if (!_fullscreen) _dragFrom = e.GetPosition(Overlay);
    }

    void TryStartDrag(MouseEventArgs e)
    {
        if (_dragFrom is not Point from) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragFrom = null; return; }
        var p = e.GetPosition(Overlay);
        if (Math.Abs(p.X - from.X) < 4 && Math.Abs(p.Y - from.Y) < 4) return;
        _dragFrom = null;
        StartNonClient(2 /* HTCAPTION */);
    }

    void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Overlay && e.OriginalSource is not Panel) return;
        ShowMenu(BuildContextMenu(), null);
        e.Handled = true;
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((BottomBar.IsMouseOver || Dock.IsMouseOver) && !Volume.IsMouseOver) return;
        SetVolume(_volume + (e.Delta > 0 ? 1 : -1) * (int)VolumeStep, osd: true);
    }

    // ------------------------------------------------------------------ periodic UI

    void UpdateUi()
    {
        var mp = _player.Mp;
        bool has = _player.HasMedia && mp.State is not (VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial or VLCState.Error);
        Idle.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has && Curtain.Visibility != Visibility.Visible) DropCurtain();
        IdleHint.Text = $"or press {(KeyLabelOr("", "Ctrl+O"))} to open a file";
        long len = mp.Length, t = mp.Time;
        TimeText.Text = Fmt.Time(has ? t : 0);
        DurationText.Text = Fmt.Time(has ? len : 0);
        Seek.Enabled = has && mp.IsSeekable && len > 0;
        Seek.Value = len > 0 ? (double)t / len : 0;
        PlayIcon.Data = (Geometry)FindResource(mp.IsPlaying ? "Icon.Pause" : "Icon.Play");
        PlayButton.ToolTip = (mp.IsPlaying ? "Pause" : "Play") + KeyHint("play-pause");
        TitleText.Text = _player.HasMedia ? _player.Title : "VLC Redesign";
        UpdateMusicMode(has);
        EnsureThumbnails(has);
        DockTitle.Text = TitleText.Text;
        Title = _player.HasMedia ? _player.Title + " — VLC Redesign" : "VLC Redesign";
        MuteIcon.Data = (Geometry)FindResource(mp.Mute ? "Icon.Muted" : "Icon.Volume");
        Volume.Muted = mp.Mute;
        Volume.InvalidateVisual();
        if (_player.CurrentItem is { DurationMs: 0 } cur && len > 0) cur.SetDuration(len);
        if (!mp.IsPlaying) ShowChromeQuietly();
    }

    // Paused or stopped: keep the bars up, without restarting the hide timer.
    void ShowChromeQuietly()
    {
        if (_chromeShown) return;
        _chromeShown = true;
        Overlay.Cursor = null;
        Chrome.IsHitTestVisible = true;
        AnimateChrome(show: true);
    }

    // ------------------------------------------------------------------ seek previews

    Thumbnails? _thumbs;
    QueueItem? _thumbsFor;
    ChapterDescription[] _chapters = Array.Empty<ChapterDescription>();
    readonly DispatcherTimer _thumbDwell = new() { Interval = TimeSpan.FromMilliseconds(120) };
    long _thumbWant;

    // Start the frame grabber once a local video is actually playing (its size is known then).
    void EnsureThumbnails(bool has)
    {
        var mp = _player.Mp;
        var item = _player.CurrentItem;
        if (_thumbs != null || !has || _musicMode || item == null || _thumbsFor == item) return;
        if (mp.State != VLCState.Playing || !mp.IsSeekable || mp.Length <= 0 || mp.VideoTrackCount <= 0 || !File.Exists(item.Mrl)) return;
        uint w = 0, h = 0;
        if (!mp.Size(0, ref w, ref h) || w == 0 || h == 0) return;
        _thumbsFor = item;
        try
        {
            _chapters = mp.FullChapterDescriptions(-1) ?? Array.Empty<ChapterDescription>();
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _thumbs = new Thumbnails(_player.LibVlc, item.Mrl, mp.Length, (double)w / h, (int)Math.Ceiling(Seek.PreviewWidth * dpi));
            _thumbs.Updated += () => Dispatcher.BeginInvoke(Seek.RefreshPreview, DispatcherPriority.Background);
        }
        catch (Exception ex) { App.LogError(ex); _thumbs = null; }
    }

    void DropThumbnails()
    {
        _thumbDwell.Stop();
        _thumbs?.Dispose();
        _thumbs = null;
        _thumbsFor = null;
        _chapters = Array.Empty<ChapterDescription>();
        Seek.HidePreview();
    }

    SeekPreview SeekPreviewAt(double pos)
    {
        long len = _player.Mp.Length;
        if (len <= 0) return new SeekPreview(null, "");
        long ms = (long)(pos * len);
        // Show the nearest frame now; ask for the exact one once the pointer settles.
        _thumbWant = ms;
        _thumbDwell.Stop(); _thumbDwell.Start();
        string? chapter = null;
        if (_chapters.Length > 1)
            foreach (var c in _chapters) if (c.TimeOffset <= ms) chapter = c.Name;
        return new SeekPreview(_thumbs?.Get(ms), Fmt.Time(ms), string.IsNullOrWhiteSpace(chapter) ? null : chapter);
    }

    // ------------------------------------------------------------------ music

    bool _musicMode;
    CancellationTokenSource? _waveCts;

    // Audio-only media switches to the music look; the bars stay up since there's no picture.
    void UpdateMusicMode(bool has)
    {
        var mp = _player.Mp;
        bool music = has && mp.State is VLCState.Playing or VLCState.Paused && mp.Time > 0 && mp.VideoTrackCount <= 0;
        if (music == _musicMode) return;
        _musicMode = music;
        MusicView.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        if (!music) { StopWaveform(); return; }
        ShowChromeQuietly();
        var media = mp.Media;
        string? Meta(MetadataType t) { var v = media?.Meta(t); return string.IsNullOrWhiteSpace(v) ? null : v; }
        MusicTitle.Text = Meta(MetadataType.Title) ?? _player.Title;
        MusicArtist.Text = string.Join("  ·  ", new[] { Meta(MetadataType.Artist), Meta(MetadataType.Album) }.Where(x => x != null));
        Cover.Background = null;
        try
        {
            if (Meta(MetadataType.ArtworkURL) is string art && Uri.TryCreate(art, UriKind.Absolute, out var u) && u.IsFile && File.Exists(u.LocalPath))
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = u; bmp.DecodePixelWidth = 600; bmp.EndInit();
                Cover.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
        }
        catch (Exception ex) { App.LogError(ex); }
        FitCover();
        if (_settings.Waveform && _player.CurrentItem is { } item && File.Exists(item.Mrl)) LoadWaveform(item);
    }

    void FitCover()
    {
        double size = Math.Clamp(Math.Min(Overlay.ActualWidth * 0.4, Overlay.ActualHeight - 300), 140, 340);
        CoverFrame.Width = CoverFrame.Height = size;
    }

    async void LoadWaveform(QueueItem item)
    {
        StopWaveform();
        var cts = _waveCts = new CancellationTokenSource();
        var peaks = await Waveform.GetAsync(_player.LibVlc, item.Mrl, cts.Token);
        if (!cts.IsCancellationRequested && _player.CurrentItem == item && _musicMode) Seek.Peaks = peaks;
    }

    void StopWaveform()
    {
        _waveCts?.Cancel();
        _waveCts = null;
        Seek.Peaks = null;
    }

    readonly DispatcherTimer _curtainTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    void DropCurtain()
    {
        _curtainTimer.Stop();
        Curtain.BeginAnimation(OpacityProperty, null);
        Curtain.Opacity = 1;
        Curtain.Visibility = Visibility.Visible;
    }

    // Wait a beat after "Playing" so the first frames (and the darkened vout windows) are up.
    void LiftCurtainSoon()
    {
        DarkenVideoWindows();
        _curtainTimer.Stop();
        _curtainTimer.Tick -= OnCurtainTick;
        _curtainTimer.Tick += OnCurtainTick;
        _curtainTimer.Start();
    }

    void OnCurtainTick(object? s, EventArgs e)
    {
        _curtainTimer.Stop();
        DarkenVideoWindows();
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        a.Completed += (_, _) => { if (_player.Mp.IsPlaying) Curtain.Visibility = Visibility.Collapsed; };
        Curtain.BeginAnimation(OpacityProperty, a);
    }

    void OnMediaChanged()
    {
        DropThumbnails();
        _musicMode = false;
        MusicView.Visibility = Visibility.Collapsed;
        StopWaveform();
        DropCurtain();
        UpdateQueueHeader();
        UpdateUi();
        ShowChrome();
        if (Side.Visibility == Visibility.Visible) RefreshSidebar();
    }

    void OnSeek(double pos, bool final)
    {
        if (!_player.Mp.IsSeekable) return;
        // Live-scrub while dragging; libvlc handles rapid position updates fine.
        _player.Mp.Position = (float)pos;
        TimeText.Text = Fmt.Time((long)(pos * _player.Mp.Length));
    }

    // ------------------------------------------------------------------ volume

    double VolumeStep => Math.Max(1, Math.Round(_cfg.GetDouble("volume-step", 12.8) / 256 * 100));

    void SetVolume(int v, bool osd, string key = "")
    {
        _volume = (int)Math.Clamp(v, 0, Volume.Max);
        Volume.Value = _volume;
        ApplyVolume();
        if (osd) ShowOsd($"Volume {_volume}%", key);
    }

    void ApplyVolume()
    {
        _player.Mp.Volume = _volume;
        if (_settings.Muted != _player.Mp.Mute && _player.Mp.IsPlaying) { }
    }

    void ToggleMute(string key = "")
    {
        _player.Mp.Mute = !_player.Mp.Mute;
        UpdateUi();
        ShowOsd(_player.Mp.Mute ? "Muted" : $"Volume {_volume}%", key);
    }

    // ------------------------------------------------------------------ OSD

    void ShowOsd(string text, string key = "")
    {
        OsdText.Text = text;
        OsdKey.Text = key;
        OsdKeyBox.Visibility = string.IsNullOrEmpty(key) ? Visibility.Collapsed : Visibility.Visible;
        Osd.Margin = new Thickness(18, _chromeShown && !_fullscreen ? 50 : 18, 0, 0);
        Osd.BeginAnimation(OpacityProperty, null);
        Osd.Opacity = 1;
        _osdTimer.Stop(); _osdTimer.Start();
    }

    // ------------------------------------------------------------------ hotkeys

    string KeyHint(string action) { var l = _keys.Label(action); return l.Length > 0 ? $"  ({l})" : ""; }
    string KeyLabelOr(string action, string fallback) { var l = action.Length > 0 ? _keys.Label(action) : ""; return l.Length > 0 ? l : fallback; }

    void UpdateTooltips()
    {
        PrevButton.ToolTip = "Previous" + KeyHint("prev");
        NextButton.ToolTip = "Next" + KeyHint("next");
        MuteButton.ToolTip = "Mute" + KeyHint("vol-mute");
        AudioButton.ToolTip = "Audio" + KeyHint("audio-track");
        SubsButton.ToolTip = "Subtitles" + KeyHint("subtitle-track");
        GearButton.ToolTip = "Player settings  (Ctrl+E)";
        QueueButton.ToolTip = "Queue  (Ctrl+L)";
        Volume.ToolTip = "Volume (scroll to change)";
        UpdateWindowButtons();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        // Arrows belong to a focused slider or queue list; everywhere else they seek / set volume.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && e.OriginalSource is Slider or ListBoxItem or ListBox) return;
        var action = _keys.Match(e, out var pressed);
        if (action != null && Do(action, Pretty(pressed))) { e.Handled = true; return; }
        // App shortcuts (VLC's menu shortcuts, which aren't hotkeys in vlcrc).
        var mods = Keyboard.Modifiers;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (mods == ModifierKeys.Control)
        {
            switch (k)
            {
                case Key.O: OpenFiles(replace: true); break;
                case Key.F: OpenFolder(); break;
                case Key.N: ShowUrlBox(); break;
                case Key.L: ToggleSide(QueuePane); break;
                case Key.E: ToggleSide(SettingsPane); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (mods == ModifierKeys.None && k == Key.F1) { ToggleSide(KeysPane); e.Handled = true; }
        else if (mods == ModifierKeys.None && k == Key.Escape && Side.Visibility == Visibility.Visible) { CloseSide(); e.Handled = true; }
    }

    static string Pretty(string k) => string.Join("+", k.Split('+').Select(p => p.Length == 1 ? p.ToUpperInvariant() : p));

    long JumpMs(string size) => (long)(_cfg.GetDouble($"{size}-jump-size", size switch
    { "extrashort" => 3, "short" => 10, "medium" => 60, _ => 300 }) * 1000);

    /// <summary>Runs a VLC hotkey action. Returns false for actions this player doesn't implement.</summary>
    bool Do(string action, string key = "")
    {
        var mp = _player.Mp;
        switch (action)
        {
            case "play-pause": _player.TogglePlay(); ShowOsd(mp.IsPlaying ? "Pause" : "Play", key); break;
            case "play": if (!mp.IsPlaying) _player.TogglePlay(); break;
            case "pause": if (mp.IsPlaying) mp.SetPause(true); break;
            case "stop": _player.Stop(); ShowOsd("Stop", key); break;
            case "next": _player.Next(); break;
            case "prev": _player.Prev(); break;
            case "toggle-fullscreen": ToggleFullscreen(); break;
            case "leave-fullscreen": if (_fullscreen) ToggleFullscreen(); else return false; break;
            case "vol-up": SetVolume(_volume + (int)VolumeStep, true, key); break;
            case "vol-down": SetVolume(_volume - (int)VolumeStep, true, key); break;
            case "vol-mute": ToggleMute(key); break;
            case "faster": SetRate(CoarseRates.FirstOrDefault(r => r > mp.Rate + 0.01f, CoarseRates[^1]), key); break;
            case "slower": SetRate(CoarseRates.LastOrDefault(r => r < mp.Rate - 0.01f, CoarseRates[0]), key); break;
            case "rate-normal": SetRate(1, key); break;
            case "rate-faster-fine": SetRate(MathF.Round((mp.Rate + 0.1f) * 10) / 10, key); break;
            case "rate-slower-fine": SetRate(MathF.Round((mp.Rate - 0.1f) * 10) / 10, key); break;
            case "jump-extrashort": Jump(-JumpMs("extrashort"), key); break;
            case "jump+extrashort": Jump(JumpMs("extrashort"), key); break;
            case "jump-short": Jump(-JumpMs("short"), key); break;
            case "jump+short": Jump(JumpMs("short"), key); break;
            // VLC binds the plain arrows to menu navigation; outside a DVD menu it falls back to
            // Left/Right = short jump and Up/Down = volume (src/input/input.c, ControlNav).
            case "nav-left": Jump(-JumpMs("short"), key); break;
            case "nav-right": Jump(JumpMs("short"), key); break;
            case "nav-up": SetVolume(_volume + (int)VolumeStep, true, key); break;
            case "nav-down": SetVolume(_volume - (int)VolumeStep, true, key); break;
            case "jump-medium": Jump(-JumpMs("medium"), key); break;
            case "jump+medium": Jump(JumpMs("medium"), key); break;
            case "jump-long": Jump(-JumpMs("long"), key); break;
            case "jump+long": Jump(JumpMs("long"), key); break;
            case "frame-next": mp.NextFrame(); ShowOsd("Next frame", key); break;
            case "audio-track": CycleAudio(key); break;
            case "subtitle-track": CycleSubs(+1, key); break;
            case "subtitle-revtrack": CycleSubs(-1, key); break;
            case "subtitle-toggle": ToggleSubs(key); break;
            case "audiodelay-up": SetAudioDelay(mp.AudioDelay / 1000 + 50, key); break;
            case "audiodelay-down": SetAudioDelay(mp.AudioDelay / 1000 - 50, key); break;
            case "subdelay-up": SetSubDelay(mp.SpuDelay / 1000 + 50, key); break;
            case "subdelay-down": SetSubDelay(mp.SpuDelay / 1000 - 50, key); break;
            case "aspect-ratio": CycleAspect(key); break;
            case "snapshot": TakeSnapshot(key); break;
            case "quit": Close(); break;
            case "loop": _player.Repeat = !_player.Repeat; SyncToggles(); ShowOsd(_player.Repeat ? "Repeat queue: on" : "Repeat queue: off", key); break;
            case "random": _player.Shuffle = !_player.Shuffle; SyncToggles(); ShowOsd(_player.Shuffle ? "Shuffle: on" : "Shuffle: off", key); break;
            case "intf-show": if (_chromeShown) HideChrome(); else ShowChrome(); break;
            case "position": ShowOsd($"{Fmt.Time(mp.Time)} / {Fmt.Time(mp.Length)}", key); break;
            case "chapter-next": mp.NextChapter(); ShowOsd("Next chapter", key); break;
            case "chapter-prev": mp.PreviousChapter(); ShowOsd("Previous chapter", key); break;
            case "title-next": mp.Title = mp.Title + 1; break;
            case "title-prev": if (mp.Title > 0) mp.Title = mp.Title - 1; break;
            case "play-bookmark1": case "set-bookmark1": return false;
            default: return false;
        }
        UpdateUi();
        return true;
    }

    void Jump(long ms, string key)
    {
        _player.SeekBy(ms);
        ShowOsd($"{(ms > 0 ? "+" : "−")}{Math.Abs(ms) / 1000}s   {Fmt.Time(_player.Mp.Time)}", key);
    }

    void SetRate(float r, string key = "")
    {
        r = Math.Clamp(r, 0.25f, 4f);
        _player.Mp.SetRate(r);
        ShowOsd($"Speed {r:0.##}×", key);
        _updatingUi = true; SpeedSlider.Value = Math.Min(2, r); SpeedValue.Text = $"{r:0.##}×"; _updatingUi = false;
    }

    void SetAudioDelay(long ms, string key = "")
    {
        _player.Mp.SetAudioDelay(ms * 1000);
        ShowOsd($"Audio delay {ms:+0;−0;0} ms", key);
        SyncDelays();
    }

    void SetSubDelay(long ms, string key = "")
    {
        _player.Mp.SetSpuDelay(ms * 1000);
        ShowOsd($"Subtitle delay {ms:+0;−0;0} ms", key);
        SyncDelays();
    }

    void CycleAudio(string key)
    {
        var tracks = _player.Mp.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
        if (tracks.Length == 0) { ShowOsd("No audio tracks", key); return; }
        int i = Array.FindIndex(tracks, t => t.Id == _player.Mp.AudioTrack);
        var next = tracks[(i + 1) % tracks.Length];
        _player.Mp.SetAudioTrack(next.Id);
        ShowOsd($"Audio: {next.Name}", key);
    }

    void CycleSubs(int dir, string key)
    {
        var tracks = _player.Mp.SpuDescription; // includes "Disable" (-1)
        if (tracks.Length <= 1) { ShowOsd("No subtitles", key); return; }
        int i = Array.FindIndex(tracks, t => t.Id == _player.Mp.Spu);
        var next = tracks[((i + dir) % tracks.Length + tracks.Length) % tracks.Length];
        _player.Mp.SetSpu(next.Id);
        ShowOsd(next.Id < 0 ? "Subtitles off" : $"Subtitles: {next.Name}", key);
    }

    int _lastSpu = -1;
    void ToggleSubs(string key)
    {
        var mp = _player.Mp;
        if (mp.Spu >= 0) { _lastSpu = mp.Spu; mp.SetSpu(-1); ShowOsd("Subtitles off", key); }
        else
        {
            var id = _lastSpu >= 0 ? _lastSpu : mp.SpuDescription.FirstOrDefault(t => t.Id >= 0).Id;
            if (id > 0 || mp.SpuDescription.Any(t => t.Id == id && id >= 0)) { mp.SetSpu(id); ShowOsd("Subtitles on", key); }
            else ShowOsd("No subtitles", key);
        }
    }

    void CycleAspect(string key)
    {
        var cur = _player.Mp.AspectRatio;
        int i = Array.FindIndex(Aspects, a => a.Value == cur);
        var next = Aspects[(i + 1) % Aspects.Length];
        _player.Mp.AspectRatio = next.Value;
        ShowOsd($"Aspect ratio: {next.Label}", key);
    }

    void TakeSnapshot(string key = "")
    {
        if (!_player.HasMedia) return;
        var dir = _cfg.Get("snapshot-path");
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        bool ok = _player.Mp.TakeSnapshot(0, dir, 0, 0);
        ShowOsd(ok ? $"Snapshot saved to {Path.GetFileName(dir.TrimEnd('\\'))}" : "Snapshot failed", key);
    }

    // ------------------------------------------------------------------ menus

    ContextMenu? _openMenu;

    void ShowMenu(ContextMenu menu, UIElement? target)
    {
        if (_openMenu != null) _openMenu.IsOpen = false;
        _openMenu = menu;
        if (target != null) { menu.PlacementTarget = target; menu.Placement = target == MoreButton ? PlacementMode.Bottom : PlacementMode.Top; }
        else menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) => { if (_openMenu == menu) _openMenu = null; _idle.Stop(); _idle.Start(); };
        menu.IsOpen = true;
        ShowChrome();
    }

    MenuItem Item(string header, Action click, string gesture = "", string? icon = null, bool isChecked = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture, IsChecked = isChecked, IsEnabled = enabled };
        if (icon != null) mi.Icon = new IconView { Data = (Geometry)FindResource("Icon." + icon), Size = 16, Foreground = (Brush)FindResource("TextDim") };
        mi.Click += (_, _) => click();
        return mi;
    }

    ContextMenu BuildMainMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(Item("Open File…", () => OpenFiles(replace: true), "Ctrl+O", "File"));
        m.Items.Add(Item("Open Folder…", OpenFolder, "Ctrl+F", "Folder"));
        m.Items.Add(Item("Open Network Stream…", ShowUrlBox, "Ctrl+N", "Network"));
        m.Items.Add(Item("Add Subtitle File…", AddSubtitleFile, "", "Subtitles", enabled: _player.HasMedia));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Queue", () => ToggleSide(QueuePane), "Ctrl+L", "Queue"));
        m.Items.Add(Item("Player Settings", () => ToggleSide(SettingsPane), "Ctrl+E", "Settings"));
        m.Items.Add(Item("Keyboard Shortcuts", () => ToggleSide(KeysPane), "F1", "Keyboard"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Open in Classic VLC", OpenInVlc, "", "Pin", enabled: _player.HasMedia));
        bool reg = ShellIntegration.IsRegistered();
        m.Items.Add(Item(reg ? "Remove from Explorer's Open with" : "Add to Explorer's Open with", () =>
        {
            try { if (reg) ShellIntegration.Unregister(); else ShellIntegration.Register(); ShowOsd(reg ? "Removed from Open with" : "Added to Open with"); }
            catch (Exception ex) { App.LogError(ex); ShowOsd("Couldn't update Open with"); }
        }, "", "Folder"));
        m.Items.Add(Item("About", () => ShowOsd($"VLC Redesign · libVLC {_player.LibVlc.Version.Split(' ')[0]} · design by Juxtopposed"), "", "Info"));
        m.Items.Add(Item("Quit", Close, _keys.Label("quit"), "Quit"));
        return m;
    }

    ContextMenu BuildContextMenu()
    {
        var mp = _player.Mp;
        var m = new ContextMenu();
        m.Items.Add(Item(mp.IsPlaying ? "Pause" : "Play", () => Do("play-pause"), _keys.Label("play-pause")));
        m.Items.Add(Item("Stop", () => Do("stop"), _keys.Label("stop"), enabled: _player.HasMedia));
        m.Items.Add(Item("Previous", () => Do("prev"), _keys.Label("prev")));
        m.Items.Add(Item("Next", () => Do("next"), _keys.Label("next")));
        m.Items.Add(new Separator());
        var audio = new MenuItem { Header = "Audio track" }; FillAudioMenu(audio.Items); m.Items.Add(audio);
        var subs = new MenuItem { Header = "Subtitles" }; FillSubsMenu(subs.Items); m.Items.Add(subs);
        var speed = new MenuItem { Header = "Speed" };
        foreach (var r in new[] { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f })
            speed.Items.Add(Item($"{r:0.##}×", () => SetRate(r), isChecked: Math.Abs(mp.Rate - r) < 0.01));
        m.Items.Add(speed);
        var aspect = new MenuItem { Header = "Aspect ratio" };
        foreach (var a in Aspects) aspect.Items.Add(Item(a.Label, () => { mp.AspectRatio = a.Value; ShowOsd($"Aspect ratio: {a.Label}"); }, isChecked: mp.AspectRatio == a.Value));
        m.Items.Add(aspect);
        m.Items.Add(new Separator());
        m.Items.Add(Item(_fullscreen ? "Exit Fullscreen" : "Fullscreen", ToggleFullscreen, _keys.Label("toggle-fullscreen"), "Fullscreen"));
        m.Items.Add(Item("Take Snapshot", () => TakeSnapshot(), _keys.Label("snapshot"), "Camera", enabled: _player.HasMedia));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Open File…", () => OpenFiles(replace: true), "Ctrl+O", "File"));
        m.Items.Add(Item("Queue", () => ToggleSide(QueuePane), "Ctrl+L", "Queue"));
        m.Items.Add(Item("Player Settings", () => ToggleSide(SettingsPane), "Ctrl+E", "Settings"));
        return m;
    }

    void FillAudioMenu(ItemCollection items)
    {
        var mp = _player.Mp;
        var tracks = mp.AudioTrackDescription;
        if (tracks.Length == 0) items.Add(Item("No audio tracks", () => { }, enabled: false));
        foreach (var t in tracks)
        {
            var id = t.Id;
            items.Add(Item(id < 0 ? "Off" : t.Name, () => mp.SetAudioTrack(id), isChecked: mp.AudioTrack == id));
        }
        var devices = mp.AudioOutputDeviceEnum;
        if (devices.Length == 0) return;
        items.Add(new Separator());
        var dev = new MenuItem { Header = "Output device" };
        string? curDev = mp.OutputDevice;
        foreach (var d in devices)
        {
            var id = d.DeviceIdentifier;
            dev.Items.Add(Item(d.Description, () => mp.SetOutputDevice(id), isChecked: curDev == id));
        }
        items.Add(dev);
    }

    void FillSubsMenu(ItemCollection items)
    {
        var mp = _player.Mp;
        foreach (var t in mp.SpuDescription)
        {
            var id = t.Id;
            items.Add(Item(id < 0 ? "Off" : t.Name, () => mp.SetSpu(id), isChecked: mp.Spu == id));
        }
        if (mp.SpuDescription.Length == 0) items.Add(Item("No subtitles", () => { }, enabled: false));
        items.Add(new Separator());
        items.Add(Item("Add subtitle file…", AddSubtitleFile, "", "Add", enabled: _player.HasMedia));
        items.Add(Item("Subtitle timing…", () => { TabSubs.IsChecked = true; OpenSide(SettingsPane); }, "", "Settings"));
    }

    void More_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildMainMenu(), MoreButton);

    void AudioMenu_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        FillAudioMenu(m.Items);
        ShowMenu(m, AudioButton);
    }

    void SubsMenu_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        FillSubsMenu(m.Items);
        ShowMenu(m, SubsButton);
    }

    // ------------------------------------------------------------------ side panels

    void ToggleSide(FrameworkElement pane)
    {
        if (Side.Visibility == Visibility.Visible && pane.Visibility == Visibility.Visible) CloseSide();
        else OpenSide(pane);
    }

    void OpenSide(FrameworkElement pane)
    {
        foreach (var p in new FrameworkElement[] { SettingsPane, QueuePane, KeysPane }) p.Visibility = p == pane ? Visibility.Visible : Visibility.Collapsed;
        Side.Visibility = Visibility.Visible;
        if (pane == SettingsPane) RefreshSidebar();
        if (pane == KeysPane) FillShortcuts();
        if (pane == QueuePane) { SyncToggles(); QueueList.ScrollIntoView(_player.CurrentItem); }
        ShowChrome();
    }

    void CloseSide() { Side.Visibility = Visibility.Collapsed; _idle.Stop(); _idle.Start(); }

    void Gear_Click(object sender, RoutedEventArgs e) => ToggleSide(SettingsPane);
    void Queue_Click(object sender, RoutedEventArgs e) => ToggleSide(QueuePane);

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanePlayback == null) return;
        PanePlayback.Visibility = TabPlayback.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneSubs.Visibility = TabSubs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneAudio.Visibility = TabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneVideo.Visibility = TabVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshSidebar();
    }

    RadioButton Option(string text, bool isChecked, Action onPick, string group)
    {
        var rb = new RadioButton { Content = text, IsChecked = isChecked, GroupName = group, Style = (Style)FindResource("OptionRow") };
        rb.Checked += (_, _) => { if (!_updatingUi) onPick(); };
        return rb;
    }

    void RefreshSidebar()
    {
        var mp = _player.Mp;
        _updatingUi = true;
        SpeedSlider.Value = Math.Min(2, mp.Rate); SpeedValue.Text = $"{mp.Rate:0.##}×";
        WaveOptions.Children.Clear();
        WaveOptions.Children.Add(Option("For music", _settings.Waveform, () => { _settings.Waveform = true; _musicMode = false; UpdateUi(); }, "wave"));
        WaveOptions.Children.Add(Option("Off", !_settings.Waveform, () => { _settings.Waveform = false; StopWaveform(); }, "wave"));

        SubTracks.Children.Clear();
        foreach (var t in mp.SpuDescription) { var id = t.Id; SubTracks.Children.Add(Option(id < 0 ? "Off" : t.Name, mp.Spu == id, () => mp.SetSpu(id), "spu")); }
        if (mp.SpuDescription.Length == 0) SubTracks.Children.Add(new TextBlock { Text = "No subtitles in this file", Foreground = (Brush)FindResource("TextFaint"), Margin = new Thickness(10, 6, 0, 0) });

        AudioTracks.Children.Clear();
        foreach (var t in mp.AudioTrackDescription) { var id = t.Id; AudioTracks.Children.Add(Option(id < 0 ? "Off" : t.Name, mp.AudioTrack == id, () => mp.SetAudioTrack(id), "atrack")); }
        AudioDevices.Children.Clear();
        string? curDev = mp.OutputDevice;
        foreach (var d in mp.AudioOutputDeviceEnum) { var id = d.DeviceIdentifier; AudioDevices.Children.Add(Option(d.Description, curDev == id, () => mp.SetOutputDevice(id), "adev")); }
        AudioDevicesHeader.Visibility = AudioDevices.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AspectList.Children.Clear();
        foreach (var a in Aspects) AspectList.Children.Add(Option(a.Label, mp.AspectRatio == a.Value, () => mp.AspectRatio = a.Value, "aspect"));
        VideoTracks.Children.Clear();
        foreach (var t in mp.VideoTrackDescription) { var id = t.Id; VideoTracks.Children.Add(Option(id < 0 ? "Off" : t.Name, mp.VideoTrack == id, () => mp.SetVideoTrack(id), "vtrack")); }

        SyncDelays();
        SyncToggles();
        _updatingUi = false;
    }

    void SyncDelays()
    {
        bool was = _updatingUi; _updatingUi = true;
        double sd = _player.Mp.SpuDelay / 1_000_000.0, ad = _player.Mp.AudioDelay / 1_000_000.0;
        SubDelaySlider.Value = sd; SubDelayValue.Text = $"{sd:+0.00;−0.00;0.00} s";
        AudioDelaySlider.Value = ad; AudioDelayValue.Text = $"{ad:+0.00;−0.00;0.00} s";
        _updatingUi = was;
    }

    void SyncToggles()
    {
        RepeatToggle.IsChecked = RepeatToggle2.IsChecked = _player.Repeat;
        ShuffleToggle.IsChecked = ShuffleToggle2.IsChecked = _player.Shuffle;
    }

    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || SpeedValue == null || _player == null) return; // fires during InitializeComponent
        var r = (float)(Math.Round(e.NewValue / 0.05) * 0.05);
        _player.Mp.SetRate(r);
        SpeedValue.Text = $"{r:0.##}×";
    }
    void SpeedReset_Click(object sender, RoutedEventArgs e) => SetRate(1);

    void SubDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || SubDelayValue == null || _player == null) return; // fires during InitializeComponent
        long ms = (long)Math.Round(e.NewValue * 20) * 50;
        _player.Mp.SetSpuDelay(ms * 1000);
        SubDelayValue.Text = $"{ms / 1000.0:+0.00;−0.00;0.00} s";
    }
    void SubDelayReset_Click(object sender, RoutedEventArgs e) => SetSubDelay(0);

    void AudioDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || AudioDelayValue == null || _player == null) return; // fires during InitializeComponent
        long ms = (long)Math.Round(e.NewValue * 20) * 50;
        _player.Mp.SetAudioDelay(ms * 1000);
        AudioDelayValue.Text = $"{ms / 1000.0:+0.00;−0.00;0.00} s";
    }
    void AudioDelayReset_Click(object sender, RoutedEventArgs e) => SetAudioDelay(0);

    void ChapterPrev_Click(object sender, RoutedEventArgs e) => Do("chapter-prev");
    void ChapterNext_Click(object sender, RoutedEventArgs e) => Do("chapter-next");
    void Snapshot_Click(object sender, RoutedEventArgs e) => TakeSnapshot();
    void AddSubtitle_Click(object sender, RoutedEventArgs e) => AddSubtitleFile();

    static readonly Dictionary<string, string> ActionNames = new()
    {
        ["play-pause"] = "Play / pause", ["stop"] = "Stop", ["next"] = "Next", ["prev"] = "Previous",
        ["toggle-fullscreen"] = "Fullscreen", ["leave-fullscreen"] = "Leave fullscreen",
        ["vol-up"] = "Volume up", ["vol-down"] = "Volume down", ["vol-mute"] = "Mute",
        ["faster"] = "Faster", ["slower"] = "Slower", ["rate-normal"] = "Normal speed",
        ["rate-faster-fine"] = "Faster (fine)", ["rate-slower-fine"] = "Slower (fine)",
        ["jump-extrashort"] = "Back (very short)", ["jump+extrashort"] = "Forward (very short)",
        ["nav-left"] = "Back", ["nav-right"] = "Forward", ["nav-up"] = "Volume up (arrow)", ["nav-down"] = "Volume down (arrow)",
        ["jump-short"] = "Back (short)", ["jump+short"] = "Forward (short)",
        ["jump-medium"] = "Back (medium)", ["jump+medium"] = "Forward (medium)",
        ["jump-long"] = "Back (long)", ["jump+long"] = "Forward (long)",
        ["frame-next"] = "Next frame", ["audio-track"] = "Cycle audio track",
        ["subtitle-track"] = "Cycle subtitle track", ["subtitle-revtrack"] = "Cycle subtitles backwards",
        ["subtitle-toggle"] = "Subtitles on / off", ["audiodelay-up"] = "Audio delay +", ["audiodelay-down"] = "Audio delay −",
        ["subdelay-up"] = "Subtitle delay +", ["subdelay-down"] = "Subtitle delay −", ["aspect-ratio"] = "Cycle aspect ratio",
        ["snapshot"] = "Snapshot", ["quit"] = "Quit", ["loop"] = "Repeat", ["random"] = "Shuffle",
        ["intf-show"] = "Show / hide controls", ["position"] = "Show position",
        ["chapter-next"] = "Next chapter", ["chapter-prev"] = "Previous chapter",
    };

    void FillShortcuts()
    {
        var bound = _keys.All().ToDictionary(k => k.Action, k => k.Keys);
        var rows = ActionNames.Where(a => bound.ContainsKey(a.Key))
            .Select(a => new { Name = a.Value, Keys = bound[a.Key] })
            .Concat(new[]
            {
                new { Name = "Open file", Keys = "Ctrl+O" }, new { Name = "Open folder", Keys = "Ctrl+F" },
                new { Name = "Network stream", Keys = "Ctrl+N" }, new { Name = "Queue", Keys = "Ctrl+L" },
                new { Name = "Player settings", Keys = "Ctrl+E" }, new { Name = "This list", Keys = "F1" },
            }).ToList();
        KeysList.ItemsSource = rows;
    }

    // ------------------------------------------------------------------ queue

    void UpdateQueueHeader()
    {
        int n = _player.Queue.Count;
        long total = _player.Queue.Sum(q => q.DurationMs);
        string dur = total > 0 ? $" · {(total >= 3_600_000 ? $"{total / 3_600_000}h {total / 60_000 % 60}m" : $"{Math.Max(1, total / 60_000)}m")}" : "";
        QueueHeader.Text = $"Queue · {n} item{(n == 1 ? "" : "s")}{dur}";
        QueueEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedIndex >= 0) _player.PlayAt(QueueList.SelectedIndex);
    }

    void QueueList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { QueueRemove_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && QueueList.SelectedIndex >= 0) { _player.PlayAt(QueueList.SelectedIndex); e.Handled = true; }
    }

    void QueueRemove_Click(object sender, RoutedEventArgs e) { _player.RemoveAt(QueueList.SelectedIndex); UpdateQueueHeader(); }
    void QueueAdd_Click(object sender, RoutedEventArgs e) => OpenFiles(replace: false);
    void QueueSort_Click(object sender, RoutedEventArgs e) => _player.SortQueue();
    void Repeat_Click(object sender, RoutedEventArgs e) { _player.Repeat = ((ToggleButton)sender).IsChecked == true; SyncToggles(); }
    void Shuffle_Click(object sender, RoutedEventArgs e) { _player.Shuffle = ((ToggleButton)sender).IsChecked == true; SyncToggles(); }

    // ------------------------------------------------------------------ open

    string MediaFilter => "Media files|" + string.Join(";", MediaExts.Select(x => "*" + x)) + "|All files|*.*";

    void OpenFiles(bool replace)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaFilter, Title = replace ? "Open" : "Add to queue" };
        if (Directory.Exists(_settings.LastFolder)) dlg.InitialDirectory = _settings.LastFolder;
        if (dlg.ShowDialog(this) != true) return;
        _settings.LastFolder = Path.GetDirectoryName(dlg.FileNames[0]) ?? "";
        if (replace) _player.ReplaceQueue(dlg.FileNames);
        else _player.Enqueue(dlg.FileNames, playFirst: !_player.HasMedia);
    }

    void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Open folder" };
        if (Directory.Exists(_settings.LastFolder)) dlg.InitialDirectory = _settings.LastFolder;
        if (dlg.ShowDialog(this) != true) return;
        _settings.LastFolder = dlg.FolderName;
        var files = MediaInFolder(dlg.FolderName);
        if (files.Count == 0) { ShowOsd("No media files in that folder"); return; }
        _player.ReplaceQueue(files);
    }

    static List<string> MediaInFolder(string dir) =>
        Directory.EnumerateFiles(dir).Where(f => MediaExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                 .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();

    void AddSubtitleFile()
    {
        if (!_player.HasMedia) return;
        var dlg = new OpenFileDialog { Filter = "Subtitles|" + string.Join(";", SubExts.Select(x => "*" + x)) + "|All files|*.*", Title = "Add subtitle file" };
        var cur = _player.CurrentItem!.Mrl;
        if (File.Exists(cur)) dlg.InitialDirectory = Path.GetDirectoryName(cur);
        if (dlg.ShowDialog(this) == true) AddSubtitle(dlg.FileName);
    }

    void AddSubtitle(string path)
    {
        _player.Mp.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
        ShowOsd($"Subtitles: {Path.GetFileName(path)}");
    }

    void ShowUrlBox()
    {
        UrlBox.Visibility = Visibility.Visible;
        if (Clipboard.ContainsText() && Uri.TryCreate(Clipboard.GetText().Trim(), UriKind.Absolute, out var u) && !u.IsFile)
            UrlInput.Text = u.ToString();
        ShowChrome();
        _overlayWindow?.Activate();
        UrlInput.Focus();
        UrlInput.SelectAll();
    }

    void UrlOpen_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { ShowOsd("That doesn't look like a URL"); return; }
        UrlBox.Visibility = Visibility.Collapsed;
        _player.ReplaceQueue(new[] { url });
    }
    void UrlCancel_Click(object sender, RoutedEventArgs e) => UrlBox.Visibility = Visibility.Collapsed;
    void UrlInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) UrlOpen_Click(sender, e);
        else if (e.Key == Key.Escape) UrlBox.Visibility = Visibility.Collapsed;
    }

    void OpenInVlc()
    {
        var item = _player.CurrentItem;
        if (item == null) return;
        var t = Math.Max(0, _player.Mp.Time / 1000);
        _player.Mp.SetPause(true);
        Process.Start(new ProcessStartInfo(Path.Combine(_cfg.InstallDir, "vlc.exe"))
        {
            ArgumentList = { "--start-time=" + t, item.Mrl },
            UseShellExecute = false,
        });
    }

    void Overlay_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        ShowChrome();
    }

    void Overlay_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        // A subtitle dropped onto a playing video gets attached to it.
        if (dropped.Length == 1 && SubExts.Contains(Path.GetExtension(dropped[0]).ToLowerInvariant()) && _player.HasMedia)
        { AddSubtitle(dropped[0]); return; }
        var files = dropped.SelectMany(p => Directory.Exists(p) ? MediaInFolder(p) : new List<string> { p }).ToList();
        if (files.Count == 0) return;
        // Dropping onto the open queue appends; anywhere else plays right away (like VLC).
        if (QueuePane.Visibility == Visibility.Visible && Side.Visibility == Visibility.Visible)
            _player.Enqueue(files, playFirst: !_player.HasMedia);
        else _player.ReplaceQueue(files);
    }

    void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFiles(replace: true);

    // ------------------------------------------------------------------ buttons

    void Play_Click(object sender, RoutedEventArgs e) { if (!_player.HasMedia && _player.Queue.Count == 0) OpenFiles(true); else Do("play-pause"); }
    void Prev_Click(object sender, RoutedEventArgs e) => Do("prev");
    void Next_Click(object sender, RoutedEventArgs e) => Do("next");
    void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool IsWindowArranged(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")] static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("gdi32.dll")] static extern uint SetBkColor(IntPtr hdc, uint color);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
}
