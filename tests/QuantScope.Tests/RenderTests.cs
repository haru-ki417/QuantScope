using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Rendering;

namespace QuantScope.Tests;

public class RenderTests
{
    [Fact]
    public void 白黒の8bitはそのままの明るさで_カラーはBGRの順に並べる()
    {
        var gray = TestImages.Gray(2, 1, (x, _) => x * 200);
        Assert.Equal([0, 0, 0, 255, 200, 200, 200, 255], DisplayRenderer.ToBgra(gray, ColorMap.Gray));
        var color = new Raster(1, 1, 3, [10, 20, 30]);
        Assert.Equal([30, 20, 10, 255], DisplayRenderer.ToBgra(color, ColorMap.Gray));
    }

    [Fact]
    public void 十六bitは使っている範囲を広げて見せる()
    {
        var img = new Raster(1000, 1, 1, Enumerable.Range(0, 1000).Select(i => 1000f + i).ToArray(), 0, 65535);
        var (lo, hi) = DisplayRenderer.DisplayRange(img);
        Assert.InRange(lo, 1000, 1003);
        Assert.InRange(hi, 1996, 2000);
        var b = DisplayRenderer.ToBgra(img, ColorMap.Gray);
        Assert.Equal(0, b[0]);
        Assert.Equal(255, b[^2]);
    }

    [Fact]
    public void 色の表は暗い色から明るい色へ()
    {
        foreach (var map in new[] { ColorMap.Gray, ColorMap.Fire, ColorMap.Viridis })
        {
            var lut = DisplayRenderer.Lut(map);
            double Luma(uint c) => (0.299 * ((c >> 16) & 255)) + (0.587 * ((c >> 8) & 255)) + (0.114 * (c & 255));
            Assert.True(Luma(lut[0]) < Luma(lut[128]) && Luma(lut[128]) < Luma(lut[255]), map.ToString());
        }
    }

    [Fact]
    public void マスクのふちは濃く_中は薄く_外は透明()
    {
        var m = TestImages.Rect(5, 5, 1, 1, 3, 3);
        var o = DisplayRenderer.MaskOverlay(m);
        Assert.Equal(0, o[3]);                    // 外
        Assert.Equal(255, o[(((1 * 5) + 1) * 4) + 3]); // ふち
        Assert.Equal(110, o[(((2 * 5) + 2) * 4) + 3]); // 中
    }

    [Fact]
    public void 選んだ粒は緑で_重ねると色が混ざる()
    {
        var labels = Labeling.Label(TestImages.Rect(4, 1, 0, 0, 4, 1));
        var o = DisplayRenderer.LabelOverlay(labels, OverlayColoring.Uniform, selectedId: 1);
        Assert.Equal([0x9A, 0xD1, 0x45], o[..3]);
        var mixed = DisplayRenderer.Compose(new byte[16], o, 1.0);
        Assert.Equal(0x9A, mixed[0]);
        Assert.NotEqual(DisplayRenderer.ObjectColor(1), DisplayRenderer.ObjectColor(2));
    }
}
