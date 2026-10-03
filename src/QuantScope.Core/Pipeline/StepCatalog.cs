using System.Globalization;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Processing;

namespace QuantScope.Core.Pipeline;

public enum StepCategory
{
    Adjust,
    Filter,
    Shape,
    Segment,
    Refine,
}

public enum ParamKind
{
    Number,
    Choice,
    Toggle,
}

/// <summary>値の範囲の決め方</summary>
public enum ValueScale
{
    /// <summary>そのままの値</summary>
    Absolute,

    /// <summary>画像の値（最小〜最大の間の位置。既定値・範囲は 0〜1 の割合で書く）</summary>
    ImageValue,

    /// <summary>画像の値の幅（既定値・範囲は、値の範囲に対する割合で書く）</summary>
    ImageSpan,

    /// <summary>画像の幅・高さ（px）</summary>
    ImagePixels,
}

/// <summary>手順の値 1 つの定義（画面の入力欄もここから作る）</summary>
public sealed record ParamDef(string Key, string Label, ParamKind Kind, double Default, double Min = 0, double Max = 1, double Step = 1)
{
    public string? Unit { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    public ValueScale Scale { get; init; } = ValueScale.Absolute;
    public string? Help { get; init; }

    /// <summary>ほかの値（選択肢）がこの番号のときだけ使う値（そうでないときは画面に出さない）</summary>
    public (string Key, int Value)? ShowWhen { get; init; }

    /// <summary>この手順の値で、いま使われる値か</summary>
    public bool IsUsed(Step step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return ShowWhen is not { } w || step.GetInt(w.Key) == w.Value;
    }

    /// <summary>この画像での範囲と既定値（ImageValue などは画像の値に直す）</summary>
    public (double Min, double Max, double Default) Resolve(Raster? image)
    {
        // 8bit は 0〜255、16bit・CT などは実際に使っている範囲を基準にする（12bit のカメラなどで 0〜65535 だと使いにくいため）
        double lo = 0, range = 255;
        if (image is not null && !(image.NominalMin == 0 && image.NominalMax == 255))
        {
            var (min, max) = image.ToGray().ValueRange();
            lo = min;
            range = Math.Max(max - min, 1);
        }
        return Scale switch
        {
            ValueScale.ImageValue => (lo + (Min * range), lo + (Max * range), lo + (Default * range)),
            ValueScale.ImageSpan => (Min * range, Max * range, Default * range),
            ValueScale.ImagePixels => (Min, Math.Max(image?.Width ?? 1, image?.Height ?? 1), Default),
            _ => (Min, Max, Default),
        };
    }
}

/// <summary>手順の実行中の知らせを受け取る</summary>
public sealed class StepContext
{
    public StepContext(CancellationToken cancel) => Cancel = cancel;

    public CancellationToken Cancel { get; }
    public string? Info { get; set; }
    public string? Warning { get; set; }
}

/// <summary>この手順は今の状態では使えない（理由は画面に出す）</summary>
public sealed class StepNotApplicableException(string message) : Exception(message);

/// <summary>手順の種類 1 つの定義: 名前・説明（学習用の解説を含む）・値・処理の中身</summary>
public sealed class StepDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required StepCategory Category { get; init; }

    /// <summary>一行の説明</summary>
    public required string Summary { get; init; }

    /// <summary>しくみ（式や考え方）</summary>
    public required string HowItWorks { get; init; }

    /// <summary>使いどころ・注意</summary>
    public string? Tip { get; init; }

    public IReadOnlyList<ParamDef> Parameters { get; init; } = [];
    public required Func<PipelineState, Step, StepContext, PipelineState> Apply { get; init; }

    /// <summary>二値化のあと（マスクがあるとき）に使う手順</summary>
    public bool NeedsMask { get; init; }
}

/// <summary>使える手順の一覧</summary>
public static class StepCatalog
{
    private static readonly Dictionary<string, StepDefinition> ById;

    static StepCatalog()
    {
        All = Build();
        ById = All.ToDictionary(d => d.Id, StringComparer.Ordinal);
    }

    public static IReadOnlyList<StepDefinition> All { get; }

