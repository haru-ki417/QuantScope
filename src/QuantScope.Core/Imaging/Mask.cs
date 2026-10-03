namespace QuantScope.Core.Imaging;

/// <summary>2 値の画像（true が「対象」）。二値化のあとの処理と計測に使う。</summary>
public sealed class Mask
{
    public Mask(int width, int height, bool[]? bits = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "大きさが正しくありません。");
        Width = width;
        Height = height;
        Bits = bits ?? new bool[width * height];
        if (Bits.Length != width * height) throw new ArgumentException("画素の数が大きさと合いません。", nameof(bits));
    }

    public int Width { get; }
    public int Height { get; }
    public bool[] Bits { get; }
    public int PixelCount => Width * Height;

    public bool this[int x, int y]
    {
        get => Bits[(y * Width) + x];
        set => Bits[(y * Width) + x] = value;
    }

    public int Count()
    {
        int n = 0;
        foreach (bool b in Bits) if (b) n++;
        return n;
    }

    public Mask Clone() => new(Width, Height, (bool[])Bits.Clone());

    public Mask Invert()
    {
        var r = new bool[Bits.Length];
        for (int i = 0; i < r.Length; i++) r[i] = !Bits[i];
        return new Mask(Width, Height, r);
    }

    public Mask And(Mask other)
    {
        ArgumentNullException.ThrowIfNull(other);
        RequireSameSize(other);
        var r = new bool[Bits.Length];
        for (int i = 0; i < r.Length; i++) r[i] = Bits[i] && other.Bits[i];
        return new Mask(Width, Height, r);
    }

    public bool SameSize(int width, int height) => Width == width && Height == height;

    private void RequireSameSize(Mask other)
    {
        if (!SameSize(other.Width, other.Height)) throw new ArgumentException("大きさの違うマスクは重ねられません。", nameof(other));
    }

    public static Mask Full(int width, int height)
    {
        var m = new Mask(width, height);
        Array.Fill(m.Bits, true);
        return m;
    }
}
