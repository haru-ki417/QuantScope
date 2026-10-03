using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Processing;
using QuantScope.Core.Reporting;
using QuantScope.Core.Samples;

namespace QuantScope.Tests;

public class FeatureTests
{
    /// <summary>ヘマトキシリンと DAB を決まった量だけ重ねた色（吸光度の足し算から作る）</summary>
    private static (float R, float G, float B) Mix(double h, double dab)
    {
        double[] hv = [0.650, 0.704, 0.286], dv = [0.268, 0.570, 0.776];
        static double N(double[] v, int c) => v[c] / Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        float Ch(int c) => (float)(255 * Math.Pow(10, -((h * N(hv, c)) + (dab * N(dv, c)))));
        return (Ch(0), Ch(1), Ch(2));
    }

    [Fact]
    public void 染色を分けると_重ねた染料の量が別々に戻る()
    {
        var img = Raster.CreateColor(2, 1);
        var (r0, g0, b0) = Mix(0.4, 0.3);
        var (r1, g1, b1) = Mix(0.8, 0);
        float[] d = [r0, g0, b0, r1, g1, b1];
        d.CopyTo(img.Data, 0);
        var dab = ColorDeconvolution.Amount(img, StainChannel.Dab);
        var hem = ColorDeconvolution.Amount(img, StainChannel.HematoxylinHDab);
        Assert.InRange(dab.Data[0], 0.27, 0.33);
        Assert.InRange(hem.Data[0], 0.37, 0.43);
        Assert.InRange(dab.Data[1], 0, 0.03);
        Assert.InRange(hem.Data[1], 0.76, 0.84);
        Assert.Throws<ArgumentException>(() => ColorDeconvolution.Amount(Raster.CreateGray(2, 2), StainChannel.Dab));
    }

    [Fact]
    public void 形で選ぶと_丸い粒だけが残る()
    {
        var m = TestImages.Disk(200, 100, 50, 50, 25);
        TestImages.Rect(200, 100, 110, 45, 80, 8, m);
        var (mask, kept, total) = ShapeFilter.Apply(m, 0.8, 1, 0, 0);
        Assert.Equal(2, total);
        Assert.Equal(1, kept);
        Assert.True(mask[50, 50]);
        Assert.False(mask[150, 48]);
        // 縦横比の上限でも同じように分けられる
        Assert.Equal(1, ShapeFilter.Apply(m, 0, 1, 3, 0).Kept);
    }

    [Fact]
    public void 手で除いた粒は数えず_数を記録する()
    {
        var m = TestImages.Rect(100, 50, 5, 5, 20, 20);
        TestImages.Rect(100, 50, 60, 5, 20, 20, m);
        var r = ParticleAnalyzer.Analyze(m, null, Calibration.Pixels, new AnalysisOptions { ExcludePoints = [new PointD(70.5, 15.5), new PointD(40, 40)] });
        var p = Assert.Single(r.Particles);
        Assert.Equal(15, p.CentroidX, 3);
        Assert.Equal(1, r.Summary.ExcludedCount);
        Assert.NotNull(r.Excluded);
        Assert.True(r.Excluded![70, 15]);
        Assert.Equal(400.0 / 5000 * 100, r.Summary.AreaFraction, 6);
    }

