using System.Globalization;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RedesignPlayer.Core;

/// <summary>A tunable effect parameter, mirroring VLC's Effects window (name, range, default).</summary>
public sealed record Param(string Var, string Label, double Min, double Max, double Default, double Step, string Unit = "", int Type = VlcVars.Float, double Display = 1)
{
    public string Format(double v) => Type == VlcVars.Int || Step >= 1 ? $"{v * Display:0}{Unit}" : $"{v * Display:0.##}{Unit}";
}

/// <summary>An audio or video filter module with its parameters.</summary>
public sealed record Filter(string Module, string Label, Param[] Params, string? Hint = null);

/// <summary>
/// Everything in VLC's Tools → Effects and Filters that this player can drive, with VLC's own
/// module names, variable names, ranges and defaults (from modules/gui/qt/.../extended_panels.cpp
/// and the filter modules). Values are saved in player.json, like VLC keeps them in vlcrc.
/// </summary>
public sealed class EffectsState
{
    // ---- audio
    public bool EqOn { get; set; }
    public string EqPreset { get; set; } = "Flat";
    public float EqPreamp { get; set; } = 12f;      // VLC's "flat" preamp
    public float[] EqBands { get; set; } = new float[10];
    public bool Eq2Pass { get; set; }
    public HashSet<string> AudioOn { get; set; } = new();   // compressor, spatializer, stereo_widen, scaletempo_pitch
    public Dictionary<string, double> Values { get; set; } = new();
    public int StereoMode { get; set; } = 1;          // libvlc_audio_output_channel_t: 1 stereo

    // ---- video
    public HashSet<string> VideoOn { get; set; } = new();   // adjust, sharpen, transform, invert, ...
    public string Transform { get; set; } = "90";
    public string Deinterlace { get; set; } = "auto";      // off | auto | <mode>
    public string DeinterlaceMode { get; set; } = "yadif";

    public double Get(Param p) => Values.TryGetValue(p.Var, out var v) ? v : p.Default;
}

public static class EffectCatalog
{
    public static readonly Filter Compressor = new("compressor", "Compressor", new Param[]
    {
        new("compressor-rms-peak", "RMS / peak", 0, 1, 0, 0.01),
        new("compressor-attack", "Attack", 1.5, 400, 25, 0.5, " ms"),
        new("compressor-release", "Release", 2, 800, 100, 1, " ms"),
        new("compressor-threshold", "Threshold", -30, 0, -11, 0.1, " dB"),
        new("compressor-ratio", "Ratio", 1, 20, 8, 0.1, ":1"),
        new("compressor-knee", "Knee radius", 1, 10, 2.5, 0.1, " dB"),
        new("compressor-makeup-gain", "Makeup gain", 0, 24, 7, 0.1, " dB"),
    }, "Evens out loud and quiet parts.");

    public static readonly Filter Spatializer = new("spatializer", "Spatializer", new Param[]
    {
        new("spatializer-roomsize", "Size", 0, 1.1, 0.85, 0.01, "", VlcVars.Float, 10),
        new("spatializer-width", "Width", 0, 1, 1, 0.01, "", VlcVars.Float, 10),
        new("spatializer-wet", "Wet", 0, 1, 0.4, 0.01, "", VlcVars.Float, 10),
        new("spatializer-dry", "Dry", 0, 1, 0.5, 0.01, "", VlcVars.Float, 10),
        new("spatializer-damp", "Damp", 0, 1, 0.5, 0.01, "", VlcVars.Float, 10),
    }, "Room-like reverb.");

    public static readonly Filter StereoWiden = new("stereo_widen", "Stereo widener", new Param[]
    {
        new("stereowiden-delay", "Delay time", 1, 100, 20, 1, " ms"),
        new("stereowiden-feedback", "Feedback gain", 0, 0.9, 0.3, 0.01, "%", VlcVars.Float, 100),
        new("stereowiden-crossfeed", "Crossfeed", 0, 0.8, 0.3, 0.01, "%", VlcVars.Float, 100),
        new("stereowiden-dry-mix", "Dry mix", 0, 1, 0.8, 0.01, "%", VlcVars.Float, 100),
    });

    public static readonly Filter Pitch = new("scaletempo_pitch", "Pitch", new Param[]
    {
        new("pitch-shift", "Adjust pitch", -12, 12, 0, 0.25, " semitones"),
    }, "Shifts pitch without changing speed.");

