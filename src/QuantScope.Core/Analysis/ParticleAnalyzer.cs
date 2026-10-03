using QuantScope.Core.Imaging;

namespace QuantScope.Core.Analysis;

/// <summary>1 つの粒の計測値。長さ・面積は縮尺をかけた値（縮尺がなければ px）。</summary>
public sealed record Particle
{
    public int Id { get; init; }
    public double Area { get; init; }
    public double Perimeter { get; init; }

    /// <summary>円形度 4πA / P²（真円で 1、細長い・でこぼこほど小さい）</summary>
    public double Circularity { get; init; }

    /// <summary>重心（px）</summary>
    public double CentroidX { get; init; }
    public double CentroidY { get; init; }

    /// <summary>同じ面積の円の直径</summary>
    public double EquivalentDiameter { get; init; }

    /// <summary>当てはめた楕円の長軸・短軸の長さと、長軸の向き（度、右向きから反時計回り 0〜180）</summary>
    public double MajorAxis { get; init; }
    public double MinorAxis { get; init; }
    public double Angle { get; init; }

    /// <summary>縦横比（長軸 / 短軸）</summary>
    public double AspectRatio { get; init; }

    /// <summary>フェレ径（いちばん長い差し渡し）</summary>
    public double Feret { get; init; }

    /// <summary>充実度（面積 / 凸包の面積）。くぼみが多いほど小さい</summary>
    public double Solidity { get; init; }

    /// <summary>明るさ（計測に使う画像の値）</summary>
    public double MeanIntensity { get; init; }
    public double StdIntensity { get; init; }
    public double MinIntensity { get; init; }
    public double MaxIntensity { get; init; }

    /// <summary>明るさの合計（画素の値の総和）。蛍光の量の比較などに使う</summary>
    public double IntegratedIntensity { get; init; }

    /// <summary>囲む四角（px）</summary>
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
    public int BoundsWidth { get; init; }
    public int BoundsHeight { get; init; }

    /// <summary>画像（または範囲）のふちに触れているか</summary>
    public bool TouchesEdge { get; init; }

    public int PixelCount { get; init; }

    /// <summary>陽性か（判定しないときは null）</summary>
    public bool? Positive { get; init; }
}

/// <summary>陽性の決まり: 粒の平均（明るさ・染色の量）がしきい値以上（Above が false なら未満）なら陽性</summary>
public sealed record PositiveRule(double Threshold, bool Above = true)
{
    public bool IsPositive(double value) => Above ? value >= Threshold : value < Threshold;
}

/// <summary>全体のまとめ</summary>
public sealed record AnalysisSummary
{
    public int Count { get; init; }
    public double TotalArea { get; init; }
    public double MeanArea { get; init; }
    public double MedianArea { get; init; }
    public double StdArea { get; init; }

    /// <summary>調べた範囲の面積（範囲がなければ画像全体）</summary>
    public double AnalyzedArea { get; init; }

    /// <summary>占有率（対象の面積 / 調べた範囲の面積、%）</summary>
    public double AreaFraction { get; init; }

    public double MeanCircularity { get; init; }
    public double MeanIntensity { get; init; }

    /// <summary>単位面積あたりの数（個 / 単位²）</summary>
    public double Density { get; init; }

    public string AreaUnit { get; init; } = "px²";
    public string LengthUnit { get; init; } = "px";

    /// <summary>明るさとして測ったもの（「明るさ」「DAB の量」など）</summary>
    public string IntensityLabel { get; init; } = "明るさ";

    /// <summary>手で除いた粒の数</summary>
    public int ExcludedCount { get; init; }

    /// <summary>陽性の決まり（判定しないときは null）と、陽性の数・割合（%）</summary>
    public PositiveRule? Positive { get; init; }
    public int PositiveCount { get; init; }
    public double PositivePercent { get; init; }
}

public sealed class AnalysisResult
{
    public AnalysisResult(IReadOnlyList<Particle> particles, AnalysisSummary summary, LabelImage labels, Mask? excluded = null)
    {
        Particles = particles;
        Summary = summary;
        Labels = labels;
        Excluded = excluded;
    }

    /// <summary>手で除いた粒の画素（なければ null）</summary>
    public Mask? Excluded { get; }

    public IReadOnlyList<Particle> Particles { get; }
    public AnalysisSummary Summary { get; }

    /// <summary>計測に使った粒の番号の画像（範囲外・大きさで除いた粒は 0）</summary>
    public LabelImage Labels { get; }
}

public sealed record AnalysisOptions
{
    /// <summary>この面積（縮尺をかけた単位）より小さい粒は数えない</summary>
    public double MinArea { get; init; }

    /// <summary>この面積より大きい粒は数えない（0 なら上限なし）</summary>
    public double MaxArea { get; init; }

