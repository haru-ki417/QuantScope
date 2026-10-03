using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

public enum Rotation
{
    Clockwise90,
    Rotate180,
    CounterClockwise90,
}

/// <summary>
/// 形と範囲を変える処理（切り抜き・反転・回転・縮小）。画像とマスクの両方に同じ変換をかけられる。
/// </summary>
public static class Geometry
{
    public static Raster Crop(Raster image, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        (x, y, width, height) = ClampRect(image.Width, image.Height, x, y, width, height);
        int ch = image.Channels;
        var d = new float[width * height * ch];
        for (int yy = 0; yy < height; yy++)
            Array.Copy(image.Data, (((y + yy) * image.Width) + x) * ch, d, yy * width * ch, width * ch);
        return new Raster(width, height, ch, d, image.NominalMin, image.NominalMax);
    }

    public static Mask Crop(Mask mask, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mask);
        (x, y, width, height) = ClampRect(mask.Width, mask.Height, x, y, width, height);
        var b = new bool[width * height];
        for (int yy = 0; yy < height; yy++) Array.Copy(mask.Bits, ((y + yy) * mask.Width) + x, b, yy * width, width);
        return new Mask(width, height, b);
    }

    /// <summary>画像の外にはみ出した四角を、中に収める（幅・高さは最低 1）</summary>
    public static (int X, int Y, int Width, int Height) ClampRect(int imageWidth, int imageHeight, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, imageWidth - 1);
        y = Math.Clamp(y, 0, imageHeight - 1);
        width = Math.Clamp(width, 1, imageWidth - x);
        height = Math.Clamp(height, 1, imageHeight - y);
        return (x, y, width, height);
    }

    /// <summary>
    /// 対象の写っている範囲を探す（自動の切り抜き）。背景より明るい（brightObjects なら）画素を囲む最小の四角。
    /// 見つからなければ null。
    /// </summary>
    public static (int X, int Y, int Width, int Height)? FindContentBounds(Raster image, double threshold, bool brightObjects, int margin)
    {
        ArgumentNullException.ThrowIfNull(image);
        var g = image.ToGray();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                float v = g.Data[(y * g.Width) + x];
                if (brightObjects ? v > threshold : v < threshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        if (maxX < 0) return null;
        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(g.Width - 1, maxX + margin);
        maxY = Math.Min(g.Height - 1, maxY + margin);
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public static Raster Flip(Raster image, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Remap(image, image.Width, image.Height, (x, y) => horizontal ? (image.Width - 1 - x, y) : (x, image.Height - 1 - y));
    }

    public static Mask Flip(Mask mask, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(mask);
        return Remap(mask, mask.Width, mask.Height, (x, y) => horizontal ? (mask.Width - 1 - x, y) : (x, mask.Height - 1 - y));
    }

    public static Raster Rotate(Raster image, Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        return rotation switch
        {
            Rotation.Clockwise90 => Remap(image, h, w, (x, y) => (y, h - 1 - x)),
            Rotation.CounterClockwise90 => Remap(image, h, w, (x, y) => (w - 1 - y, x)),
            _ => Remap(image, w, h, (x, y) => (w - 1 - x, h - 1 - y)),
        };
    }

    public static Mask Rotate(Mask mask, Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.Width, h = mask.Height;
        return rotation switch
        {
            Rotation.Clockwise90 => Remap(mask, h, w, (x, y) => (y, h - 1 - x)),
            Rotation.CounterClockwise90 => Remap(mask, h, w, (x, y) => (w - 1 - y, x)),
            _ => Remap(mask, w, h, (x, y) => (w - 1 - x, h - 1 - y)),
        };
    }

    /// <summary>
    /// 縮小・拡大（factor 倍）。縮小は範囲の平均（面積の重み）、拡大は双線形補間。
    /// 大きな画像を軽くするためのもので、縮尺は呼び出し側で factor に合わせて直す。
    /// </summary>
    public static Raster Resize(Raster image, double factor)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(factor > 0)) throw new ArgumentOutOfRangeException(nameof(factor));
        int nw = Math.Max(1, (int)Math.Round(image.Width * factor)), nh = Math.Max(1, (int)Math.Round(image.Height * factor));
        if (nw == image.Width && nh == image.Height) return image;
        int ch = image.Channels;
        var d = new float[nw * nh * ch];
        double sx = (double)image.Width / nw, sy = (double)image.Height / nh;
        Parallel.For(0, nh, y =>
        {
            for (int x = 0; x < nw; x++)
                for (int c = 0; c < ch; c++)
                    d[(((y * nw) + x) * ch) + c] = factor < 1
                        ? AreaAverage(image, x * sx, y * sy, (x + 1) * sx, (y + 1) * sy, c)
                        : Bilinear(image, ((x + 0.5) * sx) - 0.5, ((y + 0.5) * sy) - 0.5, c);
        });
        return new Raster(nw, nh, ch, d, image.NominalMin, image.NominalMax);
    }

    /// <summary>画素の中心を基準にした双線形補間（画像の外は端の値）</summary>
    public static float Bilinear(Raster image, double x, double y, int c = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        double fx = x - x0, fy = y - y0;
        double a = image.Get(x0, y0, c), b = image.Get(x1, y0, c), cc = image.Get(x0, y1, c), d = image.Get(x1, y1, c);
        return (float)(((1 - fy) * (((1 - fx) * a) + (fx * b))) + (fy * (((1 - fx) * cc) + (fx * d))));
    }

    private static float AreaAverage(Raster image, double x0, double y0, double x1, double y1, int c)
    {
        double sum = 0, wsum = 0;
        for (int y = (int)Math.Floor(y0); y < Math.Min(image.Height, (int)Math.Ceiling(y1)); y++)
        {
            double wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
            for (int x = (int)Math.Floor(x0); x < Math.Min(image.Width, (int)Math.Ceiling(x1)); x++)
            {
                double wx = Math.Min(x + 1, x1) - Math.Max(x, x0);
                double wgt = wx * wy;
                sum += wgt * image.Get(x, y, c);
                wsum += wgt;
            }
        }
        return (float)(wsum > 0 ? sum / wsum : 0);
    }

    private static Raster Remap(Raster image, int nw, int nh, Func<int, int, (int X, int Y)> source)
    {
        int ch = image.Channels;
        var d = new float[nw * nh * ch];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                var (sx, sy) = source(x, y);
                for (int c = 0; c < ch; c++) d[(((y * nw) + x) * ch) + c] = image.Data[(((sy * image.Width) + sx) * ch) + c];
            }
        return new Raster(nw, nh, ch, d, image.NominalMin, image.NominalMax);
    }

    private static Mask Remap(Mask mask, int nw, int nh, Func<int, int, (int X, int Y)> source)
    {
        var b = new bool[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                var (sx, sy) = source(x, y);
                b[(y * nw) + x] = mask.Bits[(sy * mask.Width) + sx];
            }
        return new Mask(nw, nh, b);
    }
}
