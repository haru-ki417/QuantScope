using System.IO;
using System.Windows;
using System.Windows.Threading;
using QuantScope.App.ViewModels;
using QuantScope.App.Views;
using QuantScope.Core.Rendering;
using QuantScope.Core.Samples;
using QuantScope.Core.Imaging;

namespace QuantScope.App.Services;

/// <summary>
/// 見本で画面を一通り開き、画像に保存して終わる（README の画像づくりと、Windows での表示の確認）。
///   QuantScope.exe --snapshots フォルダー
/// </summary>
public static class SnapshotRunner
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, string dir)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(vm);
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        try
        {
            window.Width = 1600;
            window.Height = 960;
            window.Show();
            await Idle(800);
            Save(window, dir, "01-start.png", log);

            // 蛍光の核: 結果
            vm.OpenSampleCommand.Execute("nuclei");
            await Settle(vm);
            Save(window, dir, "02-nuclei-result.png", log);
            log.Add($"nuclei: {vm.SummaryCount} / {vm.SummaryMeanArea} / {vm.SummaryFraction}");

            // しきい値の手順を選び、分布を見る
            vm.SelectedStep = vm.Steps.First(s => s.IsThreshold);
            vm.RightTab = RightTab.Histogram;
            await Settle(vm);
            Save(window, dir, "03-threshold-histogram.png", log);

            // ウォーターシェッドのしくみ、粒ごとに色分け
            vm.SelectedStep = vm.Steps.First(s => s.Step.Kind == "watershed");
            vm.RightTab = RightTab.Explain;
            vm.ColorPerObject = true;
            await Settle(vm);
            Save(window, dir, "04-watershed-explain.png", log);
            vm.ColorPerObject = false;

            // 染色した組織: 色で選び、範囲の中の占有率
            vm.OpenSampleCommand.Execute("tissue");
            await Settle(vm);
            vm.Roi = Roi.Ellipse(140, 90, 560, 470);
            vm.RightTab = RightTab.Results;
            await Settle(vm);
            Save(window, dir, "05-tissue-roi.png", log);
            log.Add($"tissue: {vm.SummaryCount} / {vm.SummaryFraction}");

            // 形の見本: 粒を選ぶ・線で長さ
            vm.OpenSampleCommand.Execute("shapes");
            await Settle(vm);
            vm.SelectedParticle = vm.Particles.FirstOrDefault(p => p.Solidity < 0.8);
            vm.Line = new MeasureLine(new PointD(50, 110), new PointD(170, 110));
            vm.RightTab = RightTab.Profile;
            await Settle(vm);
            Save(window, dir, "06-shapes-line.png", log);

            // AI の画面（キーなし）
            vm.RightTab = RightTab.Ai;
            vm.Line = null;
            await Settle(vm);
            Save(window, dir, "07-ai.png", log);

            // 画像ファイルの読み書きと一括処理（Windows の画像の部品を使うところの確認）
            await CheckFilesAndBatch(window, vm, dir, log);

            // 設定の画面
            var settings = new SettingsWindow(new UserSettings()) { Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            settings.Show();
            await Idle(600);
            Save(settings, dir, "10-settings.png", log);
            settings.Close();
            log.Add("ok");
        }
        catch (Exception ex)
        {
            log.Add("ERROR: " + ex);
        }
        finally
        {
            try
            {
                await File.WriteAllLinesAsync(Path.Combine(dir, "snapshots.log"), log);
            }
            catch (IOException)
            {
            }
            window.Close();
        }
    }

    private static async Task CheckFilesAndBatch(MainWindow window, MainViewModel vm, string dir, List<string> log)
    {
        string folder = Path.Combine(Path.GetTempPath(), "quantscope-snapshot-batch");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        foreach (var id in new[] { "tissue", "shapes" })
        {
            var s = SampleImages.Create(id);
            ImageIo.SavePng(Path.Combine(folder, id + ".png"), s.Image.Width, s.Image.Height, DisplayRenderer.ToBgra(s.Image, ColorMap.Gray));
        }
        File.WriteAllText(Path.Combine(folder, "broken.png"), "not an image");

        // 保存した PNG を開き直す（見本の組織のレシピのまま）
        vm.OpenSampleCommand.Execute("tissue");
        await Settle(vm);
        string before = vm.SummaryCount;
        await vm.OpenPathAsync(Path.Combine(folder, "tissue.png"));
        await Settle(vm);
        log.Add($"reopen png: {vm.ImageDescription} count {before} -> {vm.SummaryCount}");
        Save(window, dir, "08-reopened-png.png", log);

        var batch = new BatchViewModel(vm.Recipe.Clone(), vm.Calibration, folder, new WpfDialogs(), vm.Settings);
        var bw = new BatchWindow(batch) { Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        bw.Show();
        await batch.RunCommand.ExecuteAsync(null);
        await Idle(600);
        Save(bw, dir, "09-batch.png", log);
        bw.Close();
        foreach (var r in batch.Rows) log.Add($"batch: {r.File} count={r.Count} fraction={r.Fraction} note={r.Note}");
        var outputs = Directory.Exists(batch.OutputFolder!) ? Directory.GetFiles(batch.OutputFolder!).Select(Path.GetFileName) : [];
        log.Add("batch outputs: " + string.Join(", ", outputs));
        log.Add(batch.ProgressText);
    }

    private static async Task Settle(MainViewModel vm)
    {
        await Idle(300);
        await vm.RunAsync();
        vm.RenderView();
        await Idle(900);
    }

    private static async Task Idle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string dir, string name, List<string> log)
    {
        if (window.Content is not FrameworkElement root) return;
        MainWindow.SavePng(root, Path.Combine(dir, name));
        log.Add(name);
    }
}