    [Fact]
    public void 陽性の判定は粒の平均で分け_陽性率を出す()
    {
        var m = TestImages.Rect(100, 50, 5, 5, 20, 20);
        TestImages.Rect(100, 50, 60, 5, 20, 20, m);
        var img = TestImages.Gray(100, 50, (x, _) => x < 50 ? 50 : 200);
        var r = ParticleAnalyzer.Analyze(m, img, Calibration.Pixels, new AnalysisOptions { Positive = new PositiveRule(100) });
        Assert.Equal([false, true], r.Particles.Select(p => p.Positive == true));
        Assert.Equal(1, r.Summary.PositiveCount);
        Assert.Equal(50, r.Summary.PositivePercent, 6);
        var below = ParticleAnalyzer.Analyze(m, img, Calibration.Pixels, new AnalysisOptions { Positive = new PositiveRule(100, Above: false) });
        Assert.Equal([true, false], below.Particles.Select(p => p.Positive == true));
        string csv = CsvExport.Particles(r.Particles.Select(p => ((string?)null, p)), "px", "px²", "DAB の量");
        Assert.Contains("平均（DAB の量）", csv, StringComparison.Ordinal);
        Assert.EndsWith(",陽性\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void 記述統計と度数分布と大津のしきい値()
    {
        var d = Statistics.Describe([5, 1, 4, 2, 3, double.NaN]);
        Assert.Equal(5, d.N);
        Assert.Equal(3, d.Mean, 9);
        Assert.Equal(Math.Sqrt(2.5), d.Sd, 9);
        Assert.Equal((2.0, 3.0, 4.0), (d.Q1, d.Median, d.Q3));
        Assert.Equal((1.0, 5.0), (d.Min, d.Max));

        var values = Enumerable.Range(0, 100).Select(i => i < 60 ? 10 + (i % 5) : 80 + (i % 7)).Select(v => (double)v).ToList();
        var bins = Statistics.Histogram(values);
        Assert.Equal(100, bins.Sum(b => b.Count));
        Assert.InRange(bins.Count, 5, 40);
        Assert.Equal(10, bins[0].Lower);
        Assert.Equal(86, bins[^1].Upper);
        double t = Statistics.OtsuThreshold(values);
        Assert.InRange(t, 15, 80);
        Assert.Equal(Descriptive.Empty, Statistics.Describe([]));
    }

    [Fact]
    public void 計測の条件はレシピに保存され_白黒の画像では明るさに戻る()
    {
        var recipe = new Recipe { Measure = new MeasureSettings { Channel = IntensityChannel.Dab, Classify = true, PositiveThreshold = 0.25, PositiveAbove = true } };
        string json = recipe.ToJson();
        Assert.Contains("\"dab\"", json, StringComparison.Ordinal);
        var back = Recipe.FromJson(json).Measure;
        Assert.Equal(recipe.Measure, back);

        var m = TestImages.Rect(40, 40, 5, 5, 10, 10);
        var state = PipelineState.Start(TestImages.FromMask(m), Calibration.Pixels) with { Mask = m };
        var r = PipelineRunner.Analyze(state, back)!;
        Assert.Equal("明るさ", r.Summary.IntensityLabel);
        Assert.NotNull(r.Summary.Positive);
    }

    [Fact]
    public void 染色を分ける手順は白黒の画像では理由を出して飛ばす()
    {
        var gray = TestImages.Gray(10, 10, (x, _) => x);
        var run = PipelineRunner.Run(gray, Calibration.Pixels, [StepCatalog.Create("stain", gray)]);
        Assert.False(run.Outcomes[0].Applied);
        Assert.Contains("カラー", run.Outcomes[0].Warning, StringComparison.Ordinal);

        var color = Raster.CreateColor(4, 4);
        Array.Fill(color.Data, 128f);
        var ok = PipelineRunner.Run(color, Calibration.Pixels, [StepCatalog.Create("stain", color)]);
        Assert.True(ok.Outcomes[0].Applied);
        Assert.False(ok.Final.Image.IsColor);
    }
}

public class SampleAndReportTests
{
    [Fact]
    public void 免疫染色の見本は_核を数えてDABで陽性を正しく分ける()
    {
        var sample = SampleImages.Create("ihc");
        var run = PipelineRunner.Run(sample.Image, sample.Calibration, sample.Recipe.Steps);
        var r = PipelineRunner.Analyze(run.Final, sample.Recipe.Measure)!;
        // ふちに触れない核の正解
        var truth = SampleImages.IhcLayout().Where(n => n.X - n.R - 2 > 0 && n.Y - n.R - 2 > 0 && n.X + n.R + 2 < 800 && n.Y + n.R + 2 < 560).ToList();
        Assert.InRange(r.Summary.Count, truth.Count - 2, truth.Count + 1);
        Assert.Equal("DAB の量", r.Summary.IntensityLabel);
        int truePos = truth.Count(n => n.Positive);
        Assert.InRange(r.Summary.PositiveCount, truePos - 2, truePos + 1);
        // 陽性の粒は、本当に陽性の核の上にある
        foreach (var p in r.Particles.Where(p => p.Positive == true))
        {
            var nearest = truth.MinBy(n => ((n.X - p.CentroidX) * (n.X - p.CentroidX)) + ((n.Y - p.CentroidY) * (n.Y - p.CentroidY)));
            Assert.True(nearest.Positive);
        }
    }

    [Fact]
    public void レポートは1つのHTMLで_名前は文字として書き_手順と結果が入る()
    {
        var sample = SampleImages.Create("ihc");
        var run = PipelineRunner.Run(sample.Image, sample.Calibration, sample.Recipe.Steps);
        var r = PipelineRunner.Analyze(run.Final, sample.Recipe.Measure)!;
        string html = AnalysisReport.BuildHtml(new ReportInput
        {
            Recipe = sample.Recipe,
            Result = r,
            Calibration = sample.Calibration,
            FileName = "<script>alert(1)</script>.png",
            Outcomes = run.Outcomes,
            OverlayPng = [1, 2, 3],
            CreatedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(9)),
            AppVersion = "1.1.0",
            MaxRows = 10,
        });
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("2026-10-03 12:00", html, StringComparison.Ordinal);
        Assert.Contains("陽性率", html, StringComparison.Ordinal);
        Assert.Contains("白黒にする", html, StringComparison.Ordinal);
        Assert.Contains("決め方: 大津の方法", html, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,AQID", html, StringComparison.Ordinal);
        Assert.Contains("最初の 10 個", html, StringComparison.Ordinal);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
        Assert.Equal(10, html.Split("<tr><td>", StringSplitOptions.None).Length - 1);
    }
}
