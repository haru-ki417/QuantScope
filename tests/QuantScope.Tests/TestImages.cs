using QuantScope.Core.Imaging;

namespace QuantScope.Tests;

/// <summary>テスト用の画像・マスクを作る</summary>
internal static class TestImages
{
    public static Mask Disk(int w, int h, double cx, double cy, double r, Mask? into = null)
    {
        var m = into ?? new Mask(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                if ((dx * dx) + (dy * dy) <= r * r) m[x, y] = true;
            }
        return m;
    }

    public static Mask Rect(int w, int h, int x0, int y0, int rw, int rh, Mask? into = null)
    {
        var m = into ?? new Mask(w, h);
        for (int y = y0; y < y0 + rh; y++)
            for (int x = x0; x < x0 + rw; x++) m[x, y] = true;
        return m;
    }

    public static Mask Ellipse(int w, int h, double cx, double cy, double a, double b, double angleDeg, Mask? into = null)
    {
        var m = into ?? new Mask(w, h);
        double t = angleDeg * Math.PI / 180, cos = Math.Cos(t), sin = Math.Sin(t);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // 画像の y は下向き。角度は「右から反時計回り」で与える
                double dx = x + 0.5 - cx, dy = -(y + 0.5 - cy);
                double u = ((dx * cos) + (dy * sin)) / a, v = ((-dx * sin) + (dy * cos)) / b;
                if ((u * u) + (v * v) <= 1) m[x, y] = true;
            }
        return m;
    }

    /// <summary>マスクを明るさの画像に（対象 = fg、背景 = bg）</summary>
    public static Raster FromMask(Mask m, float fg = 200, float bg = 30)
    {
        var d = new float[m.PixelCount];
        for (int i = 0; i < d.Length; i++) d[i] = m.Bits[i] ? fg : bg;
        return new Raster(m.Width, m.Height, 1, d);
    }

    public static Raster Gray(int w, int h, Func<int, int, float> f, float max = 255)
    {
        var d = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) d[(y * w) + x] = f(x, y);
        return new Raster(w, h, 1, d, 0, max);
    }
}
