using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.Core.Rendering;

/// <summary>白黒の画像の色の付け方（表示だけ。計測の値は変わらない）</summary>
public enum ColorMap
{
    Gray,
    Fire,
    Viridis,
}

public enum OverlayColoring
{
    /// <summary>すべての粒を同じ色（マゼンタ）で</summary>
    Uniform,

    /// <summary>粒ごとに違う色で（くっついた粒が分かれたかを見やすい）</summary>
    PerObject,
}

/// <summary>
/// 画面に出すための色（BGRA、1 画素 4 バイト）を作る。
/// 16bit などの画像は、明るさの範囲（下と上の 0.1% を除く）を 0〜255 に広げて見せる。
/// </summary>
public static class DisplayRenderer
{
    public const uint MaskArgb = 0xFFFF5FAA;
    public const uint SelectedArgb = 0xFF45D19A;

    private static readonly uint[][] Luts =
    [
        BuildLut(ColorMap.Gray),
        BuildLut(ColorMap.Fire),
        BuildLut(ColorMap.Viridis),
    ];

    /// <summary>表示に使う明るさの範囲。8bit はそのまま 0〜255、それ以外は 0.1%〜99.9% の範囲。</summary>
    public static (double Lo, double Hi) DisplayRange(Raster image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.NominalMin == 0 && image.NominalMax == 255) return (0, 255);
        var (min, max) = image.ToGray().ValueRange();
        if (!(max > min)) return (min, min + 1);
        var h = Histogram.Of(image, bins: 2048, range: (min, max));
        double lo = h.Quantile(0.001), hi = h.Quantile(0.999);
        return hi > lo ? (lo, hi) : (min, max);
    }

    public static byte[] ToBgra(Raster image, ColorMap map, (double Lo, double Hi)? range = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (lo, hi) = range ?? DisplayRange(image);
        double k = 255.0 / Math.Max(hi - lo, 1e-9);
        var outb = new byte[image.PixelCount * 4];
        var lut = Luts[(int)map];
        var d = image.Data;
        if (image.IsColor)
        {
            Parallel.For(0, image.Height, y =>
            {
                for (int x = 0; x < image.Width; x++)
                {
                    int i = (y * image.Width) + x, j = i * 3, o = i * 4;
                    outb[o] = Byte((d[j + 2] - lo) * k);
                    outb[o + 1] = Byte((d[j + 1] - lo) * k);
                    outb[o + 2] = Byte((d[j] - lo) * k);
                    outb[o + 3] = 255;
                }
            });
        }
        else
        {
            Parallel.For(0, image.Height, y =>
            {
                for (int x = 0; x < image.Width; x++)
                {
                    int i = (y * image.Width) + x, o = i * 4;
                    uint c = lut[Byte((d[i] - lo) * k)];
                    outb[o] = (byte)c;
                    outb[o + 1] = (byte)(c >> 8);
                    outb[o + 2] = (byte)(c >> 16);
                    outb[o + 3] = 255;
                }
            });
        }
        return outb;
    }

    /// <summary>マスクを半透明の色に（対象の中は薄く、ふちは濃く）</summary>
    public static byte[] MaskOverlay(Mask mask, uint argb = MaskArgb)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.Width, h = mask.Height;
        var outb = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                if (!mask.Bits[i]) continue;
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1 || !mask.Bits[i - 1] || !mask.Bits[i + 1] || !mask.Bits[i - w] || !mask.Bits[i + w];
                Put(outb, i * 4, argb, edge ? 255 : 110);
            }
        });
        return outb;
    }

    /// <summary>計測した粒を色で示す。selectedId の粒はミントの緑で強調する。</summary>
    public static byte[] LabelOverlay(LabelImage labels, OverlayColoring coloring, int selectedId = 0)
    {
        ArgumentNullException.ThrowIfNull(labels);
        int w = labels.Width, h = labels.Height;
        var outb = new byte[w * h * 4];
        var l = labels.Labels;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                int id = l[i];
                if (id == 0) continue;
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1 || l[i - 1] != id || l[i + 1] != id || l[i - w] != id || l[i + w] != id;
                uint c = id == selectedId ? SelectedArgb : coloring == OverlayColoring.PerObject ? ObjectColor(id) : MaskArgb;
                Put(outb, i * 4, c, edge ? 255 : id == selectedId ? 150 : 100);
            }
        });
        return outb;
    }

    /// <summary>画像の上に重ね合わせを合成する（保存用。opacity は重ね合わせの濃さ 0〜1）</summary>
    public static byte[] Compose(byte[] baseBgra, byte[] overlayBgra, double opacity)
    {
        ArgumentNullException.ThrowIfNull(baseBgra);
        ArgumentNullException.ThrowIfNull(overlayBgra);
        if (baseBgra.Length != overlayBgra.Length) throw new ArgumentException("大きさが違います。", nameof(overlayBgra));
        var outb = (byte[])baseBgra.Clone();
        for (int i = 0; i < outb.Length; i += 4)
        {
            double a = overlayBgra[i + 3] / 255.0 * opacity;
            if (a <= 0) continue;
            for (int c = 0; c < 3; c++) outb[i + c] = (byte)Math.Round((outb[i + c] * (1 - a)) + (overlayBgra[i + c] * a));
        }
        return outb;
    }

    /// <summary>マスクを白黒の画像に（対象 = 白）。ほかのソフトで使うための保存用</summary>
    public static byte[] MaskToBgra(Mask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var outb = new byte[mask.PixelCount * 4];
        for (int i = 0; i < mask.PixelCount; i++)
        {
            byte v = mask.Bits[i] ? (byte)255 : (byte)0;
            outb[i * 4] = v;
            outb[(i * 4) + 1] = v;
            outb[(i * 4) + 2] = v;
            outb[(i * 4) + 3] = 255;
        }
        return outb;
    }

    /// <summary>粒の色（黄金角で色相をずらし、隣の番号どうしが似た色にならないように）</summary>
    public static uint ObjectColor(int id)
    {
        double hue = (id * 137.508) % 360;
        var (r, g, b) = HsvToRgb(hue, 0.65, 1.0);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>色の表（0〜255 → 0xAARRGGBB）。凡例の表示にも使う</summary>
    public static uint[] Lut(ColorMap map) => (uint[])Luts[(int)map].Clone();

    private static uint[] BuildLut(ColorMap map)
    {
        var lut = new uint[256];
        // Viridis は代表の色の間を補間する
        (double T, byte R, byte G, byte B)[] viridis =
        [
            (0.0, 68, 1, 84), (0.13, 71, 44, 122), (0.25, 59, 81, 139), (0.38, 44, 113, 142), (0.5, 33, 144, 141),
            (0.63, 39, 173, 129), (0.75, 92, 200, 99), (0.88, 170, 220, 50), (1.0, 253, 231, 37),
        ];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            byte r, g, b;
            switch (map)
            {
                case ColorMap.Fire:
                    // 黒 → 赤 → 黄 → 白
                    r = Byte(t * 3 * 255);
                    g = Byte(((t * 3) - 1) * 255);
                    b = Byte(((t * 3) - 2) * 255);
                    break;
                case ColorMap.Viridis:
                    int k = 0;
                    while (k < viridis.Length - 2 && t > viridis[k + 1].T) k++;
                    var a0 = viridis[k];
                    var a1 = viridis[k + 1];
                    double f = (t - a0.T) / (a1.T - a0.T);
                    r = Byte(a0.R + ((a1.R - a0.R) * f));
                    g = Byte(a0.G + ((a1.G - a0.G) * f));
                    b = Byte(a0.B + ((a1.B - a0.B) * f));
                    break;
                default:
                    r = g = b = (byte)i;
                    break;
            }
            lut[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return lut;
    }

    private static void Put(byte[] b, int o, uint argb, int alpha)
    {
        b[o] = (byte)argb;
        b[o + 1] = (byte)(argb >> 8);
        b[o + 2] = (byte)(argb >> 16);
        b[o + 3] = (byte)alpha;
    }

    private static byte Byte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (Byte((r + m) * 255), Byte((g + m) * 255), Byte((b + m) * 255));
    }
}
