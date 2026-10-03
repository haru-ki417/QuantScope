using System.Buffers.Binary;
using QuantScope.Core.Imaging.Codecs;
using QuantScope.Core.Pipeline;

namespace QuantScope.Web.State;

/// <summary>ファイルの中身から画像を読む（PNG・TIFF・DICOM は 16 bit のまま、それ以外はブラウザーの部品で）</summary>
public static class ImageLoader
{
    public const long MaxPixels = 60_000_000;

    public static async Task<LoadedImage> LoadAsync(byte[] bytes, string name, string type, BrowserIo io)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(io);
        LoadedImage? img;
        try
        {
            img = ImageFiles.TryDecode(bytes, name);
        }
        catch (NotSupportedException)
        {
            img = null; // 読めない種類の TIFF などは、ブラウザーに任せてみる
        }
        if (img is not null) return Check(img);
        var packed = await io.DecodeImageAsync(bytes, type) ?? throw new InvalidDataException("画像として読めませんでした（対応していない形式か、壊れたファイルです）。");
        int w = (int)BinaryPrimitives.ReadUInt32LittleEndian(packed);
        int h = (int)BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(4));
        if ((long)w * h > MaxPixels) throw new InvalidDataException($"画像が大きすぎます（{w} × {h}）。");
        string format = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return ImageFiles.FromRgba(packed[8..], w, h, format.Length == 0 ? "画像" : format);
    }

    private static LoadedImage Check(LoadedImage img) =>
        (long)img.Raster.Width * img.Raster.Height > MaxPixels
            ? throw new InvalidDataException($"画像が大きすぎます（{img.Raster.Width} × {img.Raster.Height}）。ブラウザー版は 6,000 万画素までです。")
            : img;
}
