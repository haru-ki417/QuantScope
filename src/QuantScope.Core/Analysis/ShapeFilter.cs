using QuantScope.Core.Imaging;

namespace QuantScope.Core.Analysis;

/// <summary>形の条件で粒を選ぶ（丸くないゴミ・重なったかたまり・細長い断片などを除く）</summary>
public static class ShapeFilter
{
    /// <summary>
    /// 円形度・縦横比・充実度がすべて範囲に入る粒だけを残す。maxAspect が 0 以下なら縦横比の上限なし。
    /// </summary>
    public static (Mask Mask, int Kept, int Total) Apply(Mask mask, double minCircularity, double maxCircularity, double maxAspect, double minSolidity, bool eightConnected = true)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var r = ParticleAnalyzer.Analyze(mask, null, Calibration.Pixels, new AnalysisOptions { EightConnected = eightConnected });
        var keep = new bool[r.Particles.Count + 1];
        int kept = 0;
        foreach (var p in r.Particles)
        {
            bool ok = p.Circularity >= minCircularity - 1e-9
                && p.Circularity <= maxCircularity + 1e-9
                && (maxAspect <= 0 || p.AspectRatio <= maxAspect + 1e-9)
                && p.Solidity >= minSolidity - 1e-9;
            keep[p.Id] = ok;
            if (ok) kept++;
        }
        var outMask = new Mask(mask.Width, mask.Height);
        var labels = r.Labels.Labels;
        for (int i = 0; i < labels.Length; i++) outMask.Bits[i] = keep[labels[i]];
        return (outMask, kept, r.Particles.Count);
    }
}
