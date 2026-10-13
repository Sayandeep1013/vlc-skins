using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RedesignPlayer.UI;

/// <summary>
/// Slider for effect parameters. The fill grows from the parameter's default (its origin), so a
/// centred control like hue, pitch or an equalizer band reads as "how far from neutral".
/// Drag or click to set, mouse wheel to nudge, double-click to reset to the default.
/// </summary>
public sealed class FxSlider : FrameworkElement
{
    static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33));
    static readonly Brush FillOn = Frozen(Color.FromRgb(0xE8, 0x71, 0x0A));
    static readonly Brush FillOff = Frozen(Color.FromRgb(0x6A, 0x45, 0x25));
    static readonly Brush ThumbOn = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    static readonly Brush OriginTick = Frozen(Color.FromRgb(0x5A, 0x5A, 0x5A));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1;
    public double Origin { get; set; }
    public double Step { get; set; } = 0.01;
    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    bool _active = true;
    /// <summary>Dims the fill while the effect is switched off (values can still be prepared).</summary>
    public bool Active { get => _active; set { _active = value; InvalidateVisual(); } }

    double _value;
    public double Value { get => _value; set => SetValue(value, notify: false); }

    /// <summary>Raised continuously while the value changes (dragging, wheel, reset).</summary>
    public event Action<double>? Changed;
    /// <summary>Raised when a drag ends.</summary>
    public event Action<double>? Committed;

    bool _drag, _hover;
    const double Pad = 9, Thick = 4;

    public FxSlider()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        ToolTip = "Drag, scroll to fine-tune, double-click to reset";
        ToolTipService.SetInitialShowDelay(this, 1200);
    }

    void SetValue(double v, bool notify)
    {
        v = Math.Clamp(Step > 0 ? Math.Round((v - Minimum) / Step) * Step + Minimum : v, Minimum, Maximum);
        if (Math.Abs(v - _value) < 1e-9) return;
        _value = v;
        InvalidateVisual();
        if (notify) Changed?.Invoke(v);
    }

    bool Vertical => Orientation == Orientation.Vertical;
    double Length => Math.Max(1, (Vertical ? ActualHeight : ActualWidth) - 2 * Pad);
    double Frac(double v) => Maximum > Minimum ? (v - Minimum) / (Maximum - Minimum) : 0;

    // Horizontal: left→right. Vertical: bottom→top.
    double Pos(double v) => Vertical ? ActualHeight - Pad - Frac(v) * Length : Pad + Frac(v) * Length;
    double At(Point p) => Minimum + Math.Clamp(Vertical ? (ActualHeight - Pad - p.Y) / Length : (p.X - Pad) / Length, 0, 1) * (Maximum - Minimum);

    protected override Size MeasureOverride(Size s) => Vertical ? new Size(22, double.IsInfinity(s.Height) ? 120 : s.Height) : new Size(double.IsInfinity(s.Width) ? 160 : s.Width, 22);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { SetValue(Origin, notify: true); Committed?.Invoke(_value); e.Handled = true; return; }
        _drag = true;
        CaptureMouse();
        SetValue(At(e.GetPosition(this)), notify: true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetValue(At(e.GetPosition(this)), notify: true); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        ReleaseMouseCapture();
        Committed?.Invoke(_value);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double step = Step > 0 ? Step : (Maximum - Minimum) / 100;
        if ((Maximum - Minimum) / step > 400) step *= 5; // big ranges: coarser notches
        SetValue(_value + Math.Sign(e.Delta) * step, notify: true);
        Committed?.Invoke(_value);
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area takes the mouse
        double p0 = Pos(Math.Clamp(Origin, Minimum, Maximum)), pv = Pos(_value);
        if (Vertical)
        {
            double x = w / 2 - Thick / 2;
            dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x, Pad, Thick, h - 2 * Pad), 2, 2);
            if (Origin > Minimum && Origin < Maximum) dc.DrawRectangle(OriginTick, null, new Rect(w / 2 - 6, p0 - 0.5, 12, 1));
            dc.DrawRoundedRectangle(_active ? FillOn : FillOff, null, new Rect(x, Math.Min(p0, pv), Thick, Math.Abs(pv - p0)), 2, 2);
            DrawThumb(dc, new Point(w / 2, pv));
        }
        else
        {
            double y = h / 2 - Thick / 2;
            dc.DrawRoundedRectangle(TrackBrush, null, new Rect(Pad, y, w - 2 * Pad, Thick), 2, 2);
            if (Origin > Minimum && Origin < Maximum) dc.DrawRectangle(OriginTick, null, new Rect(p0 - 0.5, h / 2 - 6, 1, 12));
            dc.DrawRoundedRectangle(_active ? FillOn : FillOff, null, new Rect(Math.Min(p0, pv), y, Math.Abs(pv - p0), Thick), 2, 2);
            DrawThumb(dc, new Point(pv, h / 2));
        }
    }

    void DrawThumb(DrawingContext dc, Point c)
    {
        double r = _drag ? 8 : _hover ? 7.5 : 7;
        dc.DrawEllipse(_active ? FillOn : FillOff, null, c, r, r);
        dc.DrawEllipse(ThumbOn, null, c, r - 3.5, r - 3.5);
    }
}

/// <summary>3×3 grid for overlay positions (VLC's bitmask: 1 left, 2 right, 4 top, 8 bottom, 0 centre).</summary>
public sealed class PositionPicker : UniformGrid
{
    static readonly int[] Codes = { 5, 4, 6, 1, 0, 2, 9, 8, 10 };
    static readonly string[] Names = { "Top left", "Top", "Top right", "Left", "Center", "Right", "Bottom left", "Bottom", "Bottom right" };
    readonly ToggleButton[] _cells = new ToggleButton[9];
    public event Action<int>? Picked;

    public PositionPicker(int value, Style cellStyle)
    {
        Rows = 3; Columns = 3; Width = 84; Height = 60; HorizontalAlignment = HorizontalAlignment.Left;
        for (int i = 0; i < 9; i++)
        {
            int code = Codes[i];
            var b = new ToggleButton { Style = cellStyle, ToolTip = Names[i], IsChecked = code == value, Margin = new Thickness(2) };
            b.Click += (_, _) => { Select(code); Picked?.Invoke(code); };
            _cells[i] = b;
            Children.Add(b);
        }
    }

    public void Select(int code) { for (int i = 0; i < 9; i++) _cells[i].IsChecked = Codes[i] == code; }
}
