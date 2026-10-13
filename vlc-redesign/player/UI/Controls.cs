using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RedesignPlayer.UI;

/// <summary>Draws a 24-unit Lucide-style geometry at <see cref="Size"/> px in the inherited Foreground.</summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(IconView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size _) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Data == null) return;
        double s = Size / 24.0;
        var pen = new Pen(Foreground, StrokeWidth / s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(s, s));
        dc.DrawGeometry(Filled ? Foreground : null, pen, Data);
        dc.Pop();
    }
}

/// <summary>The redesign's thick seek bar: dark track, brown fill, orange pill handle, time preview on hover.</summary>
public sealed class SeekBar : FrameworkElement
{
    static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05)));
    static readonly Brush FillBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x1F, 0x05)));
    static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xE8, 0x71, 0x0A)));
    static readonly Brush Handle = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x71, 0x0A)));
    static readonly Brush HandleHi = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x24)));
    static readonly Brush WavePlayed = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0x62, 0x0B)));
    static readonly Brush WaveHover = Frozen(new SolidColorBrush(Color.FromRgb(0x6A, 0x3A, 0x10)));
    static readonly Brush WaveRest = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    double _value;           // 0..1 playback position
    double? _hover;          // 0..1 under the mouse
    bool _dragging;
    public bool Enabled { get; set; } = true;

    /// <summary>Raised with the new position (0..1) while dragging and on release.</summary>
    public event Action<double, bool>? Seek; // (position, final)
    /// <summary>Raised to ask for a hover label (position 0..1 -> text).</summary>
    public Func<double, string>? HoverText;

    float[]? _peaks;
    /// <summary>Waveform peaks (0..1). When set, the bar draws the redesign's waveform seek bar.</summary>
    public float[]? Peaks { get => _peaks; set { _peaks = value; InvalidateVisual(); } }

    readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative, StaysOpen = true };

    public SeekBar()
    {
        Height = 28;
        Cursor = Cursors.Hand;
        ToolTipService.SetIsEnabled(this, false);
        _tip.PlacementTarget = this;
    }

    public double Value
    {
        get => _value;
        set { if (_dragging) return; _value = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    double At(MouseEventArgs e) => Math.Clamp((e.GetPosition(this).X - 6) / Math.Max(1, ActualWidth - 12), 0, 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = At(e);
        if (_dragging) { _value = _hover.Value; Seek?.Invoke(_value, false); }
        if (Enabled && HoverText != null)
        {
            _tip.Content = HoverText(_hover.Value);
            _tip.HorizontalOffset = 6 + _hover.Value * (ActualWidth - 12) - 24;
            _tip.VerticalOffset = -34;
            _tip.IsOpen = true;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; _tip.IsOpen = false; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Enabled) return;
        _dragging = true;
        CaptureMouse();
        _value = At(e);
        Seek?.Invoke(_value, false);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _value = At(e);
        Seek?.Invoke(_value, true);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var r = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(r, 8, 8));
        dc.DrawRectangle(TrackBrush, null, r);
        if (Enabled && _peaks is { Length: > 0 } peaks)
        {
            // One 2px bar every 3px, mirrored around the middle; played part orange.
            double x = 6 + _value * (w - 12);
            double? hx = _hover is double hv && !_dragging ? 6 + hv * (w - 12) : null;
            int n = Math.Max(1, (int)((w - 8) / 3));
            for (int i = 0; i < n; i++)
            {
                double bx = 4 + i * 3;
                int from = i * peaks.Length / n, to = Math.Max(from + 1, (i + 1) * peaks.Length / n);
                float p = 0;
                for (int k = from; k < to && k < peaks.Length; k++) p = Math.Max(p, peaks[k]);
                double bh = Math.Max(2, p * (h - 8));
                var brush = bx + 1 < x ? WavePlayed : hx is double hxx && bx < hxx ? WaveHover : WaveRest;
                dc.DrawRoundedRectangle(brush, null, new Rect(bx, (h - bh) / 2, 2, bh), 1, 1);
            }
            dc.Pop();
            bool hot = _dragging || _hover != null;
            dc.DrawRoundedRectangle(hot ? HandleHi : Handle, null, new Rect(x - 2, 1, 4, h - 2), 2, 2);
        }
        else if (Enabled)
        {
            double x = 6 + _value * (w - 12);
            if (_hover is double hv && !_dragging)
            {
                double hx = 6 + hv * (w - 12);
                if (hx > x) dc.DrawRectangle(HoverBrush, null, new Rect(x, 0, hx - x, h));
            }
            dc.DrawRectangle(FillBrush, null, new Rect(0, 0, x, h));
            dc.Pop();
            bool hot = _dragging || _hover != null;
            dc.DrawRoundedRectangle(hot ? HandleHi : Handle, null, new Rect(x - 4, 2, 8, h - 4), 4, 4);
        }
        else dc.Pop();
    }
}

/// <summary>The redesign's cone-shaped volume: narrow left, tall right, green to orange.</summary>
public sealed class ConeVolume : FrameworkElement
{
    static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05));
    static readonly Brush Grad = new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(0x4F, 0x9A, 0x00), 0), new(Color.FromRgb(0x9A, 0x8A, 0x00), 0.55), new(Color.FromRgb(0xE8, 0x71, 0x0A), 1),
    }, new Point(0, 0), new Point(1, 0));

    double _value = 100;
    public double Max { get; set; } = 125;
    public bool Muted { get; set; }
    bool _dragging;
    public event Action<double>? Changed;

    public ConeVolume() { Width = 88; Height = 24; Cursor = Cursors.Hand; }

    public double Value { get => _value; set { _value = Math.Clamp(value, 0, Max); InvalidateVisual(); } }

    Geometry Shape(double w, double h)
    {
        double mid = h / 2;
        var g = Geometry.Parse(FormattableString.Invariant(
            $"M2,{mid - 3} L{w - 3},1 Q{w},1 {w},4 V{h - 4} Q{w},{h - 1} {w - 3},{h - 1} L2,{mid + 3} Q0,{mid + 2.5} 0,{mid} Q0,{mid - 2.5} 2,{mid - 3} Z"));
        g.Freeze();
        return g;
    }

    void SetFrom(MouseEventArgs e)
    {
        _value = Math.Round(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) * Max);
        Changed?.Invoke(_value);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { _dragging = true; CaptureMouse(); SetFrom(e); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) SetFrom(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _dragging = false; ReleaseMouseCapture(); }

    protected override void OnRender(DrawingContext dc)
    {
        var shape = Shape(ActualWidth, ActualHeight);
        dc.DrawGeometry(TrackBrush, null, shape);
        double frac = Muted ? 0 : _value / Max;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth * frac, ActualHeight)));
        dc.DrawGeometry(Grad, null, shape);
        dc.Pop();
    }
}