    public static StepDefinition Get(string id) =>
        ById.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"「{id}」という手順はありません。");

    public static bool Exists(string id) => ById.ContainsKey(id);

    public static string CategoryTitle(StepCategory c) => c switch
    {
        StepCategory.Adjust => "明るさ・色",
        StepCategory.Filter => "フィルター",
        StepCategory.Shape => "形・範囲",
        StepCategory.Segment => "二値化（対象を選ぶ）",
        _ => "マスクを整える",
    };

    /// <summary>この画像に合わせた既定値で手順を作る</summary>
    public static Step Create(string id, Raster? image)
    {
        var def = Get(id);
        return new Step(id, def.Parameters.ToDictionary(p => p.Key, p => p.Resolve(image).Default));
    }

    private static string F(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static Mask RequireMask(PipelineState s) =>
        s.Mask ?? throw new StepNotApplicableException("マスクがありません。先に「二値化」などで対象を選んでください。");

    private static IReadOnlyList<StepDefinition> Build() =>
    [
        // ------------------------------------------------------------ 明るさ・色
        new()
        {
            Id = "gray", Title = "白黒にする", Category = StepCategory.Adjust,
            Summary = "カラーを明るさだけの白黒にします。",
            HowItWorks = "人の目の感じ方に合わせて、明るさ Y = 0.299R + 0.587G + 0.114B を求めます（緑を重く、青を軽く数える）。R・G・B の単純な平均より、見た目の明るさに近くなります。",
            Apply = (s, _, _) => s with { Image = s.Image.ToGray() },
        },
        new()
        {
            Id = "channel", Title = "色を 1 つ取り出す", Category = StepCategory.Adjust,
            Summary = "R・G・B のうち 1 つだけを白黒の画像にします。",
            HowItWorks = "カラーの画像は、赤・緑・青の 3 枚の白黒の画像の重ね合わせです。そのうち 1 枚だけを取り出します。",
            Tip = "蛍光の多重染色で、色ごとに分けて測るときに使います。",
            Parameters = [new("channel", "色", ParamKind.Choice, 0) { Choices = ["赤 (R)", "緑 (G)", "青 (B)"] }],
            Apply = (s, p, c) =>
            {
                if (!s.Image.IsColor) c.Warning = "白黒の画像なので、そのままにしました。";
                return s with { Image = Adjust.ExtractChannel(s.Image, (ColorChannel)p.GetInt("channel")) };
            },
        },
        new()
        {
            Id = "stain", Title = "染色を分ける", Category = StepCategory.Adjust,
            Summary = "明視野の染色画像から、1 つの染料の量だけを取り出します（H&E・免疫染色の DAB など）。",
            HowItWorks = "光は染料に吸収されるので、吸光度 OD = −log₁₀(I / 255) は染料の量に比例し、重なった染料の OD は足し算になります。染料ごとの色（R・G・B それぞれの吸光度の割合）を並べた行列の逆行列をかけると、画素の OD を染料ごとの量に分けられます（Ruifrok と Johnston のカラーデコンボリューション）。結果は染料の量（OD、0〜3）で、濃く染まった所ほど明るくなります。",
            Tip = "免疫染色（DAB）で陽性の核を数えるときは、「DAB」を取り出して二値化します。核全体を数えて陽性率を出すなら、計測の条件で「測るもの」を「DAB の量」にし、「陽性を判定する」をオンにします。染料の色は標準的な値を使うので、染めむらの強い標本では分かれ方がずれることがあります。",
            Parameters = [new("stain", "取り出す染料", ParamKind.Choice, 3) { Choices = ["ヘマトキシリン（H&E）", "エオシン（H&E）", "ヘマトキシリン（H-DAB）", "DAB（H-DAB）"] }],
            Apply = (s, p, c) =>
            {
                if (!s.Image.IsColor) throw new StepNotApplicableException("染色を分けるには、カラーの画像が必要です（前の手順で白黒にしていないか確かめてください）。");
                var stain = (StainChannel)p.GetInt("stain");
                var amount = ColorDeconvolution.Amount(s.Image, stain);
                double mean = amount.Data.Average(v => (double)v);
                c.Info = string.Create(CultureInfo.InvariantCulture, $"{ColorDeconvolution.Title(stain)}・平均の量 {mean:0.000}");
                return s with { Image = amount };
            },
        },
        new()
        {
            Id = "invert", Title = "反転（ネガポジ）", Category = StepCategory.Adjust,
            Summary = "明るいところを暗く、暗いところを明るくします。",
            HowItWorks = "値の範囲の下端を a、上端を b として、出力 = a + b − 入力 にします。",
            Tip = "明るい背景に暗い対象（明視野の細胞など）を、暗い背景に明るい対象として扱いたいときに使います。",
            Apply = (s, _, _) => s with { Image = Adjust.Invert(s.Image) },
        },
        new()
        {
            Id = "windowLevel", Title = "ウインドウ・レベル", Category = StepCategory.Adjust,
            Summary = "見たい明るさの範囲だけを 0〜255 に広げます。",
            HowItWorks = "レベル L を中心に、幅 W の範囲 [L − W/2, L + W/2] を 0〜255 に引き伸ばし、範囲より暗い値は黒、明るい値は白にします。CT（HU）や 16bit の画像を、見たい組織に合わせて表示するときの方法です。",
            Tip = "幅を狭くするほどコントラストが強くなります。結果は 8bit（0〜255）になります。",
            Parameters =
            [
                new("level", "レベル（中心）", ParamKind.Number, 0.5, 0, 1, 1) { Scale = ValueScale.ImageValue },
                new("width", "ウインドウ（幅）", ParamKind.Number, 1, 0.005, 1, 1) { Scale = ValueScale.ImageSpan },
            ],
            Apply = (s, p, _) => s with { Image = Adjust.WindowLevel(s.Image, p.Get("level"), Math.Max(p.Get("width"), 1e-6)) },
        },
        new()
        {
            Id = "autoContrast", Title = "自動コントラスト", Category = StepCategory.Adjust,
            Summary = "使われている明るさの範囲を、いっぱいに広げます。",
            HowItWorks = "暗い側・明るい側からそれぞれ指定した割合の画素を「飛ばしてよい」とし、残りの範囲を値の範囲いっぱいに線形に引き伸ばします。少数の極端に明るい点に引きずられないための工夫です。",
            Parameters = [new("saturated", "飛ばす割合（両側それぞれ）", ParamKind.Number, 0.35, 0, 5, 0.05) { Unit = "%" }],
            Apply = (s, p, _) => s with { Image = Adjust.AutoContrast(s.Image, p.Get("saturated")) },
        },
        new()
        {
            Id = "brightnessContrast", Title = "明るさ・コントラスト", Category = StepCategory.Adjust,
            Summary = "全体を明るく・暗くし、差を強く・弱くします。",
            HowItWorks = "出力 = (入力 − 中央) × コントラスト + 中央 + 明るさ。コントラストが 1 より大きいと、中央より明るい所はより明るく、暗い所はより暗くなります。",
            Parameters =
            [
                new("brightness", "明るさ", ParamKind.Number, 0, -100, 100, 1) { Unit = "%" },
                new("contrast", "コントラスト", ParamKind.Number, 1, 0.1, 4, 0.05) { Unit = "倍" },
            ],
            Apply = (s, p, _) => s with { Image = Adjust.BrightnessContrast(s.Image, p.Get("brightness") / 100, p.Get("contrast")) },
        },
        new()
        {
            Id = "gamma", Title = "ガンマ補正", Category = StepCategory.Adjust,
            Summary = "中間の明るさを持ち上げたり、沈めたりします。",
            HowItWorks = "値を 0〜1 にしてから γ 乗します（出力 = 入力^γ）。γ < 1 なら暗い部分が明るくなり、γ > 1 なら暗く締まります。黒と白の端は動きません。",
            Parameters = [new("gamma", "γ", ParamKind.Number, 0.7, 0.1, 5, 0.05)],
            Apply = (s, p, _) => s with { Image = Adjust.Gamma(s.Image, p.Get("gamma")) },
        },
        new()
        {
            Id = "equalize", Title = "ヒストグラム平坦化", Category = StepCategory.Adjust,
            Summary = "明るさの分布が平らになるように、明るさを付け直します。",
            HowItWorks = "暗いほうから数えた画素の割合（累積度数）を、そのまま新しい明るさにします。画素がたくさん集まっている明るさの範囲ほど広げられるので、全体のコントラストが上がります。",
            Tip = "見た目は強く変わりますが、明るさの値そのものも変わるので、明るさを測る前提の画像には使わないでください（計測は元の画像の明るさで行います）。",
            Apply = (s, _, _) => s with { Image = Adjust.Equalize(s.Image) },
        },
        new()
        {
            Id = "clahe", Title = "局所コントラスト（CLAHE）", Category = StepCategory.Adjust,
            Summary = "場所ごとにコントラストを上げ、暗い所・明るい所の細部を見やすくします。",
            HowItWorks = "画像を区画に分け、区画ごとにヒストグラム平坦化を行います。ノイズまで強調しすぎないよう、1 つの明るさに集まる画素の数に上限（クリップ）を設け、区画の境目は補間でなめらかにつなぎます（Contrast Limited Adaptive Histogram Equalization）。",
            Parameters =
            [
                new("tiles", "区画の数（縦・横）", ParamKind.Number, 8, 2, 16, 1),
                new("clip", "強さの上限", ParamKind.Number, 2.5, 1, 10, 0.1) { Unit = "倍" },
            ],
            Apply = (s, p, _) => s with { Image = Adjust.Clahe(s.Image, p.GetInt("tiles"), p.Get("clip")) },
        },

        // ------------------------------------------------------------ フィルター
        new()
        {
            Id = "gaussian", Title = "ぼかし（ガウス）", Category = StepCategory.Filter,
            Summary = "細かいノイズをならします。",
            HowItWorks = "近くの画素ほど重く、遠い画素ほど軽く（正規分布の重み）平均します。σ は重みの広がりで、大きいほど強くぼけます。縦・横に分けて計算するので速く、σ が大きいときは箱型の平均を 3 回重ねて近似します。",
            Tip = "二値化の前に軽く（σ = 1〜2）かけると、境目のギザギザや小さなゴミが減ります。",
            Parameters = [new("sigma", "σ（強さ）", ParamKind.Number, 1.5, 0.3, 30, 0.1) { Unit = "px" }],
            Apply = (s, p, _) => s with { Image = Filters.GaussianBlur(s.Image, p.Get("sigma")) },
        },
        new()
        {
            Id = "median", Title = "メディアン", Category = StepCategory.Filter,
            Summary = "ごま塩のような点ノイズを消します。輪郭はぼけにくいです。",
            HowItWorks = "周りの (2r+1)×(2r+1) 個の画素を並べ、真ん中の値（中央値）にします。極端な値は中央値に選ばれにくいので、白や黒の点だけが消えます。",
            Parameters = [new("radius", "半径 r", ParamKind.Number, 1, 1, 5, 1) { Unit = "px" }],
            Apply = (s, p, _) => s with { Image = Filters.Median(s.Image, p.GetInt("radius")) },
        },
        new()
        {
            Id = "sharpen", Title = "鮮鋭化（アンシャープマスク）", Category = StepCategory.Filter,
            Summary = "輪郭をくっきりさせます。",
            HowItWorks = "元の画像からぼかした画像を引くと、輪郭だけが残ります。それを強さの分だけ元に足します（出力 = 元 + 強さ × (元 − ぼかし)）。",
            Tip = "ノイズも強調されるので、強くしすぎないようにします。",
            Parameters =
            [
                new("sigma", "ぼかしの σ", ParamKind.Number, 1.5, 0.3, 10, 0.1) { Unit = "px" },
                new("amount", "強さ", ParamKind.Number, 0.8, 0.1, 4, 0.1),
            ],
            Apply = (s, p, _) => s with { Image = Filters.Sharpen(s.Image, p.Get("sigma"), p.Get("amount")) },
        },
        new()
        {
            Id = "sobel", Title = "輪郭の強さ（Sobel）", Category = StepCategory.Filter,
            Summary = "明るさが急に変わる所（輪郭）を白く浮かび上がらせます。",
            HowItWorks = "横方向・縦方向の明るさの変化（傾き）Gx・Gy を、3×3 の重み（Sobel のフィルター）で求め、その大きさ √(Gx² + Gy²) を明るさにします。結果は白黒です。",
            Apply = (s, _, _) => s with { Image = Filters.Sobel(s.Image) },
        },
        new()
        {
            Id = "background", Title = "背景のむらを補正", Category = StepCategory.Filter,
            Summary = "照明のむら（端が暗いなど）を取り除きます。",
            HowItWorks = "対象より十分大きくぼかした画像を「背景」とみなします。背景が暗い画像（蛍光）は背景を引き、背景が明るい画像（明視野）は背景で割ってから全体の平均の明るさに戻します。",
            Tip = "σ は対象の直径の 2〜3 倍が目安です。小さすぎると対象まで消えてしまいます。",
            Parameters =
            [
                new("sigma", "背景の σ", ParamKind.Number, 40, 5, 300, 1) { Unit = "px" },
                new("mode", "背景", ParamKind.Choice, 0) { Choices = ["暗い（引く）", "明るい（割る）"] },
            ],
            Apply = (s, p, _) => s with { Image = Filters.CorrectBackground(s.Image, p.Get("sigma"), (BackgroundMode)p.GetInt("mode")) },
        },
        new()
        {
            Id = "noise", Title = "ノイズを加える（練習用）", Category = StepCategory.Filter,
            Summary = "わざとノイズを加えて、ノイズを消すフィルターの効き目を比べます。",
            HowItWorks = "ガウスノイズ（正規分布のばらつき）と、ごま塩ノイズ（ところどころが真っ白・真っ黒になる）を加えます。番号（シード）が同じなら、毎回同じノイズになります。",
            Tip = "このあとに「ぼかし」と「メディアン」を入れて、どちらがどのノイズに強いかを比べてみてください。",
            Parameters =
            [
                new("sd", "ガウスノイズの強さ", ParamKind.Number, 5, 0, 30, 0.5) { Unit = "%" },
                new("salt", "ごま塩の割合", ParamKind.Number, 0, 0, 20, 0.5) { Unit = "%" },
                new("seed", "番号（シード）", ParamKind.Number, 1, 1, 999, 1),
            ],
            Apply = (s, p, _) => s with { Image = Filters.AddNoise(s.Image, p.Get("sd") / 100, p.Get("salt") / 100, p.GetInt("seed")) },
        },

        // ------------------------------------------------------------ 形・範囲
        new()
        {
            Id = "crop", Title = "切り抜き", Category = StepCategory.Shape,
            Summary = "四角い範囲だけを残します。",
            HowItWorks = "左上の位置と幅・高さで指定した範囲の画素だけを残します。画像の上で範囲を選んで「選んだ範囲にする」を押すと、値が入ります。",
            Parameters =
            [
                new("x", "左", ParamKind.Number, 0, 0, 1, 1) { Unit = "px", Scale = ValueScale.ImagePixels },
                new("y", "上", ParamKind.Number, 0, 0, 1, 1) { Unit = "px", Scale = ValueScale.ImagePixels },
                new("width", "幅", ParamKind.Number, 256, 1, 1, 1) { Unit = "px", Scale = ValueScale.ImagePixels },
                new("height", "高さ", ParamKind.Number, 256, 1, 1, 1) { Unit = "px", Scale = ValueScale.ImagePixels },
            ],
            Apply = (s, p, _) => ApplyGeometry(s,
                r => Geometry.Crop(r, p.GetInt("x"), p.GetInt("y"), p.GetInt("width"), p.GetInt("height")),
                m => Geometry.Crop(m, p.GetInt("x"), p.GetInt("y"), p.GetInt("width"), p.GetInt("height"))),
        },
        new()
        {
            Id = "autoCrop", Title = "自動で切り抜く", Category = StepCategory.Shape,
            Summary = "何も写っていない周りの余白を切り落とします。",
            HowItWorks = "背景の明るさのしきい値より明るい（または暗い）画素をすべて囲む、いちばん小さな四角を探して切り抜きます。",
            Parameters =
            [
                new("threshold", "背景のしきい値", ParamKind.Number, 0.06, 0, 1, 1) { Scale = ValueScale.ImageValue },
                new("bright", "対象は背景より", ParamKind.Choice, 0) { Choices = ["明るい", "暗い"] },
                new("margin", "余白", ParamKind.Number, 4, 0, 100, 1) { Unit = "px" },
            ],
            Apply = (s, p, c) =>
            {
                var rect = Geometry.FindContentBounds(s.Image, p.Get("threshold"), p.GetInt("bright") == 0, p.GetInt("margin"));
                if (rect is not { } r) throw new StepNotApplicableException("対象が見つかりませんでした（すべて背景とみなされました）。しきい値を変えてください。");
                c.Info = string.Create(CultureInfo.InvariantCulture, $"{r.Width} × {r.Height} px を残しました");
                return ApplyGeometry(s, img => Geometry.Crop(img, r.X, r.Y, r.Width, r.Height), m => Geometry.Crop(m, r.X, r.Y, r.Width, r.Height));
            },
        },
        new()
        {
            Id = "flip", Title = "反転（左右・上下）", Category = StepCategory.Shape,
            Summary = "鏡に映したように裏返します。",
            HowItWorks = "左右の反転は x を (幅 − 1 − x) に、上下の反転は y を (高さ − 1 − y) に移します。",
            Parameters = [new("direction", "向き", ParamKind.Choice, 0) { Choices = ["左右", "上下"] }],
            Apply = (s, p, _) =>
            {
                bool h = p.GetInt("direction") == 0;
                return ApplyGeometry(s, r => Geometry.Flip(r, h), m => Geometry.Flip(m, h));
            },
        },
        new()
        {
            Id = "rotate", Title = "回転", Category = StepCategory.Shape,
            Summary = "90 度ずつ回します。",
            HowItWorks = "画素の並びを入れかえるだけなので、補間による画質の低下はありません。",
            Parameters = [new("rotation", "回し方", ParamKind.Choice, 0) { Choices = ["右に 90°", "180°", "左に 90°"] }],
            Apply = (s, p, _) =>
            {
                var rot = (Rotation)p.GetInt("rotation");
                return ApplyGeometry(s, r => Geometry.Rotate(r, rot), m => Geometry.Rotate(m, rot));
            },
        },
        new()
        {
            Id = "resize", Title = "縮小", Category = StepCategory.Shape,
            Summary = "大きな画像を小さくして、処理を軽くします。",
            HowItWorks = "縮小後の 1 画素に入る元の画素の平均（面積で重みづけ）をとります。縮尺は自動で直すので、面積や長さはそのまま正しく測れます。",
            Tip = "細かい対象は小さくしすぎると見えなくなります。",
            Parameters = [new("factor", "倍率", ParamKind.Number, 0.5, 0.1, 1, 0.05) { Unit = "倍" }],
            Apply = (s, p, _) =>
            {
                double f = Math.Clamp(p.Get("factor"), 0.05, 1);
                if (s.Mask is not null) throw new StepNotApplicableException("縮小は二値化より前に入れてください。");
                return s with { Original = Geometry.Resize(s.Original, f), Image = Geometry.Resize(s.Image, f), Calibration = s.Calibration.Scaled(f) };
            },
        },

        // ------------------------------------------------------------ 二値化
        new()
        {
            Id = "threshold", Title = "二値化（しきい値）", Category = StepCategory.Segment,
            Summary = "しきい値より明るい（暗い）所を「対象」にします。",
            HowItWorks = "大津の方法は、明るさの分布を 2 組に分けたときに、組の間の分散 ω₀ω₁(μ₀ − μ₁)² が最大になる値を選びます。三角法は、分布の山の頂点と裾を結ぶ線から最も離れた所を選び、対象が少ない画像に向きます。反復法は、2 組の平均の中点を、変わらなくなるまで繰り返します。",
            Tip = "対象が背景より暗い（明視野の細胞など）ときは「対象は」を「暗い」にします。",
            Parameters =
            [
                new("method", "決め方", ParamKind.Choice, 1) { Choices = ["手動", "大津の方法", "三角法", "平均", "反復法"] },
                new("value", "しきい値", ParamKind.Number, 0.5, 0, 1, 1) { Scale = ValueScale.ImageValue, ShowWhen = ("method", 0) },
                new("bright", "対象は", ParamKind.Choice, 0) { Choices = ["明るい", "暗い"] },
            ],
            Apply = (s, p, c) =>
            {
                var method = (ThresholdMethod)p.GetInt("method");
                double t = method == ThresholdMethod.Manual ? p.Get("value") : Thresholds.Find(s.Image, method);
                bool bright = p.GetInt("bright") == 0;
                var mask = Thresholds.Apply(s.Image, t, bright);
                c.Info = $"しきい値 {F(t)}（{(bright ? "以上" : "未満")}）・対象 {100.0 * mask.Count() / mask.PixelCount:0.0}%";
                return s with { Mask = mask };
            },
        },
        new()
        {
            Id = "colorThreshold", Title = "色で選ぶ（HSV）", Category = StepCategory.Segment,
            Summary = "決めた色の範囲に入る画素を「対象」にします。",
            HowItWorks = "RGB を、色相 H（色の種類、0〜360°）・彩度 S（あざやかさ）・明度 V（明るさ）に直し、3 つとも範囲に入る画素を選びます。色相は円になっているので、最小 > 最大にすると 0°（赤）をまたぐ範囲になります。",
            Tip = "HE 染色の核（青紫）は色相 220〜300° あたり、DAB の茶色は 10〜50° あたりが目安です。",
            Parameters =
            [
                new("hueMin", "色相の最小", ParamKind.Number, 220, 0, 360, 1) { Unit = "°" },
                new("hueMax", "色相の最大", ParamKind.Number, 300, 0, 360, 1) { Unit = "°" },
                new("satMin", "彩度の最小", ParamKind.Number, 20, 0, 100, 1) { Unit = "%" },
                new("satMax", "彩度の最大", ParamKind.Number, 100, 0, 100, 1) { Unit = "%" },
                new("valMin", "明度の最小", ParamKind.Number, 0, 0, 100, 1) { Unit = "%" },
                new("valMax", "明度の最大", ParamKind.Number, 100, 0, 100, 1) { Unit = "%" },
            ],
            Apply = (s, p, c) =>
            {
                if (!s.Image.IsColor) throw new StepNotApplicableException("色で選ぶには、カラーの画像が必要です（前の手順で白黒にしていないか確かめてください）。");
                var mask = Thresholds.Hsv(s.Image, p.Get("hueMin"), p.Get("hueMax"), p.Get("satMin") / 100, p.Get("satMax") / 100, p.Get("valMin") / 100, p.Get("valMax") / 100);
                c.Info = $"対象 {100.0 * mask.Count() / mask.PixelCount:0.0}%";
                return s with { Mask = mask };
            },
        },
        new()
        {
            Id = "kmeans", Title = "k-means で分ける", Category = StepCategory.Segment,
            Summary = "似た色（明るさ）の画素を k 個の組に分け、そのうち 1 つを「対象」にします。",
            HowItWorks = "① k 個の中心を選ぶ（k-means++: 既にある中心から遠い画素ほど選ばれやすい）② 各画素を最も近い中心の組に入れる ③ 組ごとの平均を新しい中心にする。②③を変わらなくなるまで繰り返します。組は暗い順に 1, 2, … と番号を付けます。",
            Tip = "しきい値が 1 つでは分けにくい、3 種類以上の領域（例: 核・細胞質・背景）があるときに使います。",
            Parameters =
            [
                new("k", "組の数 k", ParamKind.Number, 3, 2, 8, 1),
                new("cluster", "対象にする組（暗い順）", ParamKind.Number, 1, 1, 8, 1),
            ],
            Apply = (s, p, c) =>
            {
                int k = p.GetInt("k");
                var result = KMeans.Cluster(s.Image, k);
                int pick = Math.Clamp(p.GetInt("cluster"), 1, result.K) - 1;
                var mask = result.MaskOf(pick);
                c.Info = $"組 {pick + 1} / {result.K}・対象 {100.0 * mask.Count() / mask.PixelCount:0.0}%";
                return s with { Image = result.Preview, Mask = mask };
            },
        },

        // ------------------------------------------------------------ マスクを整える
        new()
        {
            Id = "morphology", Title = "膨張・収縮", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "対象のふちを削ったり太らせたりして、形を整えます。",
            HowItWorks = "収縮は、背景から半径 r 以内の対象の画素を消します。膨張は、対象から半径 r 以内の背景を対象にします。オープニング（収縮 → 膨張）は小さなゴミや細いつながりを消し、クロージング（膨張 → 収縮）は小さな穴や切れ目をふさぎます。距離変換を使うので、半径が大きくても速く、形は正確な円になります。",
            Parameters =
            [
                new("op", "処理", ParamKind.Choice, 2) { Choices = ["収縮", "膨張", "オープニング", "クロージング"] },
                new("radius", "半径 r", ParamKind.Number, 1.5, 0.5, 20, 0.5) { Unit = "px" },
            ],
            Apply = (s, p, _) => s with { Mask = Morphology.Apply(RequireMask(s), (MorphologyOperation)p.GetInt("op"), p.Get("radius")) },
        },
        new()
        {
            Id = "fillHoles", Title = "穴を埋める", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "対象の中の抜けた穴を埋めます。",
            HowItWorks = "画像のふちから背景をたどっていき、たどり着けなかった背景（＝対象に囲まれた穴）を対象にします。",
            Apply = (s, _, _) => s with { Mask = Morphology.FillHoles(RequireMask(s)) },
        },
        new()
        {
            Id = "watershed", Title = "くっついた粒を分ける", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "接している丸い粒の間に切れ目を入れ、1 つずつ数えられるようにします。",
            HowItWorks = "各画素の「背景までの距離」を高さとみなすと、粒の中心が山になります。高い所から水を満たしていき、別々の山から来た水がぶつかる所に切れ目を入れます（ウォーターシェッド法）。くぼみの深さが「分けにくさ」より浅い山は、同じ粒の一部とみなします。",
            Tip = "楕円の粒が 2 つに割れてしまうときは「分けにくさ」を大きく、くっついたままのときは小さくします。",
            Parameters = [new("tolerance", "分けにくさ", ParamKind.Number, 1.0, 0.2, 10, 0.1) { Unit = "px" }],
            Apply = (s, p, _) => s with { Mask = Watershed.Split(RequireMask(s), p.Get("tolerance")) },
        },
        new()
        {
            Id = "sizeFilter", Title = "大きさで選ぶ", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "小さすぎる粒（ゴミ）や、大きすぎる粒（かたまり）を消します。",
            HowItWorks = "つながった対象を 1 つの粒として数え（ラベリング）、粒ごとの面積（画素数）で残すかどうかを決めます。",
            Parameters =
            [
                new("min", "最小の面積", ParamKind.Number, 20, 0, 5000, 1) { Unit = "px" },
                new("max", "最大の面積（0 は上限なし）", ParamKind.Number, 0, 0, 1_000_000, 10) { Unit = "px" },
            ],
            Apply = (s, p, _) => s with { Mask = Morphology.FilterBySize(RequireMask(s), p.Get("min"), p.Get("max")) },
        },
        new()
        {
            Id = "shapeFilter", Title = "形で選ぶ", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "丸さ・細長さ・くぼみの少なさで粒を選び、ゴミや重なったかたまりを除きます。",
            HowItWorks = "粒ごとに、円形度 4π × 面積 / 周囲長²（真円で 1）、縦横比（当てはめた楕円の長軸 / 短軸）、充実度（面積 / 凸包の面積。くぼみがあるほど小さい）を求め、すべての条件に入る粒だけを残します。",
            Tip = "細胞の核なら、円形度 0.6 以上・充実度 0.85 以上あたりから試すと、重なったかたまりや細長い断片が除けます。除いた粒を確かめるには、この手順のオン・オフを切りかえて比べます。",
            Parameters =
            [
                new("circMin", "円形度の最小", ParamKind.Number, 0, 0, 1, 0.01),
                new("circMax", "円形度の最大", ParamKind.Number, 1, 0, 1, 0.01),
                new("aspectMax", "縦横比の最大（0 は上限なし）", ParamKind.Number, 0, 0, 20, 0.1),
                new("solidityMin", "充実度の最小", ParamKind.Number, 0, 0, 1, 0.01),
            ],
            Apply = (s, p, c) =>
            {
                var (mask, kept, total) = ShapeFilter.Apply(RequireMask(s), p.Get("circMin"), p.Get("circMax"), p.Get("aspectMax"), p.Get("solidityMin"));
                c.Info = $"{total} 個のうち {kept} 個を残しました";
                return s with { Mask = mask };
            },
        },
        new()
        {
            Id = "clearBorder", Title = "ふちの粒を除く", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "画像のふちで切れている粒を消します。",
            HowItWorks = "ふちに触れている粒は、画像の外にはみ出した部分が分からず、面積を正しく測れないため除きます。",
            Apply = (s, _, _) => s with { Mask = Morphology.ClearBorder(RequireMask(s)) },
        },
        new()
        {
            Id = "invertMask", Title = "マスクを反転", Category = StepCategory.Refine, NeedsMask = true,
            Summary = "対象と背景を入れかえます。",
            HowItWorks = "対象だった画素を背景に、背景だった画素を対象にします。",
            Apply = (s, _, _) => s with { Mask = RequireMask(s).Invert() },
        },
    ];

    /// <summary>形を変える処理を、計測用の元画像・今の画像・マスクのすべてにかける</summary>
    private static PipelineState ApplyGeometry(PipelineState s, Func<Raster, Raster> raster, Func<Mask, Mask> mask) =>
        s with
        {
            Original = raster(s.Original),
            Image = raster(s.Image),
            Mask = s.Mask is null ? null : mask(s.Mask),
        };
}
