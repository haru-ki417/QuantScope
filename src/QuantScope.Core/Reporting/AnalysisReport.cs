using System.Globalization;
using System.Net;
using System.Text;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;

namespace QuantScope.Core.Reporting;

/// <summary>レポートに入れるもの</summary>
public sealed record ReportInput
{
    public required Recipe Recipe { get; init; }
    public required AnalysisResult Result { get; init; }
    public required Calibration Calibration { get; init; }
    public string? FileName { get; init; }
    public string? ImageDescription { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string AppVersion { get; init; } = "";

    /// <summary>手順ごとの結果（しきい値など。なければ省く）</summary>
    public IReadOnlyList<StepOutcome>? Outcomes { get; init; }

    /// <summary>解析した範囲の説明（なければ画像全体）</summary>
    public string? RegionText { get; init; }

    /// <summary>元の画像と、結果を重ねた画像（PNG）</summary>
    public byte[]? OriginalPng { get; init; }
    public byte[]? OverlayPng { get; init; }

    /// <summary>表に載せる粒の数の上限（全部は CSV で）</summary>
    public int MaxRows { get; init; } = 300;
}

/// <summary>
/// 解析レポート（1 つの HTML ファイル）。画像・手順と値・計測の条件・結果・分布をまとめ、
/// 解析を後から確かめたり、同じ条件でやり直したりできるようにする（再現性のため）。
/// 画像は埋め込むので、ファイル 1 つで持ち運べる。ブラウザーで印刷すれば PDF にもできる。
/// </summary>
public static class AnalysisReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string BuildHtml(ReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var r = input.Result;
        var s = r.Summary;
        var sb = new StringBuilder();
        string title = string.IsNullOrWhiteSpace(input.FileName) ? "解析レポート" : $"解析レポート — {input.FileName}";
        sb.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        // 見出し
        sb.Append("<header><div class=\"brand\">QuantScope</div><h1>").Append(E(input.FileName ?? "解析レポート")).Append("</h1>");
        sb.Append("<p class=\"meta\">");
        var meta = new List<string> { "作成 " + input.CreatedAt.ToString("yyyy-MM-dd HH:mm", Inv) };
        if (!string.IsNullOrWhiteSpace(input.AppVersion)) meta.Add("QuantScope " + input.AppVersion);
        if (input.ImageWidth > 0) meta.Add(string.Create(Inv, $"{input.ImageWidth} × {input.ImageHeight} px"));
        if (!string.IsNullOrWhiteSpace(input.ImageDescription)) meta.Add(input.ImageDescription!);
        meta.Add("縮尺 " + input.Calibration);
        sb.Append(string.Join("　·　", meta.Select(E))).Append("</p></header>\n");

        // まとめ
        sb.Append("<section><h2>結果のまとめ</h2><div class=\"kpis\">");
        Kpi(sb, "数", s.Count.ToString("N0", Inv), "個");
        Kpi(sb, "占有率", N(s.AreaFraction, "0.00"), "%");
        Kpi(sb, "平均の面積", F(s.MeanArea), s.AreaUnit);
        if (s.Positive is not null) Kpi(sb, "陽性率", N(s.PositivePercent, "0.0"), $"%（{s.PositiveCount} / {s.Count}）");
        else Kpi(sb, "平均の円形度", N(s.MeanCircularity, "0.000"), "");
        sb.Append("</div>\n<table class=\"kv\">");
        Row(sb, "面積の合計", $"{F(s.TotalArea)} {s.AreaUnit}");
        Row(sb, "面積の中央値・標準偏差", $"{F(s.MedianArea)} / {F(s.StdArea)} {s.AreaUnit}");
        Row(sb, "調べた範囲", $"{F(s.AnalyzedArea)} {s.AreaUnit}" + (string.IsNullOrWhiteSpace(input.RegionText) ? "（画像全体）" : $"（{input.RegionText}）"));
        Row(sb, "密度", $"{s.Density.ToString("G4", Inv)} 個 / {s.AreaUnit}");
        Row(sb, "平均の円形度", N(s.MeanCircularity, "0.000"));
        Row(sb, $"平均（{s.IntensityLabel}）", F(s.MeanIntensity));
        if (s.Positive is { } rule)
            Row(sb, "陽性の決まり", $"粒ごとの平均（{s.IntensityLabel}）が {F(rule.Threshold)} {(rule.Above ? "以上" : "未満")}");
        if (s.ExcludedCount > 0) Row(sb, "手で除いた粒", $"{s.ExcludedCount} 個");
        sb.Append("</table></section>\n");

        // 画像
        if (input.OriginalPng is not null || input.OverlayPng is not null)
        {
            sb.Append("<section><h2>画像</h2><div class=\"images\">");
            if (input.OriginalPng is not null) Figure(sb, input.OriginalPng, "元の画像");
            if (input.OverlayPng is not null) Figure(sb, input.OverlayPng, s.Positive is null ? "数えた粒（マゼンタ）" : "陽性（マゼンタ）・陰性（水色）");
            sb.Append("</div></section>\n");
        }

        // 分布
        var features = new List<ParticleFeature> { ParticleFeatures.Area, ParticleFeatures.Circularity, ParticleFeatures.EquivalentDiameter };
        if (s.Positive is not null || s.MeanIntensity != 0) features.Add(ParticleFeatures.MeanIntensity);
        if (r.Particles.Count > 0)
        {
            sb.Append("<section><h2>粒の値の分布</h2><div class=\"dists\">");
            foreach (var f in features)
            {
                var values = r.Particles.Select(f.Get).ToList();
                var d = Statistics.Describe(values);
                double? marker = f == ParticleFeatures.MeanIntensity && s.Positive is { } pr ? pr.Threshold : null;
                sb.Append("<div class=\"dist\"><h3>").Append(E(f.Header(s))).Append("</h3>");
                sb.Append(HistogramSvg(Statistics.Histogram(values), marker));
                sb.Append("<p class=\"stats\">").Append(E(string.Create(Inv,
                    $"n = {d.N}　平均 {F(d.Mean)} ± {F(d.Sd)}（SD）　中央値 {F(d.Median)}［{F(d.Q1)}–{F(d.Q3)}］　範囲 {F(d.Min)}–{F(d.Max)}"))).Append("</p></div>");
            }
            sb.Append("</div></section>\n");
        }

        // 手順
        sb.Append("<section><h2>手順（レシピ「").Append(E(input.Recipe.Name)).Append("」）</h2><ol class=\"steps\">");
        for (int i = 0; i < input.Recipe.Steps.Count; i++)
        {
            var step = input.Recipe.Steps[i];
            if (!StepCatalog.Exists(step.Kind)) continue;
            var def = StepCatalog.Get(step.Kind);
            sb.Append("<li").Append(step.Enabled ? "" : " class=\"off\"").Append("><b>").Append(E(def.Title)).Append("</b>");
            if (!step.Enabled) sb.Append("（オフ）");
            var values = def.Parameters.Where(p => p.IsUsed(step)).Select(p => $"{p.Label}: {ParamText(p, step.Get(p.Key))}").ToList();
            if (values.Count > 0) sb.Append("<span class=\"params\">").Append(E(string.Join("　", values))).Append("</span>");
            var o = input.Outcomes is not null && i < input.Outcomes.Count ? input.Outcomes[i] : null;
            if (o?.Info is { Length: > 0 } info && step.Enabled) sb.Append("<span class=\"info\">→ ").Append(E(info)).Append("</span>");
            if (o?.Warning is { Length: > 0 } warn) sb.Append("<span class=\"warn\">").Append(E(warn)).Append("</span>");
            sb.Append("</li>");
        }
        if (input.Recipe.Steps.Count == 0) sb.Append("<li>手順はありません。</li>");
        sb.Append("</ol>\n");

        var m = input.Recipe.Measure;
        sb.Append("<h3>計測の条件</h3><table class=\"kv\">");
        Row(sb, "面積の範囲", $"{F(m.MinArea)} 〜 {(m.MaxArea > 0 ? F(m.MaxArea) : "上限なし")} {s.AreaUnit}");
        Row(sb, "ふちに触れた粒", m.ExcludeEdges ? "数えない" : "数える");
        Row(sb, "粒のつながり", m.EightConnected ? "8 近傍（斜めもつながる）" : "4 近傍");
        Row(sb, "明るさとして測るもの", $"{s.IntensityLabel}（{(m.IntensityFromOriginal ? "処理する前の画像" : "処理したあとの画像")}）");
        Row(sb, "陽性の判定", s.Positive is { } p2 ? $"{F(p2.Threshold)} {(p2.Above ? "以上" : "未満")}を陽性" : "しない");
        sb.Append("</table></section>\n");

        // 粒ごとの値
        if (r.Particles.Count > 0)
        {
            var cols = new List<ParticleFeature>
            {
                ParticleFeatures.Area, ParticleFeatures.Perimeter, ParticleFeatures.Circularity, ParticleFeatures.EquivalentDiameter,
                ParticleFeatures.AspectRatio, ParticleFeatures.Feret, ParticleFeatures.Solidity, ParticleFeatures.MeanIntensity,
            };
            sb.Append("<section><h2>粒ごとの値</h2><table class=\"grid\"><thead><tr><th>番号</th>");
            foreach (var c in cols) sb.Append("<th>").Append(E(c.Header(s))).Append("</th>");
            if (s.Positive is not null) sb.Append("<th>判定</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var p in r.Particles.Take(input.MaxRows))
            {
                sb.Append("<tr><td>").Append(p.Id.ToString(Inv)).Append("</td>");
                foreach (var c in cols) sb.Append("<td>").Append(E(F(c.Get(p)))).Append("</td>");
                if (s.Positive is not null) sb.Append(p.Positive == true ? "<td class=\"pos\">陽性</td>" : "<td>陰性</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
            if (r.Particles.Count > input.MaxRows)
                sb.Append("<p class=\"note\">").Append(E($"最初の {input.MaxRows} 個だけを載せています（全部で {r.Particles.Count} 個）。すべての値は CSV に書き出せます。")).Append("</p>");
            sb.Append("</section>\n");
        }

        sb.Append("<footer>QuantScope で作成。研究・学習用のソフトウェアの出力で、医療機器ではなく、診断には使えません。面積・長さは画素の数と縮尺から計算した推定値です。</footer>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>手順の値を読める形に（選択肢は名前、数は単位つき）</summary>
    public static string ParamText(ParamDef p, double v)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Kind switch
        {
            ParamKind.Choice => p.Choices.Count > 0 ? p.Choices[Math.Clamp((int)Math.Round(v), 0, p.Choices.Count - 1)] : F(v),
            ParamKind.Toggle => v >= 0.5 ? "オン" : "オフ",
            _ => F(v) + (string.IsNullOrEmpty(p.Unit) ? "" : " " + p.Unit),
        };
    }

    /// <summary>度数分布の棒グラフ（SVG）。marker があれば縦の線を引く</summary>
    internal static string HistogramSvg(IReadOnlyList<Bin> bins, double? marker = null)
    {
        const double w = 480, h = 96, bottom = 18;
        var sb = new StringBuilder();
        sb.Append(string.Create(Inv, $"<svg viewBox=\"0 0 {w} {h + bottom}\" class=\"hist\" role=\"img\">"));
        if (bins.Count == 0)
        {
            sb.Append("</svg>");
            return sb.ToString();
        }
        double lo = bins[0].Lower, hi = bins[^1].Upper;
        double span = hi - lo;
        if (span <= 0)
        {
            lo -= 0.5;
            hi += 0.5;
            span = 1;
        }
        int max = Math.Max(1, bins.Max(b => b.Count));
        foreach (var b in bins)
        {
            double x0 = (b.Lower - lo) / span * w, x1 = (b.Upper - lo) / span * w;
            if (bins.Count == 1)
            {
                x0 = w * 0.4;
                x1 = w * 0.6;
            }
            double bh = (double)b.Count / max * (h - 6);
            sb.Append(string.Create(Inv, $"<rect x=\"{x0 + 0.5:0.##}\" y=\"{h - bh:0.##}\" width=\"{Math.Max(0.5, x1 - x0 - 1):0.##}\" height=\"{bh:0.##}\" />"));
        }
        sb.Append(string.Create(Inv, $"<line x1=\"0\" y1=\"{h}\" x2=\"{w}\" y2=\"{h}\" class=\"axis\" />"));
        sb.Append(string.Create(Inv, $"<text x=\"0\" y=\"{h + 13}\">{E(F(lo))}</text>"));
        sb.Append(string.Create(Inv, $"<text x=\"{w}\" y=\"{h + 13}\" text-anchor=\"end\">{E(F(hi))}</text>"));
        if (marker is { } m && m >= lo && m <= hi)
        {
            double x = (m - lo) / span * w;
            sb.Append(string.Create(Inv, $"<line x1=\"{x:0.##}\" y1=\"0\" x2=\"{x:0.##}\" y2=\"{h}\" class=\"marker\" />"));
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Kpi(StringBuilder sb, string label, string value, string unit) =>
        sb.Append("<div class=\"kpi\"><span class=\"label\">").Append(E(label)).Append("</span><span class=\"value\">").Append(E(value))
          .Append("</span><span class=\"unit\">").Append(E(unit)).Append("</span></div>");

    private static void Row(StringBuilder sb, string key, string value) =>
        sb.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value)).Append("</td></tr>");

    private static void Figure(StringBuilder sb, byte[] png, string caption) =>
        sb.Append("<figure><img alt=\"").Append(E(caption)).Append("\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(png))
          .Append("\"><figcaption>").Append(E(caption)).Append("</figcaption></figure>");

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string N(double v, string format) => double.IsFinite(v) ? v.ToString(format, Inv) : "–";

    private static string F(double v) => ParticleFeature.Format(v, Inv);

    private const string Css = """

        :root { --ink:#1b2328; --muted:#5d6b73; --line:#dde3e7; --soft:#f4f7f8; --accent:#16a874; --mask:#d63384; }
        * { box-sizing: border-box; }
        body { margin: 0; background: #fff; color: var(--ink); font: 14px/1.6 "BIZ UDPGothic", "Yu Gothic UI", "Hiragino Sans", "Noto Sans JP", sans-serif; }
        main { max-width: 980px; margin: 0 auto; padding: 32px 28px 48px; }
        header { border-bottom: 2px solid var(--ink); padding-bottom: 14px; margin-bottom: 24px; }
        .brand { font: 600 13px/1 "Bahnschrift", "Segoe UI", sans-serif; letter-spacing: .06em; color: var(--accent); }
        h1 { font-size: 24px; margin: 8px 0 4px; word-break: break-all; }
        h2 { font-size: 16px; margin: 0 0 12px; padding-left: 10px; border-left: 4px solid var(--accent); }
        h3 { font-size: 13px; margin: 16px 0 6px; color: var(--muted); }
        .meta { color: var(--muted); font-size: 12.5px; margin: 0; }
        section { margin: 0 0 28px; break-inside: avoid-page; }
        .kpis { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 14px; }
        .kpi { background: var(--soft); border-radius: 8px; padding: 10px 14px; }
        .kpi .label { display: block; font-size: 12px; color: var(--muted); }
        .kpi .value { font: 600 24px/1.25 "Bahnschrift", "Segoe UI", sans-serif; }
        .kpi .unit { font-size: 12px; color: var(--muted); margin-left: 4px; }
        table { border-collapse: collapse; width: 100%; }
        .kv th { text-align: left; font-weight: normal; color: var(--muted); width: 34%; padding: 5px 8px 5px 0; border-bottom: 1px solid var(--line); vertical-align: top; }
        .kv td { padding: 5px 0; border-bottom: 1px solid var(--line); }
        .images { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
        figure { margin: 0; }
        figure img { width: 100%; display: block; border-radius: 6px; background: #000; }
        figcaption { font-size: 12px; color: var(--muted); margin-top: 4px; }
        .dists { display: grid; grid-template-columns: 1fr 1fr; gap: 14px 22px; }
        .dist h3 { margin-top: 0; color: var(--ink); }
        .hist { width: 100%; height: auto; display: block; }
        .hist rect { fill: #9fb2bb; }
        .hist .axis { stroke: var(--muted); stroke-width: 1; }
        .hist .marker { stroke: var(--mask); stroke-width: 2; }
        .hist text { font: 13px "Bahnschrift", "Segoe UI", sans-serif; fill: var(--muted); }
        .stats { font-size: 12px; color: var(--muted); margin: 4px 0 0; }
        .steps { padding-left: 22px; margin: 0; }
        .steps li { margin-bottom: 6px; }
        .steps li.off { color: var(--muted); }
        .params { display: block; font-size: 12.5px; color: var(--muted); }
        .info { display: block; font-size: 12.5px; color: var(--accent); }
        .warn { display: block; font-size: 12.5px; color: #b26a00; }
        .grid { font: 12px/1.4 "Bahnschrift", "Segoe UI", sans-serif; }
        .grid th { background: var(--soft); font-weight: 600; text-align: right; padding: 6px 8px; border-bottom: 1px solid var(--line); }
        .grid td { text-align: right; padding: 4px 8px; border-bottom: 1px solid var(--line); }
        .grid tr:nth-child(even) td { background: #fafbfc; }
        .grid .pos { color: var(--mask); font-weight: 600; }
        .note { font-size: 12px; color: var(--muted); }
        footer { margin-top: 36px; padding-top: 12px; border-top: 1px solid var(--line); font-size: 11.5px; color: var(--muted); }
        @media (max-width: 720px) { .kpis { grid-template-columns: 1fr 1fr; } .images, .dists { grid-template-columns: 1fr; } }
        @media print { main { padding: 0; max-width: none; } section { break-inside: avoid; } .grid { font-size: 10.5px; } }

        """;
}
