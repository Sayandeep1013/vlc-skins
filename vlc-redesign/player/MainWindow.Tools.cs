using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using RedesignPlayer.Core;
using RedesignPlayer.UI;

namespace RedesignPlayer;

// VLC's Tools menu (Effects and Filters, Track Synchronization, Media/Codec Information,
// Messages, Plugins, Preferences) plus the Video/Audio/Playback menu items that go with it
// (zoom, crop, deinterlace, always on top, stereo mode, jump to time).
public partial class MainWindow
{
    EffectsEngine _fx = null!;
    EffectsState Fx => _settings.Effects;
    bool _audioFxApplied;

    // Per session, like VLC (crop, zoom and overlays aren't saved).
    string? _crop;
    readonly int[] _cropPx = new int[4]; // top, bottom, left, right
    float _zoom;
    bool _marqOn, _logoOn;
    string _marqText = "VLC Redesign";
    int _marqPos, _marqOpacity = 255, _marqSize, _logoPos, _logoOpacity = 255;
    string? _logoFile;

    void InitTools()
    {
        VlcVars.Init(_cfg.InstallDir);
        _fx = new EffectsEngine(_player.Mp, Fx, _cfg);
        SafeFx(_fx.ApplyAll); // sets the player-level values new outputs inherit
        // Outputs come and go with media: push the effects to each new one.
        _player.Mp.Playing += (_, _) => Dispatcher.BeginInvoke(() => { if (!_audioFxApplied) { _audioFxApplied = true; SafeFx(_fx.ApplyAudio); } });
        _player.Mp.Vout += (_, _) => Dispatcher.BeginInvoke(() => SafeFx(() => { _fx.ApplyVideo(); ApplySessionVideo(); }));
        _player.MediaChanged += () => _audioFxApplied = false;
        _player.LibVlc.Log += OnVlcLog;
        _logTimer.Tick += (_, _) => FlushLog();
        _infoTimer.Tick += (_, _) => { if (InfoPane.Visibility == Visibility.Visible && InfoTabStats.IsChecked == true) BuildInfo(); };
        _fxDebounce.Tick += (_, _) => { _fxDebounce.Stop(); var a = _fxPending; _fxPending = null; if (a != null) SafeFx(a); };
        Topmost = _settings.AlwaysOnTop;
    }

