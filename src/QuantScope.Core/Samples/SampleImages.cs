using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;

namespace QuantScope.Core.Samples;

/// <summary>見本: 作った画像と、その画像に合うレシピ。実在の患者・試料の画像ではない。</summary>
public sealed record Sample(string Id, string Title, string Description, Raster Image, Calibration Calibration, Recipe Recipe);

/// <summary>
/// 見本の画像を計算で作る（毎回同じ画像になる）。
/// どれも実在の試料ではなく、使い方を試すための人工の画像。
/// </summary>
public static class SampleImages
{
    public static IReadOnlyList<(string Id, string Title)> List { get; } =
    [
        ("nuclei", "蛍光の細胞核（16bit）"),
        ("tissue", "染色した組織（カラー）"),
        ("shapes", "形の見本（大きさが分かっている図形）"),
    ];

    public static Sample Create(string id) => id switch
    {
        "nuclei" => Nuclei(),
        "tissue" => Tissue(),
        "shapes" => Shapes(),
        _ => throw new ArgumentException($"見本「{id}」はありません。", nameof(id)),
    };

    /// <summary>核の中心と半径（テストで使う）</summary>
    internal static IReadOnlyList<(double X, double Y, double Rx, double Ry, double Angle)> NucleiLayout(int seed = 7)
    {
        var rng = new Random(seed);
        var list = new List<(double, double, double, double, double)>();
        int tries = 0;
        while (list.Count < 64 && tries++ < 5000)
        {
            double rx = 8 + (rng.NextDouble() * 5), ry = rx * (0.75 + (rng.NextDouble() * 0.25));
            double x = 30 + (rng.NextDouble() * 708), y = 30 + (rng.NextDouble() * 516), a = rng.NextDouble() * Math.PI;
            // 一部はくっつけて置き、ほかは離して置く
            bool ok = list.All(n => Math.Sqrt(((n.Item1 - x) * (n.Item1 - x)) + ((n.Item2 - y) * (n.Item2 - y))) > n.Item3 + rx + 6);
            if (ok) list.Add((x, y, rx, ry, a));
        }
        // くっついた組を 6 つ足す（ウォーターシェッドの練習用）。相手の核とだけ重なり、ほかとは離れる位置に置く
        var singles = list.ToList();
        int added = 0;
        foreach (var n in singles)
        {
            if (added >= 6) break;
            // 少し（3 px）だけ重なる、丸い相手の核
            double pr = n.Item3 * 0.9;
            double px = n.Item1 + n.Item3 + pr - 3, py = n.Item2;
            if (px > 738) continue;
            bool clear = list.All(o => o.Equals(n) || Math.Sqrt(((o.Item1 - px) * (o.Item1 - px)) + ((o.Item2 - py) * (o.Item2 - py))) > o.Item3 + pr + 6);
            if (!clear) continue;
            list.Add((px, py, pr, pr * 0.95, Math.PI / 2));
            added++;
        }
        return list;
    }

