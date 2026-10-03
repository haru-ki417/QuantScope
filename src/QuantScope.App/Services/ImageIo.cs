using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuantScope.Core.Dicom;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;

namespace QuantScope.App.Services;

/// <summary>画像ファイルの読み書き（Windows の画像の部品を使う。DICOM は Core で読む）</summary>
public static class ImageIo
{
    public const string OpenFilter = "画像 (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif;*.dcm)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif;*.dcm|すべてのファイル (*.*)|*.*";

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".dcm", ".dicom"];

    /// <summary>大きすぎる画像は読まない（メモリが足りなくなるのを防ぐ）</summary>
    public const long MaxPixels = 120_000_000;

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || (Path.GetExtension(path).Length == 0 && LooksLikeDicom(path));

    public static IReadOnlyList<string> FindImages(string folder, bool recursive) =>
        Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsImageFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static LoadedImage Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".dcm" or ".dicom" || LooksLikeDicom(path)) return DicomImageLoader.Load(path);

        BitmapFrame frame;
        try
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("画像が入っていません。");
            frame = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException("画像として読めませんでした（対応していない形式か、壊れたファイルです）。", ex);
        }
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new InvalidDataException($"画像が大きすぎます（{frame.PixelWidth} × {frame.PixelHeight}）。小さくしてから開いてください。");
        var raster = FromBitmap(frame);
        string kind = raster.IsColor ? "カラー" : raster.NominalMax > 255 ? "白黒 16bit" : "白黒 8bit";
        return new LoadedImage(raster, null, $"{ext.TrimStart('.').ToUpperInvariant()} {frame.PixelWidth}×{frame.PixelHeight} {kind}");
    }

    /// <summary>WPF の画像を Raster に。16bit の白黒・カラーは 16bit のまま、それ以外は 8bit にそろえる。</summary>
    public static Raster FromBitmap(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int w = source.PixelWidth, h = source.PixelHeight;
        var f = source.Format;
        if (f == PixelFormats.Gray16)
        {
            var buf = new ushort[w * h];
            source.CopyPixels(buf, w * 2, 0);
            return new Raster(w, h, 1, buf.Select(v => (float)v).ToArray(), 0, 65535);
        }
        if (f == PixelFormats.Rgb48 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64)
        {
            var conv = f == PixelFormats.Rgb48 ? source : new FormatConvertedBitmap(source, PixelFormats.Rgb48, null, 0);
            var buf = new ushort[w * h * 3];
            conv.CopyPixels(buf, w * 6, 0);
            return GrayIfNeutral(w, h, buf.Select(v => (float)v).ToArray(), 65535);
        }
        if (f == PixelFormats.Gray8 || f == PixelFormats.Gray4 || f == PixelFormats.Gray2 || f == PixelFormats.BlackWhite)
        {
            var conv = f == PixelFormats.Gray8 ? source : new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            var buf = new byte[w * h];
            conv.CopyPixels(buf, w, 0);
            return new Raster(w, h, 1, buf.Select(v => (float)v).ToArray());
        }
        var bgra = f == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var rgb = new float[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = px[(i * 4) + 2];
            rgb[(i * 3) + 1] = px[(i * 4) + 1];
            rgb[(i * 3) + 2] = px[i * 4];
        }
        return GrayIfNeutral(w, h, rgb, 255);
    }

    /// <summary>R・G・B がすべて同じ（見た目が白黒）なら、白黒の画像として扱う</summary>
    private static Raster GrayIfNeutral(int w, int h, float[] rgb, float max)
    {
        for (int i = 0; i < w * h; i++)
            if (rgb[i * 3] != rgb[(i * 3) + 1] || rgb[i * 3] != rgb[(i * 3) + 2]) return new Raster(w, h, 3, rgb, 0, max);
        var g = new float[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = rgb[i * 3];
        return new Raster(w, h, 1, g, 0, max);
    }

    public static BitmapSource ToBitmap(int width, int height, byte[] bgra)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(string path, int width, int height, byte[] bgra)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(width, height, bgra)));
        string temp = path + ".tmp";
        using (var fs = File.Create(temp)) encoder.Save(fs);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>AI に送る PNG（長い辺を maxEdge 以下に縮める。付帯情報は入れない）</summary>
    public static byte[] EncodeForAi(int width, int height, byte[] bgra, int maxEdge = 1024)
    {
        BitmapSource bmp = ToBitmap(width, height, bgra);
        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
        if (scale < 1)
        {
            var t = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
            t.Freeze();
            bmp = t;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>拡張子のない DICOM（先頭 128 バイトのあとに "DICM"）</summary>
    private static bool LooksLikeDicom(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 132) return false;
            var b = new byte[132];
            fs.ReadExactly(b);
            return b[128] == 'D' && b[129] == 'I' && b[130] == 'C' && b[131] == 'M';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
