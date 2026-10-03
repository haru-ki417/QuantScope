using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.Core.Pipeline;

/// <summary>読み込んだ画像と、その画像が持っていた縮尺（DICOM の画素間隔など。なければ null）</summary>
public sealed record LoadedImage(Raster Raster, Calibration? Calibration, string Description);

/// <summary>一括処理の 1 枚分の結果</summary>
public sealed record BatchItemResult(string File, AnalysisResult? Result, string? Error)
{
    public bool Succeeded => Result is not null;
}

public sealed record BatchProgress(int Done, int Total, string Current);

/// <summary>
/// 一括処理: 同じレシピを、たくさんの画像に同じようにかけて計測する。
/// 1 枚が読めなくても止めず、理由を残して次へ進む。
/// </summary>
public static class BatchProcessor
{
    public static async Task<IReadOnlyList<BatchItemResult>> RunAsync(
        IReadOnlyList<string> files,
        Func<string, LoadedImage> load,
        Recipe recipe,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(recipe);
        var results = new List<BatchItemResult>();
        for (int i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            string file = files[i];
            progress?.Report(new BatchProgress(i, files.Count, Path.GetFileName(file)));
            var item = await Task.Run(() => ProcessOne(file, load, recipe, cancel), cancel).ConfigureAwait(false);
            results.Add(item);
        }
        progress?.Report(new BatchProgress(files.Count, files.Count, ""));
        return results;
    }

    public static BatchItemResult ProcessOne(string file, Func<string, LoadedImage> load, Recipe recipe, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(recipe);
        string name = Path.GetFileName(file);
        try
        {
            var img = load(file);
            var cal = recipe.Calibration ?? img.Calibration ?? Calibration.Pixels;
            var run = PipelineRunner.Run(img.Raster, cal, recipe.Steps, null, cancel);
            var result = PipelineRunner.Analyze(run.Final, recipe.Measure);
            var warn = run.Outcomes.Select((o, k) => (o, k)).Where(t => t.o.Warning is not null).Select(t => $"手順 {t.k + 1}: {t.o.Warning}").FirstOrDefault();
            if (result is null) return new BatchItemResult(name, null, warn ?? "対象を選ぶ手順（二値化など）がありません");
            return new BatchItemResult(name, result, warn);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException)
        {
            return new BatchItemResult(name, null, ex.Message);
        }
    }

    public static string SummaryCsv(IEnumerable<BatchItemResult> results) =>
        CsvExport.Summaries(results.Select(r => (r.File, r.Result?.Summary, r.Error)));

    public static string ParticlesCsv(IEnumerable<BatchItemResult> results)
    {
        var ok = results.Where(r => r.Result is not null).ToList();
        var first = ok.FirstOrDefault()?.Result?.Summary;
        return CsvExport.Particles(ok.SelectMany(r => r.Result!.Particles.Select(p => ((string?)r.File, p))), first?.LengthUnit ?? "px", first?.AreaUnit ?? "px²", first?.IntensityLabel ?? "明るさ");
    }
}
