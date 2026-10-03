using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

/// <summary>
/// くっついた粒を分ける（距離変換とウォーターシェッド）。
/// 対象の中で「背景からの距離」が山になるところを粒の中心とみなし、山の高いほうから水をためるように広げていく。
/// 2 つの山の水がぶつかる所に 1 画素の切れ目を入れる。
/// 山と、ほかの山との間のくぼみの深さが tolerance（画素）より浅い山は、別の粒とはみなさない（くびれの浅い楕円を分けすぎないため）。
/// </summary>
public static class Watershed
{
    public static Mask Split(Mask mask, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.Width, h = mask.Height, n = w * h;
        var dist = DistanceTransform.ToBackground(mask);
        // 距離の細かい段差でできる小さな山は tolerance でまとめる（ぼかすと浅いくびれが消えるので、ならさない）

        var order = new List<int>();
        for (int i = 0; i < n; i++) if (mask.Bits[i]) order.Add(i);
        var keys = order.Select(i => -dist[i]).ToArray();
        var idx = order.ToArray();
        Array.Sort(keys, idx); // 高い順

        var parent = new int[n];
        var peak = new float[n];
        var state = new byte[n]; // 0 = まだ, 1 = 粒, 2 = 切れ目
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        Span<int> roots = stackalloc int[8];
        foreach (int p in idx)
        {
            int x = p % w, y = p / w;
            int nr = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int q = (yy * w) + xx;
                    if (state[q] != 1) continue;
                    int r = Find(q);
                    bool seen = false;
                    for (int k = 0; k < nr; k++) seen |= roots[k] == r;
                    if (!seen) roots[nr++] = r;
                }

            float level = dist[p];
            if (nr == 0)
            {
                // 新しい山
                state[p] = 1;
                peak[p] = level;
                continue;
            }

            // いちばん高い山へ、浅い山をまとめる
            int main = roots[0];
            for (int k = 1; k < nr; k++) if (peak[roots[k]] > peak[main]) main = roots[k];
            int significant = 0;
            for (int k = 0; k < nr; k++) if (peak[roots[k]] - level >= tolerance) significant++;

            if (significant >= 2)
            {
                // 深い谷で 2 つ以上の山がぶつかる → 切れ目。浅い山だけは高い山にまとめる
                for (int k = 0; k < nr; k++)
                    if (peak[roots[k]] - level < tolerance && roots[k] != main) parent[roots[k]] = main;
                state[p] = 2;
                continue;
            }
            for (int k = 0; k < nr; k++) if (roots[k] != main) parent[roots[k]] = main;
            parent[p] = main;
            state[p] = 1;
        }

        var r2 = new bool[n];
        for (int i = 0; i < n; i++) r2[i] = state[i] == 1;
        return new Mask(w, h, r2);
    }
}
