using System.Globalization;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Processing;
using QuantScope.Core.Rendering;
using QuantScope.Core.Samples;

namespace QuantScope.Web.State;

public enum Tool
{
    Pan,
    Rect,
    Ellipse,
    Polygon,
    Line,
    Exclude,
}

public enum Panel
{
    Steps,
    Results,
    Distribution,
    Line,
    Explain,
}

public enum DistributionMode
{
    Pixels,
    Particles,
}

/// <summary>画像の上の線（px の座標）</summary>
public sealed record MeasureLine(PointD A, PointD B);

/// <summary>表示する値の 1 行</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>
/// ブラウザー版の作業の状態: 開いた画像・レシピ・計算の結果・表示のしかた。
/// 計算はすべてこのブラウザーの中で行い、画像はどこにも送らない。
/// （Windows 版の MainViewModel と同じ考え方を、画面の部品に依存しない形で書いたもの）
/// </summary>
public sealed class Workspace
{
    private readonly UndoHistory _undo = new();
    private readonly List<PointD> _excluded = [];
    private CancellationTokenSource? _pending;
    private int _runVersion;

    public Workspace()
    {
        _undo.Reset(Recipe.ToJson());
    }

    /// <summary>状態が変わった（画面を描き直す）</summary>
    public event Action? Changed;

    /// <summary>表示する画像・重ね合わせが変わった（画像の表示を作り直す）</summary>
    public event Action? ViewChanged;

    public void Notify() => Changed?.Invoke();

