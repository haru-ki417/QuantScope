using System.Text.Json;
using System.Text.Json.Serialization;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.Core.Pipeline;

/// <summary>計測の決まり（レシピに一緒に保存する）</summary>
public sealed record MeasureSettings
{
    /// <summary>この面積（縮尺の単位）より小さい粒は数えない</summary>
    public double MinArea { get; init; }

    /// <summary>この面積より大きい粒は数えない（0 は上限なし）</summary>
    public double MaxArea { get; init; }

    public bool ExcludeEdges { get; init; }
    public bool EightConnected { get; init; } = true;

    /// <summary>明るさを、処理の前の元画像で測る（false なら処理後の画像）</summary>
    public bool IntensityFromOriginal { get; init; } = true;

    public AnalysisOptions ToOptions() => new()
    {
        MinArea = MinArea,
        MaxArea = MaxArea,
        ExcludeEdges = ExcludeEdges,
        EightConnected = EightConnected,
    };
}

/// <summary>
/// レシピ: 手順の並びと計測の決まり。保存して同じ解析をほかの画像にもかけられる（一括処理・再現性のため）。
/// </summary>
public sealed class Recipe
{
    public const string FormatName = "quantscope-recipe";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name { get; set; } = "新しいレシピ";
    public List<Step> Steps { get; init; } = [];
    public MeasureSettings Measure { get; set; } = new();

    /// <summary>縮尺（null なら画像の情報か、px のまま）</summary>
    public Calibration? Calibration { get; set; }

    public Recipe Clone() => new()
    {
        Name = Name,
        Steps = Steps.Select(s => s.Clone()).ToList(),
        Measure = Measure,
        Calibration = Calibration,
    };

    public string ToJson()
    {
        var dto = new RecipeFile
        {
            Format = FormatName,
            Version = CurrentVersion,
            Name = Name,
            Steps = Steps.Select(s => new StepFile { Kind = s.Kind, Enabled = s.Enabled, Values = new SortedDictionary<string, double>(s.Values, StringComparer.Ordinal) }).ToList(),
            Measure = Measure,
            Calibration = Calibration is { IsCalibrated: true } c ? new CalibrationFile { UnitsPerPixel = c.UnitsPerPixel, Unit = c.Unit } : null,
        };
        return JsonSerializer.Serialize(dto, Json);
    }

    /// <summary>JSON から読む。知らない手順や形式の違うファイルは、理由を添えて InvalidDataException にする。</summary>
    public static Recipe FromJson(string json)
    {
        RecipeFile? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RecipeFile>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("レシピのファイルとして読めませんでした。", ex);
        }
        if (dto is null || dto.Format != FormatName) throw new InvalidDataException("QuantScope のレシピではありません。");
        if (dto.Version > CurrentVersion) throw new InvalidDataException("新しい版の QuantScope で作られたレシピです。アプリを更新してください。");

        var steps = new List<Step>();
        foreach (var s in dto.Steps ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.Kind) || !StepCatalog.Exists(s.Kind)) throw new InvalidDataException($"知らない手順「{s.Kind}」があります。");
            var values = new Dictionary<string, double>();
            foreach (var kv in s.Values ?? new SortedDictionary<string, double>())
                if (double.IsFinite(kv.Value)) values[kv.Key] = kv.Value;
            steps.Add(new Step(s.Kind, values, s.Enabled));
        }
        Calibration? cal = null;
        if (dto.Calibration is { } c && c.UnitsPerPixel > 0 && !string.IsNullOrWhiteSpace(c.Unit)) cal = new Calibration(c.UnitsPerPixel, c.Unit);
        return new Recipe
        {
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "レシピ" : dto.Name,
            Steps = steps,
            Measure = dto.Measure ?? new MeasureSettings(),
            Calibration = cal,
        };
    }

    private sealed class RecipeFile
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Name { get; set; }
        public List<StepFile>? Steps { get; set; }
        public MeasureSettings? Measure { get; set; }
        public CalibrationFile? Calibration { get; set; }
    }

    private sealed class StepFile
    {
        public string? Kind { get; set; }
        public bool Enabled { get; set; } = true;
        public SortedDictionary<string, double>? Values { get; set; }
    }

    private sealed class CalibrationFile
    {
        public double UnitsPerPixel { get; set; }
        public string Unit { get; set; } = "px";
    }
}
