using QuantScope.Core.Imaging;

namespace QuantScope.Core.Processing;

/// <summary>k-means の結果。組は暗い順に 0, 1, 2, … と並べ直してある（毎回同じ番号になる）。</summary>
public sealed class KMeansResult
{
    internal KMeansResult(int width, int height, int[] labels, float[][] centers, Raster preview)
    {
        Width = width;
        Height = height;
        Labels = labels;
        Centers = centers;
        Preview = preview;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>画素ごとの組の番号</summary>
    public int[] Labels { get; }

    /// <summary>組の中心（白黒は 1 つ、カラーは R,G,B）</summary>
    public float[][] Centers { get; }

    /// <summary>各画素を組の中心の色で塗った画像</summary>
    public Raster Preview { get; }

    public int K => Centers.Length;

    public Mask MaskOf(int cluster)
    {
        var m = new Mask(Width, Height);
        for (int i = 0; i < Labels.Length; i++) m.Bits[i] = Labels[i] == cluster;
        return m;
    }
}

/// <summary>
/// k-means 法: 画素を色（白黒は明るさ）の近さで k 個の組に分ける。
/// 初めの中心は k-means++ で選び、seed が同じなら毎回同じ結果になる。大きな画像は一部の画素で中心を求めてから全体を分ける。
/// </summary>
public static class KMeans
{
    public static KMeansResult Cluster(Raster image, int k, int seed = 1, int maxIterations = 50, int sampleSize = 60_000)
    {
        ArgumentNullException.ThrowIfNull(image);
        k = Math.Clamp(k, 2, 8);
        int ch = image.Channels, n = image.PixelCount;
        var data = image.Data;
        float scale = 1f / image.NominalRange, min = image.NominalMin;
        var rng = new Random(seed);

        // 中心を求めるための画素（多すぎるときは等間隔に間引く）
        int step = Math.Max(1, n / sampleSize);
        var sample = new List<int>();
        for (int i = 0; i < n; i += step) sample.Add(i);

        float Dist(int pixel, float[] c)
        {
            float d = 0;
            for (int j = 0; j < ch; j++)
            {
                float v = ((data[(pixel * ch) + j] - min) * scale) - c[j];
                d += v * v;
            }
            return d;
        }

        float[] Feature(int pixel)
        {
            var f = new float[ch];
            for (int j = 0; j < ch; j++) f[j] = (data[(pixel * ch) + j] - min) * scale;
            return f;
        }

        // k-means++: 既にある中心から遠い画素ほど選ばれやすくする
        var centers = new List<float[]> { Feature(sample[rng.Next(sample.Count)]) };
        var dmin = new double[sample.Count];
        while (centers.Count < k)
        {
            double sum = 0;
            for (int s = 0; s < sample.Count; s++)
            {
                dmin[s] = centers.Min(c => Dist(sample[s], c));
                sum += dmin[s];
            }
            if (sum <= 0)
            {
                centers.Add(Feature(sample[rng.Next(sample.Count)]));
                continue;
            }
            double r = rng.NextDouble() * sum;
            int pick = 0;
            for (double acc = 0; pick < sample.Count - 1; pick++)
            {
                acc += dmin[pick];
                if (acc >= r) break;
            }
            centers.Add(Feature(sample[pick]));
        }

        var assign = new int[sample.Count];
        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;
            for (int s = 0; s < sample.Count; s++)
            {
                int best = Nearest(sample[s], centers, Dist);
                if (best != assign[s])
                {
                    assign[s] = best;
                    changed = true;
                }
            }
            var sums = new double[k, ch];
            var counts = new int[k];
            for (int s = 0; s < sample.Count; s++)
            {
                counts[assign[s]]++;
                for (int j = 0; j < ch; j++) sums[assign[s], j] += (data[(sample[s] * ch) + j] - min) * scale;
            }
            for (int c = 0; c < k; c++)
                if (counts[c] > 0)
                    for (int j = 0; j < ch; j++) centers[c][j] = (float)(sums[c, j] / counts[c]);
            if (!changed && iter > 0) break;
        }

        // 暗い順に並べ直す（明るさ Y で比べる）
        var order = Enumerable.Range(0, k).OrderBy(c => Luma(centers[c])).ToArray();
        var rank = new int[k];
        for (int r = 0; r < k; r++) rank[order[r]] = r;
        var sorted = order.Select(c => centers[c]).ToArray();

        var labels = new int[n];
        Parallel.For(0, n, i => labels[i] = rank[Nearest(i, centers, Dist)]);

        var preview = new float[data.Length];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < ch; j++) preview[(i * ch) + j] = min + (sorted[labels[i]][j] / scale);
        var centersOut = sorted.Select(c => c.Select(v => min + (v / scale)).ToArray()).ToArray();
        return new KMeansResult(image.Width, image.Height, labels, centersOut, image.With(preview));
    }

    private static int Nearest(int pixel, List<float[]> centers, Func<int, float[], float> dist)
    {
        int best = 0;
        float bd = float.MaxValue;
        for (int c = 0; c < centers.Count; c++)
        {
            float d = dist(pixel, centers[c]);
            if (d < bd)
            {
                bd = d;
                best = c;
            }
        }
        return best;
    }

    private static double Luma(float[] c) => c.Length == 3 ? (0.299 * c[0]) + (0.587 * c[1]) + (0.114 * c[2]) : c[0];
}
