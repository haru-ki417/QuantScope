namespace QuantScope.Core.Imaging;

/// <summary>
/// 明るさの度数分布と、基本の統計量。白黒の画像（カラーは明るさ Y）から作る。
/// 区間は [Min, Max] を Bins 等分する（8bit の画像なら 1 区間 = 1 段階）。
/// </summary>
public sealed class Histogram
{
    private Histogram(long[] counts, double min, double max, long total, double mean, double sd, double dataMin, double dataMax, double median)
    {
        Counts = counts;
        Min = min;
        Max = max;
        Total = total;
        Mean = mean;
        StdDev = sd;
        DataMin = dataMin;
        DataMax = dataMax;
        Median = median;
    }

    public long[] Counts { get; }

    /// <summary>区間の下端・上端</summary>
    public double Min { get; }
    public double Max { get; }
    public int Bins => Counts.Length;
    public double BinWidth => (Max - Min) / Bins;

    /// <summary>数えた画素の数</summary>
    public long Total { get; }
    public double Mean { get; }
    public double StdDev { get; }
    public double DataMin { get; }
    public double DataMax { get; }
    public double Median { get; }

    /// <summary>いちばん多い区間の値（区間の中央）</summary>
    public double Mode
    {
        get
        {
            int best = 0;
            for (int i = 1; i < Counts.Length; i++) if (Counts[i] > Counts[best]) best = i;
            return BinCenter(best);
        }
    }

    public double BinCenter(int i) => Min + ((i + 0.5) * BinWidth);

    /// <summary>区間 i の下端（しきい値として「この値以上」に使う）</summary>
    public double BinLower(int i) => Min + (i * BinWidth);

    public int BinOf(double v)
    {
        int i = (int)((v - Min) / (Max - Min) * Bins);
        return Math.Clamp(i, 0, Bins - 1);
    }

    /// <summary>
    /// 画像（と範囲のマスク）から作る。range を省くと、8bit の画像は 0〜255 を 1 段階ずつ、
    /// それ以外（16bit・CT など）は実際の最小〜最大を使う。
    /// </summary>
    public static Histogram Of(Raster image, Mask? within = null, int bins = 256, (double Min, double Max)? range = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(bins, 2);
        var gray = image.ToGray();
        if (within is not null && !within.SameSize(gray.Width, gray.Height)) throw new ArgumentException("範囲の大きさが画像と違います。", nameof(within));

        double dmin = double.MaxValue, dmax = double.MinValue, sum = 0, sum2 = 0;
        long n = 0;
        var d = gray.Data;
        for (int i = 0; i < d.Length; i++)
        {
            if (within is not null && !within.Bits[i]) continue;
            double v = d[i];
            n++;
            sum += v;
            sum2 += v * v;
            if (v < dmin) dmin = v;
            if (v > dmax) dmax = v;
        }
        if (n == 0) return new Histogram(new long[bins], 0, 1, 0, 0, 0, 0, 0, 0);

        double lo, hi;
        if (range is { } r) (lo, hi) = r;
        else if (IsEightBit(gray)) (lo, hi) = (0, 256);
        else (lo, hi) = (dmin, dmax);
        if (!(hi > lo)) hi = lo + 1;

        var counts = new long[bins];
        double scale = bins / (hi - lo);
        for (int i = 0; i < d.Length; i++)
        {
            if (within is not null && !within.Bits[i]) continue;
            int b = (int)((d[i] - lo) * scale);
            counts[Math.Clamp(b, 0, bins - 1)]++;
        }

        double mean = sum / n;
        double sd = n > 1 ? Math.Sqrt(Math.Max(0, (sum2 - (sum * sum / n)) / (n - 1))) : 0;
        var h = new Histogram(counts, lo, hi, n, mean, sd, dmin, dmax, 0);
        return new Histogram(counts, lo, hi, n, mean, sd, dmin, dmax, h.Quantile(0.5));
    }

    /// <summary>度数分布から求めた q 分位点（0〜1）。区間の中は一様とみなして補間する。</summary>
    public double Quantile(double q)
    {
        if (Total == 0) return 0;
        double target = q * Total;
        long acc = 0;
        for (int i = 0; i < Counts.Length; i++)
        {
            if (acc + Counts[i] >= target && Counts[i] > 0)
            {
                double f = (target - acc) / Counts[i];
                return BinLower(i) + (f * BinWidth);
            }
            acc += Counts[i];
        }
        return Max;
    }

    /// <summary>8bit の画像か（そのときは 1 区間 = 1 段階にする）。16bit などは実際に使っている範囲で区切る。</summary>
    private static bool IsEightBit(Raster r) => r.NominalMin == 0 && r.NominalMax == 255;
}
