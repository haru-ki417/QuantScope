using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuantScope.App.ViewModels;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.App.Controls;

internal static class ChartColors
{
    public static readonly Brush Fill = Frozen(Color.FromRgb(0x3A, 0x4A, 0x52));
    public static readonly Brush Axis = Frozen(Color.FromRgb(0x72, 0x80, 0x88));
    public static readonly Brush Grid = Frozen(Color.FromRgb(0x24, 0x2D, 0x33));
    public static readonly Brush Accent = Frozen(Color.FromRgb(0x45, 0xD1, 0x9A));
    public static readonly Brush Mask = Frozen(Color.FromRgb(0xFF, 0x5F, 0xAA));
    public static readonly Typeface Face = new("Bahnschrift");

    public static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static FormattedText Text(string s, double size, Brush brush, Visual v) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);
}

/// <summary>
/// 明るさの分布。線形と対数（少ない値も見える）の高さを重ねて描く。
/// しきい値の手順を選んでいるときは線を出し、ドラッグでしきい値を変えられる。
/// </summary>
public sealed class HistogramChart : FrameworkElement
{
    public static readonly DependencyProperty HistogramProperty = DependencyProperty.Register(
        nameof(Histogram), typeof(Histogram), typeof(HistogramChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(double?), typeof(HistogramChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Bottom = 18;

    public HistogramChart()
    {
        Focusable = true;
    }

    /// <summary>線をドラッグした（値は画素値の単位）</summary>
    public event EventHandler<double>? MarkerDragged;

    public Histogram? Histogram { get => (Histogram?)GetValue(HistogramProperty); set => SetValue(HistogramProperty, value); }
    public double? Marker { get => (double?)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    private double ToX(double v, Histogram h) => (v - h.Min) / (h.Max - h.Min) * ActualWidth;

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        double w = ActualWidth, h = ActualHeight - Bottom;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        if (Histogram is not { Total: > 0 } hist) return;
        Cursor = Marker is null ? Cursors.Arrow : Cursors.SizeWE;
        long max = hist.Counts.Max();
        double logMax = Math.Log10(max + 1);
        var lin = new StreamGeometry();
        var log = new StreamGeometry();
        using (var a = lin.Open())
        using (var b = log.Open())
        {
            a.BeginFigure(new Point(0, h), true, true);
            b.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < hist.Bins; i++)
            {
                double x0 = (double)i / hist.Bins * w, x1 = (double)(i + 1) / hist.Bins * w;
                double y = h - ((double)hist.Counts[i] / max * (h - 4));
                double yl = h - (Math.Log10(hist.Counts[i] + 1) / Math.Max(logMax, 1e-9) * (h - 4));
                a.LineTo(new Point(x0, y), true, false);
                a.LineTo(new Point(x1, y), true, false);
                b.LineTo(new Point(x0, yl), true, false);
                b.LineTo(new Point(x1, yl), true, false);
            }
            a.LineTo(new Point(w, h), true, false);
            b.LineTo(new Point(w, h), true, false);
        }
        lin.Freeze();
        log.Freeze();
        dc.DrawGeometry(ChartColors.Grid, null, log);
        dc.DrawGeometry(ChartColors.Fill, null, lin);
        dc.DrawLine(new Pen(ChartColors.Axis, 1), new Point(0, h), new Point(w, h));

        // 目盛り（左端・中央・右端）
        foreach (double f in new[] { 0.0, 0.5, 1.0 })
        {
            double v = hist.Min + (f * (hist.Max - hist.Min));
            var t = ChartColors.Text(MainViewModel.Fmt(v), 10, ChartColors.Axis, this);
            dc.DrawText(t, new Point(Math.Clamp((f * w) - (t.Width / 2), 0, w - t.Width), h + 3));
        }

        if (Marker is { } m)
        {
            double x = ToX(m, hist);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x5F, 0xAA)), null, new Rect(Math.Max(0, x), 0, Math.Max(0, w - x), h));
            dc.DrawLine(new Pen(ChartColors.Accent, 2), new Point(x, 0), new Point(x, h));
            dc.DrawEllipse(ChartColors.Accent, null, new Point(x, 5), 4.5, 4.5);
            var t = ChartColors.Text(MainViewModel.Fmt(m), 11, ChartColors.Accent, this);
            dc.DrawText(t, new Point(x + t.Width + 8 < w ? x + 7 : x - t.Width - 7, 10));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Marker is null || Histogram is null) return;
        CaptureMouse();
        Focus();
        Drag(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (IsMouseCaptured) Drag(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    private void Drag(double x)
    {
        if (Histogram is not { } h) return;
        double v = h.Min + (Math.Clamp(x, 0, ActualWidth) / Math.Max(ActualWidth, 1) * (h.Max - h.Min));
        MarkerDragged?.Invoke(this, v);
    }
}

/// <summary>線に沿った明るさの変化（折れ線）</summary>
public sealed class ProfileChart : FrameworkElement
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(LineProfileResult), typeof(ProfileChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public LineProfileResult? Profile { get => (LineProfileResult?)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        const double left = 40, bottom = 20, top = 8;
        double w = ActualWidth - left - 6, h = ActualHeight - bottom - top;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Profile is not { Values.Length: > 1 } p || w <= 0 || h <= 0) return;
        double min = p.Values.Min(), max = p.Values.Max();
        if (max - min < 1e-9) max = min + 1;
        double len = Math.Max(p.Distances[^1], 1e-9);
        var gridPen = new Pen(ChartColors.Grid, 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = top + (h * i / 4);
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + w, y));
            var t = ChartColors.Text(MainViewModel.Fmt(max - ((max - min) * i / 4)), 10, ChartColors.Axis, this);
            dc.DrawText(t, new Point(left - t.Width - 5, y - (t.Height / 2)));
        }
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int i = 0; i < p.Values.Length; i++)
            {
                var pt = new Point(left + (p.Distances[i] / len * w), top + ((max - p.Values[i]) / (max - min) * h));
                if (i == 0) c.BeginFigure(pt, false, false);
                else c.LineTo(pt, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(ChartColors.Accent, 1.6), geo);
        foreach (double f in new[] { 0.0, 0.5, 1.0 })
        {
            var t = ChartColors.Text(MainViewModel.Fmt(f * p.Length) + (f == 1 ? " " + p.LengthUnit : ""), 10, ChartColors.Axis, this);
            dc.DrawText(t, new Point(Math.Clamp(left + (f * w) - (t.Width / 2), left - 10, left + w - t.Width), top + h + 4));
        }
    }
}
