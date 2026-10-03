using System.Globalization;

namespace QuantScope.Core.Imaging;

/// <summary>
/// 縮尺（1 画素が何 µm / mm か）。未設定なら 1 画素 = 1 px として数える。
/// </summary>
public sealed record Calibration(double UnitsPerPixel, string Unit)
{
    public static Calibration Pixels { get; } = new(1, "px");

    public bool IsCalibrated => Unit != "px";

    /// <summary>画面上で長さの分かる線を引いて縮尺を決める（線の長さ px と、実際の長さ）</summary>
    public static Calibration FromKnownLength(double pixels, double length, string unit)
    {
        if (!(pixels > 0)) throw new ArgumentOutOfRangeException(nameof(pixels), "線の長さが 0 です。");
        if (!(length > 0)) throw new ArgumentOutOfRangeException(nameof(length), "実際の長さは 0 より大きくしてください。");
        if (string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("単位を入れてください。", nameof(unit));
        return new Calibration(length / pixels, unit.Trim());
    }

    public double Length(double pixels) => pixels * UnitsPerPixel;
    public double Area(double pixels) => pixels * UnitsPerPixel * UnitsPerPixel;

    public string AreaUnit => IsCalibrated ? Unit + "²" : "px²";
    public string LengthUnit => IsCalibrated ? Unit : "px";

    /// <summary>画像を縮小・拡大したときの縮尺（factor = 新しい大きさ / 元の大きさ）</summary>
    public Calibration Scaled(double factor) => this with { UnitsPerPixel = UnitsPerPixel / factor };

    public override string ToString() =>
        IsCalibrated
            ? string.Create(CultureInfo.InvariantCulture, $"1 px = {UnitsPerPixel:0.####} {Unit}")
            : "縮尺なし（px）";
}