    /// <summary>ふちに触れた粒を数えない</summary>
    public bool ExcludeEdges { get; init; }

    /// <summary>斜めのつながりも同じ粒とみなす（8 連結）</summary>
    public bool EightConnected { get; init; } = true;

    /// <summary>この点（px）を含む粒は数えない（誤って選ばれたゴミなどを手で除く）</summary>
    public IReadOnlyList<PointD> ExcludePoints { get; init; } = [];

    /// <summary>陽性・陰性に分ける決まり（null なら分けない）</summary>
    public PositiveRule? Positive { get; init; }

    /// <summary>明るさとして測るものの名前（表の見出しに使う）</summary>
    public string IntensityLabel { get; init; } = "明るさ";

    public static AnalysisOptions Default { get; } = new();
}

/// <summary>
/// 粒子解析: マスクの粒ごとに、面積・周囲長・形・明るさを測る。
/// 周囲長は外側の輪郭をなぞったチェーンコードから求める（画素の階段をそのまま数えるより実際の輪郭に近い）。
/// </summary>
public static class ParticleAnalyzer
{
    public static AnalysisResult Analyze(Mask mask, Raster? intensity, Calibration calibration, AnalysisOptions options, Mask? region = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(options);
        int w = mask.Width, h = mask.Height;
        var gray = intensity?.ToGray();
        if (gray is not null && (gray.Width != w || gray.Height != h)) throw new ArgumentException("明るさの画像とマスクの大きさが違います。", nameof(intensity));
        if (region is not null && !region.SameSize(w, h)) throw new ArgumentException("範囲とマスクの大きさが違います。", nameof(region));

        var target = region is null ? mask : mask.And(region);
        var labels = Labeling.Label(target, options.EightConnected);
        int n = labels.Count;

        // 1 回の走査で、粒ごとの和を集める
        var cnt = new int[n + 1];
        var sx = new double[n + 1];
        var sy = new double[n + 1];
        var sxx = new double[n + 1];
        var syy = new double[n + 1];
        var sxy = new double[n + 1];
        var si = new double[n + 1];
        var sii = new double[n + 1];
        var imin = Enumerable.Repeat(double.MaxValue, n + 1).ToArray();
        var imax = Enumerable.Repeat(double.MinValue, n + 1).ToArray();
        var bx0 = Enumerable.Repeat(int.MaxValue, n + 1).ToArray();
        var by0 = Enumerable.Repeat(int.MaxValue, n + 1).ToArray();
        var bx1 = new int[n + 1];
        var by1 = new int[n + 1];
        var edge = new bool[n + 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                int l = labels.Labels[i];
                if (l == 0) continue;
                cnt[l]++;
                double cx = x + 0.5, cy = y + 0.5;
                sx[l] += cx;
                sy[l] += cy;
                sxx[l] += cx * cx;
                syy[l] += cy * cy;
                sxy[l] += cx * cy;
                if (gray is not null)
                {
                    double v = gray.Data[i];
                    si[l] += v;
                    sii[l] += v * v;
                    if (v < imin[l]) imin[l] = v;
                    if (v > imax[l]) imax[l] = v;
                }
                if (x < bx0[l]) bx0[l] = x;
                if (y < by0[l]) by0[l] = y;
                if (x > bx1[l]) bx1[l] = x;
                if (y > by1[l]) by1[l] = y;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) edge[l] = true;
                else if (region is not null && (!region.Bits[i - 1] || !region.Bits[i + 1] || !region.Bits[i - w] || !region.Bits[i + w])) edge[l] = true;
            }

        // 手で除く点が入っている粒
        var excludedLabel = new bool[n + 1];
        foreach (var pt in options.ExcludePoints)
        {
            int px = (int)Math.Floor(pt.X), py = (int)Math.Floor(pt.Y);
            if (px < 0 || py < 0 || px >= w || py >= h) continue;
            excludedLabel[labels.Labels[(py * w) + px]] = true;
        }
        excludedLabel[0] = false;

