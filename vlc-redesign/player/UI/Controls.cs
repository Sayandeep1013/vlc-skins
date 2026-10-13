using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

/// <summary>What the seek bar's hover card shows for a position.</summary>
public sealed record SeekPreview(ImageSource? Image, string Time, string? Chapter = null);

/// <summary>The redesign's thick seek bar: dark track, brown fill, orange pill handle, and a
/// YouTube-style preview card (frame, chapter, time) while hovering or dragging.</summary>
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
    /// <summary>Asked for the preview card's content at a position (0..1).</summary>
    public Func<double, SeekPreview>? Preview;
    /// <summary>Width of the preview frame in DIPs; its height follows the image's aspect.</summary>
    public double PreviewWidth { get; set; } = 176;
    /// <summary>Element whose top edge the card should float above (the bar or dock holding this).</summary>
    public FrameworkElement? PreviewAbove { get; set; }

    float[]? _peaks;
    /// <summary>Waveform peaks (0..1). When set, the bar draws the redesign's waveform seek bar.</summary>
    public float[]? Peaks { get => _peaks; set { _peaks = value; InvalidateVisual(); } }

    // Preview card: a borderless popup above the bar that never takes the mouse.
    readonly System.Windows.Controls.Primitives.Popup _card = new()
    {
        AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Focusable = false,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
    };
    readonly Border _cardRoot, _frame;
    readonly Image _img = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _chapter, _time;
    const double CardMargin = 14; // room for the shadow around the card

    public SeekBar()
    {
        Height = 28;
        Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        _frame = new Border { CornerRadius = new CornerRadius(8), Child = _img, Background = Brushes.Black };
        _chapter = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 7, 6, 0) };
        _time = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 5, 8, 2) };
        var stack = new StackPanel();
        stack.Children.Add(_frame); stack.Children.Add(_chapter); stack.Children.Add(_time);
        _cardRoot = new Border
        {
            Child = stack, Margin = new Thickness(CardMargin), Padding = new Thickness(4),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x14, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5, Direction = 270 },
        };
        _card.Child = _cardRoot;
        _card.PlacementTarget = this;
    }

    /// <summary>Re-reads the card content (e.g. when a better frame has arrived).</summary>
    public void RefreshPreview() { if (_hover != null && _card.IsOpen) UpdateCard(); }

    public void HidePreview() => _card.IsOpen = false;

    // Debug bridge: pretend the pointer is at a position (null = gone), and expose the card.
    public void DebugHover(double? pos) { _hover = pos; if (pos == null) _card.IsOpen = false; else UpdateCard(); InvalidateVisual(); }
    public FrameworkElement? OpenCard => _card.IsOpen ? _cardRoot : null;

    void UpdateCard()
    {
        if (_hover is not double hv || Preview == null || !Enabled) { _card.IsOpen = false; return; }
        var p = Preview(hv);
        _time.Text = p.Time;
        _chapter.Text = p.Chapter ?? "";
        _chapter.Visibility = string.IsNullOrEmpty(p.Chapter) ? Visibility.Collapsed : Visibility.Visible;
        if (p.Image is BitmapSource bmp && bmp.PixelWidth > 0)
        {
            _img.Source = bmp;
            _frame.Width = PreviewWidth;
            _frame.Height = Math.Round(PreviewWidth * bmp.PixelHeight / bmp.PixelWidth);
            _frame.Clip = new RectangleGeometry(new Rect(0, 0, _frame.Width, _frame.Height), 8, 8);
            _frame.Visibility = Visibility.Visible;
            _cardRoot.Padding = new Thickness(4);
        }
        else
        {
            _frame.Visibility = Visibility.Collapsed;
            _cardRoot.Padding = new Thickness(6, 2, 6, 2);
        }
        _cardRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cw = _cardRoot.DesiredSize.Width, ch = _cardRoot.DesiredSize.Height; // includes the shadow margin
        // Centred on the pointer, but kept within the bar's ends (like YouTube's player edges).
        double x = 6 + hv * (ActualWidth - 12) - cw / 2;
        _card.HorizontalOffset = Math.Clamp(x, -CardMargin - 8, Math.Max(-CardMargin, ActualWidth - cw + CardMargin + 8)) + _shift.X;
        double lift = PreviewAbove != null && PreviewAbove.IsAncestorOf(this) ? TranslatePoint(new Point(), PreviewAbove).Y : 0;
        _card.VerticalOffset = -ch + CardMargin - 10 - lift + _shift.Y;
        _card.IsOpen = true;
        CorrectCardPlacement();
    }

    // WPF positions a popup by its rendered bounds, which include the drop shadow, so the card
    // lands a shadow's width off. Measure where it really is once and remember the correction.
    Vector _shift;
    void CorrectCardPlacement()
    {
        if (PresentationSource.FromVisual(_cardRoot) == null) return;
        _cardRoot.UpdateLayout();
        var actual = PointFromScreen(_cardRoot.PointToScreen(new Point(0, 0)));
        var wanted = new Point(_card.HorizontalOffset - _shift.X + CardMargin, _card.VerticalOffset - _shift.Y + CardMargin);
        var err = wanted - actual;
        if (Math.Abs(err.X) < 0.75 && Math.Abs(err.Y) < 0.75) return;
        _shift += err;
        _card.HorizontalOffset += err.X;
        _card.VerticalOffset += err.Y;
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
        UpdateCard();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_dragging) return; // keep the card while scrubbing outside the bar
        _hover = null; _card.IsOpen = false; InvalidateVisual();
    }

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
        if (!IsMouseOver) { _hover = null; _card.IsOpen = false; } // released away from the bar
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