    private static Sample Nuclei()
    {
        const int w = 768, h = 576;
        var d = new float[w * h];
        var rng = new Random(11);
        var nuclei = NucleiLayout();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // 左上が明るい照明のむら + 暗い背景
                double bg = 180 + (220 * Math.Exp(-((((x - 120) * (x - 120)) + ((y - 90) * (y - 90))) / (2.0 * 420 * 420))));
                d[(y * w) + x] = (float)bg;
            }
        foreach (var (cx, cy, rx, ry, a) in nuclei)
        {
            double bright = 1400 + (rng.NextDouble() * 1600);
            double cos = Math.Cos(a), sin = Math.Sin(a);
            int x0 = (int)(cx - rx - 3), x1 = (int)(cx + rx + 3), y0 = (int)(cy - rx - 3), y1 = (int)(cy + rx + 3);
            for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    double u = ((dx * cos) + (dy * sin)) / rx, v = ((-dx * sin) + (dy * cos)) / ry;
                    double r = Math.Sqrt((u * u) + (v * v));
                    // ふちがなめらかに落ちる核。中は少しまだら
                    double edge = 1 / (1 + Math.Exp((r - 1) * 14));
                    double tex = 0.85 + (0.15 * Math.Sin((x * 0.7) + (y * 0.45) + (cx * 0.1)));
                    d[(y * w) + x] += (float)(bright * edge * tex);
                }
        }
        for (int i = 0; i < d.Length; i++)
        {
            double noise = Math.Sqrt(Math.Max(d[i], 1)) * 1.2 * Gaussian(rng);
            d[i] = (float)Math.Clamp(Math.Round(d[i] + noise), 0, 4095);
        }
        var image = new Raster(w, h, 1, d, 0, 65535);
        var recipe = new Recipe
        {
            Name = "蛍光の核を数える",
            Steps =
            [
                Make("background", image, ("sigma", 60), ("mode", 0)),
                Make("gaussian", image, ("sigma", 1.5)),
                Make("threshold", image, ("method", 1), ("bright", 0)),
                Make("fillHoles", image),
                Make("watershed", image, ("tolerance", 1.0)),
                Make("sizeFilter", image, ("min", 40), ("max", 0)),
            ],
            Measure = new MeasureSettings { ExcludeEdges = true },
        };
        return new Sample("nuclei", "蛍光の細胞核（16bit）",
            "暗い背景に明るい核が並んだ、蛍光顕微鏡ふうの人工の画像です（12bit のカメラを想定した 16bit の白黒、左上が明るい照明のむらとノイズ入り）。くっついた核も混ぜてあります。1 px = 0.5 µm。",
            image, new Calibration(0.5, "µm"), recipe);
    }

    private static Sample Tissue()
    {
        const int w = 800, h = 560;
        var d = new float[w * h * 3];
        var rng = new Random(23);
        // 間質（ピンク）
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double fiber = 0.5 + (0.5 * Math.Sin((x * 0.045) + (Math.Sin(y * 0.02) * 3)));
                Set(d, w, x, y, 236 - (14 * fiber), 186 - (30 * fiber), 208 - (16 * fiber));
            }
        // 腺腔（白い空間）
        var lumens = new List<(double X, double Y, double R)>();
        for (int i = 0; i < 7; i++) lumens.Add((80 + (rng.NextDouble() * 640), 70 + (rng.NextDouble() * 420), 32 + (rng.NextDouble() * 40)));
        // 核（青紫）: 腺腔のまわりに多く、ほかにもまばらに
        var nuclei = new List<(double X, double Y, double R)>();
        foreach (var (lx, ly, lr) in lumens)
            for (double t = 0; t < Math.PI * 2; t += 0.32 + (rng.NextDouble() * 0.12))
                nuclei.Add((lx + (Math.Cos(t) * (lr + 9)), ly + (Math.Sin(t) * (lr + 9)), 5 + (rng.NextDouble() * 2.5)));
        for (int i = 0; i < 160; i++) nuclei.Add((rng.NextDouble() * w, rng.NextDouble() * h, 3.5 + (rng.NextDouble() * 2.5)));

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                foreach (var (lx, ly, lr) in lumens)
                {
                    double r = Math.Sqrt(((x - lx) * (x - lx)) + ((y - ly) * (y - ly)));
                    double wob = lr * (1 + (0.12 * Math.Sin(Math.Atan2(y - ly, x - lx) * 5)));
                    if (r < wob) Set(d, w, x, y, 246, 240, 244);
                }
            }
        foreach (var (nx, ny, nr) in nuclei)
            for (int y = (int)(ny - nr - 2); y <= (int)(ny + nr + 2); y++)
                for (int x = (int)(nx - nr - 2); x <= (int)(nx + nr + 2); x++)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    double r = Math.Sqrt(((x + 0.5 - nx) * (x + 0.5 - nx)) + ((y + 0.5 - ny) * (y + 0.5 - ny))) / nr;
                    double a = 1 / (1 + Math.Exp((r - 1) * 10));
                    int j = ((y * w) + x) * 3;
                    d[j] = (float)((d[j] * (1 - a)) + (88 * a));
                    d[j + 1] = (float)((d[j + 1] * (1 - a)) + (58 * a));
                    d[j + 2] = (float)((d[j + 2] * (1 - a)) + (146 * a));
                }
        for (int i = 0; i < d.Length; i++) d[i] = (float)Math.Clamp(Math.Round(d[i] + (3 * Gaussian(rng))), 0, 255);
        var image = new Raster(w, h, 3, d);
        var recipe = new Recipe
        {
            Name = "染色した核の面積の割合",
            Steps =
            [
                Make("gaussian", image, ("sigma", 1)),
                Make("colorThreshold", image, ("hueMin", 220), ("hueMax", 300), ("satMin", 25), ("satMax", 100), ("valMin", 0), ("valMax", 85)),
                Make("morphology", image, ("op", 2), ("radius", 1)),
                Make("sizeFilter", image, ("min", 12), ("max", 0)),
            ],
        };
        return new Sample("tissue", "染色した組織（カラー）",
            "HE 染色ふうの人工の組織の画像です。ピンクの間質、白い腺腔、青紫の核があります。色で核を選び、面積の割合（占有率）を測る練習に使えます。1 px = 0.25 µm。",
            image, new Calibration(0.25, "µm"), recipe);
    }

    /// <summary>形の見本の図形（名前・中心・作り方）。テストで正しい値と比べる。</summary>
    internal static void DrawShapes(Func<double, double, bool>[] shapes, float[] d, int w, int h)
    {
        // 4×4 の小さな点に分けて、図形に入る割合で塗る（なめらかなふち）
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double px = x + ((sx + 0.5) / 4), py = y + ((sy + 0.5) / 4);
                        if (shapes.Any(f => f(px, py))) inside++;
                    }
                d[(y * w) + x] = 20 + (210f * inside / 16);
            }
    }

    private static Sample Shapes()
    {
        const int w = 720, h = 420;
        var d = new float[w * h];
        var shapes = new Func<double, double, bool>[]
        {
            (x, y) => Sq2(x - 110, y - 110) <= 60 * 60,                                  // 円（半径 60）
            (x, y) => Math.Abs(x - 290) <= 55 && Math.Abs(y - 110) <= 55,               // 正方形（一辺 110）
            (x, y) => (Sq((x - 480) / 90) + Sq((y - 110) / 40)) <= 1,                    // 楕円（90 × 40）
            (x, y) => Star(x - 640, y - 110, 55, 24, 5),                                 // 星
            (x, y) => Sq2(x - 110, y - 300) <= 60 * 60 && Sq2(x - 110, y - 300) >= 30 * 30, // 輪（外 60・内 30）
            (x, y) => Math.Abs(x - 300) <= 90 && Math.Abs(y - 300) <= 18,               // 細長い四角（180 × 36）
            (x, y) => Triangle(x - 500, y - 300, 70),                                     // 正三角形
            (x, y) => Sq2(x - 650, y - 300) <= 20 * 20,                                   // 小さな円（半径 20）
        };
        DrawShapes(shapes, d, w, h);
        var image = new Raster(w, h, 1, d);
        var recipe = new Recipe
        {
            Name = "形を測る",
            Steps = [Make("threshold", image, ("method", 1), ("bright", 0))],
        };
        return new Sample("shapes", "形の見本",
            "大きさの分かっている図形です（円の半径 60 px、正方形の一辺 110 px など）。円形度・縦横比・フェレ径・充実度が、形によってどう変わるかを確かめられます。縮尺は 1 px = 1 µm にしてあります。",
            image, new Calibration(1, "µm"), recipe);

        static double Sq(double v) => v * v;
        static double Sq2(double a, double b) => (a * a) + (b * b);
        static bool Triangle(double x, double y, double r)
        {
            // 外接円の半径 r、上向きの正三角形
            for (int k = 0; k < 3; k++)
            {
                double a = (-Math.PI / 2) + (k * 2 * Math.PI / 3), b = a + (2 * Math.PI / 3);
                double ax = r * Math.Cos(a), ay = r * Math.Sin(a), bx = r * Math.Cos(b), by = r * Math.Sin(b);
                if ((((bx - ax) * (y - ay)) - ((by - ay) * (x - ax))) < 0) return false;
            }
            return true;
        }
        static bool Star(double x, double y, double ro, double ri, int n)
        {
            double r = Math.Sqrt(Sq2(x, y));
            double t = Math.Atan2(y, x) + (Math.PI / 2);
            double seg = Math.PI / n;
            double m = ((t % (2 * seg)) + (2 * seg)) % (2 * seg);
            double f = Math.Abs(m - seg) / seg; // 1 = 先端、0 = くぼみ
            return r <= ri + ((ro - ri) * f);
        }
    }

    private static Step Make(string kind, Raster image, params (string Key, double Value)[] values)
    {
        var s = StepCatalog.Create(kind, image);
        foreach (var (k, v) in values) s.Values[k] = v;
        return s;
    }

    private static void Set(float[] d, int w, int x, int y, double r, double g, double b)
    {
        int j = ((y * w) + x) * 3;
        d[j] = (float)r;
        d[j + 1] = (float)g;
        d[j + 2] = (float)b;
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
