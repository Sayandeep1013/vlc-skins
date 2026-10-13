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
        Topmost = _settings.AlwaysOnTop;
    }

    static void SafeFx(Action a)
    {
        try { a(); }
        catch (Exception ex) { App.LogError(ex); }
    }

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
                ModulesHost.Children.Add(KeyValue(m.Name ?? "", (string.IsNullOrEmpty(m.LongName) ? m.ShortName : m.LongName) ?? ""));
        }
        var lib = _player.LibVlc;
        List($"Audio filters ({lib.AudioFilters.Length})", lib.AudioFilters);
        List($"Video filters ({lib.VideoFilters.Length})", lib.VideoFilters);
        ModulesHost.Children.Add(Label($"Audio outputs ({lib.AudioOutputs.Length})", 12.5, "Text", new Thickness(2, 16, 0, 4)));
        foreach (var o in lib.AudioOutputs) ModulesHost.Children.Add(KeyValue(o.Name ?? "", o.Description ?? ""));
        ModulesHost.Children.Add(Label($"From {_cfg.InstallDir} · libVLC {lib.Version}", 11, "TextFaint", new Thickness(2, 14, 0, 0)));
    }
}
