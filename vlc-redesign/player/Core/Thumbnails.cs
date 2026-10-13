using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RedesignPlayer.Core;

/// <summary>
/// Seek-bar previews, YouTube style. A hidden, silent second player decodes small frames:
/// first a storyboard across the whole file (coarse-to-fine, so early hovers already find a
/// frame nearby), and on top of that exact frames for wherever the pointer rests. Lookups
/// return the nearest frame available right now.
/// </summary>
public sealed class Thumbnails : IDisposable
{
    readonly Media _media;
    readonly MediaPlayer _mp;
    readonly int _w, _h;
    readonly long _len;
    readonly SortedList<long, BitmapSource> _frames = new();
    readonly object _sync = new();

    // libvlc renders into _buf; Display copies it out while the decoder waits.
    readonly IntPtr _buf;
    readonly byte[] _last;
    int _seq;
    readonly AutoResetEvent _frameReady = new(false);
    readonly AutoResetEvent _wake = new(false);
    readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    long _wanted = -1;   // latest exact-frame request (ms), -1 = none
    volatile bool _stop;
    readonly Thread _worker;

    /// <summary>Raised (on a worker thread) whenever a new frame is available.</summary>
    public event Action? Updated;

    public int Count { get { lock (_sync) return _frames.Count; } }
    public int PixelWidth => _w;
    public int PixelHeight => _h;

    public Thumbnails(LibVLC lib, string path, long lengthMs, double aspect, int pixelWidth = 256)
    {
        _len = lengthMs;
        _w = pixelWidth & ~1;
        _h = Math.Max(2, (int)Math.Round(_w / Math.Clamp(aspect, 0.3, 4)) & ~1);
        _buf = Marshal.AllocHGlobal(_w * _h * 4);
        _last = new byte[_w * _h * 4];

        _media = new Media(lib, path, FromType.FromPath);
        foreach (var o in new[] { ":no-audio", ":no-spu", ":no-sub-autodetect-file", ":avcodec-hw=none", ":no-osd" })
            _media.AddOption(o);
        _mp = new MediaPlayer(_media) { EnableHardwareDecoding = false };
        _lockCb = Lock;
        _displayCb = Display;
        _mp.SetVideoFormat("RV32", (uint)_w, (uint)_h, (uint)(_w * 4));
        _mp.SetVideoCallbacks(_lockCb, null, _displayCb);

        _worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "thumbnails" };
        _worker.Start();
    }

    IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buf);
        return IntPtr.Zero;
    }

    void Display(IntPtr opaque, IntPtr picture)
    {
        lock (_last) Marshal.Copy(_buf, _last, 0, _last.Length);
        Interlocked.Increment(ref _seq);
        _frameReady.Set();
    }

    /// <summary>Nearest frame to <paramref name="ms"/>, or null if none is ready yet.</summary>
    public BitmapSource? Get(long ms)
    {
        lock (_sync)
        {
            if (_frames.Count == 0) return null;
            var keys = _frames.Keys;
            int lo = 0, hi = keys.Count - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (keys[mid] < ms) lo = mid + 1; else hi = mid; }
            int best = lo;
            if (lo > 0 && Math.Abs(keys[lo - 1] - ms) <= Math.Abs(keys[lo] - ms)) best = lo - 1;
            return _frames.Values[best];
        }
    }

    /// <summary>Ask for an exact frame at <paramref name="ms"/> (only the latest request is kept).</summary>
    public void Request(long ms)
    {
        Interlocked.Exchange(ref _wanted, Math.Clamp(ms, 0, Math.Max(0, _len - 500)));
        _wake.Set();
    }

    void Run()
    {
        try
        {
            _mp.Play();
            // Wait for the first picture, then stay paused: from here every seek yields one frame.
            if (!_frameReady.WaitOne(8000) || _stop) return;
            _mp.SetPause(true);
            Thread.Sleep(100);

            // Storyboard: one frame every ~len/count, visited coarse-to-fine (0, ½, ¼, ¾, ⅛, …).
            int count = (int)Math.Clamp(_len / 4000, 12, 240);
            long step = _len / count;
            foreach (int i in CoarseToFine(count))
            {
                if (_stop) return;
                ServeRequest();
                Grab(i * step + step / 2);
            }
            // Then only exact frames on request.
            while (!_stop)
            {
                _wake.WaitOne(1000);
                ServeRequest();
            }
        }
        catch (Exception ex) { if (!_stop) App.LogError(ex); }
    }

    void ServeRequest()
    {
        long want = Interlocked.Exchange(ref _wanted, -1);
        if (want < 0) return;
        // Already have one within a second: no need to decode again.
        lock (_sync)
        {
            int i = _frames.IndexOfKey(want);
            if (i >= 0) return;
            foreach (var k in _frames.Keys) if (Math.Abs(k - want) < 1000) return;
        }
        Grab(want);
    }

    void Grab(long ms)
    {
        int before = Volatile.Read(ref _seq);
        _frameReady.Reset();
        _mp.Time = ms;
        // A paused seek normally shows the new frame; if it doesn't, step one frame.
        if (!WaitFrame(before, 700))
        {
            _mp.NextFrame();
            if (!WaitFrame(before, 900)) return;
        }
        byte[] copy;
        lock (_last) copy = (byte[])_last.Clone();
        var bmp = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgr32, null, copy, _w * 4);
        bmp.Freeze();
        // File it under the time actually decoded, so lookups always pick the truly nearest frame.
        long at = _mp.Time;
        if (at < 0 || Math.Abs(at - ms) > 15000) at = ms;
        lock (_sync) _frames[at] = bmp;
        Updated?.Invoke();
    }

    bool WaitFrame(int before, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _seq) == before)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0 || _stop) return false;
            _frameReady.WaitOne((int)left);
        }
        return true;
    }

    static IEnumerable<int> CoarseToFine(int n)
    {
        var seen = new bool[n];
        for (int div = 1; div <= n * 2; div *= 2)
            for (int k = 0; k < div; k++)
            {
                int i = (int)((2L * k + 1) * n / (2L * div));
                if (i < n && !seen[i]) { seen[i] = true; yield return i; }
            }
        for (int i = 0; i < n; i++) if (!seen[i]) yield return i;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _frameReady.Set();
        // Stopping libvlc can block briefly; never do it on the UI thread.
        Task.Run(() =>
        {
            _worker.Join(3000);
            try { _mp.Stop(); } catch { }
            _mp.Dispose();
            _media.Dispose();
            Marshal.FreeHGlobal(_buf);
        });
    }
}
