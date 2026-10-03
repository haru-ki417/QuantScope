using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

/// <summary>
/// ユークリッド距離変換: 各画素から「いちばん近い背景（false）の画素」までの距離。
/// Felzenszwalb と Huttenlocher の方法（縦・横の 1 次元に分けて、画素数に比例する時間で正確に求める）。
/// 画像の外は背景とみなす。
/// </summary>
public static class DistanceTransform
{
    private const double Inf = 1e20;

    /// <summary>対象（true）の各画素から、最も近い背景までの距離（背景の画素は 0）</summary>
    public static float[] ToBackground(Mask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        return Compute(mask.Width, mask.Height, i => !mask.Bits[i], outsideIsFeature: true);
    }

    /// <summary>各画素から、最も近い対象（true）までの距離（対象の画素は 0）</summary>
    public static float[] ToForeground(Mask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        return Compute(mask.Width, mask.Height, i => mask.Bits[i], outsideIsFeature: false);
    }

    private static float[] Compute(int w, int h, Func<int, bool> isFeature, bool outsideIsFeature)
    {
        // 画像の外を「特徴（距離 0）」として扱うときは、まわりに 1 画素の枠を足して計算する
        int pad = outsideIsFeature ? 1 : 0;
        int W = w + (2 * pad), H = h + (2 * pad);
        var f = new double[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int ox = x - pad, oy = y - pad;
                bool inside = ox >= 0 && oy >= 0 && ox < w && oy < h;
                bool feature = inside ? isFeature((oy * w) + ox) : outsideIsFeature;
                f[(y * W) + x] = feature ? 0 : Inf;
            }

        // 縦方向
        Parallel.For(0, W, () => (new double[H], new double[H], new int[H], new double[H + 1]), (x, _, buf) =>
        {
            var (col, d, v, z) = buf;
            for (int y = 0; y < H; y++) col[y] = f[(y * W) + x];
            OneDim(col, d, v, z, H);
            for (int y = 0; y < H; y++) f[(y * W) + x] = d[y];
            return buf;
        }, _ => { });

        // 横方向
        Parallel.For(0, H, () => (new double[W], new double[W], new int[W], new double[W + 1]), (y, _, buf) =>
        {
            var (row, d, v, z) = buf;
            Array.Copy(f, y * W, row, 0, W);
            OneDim(row, d, v, z, W);
            Array.Copy(d, 0, f, y * W, W);
            return buf;
        }, _ => { });

        var outd = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                outd[(y * w) + x] = (float)Math.Sqrt(f[((y + pad) * W) + x + pad]);
        return outd;
    }

    /// <summary>1 次元の二乗距離（放物線の下側の包絡線）</summary>
    private static void OneDim(double[] f, double[] d, int[] v, double[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = Intersect(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersect(f, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = (dq * dq) + f[v[k]];
        }
    }

    private static double Intersect(double[] f, int q, int p) => ((f[q] + ((double)q * q)) - (f[p] + ((double)p * p))) / (2.0 * (q - p));
}