    public static readonly Filter[] AudioFilters = { Compressor, Spatializer, StereoWiden, Pitch };

    // Video filters ("video-filter" modules). Adjust is the "Essential" image adjustments.
    public static readonly Filter Adjust = new("adjust", "Image adjust", new Param[]
    {
        new("brightness", "Brightness", 0, 2, 1, 0.01),
        new("contrast", "Contrast", 0, 2, 1, 0.01),
        new("hue", "Hue", -180, 180, 0, 1, "°"),
        new("saturation", "Saturation", 0, 3, 1, 0.01),
        new("gamma", "Gamma", 0.01, 10, 1, 0.01),
    });
    public static readonly Filter Sharpen = new("sharpen", "Sharpen", new Param[] { new("sharpen-sigma", "Sigma", 0, 2, 0.05, 0.01) });
    public static readonly Filter Grain = new("grain", "Film grain", new Param[] { new("grain-variance", "Variance", 0, 10, 2, 0.1) });
    public static readonly Filter Gradfun = new("gradfun", "Banding removal", new Param[]
    {
        new("gradfun-radius", "Radius", 4, 32, 16, 1, "", VlcVars.Int),
        new("gradfun-strength", "Strength", 0.51, 255, 1.2, 0.01),
    });
    public static readonly Filter Rotate = new("rotate", "Rotate (free angle)", new Param[] { new("rotate-angle", "Angle", 0, 359, 0, 1, "°") });
    public static readonly Filter Transform = new("transform", "Transform", Array.Empty<Param>());
    public static readonly Filter Invert = new("invert", "Invert colors", Array.Empty<Param>());
    public static readonly Filter Sepia = new("sepia", "Sepia", new Param[] { new("sepia-intensity", "Intensity", 0, 255, 120, 1, "", VlcVars.Int) });
    public static readonly Filter Posterize = new("posterize", "Posterize", new Param[] { new("posterize-level", "Levels", 2, 256, 6, 1, "", VlcVars.Int) });
    public static readonly Filter Gradient = new("gradient", "Gradient / edge", new Param[]
    {
        new("gradient-type", "Colored", 0, 1, 0, 1, "", VlcVars.Int),
    });
    public static readonly Filter Extract = new("extract", "Color extraction", Array.Empty<Param>(), "Keeps only the red channel (VLC's default).");
    public static readonly Filter ColorThres = new("colorthres", "Color threshold", new Param[]
    {
        new("colorthres-saturationthres", "Saturation", 0, 255, 20, 1, "", VlcVars.Int),
        new("colorthres-similaritythres", "Similarity", 0, 255, 15, 1, "", VlcVars.Int),
    }, "Keeps red, turns the rest grey (VLC's default color).");
    public static readonly Filter Anaglyph = new("anaglyph", "Anaglyph 3D", Array.Empty<Param>(), "Red/cyan glasses.");
    public static readonly Filter Mirror = new("mirror", "Mirror", new Param[]
    {
        new("mirror-split", "Split", 0, 1, 0, 1, "", VlcVars.Int),
        new("mirror-direction", "Direction", 0, 1, 0, 1, "", VlcVars.Int),
    });
    public static readonly Filter Psychedelic = new("psychedelic", "Psychedelic", Array.Empty<Param>());
    public static readonly Filter Wave = new("wave", "Waves", Array.Empty<Param>());
    public static readonly Filter Ripple = new("ripple", "Water effect", Array.Empty<Param>());
    public static readonly Filter MotionBlur = new("motionblur", "Motion blur", new Param[] { new("blur-factor", "Factor", 1, 127, 80, 1, "", VlcVars.Int) });
    public static readonly Filter MotionDetect = new("motiondetect", "Motion detect", Array.Empty<Param>());
    public static readonly Filter GaussianBlur = new("gaussianblur", "Spatial blur", new Param[] { new("gaussianblur-sigma", "Sigma", 0.01, 10, 2, 0.01) });
    public static readonly Filter Denoise = new("hqdn3d", "Denoiser", new Param[]
    {
        new("hqdn3d-luma-spat", "Spatial luma", 0, 254, 4, 0.1),
        new("hqdn3d-chroma-spat", "Spatial chroma", 0, 254, 3, 0.1),
        new("hqdn3d-luma-temp", "Temporal luma", 0, 254, 6, 0.1),
        new("hqdn3d-chroma-temp", "Temporal chroma", 0, 254, 4.5, 0.1),
    });
    public static readonly Filter AntiFlicker = new("antiflicker", "Anti-flickering", new Param[]
    {
        new("antiflicker-window-size", "Window", 0, 100, 10, 1, "", VlcVars.Int),
        new("antiflicker-softening-size", "Softening", 0, 31, 10, 1, "", VlcVars.Int),
    });

