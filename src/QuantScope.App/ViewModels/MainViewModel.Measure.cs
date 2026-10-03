using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantScope.App.Services;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Reporting;
using QuantScope.Core.Rendering;

namespace QuantScope.App.ViewModels;

public enum DistributionMode
{
    /// <summary>表示している画像の画素の明るさ</summary>
    Pixels,

    /// <summary>計測した粒の値（面積・円形度など）</summary>
    Particles,
}

/// <summary>分布で選べる値（見出しは単位つき）</summary>
public sealed record FeatureItem(ParticleFeature Feature, string Label)
{
    public override string ToString() => Label;
}

/// <summary>選んだ粒の値の 1 行</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>計測の条件・陽性の判定・手で除く粒・粒の値の分布・比べる表示・最近のファイル・レポート</summary>
public sealed partial class MainViewModel
{
    public static IReadOnlyList<string> ChannelTitles => IntensityChannels.Titles;

    public static string AppVersion { get; } =
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    // ---------------------------------------------------------------- 測るもの・陽性の判定

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelNeedsColor))]
    private int _channelIndex;

    /// <summary>白黒の画像で色や染色を選んでいる（明るさで測ることになる）</summary>
    public bool ChannelNeedsColor => ChannelIndex != 0 && Input is { IsColor: false };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeatureSplit))]
    private bool _classify;

    [ObservableProperty]
    private double _positiveThreshold;

    [ObservableProperty]
    private bool _positiveAbove = true;

    partial void OnChannelIndexChanged(int value) => MeasureChanged();
    partial void OnClassifyChanged(bool value)
    {
        if (value && PositiveThreshold == 0 && Analysis is { Particles.Count: > 1 } a)
        {
            // 初めてオンにしたときは、粒の明るさを 2 組に分ける値（大津）から始める
            _suppressMeasure = true;
            PositiveThreshold = Math.Round(Statistics.OtsuThreshold(a.Particles.Select(p => p.MeanIntensity)), 4);
            _suppressMeasure = false;
        }
        MeasureChanged();
    }

    partial void OnPositiveThresholdChanged(double value)
    {
        UpdateDistribution();
        if (!_suppressMeasure) MeasureChanged(coalesce: "positive");
    }

    partial void OnPositiveAboveChanged(bool value) => MeasureChanged();

    private bool _suppressMeasure;

    /// <summary>陽性のしきい値を、粒の平均の分布を 2 組に分ける値（大津の方法）にする</summary>
    [RelayCommand]
    private void AutoPositiveThreshold()
    {
        if (Analysis is not { Particles.Count: > 1 } a) return;
        PositiveThreshold = Math.Round(Statistics.OtsuThreshold(a.Particles.Select(p => p.MeanIntensity)), 4);
        Status = string.Create(CultureInfo.CurrentCulture, $"陽性のしきい値を {Fmt(PositiveThreshold)} にしました（粒の平均を 2 組に分ける値・大津の方法）。");
    }

    /// <summary>陽性の判定に使う、粒の平均の分布</summary>
    [ObservableProperty]
    private IReadOnlyList<Bin>? _intensityBins;

    public void SetPositiveFromChart(double v) => PositiveThreshold = Math.Round(v, 4);

    // ---------------------------------------------------------------- 結果のカード

    /// <summary>画像の上に陽性・陰性の凡例を出す</summary>
    public bool ShowClassLegend => Analysis?.Summary.Positive is not null && ShowOverlay && !ColorPerObject && IsShowingResult;

    [ObservableProperty]
    private string _kpi4Label = "平均の円形度";

    [ObservableProperty]
    private string _kpi4Value = "–";

    [ObservableProperty]
    private string _kpi4Sub = "";

    [ObservableProperty]
    private string _intensityHeader = "平均の明るさ";

    [ObservableProperty]
    private string _measureSummary = "";

    private void UpdateMeasureSummary()
    {
        var m = Recipe.Measure;
        var parts = new List<string>();
        if (m.MinArea > 0 || m.MaxArea > 0) parts.Add($"面積 {Fmt(m.MinArea)}〜{(m.MaxArea > 0 ? Fmt(m.MaxArea) : "")}");
        if (m.ExcludeEdges) parts.Add("ふちを除く");
        parts.Add(IntensityChannels.Title(m.Channel));
        if (m.Classify) parts.Add($"陽性 {(m.PositiveAbove ? "≥" : "<")} {Fmt(m.PositiveThreshold)}");
        if (_excluded.Count > 0) parts.Add($"手で除く {_excluded.Count}");
        MeasureSummary = string.Join("・", parts);
    }

    // ---------------------------------------------------------------- 手で除く粒

    private readonly List<PointD> _excluded = [];

    [ObservableProperty]
    private string _excludedText = "";

    public bool HasExcluded => _excluded.Count > 0;

    /// <summary>「粒を除く」の道具でクリックした: 粒を除く（除いた粒なら戻す）</summary>
    public void ToggleExcludeAt(PointD p)
    {
        var a = Analysis;
        if (a is null) return;
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= a.Labels.Width || y >= a.Labels.Height) return;
        if (a.Excluded is { } ex && ex[x, y])
        {
            // 除いた粒をクリック: その粒の中にある除く点を消す
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
            Status = "粒を除きました（もう一度クリックすると戻せます）。";
        }
        else
        {
            return;
        }
        ExcludedChanged();
    }

    /// <summary>選んでいる粒を除く</summary>
    [RelayCommand]
    private void ExcludeSelected()
    {
        if (SelectedParticle is not { } p || Analysis is not { } a) return;
        // 重心が粒の外になる形（輪など）もあるので、粒の中の画素を探す
        var l = a.Labels;
        for (int y = p.BoundsY; y < p.BoundsY + p.BoundsHeight; y++)
            for (int x = p.BoundsX; x < p.BoundsX + p.BoundsWidth; x++)
                if (l.Labels[(y * l.Width) + x] == p.Id)
                {
                    _excluded.Add(new PointD(x + 0.5, y + 0.5));
                    SelectedParticle = null;
                    Status = $"粒 {p.Id} を除きました。";
                    ExcludedChanged();
                    return;
                }
    }

    [RelayCommand]
    private void RestoreExcluded()
    {
        if (_excluded.Count == 0) return;
        _excluded.Clear();
        Status = "手で除いた粒を、すべて戻しました。";
        ExcludedChanged();
    }

    private void ExcludedChanged()
    {
        ExcludedText = _excluded.Count == 0 ? "" : $"手で除いた粒 {_excluded.Count} 個";
        OnPropertyChanged(nameof(HasExcluded));
        UpdateMeasureSummary();
        ScheduleRun();
    }

    private void ClearExcluded()
    {
        _excluded.Clear();
        ExcludedText = "";
        OnPropertyChanged(nameof(HasExcluded));
    }

    // ---------------------------------------------------------------- 選んだ粒

    [ObservableProperty]
    private IReadOnlyList<DetailRow> _selectedParticleRows = [];

    private void UpdateSelectedDetails()
    {
        if (SelectedParticle is not { } p || Analysis is not { } a)
        {
            SelectedParticleRows = [];
            return;
        }
        var s = a.Summary;
        var rows = ParticleFeatures.All.Select(f => new DetailRow(f.Header(s), Fmt(f.Get(p)))).ToList();
        rows.Add(new DetailRow("重心 (px)", string.Create(CultureInfo.CurrentCulture, $"{p.CentroidX:0.0}, {p.CentroidY:0.0}")));
        if (p.Positive is { } pos) rows.Insert(0, new DetailRow("判定", pos ? "陽性" : "陰性"));
        if (p.TouchesEdge) rows.Add(new DetailRow("ふち", "触れている"));
        SelectedParticleRows = rows;
    }

    // ---------------------------------------------------------------- 粒の値の分布

    [ObservableProperty]
    private DistributionMode _distributionMode = DistributionMode.Pixels;

    [ObservableProperty]
    private IReadOnlyList<FeatureItem> _featureItems = [];

    [ObservableProperty]
    private FeatureItem? _selectedFeature;

    [ObservableProperty]
    private IReadOnlyList<Bin>? _featureBins;

    [ObservableProperty]
    private IReadOnlyList<DetailRow> _featureStats = [];

    [ObservableProperty]
    private double? _featureMarker;

    /// <summary>分布で、陽性・陰性の色分けをする（平均の値を見ていて、判定がオンのとき）</summary>
    public bool FeatureSplit => Classify && SelectedFeature?.Feature == ParticleFeatures.MeanIntensity;

    partial void OnDistributionModeChanged(DistributionMode value) => UpdateDistribution();

    partial void OnSelectedFeatureChanged(FeatureItem? value)
    {
        OnPropertyChanged(nameof(FeatureSplit));
        UpdateDistribution();
    }

    private void RefreshFeatureItems()
    {
        var s = Analysis?.Summary ?? new AnalysisSummary { AreaUnit = Calibration.AreaUnit, LengthUnit = Calibration.LengthUnit, IntensityLabel = IntensityChannels.Title((IntensityChannel)ChannelIndex) };
        string key = SelectedFeature?.Feature.Key ?? ParticleFeatures.Area.Key;
        FeatureItems = ParticleFeatures.All.Select(f => new FeatureItem(f, f.Header(s))).ToList();
        SelectedFeature = FeatureItems.FirstOrDefault(i => i.Feature.Key == key) ?? FeatureItems[0];
    }

    private void UpdateDistribution()
    {
        var a = Analysis;
        IntensityBins = a is { Particles.Count: > 0 } ? Statistics.Histogram(a.Particles.Select(p => p.MeanIntensity)) : null;
        if (a is null || SelectedFeature is null || a.Particles.Count == 0)
        {
            FeatureBins = null;
            FeatureStats = [];
            FeatureMarker = null;
            return;
        }
        var f = SelectedFeature.Feature;
        var values = a.Particles.Select(f.Get).ToList();
        FeatureBins = Statistics.Histogram(values);
        FeatureMarker = FeatureSplit ? PositiveThreshold : null;
        var d = Statistics.Describe(values);
        string u = f.UnitText(a.Summary);
        string U(double v) => Fmt(v) + (u.Length > 0 ? " " + u : "");
        var rows = new List<DetailRow>
        {
            new("数", d.N.ToString("N0", CultureInfo.CurrentCulture)),
            new("平均 ± 標準偏差", $"{U(d.Mean)} ± {Fmt(d.Sd)}"),
            new("中央値［四分位］", $"{U(d.Median)}［{Fmt(d.Q1)}–{Fmt(d.Q3)}］"),
            new("最小 – 最大", $"{Fmt(d.Min)} – {U(d.Max)}"),
            new("変動係数", d.CvPercent.ToString("0.0", CultureInfo.CurrentCulture) + " %"),
        };
        if (FeatureSplit && a.Summary.Positive is not null)
            rows.Add(new DetailRow("陽性", $"{a.Summary.PositiveCount} / {a.Summary.Count}（{a.Summary.PositivePercent.ToString("0.0", CultureInfo.CurrentCulture)} %）"));
        FeatureStats = rows;
    }

    // ---------------------------------------------------------------- 比べる

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private ImageSource? _compareImage;

    partial void OnIsComparingChanged(bool value) => RenderCompare();

    private void RenderCompare()
    {
        if (!IsComparing || ViewState is not { } st)
        {
            CompareImage = null;
            return;
        }
        var img = st.Original;
        var map = ColorMap;
        _ = Task.Run(() => DisplayRenderer.ToBgra(img, map)).ContinueWith(t =>
        {
            if (t.IsFaulted || !IsComparing) return;
            CompareImage = ImageIo.ToBitmap(img.Width, img.Height, t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------------------------------------------------------- 最近のファイル

    public ObservableCollection<NamedItem> RecentFiles { get; } = [];

    [ObservableProperty]
    private bool _isOpenMenuOpen;

    private void RefreshRecent()
    {
        RecentFiles.Clear();
        foreach (var path in Settings.RecentFiles.Where(File.Exists).Take(UserSettings.MaxRecent))
            RecentFiles.Add(new NamedItem(path, Path.GetFileName(path)));
        OnPropertyChanged(nameof(HasRecent));
    }

    public bool HasRecent => RecentFiles.Count > 0;

    [RelayCommand]
    private async Task OpenRecent(string path)
    {
        IsOpenMenuOpen = false;
        await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task OpenFromMenu()
    {
        IsOpenMenuOpen = false;
        await Open();
    }

    [RelayCommand]
    private void LoadRecipeFromMenu()
    {
        IsOpenMenuOpen = false;
        LoadRecipe();
    }

    // ---------------------------------------------------------------- 表のコピー・レポート

    [RelayCommand]
    private void CopyTable()
    {
        if (Analysis is not { } a || a.Particles.Count == 0) return;
        var s = a.Summary;
        string tsv = CsvExport.Particles(a.Particles.Select(p => ((string?)null, p)), s.LengthUnit, s.AreaUnit, s.IntensityLabel, '\t');
        try
        {
            Clipboard.SetText(tsv);
            Status = $"粒ごとの値（{a.Particles.Count} 行）をコピーしました。Excel などに貼り付けられます。";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Status = "コピーできませんでした: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveReport()
    {
        IsSaveMenuOpen = false;
        if (Analysis is null || _run is null) return;
        string? path = _dialogs.SaveFile("解析レポートを保存", "HTML (*.html)|*.html", $"{BaseName}_レポート.html", Settings.LastFolder);
        if (path is null) return;
        IsBusy = true;
        Status = "レポートを作っています…";
        try
        {
            string? html = await BuildReportHtmlAsync();
            if (html is null) return;
            Try(() => File.WriteAllText(path, html, new System.Text.UTF8Encoding(false)), path);
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status = "レポートを作れませんでした: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>今の結果の解析レポート（HTML）を作る。結果がなければ null</summary>
    public async Task<string?> BuildReportHtmlAsync()
    {
        if (Analysis is not { } a || _run is not { } run) return null;
        var state = run.Final;
        var map = ColorMap;
        bool perObject = ColorPerObject;
        double opacity = Math.Max(OverlayOpacity, 0.6);
        var input = new ReportInput
        {
            Recipe = Recipe.Clone(),
            Result = a,
            Calibration = Calibration,
            FileName = FileName,
            ImageDescription = ImageDescription,
            ImageWidth = state.Image.Width,
            ImageHeight = state.Image.Height,
            Outcomes = run.Outcomes,
            RegionText = HasRoi ? RoiText : null,
            AppVersion = AppVersion,
        };
        return await Task.Run(() =>
        {
            var orig = state.Original.Width == state.Image.Width ? state.Original : state.Image;
            byte[] originalPng = ImageIo.EncodePng(orig.Width, orig.Height, DisplayRenderer.ToBgra(orig, map), 1600);
            byte[] overlayPng = ImageIo.EncodePng(orig.Width, orig.Height, OverlayBytes(state, a, map, perObject, opacity), 1600);
            return AnalysisReport.BuildHtml(input with { OriginalPng = originalPng, OverlayPng = overlayPng });
        });
    }
}
