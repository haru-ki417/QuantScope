using System.Globalization;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Samples;

namespace QuantScope.Tests;

public class PipelineTests
{
    private static Step S(string kind, Raster? img = null, params (string K, double V)[] values)
    {
        var s = StepCatalog.Create(kind, img);
        foreach (var (k, v) in values) s.Values[k] = v;
        return s;
    }

    [Fact]
    public void すべての手順に名前と説明があり_既定値で動く()
    {
        var sample = SampleImages.Create("tissue");
        var ids = StepCatalog.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var def in StepCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(def.Title));
            Assert.False(string.IsNullOrWhiteSpace(def.Summary));
            Assert.False(string.IsNullOrWhiteSpace(def.HowItWorks));
            foreach (var p in def.Parameters.Where(p => p.Kind == ParamKind.Choice)) Assert.NotEmpty(p.Choices);
            // マスクが要る手順は二値化のあとに置く
            var steps = new List<Step>();
            if (def.NeedsMask) steps.Add(S("threshold", sample.Image));
            steps.Add(StepCatalog.Create(def.Id, sample.Image));
            var run = PipelineRunner.Run(sample.Image, sample.Calibration, steps);
            var outcome = run.Outcomes[^1];
            Assert.True(outcome.Applied, $"{def.Id}: {outcome.Warning}");
        }
    }

    [Fact]
    public void 値を変えた手順から先だけを計算し直す()
    {
        var img = SampleImages.Create("shapes").Image;
        var steps = new List<Step> { S("gaussian", img), S("threshold", img), S("fillHoles", img) };
        var first = PipelineRunner.Run(img, Calibration.Pixels, steps);
        steps[1].Values["bright"] = 1;
        var second = PipelineRunner.Run(img, Calibration.Pixels, steps, first);
        Assert.Same(first.States[1], second.States[1]); // ぼかしは使い回し
        Assert.NotSame(first.States[2], second.States[2]);
        Assert.NotEqual(first.Final.Mask!.Count(), second.Final.Mask!.Count());
        // 入力の画像が変われば使い回さない
        var other = img.Clone();
        var third = PipelineRunner.Run(other, Calibration.Pixels, steps, second);
        Assert.NotSame(second.States[1], third.States[1]);
    }

    [Fact]
    public void マスクがないのにマスクの手順を入れると飛ばして理由を残す()
    {
        var img = SampleImages.Create("shapes").Image;
        var run = PipelineRunner.Run(img, Calibration.Pixels, [S("fillHoles", img), S("threshold", img)]);
        Assert.False(run.Outcomes[0].Applied);
        Assert.Contains("二値化", run.Outcomes[0].Warning, StringComparison.Ordinal);
        Assert.True(run.Outcomes[1].Applied);
        Assert.Contains("しきい値", run.Outcomes[1].Info, StringComparison.Ordinal);
    }

    [Fact]
    public void オフの手順は飛ばす()
    {
        var img = SampleImages.Create("shapes").Image;
        var inv = S("invert", img);
        inv.Enabled = false;
        var run = PipelineRunner.Run(img, Calibration.Pixels, [inv]);
        Assert.Same(img, run.Final.Image);
    }

    [Fact]
    public void 切り抜きと縮小は計測用の元画像とマスクと縮尺にもかかる()
    {
        var sample = SampleImages.Create("shapes");
        var img = sample.Image;
        var run = PipelineRunner.Run(img, sample.Calibration,
        [
            S("resize", img, ("factor", 0.5)),
            S("crop", img, ("x", 0), ("y", 0), ("width", 120), ("height", 120)),
            S("threshold", img),
        ]);
        var final = run.Final;
        Assert.Equal((120, 120), (final.Image.Width, final.Image.Height));
        Assert.Equal((120, 120), (final.Original.Width, final.Original.Height));
        Assert.Equal(2.0, final.Calibration.UnitsPerPixel, 9);
        var result = PipelineRunner.Analyze(final, new MeasureSettings())!;
        // 半分に縮めても、縮尺を直すので円の面積（µm²）は同じ
        var circle = result.Particles.Single(p => p.Area > 5000);
        Assert.InRange(circle.Area, Math.PI * 3600 * 0.97, Math.PI * 3600 * 1.03);
        // 縮小は二値化のあとには使えない
        var bad = PipelineRunner.Run(img, Calibration.Pixels, [S("threshold", img), S("resize", img)]);
        Assert.False(bad.Outcomes[1].Applied);
    }

    [Fact]
    public void 見本の蛍光の核をレシピどおりに数えると_ほぼ正しい数になる()
    {
        var sample = SampleImages.Create("nuclei");
        int truth = SampleImages.NucleiLayout().Count;
        var run = PipelineRunner.Run(sample.Image, sample.Calibration, sample.Recipe.Steps);
        Assert.All(run.Outcomes, o => Assert.True(o.Applied, o.Warning));
        var r = PipelineRunner.Analyze(run.Final, sample.Recipe.Measure)!;
        Assert.True(Math.Abs(r.Summary.Count - truth) <= 2, $"count {r.Summary.Count} truth {truth}");
        Assert.Equal("µm²", r.Summary.AreaUnit);
        // 核の大きさ（半径 8〜13 px = 4〜6.5 µm）
        Assert.InRange(r.Summary.MeanArea, Math.PI * 4 * 4, Math.PI * 6.5 * 6.5);
    }

    [Fact]
    public void 見本の組織は核の占有率を出せる()
    {
        var sample = SampleImages.Create("tissue");
        var run = PipelineRunner.Run(sample.Image, sample.Calibration, sample.Recipe.Steps);
        var r = PipelineRunner.Analyze(run.Final, sample.Recipe.Measure)!;
        Assert.InRange(r.Summary.AreaFraction, 2, 20);
        Assert.True(r.Summary.Count > 100, $"count {r.Summary.Count}");
    }

    [Fact]
    public void 範囲を指定して計測できる()
    {
        var sample = SampleImages.Create("shapes");
        var run = PipelineRunner.Run(sample.Image, Calibration.Pixels, sample.Recipe.Steps);
        var r = PipelineRunner.Analyze(run.Final, new MeasureSettings(), Roi.Rectangle(0, 0, 220, 220))!;
        var only = Assert.Single(r.Particles); // 左上の円だけ
        Assert.InRange(only.Area, Math.PI * 3600 * 0.99, Math.PI * 3600 * 1.01);
        Assert.Equal(220 * 220, r.Summary.AnalyzedArea);
    }

    [Fact]
    public void レシピはJSONで保存して読み戻せる()
    {
        var sample = SampleImages.Create("nuclei");
        var recipe = sample.Recipe.Clone();
        recipe.Calibration = sample.Calibration;
        recipe.Steps[1].Enabled = false;
        string json = recipe.ToJson();
        Assert.Contains("\"format\": \"quantscope-recipe\"", json, StringComparison.Ordinal);
        var back = Recipe.FromJson(json);
        Assert.Equal(recipe.Name, back.Name);
        Assert.Equal(recipe.Steps.Select(s => s.Signature()), back.Steps.Select(s => s.Signature()));
        Assert.Equal(recipe.Measure, back.Measure);
        Assert.Equal(sample.Calibration, back.Calibration);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"quantscope-recipe\",\"version\":1,\"steps\":[{\"kind\":\"rm -rf\"}]}")]
    [InlineData("{\"format\":\"quantscope-recipe\",\"version\":99,\"steps\":[]}")]
    public void 壊れたレシピや知らない手順は理由を添えて断る(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Recipe.FromJson(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task 一括処理は読めない画像があっても続ける()
    {
        var sample = SampleImages.Create("shapes");
        LoadedImage Load(string f) => f.EndsWith("bad.png", StringComparison.Ordinal) ? throw new InvalidDataException("画像として読めませんでした。") : new LoadedImage(sample.Image, sample.Calibration, "");
        var progress = new List<BatchProgress>();
        var results = await BatchProcessor.RunAsync(["a.png", "bad.png", "c.png"], Load, sample.Recipe, new Progress<BatchProgress>(progress.Add), TestContext.Current.CancellationToken);
        Assert.Equal(3, results.Count);
        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.Equal(8, results[2].Result!.Summary.Count);
        string summary = BatchProcessor.SummaryCsv(results);
        Assert.Contains("bad.png,,", summary, StringComparison.Ordinal);
        Assert.Contains("画像として読めませんでした", summary, StringComparison.Ordinal);
        string particles = BatchProcessor.ParticlesCsv(results);
        Assert.Equal(1 + 16, particles.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.StartsWith("ファイル,番号,面積 (µm²)", particles, StringComparison.Ordinal);
    }

    [Fact]
    public void CSVは地域の設定によらず小数点がドットで_式として実行されない()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var p = new Particle { Id = 1, Area = 12.5, Circularity = 0.75 };
            string csv = CsvExport.Particles([("=HYPERLINK(\"x\"),a.png", p)], "µm", "µm²");
            Assert.Contains(",12.5,", csv, StringComparison.Ordinal);
            Assert.Contains("\"'=HYPERLINK(\"\"x\"\"),a.png\"", csv, StringComparison.Ordinal);
            Assert.Equal("-1.5", CsvExport.Escape("-1.5"));
            Assert.Equal([0xEF, 0xBB, 0xBF], CsvExport.Encoding.GetPreamble());
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void 見本はどれも作れて毎回同じ()
    {
        foreach (var (id, _) in SampleImages.List)
        {
            var a = SampleImages.Create(id);
            var b = SampleImages.Create(id);
            Assert.Equal(a.Image.Data, b.Image.Data);
            Assert.True(a.Calibration.IsCalibrated);
            Assert.NotEmpty(a.Recipe.Steps);
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
        }
        Assert.Throws<ArgumentException>(() => SampleImages.Create("x"));
    }

    [Fact]
    public void 手順の値の範囲は画像に合わせる()
    {
        var def = StepCatalog.Get("threshold").Parameters.Single(p => p.Key == "value");
        Assert.Equal((0, 255, 127.5), def.Resolve(null));
        var twelveBit = new Raster(2, 1, 1, [100, 4095], 0, 65535);
        var (min, max, dflt) = def.Resolve(twelveBit);
        Assert.Equal(100, min);
        Assert.Equal(4095, max);
        Assert.Equal(2097.5, dflt);
    }
}
