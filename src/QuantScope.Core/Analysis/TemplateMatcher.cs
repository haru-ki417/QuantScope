using QuantScope.Core.Imaging;
using QuantScope.Core.Processing;

namespace QuantScope.Core.Analysis;

/// <summary>見本と似ている場所（左上の座標 px と、似ている度合い −1〜1）</summary>
public sealed record TemplateMatch(int X, int Y, int Width, int Height, double Score);

/// <summary>
/// 似ている場所を探す（正規化相互相関 NCC）。明るさ・コントラストの違いに強い。
/// まず縮小した画像で全体を調べ、候補の近くだけを元の大きさで調べ直す（大きな画像でも速い）。
/// </summary>
public static class TemplateMatcher
{
    public static IReadOnlyList<TemplateMatch> Find(Raster image, Raster template, double minScore = 0.6, int maxResults = 20, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        var g = image.ToGray();
        var t = template.ToGray();
        if (t.Width > g.Width || t.Height > g.Height) throw new ArgumentException("見本が画像より大きいです。", nameof(template));
        if (t.Width < 3 || t.Height < 3) throw new ArgumentException("見本が小さすぎます（3 画素以上にしてください）。", nameof(template));

        // 見本の短い辺が 16 画素くらいになるまで縮める
        int factor = Math.Max(1, Math.Min(t.Width, t.Height) / 16);
        var candidates = new List<(int X, int Y, double S)>();
        if (factor > 1)
        {
            var gs = Geometry.Resize(g, 1.0 / factor);
            var ts = Geometry.Resize(t, 1.0 / factor);
            var coarse = Scores(gs, ts, 0, 0, gs.Width - ts.Width, gs.Height - ts.Height, cancel);
            foreach (var c in Peaks(coarse.Map, coarse.W, coarse.H, ts.Width, ts.Height, minScore - 0.25, maxResults * 4))
                candidates.Add((c.X * factor, c.Y * factor, c.S));
        }

        var results = new List<(int X, int Y, double S)>();
        int maxX = g.Width - t.Width, maxY = g.Height - t.Height;
        if (factor == 1)
        {
            var full = Scores(g, t, 0, 0, maxX, maxY, cancel);
            results.AddRange(Peaks(full.Map, full.W, full.H, t.Width, t.Height, minScore, maxResults));
        }
        else
        {
            foreach (var c in candidates)
            {
                int x0 = Math.Max(0, c.X - factor), y0 = Math.Max(0, c.Y - factor);
                int x1 = Math.Min(maxX, c.X + factor), y1 = Math.Min(maxY, c.Y + factor);
                var local = Scores(g, t, x0, y0, x1, y1, cancel);
                int best = 0;
                for (int i = 1; i < local.Map.Length; i++) if (local.Map[i] > local.Map[best]) best = i;
                double s = local.Map[best];
                if (s >= minScore) results.Add((x0 + (best % local.W), y0 + (best / local.W), s));
            }
        }

        // 重なったものは、似ている度合いの高いほうだけ残す
        var picked = new List<TemplateMatch>();
        foreach (var r in results.OrderByDescending(r => r.S))
        {
            bool overlap = picked.Any(p => Math.Abs(p.X - r.X) < t.Width / 2 && Math.Abs(p.Y - r.Y) < t.Height / 2);
            if (!overlap) picked.Add(new TemplateMatch(r.X, r.Y, t.Width, t.Height, Math.Round(r.S, 4)));
            if (picked.Count >= maxResults) break;
        }
        return picked;
    }

    /// <summary>左上 (x0..x1, y0..y1) の各位置での NCC</summary>
    private static (double[] Map, int W, int H) Scores(Raster g, Raster t, int x0, int y0, int x1, int y1, CancellationToken cancel)
    {
        int tw = t.Width, th = t.Height, n = tw * th, gw = g.Width;
        double tMean = t.Data.Average();
        var tz = new double[n];
        double tNorm = 0;
        for (int i = 0; i < n; i++)
        {
            tz[i] = t.Data[i] - tMean;
            tNorm += tz[i] * tz[i];
        }
        tNorm = Math.Sqrt(tNorm);
        int W = x1 - x0 + 1, H = y1 - y0 + 1;
        var map = new double[W * H];
        if (tNorm < 1e-9) return (map, W, H);

        Parallel.For(0, H, new ParallelOptions { CancellationToken = cancel }, yy =>
        {
            int y = y0 + yy;
            for (int xx = 0; xx < W; xx++)
            {
                int x = x0 + xx;
                double sum = 0, sum2 = 0, cross = 0;
                for (int j = 0; j < th; j++)
                {
                    int row = ((y + j) * gw) + x;
                    for (int i = 0; i < tw; i++)
                    {
                        double v = g.Data[row + i];
                        sum += v;
                        sum2 += v * v;
                        cross += v * tz[(j * tw) + i];
                    }
                }
                double variance = sum2 - (sum * sum / n);
                map[(yy * W) + xx] = variance > 1e-9 ? cross / (Math.Sqrt(variance) * tNorm) : 0;
            }
        });
        return (map, W, H);
    }

    /// <summary>ほかより高い点（見本の大きさの半分の範囲で最大）を、高い順に返す</summary>
    private static IEnumerable<(int X, int Y, double S)> Peaks(double[] map, int w, int h, int tw, int th, double minScore, int max)
    {
        int rx = Math.Max(1, tw / 2), ry = Math.Max(1, th / 2);
        var list = new List<(int X, int Y, double S)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double s = map[(y * w) + x];
                if (s < minScore) continue;
                bool top = true;
                for (int dy = -ry; dy <= ry && top; dy++)
                    for (int dx = -rx; dx <= rx; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h || (dx == 0 && dy == 0)) continue;
                        double o = map[(yy * w) + xx];
                        if (o > s || (o == s && (yy * w) + xx < (y * w) + x))
                        {
                            top = false;
                            break;
                        }
                    }
                if (top) list.Add((x, y, s));
            }
        return list.OrderByDescending(p => p.S).Take(max);
    }
}
