using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace RedesignPlayer.Core;

public sealed class QueueItem : System.ComponentModel.INotifyPropertyChanged
{
    public required string Mrl { get; init; }
    public required string Name { get; init; }
    public long DurationMs { get; private set; }
    string _duration = "";
    bool _current;
    public string DurationText { get => _duration; private set { _duration = value; Raise(nameof(DurationText)); } }
    public bool IsCurrent { get => _current; set { if (_current != value) { _current = value; Raise(nameof(IsCurrent)); } } }
    public void SetDuration(long ms) { DurationMs = ms; DurationText = ms > 0 ? Fmt.Time(ms, compact: true) : ""; }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public static class Fmt
{
    public static string Time(long ms, bool compact = false)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (compact) return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}

/// <summary>
/// libvlc + a simple play queue. All libvlc callbacks are marshalled to the UI thread;
/// libvlc must never be called back from its own event threads.
/// </summary>
public sealed class Player : IDisposable
{
    public LibVLC LibVlc { get; }
    public MediaPlayer Mp { get; }
    public ObservableCollection<QueueItem> Queue { get; } = new();
    public int Current { get; private set; } = -1;
    public bool Repeat { get; set; }
    public bool Shuffle { get; set; }
    public event Action? MediaChanged;
    public event Action? StateChanged;
    readonly Dispatcher _ui;
    readonly Random _rng = new();

    public Player(VlcConfig cfg, Dispatcher ui)
    {
        _ui = ui;
        LibVLCSharp.Shared.Core.Initialize(cfg.InstallDir);
        // --no-ignore-config: load the user's vlcrc so their VLC preferences apply
        // (hardware decoding, audio output, subtitle font/encoding, languages, ...).
        // The title OSD is turned off because the top bar shows the title.
        var args = new List<string> { "--no-ignore-config", "--no-video-title-show", "--no-snapshot-preview" };
        // Extra libvlc options for troubleshooting, e.g. VLC_REDESIGN_ARGS="--verbose=2 --vout=direct3d9".
        var extra = Environment.GetEnvironmentVariable("VLC_REDESIGN_ARGS");
        if (!string.IsNullOrWhiteSpace(extra)) args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        LibVlc = new LibVLC(args.ToArray());
        Mp = new MediaPlayer(LibVlc) { EnableKeyInput = false, EnableMouseInput = false };
        Mp.EndReached += (_, _) => _ui.BeginInvoke(OnEnded);
        Mp.Playing += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke());
        Mp.Paused += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke());
        Mp.Stopped += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke());
        Mp.EncounteredError += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke());
    }

    public QueueItem? CurrentItem => Current >= 0 && Current < Queue.Count ? Queue[Current] : null;
    public bool HasMedia => CurrentItem != null;

    public void Enqueue(IEnumerable<string> paths, bool playFirst)
    {
        int first = Queue.Count;
        foreach (var p in paths)
        {
            var item = MakeItem(p);
            Queue.Add(item);
            _ = FillDurationAsync(item);
        }
        if (playFirst && Queue.Count > first) PlayAt(first);
    }

    public void ReplaceQueue(IEnumerable<string> paths)
    {
        Mp.Stop();
        Queue.Clear();
        Current = -1;
        Enqueue(paths, playFirst: true);
    }

    // Reads the duration of local files without playing them.
    async Task FillDurationAsync(QueueItem item)
    {
        if (!File.Exists(item.Mrl)) return;
        try
        {
            using var media = new Media(LibVlc, item.Mrl, FromType.FromPath);
            await media.Parse(MediaParseOptions.ParseLocal, timeout: 5000);
            long d = media.Duration;
            await _ui.InvokeAsync(() => item.SetDuration(d));
        }
        catch { /* duration is cosmetic */ }
    }

    static QueueItem MakeItem(string p)
    {
        bool isUrl = Uri.TryCreate(p, UriKind.Absolute, out var u) && !u.IsFile;
        string name = isUrl ? u!.ToString() : Path.GetFileNameWithoutExtension(p);
        return new QueueItem { Mrl = p, Name = name };
    }

    public void PlayAt(int i)
    {
        if (i < 0 || i >= Queue.Count) return;
        if (CurrentItem is { } prev) prev.IsCurrent = false;
        Current = i;
        var item = Queue[i];
        item.IsCurrent = true;
        bool isUrl = Uri.TryCreate(item.Mrl, UriKind.Absolute, out var u) && !u.IsFile;
        using var media = isUrl ? new Media(LibVlc, u!) : new Media(LibVlc, item.Mrl, FromType.FromPath);
        Mp.Play(media);
        MediaChanged?.Invoke();
    }

    public void TogglePlay()
    {
        if (!HasMedia) { if (Queue.Count > 0) PlayAt(0); return; }
        if (Mp.State is VLCState.Ended or VLCState.Stopped or VLCState.Error) PlayAt(Current);
        else Mp.SetPause(Mp.IsPlaying);
    }

    public void Stop() { Mp.Stop(); StateChanged?.Invoke(); }

    public void Next()
    {
        if (Queue.Count == 0) return;
        if (Shuffle && Queue.Count > 1) { int n; do n = _rng.Next(Queue.Count); while (n == Current); PlayAt(n); return; }
        if (Current + 1 < Queue.Count) PlayAt(Current + 1);
        else if (Repeat) PlayAt(0);
    }

    public void Prev()
    {
        // Like most players: restart the current item unless we're near its start.
        if (Mp.Time > 3000 || Current <= 0) { Mp.Time = 0; return; }
        PlayAt(Current - 1);
    }

    void OnEnded()
    {
        if (Current + 1 < Queue.Count || Repeat || Shuffle) Next();
        else StateChanged?.Invoke();
    }

    public void RemoveAt(int i)
    {
        if (i < 0 || i >= Queue.Count) return;
        Queue.RemoveAt(i);
        if (i < Current) Current--;
        else if (i == Current) { Mp.Stop(); Current = Math.Min(Current, Queue.Count - 1); if (Queue.Count == 0) Current = -1; MediaChanged?.Invoke(); }
    }

    public void SortQueue()
    {
        var cur = CurrentItem;
        var sorted = Queue.OrderBy(q => q.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Queue.Clear();
        foreach (var q in sorted) Queue.Add(q);
        Current = cur == null ? -1 : Queue.IndexOf(cur);
    }

    public void SeekBy(long ms)
    {
        if (!Mp.IsSeekable) return;
        long t = Math.Clamp(Mp.Time + ms, 0, Math.Max(0, Mp.Length - 500));
        Mp.Time = t;
    }

    public string Title
    {
        get
        {
            var m = Mp.Media;
            var t = m?.Meta(MetadataType.Title);
            var item = CurrentItem;
            // libvlc reports the file name as the title when there's no tag; prefer our cleaner name.
            if (string.IsNullOrWhiteSpace(t) || item == null || t.Contains(Path.GetExtension(item.Mrl), StringComparison.OrdinalIgnoreCase))
                return item?.Name ?? "";
            return t;
        }
    }

    public void Dispose()
    {
        Mp.Stop();
        Mp.Dispose();
        LibVlc.Dispose();
    }
}
