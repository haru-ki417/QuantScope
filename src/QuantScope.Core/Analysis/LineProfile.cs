using QuantScope.Core.Imaging;
using QuantScope.Core.Processing;

namespace QuantScope.Core.Analysis;

/// <summary>線に沿った明るさの変化（ラインプロファイル）</summary>
public sealed record LineProfileResult(double[] Distances, double[] Values, double Length, string LengthUnit);

public static class LineProfile
{
    /// <summary>a から b まで、1 画素おきに明るさを補間して読む</summary>
    public static LineProfileResult Sample(Raster image, PointD a, PointD b, Calibration calibration)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(calibration);
        var g = image.ToGray();
        double len = a.DistanceTo(b);
        int n = Math.Max(2, (int)Math.Ceiling(len) + 1);
        var dist = new double[n];
        var vals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            // 画素の座標（中心が +0.5）から、補間の座標（中心が 0）へ
            double x = a.X + ((b.X - a.X) * t) - 0.5, y = a.Y + ((b.Y - a.Y) * t) - 0.5;
            dist[i] = calibration.Length(len * t);
            vals[i] = Geometry.Bilinear(g, x, y);
        }
        return new LineProfileResult(dist, vals, calibration.Length(len), calibration.LengthUnit);
    }
}
