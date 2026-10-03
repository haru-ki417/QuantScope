using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantScope.App.Services;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Rendering;

namespace QuantScope.App.ViewModels;

/// <summary>一括処理の 1 行</summary>
public sealed record BatchRow(string File, string Count, string Fraction, string MeanArea, string Positive, string Note, bool Failed);

/// <summary>
/// 一括処理: フォルダーの画像すべてに、今のレシピを同じようにかけて測り、CSV（と確認用の重ねた画像）を書き出す。
/// </summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private readonly Recipe _recipe;
    private readonly Calibration _calibration;
    private readonly IDialogs _dialogs;
    private readonly UserSettings _settings;
    private CancellationTokenSource? _cts;

    public BatchViewModel(Recipe recipe, Calibration calibration, string? folder, IDialogs dialogs, UserSettings settings)
    {
        _recipe = recipe;
        _calibration = calibration;
        _dialogs = dialogs;
        _settings = settings;
        RecipeText = $"「{recipe.Name}」（手順 {recipe.Steps.Count(s => s.Enabled)} つ・縮尺 {calibration}）";
        if (folder is not null) SetFolder(folder);
    }

    public string RecipeText { get; }

    public bool RecipeHasSegmentation => _recipe.Steps.Any(s => s.Enabled && StepCatalog.Get(s.Kind).Category == StepCategory.Segment);

    [ObservableProperty]
    private string? _inputFolder;

    [ObservableProperty]
    private string? _outputFolder;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private bool _saveOverlays = true;

    [ObservableProperty]
    private IReadOnlyList<string> _files = [];

    [ObservableProperty]
    private string _filesText = "フォルダーを選んでください。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private bool _isDone;

    public ObservableCollection<BatchRow> Rows { get; } = [];

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (InputFolder is not null) SetFolder(InputFolder);
    }

    [RelayCommand]
    private void PickInput()
    {
        string? f = _dialogs.PickFolder("画像の入ったフォルダーを選ぶ", InputFolder ?? _settings.LastFolder);
        if (f is not null) SetFolder(f);
    }

    [RelayCommand]
    private void PickOutput()
    {
        string? f = _dialogs.PickFolder("結果を書き出すフォルダーを選ぶ", OutputFolder ?? InputFolder);
        if (f is not null) OutputFolder = f;
    }

    private void SetFolder(string folder)
    {
        InputFolder = folder;
        _settings.LastFolder = folder;
        OutputFolder ??= Path.Combine(folder, "QuantScope の結果");
        try
        {
            Files = ImageIo.FindImages(folder, IncludeSubfolders)
                .Where(f => !f.StartsWith(OutputFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FilesText = Files.Count == 0 ? "このフォルダーには、読める画像がありません。" : $"画像 {Files.Count} 枚";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Files = [];
            FilesText = "フォルダーを読めませんでした: " + ex.Message;
        }
        RunCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsRunning && Files.Count > 0 && OutputFolder is not null && RecipeHasSegmentation;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (OutputFolder is null) return;
        IsRunning = true;
        IsDone = false;
        Rows.Clear();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var results = new List<BatchItemResult>();
        string outDir = OutputFolder;
        bool overlays = SaveOverlays;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        try
        {
            Directory.CreateDirectory(outDir);
            var recipe = _recipe.Clone();
            recipe.Calibration ??= _calibration.IsCalibrated ? _calibration : null;
            for (int i = 0; i < Files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string file = Files[i];
                Progress = (double)i / Files.Count;
                ProgressText = $"{i + 1} / {Files.Count}　{Path.GetFileName(file)}";
                var item = await Task.Run(() => ProcessOne(file, recipe, overlays ? outDir : null, token), token);
                results.Add(item);
                var s = item.Result?.Summary;
                var c = CultureInfo.CurrentCulture;
                Rows.Add(new BatchRow(item.File,
                    s?.Count.ToString("N0", c) ?? "",
                    s is null ? "" : s.AreaFraction.ToString("0.00", c) + "%",
                    s is null ? "" : MainViewModel.Fmt(s.MeanArea),
                    s?.Positive is null ? "" : s.PositivePercent.ToString("0.0", c) + "%",
                    item.Error ?? "",
                    item.Result is null));
            }
            Progress = 1;
            await File.WriteAllTextAsync(Path.Combine(outDir, $"まとめ_{stamp}.csv"), BatchProcessor.SummaryCsv(results), CsvExport.Encoding, token);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"粒ごと_{stamp}.csv"), BatchProcessor.ParticlesCsv(results), CsvExport.Encoding, token);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"レシピ_{stamp}.json"), recipe.ToJson(), token);
            int failed = results.Count(r => r.Result is null);
            ProgressText = $"終わりました: {results.Count - failed} 枚を測りました" + (failed > 0 ? $"（{failed} 枚は測れませんでした）" : "") + "。結果は書き出し先のフォルダーにあります。";
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText = $"止めました（{results.Count} 枚まで）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText = "書き出せませんでした: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static BatchItemResult ProcessOne(string file, Recipe recipe, string? overlayDir, CancellationToken token)
    {
        string name = Path.GetFileName(file);
        try
        {
            var img = ImageIo.Load(file);
            var cal = recipe.Calibration ?? img.Calibration ?? Calibration.Pixels;
            var run = PipelineRunner.Run(img.Raster, cal, recipe.Steps, null, token);
            var result = PipelineRunner.Analyze(run.Final, recipe.Measure);
            var warn = run.Outcomes.Select((o, k) => (o, k)).Where(t => t.o.Warning is not null).Select(t => $"手順 {t.k + 1}: {t.o.Warning}").FirstOrDefault();
            if (result is null) return new BatchItemResult(name, null, warn ?? "対象を選ぶ手順（二値化など）がありません");
            if (overlayDir is not null)
            {
                var bytes = MainViewModel.OverlayBytes(run.Final, result, ColorMap.Gray, perObject: false, 0.8);
                ImageIo.SavePng(Path.Combine(overlayDir, Path.GetFileNameWithoutExtension(file) + "_重ね.png"), run.Final.Image.Width, run.Final.Image.Height, bytes);
            }
            return new BatchItemResult(name, result, warn);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            return new BatchItemResult(name, null, ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenOutput()
    {
        if (OutputFolder is null || !Directory.Exists(OutputFolder)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{OutputFolder}\"") { UseShellExecute = true });
    }
}
