using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantScope.App.Services;
using QuantScope.Core.Ai;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Rendering;
using QuantScope.Core.Samples;

namespace QuantScope.App.ViewModels;

/// <summary>開く・保存・見本・縮尺・似た場所を探す・AI</summary>
public sealed partial class MainViewModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static IReadOnlyList<NamedItem> Samples { get; } = SampleImages.List.Select(s => new NamedItem(s.Id, s.Title)).ToList();

    // ---------------------------------------------------------------- 開く

    [RelayCommand]
    private async Task Open()
    {
        string? path = _dialogs.OpenFile("画像を開く", ImageIo.OpenFilter, Settings.LastFolder);
        if (path is not null) await OpenPathAsync(path);
    }

    /// <summary>画像・レシピ（.json）・フォルダー（一括処理）を開く</summary>
    public async Task OpenPathAsync(string path)
    {
        if (Directory.Exists(path))
        {
            OpenBatch(path);
            return;
        }
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            LoadRecipeFrom(path);
            return;
        }
        IsBusy = true;
        Status = $"{Path.GetFileName(path)} を開いています…";
        try
        {
            var img = await Task.Run(() => ImageIo.Load(path));
            SetImage(img, Path.GetFileName(path));
            Settings.LastFolder = Path.GetDirectoryName(path);
            Settings.AddRecent(Path.GetFullPath(path));
            SaveSettings();
            RefreshRecent();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = "開けませんでした: " + ex.Message;
            _dialogs.Info($"{Path.GetFileName(path)} を開けませんでした。\n\n{ex.Message}", "QuantScope");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>新しい画像にする。手順（レシピ）はそのまま使う。</summary>
    public void SetImage(LoadedImage image, string name)
    {
        ArgumentNullException.ThrowIfNull(image);
        _runCts?.Cancel();
        _run = null;
        _source = image;
        Roi = null;
        Line = null;
        Matches = [];
        SelectedParticle = null;
        Analysis = null;
        ClearExcluded();
        UpdateMeasureSummary();
        Input = image.Raster;
        FileName = name;
        ImageDescription = image.Description;
        OnPropertyChanged(nameof(Calibration));
        OnPropertyChanged(nameof(CalibrationText));
        // 画像の値の範囲が変わると、手順の入力欄の範囲も変わるので作り直す
        int selected = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        _suppressRun = true;
        Steps.Clear();
        foreach (var s in Recipe.Steps) Steps.Add(Wrap(s));
        SelectedStep = selected >= 0 && selected < Steps.Count ? Steps[selected] : null;
        ViewIndex = SelectedStep is null ? Steps.Count - 1 : selected;
        _suppressRun = false;
        RenderView();
        Status = $"{name} を開きました（{image.Description}）。";
        ScheduleRun();
    }

    [RelayCommand]
    private void OpenSample(string id)
    {
        var s = SampleImages.Create(id);
        IsSampleMenuOpen = false;
        var recipe = s.Recipe.Clone();
        recipe.Calibration = s.Calibration;
        ApplyRecipe(recipe);
        SetImage(new LoadedImage(s.Image, s.Calibration, "見本の画像（人工）"), s.Title);
        SelectedStep = null;
        ViewIndex = Steps.Count - 1;
        Status = s.Description;
    }

    [ObservableProperty]
    private bool _isSampleMenuOpen;

    [ObservableProperty]
    private bool _isSaveMenuOpen;

    // ---------------------------------------------------------------- 保存

    private string BaseName => Path.GetFileNameWithoutExtension(FileName ?? "quantscope");

    [RelayCommand]
    private void SaveView()
    {
        IsSaveMenuOpen = false;
        var img = ViewState?.Image ?? Input;
        if (img is null) return;
        string? path = _dialogs.SaveFile("表示している画像を保存", "PNG 画像 (*.png)|*.png", $"{BaseName}_処理後.png", Settings.LastFolder);
        if (path is null) return;
        Try(() => ImageIo.SavePng(path, img.Width, img.Height, DisplayRenderer.ToBgra(img, ColorMap)), path);
    }

    [RelayCommand]
    private void SaveOverlay()
    {
        IsSaveMenuOpen = false;
        var state = ViewState;
        if (state?.Mask is null) return;
        string? path = _dialogs.SaveFile("重ねた画像を保存", "PNG 画像 (*.png)|*.png", $"{BaseName}_重ね.png", Settings.LastFolder);
        if (path is null) return;
        Try(() => ImageIo.SavePng(path, state.Image.Width, state.Image.Height, OverlayBytes(state, Analysis, ColorMap, ColorPerObject, OverlayOpacity)), path);
    }

    /// <summary>画像にマスク（計測した粒）を重ねた色</summary>
    internal static byte[] OverlayBytes(PipelineState state, AnalysisResult? analysis, ColorMap map, bool perObject, double opacity)
    {
        var baseBytes = DisplayRenderer.ToBgra(state.Original.Width == state.Image.Width ? state.Original : state.Image, map);
        var over = analysis is not null && analysis.Labels.Width == state.Mask!.Width
            ? ResultOverlay(analysis, perObject)
            : DisplayRenderer.MaskOverlay(state.Mask!);
        return DisplayRenderer.Compose(baseBytes, over, opacity);
    }

    [RelayCommand]
    private void SaveMask()
    {
        IsSaveMenuOpen = false;
        var mask = ViewState?.Mask;
        if (mask is null) return;
        string? path = _dialogs.SaveFile("マスクを保存（対象 = 白）", "PNG 画像 (*.png)|*.png", $"{BaseName}_マスク.png", Settings.LastFolder);
        if (path is null) return;
        Try(() => ImageIo.SavePng(path, mask.Width, mask.Height, DisplayRenderer.MaskToBgra(mask)), path);
    }

    [RelayCommand]
    private void SaveCsv()
    {
        IsSaveMenuOpen = false;
        if (Analysis is null) return;
        string? path = _dialogs.SaveFile("計測結果を保存", "CSV (*.csv)|*.csv", $"{BaseName}_計測.csv", Settings.LastFolder);
        if (path is null) return;
        var s = Analysis.Summary;
        string csv = CsvExport.Particles(Analysis.Particles.Select(p => ((string?)null, p)), s.LengthUnit, s.AreaUnit, s.IntensityLabel);
        Try(() => File.WriteAllText(path, csv, CsvExport.Encoding), path);
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        IsSaveMenuOpen = false;
        string? path = _dialogs.SaveFile("レシピを保存", "QuantScope のレシピ (*.json)|*.json", $"{RecipeName}.json", Settings.LastFolder);
        if (path is null) return;
        Recipe.Name = RecipeName;
        Try(() => File.WriteAllText(path, Recipe.ToJson()), path);
    }

    [RelayCommand]
    private void LoadRecipe()
    {
        string? path = _dialogs.OpenFile("レシピを開く", "QuantScope のレシピ (*.json)|*.json", Settings.LastFolder);
        if (path is not null) LoadRecipeFrom(path);
    }

    private void LoadRecipeFrom(string path)
    {
        try
        {
            ApplyRecipe(Recipe.FromJson(File.ReadAllText(path)));
            Status = $"レシピ「{RecipeName}」を読み込みました。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.Info("レシピを読み込めませんでした。\n\n" + ex.Message, "QuantScope");
        }
    }

    private void Try(Action save, string path)
    {
        try
        {
            save();
            Status = $"{Path.GetFileName(path)} に保存しました。";
            Settings.LastFolder = Path.GetDirectoryName(path);
            SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = "保存できませんでした: " + ex.Message;
            _dialogs.Info("保存できませんでした。\n\n" + ex.Message, "QuantScope");
        }
    }

    // ---------------------------------------------------------------- 縮尺

    [ObservableProperty]
    private bool _isCalibrating;

    [ObservableProperty]
    private string _calibrationLength = "10";

    [ObservableProperty]
    private string _calibrationUnit = "µm";

    public static IReadOnlyList<string> Units { get; } = ["nm", "µm", "mm", "cm"];

    [ObservableProperty]
    private string _calibrationScaleInput = "1";

    /// <summary>縮尺の設定を開く（線があれば、その長さを使う）</summary>
    [RelayCommand]
    private void BeginCalibration()
    {
        CalibrationUnit = Calibration.IsCalibrated ? Calibration.Unit : "µm";
        CalibrationScaleInput = Calibration.IsCalibrated ? Calibration.UnitsPerPixel.ToString("0.#####", CultureInfo.CurrentCulture) : "1";
        IsCalibrating = true;
    }

    public string CalibrationLineText => Line is null
        ? "画像の上で「線」の道具を使い、長さの分かるもの（スケールバーなど）に線を引くと、線から縮尺を決められます。"
        : string.Create(CultureInfo.CurrentCulture, $"引いた線の長さ: {Line.A.DistanceTo(Line.B):0.0} px");

    [RelayCommand]
    private void ApplyCalibrationFromLine()
    {
        if (Line is null) return;
        if (!double.TryParse(CalibrationLength, NumberStyles.Float, CultureInfo.CurrentCulture, out double len) || len <= 0)
        {
            Status = "実際の長さを、0 より大きい数で入れてください。";
            return;
        }
        SetCalibration(Calibration.FromKnownLength(Line.A.DistanceTo(Line.B), len, CalibrationUnit));
    }

    [RelayCommand]
    private void ApplyCalibrationScale()
    {
        if (!double.TryParse(CalibrationScaleInput, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) || v <= 0)
        {
            Status = "1 px あたりの長さを、0 より大きい数で入れてください。";
            return;
        }
        SetCalibration(new Calibration(v, CalibrationUnit));
    }

    [RelayCommand]
    private void ClearCalibration()
    {
        Recipe.Calibration = null;
        IsCalibrating = false;
        AfterCalibration();
    }

    private void SetCalibration(Calibration c)
    {
        Recipe.Calibration = c;
        IsCalibrating = false;
        AfterCalibration();
    }

    private void AfterCalibration()
    {
        OnPropertyChanged(nameof(Calibration));
        OnPropertyChanged(nameof(CalibrationText));
        _run = null; // 縮尺が変わると面積も変わるので、すべて計算し直す
        RecipeEdited();
        UpdateProfile();
        Status = "縮尺: " + Calibration;
    }

    partial void OnLineChanged(MeasureLine? oldValue, MeasureLine? newValue) => OnPropertyChanged(nameof(CalibrationLineText));

    // ---------------------------------------------------------------- 似た場所を探す

    [RelayCommand]
    private async Task FindSimilar()
    {
        var img = ViewState?.Image ?? Input;
        if (Roi is null || img is null) return;
        var (x, y, w, h) = Roi.ClampedPixelRect(img.Width, img.Height);
        if (w < 3 || h < 3)
        {
            Status = "範囲が小さすぎます。";
            return;
        }
        IsBusy = true;
        Status = "似た場所を探しています…";
        try
        {
            var template = QuantScope.Core.Processing.Geometry.Crop(img, x, y, w, h);
            var found = await Task.Run(() => TemplateMatcher.Find(img, template, minScore: SimilarityThreshold, maxResults: 200));
            Matches = found;
            Status = $"似た場所が {found.Count} か所 見つかりました（似ている度合い {SimilarityThreshold:0.00} 以上）。";
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    private double _similarityThreshold = 0.7;

    // ---------------------------------------------------------------- AI

    [ObservableProperty]
    private string _aiQuestion = "";

    [ObservableProperty]
    private string _aiAnswer = "";

    [ObservableProperty]
    private bool _isAiBusy;

    [ObservableProperty]
    private bool _aiSendProcessed = true;

    public bool HasApiKey => Settings.HasApiKey;

    [RelayCommand]
    private async Task AskAi()
    {
        var state = ViewState;
        var img = state?.Image ?? Input;
        if (img is null) return;
        if (!Settings.HasApiKey)
        {
            AiAnswer = "OpenAI の API キーが設定されていません。右上の「設定」から入れてください。";
            return;
        }
        if (!Settings.AiConsentAccepted)
        {
            bool ok = _dialogs.Confirm(
                "AI に説明してもらうと、次のものが OpenAI に送られます。\n\n" +
                "・表示している画像（長い辺 1024 px 以下に縮めたもの）\n" +
                "・行った手順の名前と、計測のまとめの数値\n\n" +
                "ファイル名や、画像に付いている情報（DICOM の患者情報など）は送りません。\n" +
                "ただし、画像の中に文字で写り込んだ名前などはそのまま送られます。患者さんや個人の分かる画像は送らないでください。\n\n" +
                "AI の説明はまちがうことがあり、診断には使えません。\n\nよろしいですか？",
                "AI に送る前に");
            if (!ok) return;
            Settings.AiConsentAccepted = true;
            SaveSettings();
        }
        IsAiBusy = true;
        AiAnswer = "AI に送っています…";
        try
        {
            var source = AiSendProcessed ? img : (state?.Original ?? img);
            var map = ColorMap;
            byte[] png = await Task.Run(() => ImageIo.EncodeForAi(source.Width, source.Height, DisplayRenderer.ToBgra(source, map)));
            var titles = Steps.Where(s => s.Enabled).Select(s => s.Title).ToList();
            var request = new DescribeRequest(png, AiSendProcessed ? titles : [], Analysis?.Summary, AiQuestion);
            var describer = new ImageDescriber(Http);
            AiAnswer = await describer.DescribeAsync(request, Settings.OpenAIApiKey!, Settings.OpenAIModel);
        }
        catch (AiException ex)
        {
            AiAnswer = ex.Message;
        }
        finally
        {
            IsAiBusy = false;
        }
    }

    // ---------------------------------------------------------------- 設定・一括処理

    public void SaveSettings()
    {
        try
        {
            _settingsStore.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
        }
    }

    public void ReloadSettings()
    {
        Settings = _settingsStore.Load();
        OnPropertyChanged(nameof(HasApiKey));
        RefreshRecent();
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler<string?>? BatchRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenBatch(string? folder) => BatchRequested?.Invoke(this, folder);
}
