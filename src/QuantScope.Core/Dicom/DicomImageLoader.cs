using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Render;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;

namespace QuantScope.Core.Dicom;

/// <summary>
/// DICOM の画像を 1 枚読む（複数フレームのときは最初のフレーム）。
/// CT などは Rescale Slope / Intercept をかけた値（HU など）にし、画素間隔（Pixel Spacing）があれば縮尺にする。
/// 患者の名前などの情報は、説明の文字列にも含めない。
/// </summary>
public static class DicomImageLoader
{
    public static LoadedImage Load(string path)
    {
        DicomFile file;
        try
        {
            file = DicomFile.Open(path, FileReadOption.ReadAll);
        }
        catch (DicomFileException ex)
        {
            throw new InvalidDataException("DICOM のファイルとして読めませんでした。", ex);
        }
        return FromDataset(file.Dataset);
    }

    /// <summary>メモリの中の DICOM を読む（ブラウザー版など、ファイルの場所がないとき）</summary>
    public static LoadedImage Load(Stream stream)
    {
        DicomFile file;
        try
        {
            file = DicomFile.Open(stream, FileReadOption.ReadAll);
        }
        catch (DicomFileException ex)
        {
            throw new InvalidDataException("DICOM のファイルとして読めませんでした。", ex);
        }
        return FromDataset(file.Dataset);
    }

    public static bool LooksLikeDicom(ReadOnlySpan<byte> d) => d.Length >= 132 && d[128] == 'D' && d[129] == 'I' && d[130] == 'C' && d[131] == 'M';

    public static LoadedImage FromDataset(DicomDataset ds)
    {
        ArgumentNullException.ThrowIfNull(ds);
        if (!ds.Contains(DicomTag.PixelData)) throw new InvalidDataException("画像の入っていない DICOM です。");
        DicomPixelData pixelData;
        IPixelData pixels;
        try
        {
            pixelData = DicomPixelData.Create(ds);
            pixels = PixelDataFactory.Create(pixelData, 0);
        }
        catch (Exception ex) when (ex is DicomImagingException or DicomDataException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException("この DICOM の画像は展開できませんでした（対応していない圧縮の形式かもしれません）。", ex);
        }

        int w = pixels.Width, h = pixels.Height;
        string photometric = ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2").Trim();
        Raster raster;
        if (pixels is ColorPixelData24 color)
        {
            var d = new float[w * h * 3];
            byte[] src = color.Data;
            // fo-dicom は 24bit の色を R,G,B の順で返す
            for (int i = 0; i < d.Length; i++) d[i] = src[i];
            raster = new Raster(w, h, 3, d);
        }
        else
        {
            double slope = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
            double intercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
            if (slope == 0) slope = 1;
            var d = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    d[(y * w) + x] = (float)((pixels.GetPixel(x, y) * slope) + intercept);
            int bits = pixelData.BitsStored;
            bool signed = pixelData.PixelRepresentation == PixelRepresentation.Signed;
            double rawMin = signed ? -(1 << (bits - 1)) : 0, rawMax = signed ? (1 << (bits - 1)) - 1 : (1 << bits) - 1;
            double a = (rawMin * slope) + intercept, b = (rawMax * slope) + intercept;
            float lo = (float)Math.Min(a, b), hi = (float)Math.Max(a, b);
            if (photometric == "MONOCHROME1")
            {
                // 値が大きいほど暗い形式 → 反転して、明るいほど大きい値にそろえる
                for (int i = 0; i < d.Length; i++) d[i] = lo + hi - d[i];
            }
            if (bits == 8 && lo == 0 && hi == 255) raster = new Raster(w, h, 1, d);
            else raster = new Raster(w, h, 1, d, lo, hi > lo ? hi : lo + 1);
        }

        Calibration? cal = null;
        if (!ds.TryGetValues<double>(DicomTag.PixelSpacing, out var spacing) || spacing.Length < 2)
            spacing = ds.TryGetValues<double>(DicomTag.ImagerPixelSpacing, out var ips) ? ips : [];
        if (spacing.Length >= 2 && spacing[0] > 0 && Math.Abs(spacing[0] - spacing[1]) / spacing[0] < 0.01)
            cal = new Calibration(spacing[1], "mm");

        string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "");
        string desc = string.Create(CultureInfo.InvariantCulture, $"DICOM {modality} {w}×{h}{(raster.IsColor ? " カラー" : $" {pixelData.BitsStored}bit")}");
        return new LoadedImage(raster, cal, desc.Replace("  ", " ", StringComparison.Ordinal));
    }
}
