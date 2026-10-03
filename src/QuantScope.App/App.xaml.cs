using System.IO;
using System.Windows;
using System.Windows.Threading;
using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;
using QuantScope.App.Services;
using QuantScope.App.ViewModels;

namespace QuantScope.App;

public partial class App : Application
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuantScope");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        RegisterCodecs();

        // 見本の撮影では、設定（API キーなど）を読まない別の場所を使う
        int at = Array.IndexOf(e.Args, "--snapshots");
        var store = at >= 0 ? new SettingsStore(Path.Combine(Path.GetTempPath(), "quantscope-snapshot-settings.json")) : new SettingsStore();
        var vm = new MainViewModel(new WpfDialogs(), store);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        // 見本で画面を一通り開き、画像に保存して終わる: QuantScope.exe --snapshots フォルダー
        if (at >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string dir = at + 1 < e.Args.Length ? e.Args[at + 1] : Path.Combine(Environment.CurrentDirectory, "snapshots");
            try
            {
                await SnapshotRunner.RunAsync(window, vm, Path.GetFullPath(dir));
            }
            finally
            {
                Shutdown(0);
            }
            return;
        }

        window.StartMaximized();
        window.Show();
        // ファイルを指定して起動（エクスプローラーの「プログラムから開く」など）
        var path = e.Args.FirstOrDefault(a => File.Exists(a) || Directory.Exists(a));
        if (path is not null) await vm.OpenPathAsync(path);
    }

    /// <summary>圧縮された DICOM（JPEG・JPEG 2000・JPEG-LS など）を展開できるようにする</summary>
    private static void RegisterCodecs()
    {
        try
        {
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<NativeTranscoderManager>())
                .SkipValidation()
                .Build();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException or TypeInitializationException)
        {
            Log(ex); // 圧縮されていない DICOM はそのまま読める
        }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("予期しないエラーが起きました。作業は続けられます。\n\n" + e.Exception.Message, "QuantScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    internal static void Log(Exception ex)
    {
        try
        {
            string dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
