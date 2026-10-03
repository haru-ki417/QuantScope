using QuantScope.Core.Imaging;
using QuantScope.Core.Processing;

namespace QuantScope.Core.Analysis;

/// <summary>粒の「明るさ」として何を測るか</summary>
public enum IntensityChannel
{
    /// <summary>明るさ（カラーは Y = 0.299R + 0.587G + 0.114B）</summary>
    Luminance,
    Red,
    Green,
    Blue,

    /// <summary>ヘマトキシリンの量（H-DAB の組で分けた OD）</summary>
    Hematoxylin,

    /// <summary>エオシンの量（H&amp;E の組で分けた OD）</summary>
    Eosin,

    /// <summary>DAB の量（H-DAB の組で分けた OD）</summary>
    Dab,
}

public static class IntensityChannels
{
    public static IReadOnlyList<string> Titles { get; } = ["明るさ", "赤 (R)", "緑 (G)", "青 (B)", "ヘマトキシリンの量", "エオシンの量", "DAB の量"];

    public static string Title(IntensityChannel c) => Titles[(int)c];

    public static bool NeedsColor(IntensityChannel c) => c != IntensityChannel.Luminance;

    /// <summary>
    /// 測る値の白黒の画像を作る。白黒の画像で色や染色を選んだときは明るさにし、usedFallback を true にする。
    /// </summary>
    public static Raster Extract(Raster image, IntensityChannel channel, out bool usedFallback)
    {
        ArgumentNullException.ThrowIfNull(image);
        usedFallback = false;
        if (channel == IntensityChannel.Luminance) return image.ToGray();
        if (!image.IsColor)
        {
            usedFallback = true;
            return image;
        }
        return channel switch
        {
            IntensityChannel.Red => Adjust.ExtractChannel(image, ColorChannel.Red),
            IntensityChannel.Green => Adjust.ExtractChannel(image, ColorChannel.Green),
            IntensityChannel.Blue => Adjust.ExtractChannel(image, ColorChannel.Blue),
            IntensityChannel.Hematoxylin => ColorDeconvolution.Amount(image, StainChannel.HematoxylinHDab),
            IntensityChannel.Eosin => ColorDeconvolution.Amount(image, StainChannel.EosinHE),
            _ => ColorDeconvolution.Amount(image, StainChannel.Dab),
        };
    }
}
