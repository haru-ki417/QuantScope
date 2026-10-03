using System.Globalization;

namespace QuantScope.Core.Analysis;

/// <summary>値の単位の種類（縮尺によって変わるか）</summary>
public enum FeatureUnit
{
    None,
    Length,
    Area,
    Angle,
    Intensity,
}

/// <summary>粒の計測値の 1 種類（表の列・分布・レポートで共通に使う）</summary>
public sealed record ParticleFeature(string Key, string Title, FeatureUnit Unit, Func<Particle, double> Get)
{
    /// <summary>単位つきの見出し（例: 面積 (µm²)）</summary>
    public string Header(AnalysisSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return Unit switch
        {
            FeatureUnit.Length => $"{Title} ({summary.LengthUnit})",
            FeatureUnit.Area => $"{Title} ({summary.AreaUnit})",
            FeatureUnit.Angle => $"{Title} (度)",
            FeatureUnit.Intensity => $"{Title}（{summary.IntensityLabel}）",
            _ => Title,
        };
    }

    public string UnitText(AnalysisSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return Unit switch
        {
            FeatureUnit.Length => summary.LengthUnit,
            FeatureUnit.Area => summary.AreaUnit,
            FeatureUnit.Angle => "°",
            _ => "",
        };
    }

    /// <summary>値を読みやすい桁数で書く（大きい値は桁区切り、小さい値は有効数字 3 桁）</summary>
    public static string Format(double v, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (!double.IsFinite(v)) return "";
        double a = Math.Abs(v);
        if (a == 0) return "0";
        if (a >= 1000) return v.ToString("N0", culture);
        if (a >= 100) return v.ToString("0.0", culture);
        if (a >= 10) return v.ToString("0.00", culture);
        if (a >= 0.01) return v.ToString("0.000", culture);
        return v.ToString("G3", culture);
    }
}

public static class ParticleFeatures
{
    public static ParticleFeature Area { get; } = new("area", "面積", FeatureUnit.Area, p => p.Area);
    public static ParticleFeature Perimeter { get; } = new("perimeter", "周囲長", FeatureUnit.Length, p => p.Perimeter);
    public static ParticleFeature Circularity { get; } = new("circularity", "円形度", FeatureUnit.None, p => p.Circularity);
    public static ParticleFeature EquivalentDiameter { get; } = new("diameter", "相当直径", FeatureUnit.Length, p => p.EquivalentDiameter);
    public static ParticleFeature MajorAxis { get; } = new("major", "長軸", FeatureUnit.Length, p => p.MajorAxis);
    public static ParticleFeature MinorAxis { get; } = new("minor", "短軸", FeatureUnit.Length, p => p.MinorAxis);
    public static ParticleFeature Angle { get; } = new("angle", "角度", FeatureUnit.Angle, p => p.Angle);
    public static ParticleFeature AspectRatio { get; } = new("aspect", "縦横比", FeatureUnit.None, p => p.AspectRatio);
    public static ParticleFeature Feret { get; } = new("feret", "フェレ径", FeatureUnit.Length, p => p.Feret);
    public static ParticleFeature Solidity { get; } = new("solidity", "充実度", FeatureUnit.None, p => p.Solidity);
    public static ParticleFeature MeanIntensity { get; } = new("mean", "平均", FeatureUnit.Intensity, p => p.MeanIntensity);
    public static ParticleFeature StdIntensity { get; } = new("sd", "標準偏差", FeatureUnit.Intensity, p => p.StdIntensity);
    public static ParticleFeature IntegratedIntensity { get; } = new("integrated", "合計", FeatureUnit.Intensity, p => p.IntegratedIntensity);

    /// <summary>分布やレポートで選べる値</summary>
    public static IReadOnlyList<ParticleFeature> All { get; } =
    [
        Area, Perimeter, Circularity, EquivalentDiameter, MajorAxis, MinorAxis, Angle, AspectRatio, Feret, Solidity,
        MeanIntensity, StdIntensity, IntegratedIntensity,
    ];

    public static ParticleFeature Get(string key) => All.FirstOrDefault(f => f.Key == key) ?? Area;
}
