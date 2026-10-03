using System.Windows;
using QuantScope.App.Services;
using QuantScope.Core.Ai;

namespace QuantScope.App.Views;

public partial class SettingsWindow : Window
{
    private readonly UserSettings _settings;
    private bool _clearKey;

    public SettingsWindow(UserSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ModelBox.Text = settings.OpenAIModel;
        ConsentBox.IsChecked = !settings.AiConsentAccepted;
        // キーそのものは画面に出さない（入っているかどうかだけ）
        KeyState.Text = settings.HasApiKey ? "キーは保存されています。変えるときだけ、新しいキーを入れてください。" : "キーはまだ入っていません。";
        PathText.Text = "設定とエラーの記録は次の場所にあります:\n" + App.DataDirectory;
    }

    private void OnClearKey(object sender, RoutedEventArgs e)
    {
        _clearKey = true;
        KeyBox.Clear();
        KeyState.Text = "保存すると、キーを消します。";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password.Trim();
        if (key.Length > 0) _settings.OpenAIApiKey = key;
        else if (_clearKey) _settings.OpenAIApiKey = null;
        _settings.OpenAIModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? ImageDescriber.DefaultModel : ModelBox.Text.Trim();
        _settings.AiConsentAccepted = ConsentBox.IsChecked != true;
        DialogResult = true;
    }
}
