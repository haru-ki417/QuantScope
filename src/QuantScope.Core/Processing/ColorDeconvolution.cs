using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

/// <summary>取り出す染色（組み合わせごとに分け方が変わるので、組と一緒に選ぶ）</summary>
public enum StainChannel
{
    /// <summary>H&amp;E のうちヘマトキシリン（核）</summary>
    HematoxylinHE,

    /// <summary>H&amp;E のうちエオシン（細胞質・間質）</summary>
    EosinHE,

    /// <summary>H-DAB（免疫染色）のうちヘマトキシリン（対比染色の核）</summary>
    HematoxylinHDab,

    /// <summary>H-DAB のうち DAB（茶色、抗体が結合した所）</summary>
    Dab,
}

/// <summary>
/// 色の分離（カラーデコンボリューション、Ruifrok と Johnston, 2001）。
/// 明視野の染色画像では、光は染料に吸収され、吸光度（OD = −log₁₀(I / I₀)）は染料の量に比例して足し合わさる。
/// 各染料の色（RGB での吸光度の向き）が分かっていれば、画素の OD を染料ごとの量に分けられる。
/// </summary>
public static class ColorDeconvolution
{
    // Ruifrok と Johnston の論文の値（ImageJ の Colour Deconvolution と同じ）
    private static readonly double[] Hematoxylin = [0.650, 0.704, 0.286];
    private static readonly double[] Eosin = [0.072, 0.990, 0.105];
    private static readonly double[] DabVector = [0.268, 0.570, 0.776];

    /// <summary>出力の値の上限（OD）。これより濃い所は張りつく</summary>
    public const float MaxOpticalDensity = 3f;

    public static string Title(StainChannel s) => s switch
    {
        StainChannel.HematoxylinHE => "ヘマトキシリン（H&E）",
        StainChannel.EosinHE => "エオシン（H&E）",
        StainChannel.HematoxylinHDab => "ヘマトキシリン（H-DAB）",
        _ => "DAB（H-DAB）",
    };

    /// <summary>
    /// 1 つの染料の量（OD）を白黒の画像にする。濃く染まった所ほど大きい値（0〜3）。
    /// </summary>
    public static Raster Amount(Raster color, StainChannel stain)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (!color.IsColor) throw new ArgumentException("色を分けるには、カラーの画像が必要です。", nameof(color));
        var (a, b, index) = stain switch
        {
            StainChannel.HematoxylinHE => (Hematoxylin, Eosin, 0),
            StainChannel.EosinHE => (Hematoxylin, Eosin, 1),
            StainChannel.HematoxylinHDab => (Hematoxylin, DabVector, 0),
            _ => (Hematoxylin, DabVector, 1),
        };
        var inv = Inverse(StainMatrix(a, b));
        // 求める染料の列だけを使う: C_k = Σ_c OD_c · inv[c, k]
        double k0 = inv[0, index], k1 = inv[1, index], k2 = inv[2, index];
        double i0 = color.NominalMax;
        var lut = BuildOdLut(color);
        var src = color.Data;
        var dst = new float[color.PixelCount];
        Parallel.For(0, color.Height, y =>
        {
            int row = y * color.Width;
            for (int x = 0; x < color.Width; x++)
            {
                int i = row + x, j = i * 3;
                double r = Od(src[j], i0, lut), g = Od(src[j + 1], i0, lut), bl = Od(src[j + 2], i0, lut);
                double c = (r * k0) + (g * k1) + (bl * k2);
                dst[i] = (float)Math.Clamp(c, 0, MaxOpticalDensity);
            }
        });
        return new Raster(color.Width, color.Height, 1, dst, 0, MaxOpticalDensity);
    }

    /// <summary>8bit の画像は 256 通りしかないので、OD を先に表にしておく</summary>
    private static double[]? BuildOdLut(Raster color)
    {
        if (color.NominalMin != 0 || color.NominalMax != 255) return null;
        var lut = new double[256];
        for (int v = 0; v < 256; v++) lut[v] = -Math.Log10(Math.Max(v, 1) / 255.0);
        return lut;
    }

    private static double Od(float v, double i0, double[]? lut)
    {
        if (lut is not null)
        {
            int k = (int)Math.Round(v);
            return lut[Math.Clamp(k, 0, 255)];
        }
        return -Math.Log10(Math.Max(v, 1) / i0);
    }

    /// <summary>2 つの染料の向きと、それに直交する 3 つ目（残り）を、単位ベクトルにして行に並べる</summary>
    internal static double[,] StainMatrix(double[] a, double[] b)
    {
        var u = Normalize(a);
        var v = Normalize(b);
        var w = Normalize([(u[1] * v[2]) - (u[2] * v[1]), (u[2] * v[0]) - (u[0] * v[2]), (u[0] * v[1]) - (u[1] * v[0])]);
        var m = new double[3, 3];
        for (int c = 0; c < 3; c++)
        {
            m[0, c] = u[c];
            m[1, c] = v[c];
            m[2, c] = w[c];
        }
        return m;
    }

    private static double[] Normalize(double[] v)
    {
        double n = Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        return [v[0] / n, v[1] / n, v[2] / n];
    }

    /// <summary>3×3 の逆行列（余因子による）</summary>
    internal static double[,] Inverse(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2];
        double d = m[1, 0], e = m[1, 1], f = m[1, 2];
        double g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        if (Math.Abs(det) < 1e-12) throw new ArgumentException("染料の色が似すぎていて分けられません。");
        var r = new double[3, 3];
        r[0, 0] = ((e * i) - (f * h)) / det;
        r[0, 1] = ((c * h) - (b * i)) / det;
        r[0, 2] = ((b * f) - (c * e)) / det;
        r[1, 0] = ((f * g) - (d * i)) / det;
        r[1, 1] = ((a * i) - (c * g)) / det;
        r[1, 2] = ((c * d) - (a * f)) / det;
        r[2, 0] = ((d * h) - (e * g)) / det;
        r[2, 1] = ((b * g) - (a * h)) / det;
        r[2, 2] = ((a * e) - (b * d)) / det;
        return r;
    }

    /// <summary>染料の量から、その染料だけで染めたときの色（確かめ用。量 c の吸光度を RGB に戻す）</summary>
    public static (byte R, byte G, byte B) StainColor(StainChannel stain, double amount = 1)
    {
        var v = Normalize(stain switch
        {
            StainChannel.EosinHE => Eosin,
            StainChannel.Dab => DabVector,
            _ => Hematoxylin,
        });
        static byte Ch(double od) => (byte)Math.Clamp(Math.Round(255 * Math.Pow(10, -od)), 0, 255);
        return (Ch(v[0] * amount), Ch(v[1] * amount), Ch(v[2] * amount));
    }
}