    public static readonly Filter[] VideoFilters =
    {
        Adjust, Sharpen, Grain, Gradfun, Transform, Rotate, Invert, Sepia, Posterize, Gradient, Extract, ColorThres,
        Anaglyph, Mirror, Psychedelic, Wave, Ripple, MotionBlur, MotionDetect, GaussianBlur, Denoise, AntiFlicker,
    };

    public static readonly (string Value, string Label)[] Transforms =
    {
        ("90", "Rotate 90°"), ("180", "Rotate 180°"), ("270", "Rotate 270°"),
        ("hflip", "Flip horizontally"), ("vflip", "Flip vertically"), ("transpose", "Transpose"), ("antitranspose", "Anti-transpose"),
    };

    public static readonly string[] DeinterlaceModes = { "blend", "bob", "discard", "linear", "mean", "x", "yadif", "yadif2x", "phosphor", "ivtc" };

    public static readonly (string Label, string? Geometry)[] Crops =
    {
        ("Default", null), ("16:10", "16:10"), ("16:9", "16:9"), ("4:3", "4:3"), ("1.85:1", "185:100"), ("2.21:1", "221:100"),
        ("2.35:1", "235:100"), ("2.39:1", "239:100"), ("5:3", "5:3"), ("5:4", "5:4"), ("1:1", "1:1"),
    };

    public static readonly (string Label, float Scale)[] Zooms = { ("Fit to window", 0), ("1:4 Quarter", 0.25f), ("1:2 Half", 0.5f), ("1:1 Original", 1), ("2:1 Double", 2) };

    public static readonly (string Label, AudioOutputChannel Mode)[] StereoModes =
    {
        ("Stereo", AudioOutputChannel.Stereo), ("Reverse stereo", AudioOutputChannel.RStereo),
        ("Left", AudioOutputChannel.Left), ("Right", AudioOutputChannel.Right), ("Dolby Surround", AudioOutputChannel.Dolbys),
    };
}

/// <summary>Applies <see cref="EffectsState"/> to the running player, live.</summary>
public sealed class EffectsEngine
{
    readonly MediaPlayer _mp;
    readonly EffectsState _s;
    readonly string _baseAudioFilters, _baseVideoFilters;

    public EffectsEngine(MediaPlayer mp, EffectsState state, VlcConfig cfg)
    {
        _mp = mp;
        _s = state;
        // Keep any filters the user configured in VLC's own preferences.
        _baseAudioFilters = cfg.Get("audio-filter") ?? "";
        _baseVideoFilters = cfg.Get("video-filter") ?? "";
    }

    IntPtr Handle => _mp.NativeReference;

    /// <summary>Number of live audio / video outputs found (0 when nothing is playing).</summary>
    public (int Audio, int Video) Outputs()
    {
        int a = VlcVars.ForEach(Handle, VlcVars.AudioOutput, _ => { });
        int v = VlcVars.ForEach(Handle, VlcVars.VideoOutput, _ => { });
        return (a, v);
    }

    // ------------------------------------------------------------------ audio

    string AudioFilterString()
    {
        var list = Split(_baseAudioFilters).Where(m => m != "equalizer" && !EffectCatalog.AudioFilters.Any(f => f.Module == m)).ToList();
        if (_s.EqOn) list.Add("equalizer");
        foreach (var f in EffectCatalog.AudioFilters) if (_s.AudioOn.Contains(f.Module)) list.Add(f.Module);
        return string.Join(":", list);
    }

    /// <summary>Sets an audio variable on the player (inherited by new outputs) and on live outputs.</summary>
    void SetAudio(string name, int type, object value)
    {
        VlcVars.Set(Handle, name, type, value);
        VlcVars.ForEach(Handle, VlcVars.AudioOutput, a => VlcVars.Set(a, name, type, value));
    }

