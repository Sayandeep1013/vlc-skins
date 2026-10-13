using System.Collections.Concurrent;
using System.IO;
using LibVLCSharp.Shared;

namespace RedesignPlayer.Core;

/// <summary>
/// Builds the waveform for the redesign's "waveform seek bar" (shown for music). The file is
/// decoded with the installed VLC engine into a tiny mono WAV (stream output to a file runs
/// faster than real time), then reduced to one peak per bucket.
/// </summary>
public static class Waveform
{
    const int Buckets = 600;
    static readonly ConcurrentDictionary<string, float[]> Cache = new();

    public static async Task<float[]?> GetAsync(LibVLC lib, string path, CancellationToken ct)
    {
        if (Cache.TryGetValue(path, out var hit)) return hit;
        if (!File.Exists(path)) return null;
        var wav = Path.Combine(Path.GetTempPath(), $"vlc-redesign-wave-{Guid.NewGuid():N}.wav");
        try
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var media = new Media(lib, path, FromType.FromPath);
            media.AddOption($":sout=#transcode{{vcodec=none,acodec=s16l,channels=1,samplerate=4000}}:std{{access=file,mux=wav,dst='{wav}'}}");
            media.AddOption(":no-sout-video");
            media.AddOption(":no-sout-spu");
            media.AddOption(":sout-keep=no");
            using var mp = new MediaPlayer(media);
            mp.EndReached += (_, _) => done.TrySetResult(true);
            mp.EncounteredError += (_, _) => done.TrySetResult(false);
            mp.Play();
            using (ct.Register(() => done.TrySetCanceled()))
            {
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(3), ct));
                // Stop off the libvlc event thread; libvlc must not be stopped from its own callbacks.
                await Task.Run(() => mp.Stop());
                if (finished != done.Task || !await done.Task) return null;
            }
            var peaks = await Task.Run(() => Read(wav), ct);
            if (peaks != null) Cache[path] = peaks;
            return peaks;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { App.LogError(ex); return null; }
        finally { try { File.Delete(wav); } catch { } }
    }

    // 16-bit mono PCM -> peak per bucket, gently compressed so quiet passages stay visible.
    static float[]? Read(string wav)
    {
        if (!File.Exists(wav)) return null;
        var bytes = File.ReadAllBytes(wav);
        int data = -1, len = 0;
        for (int i = 12; i + 8 <= bytes.Length;)
        {
            int size = BitConverter.ToInt32(bytes, i + 4);
            if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a')
            {
                data = i + 8;
                len = size <= 0 || data + size > bytes.Length ? bytes.Length - data : size; // streamed WAVs may leave size unset
                break;
            }
            i += 8 + Math.Max(0, size) + (size & 1);
        }
        int samples = len / 2;
        if (data < 0 || samples < Buckets) return null;
        var peaks = new float[Buckets];
        for (int b = 0; b < Buckets; b++)
        {
            int from = (int)((long)samples * b / Buckets), to = (int)((long)samples * (b + 1) / Buckets);
            int max = 0;
            for (int s = from; s < to; s++) max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(bytes, data + s * 2)));
            peaks[b] = max / 32768f;
        }
        float top = Math.Max(0.05f, peaks.Max());
        for (int b = 0; b < Buckets; b++) peaks[b] = MathF.Pow(peaks[b] / top, 0.7f);
        return peaks;
    }
}
