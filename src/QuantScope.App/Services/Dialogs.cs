using System.Windows;
using Microsoft.Win32;

namespace QuantScope.App.Services;

/// <summary>ファイルの選択や確認のダイアログ（見本の撮影では出さずに進められるよう、ここにまとめる）</summary>
public interface IDialogs
{
    string? OpenFile(string title, string filter, string? folder);
    string? SaveFile(string title, string filter, string fileName, string? folder);
    string? PickFolder(string title, string? folder);
    bool Confirm(string message, string title);
    void Info(string message, string title);
}

public sealed class WpfDialogs : IDialogs
{
    private static Window? Owner => Application.Current?.MainWindow;

    public string? OpenFile(string title, string filter, string? folder)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter };
        if (folder is not null) d.InitialDirectory = folder;
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? SaveFile(string title, string filter, string fileName, string? folder)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, AddExtension = true };
        if (folder is not null) d.InitialDirectory = folder;
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? PickFolder(string title, string? folder)
    {
        var d = new OpenFolderDialog { Title = title };
        if (folder is not null) d.InitialDirectory = folder;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public bool Confirm(string message, string title) =>
        (Owner is { } o
            ? MessageBox.Show(o, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;

    public void Info(string message, string title)
    {
        if (Owner is { } o) MessageBox.Show(o, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
