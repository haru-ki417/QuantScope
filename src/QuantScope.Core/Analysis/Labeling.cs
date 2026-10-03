using QuantScope.Core.Imaging;

namespace QuantScope.Core.Analysis;

/// <summary>つながった対象ごとに番号（1, 2, …）を振った画像。0 は背景。</summary>
public sealed class LabelImage
{
    public LabelImage(int width, int height, int[] labels, int count)
    {
        Width = width;
        Height = height;
        Labels = labels;
        Count = count;
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Labels { get; }

    /// <summary>粒の数</summary>
    public int Count { get; }

    /// <summary>番号ごとの画素数（添字 0 は背景）</summary>
    public int[] Areas()
    {
        var a = new int[Count + 1];
        foreach (int l in Labels) a[l]++;
        return a;
    }
}

/// <summary>
/// 連結成分のラベリング: つながっている対象の画素を 1 つの粒としてまとめる。
/// 2 回の走査と Union-Find（同じ粒の番号をまとめる）で行い、番号は左上から見つかった順に振る。
/// </summary>
public static class Labeling
{
    public static LabelImage Label(Mask mask, bool eightConnected = true)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.Width, h = mask.Height;
        var labels = new int[w * h];
        var parent = new List<int> { 0 };

        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b) return;
            if (a < b) parent[b] = a;
            else parent[a] = b;
        }

        Span<int> nb = stackalloc int[4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                if (!mask.Bits[i]) continue;
                int best = 0;
                // すでに見た近くの画素（左・上、8 連結なら左上・右上も）
                int nn = 0;
                if (x > 0 && labels[i - 1] != 0) nb[nn++] = labels[i - 1];
                if (y > 0)
                {
                    if (labels[i - w] != 0) nb[nn++] = labels[i - w];
                    if (eightConnected)
                    {
                        if (x > 0 && labels[i - w - 1] != 0) nb[nn++] = labels[i - w - 1];
                        if (x < w - 1 && labels[i - w + 1] != 0) nb[nn++] = labels[i - w + 1];
                    }
                }
                for (int k = 0; k < nn; k++) best = best == 0 ? nb[k] : Math.Min(best, nb[k]);
                if (best == 0)
                {
                    best = parent.Count;
                    parent.Add(best);
                }
                else
                {
                    for (int k = 0; k < nn; k++) Union(best, nb[k]);
                }
                labels[i] = best;
            }

        // 番号を 1 から詰め直す（見つかった順）
        var remap = new int[parent.Count];
        int count = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] == 0) continue;
            int root = Find(labels[i]);
            if (remap[root] == 0) remap[root] = ++count;
            labels[i] = remap[root];
        }
        return new LabelImage(w, h, labels, count);
    }
}