        double u = calibration.UnitsPerPixel;
        var particles = new List<Particle>();
        int excludedCount = 0;
        var keep = new int[n + 1];
        int nextId = 0;
        for (int l = 1; l <= n; l++)
        {
            int a = cnt[l];
            double area = calibration.Area(a);
            if (area < options.MinArea) continue;
            if (options.MaxArea > 0 && area > options.MaxArea) continue;
            if (options.ExcludeEdges && edge[l]) continue;
            if (excludedLabel[l])
            {
                excludedCount++;
                continue;
            }

            double mx = sx[l] / a, my = sy[l] / a;
            // 2 次のモーメント（画素の広がり 1/12 を足す）から楕円を当てはめる
            double mu20 = (sxx[l] / a) - (mx * mx) + (1.0 / 12);
            double mu02 = (syy[l] / a) - (my * my) + (1.0 / 12);
            double mu11 = (sxy[l] / a) - (mx * my);
            double common = Math.Sqrt((((mu20 - mu02) / 2) * ((mu20 - mu02) / 2)) + (mu11 * mu11));
            double l1 = ((mu20 + mu02) / 2) + common, l2 = Math.Max(((mu20 + mu02) / 2) - common, 1e-12);
            double major = 4 * Math.Sqrt(l1), minor = 4 * Math.Sqrt(l2);
            // 画像の y は下向きなので、角度は符号を反転して「右から反時計回り」にする
            double angle = -0.5 * Math.Atan2(2 * mu11, mu20 - mu02) * 180 / Math.PI;
            if (angle < 0) angle += 180;

            var (perimeter, feret, hullArea) = Shape(labels, l, bx0[l], by0[l], bx1[l], by1[l]);
            double circ = perimeter > 0 ? Math.Min(1, 4 * Math.PI * a / (perimeter * perimeter)) : 0;

            double mean = gray is null ? 0 : si[l] / a;
            double sd = gray is null || a < 2 ? 0 : Math.Sqrt(Math.Max(0, (sii[l] - (si[l] * si[l] / a)) / (a - 1)));

            keep[l] = ++nextId;
            particles.Add(new Particle
            {
                Id = nextId,
                PixelCount = a,
                Area = area,
                Perimeter = perimeter * u,
                Circularity = circ,
                CentroidX = mx,
                CentroidY = my,
                EquivalentDiameter = 2 * Math.Sqrt(a / Math.PI) * u,
                MajorAxis = major * u,
                MinorAxis = minor * u,
                Angle = angle,
                AspectRatio = major / minor,
                Feret = feret * u,
                Solidity = hullArea > 0 ? Math.Min(1, a / hullArea) : 1,
                MeanIntensity = mean,
                StdIntensity = sd,
                MinIntensity = gray is null ? 0 : imin[l],
                MaxIntensity = gray is null ? 0 : imax[l],
                IntegratedIntensity = gray is null ? 0 : si[l],
                BoundsX = bx0[l],
                BoundsY = by0[l],
                BoundsWidth = bx1[l] - bx0[l] + 1,
                BoundsHeight = by1[l] - by0[l] + 1,
                TouchesEdge = edge[l],
                Positive = options.Positive is { } rule && gray is not null ? rule.IsPositive(mean) : null,
            });
        }

        var kept = new int[labels.Labels.Length];
        for (int i = 0; i < kept.Length; i++) kept[i] = keep[labels.Labels[i]];
        var keptLabels = new LabelImage(w, h, kept, particles.Count);
        Mask? excludedMask = null;
        if (excludedCount > 0)
        {
            excludedMask = new Mask(w, h);
            for (int i = 0; i < kept.Length; i++) excludedMask.Bits[i] = excludedLabel[labels.Labels[i]];
        }
        int positives = particles.Count(p => p.Positive == true);