    private void NotifyView()
    {
        ViewChanged?.Invoke();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- 画像

    public Raster? Input { get; private set; }
    public LoadedImage? Source { get; private set; }
    public string? FileName { get; private set; }
    public string? ImageDescription { get; private set; }
    public bool HasImage => Input is not null;
    public bool IsSample { get; private set; }

    public Calibration Calibration => Recipe.Calibration ?? Source?.Calibration ?? Calibration.Pixels;

    public void SetImage(LoadedImage image, string name, bool sample = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        _pending?.Cancel();
        Run = null;
        Source = image;
        Input = image.Raster;
        FileName = name;
        ImageDescription = image.Description;
        IsSample = sample;
        Roi = null;
        Line = null;
        Matches = [];
        SelectedParticle = null;
        Analysis = null;
        _excluded.Clear();
        ViewIndex = SelectedStep ?? Recipe.Steps.Count - 1;
        Status = $"{name} を開きました（{image.Description}）。";
        NotifyView();
        ScheduleRun(0);
    }

    public void OpenSample(string id)
    {
        var s = SampleImages.Create(id);
        var recipe = s.Recipe.Clone();
        recipe.Calibration = s.Calibration;
        ApplyRecipe(recipe);
        SelectedStep = null;
        SetImage(new LoadedImage(s.Image, s.Calibration, "見本の画像（人工）"), s.Title, sample: true);
        Status = s.Description;
        Notify();
    }

    // ---------------------------------------------------------------- レシピ

    public Recipe Recipe { get; private set; } = new();
    public IReadOnlyList<StepOutcome?> Outcomes { get; private set; } = [];

    /// <summary>選んでいる手順（null なら「計測」）</summary>
    public int? SelectedStep { get; private set; }

    /// <summary>表示している状態（-1 = 元の画像、0.. = その手順のあと）</summary>
    public int ViewIndex { get; private set; } = -1;

    public bool IsShowingResult => SelectedStep is null && ViewIndex >= Recipe.Steps.Count - 1;
    public bool IsInputSelected => ViewIndex < 0 && Recipe.Steps.Count > 0;

    public string ViewTitle => ViewIndex < 0 || Recipe.Steps.Count == 0
        ? "元の画像"
        : IsShowingResult ? "結果" : $"手順 {Math.Min(ViewIndex, Recipe.Steps.Count - 1) + 1}「{StepCatalog.Get(Recipe.Steps[Math.Min(ViewIndex, Recipe.Steps.Count - 1)].Kind).Title}」のあと";

    public void SelectStep(int? index)
    {
        SelectedStep = index is { } i && i >= 0 && i < Recipe.Steps.Count ? i : null;
        ViewIndex = SelectedStep ?? Recipe.Steps.Count - 1;
        NotifyView();
    }

    public void ShowInput()
    {
        SelectedStep = null;
        ViewIndex = -1;
        NotifyView();
    }

    public void ShowResult()
    {
        SelectedStep = null;
        ViewIndex = Recipe.Steps.Count - 1;
        NotifyView();
    }

    public void AddStep(string kind)
    {
        var step = StepCatalog.Create(kind, Input);
        if (kind == "crop" && Roi is not null && Input is not null) FillCropFromRoi(step, Run?.Final.Image ?? Input);
        int at = SelectedStep is { } s ? s + 1 : Recipe.Steps.Count;
        Recipe.Steps.Insert(at, step);
        SelectedStep = at;
        ViewIndex = at;
        Edited();
    }

    public void RemoveStep(int i)
    {
        if (i < 0 || i >= Recipe.Steps.Count) return;
        Recipe.Steps.RemoveAt(i);
        SelectedStep = Recipe.Steps.Count == 0 ? null : Math.Min(i, Recipe.Steps.Count - 1);
        ViewIndex = SelectedStep ?? Recipe.Steps.Count - 1;
        Edited();
    }

    public void MoveStep(int i, int delta)
    {
        int j = i + delta;
        if (i < 0 || j < 0 || i >= Recipe.Steps.Count || j >= Recipe.Steps.Count) return;
        (Recipe.Steps[i], Recipe.Steps[j]) = (Recipe.Steps[j], Recipe.Steps[i]);
        SelectedStep = j;
        ViewIndex = j;
        Edited();
    }

    public void DuplicateStep(int i)
    {
        if (i < 0 || i >= Recipe.Steps.Count) return;
        Recipe.Steps.Insert(i + 1, Recipe.Steps[i].Clone());
        SelectedStep = i + 1;
        ViewIndex = i + 1;
        Edited();
    }

    public void ToggleStep(int i)
    {
        if (i < 0 || i >= Recipe.Steps.Count) return;
        Recipe.Steps[i].Enabled = !Recipe.Steps[i].Enabled;
        Edited();
    }

    public void ClearSteps()
    {
        Recipe.Steps.Clear();
        SelectedStep = null;
        ViewIndex = -1;
        Edited();
    }

    public void RenameRecipe(string name)
    {
        Recipe.Name = string.IsNullOrWhiteSpace(name) ? "新しいレシピ" : name.Trim();
        _undo.Push(Recipe.ToJson(), "name");
        Notify();
    }

    /// <summary>この画像での値の範囲と既定値</summary>
    public (double Min, double Max, double Default) Range(ParamDef p) => p.Resolve(Input);

    /// <summary>スライダーの刻み</summary>
    public double SliderStep(ParamDef p)
    {
        var (min, max, _) = Range(p);
        return p.Scale is ValueScale.ImageValue or ValueScale.ImageSpan ? NiceStep((max - min) / 500) : p.Step;
    }

    public void SetParam(int stepIndex, ParamDef p, double value)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (stepIndex < 0 || stepIndex >= Recipe.Steps.Count || !double.IsFinite(value)) return;
        var step = Recipe.Steps[stepIndex];
        var (min, max, _) = Range(p);
        double v = Math.Clamp(value, min, Math.Max(max, min));
        if (p.Kind != ParamKind.Number || p.Scale == ValueScale.ImagePixels || (p.Step >= 1 && p.Scale == ValueScale.Absolute)) v = Math.Round(v);
        else
        {
            double st = SliderStep(p);
            v = Math.Round(v / st) * st;
        }
        if (step.Values.TryGetValue(p.Key, out double old) && old.Equals(v)) return;
        step.Values[p.Key] = v;
        if (step.Kind == "threshold")
        {
            // 「手動」に切りかえたら、それまでの自動のしきい値から始める。値を動かしたら「手動」にする
            if (p.Key == "method" && step.GetInt("method") == 0 && HistogramMarker is { } auto) step.Values["value"] = auto;
            if (p.Key == "value" && step.GetInt("method") != 0) step.Values["method"] = 0;
        }
        Edited(coalesce: $"{stepIndex}:{p.Key}");
    }

    public void SetThresholdFromHistogram(double value)
    {
        if (SelectedStep is not { } i || Recipe.Steps[i].Kind != "threshold") return;
        SetParam(i, StepCatalog.Get("threshold").Parameters.First(p => p.Key == "value"), value);
    }

