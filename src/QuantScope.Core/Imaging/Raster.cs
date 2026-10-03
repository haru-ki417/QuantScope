namespace QuantScope.Core.Imaging;

/// <summary>
/// 画像の画素値（白黒は 1 チャンネル、カラーは R・G・B の 3 チャンネル）。
/// 値は元の単位のまま float で持つ（8bit なら 0〜255、16bit なら 0〜65535、CT なら HU）。
/// 処理はすべて新しい Raster を返し、元の Raster は書き換えない。
/// </summary>
public sealed class Raster
{
    public Raster(int width, int height, int channels, float[] data, float nominalMin = 0, float nominalMax = 255)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "画像の大きさが正しくありません。");
        if (channels is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(channels), "チャンネル数は 1 か 3 です。");
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != (long)width * height * channels) throw new ArgumentException("画素の数が大きさと合いません。", nameof(data));
        if (!(nominalMax > nominalMin)) throw new ArgumentException("値の範囲が正しくありません。", nameof(nominalMax));
        Width = width;
        Height = height;
        Channels = channels;
        Data = data;
        NominalMin = nominalMin;
        NominalMax = nominalMax;
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }

    /// <summary>画素値（行ごと、カラーは R,G,B の順に並ぶ）</summary>
    public float[] Data { get; }

    /// <summary>この画像がとりうる値の範囲（8bit なら 0〜255）。反転や正規化の基準に使う。</summary>
    public float NominalMin { get; }
    public float NominalMax { get; }

    public bool IsColor => Channels == 3;
    public int PixelCount => Width * Height;
    public float NominalRange => NominalMax - NominalMin;

    public float Get(int x, int y, int c = 0) => Data[((y * Width) + x) * Channels + c];

    public static Raster CreateGray(int width, int height, float nominalMin = 0, float nominalMax = 255) =>
        new(width, height, 1, new float[width * height], nominalMin, nominalMax);

    public static Raster CreateColor(int width, int height, float nominalMax = 255) =>
        new(width, height, 3, new float[width * height * 3], 0, nominalMax);

    /// <summary>同じ大きさ・範囲で、新しい値の画像を作る</summary>
    public Raster With(float[] data, int? channels = null, float? nominalMin = null, float? nominalMax = null) =>
        new(Width, Height, channels ?? Channels, data, nominalMin ?? NominalMin, nominalMax ?? NominalMax);

    public Raster Clone() => With((float[])Data.Clone());

    /// <summary>白黒にする（カラーは明るさ Y = 0.299R + 0.587G + 0.114B）。白黒ならそのまま返す。</summary>
    public Raster ToGray()
    {
        if (!IsColor) return this;
        var g = new float[PixelCount];
        for (int i = 0; i < g.Length; i++)
        {
            int j = i * 3;
            g[i] = (0.299f * Data[j]) + (0.587f * Data[j + 1]) + (0.114f * Data[j + 2]);
        }
        return new Raster(Width, Height, 1, g, NominalMin, NominalMax);
    }

    /// <summary>実際の最小値・最大値</summary>
    public (float Min, float Max) ValueRange()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (float v in Data)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    public float Clamp(float v) => v < NominalMin ? NominalMin : v > NominalMax ? NominalMax : v;
}
