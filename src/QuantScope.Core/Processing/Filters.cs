using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

public enum BackgroundMode
{
    /// <summary>背景が暗い（蛍光など）: 背景を引く</summary>
    Subtract,

    /// <summary>背景が明るい（明視野など）: 背景で割って明るさのむらを消す</summary>
    Divide,
}

/// <summary>近くの画素を使う処理（ぼかし・ノイズ除去・輪郭）。カラーはチャンネルごとにかける。</summary>
public static class Filters
{
    /// <summary>
    /// ガウスぼかし。σ が小さいときはそのままの重みで、大きいときは箱型のぼかしを 3 回重ねて近似する
    /// （σ によらず計算量が一定になり、背景の推定のような大きな σ でも速い）。
    /// </summary>
    public static Raster GaussianBlur(Raster image, double sigma)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(sigma > 0)) return image;
        var outd = new float[image.Data.Length];
        for (int c = 0; c < image.Channels; c++)
        {
            var plane = Plane(image, c);
            var blurred = sigma <= 3 ? GaussianExact(plane, image.Width, image.Height, sigma) : GaussianBoxes(plane, image.Width, image.Height, sigma);
            SetPlane(outd, blurred, c, image.Channels);
        }
        return image.With(outd);
    }

    /// <summary>メディアンフィルター: 周りの (2r+1)² 画素の中央値にする。ごま塩ノイズに強く、輪郭がぼけにくい。</summary>
    public static Raster Median(Raster image, int radius)
    {
        ArgumentNullException.ThrowIfNull(image);
        radius = Math.Clamp(radius, 1, 7);
        int w = image.Width, h = image.Height, ch = image.Channels;
        var src = image.Data;
        var outd = new float[src.Length];
        Parallel.For(0, h, () => new float[(2 * radius + 1) * (2 * radius + 1)], (y, _, buf) =>
        {
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    int n = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, w - 1);
                            buf[n++] = src[(((yy * w) + xx) * ch) + c];
                        }
                    }
                    outd[(((y * w) + x) * ch) + c] = QuickSelect(buf, n, n / 2);
                }
            return buf;
        }, _ => { });
        return image.With(outd);
    }

    /// <summary>アンシャープマスク: 元 + 強さ × (元 − ぼかし)。輪郭を強調する（鮮鋭化）。</summary>
    public static Raster Sharpen(Raster image, double sigma, double amount)
    {
        ArgumentNullException.ThrowIfNull(image);
        var blur = GaussianBlur(image, sigma);
        var d = new float[image.Data.Length];
        for (int i = 0; i < d.Length; i++)
            d[i] = image.Clamp((float)(image.Data[i] + (amount * (image.Data[i] - blur.Data[i]))));
        return image.With(d);
    }

    /// <summary>
    /// 輪郭の強さ（Sobel）: 横と縦の傾き Gx・Gy から √(Gx² + Gy²) を求める。結果は白黒。
    /// </summary>
    public static Raster Sobel(Raster image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var g = image.ToGray();
        int w = g.Width, h = g.Height;
        var s = g.Data;
        var outd = new float[s.Length];
        float At(int x, int y) => s[(Math.Clamp(y, 0, h - 1) * w) + Math.Clamp(x, 0, w - 1)];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float gx = (At(x + 1, y - 1) + (2 * At(x + 1, y)) + At(x + 1, y + 1)) - (At(x - 1, y - 1) + (2 * At(x - 1, y)) + At(x - 1, y + 1));
                float gy = (At(x - 1, y + 1) + (2 * At(x, y + 1)) + At(x + 1, y + 1)) - (At(x - 1, y - 1) + (2 * At(x, y - 1)) + At(x + 1, y - 1));
                // 最大の傾き（白黒の境目）で範囲いっぱいになるよう 1/4 にする
                outd[(y * w) + x] = g.Clamp(g.NominalMin + (MathF.Sqrt((gx * gx) + (gy * gy)) / 4));
            }
        });
        return g.With(outd);
    }

    /// <summary>
    /// 背景の補正: 大きくぼかした画像を「背景（照明のむら）」とみなし、引くか割る。
    /// sigma は対象の大きさより十分大きくする（目安: 対象の直径の 2〜3 倍）。
    /// </summary>
    public static Raster CorrectBackground(Raster image, double sigma, BackgroundMode mode)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bg = GaussianBlur(image, Math.Max(1, sigma));
        var d = new float[image.Data.Length];
        if (mode == BackgroundMode.Subtract)
        {
            for (int i = 0; i < d.Length; i++) d[i] = image.Clamp(image.NominalMin + (image.Data[i] - bg.Data[i]));
        }
        else
        {
            double mean = 0;
            foreach (float v in bg.Data) mean += v;
            mean /= bg.Data.Length;
            for (int i = 0; i < d.Length; i++)
            {
                float b = Math.Max(bg.Data[i] - image.NominalMin, 1e-3f);
                d[i] = image.Clamp((float)(image.NominalMin + ((image.Data[i] - image.NominalMin) / b * (mean - image.NominalMin))));
            }
        }
        return image.With(d);
    }

    /// <summary>
    /// ノイズを加える（学習用: 除去のフィルターを試すため）。sd は範囲に対する割合のガウスノイズ、
    /// saltPepper はごま塩にする画素の割合。seed が同じなら毎回同じノイズになる。
    /// </summary>
    public static Raster AddNoise(Raster image, double sd, double saltPepper, int seed)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rng = new Random(seed);
        var d = new float[image.Data.Length];
        double s = sd * image.NominalRange;
        int ch = image.Channels;
        for (int i = 0; i < image.PixelCount; i++)
        {
            bool salt = saltPepper > 0 && rng.NextDouble() < saltPepper;
            bool white = rng.NextDouble() < 0.5;
            for (int c = 0; c < ch; c++)
            {
                int j = (i * ch) + c;
                if (salt) d[j] = white ? image.NominalMax : image.NominalMin;
                else d[j] = image.Clamp((float)(image.Data[j] + (s > 0 ? s * Gaussian(rng) : 0)));
            }
        }
        return image.With(d);
    }

    // ---------------------------------------------------------------- 内部

    internal static float[] Plane(Raster image, int c)
    {
        if (image.Channels == 1) return image.Data;
        var p = new float[image.PixelCount];
        for (int i = 0; i < p.Length; i++) p[i] = image.Data[(i * image.Channels) + c];
        return p;
    }

    private static void SetPlane(float[] dst, float[] plane, int c, int channels)
    {
        if (channels == 1)
        {
            Array.Copy(plane, dst, plane.Length);
            return;
        }
        for (int i = 0; i < plane.Length; i++) dst[(i * channels) + c] = plane[i];
    }

    internal static float[] GaussianExact(float[] src, int w, int h, double sigma)
    {
        int r = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var k = new float[(2 * r) + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++)
        {
            k[i + r] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
            sum += k[i + r];
        }
        for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);

        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int i = -r; i <= r; i++) a += k[i + r] * src[row + Math.Clamp(x + i, 0, w - 1)];
                tmp[row + x] = a;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int i = -r; i <= r; i++) a += k[i + r] * tmp[(Math.Clamp(y + i, 0, h - 1) * w) + x];
                dst[(y * w) + x] = a;
            }
        });
        return dst;
    }

    /// <summary>箱型のぼかしを 3 回重ねたガウスの近似（Kovesi の方法で箱の幅を決める）</summary>
    internal static float[] GaussianBoxes(float[] src, int w, int h, double sigma)
    {
        const int n = 3;
        double wIdeal = Math.Sqrt((12 * sigma * sigma / n) + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = ((12 * sigma * sigma) - (n * wl * wl) - (4 * n * wl) - (3 * n)) / ((-4 * wl) - 4);
        int m = (int)Math.Round(mIdeal);
        var cur = src;
        for (int i = 0; i < n; i++)
        {
            int r = ((i < m ? wl : wu) - 1) / 2;
            cur = BoxBlur(cur, w, h, Math.Max(r, 0));
        }
        return cur;
    }

    private static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        if (r == 0) return (float[])src.Clone();
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        float inv = 1f / ((2 * r) + 1);
        // 端は端の画素を伸ばしたものとして数える（累積和を順にずらす）
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            double acc = 0;
            for (int i = -r; i <= r; i++) acc += src[row + Math.Clamp(i, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = (float)(acc * inv);
                acc += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        });
        Parallel.For(0, w, x =>
        {
            double acc = 0;
            for (int i = -r; i <= r; i++) acc += tmp[(Math.Clamp(i, 0, h - 1) * w) + x];
            for (int y = 0; y < h; y++)
            {
                dst[(y * w) + x] = (float)(acc * inv);
                acc += tmp[(Math.Min(y + r + 1, h - 1) * w) + x] - tmp[(Math.Max(y - r, 0) * w) + x];
            }
        });
        return dst;
    }

    private static float QuickSelect(float[] a, int n, int k)
    {
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            float pivot = a[(lo + hi) >> 1];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else break;
        }
        return a[k];
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
