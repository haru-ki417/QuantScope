using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

public enum ColorChannel
{
    Red,
    Green,
    Blue,
}

/// <summary>明るさ・色の調整（画素ごとの変換）。どれも新しい画像を返す。</summary>
public static class Adjust
{
    /// <summary>R・G・B のうち 1 つを取り出して白黒にする（染色ごとに分けて見るときなど）</summary>
    public static Raster ExtractChannel(Raster image, ColorChannel channel)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsColor) return image;
        int c = (int)channel;
        var g = new float[image.PixelCount];
        for (int i = 0; i < g.Length; i++) g[i] = image.Data[(i * 3) + c];
        return image.With(g, channels: 1);
    }

    /// <summary>ネガとポジを入れかえる（範囲の上端と下端を軸に反転）</summary>
    public static Raster Invert(Raster image)
    {
        ArgumentNullException.ThrowIfNull(image);
        float s = image.NominalMin + image.NominalMax;
        return Map(image, v => s - v);
    }

    /// <summary>
    /// ウインドウ・レベル: level を中心に幅 width の範囲を 0〜255 に広げる。範囲の外は黒・白に張りつく。
    /// CT の HU や 16bit の画像を、見たい組織に合わせて 8bit にするときに使う。
    /// </summary>
    public static Raster WindowLevel(Raster image, double level, double width)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(width > 0)) throw new ArgumentOutOfRangeException(nameof(width), "幅は 0 より大きくしてください。");
        double lo = level - (width / 2);
        double k = 255.0 / width;
        var d = new float[image.Data.Length];
        for (int i = 0; i < d.Length; i++) d[i] = (float)Math.Clamp((image.Data[i] - lo) * k, 0, 255);
        return image.With(d, nominalMin: 0, nominalMax: 255);
    }

    /// <summary>
    /// 自動コントラスト: 暗い側・明るい側の saturatedPercent % ずつを飛ばし、残りを範囲いっぱいに広げる。
    /// </summary>
    public static Raster AutoContrast(Raster image, double saturatedPercent)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (vmin, vmax) = image.ToGray().ValueRange();
        if (!(vmax > vmin)) return image;
        var h = Histogram.Of(image, bins: 1024, range: (vmin, vmax));
        double p = Math.Clamp(saturatedPercent, 0, 49) / 100;
        double lo = h.Quantile(p), hi = h.Quantile(1 - p);
        if (!(hi > lo)) return image;
        return Stretch(image, lo, hi);
    }

    /// <summary>lo〜hi を画像の値の範囲いっぱいに広げる</summary>
    public static Raster Stretch(Raster image, double lo, double hi)
    {
        ArgumentNullException.ThrowIfNull(image);
        double k = image.NominalRange / (hi - lo);
        float min = image.NominalMin;
        return Map(image, v => (float)(min + ((v - lo) * k)));
    }

    /// <summary>
    /// 明るさとコントラスト。brightness は範囲に対する割合（−1〜1）、contrast は倍率（中央の値を中心に広げる）。
    /// </summary>
    public static Raster BrightnessContrast(Raster image, double brightness, double contrast)
    {
        ArgumentNullException.ThrowIfNull(image);
        double mid = image.NominalMin + (image.NominalRange / 2);
        double offset = brightness * image.NominalRange;
        return Map(image, v => (float)(((v - mid) * contrast) + mid + offset));
    }

    /// <summary>ガンマ補正: 出力 = 最大 × (入力 / 最大)^γ。γ &lt; 1 で暗い部分が持ち上がる。</summary>
    public static Raster Gamma(Raster image, double gamma)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(gamma > 0)) throw new ArgumentOutOfRangeException(nameof(gamma));
        float min = image.NominalMin, range = image.NominalRange;
        return Map(image, v => min + (range * (float)Math.Pow(Math.Clamp((v - min) / range, 0, 1), gamma)));
    }

    /// <summary>
    /// ヒストグラム平坦化: 累積度数を使って、明るさの分布をなるべく平らにする。
    /// カラーは明るさ（Y）だけを変え、色合いは保つ。
    /// </summary>
    public static Raster Equalize(Raster image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var gray = image.ToGray();
        var (lo, hi) = gray.ValueRange();
        if (!(hi > lo)) return image;
        var h = Histogram.Of(gray, bins: 256, range: (lo, hi + 1e-3));
        var cdf = new double[256];
        long acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += h.Counts[i];
            cdf[i] = (double)acc / h.Total;
        }
        var mapped = new float[gray.PixelCount];
        for (int i = 0; i < mapped.Length; i++)
            mapped[i] = (float)(image.NominalMin + (cdf[h.BinOf(gray.Data[i])] * image.NominalRange));
        return ReplaceLuminance(image, gray, mapped);
    }

    /// <summary>
    /// 局所コントラスト（CLAHE）: 画像を tiles × tiles の区画に分け、区画ごとに平坦化する。
    /// 1 つの値に画素が集まりすぎないよう clipLimit（平均の何倍まで）で度数を切り、区画の間はなめらかにつなぐ。
    /// </summary>
    public static Raster Clahe(Raster image, int tiles, double clipLimit)
    {
        ArgumentNullException.ThrowIfNull(image);
        tiles = Math.Clamp(tiles, 1, 32);
        var gray = image.ToGray();
        var (lo, hi) = gray.ValueRange();
        if (!(hi > lo)) return image;
        const int bins = 256;
        int w = gray.Width, h = gray.Height;
        int tx = Math.Min(tiles, w), ty = Math.Min(tiles, h);
        double tw = (double)w / tx, th = (double)h / ty;
        double scale = bins / (hi - lo + 1e-6);

        // 区画ごとの対応表（入力の区間 → 0〜1）
        var maps = new double[tx * ty][];
        Parallel.For(0, tx * ty, t =>
        {
            int cx = t % tx, cy = t / tx;
            int x0 = (int)(cx * tw), x1 = (int)((cx + 1) * tw), y0 = (int)(cy * th), y1 = (int)((cy + 1) * th);
            var hist = new double[bins];
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int b = Math.Clamp((int)((gray.Data[(y * w) + x] - lo) * scale), 0, bins - 1);
                    hist[b]++;
                    n++;
                }
            if (n == 0)
            {
                maps[t] = Enumerable.Range(0, bins).Select(i => i / (double)(bins - 1)).ToArray();
                return;
            }
            double limit = Math.Max(1, clipLimit * n / bins);
            double excess = 0;
            for (int i = 0; i < bins; i++)
                if (hist[i] > limit)
                {
                    excess += hist[i] - limit;
                    hist[i] = limit;
                }
            double add = excess / bins;
            var map = new double[bins];
            double acc = 0;
            for (int i = 0; i < bins; i++)
            {
                acc += hist[i] + add;
                map[i] = acc / n;
            }
            maps[t] = map;
        });

        var outv = new float[gray.PixelCount];
        Parallel.For(0, h, y =>
        {
            // 区画の中心どうしの間を双線形に補間する
            double gy = ((y + 0.5) / th) - 0.5;
            int ya = Math.Clamp((int)Math.Floor(gy), 0, ty - 1), yb = Math.Min(ya + 1, ty - 1);
            double fy = Math.Clamp(gy - ya, 0, 1);
            for (int x = 0; x < w; x++)
            {
                double gx = ((x + 0.5) / tw) - 0.5;
                int xa = Math.Clamp((int)Math.Floor(gx), 0, tx - 1), xb = Math.Min(xa + 1, tx - 1);
                double fx = Math.Clamp(gx - xa, 0, 1);
                int b = Math.Clamp((int)((gray.Data[(y * w) + x] - lo) * scale), 0, bins - 1);
                double v = ((1 - fy) * (((1 - fx) * maps[(ya * tx) + xa][b]) + (fx * maps[(ya * tx) + xb][b])))
                         + (fy * (((1 - fx) * maps[(yb * tx) + xa][b]) + (fx * maps[(yb * tx) + xb][b])));
                outv[(y * w) + x] = (float)(image.NominalMin + (v * image.NominalRange));
            }
        });
        return ReplaceLuminance(image, gray, outv);
    }

    /// <summary>画素ごとに同じ式をかけ、値の範囲に収める</summary>
    public static Raster Map(Raster image, Func<float, float> f)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(f);
        var d = new float[image.Data.Length];
        for (int i = 0; i < d.Length; i++) d[i] = image.Clamp(f(image.Data[i]));
        return image.With(d);
    }

    /// <summary>白黒ならそのまま、カラーは明るさの比で R・G・B を変えて、色合いを保つ</summary>
    private static Raster ReplaceLuminance(Raster image, Raster gray, float[] newY)
    {
        if (!image.IsColor) return image.With(newY);
        var d = new float[image.Data.Length];
        float min = image.NominalMin;
        for (int i = 0; i < newY.Length; i++)
        {
            float oldY = gray.Data[i] - min;
            float k = oldY > 1e-6f ? (newY[i] - min) / oldY : 1;
            for (int c = 0; c < 3; c++)
            {
                int j = (i * 3) + c;
                d[j] = oldY > 1e-6f ? image.Clamp(min + ((image.Data[j] - min) * k)) : newY[i];
            }
        }
        return image.With(d);
    }
}
