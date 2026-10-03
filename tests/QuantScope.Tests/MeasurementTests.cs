using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.Tests;

public class MeasurementTests
{
    private static Particle Single(Mask m, Raster? intensity = null, Calibration? cal = null)
    {
        var r = ParticleAnalyzer.Analyze(m, intensity, cal ?? Calibration.Pixels, AnalysisOptions.Default);
        return Assert.Single(r.Particles);
    }

    [Fact]
    public void 円の面積と周囲長が理論値に近く_円形度はほぼ1()
    {
        var p = Single(TestImages.Disk(200, 200, 100, 100, 40));
        Assert.InRange(p.Area, Math.PI * 1600 * 0.99, Math.PI * 1600 * 1.01);
        Assert.InRange(p.Perimeter, 2 * Math.PI * 40 * 0.97, 2 * Math.PI * 40 * 1.03);
        Assert.InRange(p.Circularity, 0.95, 1.0);
        Assert.InRange(p.EquivalentDiameter, 79, 81);
        Assert.InRange(p.Feret, 79, 82);
        Assert.InRange(p.Solidity, 0.97, 1.0);
        Assert.InRange(p.AspectRatio, 0.99, 1.02);
        Assert.Equal(100, p.CentroidX, 3);
        Assert.Equal(100, p.CentroidY, 3);
    }

    [Fact]
    public void 正方形の周囲長と円形度とフェレ径()
    {
        var p = Single(TestImages.Rect(100, 100, 20, 30, 40, 40));
        Assert.Equal(1600, p.Area);
        Assert.InRange(p.Perimeter, 160 * 0.96, 160 * 1.01);
        Assert.InRange(p.Circularity, Math.PI / 4 * 0.98, Math.PI / 4 * 1.08);
        Assert.InRange(p.Feret, 40 * Math.Sqrt(2) - 0.5, 40 * Math.Sqrt(2) + 0.5);
        Assert.Equal((20, 30, 40, 40), (p.BoundsX, p.BoundsY, p.BoundsWidth, p.BoundsHeight));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(120)]
    public void 楕円の長軸短軸と向き(double angle)
    {
        var p = Single(TestImages.Ellipse(300, 300, 150, 150, 70, 30, angle));
        Assert.InRange(p.MajorAxis, 140 * 0.98, 140 * 1.02);
        Assert.InRange(p.MinorAxis, 60 * 0.98, 60 * 1.03);
        double diff = Math.Abs(p.Angle - angle);
        Assert.True(Math.Min(diff, 180 - diff) < 1.0, $"angle {p.Angle}");
    }

    [Fact]
    public void 縮尺をかけると面積は二乗_長さは一乗で変わる()
    {
        var m = TestImages.Disk(100, 100, 50, 50, 20);
        var px = Single(m);
        var um = Single(m, cal: new Calibration(0.5, "µm"));
        Assert.Equal(px.Area * 0.25, um.Area, 6);
        Assert.Equal(px.Perimeter * 0.5, um.Perimeter, 6);
        Assert.Equal(px.Circularity, um.Circularity, 9);
    }

    [Fact]
    public void 明るさの平均と合計を元画像から測る()
    {
        var m = TestImages.Rect(50, 50, 10, 10, 10, 10);
        var img = TestImages.Gray(50, 50, (x, _) => x);
        var p = Single(m, img);
        Assert.Equal(14.5, p.MeanIntensity, 6);
        Assert.Equal(14.5 * 100, p.IntegratedIntensity, 6);
        Assert.Equal(10, p.MinIntensity);
        Assert.Equal(19, p.MaxIntensity);
    }

    [Fact]
    public void 範囲を指定すると範囲の中だけで占有率を出す()
    {
        var m = TestImages.Rect(100, 100, 0, 0, 50, 100); // 左半分が対象
        var region = Roi.Rectangle(25, 0, 75, 100).ToMask(100, 100); // 真ん中の 50 列
        var r = ParticleAnalyzer.Analyze(m, null, Calibration.Pixels, AnalysisOptions.Default, region);
        Assert.Equal(50.0, r.Summary.AreaFraction, 6);
        Assert.Equal(5000, r.Summary.AnalyzedArea);
        Assert.True(Assert.Single(r.Particles).TouchesEdge); // 範囲のふちで切れている
    }

    [Fact]
    public void 大きさとふちで粒を選べる()
    {
        var m = new Mask(120, 120);
        TestImages.Disk(120, 120, 30, 30, 4, m);   // 小さい
        TestImages.Disk(120, 120, 70, 70, 15, m);  // 中くらい
        TestImages.Rect(120, 120, 100, 0, 20, 20, m); // ふちに触れる
        var all = ParticleAnalyzer.Analyze(m, null, Calibration.Pixels, AnalysisOptions.Default);
        Assert.Equal(3, all.Summary.Count);
        var filtered = ParticleAnalyzer.Analyze(m, null, Calibration.Pixels, new AnalysisOptions { MinArea = 100, ExcludeEdges = true });
        var p = Assert.Single(filtered.Particles);
        Assert.Equal(1, p.Id);
        Assert.InRange(p.Area, Math.PI * 225 * 0.97, Math.PI * 225 * 1.03);
        // 除いた粒は番号の画像でも 0 になる
        Assert.Equal(0, filtered.Labels.Labels[(30 * 120) + 30]);
        Assert.Equal(1, filtered.Labels.Labels[(70 * 120) + 70]);
    }

