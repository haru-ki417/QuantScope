using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuantScope.App.ViewModels;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.App.Controls;

/// <summary>
/// 画像の表示: 拡大・移動、マスクの重ね合わせ、粒の番号、範囲（四角・楕円・多角形）と線の描画。
/// ホイールで拡大縮小（マウスの位置を中心に）、右ボタンか中ボタンのドラッグで移動、ダブルクリックで全体を表示。
/// </summary>
public sealed class ImageView : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = Dp(nameof(Source), typeof(ImageSource), null, OnSourceChanged);
    public static readonly DependencyProperty OverlayProperty = Dp(nameof(Overlay), typeof(ImageSource), null);
    public static readonly DependencyProperty ShowOverlayProperty = Dp(nameof(ShowOverlay), typeof(bool), true);
    public static readonly DependencyProperty OverlayOpacityProperty = Dp(nameof(OverlayOpacity), typeof(double), 0.75);
    public static readonly DependencyProperty ParticlesProperty = Dp(nameof(Particles), typeof(IReadOnlyList<Particle>), null);
    public static readonly DependencyProperty ShowNumbersProperty = Dp(nameof(ShowNumbers), typeof(bool), true);
    public static readonly DependencyProperty SelectedParticleProperty = Dp(nameof(SelectedParticle), typeof(Particle), null);
    public static readonly DependencyProperty RoiProperty = Dp(nameof(Roi), typeof(Roi), null, twoWay: true);
    public static readonly DependencyProperty LineProperty = Dp(nameof(Line), typeof(MeasureLine), null, twoWay: true);
    public static readonly DependencyProperty MatchesProperty = Dp(nameof(Matches), typeof(IReadOnlyList<TemplateMatch>), null);
    public static readonly DependencyProperty ToolProperty = Dp(nameof(Tool), typeof(Tool), Tool.Pan, OnToolChanged);
    public static readonly DependencyProperty CalibrationProperty = Dp(nameof(Calibration), typeof(Calibration), Calibration.Pixels);
    public static readonly DependencyProperty CompareSourceProperty = Dp(nameof(CompareSource), typeof(ImageSource), null);
    public static readonly DependencyProperty ComparingProperty = Dp(nameof(Comparing), typeof(bool), false);

    private static readonly Typeface Face = new("Bahnschrift");
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x45, 0xD1, 0x9A));
    private static readonly Brush Magenta = Frozen(Color.FromRgb(0xFF, 0x5F, 0xAA));
    private static readonly Brush Label = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LabelBack = Frozen(Color.FromArgb(0xA0, 0x0A, 0x0E, 0x10));
    private static readonly Brush Dim = Frozen(Color.FromArgb(0x44, 0x45, 0xD1, 0x9A));

    private static readonly int[] NiceSteps = [1, 2, 5, 10];

    private double _scale = 1;
    private Vector _offset;
    private bool _fitted;
    private Point? _panStart;
    private Vector _panOrigin;
    private PointD? _dragStart;
    private PointD? _dragNow;
    private readonly List<PointD> _polygon = [];
    private PointD? _hover;
    private bool _moved;
    private double _split = 0.5;
    private bool _draggingSplit;

    public ImageView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    /// <summary>マウスの位置（画像の px）が変わった</summary>
    public event EventHandler<PointD?>? HoverChanged;

    /// <summary>道具が「移動」のときに、ドラッグせずにクリックした所（粒を選ぶ）</summary>
    public event EventHandler<PointD>? Clicked;

    /// <summary>道具が「粒を除く」のときに、クリックした所</summary>
    public event EventHandler<PointD>? ExcludeClicked;

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public ImageSource? Overlay { get => (ImageSource?)GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    public bool ShowOverlay { get => (bool)GetValue(ShowOverlayProperty); set => SetValue(ShowOverlayProperty, value); }
    public double OverlayOpacity { get => (double)GetValue(OverlayOpacityProperty); set => SetValue(OverlayOpacityProperty, value); }
    public IReadOnlyList<Particle>? Particles { get => (IReadOnlyList<Particle>?)GetValue(ParticlesProperty); set => SetValue(ParticlesProperty, value); }
    public bool ShowNumbers { get => (bool)GetValue(ShowNumbersProperty); set => SetValue(ShowNumbersProperty, value); }
    public Particle? SelectedParticle { get => (Particle?)GetValue(SelectedParticleProperty); set => SetValue(SelectedParticleProperty, value); }
    public Roi? Roi { get => (Roi?)GetValue(RoiProperty); set => SetValue(RoiProperty, value); }
    public MeasureLine? Line { get => (MeasureLine?)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public IReadOnlyList<TemplateMatch>? Matches { get => (IReadOnlyList<TemplateMatch>?)GetValue(MatchesProperty); set => SetValue(MatchesProperty, value); }
    public Tool Tool { get => (Tool)GetValue(ToolProperty); set => SetValue(ToolProperty, value); }
    public Calibration Calibration { get => (Calibration)GetValue(CalibrationProperty); set => SetValue(CalibrationProperty, value); }

    /// <summary>比べる画像（左側に出す。ふつうは元の画像）</summary>
    public ImageSource? CompareSource { get => (ImageSource?)GetValue(CompareSourceProperty); set => SetValue(CompareSourceProperty, value); }
    public bool Comparing { get => (bool)GetValue(ComparingProperty); set => SetValue(ComparingProperty, value); }

    private bool CompareActive => Comparing && CompareSource is { } c && Source is { } s && PixelWidth(c) == PixelWidth(s) && PixelHeight(c) == PixelHeight(s);

    /// <summary>粒が画面の中央に来るように動かす（小さく見えているときは拡大する）</summary>
    public void CenterOn(Particle p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (Source is null || ActualWidth < 1) return;
        double size = Math.Max(p.BoundsWidth, p.BoundsHeight);
        if (size * _scale < 40) _scale = Math.Clamp(80 / Math.Max(size, 1), _scale, 16);
        _offset = new Vector((ActualWidth / 2) - (p.CentroidX * _scale), (ActualHeight / 2) - (p.CentroidY * _scale));
        InvalidateVisual();
    }

    /// <summary>今の倍率（100% = 1 画素が 1 画面の点）</summary>
    public double Zoom => _scale;

    private static DependencyProperty Dp(string name, Type type, object? def, PropertyChangedCallback? changed = null, bool twoWay = false) =>
        DependencyProperty.Register(name, type, typeof(ImageView), new FrameworkPropertyMetadata(def,
            FrameworkPropertyMetadataOptions.AffectsRender | (twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0), changed));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (ImageView)d;
        var oldSize = e.OldValue is ImageSource o ? new Size(o.Width, o.Height) : Size.Empty;
        var newSize = e.NewValue is ImageSource n ? new Size(n.Width, n.Height) : Size.Empty;
        // 大きさの違う画像になったら全体を表示し直す（同じ大きさなら拡大の具合を保つ）
        if (oldSize != newSize) v._fitted = false;
        v.InvalidateVisual();
    }

    private static void OnToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (ImageView)d;
        v._polygon.Clear();
        v._dragStart = v._dragNow = null;
        v.Cursor = ToolCursor((Tool)e.NewValue);
        v.InvalidateVisual();
    }

    private static Cursor ToolCursor(Tool t) => t switch
    {
        Tool.Pan => Cursors.Arrow,
        Tool.Exclude => Cursors.Hand,
        _ => Cursors.Cross,
    };

    private Size ImageSize => Source is { } s ? new Size(PixelWidth(s), PixelHeight(s)) : Size.Empty;

    private static double PixelWidth(ImageSource s) => s is System.Windows.Media.Imaging.BitmapSource b ? b.PixelWidth : s.Width;
    private static double PixelHeight(ImageSource s) => s is System.Windows.Media.Imaging.BitmapSource b ? b.PixelHeight : s.Height;

    public void Fit()
    {
        var size = ImageSize;
        if (size.IsEmpty || ActualWidth < 1 || ActualHeight < 1) return;
        const double pad = 24;
        _scale = Math.Min((ActualWidth - (2 * pad)) / size.Width, (ActualHeight - (2 * pad)) / size.Height);
        if (_scale <= 0) _scale = 0.01;
        _offset = new Vector((ActualWidth - (size.Width * _scale)) / 2, (ActualHeight - (size.Height * _scale)) / 2);
        _fitted = true;
        InvalidateVisual();
    }

    public void ZoomTo(double scale)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        ZoomAt(center, scale / _scale);
    }

    private void ZoomAt(Point screen, double factor)
    {
        double next = Math.Clamp(_scale * factor, 0.02, 64);
        factor = next / _scale;
        _offset = new Vector(screen.X - ((screen.X - _offset.X) * factor), screen.Y - ((screen.Y - _offset.Y) * factor));
        _scale = next;
        InvalidateVisual();
    }

    private PointD ToImage(Point p) => new((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);
    private Point ToScreen(PointD p) => new((p.X * _scale) + _offset.X, (p.Y * _scale) + _offset.Y);
    private Point ToScreen(double x, double y) => new((x * _scale) + _offset.X, (y * _scale) + _offset.Y);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_fitted) Fit();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Source is null) return;
        if (!_fitted) Fit();
        var size = ImageSize;
        var rect = new Rect(_offset.X, _offset.Y, size.Width * _scale, size.Height * _scale);

        // 拡大したときは画素の四角がそのまま見えるように（補間しない）
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, _scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var g = group.Open())
        {
            g.DrawImage(Source, rect);
            bool compare = CompareActive;
            double splitX = rect.X + (rect.Width * _split);
            if (ShowOverlay && Overlay is not null)
            {
                // 比べているときは、右側（今の表示）にだけ重ねる
                if (compare) g.PushClip(new RectangleGeometry(new Rect(splitX, rect.Y, Math.Max(0, rect.Right - splitX), rect.Height)));
                g.PushOpacity(OverlayOpacity);
                g.DrawImage(Overlay, rect);
                g.Pop();
                if (compare) g.Pop();
            }
            if (compare)
            {
                g.PushClip(new RectangleGeometry(new Rect(rect.X, rect.Y, Math.Max(0, splitX - rect.X), rect.Height)));
                g.DrawImage(CompareSource, rect);
                g.Pop();
            }
        }
        dc.DrawDrawing(group);
        if (CompareActive) DrawSplit(dc, rect, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawNumbers(dc, dpi);
        DrawSelection(dc);
        DrawMatches(dc, dpi);
        DrawRoi(dc);
        DrawLine(dc, dpi);
        DrawScaleBar(dc, dpi);
    }

    private double SplitScreenX
    {
        get
        {
            var size = ImageSize;
            return _offset.X + (size.Width * _scale * _split);
        }
    }

    /// <summary>比べる境目の線と取っ手、左右の名前</summary>
    private void DrawSplit(DrawingContext dc, Rect rect, double dpi)
    {
        double x = Math.Clamp(SplitScreenX, 0, ActualWidth);
        double top = Math.Max(0, rect.Y), bottom = Math.Min(ActualHeight, rect.Bottom);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 4), new Point(x, top), new Point(x, bottom));
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, top), new Point(x, bottom));
        double cy = (top + bottom) / 2;
        dc.DrawEllipse(Brushes.White, new Pen(LabelBack, 1), new Point(x, cy), 13, 13);
        var arrows = new FormattedText("\uE76B \uE76C", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 9, LabelBack, dpi);
        dc.DrawText(arrows, new Point(x - (arrows.Width / 2), cy - (arrows.Height / 2)));
        var left = new FormattedText("元の画像", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("BIZ UDPGothic"), 11.5, Label, dpi);
        var right = new FormattedText("今の表示", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("BIZ UDPGothic"), 11.5, Label, dpi);
        double ly = Math.Max(top + 10, 52);
        if (x - left.Width - 18 > 0)
        {
            dc.DrawRoundedRectangle(LabelBack, null, new Rect(x - left.Width - 18, ly, left.Width + 10, left.Height + 6), 3, 3);
            dc.DrawText(left, new Point(x - left.Width - 13, ly + 3));
        }
        if (x + right.Width + 18 < ActualWidth)
        {
            dc.DrawRoundedRectangle(LabelBack, null, new Rect(x + 8, ly, right.Width + 10, right.Height + 6), 3, 3);
            dc.DrawText(right, new Point(x + 13, ly + 3));
        }
    }

    private void DrawNumbers(DrawingContext dc, double dpi)
    {
        if (!ShowNumbers || !ShowOverlay || Particles is not { Count: > 0 } ps) return;
        // 小さく見えている粒や、数が多すぎるときは番号を省く（読めないため）
        if (ps.Count > 1500) return;
        double size = Math.Clamp(11 * Math.Sqrt(_scale), 9, 14);
        foreach (var p in ps)
        {
            if (Math.Max(p.BoundsWidth, p.BoundsHeight) * _scale < 14) continue;
            var pt = ToScreen(p.CentroidX, p.CentroidY);
            if (pt.X < -20 || pt.Y < -20 || pt.X > ActualWidth + 20 || pt.Y > ActualHeight + 20) continue;
            var t = new FormattedText(p.Id.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Label, dpi);
            var r = new Rect(pt.X - (t.Width / 2) - 3, pt.Y - (t.Height / 2) - 1, t.Width + 6, t.Height + 2);
            dc.DrawRoundedRectangle(LabelBack, null, r, 3, 3);
            dc.DrawText(t, new Point(r.X + 3, r.Y + 1));
        }
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (SelectedParticle is not { } p || !ShowOverlay) return;
        var a = ToScreen(p.BoundsX, p.BoundsY);
        var b = ToScreen(p.BoundsX + p.BoundsWidth, p.BoundsY + p.BoundsHeight);
        var r = new Rect(a, b);
        r.Inflate(4, 4);
        dc.DrawRectangle(null, new Pen(Accent, 2), r);
    }

    private void DrawMatches(DrawingContext dc, double dpi)
    {
        if (Matches is not { Count: > 0 } ms) return;
        var pen = new Pen(Magenta, 1.6) { DashStyle = DashStyles.Dash };
        foreach (var m in ms)
        {
            var r = new Rect(ToScreen(m.X, m.Y), ToScreen(m.X + m.Width, m.Y + m.Height));
            dc.DrawRectangle(null, pen, r);
            var t = new FormattedText(m.Score.ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Magenta, dpi);
            dc.DrawText(t, new Point(r.X + 2, r.Y - t.Height - 1));
        }
    }

    private void DrawRoi(DrawingContext dc)
    {
        var pen = new Pen(Accent, 1.6);
        var dash = new Pen(Accent, 1.4) { DashStyle = DashStyles.Dash };
        if (Roi is { } roi)
        {
            Geometry geo = roi.Shape switch
            {
                RoiShape.Polygon => Polygon(roi.Points.Select(ToScreen).ToList(), closed: true),
                RoiShape.Ellipse => new EllipseGeometry(new Rect(ToScreen(roi.Points[0]), ToScreen(roi.Points[1]))),
                _ => new RectangleGeometry(new Rect(ToScreen(roi.Points[0]), ToScreen(roi.Points[1]))),
            };
            // 範囲の外を少し暗くする
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)), geo);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, outside);
            dc.DrawGeometry(null, pen, geo);
        }
        // 描いている途中
        if (_dragStart is { } s && _dragNow is { } n && Tool is Tool.Rectangle or Tool.Ellipse)
        {
            var r = new Rect(ToScreen(s), ToScreen(n));
            if (Tool == Tool.Ellipse) dc.DrawEllipse(Dim, dash, new Point(r.X + (r.Width / 2), r.Y + (r.Height / 2)), r.Width / 2, r.Height / 2);
            else dc.DrawRectangle(Dim, dash, r);
        }
        if (Tool == Tool.Polygon && _polygon.Count > 0)
        {
            var pts = _polygon.Select(ToScreen).ToList();
            if (_hover is { } h) pts.Add(ToScreen(h));
            dc.DrawGeometry(Dim, dash, Polygon(pts, closed: false));
            foreach (var p in _polygon) dc.DrawEllipse(Accent, null, ToScreen(p), 3, 3);
        }
    }

    private void DrawLine(DrawingContext dc, double dpi)
    {
        MeasureLine? line = Line;
        if (Tool == Tool.Line && _dragStart is { } s && _dragNow is { } n) line = new MeasureLine(s, n);
        if (line is null) return;
        var a = ToScreen(line.A);
        var b = ToScreen(line.B);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 4), a, b);
        dc.DrawLine(new Pen(Accent, 2), a, b);
        dc.DrawEllipse(Accent, null, a, 3.5, 3.5);
        dc.DrawEllipse(Accent, null, b, 3.5, 3.5);
        double px = line.A.DistanceTo(line.B);
        var cal = Calibration;
        string text = cal.IsCalibrated ? $"{MainViewModel.Fmt(cal.Length(px))} {cal.Unit}" : $"{px:0.0} px";
        var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, Label, dpi);
        var mid = new Point(((a.X + b.X) / 2) + 8, ((a.Y + b.Y) / 2) - t.Height - 4);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(mid.X - 4, mid.Y - 2, t.Width + 8, t.Height + 4), 3, 3);
        dc.DrawText(t, mid);
    }

    /// <summary>縮尺があるときは、左下に目盛りの棒を出す</summary>
    private void DrawScaleBar(DrawingContext dc, double dpi)
    {
        var cal = Calibration;
        if (!cal.IsCalibrated) return;
        // 画面で 80〜200 点くらいになる、きりのよい長さ
        double target = 120 / _scale * cal.UnitsPerPixel;
        double p = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double len = NiceSteps.Select(m => m * p).Last(v => v <= target * 1.4);
        double w = len / cal.UnitsPerPixel * _scale;
        var origin = new Point(18, ActualHeight - 22);
        dc.DrawRectangle(LabelBack, null, new Rect(origin.X - 8, origin.Y - 24, w + 16, 34));
        dc.DrawRectangle(Brushes.White, null, new Rect(origin.X, origin.Y, w, 4));
        var t = new FormattedText($"{len:G3} {cal.Unit}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, Label, dpi);
        dc.DrawText(t, new Point(origin.X + ((w - t.Width) / 2), origin.Y - t.Height - 3));
    }

    private static StreamGeometry Polygon(List<Point> pts, bool closed)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], closed, closed);
            for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    // ---------------------------------------------------------------- マウス

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Source is null) return;
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Focus();
        if (Source is null) return;
        var pos = e.GetPosition(this);
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
        {
            if (Tool == Tool.Polygon && _polygon.Count >= 3)
            {
                FinishPolygon();
            }
            else if (Tool == Tool.Pan)
            {
                Fit();
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && CompareActive && Math.Abs(pos.X - SplitScreenX) < 10)
        {
            _draggingSplit = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Tool is Tool.Pan or Tool.Exclude))
        {
            _panStart = pos;
            _panOrigin = _offset;
            _moved = false;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        var ip = ToImage(pos);
        switch (Tool)
        {
            case Tool.Rectangle or Tool.Ellipse or Tool.Line:
                _dragStart = _dragNow = Clamp(ip);
                CaptureMouse();
                break;
            case Tool.Polygon:
                // 最初の点の近くをクリックしたら閉じる
                if (_polygon.Count >= 3 && (ToScreen(_polygon[0]) - pos).Length < 8) FinishPolygon();
                else _polygon.Add(Clamp(ip));
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var pos = e.GetPosition(this);
        if (_draggingSplit)
        {
            var size = ImageSize;
            if (!size.IsEmpty) _split = Math.Clamp((pos.X - _offset.X) / (size.Width * _scale), 0, 1);
            InvalidateVisual();
            return;
        }
        if (_panStart is null && _dragStart is null && CompareActive)
            Cursor = Math.Abs(pos.X - SplitScreenX) < 10 ? Cursors.SizeWE : ToolCursor(Tool);
        if (_panStart is { } ps)
        {
            var d = pos - ps;
            if (d.Length > 3) _moved = true;
            _offset = _panOrigin + d;
            InvalidateVisual();
            return;
        }
        var ip = ToImage(pos);
        _hover = ip;
        HoverChanged?.Invoke(this, ip);
        if (_dragStart is not null)
        {
            var p = Clamp(ip);
            // Shift を押しながらなら、正方形・円・水平垂直・45 度に
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) p = Constrain(_dragStart.Value, p);
            _dragNow = p;
            InvalidateVisual();
        }
        else if (Tool == Tool.Polygon && _polygon.Count > 0)
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_draggingSplit)
        {
            _draggingSplit = false;
            ReleaseMouseCapture();
            return;
        }
        if (_panStart is not null)
        {
            bool click = !_moved && e.ChangedButton == MouseButton.Left && Tool is Tool.Pan or Tool.Exclude;
            _panStart = null;
            ReleaseMouseCapture();
            Cursor = ToolCursor(Tool);
            if (click)
            {
                var ip = ToImage(e.GetPosition(this));
                if (Tool == Tool.Exclude) ExcludeClicked?.Invoke(this, ip);
                else Clicked?.Invoke(this, ip);
            }
            return;
        }
        if (_dragStart is { } s && _dragNow is { } n)
        {
            ReleaseMouseCapture();
            _dragStart = _dragNow = null;
            bool big = Math.Abs(n.X - s.X) * _scale > 3 || Math.Abs(n.Y - s.Y) * _scale > 3;
            if (big)
            {
                switch (Tool)
                {
                    case Tool.Rectangle:
                        Roi = Roi.Rectangle(Math.Min(s.X, n.X), Math.Min(s.Y, n.Y), Math.Max(s.X, n.X), Math.Max(s.Y, n.Y));
                        break;
                    case Tool.Ellipse:
                        Roi = Roi.Ellipse(Math.Min(s.X, n.X), Math.Min(s.Y, n.Y), Math.Max(s.X, n.X), Math.Max(s.Y, n.Y));
                        break;
                    case Tool.Line:
                        Line = new MeasureLine(s, n);
                        break;
                }
            }
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Escape)
        {
            if (_polygon.Count > 0 || _dragStart is not null)
            {
                _polygon.Clear();
                _dragStart = _dragNow = null;
                ReleaseMouseCapture();
            }
            else
            {
                Roi = null;
            }
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Tool == Tool.Polygon && _polygon.Count >= 3)
        {
            FinishPolygon();
            e.Handled = true;
        }
    }

    private void FinishPolygon()
    {
        Roi = Roi.Polygon(_polygon.ToList());
        _polygon.Clear();
        InvalidateVisual();
    }

    private PointD Clamp(PointD p)
    {
        var s = ImageSize;
        return new PointD(Math.Clamp(p.X, 0, s.Width), Math.Clamp(p.Y, 0, s.Height));
    }

    private PointD Constrain(PointD start, PointD p)
    {
        double dx = p.X - start.X, dy = p.Y - start.Y;
        if (Tool == Tool.Line)
        {
            double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            return Clamp(new PointD(start.X + (Math.Cos(ang) * len), start.Y + (Math.Sin(ang) * len)));
        }
        double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return Clamp(new PointD(start.X + (Math.Sign(dx) * m), start.Y + (Math.Sign(dy) * m)));
    }
}