    public void ApplyAudio()
    {
        // Parameters first, so filters pick them up when they (re)open.
        SetAudio("equalizer-preamp", VlcVars.Float, _s.EqPreamp);
        SetAudio("equalizer-bands", VlcVars.String, string.Join(" ", _s.EqBands.Select(b => b.ToString("0.0", CultureInfo.InvariantCulture))));
        SetAudio("equalizer-2pass", VlcVars.Bool, _s.Eq2Pass);
        foreach (var f in EffectCatalog.AudioFilters)
            foreach (var p in f.Params) SetAudio(p.Var, p.Type, _s.Get(p));
        SetAudio("audio-filter", VlcVars.String, AudioFilterString());
        _mp.SetChannel((AudioOutputChannel)_s.StereoMode);
    }

    /// <summary>Live parameter change (filters listen to these on the audio output).</summary>
    public void SetAudioParam(Param p, double v) { _s.Values[p.Var] = v; SetAudio(p.Var, p.Type, v); }

    public void SetEqualizer()
    {
        SetAudio("equalizer-preamp", VlcVars.Float, _s.EqPreamp);
        SetAudio("equalizer-bands", VlcVars.String, string.Join(" ", _s.EqBands.Select(b => b.ToString("0.0", CultureInfo.InvariantCulture))));
        SetAudio("equalizer-2pass", VlcVars.Bool, _s.Eq2Pass);
    }

    // ------------------------------------------------------------------ video

    string VideoFilterString()
    {
        var mine = EffectCatalog.VideoFilters.Select(f => f.Module).ToHashSet();
        var list = Split(_baseVideoFilters).Where(m => !mine.Contains(m)).ToList();
        foreach (var f in EffectCatalog.VideoFilters) if (_s.VideoOn.Contains(f.Module)) list.Add(f.Module);
        return string.Join(":", list);
    }

    void SetVideo(string name, int type, object value)
    {
        VlcVars.Set(Handle, name, type, value);
        VlcVars.ForEach(Handle, VlcVars.VideoOutput, v => { if (VlcVars.Exists(v, name) || name == "video-filter") VlcVars.Set(v, name, type, value); });
    }

    /// <summary>Pushes every video parameter and (re)builds the filter chain on live outputs.</summary>
    public void ApplyVideo()
    {
        SetVideo("transform-type", VlcVars.String, _s.Transform);
        // VLC's defaults for the parameterless color filters.
        SetVideo("extract-component", VlcVars.Int, 0xFF0000);
        SetVideo("colorthres-color", VlcVars.Int, 0xFF0000);
        SetVideo("gradient-mode", VlcVars.String, "gradient");
        foreach (var f in EffectCatalog.VideoFilters)
            foreach (var p in f.Params) SetVideo(p.Var, p.Type, _s.Get(p));
        SetVideo("video-filter", VlcVars.String, VideoFilterString());
        ApplyDeinterlace();
    }

    /// <summary>Live parameter change. Most video filters read their values when the chain is
    /// built, so the chain is rebuilt unless the variable is a live (command) variable.</summary>
    public void SetVideoParam(Param p, double v)
    {
        _s.Values[p.Var] = v;
        SetVideo(p.Var, p.Type, v);
        bool live = p.Var is "brightness" or "contrast" or "hue" or "saturation" or "gamma" or "rotate-angle" or "sharpen-sigma"
            or "grain-variance" or "sepia-intensity" or "posterize-level" or "gradient-type" or "blur-factor" or "gaussianblur-sigma";
        if (!live) SetVideo("video-filter", VlcVars.String, VideoFilterString());
    }

    public void ApplyDeinterlace()
    {
        switch (_s.Deinterlace)
        {
            case "off": _mp.SetDeinterlace(""); SetVideo("deinterlace", VlcVars.Int, 0); break;
            case "auto": SetVideo("deinterlace-mode", VlcVars.String, _s.DeinterlaceMode); SetVideo("deinterlace", VlcVars.Int, -1); break;
            default: _mp.SetDeinterlace(_s.DeinterlaceMode); break;
        }
    }

    public void ApplyAll() { ApplyAudio(); ApplyVideo(); }

    static IEnumerable<string> Split(string s) => s.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
