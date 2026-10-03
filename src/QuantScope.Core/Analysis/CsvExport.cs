using System.Globalization;
using System.Text;

namespace QuantScope.Core.Analysis;

/// <summary>
/// 計測結果を CSV にする。Excel で文字化けしないよう UTF-8（BOM 付き）で書き、
/// 小数点は国や地域の設定によらず「.」にする。
/// </summary>
public static class CsvExport
{
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>粒ごとの表。file を渡すと先頭に「ファイル」の列を付ける（一括処理用）。</summary>
    public static string Particles(IEnumerable<(string? File, Particle P)> rows, string lengthUnit, string areaUnit)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        bool withFile = list.Any(r => r.File is not null);
        var sb = new StringBuilder();
        var header = new List<string>();
        if (withFile) header.Add("ファイル");
        header.AddRange(
        [
            "番号", $"面積 ({areaUnit})", $"周囲長 ({lengthUnit})", "円形度", $"相当直径 ({lengthUnit})",
            $"長軸 ({lengthUnit})", $"短軸 ({lengthUnit})", "角度 (度)", "縦横比", $"フェレ径 ({lengthUnit})", "充実度",
            "平均の明るさ", "明るさの標準偏差", "最小の明るさ", "最大の明るさ", "明るさの合計",
            "重心 X (px)", "重心 Y (px)", "左 (px)", "上 (px)", "幅 (px)", "高さ (px)", "ふちに触れる", "画素数",
        ]);
        AppendRow(sb, header);
        foreach (var (file, p) in list)
        {
            var cells = new List<string>();
            if (withFile) cells.Add(file ?? "");
            cells.AddRange(
            [
                I(p.Id), N(p.Area), N(p.Perimeter), N(p.Circularity), N(p.EquivalentDiameter),
                N(p.MajorAxis), N(p.MinorAxis), N(p.Angle), N(p.AspectRatio), N(p.Feret), N(p.Solidity),
                N(p.MeanIntensity), N(p.StdIntensity), N(p.MinIntensity), N(p.MaxIntensity), N(p.IntegratedIntensity),
                N(p.CentroidX), N(p.CentroidY), I(p.BoundsX), I(p.BoundsY), I(p.BoundsWidth), I(p.BoundsHeight),
                p.TouchesEdge ? "はい" : "いいえ", I(p.PixelCount),
            ]);
            AppendRow(sb, cells);
        }
        return sb.ToString();
    }

    /// <summary>画像ごとのまとめの表（一括処理用）。読めなかった画像は理由を書く。</summary>
    public static string Summaries(IEnumerable<(string File, AnalysisSummary? Summary, string? Error)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        var first = list.Select(r => r.Summary).FirstOrDefault(s => s is not null);
        string au = first?.AreaUnit ?? "px²", lu = first?.LengthUnit ?? "px";
        var sb = new StringBuilder();
        AppendRow(sb,
        [
            "ファイル", "数", $"面積の合計 ({au})", $"平均の面積 ({au})", $"面積の中央値 ({au})", $"面積の標準偏差 ({au})",
            "占有率 (%)", $"調べた範囲の面積 ({au})", $"密度 (個/{au})", "平均の円形度", "平均の明るさ", "メモ",
        ]);
        foreach (var (file, s, err) in list)
        {
            if (s is null)
            {
                AppendRow(sb, [file, "", "", "", "", "", "", "", "", "", "", err ?? ""]);
                continue;
            }
            AppendRow(sb,
            [
                file, I(s.Count), N(s.TotalArea), N(s.MeanArea), N(s.MedianArea), N(s.StdArea),
                N(s.AreaFraction), N(s.AnalyzedArea), N(s.Density, "0.######"), N(s.MeanCircularity), N(s.MeanIntensity),
                s.LengthUnit != lu ? $"単位が違います（{s.LengthUnit}）" : "",
            ]);
        }
        return sb.ToString();
    }

    private static string N(double v, string format = "0.####") => double.IsFinite(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "";

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.Append(string.Join(",", cells.Select(Escape))).Append("\r\n");
    }

    /// <summary>カンマ・引用符・改行を含むときは "" で囲む。先頭が = + - @ のときは、表計算ソフトが式として実行しないよう ' を付ける。</summary>
    internal static string Escape(string s)
    {
        if (s.Length > 0 && "=+-@".Contains(s[0], StringComparison.Ordinal) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        if (s.IndexOfAny([',', '"', '\r', '\n']) >= 0) return "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return s;
    }
}