    static void SafeFx(Action a)
    {
        try { a(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    // Slider drags fire constantly; apply the latest value once things settle.
    readonly DispatcherTimer _fxDebounce = new() { Interval = TimeSpan.FromMilliseconds(70) };
    Action? _fxPending;
    void Debounced(Action a) { _fxPending = a; _fxDebounce.Stop(); _fxDebounce.Start(); }

    void ApplySessionVideo()
    {
        var mp = _player.Mp;
        mp.Scale = _zoom;
        mp.CropGeometry = CropGeometry();
        mp.SetMarqueeInt(VideoMarqueeOption.Enable, _marqOn ? 1 : 0);
        if (_marqOn)
        {
            mp.SetMarqueeString(VideoMarqueeOption.Text, _marqText);
            mp.SetMarqueeInt(VideoMarqueeOption.Position, _marqPos);
            mp.SetMarqueeInt(VideoMarqueeOption.Opacity, _marqOpacity);
            mp.SetMarqueeInt(VideoMarqueeOption.Size, _marqSize);
            mp.SetMarqueeInt(VideoMarqueeOption.Timeout, 0);
        }
        mp.SetLogoInt(VideoLogoOption.Enable, _logoOn && _logoFile != null ? 1 : 0);
        if (_logoOn && _logoFile != null)
        {
            mp.SetLogoString(VideoLogoOption.File, _logoFile);
            mp.SetLogoInt(VideoLogoOption.Position, _logoPos);
            mp.SetLogoInt(VideoLogoOption.Opacity, _logoOpacity);
        }
    }

    string? CropGeometry()
    {
        if (_cropPx.Any(v => v > 0))
        {
            uint w = 0, h = 0;
            if (_player.Mp.Size(0, ref w, ref h) && w > 0 && h > 0)
            {
                int cw = (int)w - _cropPx[2] - _cropPx[3], ch = (int)h - _cropPx[0] - _cropPx[1];
                if (cw > 16 && ch > 16) return $"{cw}x{ch}+{_cropPx[2]}+{_cropPx[0]}";
            }
        }
        return _crop;
    }

    // ------------------------------------------------------------------ opening the panels

    void OpenEffects(RadioButton tab) { tab.IsChecked = true; OpenSide(EffectsPane); BuildEffects(); }
    void OpenInfo(RadioButton tab) { tab.IsChecked = true; OpenSide(InfoPane); BuildInfo(); }
    void OpenLog() { LogTabMessages.IsChecked = true; OpenSide(LogPane); FlushLog(force: true); }

    void OpenAudioFx_Click(object sender, RoutedEventArgs e) => OpenEffects(FxTabAudio);
    void OpenVideoFx_Click(object sender, RoutedEventArgs e) => OpenEffects(FxTabVideo);

    void FxTab_Checked(object sender, RoutedEventArgs e) { if (FxHost != null && EffectsPane.Visibility == Visibility.Visible) BuildEffects(); }
    void InfoTab_Checked(object sender, RoutedEventArgs e) { if (InfoHost != null && InfoPane.Visibility == Visibility.Visible) BuildInfo(); }

    void LogTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LogText == null) return;
        bool msgs = LogTabMessages.IsChecked == true;
        LogText.Visibility = LogBar.Visibility = msgs ? Visibility.Visible : Visibility.Collapsed;
        ModulesScroll.Visibility = msgs ? Visibility.Collapsed : Visibility.Visible;
        if (!msgs) BuildModules();
    }

    void OpenPreferences()
    {
        // Preferences live in vlcrc, which this player reads; VLC's own dialog edits them.
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_cfg.InstallDir, "vlc.exe")) { UseShellExecute = false });
            ShowOsd("Opening VLC: Tools → Preferences. Restart VLC Redesign to apply changes.");
        }
        catch (Exception ex) { App.LogError(ex); ShowOsd("Couldn't start VLC"); }
    }

    // ------------------------------------------------------------------ menus

    MenuItem ToolsMenu()
    {
        var t = new MenuItem { Header = "Tools", Icon = MenuIcon("Tools") };
        t.Items.Add(Item("Effects and Filters", () => OpenEffects(FxTabAudio), "Ctrl+E", "Effects"));
        t.Items.Add(Item("Track Synchronization", () => OpenEffects(FxTabSync), "", "Sync"));
        t.Items.Add(Item("Media Information", () => OpenInfo(InfoTabGeneral), "Ctrl+I", "Info"));
        t.Items.Add(Item("Codec Information", () => OpenInfo(InfoTabCodec), "Ctrl+J", "Info"));
        t.Items.Add(Item("Messages", OpenLog, "Ctrl+M", "Messages"));
        t.Items.Add(Item("Plugins and extensions", () => { OpenLog(); LogTabModules.IsChecked = true; }, "", "Tools"));
        t.Items.Add(new Separator());
        t.Items.Add(Item("Preferences (VLC)…", OpenPreferences, "Ctrl+P", "Prefs"));
        return t;
    }

    IconView MenuIcon(string name) => new() { Data = (Geometry)FindResource("Icon." + name), Size = 16, Foreground = (Brush)FindResource("TextDim") };

    MenuItem VideoMenu()
    {
        var mp = _player.Mp;
        var v = new MenuItem { Header = "Video" };
        var zoom = new MenuItem { Header = "Zoom" };
        foreach (var z in EffectCatalog.Zooms) zoom.Items.Add(Item(z.Label, () => SetZoom(z.Scale), isChecked: Math.Abs(_zoom - z.Scale) < 0.001));
        v.Items.Add(zoom);
        var crop = new MenuItem { Header = "Crop" };
        foreach (var c in EffectCatalog.Crops) crop.Items.Add(Item(c.Label, () => SetCrop(c.Geometry, c.Label), isChecked: _crop == c.Geometry && _cropPx.All(p => p == 0)));
        v.Items.Add(crop);
        var deint = new MenuItem { Header = "Deinterlace" };
        foreach (var (val, label) in new[] { ("off", "Off"), ("auto", "Automatic"), ("on", "On") })
            deint.Items.Add(Item(label, () => { Fx.Deinterlace = val; SafeFx(_fx.ApplyDeinterlace); ShowOsd($"Deinterlace: {label}"); }, isChecked: Fx.Deinterlace == val));
        var mode = new MenuItem { Header = "Deinterlace mode" };
        foreach (var m in EffectCatalog.DeinterlaceModes)
            mode.Items.Add(Item(m, () => { Fx.DeinterlaceMode = m; SafeFx(_fx.ApplyDeinterlace); ShowOsd($"Deinterlace mode: {m}"); }, isChecked: Fx.DeinterlaceMode == m));
        v.Items.Add(deint);
        v.Items.Add(mode);
        v.Items.Add(new Separator());
        v.Items.Add(Item("Always on top", ToggleOnTop, "", "OnTop", isChecked: Topmost));
        v.Items.Add(Item("Video effects…", () => OpenEffects(FxTabVideo), "", "Effects"));
        return v;
    }

    MenuItem StereoMenu()
    {
        var s = new MenuItem { Header = "Stereo mode" };
        foreach (var m in EffectCatalog.StereoModes)
            s.Items.Add(Item(m.Label, () => SetStereo((int)m.Mode, m.Label), isChecked: Fx.StereoMode == (int)m.Mode));
        return s;
    }

    void SetZoom(float z) { _zoom = z; _player.Mp.Scale = z; ShowOsd(z == 0 ? "Zoom: fit to window" : $"Zoom: {z:0.##}×"); }

    void SetCrop(string? g, string label)
    {
        _crop = g;
        Array.Clear(_cropPx);
        _player.Mp.CropGeometry = g;
        ShowOsd($"Crop: {label}");
    }

    void SetStereo(int mode, string label)
    {
        Fx.StereoMode = mode;
        _player.Mp.SetChannel((AudioOutputChannel)mode);
        ShowOsd($"Stereo mode: {label}");
    }

    void ToggleOnTop()
    {
        Topmost = !Topmost;
        _settings.AlwaysOnTop = Topmost;
        ShowOsd(Topmost ? "Always on top: on" : "Always on top: off");
    }

    // ------------------------------------------------------------------ jump to time

    void ShowJumpBox()
    {
        if (!_player.HasMedia) return;
        JumpHint.Text = $"h:mm:ss, mm:ss or seconds  (now {Fmt.Time(_player.Mp.Time)} of {Fmt.Time(_player.Mp.Length)})";
        JumpInput.Text = Fmt.Time(_player.Mp.Time);
        JumpBox.Visibility = Visibility.Visible;
        ShowChrome();
        JumpInput.Focus();
        JumpInput.SelectAll();
    }

    static long? ParseTime(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        var parts = s.Split(':');
        if (parts.Length > 3 || parts.Any(p => !double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return null;
        double total = 0;
        foreach (var p in parts) total = total * 60 + double.Parse(p, CultureInfo.InvariantCulture);
        return (long)(total * 1000);
    }

    void DoJump()
    {
        if (ParseTime(JumpInput.Text) is not long ms) { JumpHint.Text = "Couldn't read that time. Try 1:23:45, 12:30 or 90."; return; }
        long len = _player.Mp.Length;
        if (len > 0) ms = Math.Clamp(ms, 0, len - 500);
        _player.Mp.Time = ms;
        JumpBox.Visibility = Visibility.Collapsed;
        ShowOsd($"Jumped to {Fmt.Time(ms)}");
    }

    void JumpInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DoJump(); e.Handled = true; }
        else if (e.Key == Key.Escape) { JumpBox.Visibility = Visibility.Collapsed; e.Handled = true; }
    }
    void JumpGo_Click(object sender, RoutedEventArgs e) => DoJump();
    void JumpCancel_Click(object sender, RoutedEventArgs e) => JumpBox.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ building blocks

    TextBlock Label(string text, double size = 12, string brush = "TextDim", Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, Foreground = (Brush)FindResource(brush), Margin = margin ?? new Thickness(2, 10, 0, 4), TextWrapping = TextWrapping.Wrap,
    };

    CheckBox Switch(bool on, Action<bool> changed)
    {
        var c = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        c.Click += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    Button Flat(string text, Action click, Thickness? margin = null)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("FlatButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = margin ?? new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Collapsible section: header (chevron, title, optional on/off switch) and a lazily built body.</summary>
    UIElement Section(string title, Func<UIElement> body, bool? on = null, Action<bool>? toggled = null, bool expanded = false, string? key = null)
    {
        key ??= title;
        expanded = _fxExpanded.TryGetValue(key, out var ex) ? ex : expanded;
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var head = new DockPanel();
        var hb = new ToggleButton { Style = (Style)FindResource("SectionHeader"), Content = title, IsChecked = expanded };
        if (on != null) { var sw = Switch(on.Value, v => toggled?.Invoke(v)); sw.Margin = new Thickness(8, 0, 6, 0); DockPanel.SetDock(sw, System.Windows.Controls.Dock.Right); head.Children.Add(sw); }
        head.Children.Add(hb);
        root.Children.Add(head);
        var holder = new Border { Padding = new Thickness(10, 0, 4, 8), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
        if (expanded) holder.Child = body();
        hb.Click += (_, _) =>
        {
            bool open = hb.IsChecked == true;
            _fxExpanded[key] = open;
            if (open && holder.Child == null) holder.Child = body();
            holder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        };
        root.Children.Add(holder);
        return root;
    }
    readonly Dictionary<string, bool> _fxExpanded = new();

    /// <summary>Labelled slider with the value shown on the right.</summary>
    UIElement SliderRow(string label, double min, double max, double value, double step, Func<double, string> fmt, Action<double> changed)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
        var top = new DockPanel();
        var val = new TextBlock { Text = fmt(value), FontSize = 12, Foreground = (Brush)FindResource("Text") };
        DockPanel.SetDock(val, System.Windows.Controls.Dock.Right);
        top.Children.Add(val);
        top.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("TextDim") });
        var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10, TickFrequency = step, IsSnapToTickEnabled = step >= 0.01 };
        s.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); changed(e.NewValue); };
        panel.Children.Add(top);
        panel.Children.Add(s);
        return panel;
    }

    UIElement ParamRow(Param p, double value, Action<double> changed)
    {
        if (p.Type == VlcVars.Int && p.Min == 0 && p.Max == 1)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
            var sw = Switch(value >= 0.5, v => changed(v ? 1 : 0));
            DockPanel.SetDock(sw, System.Windows.Controls.Dock.Right);
            row.Children.Add(sw);
            row.Children.Add(new TextBlock { Text = p.Label, FontSize = 12, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        return SliderRow(p.Label, p.Min, p.Max, value, p.Step, p.Format, changed);
    }

    StackPanel Options<T>(IEnumerable<(string Label, T Value)> items, T current, Action<T> pick, string group)
    {
        var sp = new StackPanel();
        foreach (var (label, v) in items)
            sp.Children.Add(Option(label, EqualityComparer<T>.Default.Equals(v, current), () => pick(v), group));
        return sp;
    }

    // ------------------------------------------------------------------ Effects and Filters

    void BuildEffects()
    {
        FxHost.Children.Clear();
        _updatingUi = true;
        try
        {
            if (FxTabAudio.IsChecked == true) BuildAudioFx();
            else if (FxTabVideo.IsChecked == true) BuildVideoFx();
            else BuildSync();
        }
        finally { _updatingUi = false; }
        if (!_player.HasMedia)
            FxHost.Children.Insert(0, Label("Settings apply as soon as something plays.", 12, "TextFaint", new Thickness(8, 4, 0, 6)));
    }

    // ---- audio

    void BuildAudioFx()
    {
        FxHost.Children.Add(Section("Equalizer", BuildEqualizer, Fx.EqOn, on => { Fx.EqOn = on; SafeFx(_fx.ApplyAudio); ShowOsd(on ? "Equalizer on" : "Equalizer off"); }, expanded: true));
        foreach (var f in EffectCatalog.AudioFilters)
        {
            var filter = f;
            FxHost.Children.Add(Section(f.Label, () => FilterBody(filter, audio: true), Fx.AudioOn.Contains(f.Module), on =>
            {
                if (on) Fx.AudioOn.Add(filter.Module); else Fx.AudioOn.Remove(filter.Module);
                SafeFx(_fx.ApplyAudio);
                ShowOsd($"{filter.Label} {(on ? "on" : "off")}");
            }));
        }
        FxHost.Children.Add(Section("Stereo mode", () => Options(EffectCatalog.StereoModes.Select(m => (m.Label, (int)m.Mode)), Fx.StereoMode,
            v => SetStereo(v, EffectCatalog.StereoModes.First(m => (int)m.Mode == v).Label), "stereo")));
        FxHost.Children.Add(Flat("Reset all audio effects", () =>
        {
            Fx.EqOn = false; Fx.AudioOn.Clear(); Fx.StereoMode = 1; Fx.Eq2Pass = false;
            foreach (var p in EffectCatalog.AudioFilters.SelectMany(f => f.Params)) Fx.Values.Remove(p.Var);
            ApplyEqPreset(0);
            SafeFx(_fx.ApplyAudio);
            BuildEffects();
            ShowOsd("Audio effects reset");
        }, new Thickness(8, 14, 0, 0)));
    }

    // Band labels follow the engine's equalizer-vlcfreqs. libVLC players use the ISO bands
    // (31 Hz…16 kHz) unless set otherwise; VLC's own window defaults to its classic set.
    string[] BandLabels => (VlcVars.GetInt(_player.Mp.NativeReference, "equalizer-vlcfreqs") ?? (_cfg.Get("equalizer-vlcfreqs", "1") == "0" ? 0 : 1)) == 0
        ? new[] { "31", "63", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" }
        : new[] { "60", "170", "310", "600", "1K", "3K", "6K", "12K", "14K", "16K" };

    void ApplyEqPreset(uint index)
    {
        using var eq = new Equalizer(index);
        using var names = new Equalizer();
        Fx.EqPreset = names.PresetName(index);
        Fx.EqPreamp = eq.Preamp;
        for (uint b = 0; b < 10; b++) Fx.EqBands[b] = eq.Amp(b);
    }

    UIElement BuildEqualizer()
    {
        var sp = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var presetBtn = new Button { Content = $"Preset: {Fx.EqPreset}  ▾", Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left };
        presetBtn.Click += (_, _) =>
        {
            var m = new ContextMenu();
            using var names = new Equalizer();
            for (uint i = 0; i < names.PresetCount; i++)
            {
                uint idx = i;
                string n = names.PresetName(i);
                m.Items.Add(Item(n, () =>
                {
                    ApplyEqPreset(idx);
                    if (!Fx.EqOn) Fx.EqOn = true;
                    SafeFx(_fx.ApplyAudio);
                    BuildEffects();
                    ShowOsd($"Equalizer: {n}");
                }, isChecked: n == Fx.EqPreset));
            }
            ShowMenu(m, presetBtn);
        };
        var twoPass = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        twoPass.Children.Add(new TextBlock { Text = "2 pass", FontSize = 12, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        twoPass.Children.Add(Switch(Fx.Eq2Pass, v => { Fx.Eq2Pass = v; SafeFx(_fx.SetEqualizer); }));
        DockPanel.SetDock(twoPass, System.Windows.Controls.Dock.Right);
        top.Children.Add(twoPass);
        top.Children.Add(presetBtn);
        sp.Children.Add(top);
        sp.Children.Add(SliderRow("Preamp", -20, 20, Fx.EqPreamp, 0.1, v => $"{v:+0.0;−0.0;0.0} dB", v =>
        {
            Fx.EqPreamp = (float)v; MarkCustom(presetBtn); Debounced(_fx.SetEqualizer);
        }));
        var bands = new UniformGrid { Columns = 10, Margin = new Thickness(0, 10, 0, 0) };
        var labels = BandLabels;
        for (int i = 0; i < 10; i++)
        {
            int b = i;
            var col = new StackPanel();
            var val = new TextBlock { Text = $"{Fx.EqBands[b]:0.#}", FontSize = 10.5, Foreground = (Brush)FindResource("Text"), HorizontalAlignment = HorizontalAlignment.Center };
            var s = new Slider { Style = (Style)FindResource("VSlider"), Minimum = -20, Maximum = 20, Value = Fx.EqBands[b], Height = 130, SmallChange = 0.1, LargeChange = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            s.ValueChanged += (_, e) =>
            {
                Fx.EqBands[b] = (float)Math.Round(e.NewValue, 1);
                val.Text = $"{Fx.EqBands[b]:0.#}";
                MarkCustom(presetBtn);
                Debounced(_fx.SetEqualizer);
            };
            col.Children.Add(val);
            col.Children.Add(s);
            col.Children.Add(new TextBlock { Text = labels[b], FontSize = 10, Foreground = (Brush)FindResource("TextFaint"), HorizontalAlignment = HorizontalAlignment.Center });
            bands.Children.Add(col);
        }
        sp.Children.Add(bands);
        sp.Children.Add(Label("Bands in dB; values in Hz.", 11, "TextFaint", new Thickness(2, 6, 0, 0)));
        return sp;
    }

    void MarkCustom(Button presetBtn)
    {
        if (_updatingUi) return;
        Fx.EqPreset = "Custom";
        presetBtn.Content = "Preset: Custom  ▾";
    }

    UIElement FilterBody(Filter f, bool audio)
    {
        var sp = new StackPanel();
        if (f.Hint != null) sp.Children.Add(Label(f.Hint, 11.5, "TextFaint", new Thickness(2, 0, 0, 4)));
        var rows = new StackPanel();
        void Fill()
        {
            rows.Children.Clear();
            foreach (var p in f.Params)
            {
                var param = p;
                rows.Children.Add(ParamRow(p, Fx.Get(p), v => Debounced(() => { if (audio) _fx.SetAudioParam(param, v); else _fx.SetVideoParam(param, v); })));
            }
        }
        Fill();
        sp.Children.Add(rows);
        if (f.Params.Length > 0)
            sp.Children.Add(Flat("Reset", () =>
            {
                foreach (var p in f.Params) Fx.Values.Remove(p.Var);
                SafeFx(audio ? _fx.ApplyAudio : _fx.ApplyVideo);
                _updatingUi = true; Fill(); _updatingUi = false;
            }));
        return sp;
    }

    // ---- video

    UIElement VideoFilterBlock(Filter f, Func<UIElement>? extra = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        var head = new DockPanel();
        var sw = Switch(Fx.VideoOn.Contains(f.Module), on =>
        {
            if (on) Fx.VideoOn.Add(f.Module); else Fx.VideoOn.Remove(f.Module);
            SafeFx(_fx.ApplyVideo);
            ShowOsd($"{f.Label} {(on ? "on" : "off")}");
        });
        DockPanel.SetDock(sw, System.Windows.Controls.Dock.Right);
        head.Children.Add(sw);
        head.Children.Add(new TextBlock { Text = f.Label, FontSize = 12.5, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        if (extra != null) panel.Children.Add(extra());
        if (f.Params.Length > 0 || f.Hint != null) panel.Children.Add(FilterBody(f, audio: false));
        return panel;
    }

    UIElement Stack(params UIElement[] items) { var sp = new StackPanel(); foreach (var i in items) sp.Children.Add(i); return sp; }

    void BuildVideoFx()
    {
        var C = EffectCatalog.VideoFilters.ToDictionary(f => f.Module);
        FxHost.Children.Add(Section("Essential", () => Stack(VideoFilterBlock(C["adjust"]), VideoFilterBlock(C["sharpen"]),
            VideoFilterBlock(C["gradfun"]), VideoFilterBlock(C["grain"])), expanded: true));
        FxHost.Children.Add(Section("Crop", BuildCrop));
        FxHost.Children.Add(Section("Zoom and aspect ratio", () => Stack(
            Label("Zoom"), Options(EffectCatalog.Zooms.Select(z => (z.Label, z.Scale)), _zoom, SetZoom, "zoom"),
            Label("Aspect ratio"), Options(Aspects.Select(a => (a.Label, a.Value)), _player.Mp.AspectRatio,
                v => { _player.Mp.AspectRatio = v; ShowOsd($"Aspect ratio: {Aspects.First(a => a.Value == v).Label}"); }, "fxaspect"))));
        FxHost.Children.Add(Section("Geometry", () => Stack(
            VideoFilterBlock(C["transform"], () => Options(EffectCatalog.Transforms.Select(t => (t.Label, t.Value)), Fx.Transform,
                v => { Fx.Transform = v; SafeFx(_fx.ApplyVideo); }, "transform")),
            VideoFilterBlock(C["rotate"]))));
        FxHost.Children.Add(Section("Colors", () => Stack(VideoFilterBlock(C["invert"]), VideoFilterBlock(C["sepia"]), VideoFilterBlock(C["posterize"]),
            VideoFilterBlock(C["gradient"]), VideoFilterBlock(C["extract"]), VideoFilterBlock(C["colorthres"]))));
        FxHost.Children.Add(Section("Overlay", BuildOverlay));
        FxHost.Children.Add(Section("Advanced", () => Stack(VideoFilterBlock(C["anaglyph"]), VideoFilterBlock(C["mirror"]), VideoFilterBlock(C["psychedelic"]),
            VideoFilterBlock(C["wave"]), VideoFilterBlock(C["ripple"]), VideoFilterBlock(C["motionblur"]), VideoFilterBlock(C["motiondetect"]),
            VideoFilterBlock(C["gaussianblur"]), VideoFilterBlock(C["hqdn3d"]), VideoFilterBlock(C["antiflicker"]))));
        FxHost.Children.Add(Section("Deinterlace", () => Stack(
            Options(new[] { ("Off", "off"), ("Automatic", "auto"), ("On", "on") }, Fx.Deinterlace, v => { Fx.Deinterlace = v; SafeFx(_fx.ApplyDeinterlace); }, "deint"),
            Label("Mode"),
            Options(EffectCatalog.DeinterlaceModes.Select(m => (m, m)), Fx.DeinterlaceMode, v => { Fx.DeinterlaceMode = v; SafeFx(_fx.ApplyDeinterlace); }, "deintmode"))));
        FxHost.Children.Add(Flat("Reset all video effects", () =>
        {
            Fx.VideoOn.Clear(); Fx.Transform = "90"; Fx.Deinterlace = "auto";
            foreach (var p in EffectCatalog.VideoFilters.SelectMany(f => f.Params)) Fx.Values.Remove(p.Var);
            _crop = null; Array.Clear(_cropPx); _zoom = 0; _marqOn = _logoOn = false; _player.Mp.AspectRatio = null;
            SafeFx(() => { _fx.ApplyVideo(); ApplySessionVideo(); });
            BuildEffects();
            ShowOsd("Video effects reset");
        }, new Thickness(8, 14, 0, 0)));
    }

    UIElement BuildCrop()
    {
        var sp = new StackPanel();
        sp.Children.Add(Label("Ratio"));
        sp.Children.Add(Options(EffectCatalog.Crops.Select(c => (c.Label, c.Geometry ?? "")), _cropPx.Any(p => p > 0) ? "-" : _crop ?? "",
            g => SetCrop(g.Length == 0 ? null : g, EffectCatalog.Crops.First(c => (c.Geometry ?? "") == g).Label), "crop"));
        uint w = 0, h = 0;
        _player.Mp.Size(0, ref w, ref h);
        sp.Children.Add(Label(w > 0 ? $"Custom, in pixels of the {w}×{h} video" : "Custom (in pixels; available while a video plays)"));
        string[] names = { "Top", "Bottom", "Left", "Right" };
        for (int i = 0; i < 4; i++)
        {
            int k = i;
            double max = Math.Max(1, (k < 2 ? h : w) / 2.0);
            sp.Children.Add(SliderRow(names[k], 0, max, _cropPx[k], 1, v => $"{v:0} px", v =>
            {
                _cropPx[k] = (int)v;
                Debounced(() => _player.Mp.CropGeometry = CropGeometry());
            }));
        }
        return sp;
    }

    static readonly (string Label, int Pos)[] Positions =
        { ("Center", 0), ("Top", 4), ("Bottom", 8), ("Left", 1), ("Right", 2), ("Top left", 5), ("Top right", 6), ("Bottom left", 9), ("Bottom right", 10) };

    UIElement BuildOverlay()
    {
        var sp = new StackPanel();
        // Marquee (VLC: Overlay → Add text)
        var mhead = new DockPanel();
        var msw = Switch(_marqOn, on => { _marqOn = on; SafeFx(ApplySessionVideo); });
        DockPanel.SetDock(msw, System.Windows.Controls.Dock.Right);
        mhead.Children.Add(msw);
        mhead.Children.Add(new TextBlock { Text = "Add text", FontSize = 12.5, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(mhead);
        var text = new TextBox { Style = (Style)FindResource("DarkText"), Text = _marqText, Margin = new Thickness(0, 6, 0, 0) };
        text.TextChanged += (_, _) => { _marqText = text.Text; if (_marqOn) Debounced(ApplySessionVideo); };
        sp.Children.Add(text);
        sp.Children.Add(SliderRow("Opacity", 0, 255, _marqOpacity, 1, v => $"{v / 2.55:0}%", v => { _marqOpacity = (int)v; if (_marqOn) Debounced(ApplySessionVideo); }));
        sp.Children.Add(SliderRow("Size", 0, 96, _marqSize, 1, v => v == 0 ? "Auto" : $"{v:0} px", v => { _marqSize = (int)v; if (_marqOn) Debounced(ApplySessionVideo); }));
        sp.Children.Add(Label("Position"));
        sp.Children.Add(Options(Positions.Select(p => (p.Label, p.Pos)), _marqPos, v => { _marqPos = v; if (_marqOn) SafeFx(ApplySessionVideo); }, "marqpos"));

        // Logo (VLC: Overlay → Add logo)
        var lhead = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var lsw = Switch(_logoOn, on => { _logoOn = on; if (on && _logoFile == null) PickLogo(); SafeFx(ApplySessionVideo); });
        DockPanel.SetDock(lsw, System.Windows.Controls.Dock.Right);
        lhead.Children.Add(lsw);
        lhead.Children.Add(new TextBlock { Text = "Add logo", FontSize = 12.5, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(lhead);
        sp.Children.Add(Flat(_logoFile == null ? "Choose image…" : Path.GetFileName(_logoFile), () => { PickLogo(); SafeFx(ApplySessionVideo); BuildEffects(); }));
        sp.Children.Add(SliderRow("Opacity", 0, 255, _logoOpacity, 1, v => $"{v / 2.55:0}%", v => { _logoOpacity = (int)v; if (_logoOn) Debounced(ApplySessionVideo); }));
        sp.Children.Add(Label("Position"));
        sp.Children.Add(Options(Positions.Select(p => (p.Label, p.Pos)), _logoPos, v => { _logoPos = v; if (_logoOn) SafeFx(ApplySessionVideo); }, "logopos"));
        return sp;
    }

    void PickLogo()
    {
        var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", Title = "Choose a logo image" };
        if (d.ShowDialog(this) == true) _logoFile = d.FileName;
    }

    // ---- synchronization

    void BuildSync()
    {
        var mp = _player.Mp;
        FxHost.Children.Add(Label("Audio track synchronization", 12.5, "Text", new Thickness(4, 6, 0, 0)));
        FxHost.Children.Add(Label("Positive values delay the audio (play it later).", 11.5, "TextFaint", new Thickness(4, 0, 0, 0)));
        FxHost.Children.Add(SliderRow("Audio delay", -10, 10, mp.AudioDelay / 1e6, 0.05, v => $"{v:+0.00;−0.00;0.00} s", v => Debounced(() => SetAudioDelay((long)Math.Round(v * 1000)))));
        FxHost.Children.Add(Label("Subtitle track synchronization", 12.5, "Text", new Thickness(4, 16, 0, 0)));
        FxHost.Children.Add(Label("Positive values delay the subtitles.", 11.5, "TextFaint", new Thickness(4, 0, 0, 0)));
        FxHost.Children.Add(SliderRow("Subtitle delay", -30, 30, mp.SpuDelay / 1e6, 0.05, v => $"{v:+0.00;−0.00;0.00} s", v => Debounced(() => SetSubDelay((long)Math.Round(v * 1000)))));
        FxHost.Children.Add(Flat("Reset", () => { SetAudioDelay(0); SetSubDelay(0); BuildEffects(); }, new Thickness(4, 12, 0, 0)));
    }

    // ------------------------------------------------------------------ Media / Codec information

    readonly DispatcherTimer _infoTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    void BuildInfo()
    {
        InfoHost.Children.Clear();
        _infoTimer.Stop();
        var media = _player.Mp.Media;
        if (!_player.HasMedia || media == null) { InfoHost.Children.Add(Label("Nothing is playing.", 12.5, "TextFaint")); return; }
        if (InfoTabGeneral.IsChecked == true) BuildGeneral(media);
        else if (InfoTabCodec.IsChecked == true) BuildCodec(media);
        else { BuildStats(media); _infoTimer.Start(); }
    }

    static readonly (MetadataType Type, string Label)[] EditableMeta =
    {
        (MetadataType.Title, "Title"), (MetadataType.Artist, "Artist"), (MetadataType.Album, "Album"), (MetadataType.Genre, "Genre"),
        (MetadataType.Date, "Date"), (MetadataType.TrackNumber, "Track number"), (MetadataType.Description, "Comments"),
        (MetadataType.Copyright, "Copyright"), (MetadataType.Publisher, "Publisher"), (MetadataType.EncodedBy, "Encoded by"), (MetadataType.Language, "Language"),
    };

    void BuildGeneral(Media media)
    {
        var boxes = new List<(MetadataType, TextBox)>();
        foreach (var (type, label) in EditableMeta)
        {
            InfoHost.Children.Add(Label(label, 11.5, "TextDim", new Thickness(2, 8, 0, 3)));
            var tb = new TextBox { Style = (Style)FindResource("DarkText"), Text = media.Meta(type) ?? "" };
            boxes.Add((type, tb));
            InfoHost.Children.Add(tb);
        }
        var status = Label("", 11.5, "TextFaint", new Thickness(2, 6, 0, 0));
        var save = Flat("Save metadata", () =>
        {
            foreach (var (type, tb) in boxes)
                if ((media.Meta(type) ?? "") != tb.Text) media.SetMeta(type, tb.Text);
            bool ok = media.SaveMeta();
            status.Text = ok ? "Saved to the file." : "Couldn't save (the file may be read-only or the format may not support tags).";
        }, new Thickness(0, 14, 0, 0));
        InfoHost.Children.Add(save);
        InfoHost.Children.Add(status);
        InfoHost.Children.Add(Label("Location", 11.5, "TextDim", new Thickness(2, 16, 0, 3)));
        InfoHost.Children.Add(new TextBox { Style = (Style)FindResource("DarkText"), Text = Uri.UnescapeDataString(media.Mrl), IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        string? art = media.Meta(MetadataType.ArtworkURL), playing = media.Meta(MetadataType.NowPlaying);
        if (!string.IsNullOrEmpty(playing)) InfoHost.Children.Add(Label($"Now playing: {playing}", 12, "Text"));
        if (!string.IsNullOrEmpty(art)) InfoHost.Children.Add(Label($"Artwork: {Uri.UnescapeDataString(art)}", 11, "TextFaint"));
    }

    static string FourCC(uint c) => c == 0 ? "" : new string(new[] { (char)(c & 0xFF), (char)((c >> 8) & 0xFF), (char)((c >> 16) & 0xFF), (char)(c >> 24) }).Trim('\0', ' ');

    void BuildCodec(Media media)
    {
        var mp = _player.Mp;
        var tracks = media.Tracks;
        if (tracks.Length == 0) { InfoHost.Children.Add(Label("No stream information yet.", 12.5, "TextFaint")); return; }
        int n = 0;
        foreach (var t in tracks)
        {
            bool current = t.TrackType switch
            {
                TrackType.Audio => mp.AudioTrack == t.Id, TrackType.Video => mp.VideoTrack == t.Id, TrackType.Text => mp.Spu == t.Id, _ => false,
            };
            string kind = t.TrackType switch { TrackType.Audio => "Audio", TrackType.Video => "Video", TrackType.Text => "Subtitle", _ => "Other" };
            InfoHost.Children.Add(Label($"Stream {n++}  ·  {kind}{(current ? "  ·  playing" : "")}", 12.5, current ? "AccentHi" : "Text", new Thickness(2, n == 1 ? 4 : 16, 0, 4)));
            var rows = new List<(string, string)>();
            string desc = media.CodecDescription(t.TrackType, t.Codec);
            rows.Add(("Codec", $"{desc} ({FourCC(t.Codec)})".Replace(" ()", "")));
            if (!string.IsNullOrEmpty(t.Language)) rows.Add(("Language", t.Language));
            if (!string.IsNullOrEmpty(t.Description)) rows.Add(("Description", t.Description));
            if (t.TrackType == TrackType.Video)
            {
                var v = t.Data.Video;
                rows.Add(("Video resolution", $"{v.Width}×{v.Height}"));
                if (v.FrameRateDen > 0) rows.Add(("Frame rate", $"{(double)v.FrameRateNum / v.FrameRateDen:0.###} fps"));
                if (v.SarNum > 0 && v.SarDen > 0 && v.SarNum != v.SarDen) rows.Add(("Sample aspect", $"{v.SarNum}:{v.SarDen}"));
                if (v.Orientation != VideoOrientation.TopLeft) rows.Add(("Orientation", v.Orientation.ToString()));
            }
            else if (t.TrackType == TrackType.Audio)
            {
                var a = t.Data.Audio;
                rows.Add(("Channels", a.Channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", _ => a.Channels.ToString() }));
                rows.Add(("Sample rate", $"{a.Rate} Hz"));
            }
            else if (t.TrackType == TrackType.Text && !string.IsNullOrEmpty(t.Data.Subtitle.Encoding)) rows.Add(("Encoding", t.Data.Subtitle.Encoding));
            if (t.Bitrate > 0) rows.Add(("Bitrate", $"{t.Bitrate / 1000} kb/s"));
            foreach (var (k, val) in rows) InfoHost.Children.Add(KeyValue(k, val));
        }
    }

    UIElement KeyValue(string k, string v)
    {
        var row = new DockPanel { Margin = new Thickness(2, 3, 0, 3) };
        var key = new TextBlock { Text = k, Width = 165, FontSize = 12, Foreground = (Brush)FindResource("TextDim") };
        DockPanel.SetDock(key, System.Windows.Controls.Dock.Left);
        row.Children.Add(key);
        row.Children.Add(new TextBlock { Text = v, FontSize = 12, Foreground = (Brush)FindResource("Text"), TextWrapping = TextWrapping.Wrap });
        return row;
    }

    void BuildStats(Media media)
    {
        var s = media.Statistics;
        void Head(string t) => InfoHost.Children.Add(Label(t, 12.5, "Text", new Thickness(2, InfoHost.Children.Count == 0 ? 4 : 16, 0, 4)));
        // libVLC reports bitrates in bytes per microsecond-ish units; VLC shows ×8000 as kb/s.
        Head("Input / read");
        InfoHost.Children.Add(KeyValue("Media data size", $"{s.ReadBytes / 1024:N0} KiB"));
        InfoHost.Children.Add(KeyValue("Input bitrate", $"{s.InputBitrate * 8000:N0} kb/s"));
        InfoHost.Children.Add(KeyValue("Demuxed data size", $"{s.DemuxReadBytes / 1024:N0} KiB"));
        InfoHost.Children.Add(KeyValue("Content bitrate", $"{s.DemuxBitrate * 8000:N0} kb/s"));
        InfoHost.Children.Add(KeyValue("Discarded (corrupted)", $"{s.DemuxCorrupted}"));
        InfoHost.Children.Add(KeyValue("Dropped (discontinued)", $"{s.DemuxDiscontinuity}"));
        Head("Video");
        InfoHost.Children.Add(KeyValue("Decoded", $"{s.DecodedVideo:N0} blocks"));
        InfoHost.Children.Add(KeyValue("Displayed", $"{s.DisplayedPictures:N0} frames"));
        InfoHost.Children.Add(KeyValue("Lost", $"{s.LostPictures:N0} frames"));
        Head("Audio");
        InfoHost.Children.Add(KeyValue("Decoded", $"{s.DecodedAudio:N0} blocks"));
        InfoHost.Children.Add(KeyValue("Played", $"{s.PlayedAudioBuffers:N0} buffers"));
        InfoHost.Children.Add(KeyValue("Lost", $"{s.LostAudioBuffers:N0} buffers"));
    }

    // ------------------------------------------------------------------ Messages and Plugins

    readonly object _logLock = new();
    readonly LinkedList<(LogLevel Level, string Text)> _log = new();
    bool _logDirty;
    int _logVerbosity = 1; // 0 errors, 1 warnings, 2 debug (VLC's -v levels)
    readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    void OnVlcLog(object? sender, LogEventArgs e)
    {
        string level = e.Level switch { LogLevel.Error => "error", LogLevel.Warning => "warning", LogLevel.Notice => "info", _ => "debug" };
        var line = $"{e.Module} {level}: {e.Message}";
        lock (_logLock)
        {
            _log.AddLast((e.Level, line));
            while (_log.Count > 4000) _log.RemoveFirst();
            _logDirty = true;
        }
    }

    bool Shown(LogLevel l) => _logVerbosity switch { 0 => l == LogLevel.Error, 1 => l is LogLevel.Error or LogLevel.Warning, _ => true };

    void FlushLog(bool force = false)
    {
        if (LogPane.Visibility != Visibility.Visible) { _logTimer.Stop(); return; }
        _logTimer.Start();
        string text;
        lock (_logLock)
        {
            if (!_logDirty && !force) return;
            _logDirty = false;
            var sb = new StringBuilder();
            foreach (var (l, t) in _log) if (Shown(l)) sb.AppendLine(t);
            text = sb.ToString();
        }
        bool atEnd = LogText.VerticalOffset + LogText.ViewportHeight >= LogText.ExtentHeight - 4;
        LogText.Text = text.Length == 0 ? "(no messages at this verbosity yet)" : text;
        if (atEnd || force) LogText.ScrollToEnd();
    }

    void LogLevel_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        foreach (var (lvl, name) in new[] { (0, "Errors"), (1, "Warnings"), (2, "Debug") })
            m.Items.Add(Item(name, () => SetLogVerbosity(lvl), isChecked: _logVerbosity == lvl));
        ShowMenu(m, LogLevelButton);
    }

    void SetLogVerbosity(int lvl)
    {
        _logVerbosity = lvl;
        LogLevelButton.Content = "Verbosity: " + (lvl switch { 0 => "Errors", 1 => "Warnings", _ => "Debug" });
        FlushLog(force: true);
    }

    void LogCopy_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(LogText.Text); ShowOsd("Messages copied"); } catch { } }
    void LogClear_Click(object sender, RoutedEventArgs e) { lock (_logLock) _log.Clear(); FlushLog(force: true); }

    void BuildModules()
    {
        ModulesHost.Children.Clear();
        void List(string title, IEnumerable<ModuleDescription> mods)
        {
            ModulesHost.Children.Add(Label(title, 12.5, "Text", new Thickness(2, ModulesHost.Children.Count == 0 ? 4 : 16, 0, 4)));
            foreach (var m in mods.OrderBy(m => m.Name))
                ModulesHost.Children.Add(KeyValue(m.Name, string.IsNullOrEmpty(m.LongName) ? m.ShortName : m.LongName));
        }
        var lib = _player.LibVlc;
        List($"Audio filters ({lib.AudioFilters.Length})", lib.AudioFilters);
        List($"Video filters ({lib.VideoFilters.Length})", lib.VideoFilters);
        ModulesHost.Children.Add(Label($"Audio outputs ({lib.AudioOutputs.Length})", 12.5, "Text", new Thickness(2, 16, 0, 4)));
        foreach (var o in lib.AudioOutputs) ModulesHost.Children.Add(KeyValue(o.Name, o.Description));
        ModulesHost.Children.Add(Label($"From {_cfg.InstallDir} · libVLC {lib.Version}", 11, "TextFaint", new Thickness(2, 14, 0, 0)));
    }
}
