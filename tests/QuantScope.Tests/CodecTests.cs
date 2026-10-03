using System.Buffers.Binary;
using System.IO.Compression;
using QuantScope.Core.Imaging.Codecs;

namespace QuantScope.Tests;

public class CodecTests
{
    private static byte[] Chunk(string type, byte[] body)
    {
        var o = new byte[12 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(o, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(o, 4);
        body.CopyTo(o, 8);
        return o; // CRC は読まないので 0 のまま
    }

    /// <summary>テスト用の PNG（フィルターは行ごとに変える）</summary>
    private static byte[] Png(int w, int h, int depth, int colorType, Func<int, int, int[]> px)
    {
        int samples = colorType switch { 0 => 1, 2 => 3, 6 => 4, _ => 1 };
        int bpp = samples * depth / 8;
        int stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        var prevRow = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            var row = new byte[stride];
            for (int x = 0; x < w; x++)
            {
                var v = px(x, y);
                for (int c = 0; c < samples; c++)
                {
                    int o = (x * bpp) + (c * depth / 8);
                    if (depth == 16)
                    {
                        row[o] = (byte)(v[c] >> 8);
                        row[o + 1] = (byte)v[c];
                    }
                    else
                    {
                        row[o] = (byte)v[c];
                    }
                }
            }
            int filter = y % 3; // 0: なし 1: 左との差 2: 上との差
            raw[y * (stride + 1)] = (byte)filter;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = y > 0 ? prevRow[i] : 0;
                raw[(y * (stride + 1)) + 1 + i] = (byte)(filter switch { 1 => row[i] - a, 2 => row[i] - b, _ => row[i] });
            }
            prevRow = row;
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", ms.ToArray()), .. Chunk("IEND", [])];
    }

    [Fact]
    public void 十六bitの白黒PNGを16bitのまま読む()
    {
        var png = Png(5, 4, 16, 0, (x, y) => [1000 + (x * 3000) + (y * 7)]);
        var r = PngDecoder.Decode(png)!;
        Assert.False(r.IsColor);
        Assert.Equal(65535, r.NominalMax);
        Assert.Equal(1000 + (4 * 3000) + (3 * 7), r.Get(4, 3));
        Assert.Equal(1000 + 7, r.Get(0, 1));
    }

    [Fact]
    public void カラーのPNGは3色で_同じ値の色は白黒にする()
    {
        var color = PngDecoder.Decode(Png(3, 3, 8, 6, (x, y) => [x * 50, y * 60, 7, 255]))!;
        Assert.True(color.IsColor);
        Assert.Equal(120, color.Get(1, 2, 1));
        var gray = PngDecoder.Decode(Png(3, 2, 8, 2, (x, _) => [x * 9, x * 9, x * 9]))!;
        Assert.False(gray.IsColor);
        Assert.Equal(18, gray.Get(2, 1));
    }

    private static byte[] Tiff16(int w, int h, Func<int, int, int> px, bool deflate, bool predictor)
    {
        var pixels = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
        {
            int prev = 0;
            for (int x = 0; x < w; x++)
            {
                int v = px(x, y);
                int stored = predictor ? (v - prev) & 0xFFFF : v;
                prev = v;
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(((y * w) + x) * 2), (ushort)stored);
            }
        }
        byte[] strip = pixels;
        if (deflate)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(pixels);
            strip = ms.ToArray();
        }
        var tags = new List<(ushort Tag, ushort Type, uint Value)>
        {
            (256, 4, (uint)w), (257, 4, (uint)h), (258, 3, 16), (259, 3, deflate ? 8u : 1u), (262, 3, 1),
            (273, 4, 0), (277, 3, 1), (278, 4, (uint)h), (279, 4, (uint)strip.Length), (317, 3, predictor ? 2u : 1u),
        };
        int ifdSize = 2 + (tags.Count * 12) + 4;
        uint stripOffset = (uint)(8 + ifdSize);
        var o = new byte[stripOffset + strip.Length];
        "II"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(8), (ushort)tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            int e = 10 + (i * 12);
            var (tag, type, value) = tags[i];
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(e), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(e + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(e + 4), 1);
            uint v = tag == 273 ? stripOffset : value;
            if (type == 3) BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(e + 8), (ushort)v);
            else BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(e + 8), v);
        }
        strip.CopyTo(o, (int)stripOffset);
        return o;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 十六bitのTIFFを読む_圧縮と差分の予測にも対応(bool deflate, bool predictor)
    {
        var tif = Tiff16(7, 5, (x, y) => 500 + (x * 911) + (y * 13), deflate, predictor);
        var r = TiffDecoder.Decode(tif);
        Assert.Equal((7, 5), (r.Width, r.Height));
        Assert.Equal(65535, r.NominalMax);
        Assert.Equal(500 + (6 * 911) + (4 * 13), r.Get(6, 4));
        Assert.Equal(500 + 13, r.Get(0, 1));
    }

    [Fact]
    public void 読めない形式はブラウザーに任せ_壊れたファイルは理由を出す()
    {
        Assert.Null(ImageFiles.TryDecode([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], "a.jpg"));
        Assert.Throws<InvalidDataException>(() => ImageFiles.TryDecode([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13], "a.png"));
        var img = ImageFiles.FromRgba([10, 10, 10, 255, 200, 200, 200, 255], 2, 1, "JPEG");
        Assert.False(img.Raster.IsColor);
        Assert.Contains("白黒", img.Description, StringComparison.Ordinal);
    }
}
