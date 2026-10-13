using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using RedesignPlayer.Core;
using RedesignPlayer.UI;

namespace RedesignPlayer;

// The Effects and Filters panel (VLC: Tools → Effects and Filters), designed as cards:
// each effect is a card with a live one-line summary and an on/off switch; choices are chips,
// the color/style filters a tile grid, sliders fill from their default. Everything applies
// live while you drag, and the panel updates in place rather than being rebuilt.
public partial class MainWindow
{
    // ------------------------------------------------------------------ live application

    // Throttle (not debounce): apply right away, then at most once per interval while a value
    // keeps changing, and once more with the final value. Dragging feels live this way.
    readonly Dictionary<string, (DispatcherTimer Timer, Action? Pending)> _throttles = new();

    void Throttled(string key, int ms, Action a)
    {
        if (!_throttles.TryGetValue(key, out var t))
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                var cur = _throttles[key];
                if (cur.Pending is { } p) { _throttles[key] = (cur.Timer, null); SafeFx(p); }
                else cur.Timer.Stop();
            };
            t = (timer, null);
            _throttles[key] = t;
        }
        if (!t.Timer.IsEnabled) { SafeFx(a); t.Timer.Start(); }
        else _throttles[key] = (t.Timer, a);
    }

    void AudioParam(Param p, double v) => Throttled("a:" + p.Var, 30, () => _fx.SetAudioParam(p, v));

    // Live where VLC allows it (next frame); otherwise the chain is rebuilt, a little less often.
    void VideoParam(Param p, double v) => Throttled("v:" + p.Var, 30, () =>
    {
        if (!_fx.SetVideoParam(p, v)) Throttled("chain", 140, _fx.RebuildVideoChain);
    });

    // ------------------------------------------------------------------ panel shell

    readonly Dictionary<string, bool> _fxExpanded = new();
    string _fxBuiltTab = "";

    void BuildEffects()
    {
        string tab = FxTabAudio.IsChecked == true ? "audio" : FxTabVideo.IsChecked == true ? "video" : "sync";
        double keep = tab == _fxBuiltTab ? FxScroll.VerticalOffset : 0;
        _fxBuiltTab = tab;
        FxHost.Children.Clear();
        if (!_player.HasMedia)
            FxHost.Children.Add(Note("Nothing is playing. Changes are kept and apply when something plays."));
        if (tab == "audio") BuildAudioFx();
        else if (tab == "video") BuildVideoFx();
        else BuildSync();
        UpdateFxTabs();
        Dispatcher.BeginInvoke(() => FxScroll.ScrollToVerticalOffset(keep), DispatcherPriority.Loaded);
    }

    // Tabs show how many effects are on, so nothing active hides behind another tab.
    void UpdateFxTabs()
    {
        int audio = (Fx.EqOn ? 1 : 0) + Fx.AudioOn.Count + (Fx.StereoMode != 1 ? 1 : 0);
        int video = Fx.VideoOn.Count + (_zoom != 0 ? 1 : 0) + (_crop != null || _cropPx.Any(p => p > 0) ? 1 : 0) + (_marqOn ? 1 : 0) + (_logoOn ? 1 : 0)
                    + (_player.Mp.AspectRatio != null ? 1 : 0);
        long ad = _player.Mp.AudioDelay, sd = _player.Mp.SpuDelay;
        int sync = (ad != 0 ? 1 : 0) + (sd != 0 ? 1 : 0);
        FxTabAudio.Content = TabLabel("Audio", audio);
        FxTabVideo.Content = TabLabel("Video", video);
        FxTabSync.Content = TabLabel("Sync", sync);
    }

    static object TabLabel(string name, int count)
    {
        if (count == 0) return name;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = name });
        sp.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x22, 0x10)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x70)) },
        });
        return sp;
    }

    // ------------------------------------------------------------------ building blocks

    TextBlock Label(string text, double size = 12, string brush = "TextDim", Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, Foreground = (Brush)FindResource(brush), Margin = margin ?? new Thickness(2, 10, 0, 4), TextWrapping = TextWrapping.Wrap,
    };

    TextBlock Note(string text) => Label(text, 11.5, "TextFaint", new Thickness(4, 2, 4, 10));

    TextBlock Group(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextFaint"),
        Margin = new Thickness(6, 14, 0, 6),
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

    Button TextButton(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("TextButton"), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A card: title, live summary, optional switch, and a body that expands.</summary>
    sealed class FxCard
    {
        public required TextBlock Summary;
        public required Func<string> SummaryText;
        public Func<bool> IsOn = () => true;
        public Action<bool>? SetOn;
        public CheckBox? Switch;
        public readonly List<FxSlider> Sliders = new();
        public readonly List<Action> Resets = new();
        public void Refresh()
        {
            bool on = IsOn();
            Summary.Text = SummaryText();
            foreach (var s in Sliders) s.Active = on;
        }
    }

    UIElement Card(string key, string title, Func<string> summary, Action<FxCard, StackPanel> fill,
        Func<bool>? isOn = null, Action<bool>? setOn = null, bool expanded = false)
    {
        expanded = _fxExpanded.TryGetValue(key, out var ex) ? ex : expanded;
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel();
        root.Child = stack;

        var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("IconHi") });
        var sum = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("TextFaint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        titles.Children.Add(sum);
        head.Children.Add(titles);
        var chev = new IconView { Data = (Geometry)FindResource("Icon.Chevron"), Size = 13, Foreground = (Brush)FindResource("TextFaint"),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(expanded ? 90 : 0), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chev, 1);
        head.Children.Add(chev);

        var card = new FxCard { Summary = sum, SummaryText = summary };
        if (isOn != null) card.IsOn = isOn;
        var body = new StackPanel { Margin = new Thickness(0, 10, 0, 2), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
        bool filled = false;
        void Expand(bool open)
        {
            _fxExpanded[key] = open;
            if (open && !filled) { filled = true; fill(card, body); card.Refresh(); }
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ((RotateTransform)chev.RenderTransform).Angle = open ? 90 : 0;
        }
        if (setOn != null && isOn != null)
        {
            var sw = Switch(isOn(), v =>
            {
                setOn(v);
                if (v && body.Visibility != Visibility.Visible) Expand(true);
                card.Refresh();
                UpdateFxTabs();
            });
            Grid.SetColumn(sw, 2);
            head.Children.Add(sw);
            card.Switch = sw;
            card.SetOn = setOn;
        }
        head.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox && !IsInside<CheckBox>(e.OriginalSource)) Expand(body.Visibility != Visibility.Visible); };
        stack.Children.Add(head);
        stack.Children.Add(body);
        if (expanded) { filled = true; fill(card, body); }
        card.Refresh();
        return root;
    }

    // Moving a setting on an effect that's off switches it on (otherwise nothing would happen).
    void EnsureOn(FxCard c)
    {
        if (c.IsOn() || c.SetOn == null) return;
        c.SetOn(true);
        if (c.Switch != null) c.Switch.IsChecked = true;
        c.Refresh();
        UpdateFxTabs();
    }

    static bool IsInside<T>(object src) where T : DependencyObject
    {
        for (var d = src as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d)) if (d is T) return true;
        return false;
    }

    /// <summary>Label, value pill and slider for one parameter. Registers itself with the card.</summary>
    UIElement ParamRow(FxCard card, Param p, double value, Action<double> apply)
    {
        if (p.Type == VlcVars.Int && p.Min == 0 && p.Max == 1)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var sw = Switch(value >= 0.5, v => { apply(v ? 1 : 0); card.Refresh(); });
            DockPanel.SetDock(sw, System.Windows.Controls.Dock.Right);
            row.Children.Add(sw);
            row.Children.Add(new TextBlock { Text = p.Label, FontSize = 12, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center });
            card.Resets.Add(() => { sw.IsChecked = p.Default >= 0.5; apply(p.Default); });
            return row;
        }
        return SliderRow(card, p.Label, p.Min, p.Max, p.Default, p.Step, value, p.Format, apply);
    }

    UIElement SliderRow(FxCard? card, string label, double min, double max, double origin, double step, double value, Func<double, string> fmt, Action<double> apply)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        var top = new DockPanel();
        var pill = new TextBlock { Text = fmt(value), FontSize = 11.5, Foreground = (Brush)FindResource("Text") };
        var pillBox = new Border { Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Child = pill };
        DockPanel.SetDock(pillBox, System.Windows.Controls.Dock.Right);
        top.Children.Add(pillBox);
        top.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center });
        var s = new FxSlider { Minimum = min, Maximum = max, Origin = origin, Step = step, Value = value, Margin = new Thickness(-9, 2, -9, 0) };
        s.Changed += v => { pill.Text = fmt(v); apply(v); card?.Refresh(); };
        panel.Children.Add(top);
        panel.Children.Add(s);
        if (card != null)
        {
            card.Sliders.Add(s);
            card.Resets.Add(() => { s.Value = origin; pill.Text = fmt(origin); apply(origin); });
        }
        return panel;
    }

    /// <summary>A row of chips where exactly one is selected.</summary>
    WrapPanel Chips<T>(IEnumerable<(string Label, T Value)> items, T current, Action<T> pick)
    {
        var wrap = new WrapPanel();
        var buttons = new List<(ToggleButton B, T V)>();
        foreach (var (label, v) in items)
        {
            var b = new ToggleButton { Style = (Style)FindResource("Chip"), Content = label, IsChecked = EqualityComparer<T>.Default.Equals(v, current) };
            var value = v;
            b.Click += (_, _) => { foreach (var (o, ov) in buttons) o.IsChecked = EqualityComparer<T>.Default.Equals(ov, value); pick(value); };
            buttons.Add((b, v));
            wrap.Children.Add(b);
        }
        return wrap;
    }

    UIElement ResetLink(FxCard card, Action? after = null)
    {
        var b = TextButton("Reset to defaults", () => { foreach (var r in card.Resets) r(); after?.Invoke(); card.Refresh(); });
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.Margin = new Thickness(0, 4, -4, 0);
        return b;
    }

    // "Ratio 12:1 · Threshold −8 dB", only for what differs from the default.
    string Changed(Filter f, bool on)
    {
        if (!on) return "Off";
        var diff = f.Params.Where(p => Math.Abs(Fx.Get(p) - p.Default) > p.Step / 2).Select(p => $"{p.Label} {p.Format(Fx.Get(p))}").ToList();
        return diff.Count == 0 ? (f.Params.Length == 0 ? "On" : "On · default settings") : string.Join(" · ", diff);
    }

    // ------------------------------------------------------------------ audio tab

    void BuildAudioFx()
    {
        FxHost.Children.Add(Card("eq", "Equalizer", () => Fx.EqOn ? $"{Fx.EqPreset} · preamp {Fx.EqPreamp:+0.#;−0.#;0} dB{(Fx.Eq2Pass ? " · 2-pass" : "")}" : "Off",
            FillEqualizer, () => Fx.EqOn, on => { Fx.EqOn = on; SafeFx(_fx.ApplyAudio); }, expanded: true));

        FxHost.Children.Add(Group("Dynamics and space"));
        foreach (var f in EffectCatalog.AudioFilters)
        {
            var filter = f;
            FxHost.Children.Add(Card("a:" + f.Module, f.Label, () => Changed(filter, Fx.AudioOn.Contains(filter.Module)), (card, body) =>
            {
                if (filter.Hint != null) body.Children.Add(Note(filter.Hint));
                foreach (var p in filter.Params) body.Children.Add(ParamRow(card, p, Fx.Get(p), v => { AudioParam(p, v); EnsureOn(card); }));
                body.Children.Add(ResetLink(card));
            }, () => Fx.AudioOn.Contains(filter.Module), on =>
            {
                if (on) Fx.AudioOn.Add(filter.Module); else Fx.AudioOn.Remove(filter.Module);
                SafeFx(_fx.ApplyAudio);
            }));
        }

        FxHost.Children.Add(Group("Output"));
        FxHost.Children.Add(Card("stereo", "Stereo mode", () => EffectCatalog.StereoModes.FirstOrDefault(m => (int)m.Mode == Fx.StereoMode).Label ?? "Stereo", (card, body) =>
            body.Children.Add(Chips(EffectCatalog.StereoModes.Select(m => (m.Label, (int)m.Mode)), Fx.StereoMode, v =>
            {
                SetStereo(v, EffectCatalog.StereoModes.First(m => (int)m.Mode == v).Label);
                card.Refresh(); UpdateFxTabs();
            }))));

        FxHost.Children.Add(Footer("Reset all audio effects", () =>
        {
            Fx.EqOn = false; Fx.AudioOn.Clear(); Fx.StereoMode = 1; Fx.Eq2Pass = false;
            foreach (var p in EffectCatalog.AudioFilters.SelectMany(f => f.Params)) Fx.Values.Remove(p.Var);
            ApplyEqPreset(0);
            SafeFx(_fx.ApplyAudio);
            _player.Mp.SetChannel(AudioOutputChannel.Stereo);
            BuildEffects();
            ShowOsd("Audio effects reset");
        }));
    }

    UIElement Footer(string text, Action click)
    {
        var b = TextButton(text, click);
        b.HorizontalAlignment = HorizontalAlignment.Center;
        b.Margin = new Thickness(0, 10, 0, 6);
        return b;
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
        Fx.EqPreset = names.PresetName(index) ?? "Preset";
        Fx.EqPreamp = eq.Preamp;
        for (uint b = 0; b < 10; b++) Fx.EqBands[b] = eq.Amp(b);
    }

    void FillEqualizer(FxCard card, StackPanel body)
    {
        var bands = new FxSlider[10];
        var values = new TextBlock[10];
        FxSlider? preamp = null;
        TextBlock? preampPill = null;

        void ShowValues()
        {
            for (int i = 0; i < 10; i++) { bands[i].Value = Fx.EqBands[i]; values[i].Text = Band(Fx.EqBands[i]); }
            if (preamp != null) { preamp.Value = Fx.EqPreamp; preampPill!.Text = Db(Fx.EqPreamp); }
            card.Refresh();
        }

        // Preset picker + 2-pass on one line.
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var two = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        two.Children.Add(new TextBlock { Text = "2-pass", FontSize = 12, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        two.Children.Add(Switch(Fx.Eq2Pass, v => { Fx.Eq2Pass = v; Throttled("eq", 40, _fx.SetEqualizer); card.Refresh(); }));
        DockPanel.SetDock(two, System.Windows.Controls.Dock.Right);
        top.Children.Add(two);
        var preset = new Button { Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left };
        void PresetText() => preset.Content = $"{Fx.EqPreset}  ▾";
        PresetText();
        preset.Click += (_, _) =>
        {
            var m = new ContextMenu();
            using var names = new Equalizer();
            for (uint i = 0; i < names.PresetCount; i++)
            {
                uint idx = i;
                string n = names.PresetName(i) ?? $"Preset {i + 1}";
                m.Items.Add(Item(n, () =>
                {
                    ApplyEqPreset(idx);
                    PresetText();
                    if (!Fx.EqOn) { Fx.EqOn = true; SafeFx(_fx.ApplyAudio); BuildEffects(); return; }
                    SafeFx(_fx.SetEqualizer);
                    ShowValues();
                }, isChecked: n == Fx.EqPreset));
            }
            ShowMenu(m, preset);
        };
        top.Children.Add(preset);
        body.Children.Add(top);

        void Custom() { if (Fx.EqPreset != "Custom") { Fx.EqPreset = "Custom"; PresetText(); } }

        // Preamp.
        var pre = SliderRow(card, "Preamp", -20, 20, 0, 0.1, Fx.EqPreamp, Db, v => { Fx.EqPreamp = (float)v; Custom(); Throttled("eq", 40, _fx.SetEqualizer); });
        body.Children.Add(pre);
        preamp = card.Sliders[^1];
        preampPill = (TextBlock)((Border)((DockPanel)((StackPanel)pre).Children[0]).Children[0]).Child;

        // Bands: centred sliders on a zero line.
        var grid = new UniformGrid { Columns = 10, Margin = new Thickness(-4, 8, -4, 0) };
        var labels = BandLabels;
        for (int i = 0; i < 10; i++)
        {
            int b = i;
            var col = new StackPanel();
            values[b] = new TextBlock { Text = Band(Fx.EqBands[b]), FontSize = 10.5, Foreground = (Brush)FindResource("Text"), HorizontalAlignment = HorizontalAlignment.Center };
            bands[b] = new FxSlider { Orientation = Orientation.Vertical, Minimum = -20, Maximum = 20, Origin = 0, Step = 0.1, Value = Fx.EqBands[b], Height = 132, HorizontalAlignment = HorizontalAlignment.Center };
            bands[b].Changed += v => { Fx.EqBands[b] = (float)Math.Round(v, 1); values[b].Text = Band(v); Custom(); Throttled("eq", 40, _fx.SetEqualizer); EnsureOn(card); card.Refresh(); };
            card.Sliders.Add(bands[b]);
            col.Children.Add(values[b]);
            col.Children.Add(bands[b]);
            col.Children.Add(new TextBlock { Text = labels[b], FontSize = 10, Foreground = (Brush)FindResource("TextFaint"), HorizontalAlignment = HorizontalAlignment.Center });
            grid.Children.Add(col);
        }
        body.Children.Add(grid);
        var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var reset = TextButton("Flat", () => { ApplyEqPreset(0); PresetText(); SafeFx(_fx.SetEqualizer); ShowValues(); });
        DockPanel.SetDock(reset, System.Windows.Controls.Dock.Right);
        foot.Children.Add(reset);
        foot.Children.Add(new TextBlock { Text = "dB per band · Hz", FontSize = 11, Foreground = (Brush)FindResource("TextFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
        body.Children.Add(foot);

        static string Band(double v) => Math.Abs(v) < 0.05 ? "0" : $"{v:+0.#;−0.#}";
        static string Db(double v) => $"{v:+0.0;−0.0;0.0} dB";
    }

    // ------------------------------------------------------------------ video tab

    static readonly Dictionary<string, Brush> Swatches = new()
    {
        ["invert"] = Grad("#F2F2F2", "#111111"), ["sepia"] = Grad("#C9A26B", "#5C3B1E"), ["posterize"] = Steps("#E8710A", "#9A3E05", "#3B1F05"),
        ["gradient"] = Grad("#7FD3FF", "#1B1B6B"), ["extract"] = Grad("#FF3B30", "#3A0A08"), ["colorthres"] = Steps("#FF3B30", "#8A8A8A", "#5A5A5A"),
        ["anaglyph"] = Steps("#FF3040", "#30E0FF"), ["mirror"] = Steps("#5E8BFF", "#5E8BFF", "#2A3F7A"), ["psychedelic"] = Rainbow(),
        ["wave"] = Grad("#3FE0C5", "#1F4E8C"), ["ripple"] = Grad("#7FC8FF", "#0D3B66"), ["motionblur"] = Grad("#FFFFFF", "#3A3A3A", 0),
        ["motiondetect"] = Steps("#2A2A2A", "#39FF14"), ["gaussianblur"] = Grad("#B8B8B8", "#4A4A4A"), ["hqdn3d"] = Grad("#9FD8B0", "#2F5E44"),
        ["antiflicker"] = Grad("#FFE38A", "#7A5A12"),
    };

    static Brush Grad(string a, string b, double angle = 45)
    {
        var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), angle);
        g.Freeze();
        return g;
    }

    static Brush Steps(params string[] colors)
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i < colors.Length; i++)
        {
            var c = (Color)ColorConverter.ConvertFromString(colors[i]);
            stops.Add(new GradientStop(c, (double)i / colors.Length));
            stops.Add(new GradientStop(c, (double)(i + 1) / colors.Length));
        }
        var g = new LinearGradientBrush(stops, 0);
        g.Freeze();
        return g;
    }

    static Brush Rainbow()
    {
        var g = new LinearGradientBrush(new GradientStopCollection
        {
            new((Color)ColorConverter.ConvertFromString("#FF3B30"), 0), new((Color)ColorConverter.ConvertFromString("#FFCC00"), 0.33),
            new((Color)ColorConverter.ConvertFromString("#34C759"), 0.66), new((Color)ColorConverter.ConvertFromString("#5856D6"), 1),
        }, 45);
        g.Freeze();
        return g;
    }

    void SetVideoFilter(string module, bool on)
    {
        if (on) Fx.VideoOn.Add(module); else Fx.VideoOn.Remove(module);
        SafeFx(_fx.ApplyVideo);
        UpdateFxTabs();
    }

    UIElement FilterCard(Filter f, string? summaryHint = null)
    {
        return Card("v:" + f.Module, f.Label, () => Changed(f, Fx.VideoOn.Contains(f.Module)), (card, body) =>
        {
            if (f.Hint != null) body.Children.Add(Note(f.Hint));
            foreach (var p in f.Params) body.Children.Add(ParamRow(card, p, Fx.Get(p), v => { VideoParam(p, v); EnsureOn(card); }));
            if (f.Params.Length > 0) body.Children.Add(ResetLink(card));
        }, () => Fx.VideoOn.Contains(f.Module), on => SetVideoFilter(f.Module, on));
    }

    void BuildVideoFx()
    {
        var C = EffectCatalog.VideoFilters.ToDictionary(f => f.Module);

        FxHost.Children.Add(Group("Picture"));
        FxHost.Children.Add(Card("v:adjust", "Image adjust", () => Changed(C["adjust"], Fx.VideoOn.Contains("adjust")), (card, body) =>
        {
            foreach (var p in C["adjust"].Params)
            {
                // Hue is centred on 0; the others on 1 (neutral).
                body.Children.Add(ParamRow(card, p, Fx.Get(p), v => { VideoParam(p, v); EnsureOn(card); }));
            }
            body.Children.Add(ResetLink(card));
        }, () => Fx.VideoOn.Contains("adjust"), on => SetVideoFilter("adjust", on), expanded: true));
        FxHost.Children.Add(FilterCard(C["sharpen"]));
        FxHost.Children.Add(FilterCard(C["grain"]));
        FxHost.Children.Add(FilterCard(C["gradfun"]));

        FxHost.Children.Add(Group("Orientation"));
        FxHost.Children.Add(Card("transform", "Rotate and flip", () => Fx.VideoOn.Contains("transform")
                ? EffectCatalog.Transforms.First(t => t.Value == Fx.Transform).Label : "None", (card, body) =>
            body.Children.Add(Chips(new[] { ("None", "") }.Concat(EffectCatalog.Transforms.Select(t => (t.Label, t.Value))),
                Fx.VideoOn.Contains("transform") ? Fx.Transform : "", v =>
                {
                    if (v.Length > 0) Fx.Transform = v;
                    SetVideoFilter("transform", v.Length > 0);
                    card.Refresh();
                })), expanded: true));
        FxHost.Children.Add(FilterCard(C["rotate"]));

        FxHost.Children.Add(Group("Frame"));
        FxHost.Children.Add(Card("crop", "Crop", () => _cropPx.Any(p => p > 0) ? $"Custom · {string.Join(" ", _cropPx)} px"
                : EffectCatalog.Crops.FirstOrDefault(c => c.Geometry == _crop).Label ?? "Original", (card, body) =>
        {
            var custom = new StackPanel { Visibility = _cropPx.Any(p => p > 0) ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            body.Children.Add(Chips(EffectCatalog.Crops.Select(c => (c.Label == "Default" ? "Original" : c.Label, c.Geometry ?? "")).Append(("Custom", "custom")),
                _cropPx.Any(p => p > 0) ? "custom" : _crop ?? "", g =>
                {
                    custom.Visibility = g == "custom" ? Visibility.Visible : Visibility.Collapsed;
                    if (g != "custom") SetCrop(g.Length == 0 ? null : g, EffectCatalog.Crops.First(c => (c.Geometry ?? "") == g).Label);
                    card.Refresh(); UpdateFxTabs();
                }));
            uint w = 0, h = 0;
            _player.Mp.Size(0, ref w, ref h);
            custom.Children.Add(Note(w > 0 ? $"Pixels to trim from the {w}×{h} picture" : "Pixels to trim (available while a video plays)"));
            string[] names = { "Top", "Bottom", "Left", "Right" };
            var grid = new UniformGrid { Columns = 2 };
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var cell = SliderRow(card, names[k], 0, Math.Max(1, (k < 2 ? h : w) / 2.0), 0, 1, _cropPx[k], v => $"{v:0}", v =>
                {
                    _cropPx[k] = (int)v;
                    Throttled("crop", 60, () => _player.Mp.CropGeometry = CropGeometry());
                    UpdateFxTabs();
                });
                ((FrameworkElement)cell).Margin = new Thickness(k % 2 == 0 ? 0 : 8, 0, k % 2 == 0 ? 8 : 0, 4);
                grid.Children.Add(cell);
            }
            custom.Children.Add(grid);
            body.Children.Add(custom);
        }));
        FxHost.Children.Add(Card("size", "Zoom and aspect ratio", () =>
                $"{EffectCatalog.Zooms.First(z => Math.Abs(z.Scale - _zoom) < 0.001).Label} · {Aspects.FirstOrDefault(a => a.Value == _player.Mp.AspectRatio).Label ?? "Default"}",
            (card, body) =>
            {
                body.Children.Add(Label("Zoom", 11.5, "TextFaint", new Thickness(2, 0, 0, 6)));
                body.Children.Add(Chips(EffectCatalog.Zooms.Select(z => (z.Label, z.Scale)), _zoom, v => { SetZoom(v); card.Refresh(); UpdateFxTabs(); }));
                body.Children.Add(Label("Aspect ratio", 11.5, "TextFaint", new Thickness(2, 6, 0, 6)));
                body.Children.Add(Chips(Aspects.Select(a => (a.Label, a.Value)), _player.Mp.AspectRatio, v =>
                {
                    _player.Mp.AspectRatio = v;
                    ShowOsd($"Aspect ratio: {Aspects.First(a => a.Value == v).Label}");
                    card.Refresh(); UpdateFxTabs();
                }));
            }));

        // Color and style filters as a tile grid; settings for the ones that are on appear below.
        FxHost.Children.Add(Group("Color and style"));
        var tilesCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(7), Margin = new Thickness(0, 0, 0, 8),
        };
        var tilesStack = new StackPanel();
        var tiles = new UniformGrid { Columns = 4 };
        var tileParams = new StackPanel();
        string[] tileOrder = { "invert", "sepia", "posterize", "gradient", "extract", "colorthres", "anaglyph", "mirror", "psychedelic", "wave", "ripple",
            "motionblur", "motiondetect", "gaussianblur", "hqdn3d", "antiflicker" };
        void FillTileParams()
        {
            tileParams.Children.Clear();
            foreach (var m in tileOrder.Where(Fx.VideoOn.Contains))
            {
                var f = C[m];
                if (f.Params.Length == 0 && f.Hint == null) continue;
                tileParams.Children.Add(Card("t:" + m, f.Label, () => Changed(f, true), (card, body) =>
                {
                    if (f.Hint != null) body.Children.Add(Note(f.Hint));
                    foreach (var p in f.Params) body.Children.Add(ParamRow(card, p, Fx.Get(p), v => VideoParam(p, v)));
                    if (f.Params.Length > 0) body.Children.Add(ResetLink(card));
                }, expanded: true));
            }
        }
        foreach (var m in tileOrder)
        {
            var f = C[m];
            var t = new ToggleButton
            {
                Style = (Style)FindResource("FxTile"), Tag = Swatches[m], IsChecked = Fx.VideoOn.Contains(m), ToolTip = f.Hint ?? f.Label,
                Content = new TextBlock { Text = f.Label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 11 },
            };
            t.Click += (_, _) => { SetVideoFilter(m, t.IsChecked == true); FillTileParams(); };
            tiles.Children.Add(t);
        }
        tilesStack.Children.Add(tiles);
        tilesCard.Child = tilesStack;
        FxHost.Children.Add(tilesCard);
        FillTileParams();
        FxHost.Children.Add(tileParams);

        FxHost.Children.Add(Group("Overlay"));
        FxHost.Children.Add(Card("marq", "Text", () => _marqOn ? $"“{_marqText}”" : "Off", FillMarquee, () => _marqOn, on => { _marqOn = on; SafeFx(ApplySessionVideo); }));
        FxHost.Children.Add(Card("logo", "Logo", () => _logoOn ? (_logoFile != null ? Path.GetFileName(_logoFile) : "Choose an image") : "Off", FillLogo,
            () => _logoOn, on => { _logoOn = on; if (on && _logoFile == null) PickLogo(); SafeFx(ApplySessionVideo); }));

        FxHost.Children.Add(Group("Interlacing"));
        FxHost.Children.Add(Card("deint", "Deinterlace", () => Fx.Deinterlace switch { "off" => "Off", "auto" => $"Automatic · {Fx.DeinterlaceMode}", _ => $"On · {Fx.DeinterlaceMode}" },
            (card, body) =>
            {
                body.Children.Add(Chips(new[] { ("Off", "off"), ("Automatic", "auto"), ("On", "on") }, Fx.Deinterlace, v => { Fx.Deinterlace = v; SafeFx(_fx.ApplyDeinterlace); card.Refresh(); }));
                body.Children.Add(Label("Mode", 11.5, "TextFaint", new Thickness(2, 6, 0, 6)));
                body.Children.Add(Chips(EffectCatalog.DeinterlaceModes.Select(m => (m, m)), Fx.DeinterlaceMode, v => { Fx.DeinterlaceMode = v; SafeFx(_fx.ApplyDeinterlace); card.Refresh(); }));
            }));

        FxHost.Children.Add(Footer("Reset all video effects", () =>
        {
            Fx.VideoOn.Clear(); Fx.Transform = "90"; Fx.Deinterlace = "auto";
            foreach (var p in EffectCatalog.VideoFilters.SelectMany(f => f.Params)) Fx.Values.Remove(p.Var);
            _crop = null; Array.Clear(_cropPx); _zoom = 0; _marqOn = _logoOn = false; _player.Mp.AspectRatio = null;
            SafeFx(() => { _fx.ApplyVideo(); ApplySessionVideo(); });
            BuildEffects();
            ShowOsd("Video effects reset");
        }));
    }

    void FillMarquee(FxCard card, StackPanel body)
    {
        var text = new TextBox { Style = (Style)FindResource("DarkText"), Text = _marqText };
        text.TextChanged += (_, _) => { _marqText = text.Text; card.Refresh(); if (_marqOn) Throttled("overlay", 80, ApplySessionVideo); };
        body.Children.Add(text);
        body.Children.Add(SliderRow(card, "Size", 0, 96, 0, 1, _marqSize, v => v == 0 ? "Auto" : $"{v:0} px", v => { _marqSize = (int)v; if (_marqOn) Throttled("overlay", 60, ApplySessionVideo); }));
        body.Children.Add(SliderRow(card, "Opacity", 0, 255, 255, 1, _marqOpacity, v => $"{v / 2.55:0}%", v => { _marqOpacity = (int)v; if (_marqOn) Throttled("overlay", 60, ApplySessionVideo); }));
        body.Children.Add(PositionRow(_marqPos, v => { _marqPos = v; if (_marqOn) SafeFx(ApplySessionVideo); }));
    }

    void FillLogo(FxCard card, StackPanel body)
    {
        var pick = Flat(_logoFile == null ? "Choose image…" : Path.GetFileName(_logoFile), () => { }, new Thickness(0, 0, 0, 6));
        pick.Click += (_, _) => { PickLogo(); pick.Content = _logoFile == null ? "Choose image…" : Path.GetFileName(_logoFile); card.Refresh(); SafeFx(ApplySessionVideo); };
        body.Children.Add(pick);
        body.Children.Add(SliderRow(card, "Opacity", 0, 255, 255, 1, _logoOpacity, v => $"{v / 2.55:0}%", v => { _logoOpacity = (int)v; if (_logoOn) Throttled("overlay", 60, ApplySessionVideo); }));
        body.Children.Add(PositionRow(_logoPos, v => { _logoPos = v; if (_logoOn) SafeFx(ApplySessionVideo); }));
    }

    UIElement PositionRow(int value, Action<int> pick)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var picker = new PositionPicker(value, (Style)FindResource("PosCell"));
        picker.Picked += pick;
        DockPanel.SetDock(picker, System.Windows.Controls.Dock.Right);
        row.Children.Add(picker);
        row.Children.Add(new TextBlock { Text = "Position", FontSize = 12, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    void PickLogo()
    {
        var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", Title = "Choose a logo image" };
        if (d.ShowDialog(this) == true) _logoFile = d.FileName;
    }

    // ------------------------------------------------------------------ sync tab

    void BuildSync()
    {
        FxHost.Children.Add(DelayCard("Audio", "Positive values play the audio later, for when sound comes before the picture.",
            () => _player.Mp.AudioDelay / 1000, ms => SetAudioDelay(ms), 10));
        FxHost.Children.Add(DelayCard("Subtitles", "Positive values show subtitles later, for when they appear too early.",
            () => _player.Mp.SpuDelay / 1000, ms => SetSubDelay(ms), 30));
        FxHost.Children.Add(Note($"Shortcuts from your VLC settings: audio {_keys.Label("audiodelay-down")} / {_keys.Label("audiodelay-up")}, subtitles {_keys.Label("subdelay-down")} / {_keys.Label("subdelay-up")} (50 ms steps)."));
    }

    UIElement DelayCard(string what, string help, Func<long> getMs, Action<long> setMs, double rangeSec)
    {
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 12, 10), Margin = new Thickness(0, 4, 0, 8),
        };
        var sp = new StackPanel();
        root.Child = sp;
        sp.Children.Add(new TextBlock { Text = $"{what} delay", FontSize = 13, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("IconHi") });
        var big = new TextBlock { FontSize = 26, FontWeight = FontWeights.Light, Foreground = (Brush)FindResource("IconHi"), Margin = new Thickness(0, 4, 0, 0) };
        sp.Children.Add(big);
        sp.Children.Add(Note(help));
        var s = new FxSlider { Minimum = -rangeSec, Maximum = rangeSec, Origin = 0, Step = 0.05, Value = getMs() / 1000.0, Margin = new Thickness(-9, 0, -9, 4) };
        void Show(long ms) { big.Text = ms == 0 ? "0 ms" : Math.Abs(ms) < 1000 ? $"{ms:+0;−0} ms" : $"{ms / 1000.0:+0.00;−0.00} s"; }
        Show(getMs());
        void Set(long ms) { ms = Math.Clamp(ms, (long)(-rangeSec * 1000), (long)(rangeSec * 1000)); Show(ms); Throttled("delay:" + what, 40, () => { setMs(ms); UpdateFxTabs(); }); }
        s.Changed += v => Set((long)Math.Round(v * 1000));
        sp.Children.Add(s);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(TextButton("−50 ms", () => { long v = getMs() - 50; s.Value = v / 1000.0; Set(v); }));
        row.Children.Add(TextButton("+50 ms", () => { long v = getMs() + 50; s.Value = v / 1000.0; Set(v); }));
        row.Children.Add(TextButton("Reset", () => { s.Value = 0; Set(0); }));
        sp.Children.Add(row);
        return root;
    }
}