        long analyzedPixels = region?.Count() ?? (long)w * h;
        long objectPixels = particles.Sum(p => (long)p.PixelCount);
        var areas = particles.Select(p => p.Area).OrderBy(v => v).ToArray();
        double meanArea = areas.Length > 0 ? areas.Average() : 0;
        double analyzedArea = calibration.Area(analyzedPixels);
        var summary = new AnalysisSummary
        {
            Count = particles.Count,
            TotalArea = calibration.Area(objectPixels),
            MeanArea = meanArea,
            MedianArea = areas.Length == 0 ? 0 : areas.Length % 2 == 1 ? areas[areas.Length / 2] : (areas[(areas.Length / 2) - 1] + areas[areas.Length / 2]) / 2,
            StdArea = areas.Length > 1 ? Math.Sqrt(areas.Sum(v => (v - meanArea) * (v - meanArea)) / (areas.Length - 1)) : 0,
            AnalyzedArea = analyzedArea,
            AreaFraction = analyzedPixels > 0 ? 100.0 * objectPixels / analyzedPixels : 0,
            MeanCircularity = particles.Count > 0 ? particles.Average(p => p.Circularity) : 0,
            MeanIntensity = objectPixels > 0 && gray is not null ? particles.Sum(p => p.IntegratedIntensity) / objectPixels : 0,
            Density = analyzedArea > 0 ? particles.Count / analyzedArea : 0,
            AreaUnit = calibration.AreaUnit,
            LengthUnit = calibration.LengthUnit,
            IntensityLabel = options.IntensityLabel,
            ExcludedCount = excludedCount,
            Positive = gray is null ? null : options.Positive,
            PositiveCount = positives,
            PositivePercent = particles.Count > 0 && options.Positive is not null ? 100.0 * positives / particles.Count : 0,
        };
        return new AnalysisResult(particles, summary, keptLabels, excludedMask);
    }

    /// <summary>周囲長（px）・フェレ径（px）・凸包の面積（px²）</summary>
    internal static (double Perimeter, double Feret, double HullArea) Shape(LabelImage labels, int l, int x0, int y0, int x1, int y1)
    {
        int w = labels.Width, h = labels.Height;
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && labels.Labels[(y * w) + x] == l;

        double perimeter = TracePerimeter(labels, l, x0, y0);

        // 行ごとの左端・右端の画素の角から凸包を作り、フェレ径と凸包の面積を求める
        var pts = new List<(double X, double Y)>();
        for (int y = y0; y <= y1; y++)
        {
            int left = -1, right = -1;
            for (int x = x0; x <= x1; x++)
                if (In(x, y))
                {
                    if (left < 0) left = x;
                    right = x;
                }
            if (left < 0) continue;
            pts.Add((left, y));
            pts.Add((left, y + 1));
            pts.Add((right + 1, y));
            pts.Add((right + 1, y + 1));
        }
        var hull = ConvexHull(pts);
        double feret = 0;
        for (int i = 0; i < hull.Count; i++)
            for (int j = i + 1; j < hull.Count; j++)
            {
                double dx = hull[i].X - hull[j].X, dy = hull[i].Y - hull[j].Y;
                feret = Math.Max(feret, Math.Sqrt((dx * dx) + (dy * dy)));
            }
        double hullArea = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var p = hull[i];
            var q = hull[(i + 1) % hull.Count];
            hullArea += (p.X * q.Y) - (q.X * p.Y);
        }
        return (perimeter, feret, Math.Abs(hullArea) / 2);
    }

    // 8 方向（0 = 右、反時計回りに 1 = 右上、2 = 上 … 7 = 右下。画像の y は下向き）
    private static readonly int[] Dx = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] Dy = [0, -1, -1, -1, 0, 1, 1, 1];

    /// <summary>
    /// 外側の輪郭をなぞって（Moore 近傍の追跡）、チェーンコードから周囲長を求める。
    /// 縦横の一歩・斜めの一歩・向きの変わり目の数に、Vossepoel と Smeulders の重み（0.980・1.406・−0.091）をかけ、
    /// 画素の中心を通る輪郭が実際のふちより半画素内側にある分（閉じた輪郭で π）を足す。
    /// </summary>
    internal static double TracePerimeter(LabelImage labels, int l, int x0, int y0)
    {
        int w = labels.Width, h = labels.Height;
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && labels.Labels[(y * w) + x] == l;

        // いちばん上の行の、いちばん左の画素から始める
        int sx = -1, sy = -1;
        for (int y = y0; y < h && sx < 0; y++)
            for (int x = x0; x < w; x++)
                if (In(x, y))
                {
                    sx = x;
                    sy = y;
                    break;
                }
        if (sx < 0) return 0;

        int even = 0, odd = 0, corners = 0;
        int cx = sx, cy = sy, dir = 7, prevCode = -1, firstCode = -1;
        int secondX = -1, secondY = -1;
        int limit = 4 * ((w * h) + 1);
        for (int step = 0; step < limit; step++)
        {
            int start = dir % 2 == 0 ? (dir + 7) % 8 : (dir + 6) % 8;
            int found = -1;
            for (int k = 0; k < 8; k++)
            {
                int d = (start + k) % 8;
                if (In(cx + Dx[d], cy + Dy[d]))
                {
                    found = d;
                    break;
                }
            }
            if (found < 0) break; // 1 画素だけの粒
            int nx = cx + Dx[found], ny = cy + Dy[found];
            // 2 歩目に戻ってきて、1 つ前が出発点なら一周した
            if (step > 0 && cx == sx && cy == sy && nx == secondX && ny == secondY) break;
            if (step == 0)
            {
                secondX = nx;
                secondY = ny;
                firstCode = found;
            }
            if (found % 2 == 0) even++;
            else odd++;
            if (prevCode >= 0 && found != prevCode) corners++;
            prevCode = found;
            dir = found;
            cx = nx;
            cy = ny;
        }
        if (even + odd == 0) return Math.PI; // 1 画素: 直径 1 の円とみなす
        if (prevCode != firstCode) corners++; // 輪の継ぎ目
        return (0.980 * even) + (1.406 * odd) - (0.091 * corners) + Math.PI;
    }

    /// <summary>凸包（Andrew の単調連鎖法）</summary>
    internal static List<(double X, double Y)> ConvexHull(List<(double X, double Y)> points)
    {
        var p = points.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        var hull = new List<(double X, double Y)>();
        foreach (var pt in p)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], pt) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pt);
        }
        int lower = hull.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
}
