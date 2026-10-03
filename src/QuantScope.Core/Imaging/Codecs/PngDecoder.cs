using System.Buffers.Binary;
using System.IO.Compression;

namespace QuantScope.Core.Imaging.Codecs;

/// <summary>
/// PNG を読む（16 bit の白黒・カラーを 16 bit のまま読むため。ブラウザーの画像の部品は 8 bit に丸めてしまう）。
/// 白黒・カラー・パレット・透明度つき、1〜16 bit に対応。透明度は使わない。インターレースの PNG は読まない（null を返す）。
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    /// <summary>読めない（インターレース）ときは null。壊れているときは InvalidDataException</summary>
    public static Raster? Decode(byte[] data, long maxPixels = 120_000_000)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsPng(data)) throw new InvalidDataException("PNG ではありません。");
        int pos = 8;
        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            if (len < 0 || pos + 12 + (long)len > data.Length) throw new InvalidDataException("PNG が途中で切れています。");
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }
            pos += 12 + len;
            if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG の大きさが読めません。");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"画像が大きすぎます（{width} × {height}）。");
        if (interlace != 0) return null;
        int samples = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("PNG の色の形式が正しくありません。") };
        if (depth is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException("PNG のビット数が正しくありません。");
        if (colorType == 3 && palette is null) throw new InvalidDataException("PNG のパレットがありません。");

        int bitsPerPixel = samples * depth;
        int stride = ((width * bitsPerPixel) + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[(long)(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n == 0) throw new InvalidDataException("PNG の画素が足りません。");
                read += n;
            }
        }
        Unfilter(raw, stride, height, bpp);

        bool color = colorType is 2 or 6 || colorType == 3;
        float max = depth == 16 && colorType != 3 ? 65535 : 255;
        var outData = new float[(long)width * height * (color ? 3 : 1)];
        for (int y = 0; y < height; y++)
        {
            int row = (y * (stride + 1)) + 1;
            for (int x = 0; x < width; x++)
            {
                if (colorType == 3)
                {
                    int idx = Sample(raw, row, x, depth);
                    int o = ((y * width) + x) * 3;
                    for (int c = 0; c < 3; c++) outData[o + c] = idx * 3 + c < palette!.Length ? palette[(idx * 3) + c] : 0;
                }
                else if (color)
                {
                    int o = ((y * width) + x) * 3;
                    for (int c = 0; c < 3; c++) outData[o + c] = SampleAt(raw, row, (x * samples) + c, depth);
                }
                else
                {
                    float v = SampleAt(raw, row, x * samples, depth);
                    // 1・2・4 bit の白黒は 0〜255 に広げる
                    if (depth < 8) v = v * 255f / ((1 << depth) - 1);
                    outData[(y * width) + x] = v;
                }
            }
        }
        var r = new Raster(width, height, color ? 3 : 1, outData, 0, max);
        return color ? GrayIfNeutral(r) : r;
    }

    private static float SampleAt(byte[] raw, int row, int index, int depth) => depth switch
    {
        16 => (raw[row + (index * 2)] << 8) | raw[row + (index * 2) + 1],
        8 => raw[row + index],
        _ => Sample(raw, row, index, depth),
    };

    private static int Sample(byte[] raw, int row, int index, int depth)
    {
        if (depth == 8) return raw[row + index];
        int bit = index * depth;
        int b = raw[row + (bit >> 3)];
        int shift = 8 - depth - (bit & 7);
        return (b >> shift) & ((1 << depth) - 1);
    }

    private static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int o = y * (stride + 1);
            int filter = raw[o];
            int cur = o + 1, prev = o + 1 - (stride + 1);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? raw[cur + i - bpp] : 0;
                int b = y > 0 ? raw[prev + i] : 0;
                int c = i >= bpp && y > 0 ? raw[prev + i - bpp] : 0;
                int v = raw[cur + i];
                raw[cur + i] = filter switch
                {
                    0 => (byte)v,
                    1 => (byte)(v + a),
                    2 => (byte)(v + b),
                    3 => (byte)(v + ((a + b) >> 1)),
                    4 => (byte)(v + Paeth(a, b, c)),
                    _ => throw new InvalidDataException("PNG のフィルターが正しくありません。"),
                };
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>R・G・B がすべて同じ（見た目が白黒）なら白黒にする</summary>
    public static Raster GrayIfNeutral(Raster r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!r.IsColor) return r;
        var d = r.Data;
        for (int i = 0; i < r.PixelCount; i++)
            if (d[i * 3] != d[(i * 3) + 1] || d[i * 3] != d[(i * 3) + 2]) return r;
        var g = new float[r.PixelCount];
        for (int i = 0; i < g.Length; i++) g[i] = d[i * 3];
        return new Raster(r.Width, r.Height, 1, g, r.NominalMin, r.NominalMax);
    }
}
