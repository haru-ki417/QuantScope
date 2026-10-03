using QuantScope.Core.Imaging;

namespace QuantScope.Core.Pipeline;

/// <summary>
/// 手順の途中の状態。
/// Original は「形を変える処理（切り抜き・回転など）だけをかけた元の画像」で、明るさの計測に使う。
/// Image は今の画像、Mask は二値化のあとの対象（二値化の前は null）。
/// </summary>
public sealed record PipelineState(Raster Original, Raster Image, Mask? Mask, Calibration Calibration)
{
    public static PipelineState Start(Raster image, Calibration calibration) => new(image, image, null, calibration);
}

/// <summary>手順を 1 つ実行した結果の知らせ（しきい値の値など）</summary>
public sealed record StepOutcome(bool Applied, string? Info, string? Warning, TimeSpan Elapsed);
