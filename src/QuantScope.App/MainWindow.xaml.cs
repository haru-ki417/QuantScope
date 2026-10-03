using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuantScope.App.ViewModels;
using QuantScope.App.Views;

namespace QuantScope.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKey;
        Viewer.HoverChanged += (_, p) => _vm?.UpdateHover(p);
        Viewer.Clicked += (_, p) => _vm?.SelectParticleAt(p);
        Viewer.ExcludeClicked += (_, p) => _vm?.ToggleExcludeAt(p);
        Histo.MarkerDragged += (_, v) => _vm?.SetThresholdFromHistogram(v);
        PositiveChart.MarkerDragged += (_, v) => _vm?.SetPositiveFromChart(v);
        FeatureChart.MarkerDragged += (_, v) => _vm?.SetPositiveFromChart(v);
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.SettingsRequested -= OnSettingsRequested;
            _vm.BatchRequested -= OnBatchRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.SettingsRequested += OnSettingsRequested;
            _vm.BatchRequested += OnBatchRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    /// <summary>表の見出しに単位と「何を測ったか」を入れ、判定の列は陽性を分けるときだけ出す</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm is null || e.PropertyName != nameof(MainViewModel.Analysis)) return;
        var s = _vm.Analysis?.Summary;
        AreaColumn.Header = s is null ? "面積" : $"面積 {s.AreaUnit}";
        FeretColumn.Header = s is null ? "フェレ径" : $"フェレ径 {s.LengthUnit}";
        IntensityColumn.Header = s is null ? "明るさ" : s.IntensityLabel;
        PositiveColumn.Visibility = s?.Positive is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// ふつうに起動したとき: 最大化して開く。最大化を戻したときも、画面（作業領域）からはみ出さない大きさにする。
    /// </summary>
    public void StartMaximized()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.92));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.92));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var w = new SettingsWindow(_vm.Settings) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _vm.SaveSettings();
            _vm.ReloadSettings();
        }
    }

    private void OnBatchRequested(object? sender, string? folder)
    {
        if (_vm is null) return;
        var w = new BatchWindow(new BatchViewModel(_vm.Recipe.Clone(), _vm.Calibration, folder, new Services.WpfDialogs(), _vm.Settings)) { Owner = this };
        w.ShowDialog();
        _vm.SaveSettings();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await _vm.OpenPathAsync(paths[0]);
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        bool typing = Keyboard.FocusedElement is TextBox;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    _vm.OpenCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Z when !typing:
                    _vm.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Y when !typing:
                    _vm.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.S:
                    _vm.SaveCsvCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }
        if (typing || mods != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.F:
                Viewer.Fit();
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                Viewer.ZoomTo(1);
                e.Handled = true;
                break;
            case Key.V:
                _vm.Tool = Tool.Pan;
                break;
            case Key.R:
                _vm.Tool = Tool.Rectangle;
                break;
            case Key.E:
                _vm.Tool = Tool.Ellipse;
                break;
            case Key.P:
                _vm.Tool = Tool.Polygon;
                break;
            case Key.L:
                _vm.Tool = Tool.Line;
                break;
            case Key.X:
                _vm.Tool = Tool.Exclude;
                break;
            case Key.C:
                _vm.IsComparing = !_vm.IsComparing;
                break;
        }
    }

    private void OnStepListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm?.SelectedStep is not null)
        {
            _vm.RemoveStepCommand.Execute(_vm.SelectedStep);
            e.Handled = true;
        }
    }

    /// <summary>数の入力欄で Enter を押したら確定する</summary>
    private void OnCommitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    // メニューの外（開くボタン）をクリックして閉じたとき、同じクリックでまた開かないようにする
    private DateTime _popupClosedAt;

    private void OnPopupClosed(object? sender, EventArgs e) => _popupClosedAt = DateTime.UtcNow;

    private bool JustClosed => (DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250;

    private void OnToggleSampleMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && !JustClosed) _vm.IsSampleMenuOpen = !_vm.IsSampleMenuOpen;
    }

    private void OnToggleSaveMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && !JustClosed) _vm.IsSaveMenuOpen = !_vm.IsSaveMenuOpen;
    }

    private void OnToggleOpenMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && !JustClosed) _vm.IsOpenMenuOpen = !_vm.IsOpenMenuOpen;
    }

    private void OnToggleAddMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && !JustClosed) _vm.IsAddMenuOpen = !_vm.IsAddMenuOpen;
    }

    private void OnStepSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepList.SelectedItem is not null) StepList.ScrollIntoView(StepList.SelectedItem);
    }

    private void OnFit(object sender, RoutedEventArgs e) => Viewer.Fit();

    private void OnActualSize(object sender, RoutedEventArgs e) => Viewer.ZoomTo(1);

    private void OnCloseCalibration(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.IsCalibrating = false;
    }

    private void OnParticleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ParticleGrid.SelectedItem is not QuantScope.Core.Analysis.Particle p) return;
        ParticleGrid.ScrollIntoView(p);
        // 表で選んだとき（画像の上で選んだときではなく）は、その粒を画像の中央に出す
        if (ParticleGrid.IsKeyboardFocusWithin) Viewer.CenterOn(p);
    }

    private void OnParticleGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm?.SelectedParticle is not null)
        {
            _vm.ExcludeSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCenterSelected(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedParticle is { } p) Viewer.CenterOn(p);
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.SelectedParticle = null;
    }

    /// <summary>画面（要素）を PNG に保存する（見本の撮影用）</summary>
    internal static void SavePng(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