    [Fact]
    public void 星はくぼみがあるので充実度が小さい()
    {
        var sample = QuantScope.Core.Samples.SampleImages.Create("shapes");
        var mask = QuantScope.Core.Processing.Thresholds.Apply(sample.Image, 125, true);
        var r = ParticleAnalyzer.Analyze(mask, sample.Image, Calibration.Pixels, AnalysisOptions.Default);
        Assert.Equal(8, r.Summary.Count);
        var star = r.Particles.Single(p => Math.Abs(p.CentroidX - 640) < 3 && Math.Abs(p.CentroidY - 110) < 3);
        var circle = r.Particles.Single(p => Math.Abs(p.CentroidX - 110) < 2 && Math.Abs(p.CentroidY - 110) < 2);
        var ring = r.Particles.Single(p => Math.Abs(p.CentroidX - 110) < 2 && Math.Abs(p.CentroidY - 300) < 2);
        var bar = r.Particles.Single(p => Math.Abs(p.CentroidX - 300) < 2 && Math.Abs(p.CentroidY - 300) < 2);
        Assert.True(star.Solidity < 0.8, $"star solidity {star.Solidity}");
        Assert.True(circle.Solidity > 0.97);
        Assert.InRange(circle.Area, Math.PI * 3600 * 0.99, Math.PI * 3600 * 1.01);
        Assert.InRange(ring.Area, Math.PI * (3600 - 900) * 0.98, Math.PI * (3600 - 900) * 1.02);
        Assert.InRange(bar.AspectRatio, 4.7, 5.3);
        Assert.InRange(bar.Feret, Math.Sqrt((180 * 180) + (36 * 36)) - 2, Math.Sqrt((180 * 180) + (36 * 36)) + 2);
    }

    [Fact]
    public void 線に沿った明るさは補間して読む()
    {
        var img = TestImages.Gray(100, 20, (x, _) => x * 2);
        var prof = LineProfile.Sample(img, new PointD(10.5, 10), new PointD(60.5, 10), new Calibration(0.1, "mm"));
        Assert.Equal(51, prof.Values.Length);
        Assert.Equal(20, prof.Values[0], 3);
        Assert.Equal(120, prof.Values[^1], 3);
        Assert.Equal(5.0, prof.Length, 6);
        Assert.Equal(2.5, prof.Distances[25], 6);
    }

    [Fact]
    public void 似ている場所を探す()
    {
        var rng = new Random(3);
        var bg = TestImages.Gray(400, 300, (_, _) => (float)(60 + rng.Next(20)));
        var d = (float[])bg.Data.Clone();
        // 十字の模様を 3 か所に置く
        (int X, int Y)[] at = [(40, 50), (220, 80), (300, 200)];
        foreach (var (ax, ay) in at)
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 40; x++)
                    d[((ay + y) * 400) + ax + x] = (Math.Abs(x - 20) < 5 || Math.Abs(y - 20) < 5) ? 220 : 40;
        var img = bg.With(d);
        var template = QuantScope.Core.Processing.Geometry.Crop(img, 40, 50, 40, 40);
        var found = TemplateMatcher.Find(img, template, minScore: 0.8);
        Assert.Equal(3, found.Count);
        foreach (var (ax, ay) in at) Assert.Contains(found, f => Math.Abs(f.X - ax) <= 1 && Math.Abs(f.Y - ay) <= 1 && f.Score > 0.95);
    }

    [Fact]
    public void ラベリングは4連結と8連結で斜めの扱いが違う()
    {
        var m = new Mask(4, 4);
        m[0, 0] = true;
        m[1, 1] = true;
        m[3, 3] = true;
        Assert.Equal(2, Labeling.Label(m, eightConnected: true).Count);
        Assert.Equal(3, Labeling.Label(m, eightConnected: false).Count);
    }

    [Fact]
    public void ラベリングはU字形を1つの粒にまとめる()
    {
        // 上から見ると 2 本に見えて、下でつながる形（番号のまとめ直しが要る）
        var m = new Mask(7, 5);
        for (int y = 0; y < 5; y++)
        {
            m[1, y] = true;
            m[5, y] = true;
        }
        for (int x = 1; x <= 5; x++) m[x, 4] = true;
        var l = Labeling.Label(m);
        Assert.Equal(1, l.Count);
        Assert.Equal(13, l.Areas()[1]);
    }
}
