using System.IO.Compression;

namespace QuantScope.Core.Imaging.Codecs;

/// <summary>
/// TIFF を読む（顕微鏡のカメラがよく使う 16 bit の白黒・カラー）。
/// 1 枚目の画像だけを読む。ストリップ形式・圧縮なし／LZW／Deflate／PackBits・差分の予測・8／16 bit の整数と 32 bit の小数に対応。
/// タイル形式など対応していないものは NotSupportedException。
/// </summary>
public static class TiffDecoder
{
    public static bool IsTiff(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42));

    public static Raster Decode(byte[] data, long maxPixels = 120_000_000)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsTiff(data)) throw new InvalidDataException("TIFF ではありません。");
        bool le = data[0] == 'I';
        try
        {
            return DecodeCore(data, le, maxPixels);
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidDataException("TIFF が途中で切れているか、壊れています。", ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("TIFF が途中で切れているか、壊れています。", ex);
        }
    }

    private static Raster DecodeCore(byte[] data, bool le, long maxPixels)
    {
        uint U16(long o) => le ? (uint)(data[o] | (data[o + 1] << 8)) : (uint)((data[o] << 8) | data[o + 1]);
        uint U32(long o) => le
            ? (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24))
            : (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);

        long ifd = U32(4);
        int count = (int)U16(ifd);
        var tags = new Dictionary<int, uint[]>();
        for (int i = 0; i < count; i++)
        {
            long e = ifd + 2 + (i * 12);
            int tag = (int)U16(e);
            int type = (int)U16(e + 2);
            uint n = U32(e + 4);
            int size = type switch { 3 => 2, 4 => 4, 1 or 2 or 6 or 7 => 1, _ => 4 };
            long valOff = n * size <= 4 ? e + 8 : U32(e + 8);
            if (n > 1_000_000) throw new InvalidDataException("TIFF のタグが正しくありません。");
            var vals = new uint[n];
            for (int k = 0; k < n; k++)
                vals[k] = size switch { 2 => U16(valOff + (k * 2)), 1 => data[valOff + k], _ => U32(valOff + (k * 4)) };
            tags[tag] = vals;
        }
        uint Tag(int t, uint def) => tags.TryGetValue(t, out var v) && v.Length > 0 ? v[0] : def;

        int width = (int)Tag(256, 0), height = (int)Tag(257, 0);
        if (width <= 0 || height <= 0) throw new InvalidDataException("TIFF の大きさが読めません。");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"画像が大きすぎます（{width} × {height}）。");
        if (tags.ContainsKey(322)) throw new NotSupportedException("タイル形式の TIFF には対応していません。");
        int bits = (int)Tag(258, 1);
        int spp = (int)Tag(277, 1);
        int compression = (int)Tag(259, 1);
        int photometric = (int)Tag(262, 1);
        int planar = (int)Tag(284, 1);
        int predictor = (int)Tag(317, 1);
        int format = (int)Tag(339, 1);
        if (planar != 1 && spp > 1) throw new NotSupportedException("色ごとに分けて保存された TIFF には対応していません。");
        if (bits is not (8 or 16 or 32) || (bits == 32 && format != 3)) throw new NotSupportedException($"{bits} bit の TIFF には対応していません。");
        if (photometric is not (0 or 1 or 2)) throw new NotSupportedException("この色の形式の TIFF には対応していません（白黒か RGB のみ）。");
        if (!tags.TryGetValue(273, out var offsets) || !tags.TryGetValue(279, out var counts)) throw new InvalidDataException("TIFF の画素の場所が読めません。");
        int rowsPerStrip = (int)Math.Min(Tag(278, (uint)height), (uint)height);

        int bytesPerSample = bits / 8;
        int rowBytes = width * spp * bytesPerSample;
        var pixels = new byte[(long)rowBytes * height];
        long written = 0;
        for (int s = 0; s < offsets.Length && written < pixels.Length; s++)
        {
            var src = data.AsSpan((int)offsets[s], (int)counts[s]);
            int rows = Math.Min(rowsPerStrip, height - (s * rowsPerStrip));
            int expect = rows * rowBytes;
            byte[] strip = compression switch
            {
                1 => src.ToArray(),
                5 => Lzw(src, expect),
                8 or 32946 => Inflate(src, expect),
                32773 => PackBits(src, expect),
                _ => throw new NotSupportedException($"この圧縮（{compression}）の TIFF には対応していません。"),
            };
            int len = (int)Math.Min(Math.Min(strip.Length, expect), pixels.Length - written);
            Array.Copy(strip, 0, pixels, written, len);
            written += expect;
        }

        bool bigEndianSamples = !le;
        bool color = photometric == 2 && spp >= 3;
        int outCh = color ? 3 : 1;
        var outData = new float[(long)width * height * outCh];
        for (int y = 0; y < height; y++)
        {
            long row = (long)y * rowBytes;
            for (int x = 0; x < width; x++)
                for (int c = 0; c < outCh; c++)
                {
                    long o = row + (((long)x * spp) + c) * bytesPerSample;
                    float v = bits switch
                    {
                        8 => pixels[o],
                        16 => bigEndianSamples ? (pixels[o] << 8) | pixels[o + 1] : pixels[o] | (pixels[o + 1] << 8),
                        _ => BitConverter.ToSingle(bigEndianSamples ? [pixels[o + 3], pixels[o + 2], pixels[o + 1], pixels[o]] : [pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]]),
                    };
                    outData[(((long)y * width) + x) * outCh + c] = v;
                }
            // 差分の予測（左の画素との差で保存されている）を戻す
            if (predictor == 2 && bits != 32)
            {
                float mod = bits == 8 ? 256 : 65536;
                for (int x = 1; x < width; x++)
                    for (int c = 0; c < outCh; c++)
                    {
                        long i = (((long)y * width) + x) * outCh + c;
                        outData[i] = (outData[i] + outData[i - outCh]) % mod;
                    }
            }
        }
        float min = 0, max = bits == 8 ? 255 : 65535;
        if (bits == 32)
        {
            min = float.MaxValue;
            max = float.MinValue;
            foreach (float v in outData)
            {
                if (!float.IsFinite(v)) continue;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            if (!(max > min)) max = min + 1;
            for (int i = 0; i < outData.Length; i++) if (!float.IsFinite(outData[i])) outData[i] = min;
        }
        if (photometric == 0)
            for (int i = 0; i < outData.Length; i++) outData[i] = max + min - outData[i];
        var r = new Raster(width, height, outCh, outData, min, max);
        return color ? PngDecoder.GrayIfNeutral(r) : r;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> src, int expect)
    {
        using var ms = new MemoryStream(src.ToArray());
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream(expect);
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static byte[] PackBits(ReadOnlySpan<byte> src, int expect)
    {
        var o = new List<byte>(expect);
        int i = 0;
        while (i < src.Length && o.Count < expect)
        {
            sbyte n = (sbyte)src[i++];
            if (n >= 0)
            {
                for (int k = 0; k <= n && i < src.Length; k++) o.Add(src[i++]);
            }
            else if (n != -128)
            {
                byte b = src[i++];
                for (int k = 0; k <= -n; k++) o.Add(b);
            }
        }
        return [.. o];
    }

    /// <summary>TIFF の LZW（上位ビットから読み、表が埋まる 1 つ手前で符号の長さを増やす）</summary>
    private static byte[] Lzw(ReadOnlySpan<byte> src, int expect)
    {
        var output = new List<byte>(expect);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++) table.Add([(byte)i]);
            table.Add([]); // 256: clear
            table.Add([]); // 257: end
        }
        Reset();
        int bitPos = 0, codeLen = 9;
        byte[]? prev = null;
        int totalBits = src.Length * 8;
        while (bitPos + codeLen <= totalBits)
        {
            int code = 0;
            for (int k = 0; k < codeLen; k++)
            {
                int b = (src[(bitPos + k) >> 3] >> (7 - ((bitPos + k) & 7))) & 1;
                code = (code << 1) | b;
            }
            bitPos += codeLen;
            if (code == 257) break;
            if (code == 256)
            {
                Reset();
                codeLen = 9;
                prev = null;
                continue;
            }
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (code == table.Count && prev is not null) entry = [.. prev, prev[0]];
            else throw new InvalidDataException("TIFF の LZW の符号が正しくありません。");
            output.AddRange(entry);
            if (prev is not null) table.Add([.. prev, entry[0]]);
            prev = entry;
            if (table.Count + 1 >= (1 << codeLen) && codeLen < 12) codeLen++;
        }
        return [.. output];
    }
}
