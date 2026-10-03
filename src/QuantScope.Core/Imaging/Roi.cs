using System.Text.Json.Serialization;

namespace QuantScope.Core.Imaging;

public enum RoiShape
{
    Rectangle,
    Ellipse,
    Polygon,
}

/// <summary>
/// 解析する範囲（関心領域）。座標は画像の画素の座標（左上が 0,0、画素の中心は +0.5）。
/// </summary>
public sealed record Roi
{
    [JsonConstructor]
    public Roi(RoiShape shape, IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Shape = shape;
        Points = points;
        int need = shape == RoiShape.Polygon ? 3 : 2;
        if (points.Count < need) throw new ArgumentException("範囲の点が足りません。", nameof(points));
    }

    public RoiShape Shape { get; }

    /// <summary>四角・楕円は対角の 2 点、多角形は頂点</summary>
    public IReadOnlyList<PointD> Points { get; }

    public static Roi Rectangle(double x0, double y0, double x1, double y1) => new(RoiShape.Rectangle, [new(x0, y0), new(x1, y1)]);
    public static Roi Ellipse(double x0, double y0, double x1, double y1) => new(RoiShape.Ellipse, [new(x0, y0), new(x1, y1)]);
    public static Roi Polygon(IEnumerable<PointD> points) => new(RoiShape.Polygon, points.ToList());

    public (double X0, double Y0, double X1, double Y1) Bounds
    {
        get
        {
            double x0 = Points.Min(p => p.X), y0 = Points.Min(p => p.Y);
            double x1 = Points.Max(p => p.X), y1 = Points.Max(p => p.Y);
            return (x0, y0, x1, y1);
        }
    }

    /// <summary>点（画素の中心）が範囲の中か</summary>
    public bool Contains(double x, double y)
    {
        var (x0, y0, x1, y1) = Bounds;
        switch (Shape)
        {
            case RoiShape.Rectangle:
                return x >= x0 && x < x1 && y >= y0 && y < y1;
            case RoiShape.Ellipse:
                double rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
                if (rx <= 0 || ry <= 0) return false;
                double dx = (x - (x0 + rx)) / rx, dy = (y - (y0 + ry)) / ry;
                return (dx * dx) + (dy * dy) <= 1;
            default:
                // 多角形: 水平な半直線と辺の交差を数える
                bool inside = false;
                for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
                {
                    var a = Points[i];
                    var b = Points[j];
                    if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X) inside = !inside;
                }
                return inside;
        }
    }

    /// <summary>範囲を画像の大きさのマスクにする（画素の中心が中なら対象）</summary>
    public Mask ToMask(int width, int height)
    {
        var m = new Mask(width, height);
        var (x0, y0, x1, y1) = Bounds;
        int ya = Math.Max(0, (int)Math.Floor(y0)), yb = Math.Min(height - 1, (int)Math.Ceiling(y1));
        int xa = Math.Max(0, (int)Math.Floor(x0)), xb = Math.Min(width - 1, (int)Math.Ceiling(x1));
        for (int y = ya; y <= yb; y++)
            for (int x = xa; x <= xb; x++)
                if (Contains(x + 0.5, y + 0.5)) m.Bits[(y * width) + x] = true;
        return m;
    }

    /// <summary>画像の外にはみ出した範囲を、画像の中に収まる四角として返す（切り抜き用）</summary>
    public (int X, int Y, int Width, int Height) ClampedPixelRect(int width, int height)
    {
        var (x0, y0, x1, y1) = Bounds;
        int xa = Math.Clamp((int)Math.Floor(x0), 0, width), ya = Math.Clamp((int)Math.Floor(y0), 0, height);
        int xb = Math.Clamp((int)Math.Ceiling(x1), 0, width), yb = Math.Clamp((int)Math.Ceiling(y1), 0, height);
        return (xa, ya, Math.Max(0, xb - xa), Math.Max(0, yb - ya));
    }
}

public readonly record struct PointD(double X, double Y)
{
    public double DistanceTo(PointD o) => Math.Sqrt(((X - o.X) * (X - o.X)) + ((Y - o.Y) * (Y - o.Y)));
}
