using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

public enum MorphologyOperation
{
    /// <summary>収縮: 対象のふちを radius だけ削る</summary>
    Erode,

    /// <summary>膨張: 対象のふちを radius だけ太らせる</summary>
    Dilate,

    /// <summary>オープニング（収縮 → 膨張）: 小さな出っ張り・ゴミを消す</summary>
    Open,

    /// <summary>クロージング（膨張 → 収縮）: 小さな穴・切れ目をふさぐ</summary>
    Close,
}

/// <summary>
/// マスクの形を整える処理。円形の構造要素（半径 radius 画素）を、距離変換を使って正確・高速に当てる。
/// </summary>
public static class Morphology
{
    public static Mask Apply(Mask mask, MorphologyOperation op, double radius)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (!(radius > 0)) return mask;
        return op switch
        {
            MorphologyOperation.Erode => Erode(mask, radius),
            MorphologyOperation.Dilate => Dilate(mask, radius),
            MorphologyOperation.Open => Dilate(Erode(mask, radius), radius),
            _ => Erode(Dilate(mask, radius), radius),
        };
    }

    public static Mask Erode(Mask mask, double radius)
    {
        ArgumentNullException.ThrowIfNull(mask);
        // 背景までの距離が半径より大きい画素だけが残る（画像の外は背景）
        var d = DistanceTransform.ToBackground(mask);
        var r = new bool[d.Length];
        for (int i = 0; i < d.Length; i++) r[i] = d[i] > radius;
        return new Mask(mask.Width, mask.Height, r);
    }

    public static Mask Dilate(Mask mask, double radius)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var d = DistanceTransform.ToForeground(mask);
        var r = new bool[d.Length];
        for (int i = 0; i < d.Length; i++) r[i] = d[i] <= radius;
        return new Mask(mask.Width, mask.Height, r);
    }

    /// <summary>穴を埋める: 画像のふちとつながっていない背景を、対象にする</summary>
    public static Mask FillHoles(Mask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.Width, h = mask.Height;
        var outside = new bool[w * h];
        var stack = new Stack<int>();
        void Seed(int x, int y)
        {
            int i = (y * w) + x;
            if (!mask.Bits[i] && !outside[i])
            {
                outside[i] = true;
                stack.Push(i);
            }
        }
        for (int x = 0; x < w; x++)
        {
            Seed(x, 0);
            Seed(x, h - 1);
        }
        for (int y = 0; y < h; y++)
        {
            Seed(0, y);
            Seed(w - 1, y);
        }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % w, y = i / w;
            if (x > 0) Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }
        var r = new bool[w * h];
        for (int i = 0; i < r.Length; i++) r[i] = mask.Bits[i] || !outside[i];
        return new Mask(w, h, r);
    }

    /// <summary>面積（画素数）が minArea より小さい粒、maxArea より大きい粒を消す（maxArea が 0 以下なら上限なし）</summary>
    public static Mask FilterBySize(Mask mask, double minArea, double maxArea = 0)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var labels = Labeling.Label(mask);
        var area = labels.Areas();
        var r = new bool[mask.PixelCount];
        for (int i = 0; i < r.Length; i++)
        {
            int l = labels.Labels[i];
            if (l == 0) continue;
            int a = area[l];
            r[i] = a >= minArea && (maxArea <= 0 || a <= maxArea);
        }
        return new Mask(mask.Width, mask.Height, r);
    }

    /// <summary>画像のふちに触れている粒を消す（途中で切れている粒を数えないため）</summary>
    public static Mask ClearBorder(Mask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var labels = Labeling.Label(mask);
        int w = mask.Width, h = mask.Height;
        var touch = new bool[labels.Count + 1];
        for (int x = 0; x < w; x++)
        {
            touch[labels.Labels[x]] = true;
            touch[labels.Labels[((h - 1) * w) + x]] = true;
        }
        for (int y = 0; y < h; y++)
        {
            touch[labels.Labels[y * w]] = true;
            touch[labels.Labels[(y * w) + w - 1]] = true;
        }
        var r = new bool[mask.PixelCount];
        for (int i = 0; i < r.Length; i++)
        {
            int l = labels.Labels[i];
            r[i] = l != 0 && !touch[l];
        }
        return new Mask(w, h, r);
    }
}