    public void UseRoiForCrop()
    {
        if (SelectedStep is not { } i || Recipe.Steps[i].Kind != "crop" || Roi is null || Input is null) return;
        var before = Run is null ? Input : Run.After(i - 1).Image;
        FillCropFromRoi(Recipe.Steps[i], before);
        Roi = null;
        Edited();
    }

    private void FillCropFromRoi(Step step, Raster img)
    {
        var (x, y, w, h) = Roi!.ClampedPixelRect(img.Width, img.Height);
        if (w < 1 || h < 1) return;
        step.Values["x"] = x;
        step.Values["y"] = y;
        step.Values["width"] = w;
        step.Values["height"] = h;
    }

    // ---------------------------------------------------------------- 計測の条件

    public MeasureSettings Measure => Recipe.Measure;

    public void SetMeasure(MeasureSettings m, string? coalesce = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Classify && !Recipe.Measure.Classify && m.PositiveThreshold == 0 && Analysis is { Particles.Count: > 1 } a)
            m = m with { PositiveThreshold = Math.Round(Statistics.OtsuThreshold(a.Particles.Select(p => p.MeanIntensity)), 4) };
        Recipe.Measure = m;
        Edited(coalesce);
    }

    public void AutoPositiveThreshold()
    {
        if (Analysis is not { Particles.Count: > 1 } a) return;
        double t = Math.Round(Statistics.OtsuThreshold(a.Particles.Select(p => p.MeanIntensity)), 4);
        SetMeasure(Recipe.Measure with { PositiveThreshold = t });
        Status = $"陽性のしきい値を {Fmt(t)} にしました（粒の平均を 2 組に分ける値・大津の方法）。";
    }

    public bool ChannelNeedsColor => Recipe.Measure.Channel != IntensityChannel.Luminance && Input is { IsColor: false };

    public string MeasureSummary
    {
        get
        {
            var m = Recipe.Measure;
            var parts = new List<string>();
            if (m.MinArea > 0 || m.MaxArea > 0) parts.Add($"面積 {Fmt(m.MinArea)}〜{(m.MaxArea > 0 ? Fmt(m.MaxArea) : "")}");
            if (m.ExcludeEdges) parts.Add("ふちを除く");
            parts.Add(IntensityChannels.Title(m.Channel));
            if (m.Classify) parts.Add($"陽性 {(m.PositiveAbove ? "≥" : "<")} {Fmt(m.PositiveThreshold)}");
            if (_excluded.Count > 0) parts.Add($"手で除く {_excluded.Count}");
            return string.Join("・", parts);
        }
    }

    // ---------------------------------------------------------------- 縮尺

    public void SetCalibration(Calibration? c)
    {
        Recipe.Calibration = c;
        Run = null; // 面積も変わるので、すべて計算し直す
        Edited();
        Status = "縮尺: " + Calibration;
    }

    // ---------------------------------------------------------------- 元に戻す

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    public void Undo()
    {
        var json = _undo.Undo();
        if (json is not null) ApplyRecipe(Recipe.FromJson(json), remember: false);
    }

    public void Redo()
    {
        var json = _undo.Redo();
        if (json is not null) ApplyRecipe(Recipe.FromJson(json), remember: false);
    }

    /// <summary>レシピを丸ごと入れかえる（読み込み・見本・元に戻す）</summary>
    public void ApplyRecipe(Recipe recipe, bool remember = true)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        int? keep = SelectedStep;
        Recipe = recipe;
        SelectedStep = !remember && keep is { } k && k < recipe.Steps.Count ? k : null;
        ViewIndex = SelectedStep ?? recipe.Steps.Count - 1;
        if (remember) _undo.Push(Recipe.ToJson(), null);
        Changed?.Invoke();
        ScheduleRun();
    }

    private void Edited(string? coalesce = null)
    {
        _undo.Push(Recipe.ToJson(), coalesce);
        NotifyView();
        ScheduleRun();
    }

    // ---------------------------------------------------------------- 計算

    public PipelineRun? Run { get; private set; }
    public AnalysisResult? Analysis { get; private set; }
    public bool IsBusy { get; private set; }
    public string Status { get; set; } = "画像を開くか、見本から始めてください。";
    public double LastMilliseconds { get; private set; }

    /// <summary>少し待ってから（続けての変更をまとめて）計算し直す</summary>
    public void ScheduleRun(int delayMs = 120)
    {
        if (Input is null) return;
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = RunLaterAsync(delayMs, cts.Token);
    }

    private async Task RunLaterAsync(int delayMs, CancellationToken token)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, token);
            await RunAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RunAsync(CancellationToken token = default)
    {
        if (Input is null) return;
        int version = ++_runVersion;
        IsBusy = true;
        Notify();
        // 「計算中」を先に描かせる（ブラウザーでは計算と描画が同じ流れで動くため）
        await Task.Delay(16, token);
        if (version != _runVersion) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var steps = Recipe.Steps.Select(s => s.Clone()).ToList();
            var run = PipelineRunner.Run(Input, Calibration, steps, Run, token);
            var final = run.Final;
            var region = Roi is not null && RoiFits(Roi, final.Image) ? Roi : null;
            var analysis = PipelineRunner.Analyze(final, Recipe.Measure, region, _excluded.ToList());
            if (version != _runVersion) return;
            Run = run;
            Outcomes = run.Outcomes;
            SetAnalysis(analysis);
            if (ViewIndex >= Recipe.Steps.Count) ViewIndex = Recipe.Steps.Count - 1;
            LastMilliseconds = sw.Elapsed.TotalMilliseconds;
            var warn = run.Outcomes.Select((o, i) => (o, i)).FirstOrDefault(t => t.o.Warning is not null);
            Status = warn.o is not null
                ? $"手順 {warn.i + 1}: {warn.o.Warning}"
                : string.Create(CultureInfo.InvariantCulture, $"計算しました（{LastMilliseconds:0} ms）");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            Status = "計算できませんでした: " + ex.Message;
        }
        finally
        {
            if (version == _runVersion) IsBusy = false;
        }
        NotifyView();
    }

    private static bool RoiFits(Roi roi, Raster image)
    {
        var (x0, y0, x1, y1) = roi.Bounds;
        return x1 > 0 && y1 > 0 && x0 < image.Width && y0 < image.Height;
    }

    private void SetAnalysis(AnalysisResult? a)
    {
        Analysis = a;
        int keep = SelectedParticle?.Id ?? 0;
        SelectedParticle = a?.Particles.FirstOrDefault(p => p.Id == keep);
        SortParticles();
    }

    /// <summary>表示している状態</summary>
    public PipelineState? ViewState => Run is null ? null : ViewIndex < 0 ? Run.States[0] : Run.After(Math.Min(ViewIndex, Recipe.Steps.Count - 1));

    /// <summary>
    /// 表示する画像: 結果で対象が選ばれているときは、処理する前の画像に重ねて見せる（正しく選べたかを確かめやすい）
    /// </summary>
    public Raster? DisplayRaster
    {
        get
        {
            var st = ViewState;
            if (st is null) return Input;
            return IsShowingResult && st.Mask is not null && st.Original.Width == st.Image.Width && st.Original.Height == st.Image.Height ? st.Original : st.Image;
        }
    }

    /// <summary>粒の重ね合わせを使うか（結果を見ているとき）</summary>
    public bool OverlayUsesLabels =>
        ViewIndex >= Recipe.Steps.Count - 1 && Analysis is { } a && ViewState?.Mask is { } m && a.Labels.Width == m.Width && a.Labels.Height == m.Height;

    // ---------------------------------------------------------------- 粒

    public Particle? SelectedParticle { get; private set; }

    public void SelectParticle(Particle? p)
    {
        SelectedParticle = p;
        NotifyView();
    }

    public void SelectParticleAt(PointD p)
    {
        if (!OverlayUsesLabels || Analysis is not { } a) return;
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= a.Labels.Width || y >= a.Labels.Height) return;
        int id = a.Labels.Labels[(y * a.Labels.Width) + x];
        SelectedParticle = id == 0 ? null : a.Particles.FirstOrDefault(q => q.Id == id);
        if (SelectedParticle is not null && Panel is not Panel.Results) Panel = Panel.Results;
        NotifyView();
    }

    public string SortKey { get; private set; } = "id";
    public bool SortDescending { get; private set; }
    public IReadOnlyList<Particle> SortedParticles { get; private set; } = [];

    public void SortBy(string key)
    {
        if (SortKey == key) SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = key != "id";
        }
        SortParticles();
        Notify();
    }

    private void SortParticles()
    {
        var list = Analysis?.Particles ?? [];
        Func<Particle, double> key = SortKey == "id" ? p => p.Id : SortKey == "positive" ? p => p.Positive == true ? 1 : 0 : ParticleFeatures.Get(SortKey).Get;
        SortedParticles = SortDescending ? list.OrderByDescending(key).ToList() : list.OrderBy(key).ToList();
    }

    public IReadOnlyList<DetailRow> SelectedDetails
    {
        get
        {
            if (SelectedParticle is not { } p || Analysis is not { } a) return [];
            var s = a.Summary;
            var rows = ParticleFeatures.All.Select(f => new DetailRow(f.Header(s), Fmt(f.Get(p)))).ToList();
            rows.Add(new DetailRow("重心 (px)", string.Create(CultureInfo.InvariantCulture, $"{p.CentroidX:0.0}, {p.CentroidY:0.0}")));
            if (p.Positive is { } pos) rows.Insert(0, new DetailRow("判定", pos ? "陽性" : "陰性"));
            if (p.TouchesEdge) rows.Add(new DetailRow("ふち", "触れている"));
            return rows;
        }
    }

    // ---------------------------------------------------------------- 手で除く

    public int ExcludedCount => _excluded.Count;

    public void ToggleExcludeAt(PointD p)
    {
        var a = Analysis;
        if (a is null) return;
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= a.Labels.Width || y >= a.Labels.Height) return;
        if (a.Excluded is { } ex && ex[x, y])
        {
            var parts = Labeling.Label(ex);
            int id = parts.Labels[(y * parts.Width) + x];
            _excluded.RemoveAll(q =>
            {
                int qx = (int)Math.Floor(q.X), qy = (int)Math.Floor(q.Y);
                return qx >= 0 && qy >= 0 && qx < parts.Width && qy < parts.Height && parts.Labels[(qy * parts.Width) + qx] == id;
            });
            Status = "粒を戻しました。";
        }
        else if (a.Labels.Labels[(y * a.Labels.Width) + x] != 0)
        {
            _excluded.Add(new PointD(x + 0.5, y + 0.5));
            Status = "粒を除きました（もう一度タップ・クリックすると戻せます）。";
        }
        else
        {
            return;
        }
        Notify();
        ScheduleRun(0);
    }

    public void ExcludeSelected()
    {
        if (SelectedParticle is not { } p || Analysis is not { } a) return;
        var l = a.Labels;
        for (int y = p.BoundsY; y < p.BoundsY + p.BoundsHeight; y++)
            for (int x = p.BoundsX; x < p.BoundsX + p.BoundsWidth; x++)
                if (l.Labels[(y * l.Width) + x] == p.Id)
                {
                    _excluded.Add(new PointD(x + 0.5, y + 0.5));
                    SelectedParticle = null;
                    Status = $"粒 {p.Id} を除きました。";
                    Notify();
                    ScheduleRun(0);
                    return;
                }
    }

    public void RestoreExcluded()
    {
        if (_excluded.Count == 0) return;
        _excluded.Clear();
        Status = "手で除いた粒を、すべて戻しました。";
        Notify();
        ScheduleRun(0);
    }

    // ---------------------------------------------------------------- 範囲・線・似た場所

    public Roi? Roi { get; private set; }
    public MeasureLine? Line { get; private set; }
    public IReadOnlyList<TemplateMatch> Matches { get; private set; } = [];
    public double SimilarityThreshold { get; set; } = 0.7;

    public void SetRoi(Roi? roi)
    {
        Roi = roi;
        Matches = [];
        NotifyView();
        ScheduleRun(0);
    }

    public void SetLine(MeasureLine? line)
    {
        Line = line;
        if (line is not null) Panel = Panel.Line;
        Notify();
    }

    public string RoiText
    {
        get
        {
            if (Roi is null) return "";
            var (x0, y0, x1, y1) = Roi.Bounds;
            string shape = Roi.Shape switch { RoiShape.Rectangle => "四角", RoiShape.Ellipse => "楕円", _ => "多角形" };
            var img = DisplayRaster;
            int px = img is null ? 0 : Roi.ToMask(img.Width, img.Height).Count();
            return string.Create(CultureInfo.InvariantCulture, $"{shape} {x1 - x0:0} × {y1 - y0:0} px　面積 {Fmt(Calibration.Area(px))} {Calibration.AreaUnit}");
        }
    }

    public async Task FindSimilarAsync()
    {
        var img = ViewState?.Image ?? Input;
        if (Roi is null || img is null) return;
        var (x, y, w, h) = Roi.ClampedPixelRect(img.Width, img.Height);
        if (w < 3 || h < 3)
        {
            Status = "範囲が小さすぎます。";
            Notify();
            return;
        }
        IsBusy = true;
        Status = "似た場所を探しています…";
        Notify();
        await Task.Delay(16);
        try
        {
            var template = Geometry.Crop(img, x, y, w, h);
            Matches = TemplateMatcher.Find(img, template, minScore: SimilarityThreshold, maxResults: 200);
            Status = string.Create(CultureInfo.InvariantCulture, $"似た場所が {Matches.Count} か所 見つかりました（似ている度合い {SimilarityThreshold:0.00} 以上）。");
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        NotifyView();
    }

    public LineProfileResult? Profile
    {
        get
        {
            var img = DisplayRaster;
            return Line is null || img is null ? null : LineProfile.Sample(img, Line.A, Line.B, Calibration);
        }
    }

    // ---------------------------------------------------------------- 分布

    public Histogram? PixelHistogram
    {
        get
        {
            var img = DisplayRaster;
            if (img is null) return null;
            var region = Roi is not null && RoiFits(Roi, img) ? Roi.ToMask(img.Width, img.Height) : null;
            return Histogram.Of(img, region);
        }
    }

    /// <summary>しきい値の手順を選んでいるときの、しきい値</summary>
    public double? HistogramMarker
    {
        get
        {
            if (SelectedStep is not { } i || Run is null || Recipe.Steps[i].Kind != "threshold") return null;
            var step = Recipe.Steps[i];
            int method = step.GetInt("method");
            return method == 0 ? step.Get("value") : Thresholds.Find(Run.After(i - 1).Image, (ThresholdMethod)method);
        }
    }

    public DistributionMode DistributionMode { get; set; } = DistributionMode.Pixels;
    public string FeatureKey { get; set; } = ParticleFeatures.Area.Key;

    // ---------------------------------------------------------------- 表示

    public Tool Tool { get; set; } = Tool.Pan;
    public Panel Panel { get; set; } = Panel.Results;
    public ColorMap ColorMap { get; set; } = ColorMap.Gray;
    public bool ShowOverlay { get; set; } = true;
    public bool ShowNumbers { get; set; } = true;
    public bool ColorPerObject { get; set; }
    public double OverlayOpacity { get; set; } = 0.75;
    public bool IsComparing { get; set; }

    public bool ShowClassLegend => Analysis?.Summary.Positive is not null && ShowOverlay && !ColorPerObject && IsShowingResult;

    public void SetDisplay(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change();
        NotifyView();
    }

    /// <summary>マウスの位置の値</summary>
    public string? HoverText(double px, double py)
    {
        var img = DisplayRaster;
        if (img is null) return null;
        int x = (int)Math.Floor(px), y = (int)Math.Floor(py);
        if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return null;
        var c = CultureInfo.InvariantCulture;
        string value = img.IsColor
            ? string.Create(c, $"R {img.Get(x, y, 0):0} G {img.Get(x, y, 1):0} B {img.Get(x, y, 2):0}")
            : "値 " + Fmt(img.Get(x, y));
        var cal = Calibration;
        string pos = cal.IsCalibrated ? $"{Fmt(cal.Length(px))}, {Fmt(cal.Length(py))} {cal.Unit}" : $"{x}, {y} px";
        return $"{pos}　{value}";
    }

    // ---------------------------------------------------------------- 書式

    public static string Fmt(double v)
    {
        var c = CultureInfo.InvariantCulture;
        double a = Math.Abs(v);
        return a >= 1000 ? v.ToString("N0", c) : a >= 10 ? v.ToString("0.0", c) : a >= 0.1 ? v.ToString("0.00", c) : v.ToString("G3", c);
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / p;
        return (m < 2 ? 1 : m < 5 ? 2 : 5) * p;
    }
}
