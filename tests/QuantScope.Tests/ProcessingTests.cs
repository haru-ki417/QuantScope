using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Processing;

namespace QuantScope.Tests;

public class ProcessingTests
{
    [Fact]
    public void 距離変換は総当たりと一致する()
    {
        var rng = new Random(5);
        var m = new Mask(37, 29);
        for (int i = 0; i < m.PixelCount; i++) m.Bits[i] = rng.NextDouble() < 0.7;
        var toBg = DistanceTransform.ToBackground(m);
        var toFg = DistanceTransform.ToForeground(m);
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
            {
                double bestBg = double.MaxValue, bestFg = double.MaxValue;
                // 画像の外（1 画素外側）も背景として数える
                for (int yy = -1; yy <= m.Height; yy++)
                    for (int xx = -1; xx <= m.Width; xx++)
                    {
                        bool inside = xx >= 0 && yy >= 0 && xx < m.Width && yy < m.Height;
                        double d = Math.Sqrt(((x - xx) * (x - xx)) + ((y - yy) * (y - yy)));
                        if (!inside || !m[xx, yy]) bestBg = Math.Min(bestBg, d);
                        if (inside && m[xx, yy]) bestFg = Math.Min(bestFg, d);
                    }
                int i = (y * m.Width) + x;
                Assert.Equal(m[x, y] ? bestBg : 0, toBg[i], 4);
                Assert.Equal(m[x, y] ? 0 : bestFg, toFg[i], 4);
            }
    }

    [Fact]
    public void 膨張は正確な円になる()
    {
        var m = new Mask(41, 41);
        m[20, 20] = true;
        var d = Morphology.Dilate(m, 5);
        // 半径 5 以内の格子点の数
        int expected = 0;
        for (int y = -5; y <= 5; y++)
            for (int x = -5; x <= 5; x++)
                if ((x * x) + (y * y) <= 25) expected++;
        Assert.Equal(expected, d.Count());
        Assert.Equal(1, Morphology.Erode(d, 4.99).Count());
    }

    [Fact]
    public void オープニングは小さなゴミを消し_クロージングは小さな穴をふさぐ()
    {
        var m = TestImages.Disk(100, 100, 50, 50, 20);
        m[5, 5] = true;
        m[6, 5] = true;
        m[50, 50] = false;
        var opened = Morphology.Apply(m, MorphologyOperation.Open, 2);
        Assert.False(opened[5, 5]);
        Assert.True(opened[45, 50]);
        var closed = Morphology.Apply(m, MorphologyOperation.Close, 2);
        Assert.True(closed[50, 50]);
    }

    [Fact]
    public void 穴埋め_大きさで選ぶ_ふちの粒を除く()
    {
        var ring = TestImages.Disk(80, 80, 40, 40, 20);
        var hole = TestImages.Disk(80, 80, 40, 40, 8);
        for (int i = 0; i < ring.PixelCount; i++) if (hole.Bits[i]) ring.Bits[i] = false;
        Assert.Equal(TestImages.Disk(80, 80, 40, 40, 20).Count(), Morphology.FillHoles(ring).Count());

        var m = TestImages.Disk(80, 80, 40, 40, 10);
        TestImages.Rect(80, 80, 5, 5, 3, 3, m);
        TestImages.Rect(80, 80, 0, 60, 10, 10, m);
        Assert.Equal(2, Labeling.Label(Morphology.FilterBySize(m, 50)).Count);
        Assert.Equal(1, Labeling.Label(Morphology.FilterBySize(m, 50, 200)).Count);
        Assert.Equal(2, Labeling.Label(Morphology.ClearBorder(m)).Count);
    }

    [Fact]
    public void くっついた2つの円を分け_楕円1つは分けない()
    {
        var m = TestImages.Disk(160, 100, 55, 50, 22);
        TestImages.Disk(160, 100, 95, 50, 22, m);
        Assert.Equal(1, Labeling.Label(m).Count);
        var split = Watershed.Split(m, 1.0);
        var labels = Labeling.Label(split);
        Assert.Equal(2, labels.Count);
        var areas = labels.Areas();
        Assert.InRange(areas[1] / (double)areas[2], 0.9, 1.1);
        // 切れ目は細い（失う面積は 3% 未満）
        Assert.True(split.Count() > m.Count() * 0.97);

        var ellipse = TestImages.Ellipse(160, 100, 80, 50, 45, 25, 20);
        Assert.Equal(1, Labeling.Label(Watershed.Split(ellipse, 1.0)).Count);
    }

    [Fact]
    public void 大津の方法は2つの山の間を選ぶ()
    {
        var rng = new Random(1);
        var img = TestImages.Gray(100, 100, (x, _) => (float)Math.Clamp((x < 50 ? 60 : 180) + (rng.NextDouble() * 20) - 10, 0, 255));
        double t = Thresholds.Find(img, ThresholdMethod.Otsu);
        Assert.InRange(t, 71, 170);
        Assert.Equal(5000, Thresholds.Apply(img, t, brightObjects: true).Count());
        Assert.Equal(5000, Thresholds.Apply(img, t, brightObjects: false).Count());
        Assert.InRange(Thresholds.Find(img, ThresholdMethod.IsoData), 71, 170);
        Assert.InRange(Thresholds.Find(img, ThresholdMethod.Mean), 71, 170);
    }

