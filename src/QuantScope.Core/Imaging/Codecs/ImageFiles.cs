using QuantScope.Core.Dicom;
using QuantScope.Core.Pipeline;

namespace QuantScope.Core.Imaging.Codecs;

/// <summary>
/// ファイルの中身から画像を読む（ブラウザー版で使う）。PNG・TIFF・DICOM はここで読み、16 bit をそのまま保つ。
/// JPEG・BMP・GIF・WebP などは null を返す（ブラウザーの画像の部品で読む）。
/// </summary>
public static class ImageFiles
{
    public static LoadedImage? TryDecode(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (DicomImageLoader.LooksLikeDicom(data) || ext is ".dcm" or ".dicom")
        {
            using var ms = new MemoryStream(data, writable: false);
            return DicomImageLoader.Load(ms);
        }
        if (TiffDecoder.IsTiff(data))
        {
            var r = TiffDecoder.Decode(data);
            return new LoadedImage(r, null, Describe("TIFF", r));
        }
        if (PngDecoder.IsPng(data))
        {
            var r = PngDecoder.Decode(data);
            return r is null ? null : new LoadedImage(r, null, Describe("PNG", r));
        }
        return null;
    }

    /// <summary>ブラウザーで読んだ 8 bit の RGBA を画像にする（R・G・B が同じなら白黒）</summary>
    public static LoadedImage FromRgba(byte[] rgba, int width, int height, string format)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < (long)width * height * 4) throw new InvalidDataException("画素の数が合いません。");
        var d = new float[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            d[i * 3] = rgba[i * 4];
            d[(i * 3) + 1] = rgba[(i * 4) + 1];
            d[(i * 3) + 2] = rgba[(i * 4) + 2];
        }
        var r = PngDecoder.GrayIfNeutral(new Raster(width, height, 3, d));
        return new LoadedImage(r, null, Describe(format, r));
    }

    public static string Describe(string format, Raster r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string kind = r.IsColor ? "カラー" : "白黒";
        string bits = r.NominalMax <= 255 && r.NominalMin == 0 ? "8bit" : r.NominalMax <= 65535 && r.NominalMin == 0 ? "16bit" : "小数";
        return $"{format} {r.Width}×{r.Height} {kind} {bits}";
    }
}
