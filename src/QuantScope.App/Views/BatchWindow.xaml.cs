using System.ComponentModel;
using System.Windows;
using QuantScope.App.ViewModels;

namespace QuantScope.App.Views;

public partial class BatchWindow : Window
{
    public BatchWindow(BatchViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 実行中に閉じたら止める
        if (DataContext is BatchViewModel { IsRunning: true } vm) vm.CancelCommand.Execute(null);
        base.OnClosing(e);
    }
}
