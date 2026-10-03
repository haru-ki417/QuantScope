using QuantScope.Core.Imaging;
using QuantScope.Core.Processing;

namespace QuantScope.Core.Analysis;

/// <summary>値の並びの要約（記述統計）</summary>
public sealed record Descriptive(int N, double Mean, double Sd, double Min, double Q1, double Median, double Q3, double Max)
{
    public static Descriptive Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>変動係数（標準偏差 / 平均、%）</summary>
    public double CvPercent => Mean != 0 ? 100 * Sd / Math.Abs(Mean) : 0;

    /// <summary>平均の標準誤差</summary>
    public double Sem => N > 0 ? Sd / Math.Sqrt(N) : 0;
}

/// <summary>度数分布の 1 本（下端 ≤ 値 &lt; 上端。最後の区間だけ上端を含む）</summary>
public sealed record Bin(double Lower, double Upper, int Count);

public static class Statistics
{
    /// <summary>平均・標準偏差（不偏）・四分位（線形補間、R の type 7 / Excel の QUARTILE.INC と同じ）</summary>
    public static Descriptive Describe(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) return Descriptive.Empty;
        double mean = v.Average();
        double sd = v.Length > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1)) : 0;
        return new Descriptive(v.Length, mean, sd, v[0], Quantile(v, 0.25), Quantile(v, 0.5), Quantile(v, 0.75), v[^1]);
    }

    /// <summary>並べ替え済みの値の分位点（線形補間）</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0) return 0;
        double h = (sorted.Count - 1) * Math.Clamp(p, 0, 1);
        int lo = (int)Math.Floor(h);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + ((h - lo) * (sorted[hi] - sorted[lo]));
    }

    /// <summary>
    /// 度数分布。区間の数を決めなければ、Freedman–Diaconis の方法（区間の幅 = 2 × 四分位範囲 / n^(1/3)）で決め、5〜40 本に収める。
    /// </summary>
    public static IReadOnlyList<Bin> Histogram(IEnumerable<double> values, int? binCount = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) return [];
        double min = v[0], max = v[^1];
        if (max - min < 1e-12) return [new Bin(min, max, v.Length)];
        int k = binCount ?? AutoBinCount(v);
        k = Math.Max(1, k);
        double width = (max - min) / k;
        var counts = new int[k];
        foreach (double x in v) counts[Math.Min((int)((x - min) / width), k - 1)]++;
        return Enumerable.Range(0, k).Select(i => new Bin(min + (i * width), i == k - 1 ? max : min + ((i + 1) * width), counts[i])).ToList();
    }

    private static int AutoBinCount(double[] sorted)
    {
        double iqr = Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
        double range = sorted[^1] - sorted[0];
        int k = iqr > 0
            ? (int)Math.Ceiling(range / (2 * iqr / Math.Cbrt(sorted.Length)))
            : (int)Math.Ceiling(Math.Log2(sorted.Length) + 1); // Sturges
        return Math.Clamp(k, 5, 40);
    }

    /// <summary>値の並びを 2 組に分ける大津のしきい値（陽性・陰性の境目を自動で決めるのに使う）</summary>
    public static double OtsuThreshold(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var v = values.Where(double.IsFinite).Select(x => (float)x).ToArray();
        if (v.Length == 0) return 0;
        float min = v.Min(), max = v.Max();
        if (max - min < 1e-9) return min;
        // 値の範囲を 0〜255 以外にしておくと、Histogram は実際の範囲を 256 区間に分ける
        var r = new Raster(v.Length, 1, 1, v, min, max + ((max - min) * 1e-6f));
        return Thresholds.Find(r, ThresholdMethod.Otsu);
    }
}
