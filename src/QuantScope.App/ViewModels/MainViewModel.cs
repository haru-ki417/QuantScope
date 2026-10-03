using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantScope.App.Services;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Rendering;

namespace QuantScope.App.ViewModels;

public enum Tool
{
    Pan,
    Rectangle,
    Ellipse,
    Polygon,
    Line,
}

public enum RightTab
{
    Results,
    Histogram,
    Profile,
    Explain,
    Ai,
}

/// <summary>
/// 画面全体の状態: 開いた画像・手順（レシピ）・実行の結果・表示のしかた。
/// 手順や値が変わると、少し待ってから（連続した変更をまとめて）裏で計算し直す。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherTimer _debounce;
    private readonly UndoHistory _undo = new();
    private CancellationTokenSource? _runCts;
    private PipelineRun? _run;
    private LoadedImage? _source;
    private bool _suppressRun;
    private int _runVersion;

    public MainViewModel(IDialogs dialogs, SettingsStore settingsStore)
    {
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunAsync();
        };
        Steps.CollectionChanged += (_, _) => Renumber();
        StepGroups = StepCatalog.All.GroupBy(d => d.Category)
            .Select(g => new StepGroup(StepCatalog.CategoryTitle(g.Key), g.ToList()))
            .ToList();
        _undo.Reset(Recipe.ToJson());
    }

    public UserSettings Settings { get; private set; }

    // ---------------------------------------------------------------- 画像

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(IsEmpty))]
    private Raster? _input;

    [ObservableProperty]
    private string? _fileName;

    [ObservableProperty]
    private string? _imageDescription;

    public bool HasImage => Input is not null;
    public bool IsEmpty => Input is null;

    /// <summary>今の縮尺（レシピで決めたもの、なければ画像の情報、それもなければ px）</summary>
    public Calibration Calibration => Recipe.Calibration ?? _source?.Calibration ?? Calibration.Pixels;

    public string CalibrationText => Calibration.ToString();

    // ---------------------------------------------------------------- 手順

    public Recipe Recipe { get; private set; } = new();

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    public IReadOnlyList<StepGroup> StepGroups { get; }

    /// <summary>手順を足すメニューの左の列（明るさ・色、形・範囲）と右の列（フィルター、二値化、マスク）</summary>
    public IReadOnlyList<StepGroup> StepGroupsLeft => [StepGroups[0], StepGroups[2]];

    public IReadOnlyList<StepGroup> StepGroupsRight => [StepGroups[1], StepGroups[3], StepGroups[4]];

    [ObservableProperty]
    private string _recipeName = "新しいレシピ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStep), nameof(SelectedDefinition))]
    private StepViewModel? _selectedStep;

    public bool HasSelectedStep => SelectedStep is not null;
    public StepDefinition? SelectedDefinition => SelectedStep?.Definition;

    /// <summary>表示している状態（-1 = 元の画像、0.. = その手順のあと）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewTitle), nameof(IsViewingFinal), nameof(IsViewingInput))]
    private int _viewIndex = -1;

    public bool IsViewingFinal => ViewIndex == Steps.Count - 1;

    /// <summary>「結果」（元の画像に重ねた表示）を見ているか</summary>
    public bool IsShowingResult => IsViewingFinal && SelectedStep is null;
    public bool IsViewingInput => ViewIndex < 0;

    public string ViewTitle => ViewIndex < 0 || Steps.Count == 0
        ? "元の画像"
        : ViewIndex >= Steps.Count - 1 && SelectedStep is null ? "結果" : $"手順 {Math.Min(ViewIndex, Steps.Count - 1) + 1}「{Steps[Math.Min(ViewIndex, Steps.Count - 1)].Title}」のあと";

    // ---------------------------------------------------------------- 計測

    [ObservableProperty]
    private double _minArea;

    [ObservableProperty]
    private double _maxArea;

    [ObservableProperty]
    private bool _excludeEdges;

    [ObservableProperty]
    private bool _intensityFromOriginal = true;

    partial void OnMinAreaChanged(double value) => MeasureChanged();
    partial void OnMaxAreaChanged(double value) => MeasureChanged();
    partial void OnExcludeEdgesChanged(bool value) => MeasureChanged();
    partial void OnIntensityFromOriginalChanged(bool value) => MeasureChanged();

    private void MeasureChanged()
    {
        if (_suppressRun) return; // レシピを丸ごと入れかえている途中
        Recipe.Measure = new MeasureSettings
        {
            MinArea = Math.Max(0, MinArea),
            MaxArea = Math.Max(0, MaxArea),
            ExcludeEdges = ExcludeEdges,
            IntensityFromOriginal = IntensityFromOriginal,
            EightConnected = true,
        };
        RecipeEdited();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysis))]
    private AnalysisResult? _analysis;

    public bool HasAnalysis => Analysis is not null;

    [ObservableProperty]
    private IReadOnlyList<Particle> _particles = [];

    [ObservableProperty]
    private Particle? _selectedParticle;

    partial void OnSelectedParticleChanged(Particle? value) => RenderOverlay();

    [ObservableProperty]
    private string _summaryCount = "–";

    [ObservableProperty]
    private string _summaryMeanArea = "–";

    [ObservableProperty]
    private string _summaryFraction = "–";

    [ObservableProperty]
    private string _summaryCircularity = "–";

    [ObservableProperty]
    private string _summaryDetail = "";

    [ObservableProperty]
    private string _analysisHint = "手順に「二値化」を入れると、対象を数えて測れます。";

    // ---------------------------------------------------------------- 表示

    [ObservableProperty]
    private ImageSource? _displayImage;

    [ObservableProperty]
    private ImageSource? _overlayImage;

    /// <summary>番号を描く粒（表示している状態が最後のときだけ）</summary>
    [ObservableProperty]
    private IReadOnlyList<Particle>? _displayParticles;

    [ObservableProperty]
    private LabelImage? _displayLabels;

    [ObservableProperty]
    private ColorMap _colorMap = ColorMap.Gray;

    [ObservableProperty]
    private bool _showOverlay = true;

    [ObservableProperty]
    private bool _showNumbers = true;

    [ObservableProperty]
    private bool _colorPerObject;

    [ObservableProperty]
    private double _overlayOpacity = 0.75;

    partial void OnColorMapChanged(ColorMap value) => RenderView();
    partial void OnColorPerObjectChanged(bool value) => RenderOverlay();

    [ObservableProperty]
    private Tool _tool = Tool.Pan;

    [ObservableProperty]
    private RightTab _rightTab = RightTab.Results;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoi), nameof(RoiText))]
    private Roi? _roi;

    public bool HasRoi => Roi is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLine))]
    private MeasureLine? _line;

    public bool HasLine => Line is not null;

    [ObservableProperty]
    private IReadOnlyList<TemplateMatch> _matches = [];

    [ObservableProperty]
    private string? _hoverText;

    [ObservableProperty]
    private string _status = "画像を開くか、見本から始めてください。";

    [ObservableProperty]
    private bool _isBusy;

    // ---------------------------------------------------------------- ヒストグラム・線

    [ObservableProperty]
    private Histogram? _histogram;

    [ObservableProperty]
    private string _histogramText = "";

    /// <summary>ヒストグラムに出す線（しきい値の手順を選んでいるとき）</summary>
    [ObservableProperty]
    private double? _histogramMarker;

    [ObservableProperty]
    private LineProfileResult? _profile;

    [ObservableProperty]
    private string _profileText = "画像の上で「線」の道具を使って線を引くと、線に沿った明るさの変化が出ます。";


    // ---------------------------------------------------------------- 手順の一覧の操作

    [RelayCommand]
    private void AddStep(string kind)
    {
        var step = StepCatalog.Create(kind, CurrentImageForDefaults());
        if (kind == "crop" && Roi is not null && Input is not null) FillCropFromRoi(step);
        // 選んでいる手順のすぐあとに入れる（なければ最後）
        int at = SelectedStep is null ? Steps.Count : Steps.IndexOf(SelectedStep) + 1;
        var vm = Wrap(step);
        Steps.Insert(at, vm);
        Recipe.Steps.Insert(at, step);
        SelectedStep = vm;
        IsAddMenuOpen = false;
        RecipeEdited();
    }

    [ObservableProperty]
    private bool _isAddMenuOpen;

    [RelayCommand]
    private void RemoveStep(StepViewModel? step)
    {
        step ??= SelectedStep;
        if (step is null) return;
        int i = Steps.IndexOf(step);
        Steps.RemoveAt(i);
        Recipe.Steps.RemoveAt(i);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(i, Steps.Count - 1)];
        RecipeEdited();
    }

    [RelayCommand]
    private void MoveUp(StepViewModel? step) => Move(step, -1);

    [RelayCommand]
    private void MoveDown(StepViewModel? step) => Move(step, +1);

    private void Move(StepViewModel? step, int delta)
    {
        step ??= SelectedStep;
        if (step is null) return;
        int i = Steps.IndexOf(step), j = i + delta;
        if (j < 0 || j >= Steps.Count) return;
        Steps.Move(i, j);
        var s = Recipe.Steps[i];
        Recipe.Steps.RemoveAt(i);
        Recipe.Steps.Insert(j, s);
        SelectedStep = step;
        RecipeEdited();
    }

    [RelayCommand]
    private void DuplicateStep(StepViewModel? step)
    {
        step ??= SelectedStep;
        if (step is null) return;
        int i = Steps.IndexOf(step) + 1;
        var copy = step.Step.Clone();
        var vm = Wrap(copy);
        Steps.Insert(i, vm);
        Recipe.Steps.Insert(i, copy);
        SelectedStep = vm;
        RecipeEdited();
    }

    [RelayCommand]
    private void ClearSteps()
    {
        if (Steps.Count == 0) return;
        if (!_dialogs.Confirm("手順をすべて消しますか？", "QuantScope")) return;
        Steps.Clear();
        Recipe.Steps.Clear();
        SelectedStep = null;
        RecipeEdited();
    }

    /// <summary>切り抜きの手順に、今の範囲を入れる</summary>
    [RelayCommand]
    private void UseRoiForCrop()
    {
        if (SelectedStep is not { IsCrop: true } s || Roi is null || Input is null) return;
        FillCropFromRoi(s.Step);
        foreach (var p in s.Parameters) p.Refresh();
        Roi = null;
        RecipeEdited();
    }

    private void FillCropFromRoi(Step step)
    {
        var state = StateBefore(step);
        var img = state?.Image ?? Input!;
        var (x, y, w, h) = Roi!.ClampedPixelRect(img.Width, img.Height);
        if (w < 1 || h < 1) return;
        step.Values["x"] = x;
        step.Values["y"] = y;
        step.Values["width"] = w;
        step.Values["height"] = h;
    }

    partial void OnSelectedStepChanged(StepViewModel? value)
    {
        if (value is not null) ViewIndex = Steps.IndexOf(value);
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(IsShowingResult));
        UpdateHistogramMarker();
        RenderView();
    }

    partial void OnViewIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(IsShowingResult));
        RenderView();
    }

    [RelayCommand]
    private void ShowInput()
    {
        SelectedStep = null;
        ViewIndex = -1;
    }

    [RelayCommand]
    private void ShowFinal()
    {
        SelectedStep = null;
        ViewIndex = Steps.Count - 1;
    }

    private StepViewModel Wrap(Step step) => new(step, CurrentImageForDefaults(), OnStepChanged);

    private Raster? CurrentImageForDefaults() => Input;

    private void OnStepChanged(StepViewModel step, ParamViewModel? param)
    {
        if (step.IsThreshold && param?.Def.Key == "method" && step.Step.GetInt("method") == 0 && HistogramMarker is { } auto)
        {
            // 「手動」に切りかえたら、それまでの自動のしきい値から始める
            step.Step.Values["value"] = auto;
            step.Param("value")?.Refresh();
        }
        if (step.IsThreshold && param?.Def.Key == "value" && step.Step.GetInt("method") != 0)
        {
            // しきい値を動かしたら、自動の決め方から「手動」に切りかえる
            step.Step.Values["method"] = 0;
            step.Param("method")?.Refresh();
        }
        UpdateHistogramMarker();
        RecipeEdited(coalesceKey: param is null ? null : step.GetHashCode() + param.Def.Key);
    }

    private void Renumber()
    {
        for (int i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(IsViewingFinal));
        OnPropertyChanged(nameof(IsShowingResult));
    }

    /// <summary>レシピが変わった: 元に戻す用に覚え、計算し直す</summary>
    private void RecipeEdited(string? coalesceKey = null)
    {
        Recipe.Name = RecipeName;
        _undo.Push(Recipe.ToJson(), coalesceKey);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ScheduleRun();
    }

    partial void OnRecipeNameChanged(string value) => Recipe.Name = value;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var json = _undo.Undo();
        if (json is not null) ApplyRecipe(Recipe.FromJson(json), remember: false);
    }

    private bool CanUndo() => _undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var json = _undo.Redo();
        if (json is not null) ApplyRecipe(Recipe.FromJson(json), remember: false);
    }

    private bool CanRedo() => _undo.CanRedo;

    /// <summary>レシピを丸ごと入れかえる（読み込み・見本・元に戻す）</summary>
    public void ApplyRecipe(Recipe recipe, bool remember = true)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        _suppressRun = true;
        try
        {
            int selected = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
            Recipe = recipe;
            RecipeName = recipe.Name;
            Steps.Clear();
            foreach (var s in recipe.Steps) Steps.Add(Wrap(s));
            MinArea = recipe.Measure.MinArea;
            MaxArea = recipe.Measure.MaxArea;
            ExcludeEdges = recipe.Measure.ExcludeEdges;
            IntensityFromOriginal = recipe.Measure.IntensityFromOriginal;
            SelectedStep = selected >= 0 && selected < Steps.Count && !remember ? Steps[selected] : null;
            ViewIndex = SelectedStep is null ? Steps.Count - 1 : Steps.IndexOf(SelectedStep);
            OnPropertyChanged(nameof(Calibration));
            OnPropertyChanged(nameof(CalibrationText));
        }
        finally
        {
            _suppressRun = false;
        }
        if (remember)
        {
            _undo.Push(Recipe.ToJson(), null);
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
        else
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
        ScheduleRun();
    }

    // ---------------------------------------------------------------- 計算

    public void ScheduleRun()
    {
        if (_suppressRun || Input is null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>今の手順で計算し直す（見本の撮影からも待てるように Task を返す）</summary>
    public async Task RunAsync()
    {
        if (Input is null) return;
        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        int version = ++_runVersion;
        var input = Input;
        var cal = Calibration;
        var steps = Recipe.Steps.Select(s => s.Clone()).ToList();
        var measure = Recipe.Measure;
        var roi = Roi;
        var previous = _run;
        IsBusy = true;
        try
        {
            var (run, analysis) = await Task.Run(() =>
            {
                var r = PipelineRunner.Run(input, cal, steps, previous, cts.Token);
                var final = r.Final;
                var region = roi is not null && RoiFits(roi, final.Image) ? roi : null;
                var a = PipelineRunner.Analyze(final, measure, region);
                return (r, a);
            }, cts.Token);
            if (version != _runVersion) return;
            _run = run;
            for (int i = 0; i < Steps.Count && i < run.Outcomes.Count; i++) Steps[i].SetOutcome(run.Outcomes[i]);
            SetAnalysis(analysis);
            UpdateHistogramMarker();
            if (ViewIndex >= Steps.Count) ViewIndex = Steps.Count - 1;
            RenderView();
            double ms = run.Outcomes.Sum(o => o.Elapsed.TotalMilliseconds);
            var warn = run.Outcomes.Select((o, i) => (o, i)).FirstOrDefault(t => t.o.Warning is not null);
            Status = warn.o is not null
                ? $"手順 {warn.i + 1}: {warn.o.Warning}"
                : string.Create(CultureInfo.CurrentCulture, $"計算しました（{ms:0} ms）");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            App.Log(ex);
            Status = "計算できませんでした: " + ex.Message;
        }
        finally
        {
            if (version == _runVersion) IsBusy = false;
        }
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
        Particles = a?.Particles ?? [];
        SelectedParticle = Particles.FirstOrDefault(p => p.Id == keep);
        if (a is null)
        {
            SummaryCount = SummaryMeanArea = SummaryFraction = SummaryCircularity = "–";
            SummaryDetail = "";
            AnalysisHint = Steps.Count == 0 ? "左の「手順を足す」から処理を選んでください。" : "手順に「二値化」などを入れて対象を選ぶと、数えて測れます。";
            return;
        }
        var s = a.Summary;
        var c = CultureInfo.CurrentCulture;
        SummaryCount = s.Count.ToString("N0", c);
        SummaryMeanArea = Fmt(s.MeanArea) + " " + s.AreaUnit;
        SummaryFraction = s.AreaFraction.ToString("0.00", c) + " %";
        SummaryCircularity = s.MeanCircularity.ToString("0.000", c);
        SummaryDetail = string.Create(c,
            $"面積の合計 {Fmt(s.TotalArea)} {s.AreaUnit}　中央値 {Fmt(s.MedianArea)}　標準偏差 {Fmt(s.StdArea)}\n調べた範囲 {Fmt(s.AnalyzedArea)} {s.AreaUnit}　密度 {s.Density:G3} 個/{s.AreaUnit}")
            + (HasRoi ? "（範囲の中だけ）" : "");
        AnalysisHint = s.Count == 0 ? "対象が見つかりませんでした。しきい値や、計測の条件（面積の下限）を見直してください。" : "";
    }

    internal static string Fmt(double v)
    {
        var c = CultureInfo.CurrentCulture;
        double a = Math.Abs(v);
        return a >= 1000 ? v.ToString("N0", c) : a >= 10 ? v.ToString("0.0", c) : a >= 0.1 ? v.ToString("0.00", c) : v.ToString("G3", c);
    }

    /// <summary>表示している状態</summary>
    public PipelineState? ViewState => _run is null ? null : ViewIndex < 0 ? _run.States[0] : _run.After(Math.Min(ViewIndex, Steps.Count - 1));

    /// <summary>手順 step の直前の状態（切り抜きの範囲を、その時点の画像の座標で入れるため）</summary>
    private PipelineState? StateBefore(Step step)
    {
        if (_run is null) return null;
        int i = Recipe.Steps.IndexOf(step);
        return i < 0 ? _run.Final : _run.After(i - 1);
    }

    private int _renderVersion;

    /// <summary>表示する画像と重ね合わせを作り直す</summary>
    public void RenderView()
    {
        var state = ViewState;
        if (state is null)
        {
            DisplayImage = Input is null ? null : ImageIo.ToBitmap(Input.Width, Input.Height, DisplayRenderer.ToBgra(Input, ColorMap));
            OverlayImage = null;
            DisplayParticles = null;
            DisplayLabels = null;
            UpdateHistogram(Input);
            return;
        }
        var img = DisplayRaster(state);
        int version = ++_renderVersion;
        var map = ColorMap;
        _ = Task.Run(() => DisplayRenderer.ToBgra(img, map)).ContinueWith(t =>
        {
            if (version != _renderVersion || t.IsFaulted) return;
            DisplayImage = ImageIo.ToBitmap(img.Width, img.Height, t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        RenderOverlay();
        UpdateHistogram(img);
        UpdateProfile();
    }

    /// <summary>
    /// 表示する画像: 結果（最後の手順のあと）で対象が選ばれているときは、処理する前の画像に重ねて見せる
    /// （ぼかしや背景の補正のあとの画像より、元の画像のほうが、正しく選べたかを確かめやすい）
    /// </summary>
    private Raster DisplayRaster(PipelineState state) =>
        IsViewingFinal && SelectedStep is null && state.Mask is not null && state.Original.Width == state.Image.Width && state.Original.Height == state.Image.Height
            ? state.Original
            : state.Image;

    /// <summary>いま画面に出している画像（値の表示・線・ヒストグラムに使う）</summary>
    private Raster? DisplayedRaster => ViewState is { } st ? DisplayRaster(st) : Input;

    private int _overlayVersion;

    public void RenderOverlay()
    {
        var state = ViewState;
        bool final = ViewIndex >= Steps.Count - 1;
        int version = ++_overlayVersion;
        if (state?.Mask is null)
        {
            OverlayImage = null;
            DisplayParticles = null;
            DisplayLabels = null;
            return;
        }
        var analysis = Analysis;
        var mask = state.Mask;
        int selected = SelectedParticle?.Id ?? 0;
        var coloring = ColorPerObject ? OverlayColoring.PerObject : OverlayColoring.Uniform;
        bool useLabels = final && analysis is not null && analysis.Labels.Width == mask.Width && analysis.Labels.Height == mask.Height;
        DisplayParticles = useLabels ? analysis!.Particles : null;
        DisplayLabels = useLabels ? analysis!.Labels : null;
        _ = Task.Run(() => useLabels ? DisplayRenderer.LabelOverlay(analysis!.Labels, coloring, selected) : DisplayRenderer.MaskOverlay(mask))
            .ContinueWith(t =>
            {
                if (version != _overlayVersion || t.IsFaulted) return;
                OverlayImage = ImageIo.ToBitmap(mask.Width, mask.Height, t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------------------------------------------------------- ヒストグラム

    private void UpdateHistogram(Raster? img)
    {
        if (img is null)
        {
            Histogram = null;
            HistogramText = "";
            return;
        }
        var region = Roi is not null && RoiFits(Roi, img) ? Roi.ToMask(img.Width, img.Height) : null;
        var h = QuantScope.Core.Imaging.Histogram.Of(img, region);
        Histogram = h;
        var c = CultureInfo.CurrentCulture;
        HistogramText = h.Total == 0 ? "範囲の中に画素がありません。" : string.Create(c,
            $"画素 {h.Total:N0}　平均 {Fmt(h.Mean)}　標準偏差 {Fmt(h.StdDev)}\n最小 {Fmt(h.DataMin)}　中央値 {Fmt(h.Median)}　最大 {Fmt(h.DataMax)}　最頻 {Fmt(h.Mode)}")
            + (region is not null ? "\n（選んだ範囲の中）" : "");
    }

    private void UpdateHistogramMarker()
    {
        if (SelectedStep is { IsThreshold: true } s && _run is not null)
        {
            int i = Steps.IndexOf(s);
            var before = _run.After(i - 1).Image;
            int method = s.Step.GetInt("method");
            HistogramMarker = method == 0 ? s.Step.Get("value") : FindThreshold(before, method);
        }
        else
        {
            HistogramMarker = null;
        }
    }

    private static double FindThreshold(Raster img, int method) =>
        QuantScope.Core.Processing.Thresholds.Find(img, (QuantScope.Core.Processing.ThresholdMethod)method);

    /// <summary>ヒストグラムの線をドラッグした: しきい値の手順の値にする</summary>
    public void SetThresholdFromHistogram(double value)
    {
        if (SelectedStep is not { IsThreshold: true } s) return;
        var p = s.Param("value");
        if (p is null) return;
        p.Value = value;
    }

    partial void OnRoiChanged(Roi? value)
    {
        Matches = [];
        ScheduleRun();
        if (value is null) UpdateHistogram(DisplayedRaster);
    }

    public string RoiText
    {
        get
        {
            if (Roi is null) return "";
            var (x0, y0, x1, y1) = Roi.Bounds;
            string shape = Roi.Shape switch { RoiShape.Rectangle => "四角", RoiShape.Ellipse => "楕円", _ => "多角形" };
            var img = DisplayedRaster;
            int px = img is null ? 0 : Roi.ToMask(img.Width, img.Height).Count();
            return string.Create(CultureInfo.CurrentCulture, $"{shape} {x1 - x0:0} × {y1 - y0:0} px　面積 {Fmt(Calibration.Area(px))} {Calibration.AreaUnit}");
        }
    }

    [RelayCommand]
    private void ClearRoi()
    {
        Roi = null;
        Matches = [];
    }

    // ---------------------------------------------------------------- 線

    partial void OnLineChanged(MeasureLine? value)
    {
        UpdateProfile();
        if (value is not null) RightTab = RightTab.Profile;
    }

    private void UpdateProfile()
    {
        var img = DisplayedRaster;
        if (Line is null || img is null)
        {
            Profile = null;
            return;
        }
        var p = LineProfile.Sample(img, Line.A, Line.B, Calibration);
        Profile = p;
        double px = Line.A.DistanceTo(Line.B);
        ProfileText = string.Create(CultureInfo.CurrentCulture, $"長さ {Fmt(p.Length)} {p.LengthUnit}（{px:0.0} px）　平均 {Fmt(p.Values.Average())}　最小 {Fmt(p.Values.Min())}　最大 {Fmt(p.Values.Max())}");
    }

    [RelayCommand]
    private void ClearLine()
    {
        Line = null;
        ProfileText = "画像の上で「線」の道具を使って線を引くと、線に沿った明るさの変化が出ます。";
    }

    // ---------------------------------------------------------------- そのほか

    [RelayCommand]
    private void SetTool(Tool tool) => Tool = tool;

    [RelayCommand]
    private void SetTab(RightTab tab) => RightTab = tab;

    [RelayCommand]
    private void SetColorMap(ColorMap map) => ColorMap = map;

    /// <summary>画像の上のマウスの位置の値を出す</summary>
    public void UpdateHover(PointD? p)
    {
        var img = DisplayedRaster;
        if (p is not { } pt || img is null)
        {
            HoverText = null;
            return;
        }
        int x = (int)Math.Floor(pt.X), y = (int)Math.Floor(pt.Y);
        if (x < 0 || y < 0 || x >= img.Width || y >= img.Height)
        {
            HoverText = null;
            return;
        }
        var c = CultureInfo.CurrentCulture;
        string value = img.IsColor
            ? string.Create(c, $"R {img.Get(x, y, 0):0} G {img.Get(x, y, 1):0} B {img.Get(x, y, 2):0}")
            : "値 " + Fmt(img.Get(x, y));
        string pos = Calibration.IsCalibrated
            ? string.Create(c, $"{Fmt(Calibration.Length(pt.X))}, {Fmt(Calibration.Length(pt.Y))} {Calibration.Unit}")
            : $"{x}, {y} px";
        HoverText = $"{pos}　{value}";
    }

    /// <summary>画像の上をクリックした所の粒を選ぶ</summary>
    public void SelectParticleAt(PointD p)
    {
        if (DisplayLabels is not { } labels || DisplayParticles is null) return;
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= labels.Width || y >= labels.Height) return;
        int id = labels.Labels[(y * labels.Width) + x];
        SelectedParticle = id == 0 ? null : DisplayParticles.FirstOrDefault(q => q.Id == id);
        if (SelectedParticle is not null) RightTab = RightTab.Results;
    }
}

/// <summary>手順を足すメニューの 1 つの区分</summary>
public sealed record StepGroup(string Title, IReadOnlyList<StepDefinition> Items);

/// <summary>メニューの項目（見本など）</summary>
public sealed record NamedItem(string Id, string Title);

/// <summary>画像の上の線（px の座標）</summary>
public sealed record MeasureLine(PointD A, PointD B);