    [Fact]
    public void 三角法は対象が少ない画像でも背景と分ける()
    {
        var rng = new Random(2);
        // 背景（暗い・多い）と、少しの明るい点
        var img = TestImages.Gray(200, 200, (x, y) => (x % 20 < 2 && y % 20 < 2) ? 200 : (float)(30 + (rng.NextDouble() * 10)));
        double t = Thresholds.Find(img, ThresholdMethod.Triangle);
        Assert.InRange(t, 41, 199);
        Assert.Equal(400, Thresholds.Apply(img, t, true).Count());
    }

    [Fact]
    public void 色の範囲で選ぶ_色相は0度をまたげる()
    {
        var d = new float[4 * 3];
        float[][] colors = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 0, 40]];
        for (int i = 0; i < 4; i++) Array.Copy(colors[i], 0, d, i * 3, 3);
        var img = new Raster(4, 1, 3, d);
        var reds = Thresholds.Hsv(img, 340, 20, 0.5, 1, 0.5, 1);
        Assert.Equal([true, false, false, true], reds.Bits);
        Assert.Equal([false, false, true, false], Thresholds.Hsv(img, 200, 260, 0.5, 1, 0.5, 1).Bits);
        var (h, s, v) = Thresholds.ToHsv(1, 0.5, 0);
        Assert.Equal(30, h, 6);
        Assert.Equal(1, s, 6);
        Assert.Equal(1, v, 6);
    }

    [Fact]
    public void kmeansは暗い順に組を並べ_毎回同じ結果()
    {
        var img = TestImages.Gray(90, 30, (x, _) => x < 30 ? 200 : x < 60 ? 20 : 110);
        var a = KMeans.Cluster(img, 3);
        var b = KMeans.Cluster(img, 3);
        Assert.Equal(a.Labels, b.Labels);
        Assert.Equal(0, a.Labels[(0 * 90) + 40]);
        Assert.Equal(2, a.Labels[10]);
        Assert.Equal(1, a.Labels[75]);
        Assert.Equal(20, a.Centers[0][0], 1);
        Assert.Equal(900, a.MaskOf(2).Count());
    }

    [Fact]
    public void ぼかしは明るさの合計を保ち_大きなσの近似は正確な計算に近い()
    {
        var rng = new Random(4);
        var img = TestImages.Gray(120, 90, (_, _) => (float)rng.Next(256));
        var blurred = Filters.GaussianBlur(img, 2);
        Assert.InRange(blurred.Data.Average(), img.Data.Average() - 1, img.Data.Average() + 1);
        var exact = Filters.GaussianExact(img.Data, 120, 90, 5);
        var boxes = Filters.GaussianBoxes(img.Data, 120, 90, 5);
        double maxDiff = 0;
        for (int y = 20; y < 70; y++)
            for (int x = 20; x < 100; x++) maxDiff = Math.Max(maxDiff, Math.Abs(exact[(y * 120) + x] - boxes[(y * 120) + x]));
        Assert.True(maxDiff < 4, $"max diff {maxDiff}");
    }

    [Fact]
    public void メディアンはごま塩を消す()
    {
        var img = TestImages.Gray(50, 50, (_, _) => 100);
        var noisy = Filters.AddNoise(img, 0, 0.05, 7);
        Assert.Contains(noisy.Data, v => v is 0 or 255);
        var clean = Filters.Median(noisy, 1);
        Assert.True(clean.Data.Count(v => v != 100) < 10);
        Assert.Equal(noisy.Data, Filters.AddNoise(img, 0, 0.05, 7).Data); // 同じシードなら同じノイズ
    }

    [Fact]
    public void Sobelは境目で最も強い()
    {
        var img = TestImages.Gray(40, 20, (x, _) => x < 20 ? 0 : 255);
        var e = Filters.Sobel(img);
        Assert.Equal(0, e.Get(5, 10));
        Assert.Equal(255, e.Get(19, 10), 0);
    }

    [Fact]
    public void 背景の補正で照明のむらが減る()
    {
        var img = TestImages.Gray(200, 200, (x, y) => (float)(50 + (x * 0.5) + ((x % 40 < 6 && y % 40 < 6) ? 100 : 0)));
        var corrected = Filters.CorrectBackground(img, 30, BackgroundMode.Subtract);
        double left = corrected.Get(20, 20), right = corrected.Get(180, 20);
        Assert.True(Math.Abs(left - right) < 10, $"{left} vs {right}");
    }

    [Fact]
    public void ウインドウレベルとガンマと反転()
    {
        var ct = new Raster(3, 1, 1, [-1000, 40, 400], -1024, 3071);
        var wl = Adjust.WindowLevel(ct, 40, 400);
        Assert.Equal([0, 127.5f, 255], wl.Data);
        Assert.Equal(255, wl.NominalMax);
        var g = Adjust.Gamma(TestImages.Gray(2, 1, (x, _) => x * 255), 0.5);
        Assert.Equal([0, 255f], g.Data);
        Assert.Equal([255, 0f], Adjust.Invert(TestImages.Gray(2, 1, (x, _) => x * 255)).Data);
    }

    [Fact]
    public void 平坦化とCLAHEは明るさの順番を保ち範囲に収まる()
    {
        var img = TestImages.Gray(64, 64, (x, y) => 100 + ((x + y) / 4f));
        foreach (var r in new[] { Adjust.Equalize(img), Adjust.Clahe(img, 4, 2) })
        {
            Assert.All(r.Data, v => Assert.InRange(v, 0, 255));
            Assert.True(r.Get(0, 0) < r.Get(63, 63));
            Assert.True(r.Data.Max() - r.Data.Min() > img.Data.Max() - img.Data.Min());
        }
    }

    [Fact]
    public void 自動コントラストは範囲いっぱいに広げる()
    {
        var img = TestImages.Gray(100, 1, (x, _) => 100 + (x * 0.5f));
        var r = Adjust.AutoContrast(img, 0);
        Assert.Equal(0, r.Data.Min(), 0);
        Assert.Equal(255, r.Data.Max(), 0);
    }

    [Fact]
    public void 回転4回で元に戻り_切り抜きと縮小()
    {
        var img = TestImages.Gray(7, 5, (x, y) => (y * 7) + x);
        var r = img;
        for (int i = 0; i < 4; i++) r = Geometry.Rotate(r, Rotation.Clockwise90);
        Assert.Equal(img.Data, r.Data);
        var cw = Geometry.Rotate(img, Rotation.Clockwise90);
        Assert.Equal((5, 7), (cw.Width, cw.Height));
        Assert.Equal(img.Get(0, 4), cw.Get(0, 0)); // 左下が左上へ
        Assert.Equal(img.Data, Geometry.Rotate(Geometry.Rotate(img, Rotation.Clockwise90), Rotation.CounterClockwise90).Data);
        var c = Geometry.Crop(img, 2, 1, 3, 2);
        Assert.Equal([9f, 10, 11, 16, 17, 18], c.Data);
        var half = Geometry.Resize(TestImages.Gray(4, 4, (x, _) => x < 2 ? 0 : 100), 0.5);
        Assert.Equal([0f, 100, 0, 100], half.Data);
    }

    [Fact]
    public void 自動の切り抜きは対象を囲む()
    {
        var img = TestImages.Gray(100, 80, (x, y) => x >= 30 && x < 60 && y >= 10 && y < 40 ? 200 : 5);
        Assert.Equal((28, 8, 34, 34), Geometry.FindContentBounds(img, 15, true, 2));
        Assert.Null(Geometry.FindContentBounds(TestImages.Gray(5, 5, (_, _) => 0), 15, true, 0));
    }

    [Fact]
    public void ヒストグラムの統計()
    {
        var img = TestImages.Gray(10, 10, (x, _) => x * 10);
        var h = Histogram.Of(img);
        Assert.Equal(256, h.Bins);
        Assert.Equal(100, h.Total);
        Assert.Equal(45, h.Mean, 6);
        Assert.Equal(90, h.DataMax);
        Assert.Equal(10, h.Counts[50]);
        Assert.InRange(h.Median, 40, 50);
        var within = Roi.Rectangle(0, 0, 5, 10).ToMask(10, 10);
        Assert.Equal(20, Histogram.Of(img, within).Mean, 6);
    }

    [Fact]
    public void 範囲の形ごとのマスク()
    {
        Assert.Equal(100, Roi.Rectangle(10, 10, 20, 20).ToMask(50, 50).Count());
        Assert.InRange(Roi.Ellipse(0, 0, 40, 40).ToMask(50, 50).Count(), Math.PI * 400 * 0.97, Math.PI * 400 * 1.03);
        var tri = Roi.Polygon([new(0, 0), new(40, 0), new(0, 40)]);
        Assert.InRange(tri.ToMask(50, 50).Count(), 780, 840);
        Assert.Equal((0, 0, 50, 50), Roi.Rectangle(-10, -5, 80, 90).ClampedPixelRect(50, 50));
    }

    [Fact]
    public void 縮尺を長さの分かる線から決める()
    {
        var c = Calibration.FromKnownLength(200, 50, "µm");
        Assert.Equal(0.25, c.UnitsPerPixel, 9);
        Assert.Equal("µm²", c.AreaUnit);
        Assert.Equal(0.5, c.Scaled(0.5).UnitsPerPixel, 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => Calibration.FromKnownLength(0, 5, "mm"));
        Assert.False(Calibration.Pixels.IsCalibrated);
    }
}
