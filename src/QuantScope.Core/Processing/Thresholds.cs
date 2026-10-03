using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

public enum ThresholdMethod
{
    /// <summary>自分で決める</summary>
    Manual,

    /// <summary>大津の判別分析: 2 つの組の「間の分散」が最大になる値</summary>
    Otsu,

    /// <summary>三角法: 山の頂点と端を結ぶ線から最も離れた値（対象が少ないときに強い）</summary>
    Triangle,

    /// <summary>平均値</summary>
    Mean,

    /// <summary>反復法（IsoData）: 2 つの組の平均の中点を、変わらなくなるまで繰り返す</summary>
    IsoData,
}

/// <summary>二値化（白黒の 2 つに分ける）。しきい値の自動の決め方と、マスクの作り方。</summary>
public static class Thresholds
{
    /// <summary>
    /// しきい値を自動で決める。戻り値は画素値の単位で、「この値以上」が明るい側。
    /// within を渡すと、その範囲の画素だけで決める。
    /// </summary>
    public static double Find(Raster image, ThresholdMethod method, Mask? within = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var h = Histogram.Of(image, within);
        if (h.Total == 0) return h.Min;
        int k = method switch
        {
            ThresholdMethod.Otsu => Otsu(h.Counts),
            ThresholdMethod.Triangle => Triangle(h.Counts),
            ThresholdMethod.IsoData => IsoData(h.Counts),
            ThresholdMethod.Mean => h.BinOf(h.Mean),
            _ => throw new ArgumentException("自動で決める方法を選んでください。", nameof(method)),
        };
        // 区間 0..k が暗い側。しきい値は区間 k+1 の下端
        return h.BinLower(k + 1);
    }

    /// <summary>lo 以上 hi 以下の画素を対象にする</summary>
    public static Mask Range(Raster image, double lo, double hi)
    {
        ArgumentNullException.ThrowIfNull(image);
        var g = image.ToGray();
        var m = new Mask(g.Width, g.Height);
        for (int i = 0; i < g.Data.Length; i++) m.Bits[i] = g.Data[i] >= lo && g.Data[i] <= hi;
        return m;
    }

    /// <summary>しきい値で分ける。brightObjects なら「しきい値以上」、そうでなければ「しきい値未満」が対象。</summary>
    public static Mask Apply(Raster image, double threshold, bool brightObjects)
    {
        ArgumentNullException.ThrowIfNull(image);
        var g = image.ToGray();
        var m = new Mask(g.Width, g.Height);
        for (int i = 0; i < g.Data.Length; i++) m.Bits[i] = brightObjects ? g.Data[i] >= threshold : g.Data[i] < threshold;
        return m;
    }

    /// <summary>
    /// 色で分ける（HSV）。色相は度（0〜360、hueMin &gt; hueMax なら 0 度をまたぐ範囲）、彩度・明度は 0〜1。
    /// 染色の色（例: ヘマトキシリンの青紫）を取り出すときに使う。
    /// </summary>
    public static Mask Hsv(Raster image, double hueMin, double hueMax, double satMin, double satMax, double valMin, double valMax)
    {
        ArgumentNullException.ThrowIfNull(image);
        var m = new Mask(image.Width, image.Height);
        if (!image.IsColor) return m;
        float min = image.NominalMin, range = image.NominalRange;
        for (int i = 0; i < image.PixelCount; i++)
        {
            int j = i * 3;
            var (hh, s, v) = ToHsv((image.Data[j] - min) / range, (image.Data[j + 1] - min) / range, (image.Data[j + 2] - min) / range);
            bool hueOk = hueMin <= hueMax ? hh >= hueMin && hh <= hueMax : hh >= hueMin || hh <= hueMax;
            m.Bits[i] = hueOk && s >= satMin && s <= satMax && v >= valMin && v <= valMax;
        }
        return m;
    }

    /// <summary>RGB（0〜1）を HSV（色相 0〜360 度、彩度・明度 0〜1）にする</summary>
    public static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        return (h, max > 0 ? d / max : 0, max);
    }

    // ---------------------------------------------------------------- しきい値の決め方（区間の番号を返す。0..k が暗い側）

    internal static int Otsu(long[] counts)
    {
        long total = 0;
        double sumAll = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += counts[i];
            sumAll += (double)i * counts[i];
        }
        var between = new double[counts.Length];
        double best = -1, sumB = 0;
        long wB = 0;
        for (int i = 0; i < counts.Length - 1; i++)
        {
            wB += counts[i];
            sumB += (double)i * counts[i];
            long wF = total - wB;
            if (wB == 0 || wF == 0) continue;
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            between[i] = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between[i] > best) best = between[i];
        }
        if (best <= 0) return 0;
        // 最大の値が続く（2 つの山の間に画素がない）ときは、その真ん中を選ぶ
        int first = -1, last = -1;
        for (int i = 0; i < between.Length; i++)
            if (between[i] >= best * (1 - 1e-12))
            {
                if (first < 0) first = i;
                last = i;
            }
        return (first + last) / 2;
    }

    internal static int Triangle(long[] counts)
    {
        int n = counts.Length;
        int first = Array.FindIndex(counts, c => c > 0), last = Array.FindLastIndex(counts, c => c > 0);
        if (first < 0 || first == last) return Math.Max(first, 0);
        int peak = first;
        for (int i = first; i <= last; i++) if (counts[i] > counts[peak]) peak = i;
        // 山の頂点から、遠い側の端へ線を引く
        bool towardHigh = (last - peak) > (peak - first);
        int end = towardHigh ? last : first;
        double x1 = peak, y1 = counts[peak], x2 = end, y2 = counts[end];
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt((dx * dx) + (dy * dy));
        int best = peak;
        double bestD = -1;
        int a = Math.Min(peak, end), b = Math.Max(peak, end);
        for (int i = a; i <= b; i++)
        {
            double d = Math.Abs((dy * i) - (dx * counts[i]) + (x2 * y1) - (y2 * x1)) / len;
            if (d > bestD)
            {
                bestD = d;
                best = i;
            }
        }
        // 頂点が明るい側にあるとき（背景が明るい）は、暗い側の区間を返す
        return Math.Clamp(towardHigh ? best : best - 1, 0, n - 2);
    }

    internal static int IsoData(long[] counts)
    {
        int n = counts.Length;
        double t = 0;
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            total += counts[i];
            t += (double)i * counts[i];
        }
        if (total == 0) return 0;
        t /= total;
        for (int iter = 0; iter < 100; iter++)
        {
            double s0 = 0, s1 = 0;
            long n0 = 0, n1 = 0;
            for (int i = 0; i < n; i++)
            {
                if (i <= t)
                {
                    s0 += (double)i * counts[i];
                    n0 += counts[i];
                }
                else
                {
                    s1 += (double)i * counts[i];
                    n1 += counts[i];
                }
            }
            if (n0 == 0 || n1 == 0) break;
            double next = ((s0 / n0) + (s1 / n1)) / 2;
            if (Math.Abs(next - t) < 0.5)
            {
                t = next;
                break;
            }
            t = next;
        }
        return Math.Clamp((int)Math.Floor(t), 0, n - 2);
    }
}
