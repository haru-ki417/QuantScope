using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuantScope.Core.Ai;

namespace QuantScope.App.Services;

/// <summary>設定画面で入れる値</summary>
public sealed class UserSettings
{
    /// <summary>OpenAI の API キー（保存時に暗号化）</summary>
    public string? OpenAIApiKey { get; set; }

    public string OpenAIModel { get; set; } = ImageDescriber.DefaultModel;

    /// <summary>AI に画像を送ることの説明に同意したか</summary>
    public bool AiConsentAccepted { get; set; }

    /// <summary>最後に開いたフォルダー</summary>
    public string? LastFolder { get; set; }

    /// <summary>最近開いた画像（新しい順、この PC の中だけに保存する）</summary>
    public List<string> RecentFiles { get; set; } = [];

    public const int MaxRecent = 8;

    /// <summary>最近開いた画像の先頭に入れる（同じものは前へ移す）</summary>
    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(OpenAIApiKey);
}

/// <summary>
/// 設定を %LOCALAPPDATA%\QuantScope\settings.json に保存する。
/// API キーは Windows の DPAPI で暗号化し、同じ Windows のユーザーでしか元に戻せないようにする。
/// </summary>
public sealed class SettingsStore
{
    private const string ProtectedPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuantScope.UserSettings.v1");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(App.DataDirectory, "settings.json");
    }

    public string FilePath { get; }

    public UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new UserSettings();
        try
        {
            var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
            s.OpenAIApiKey = Unprotect(s.OpenAIApiKey);
            if (string.IsNullOrWhiteSpace(s.OpenAIModel)) s.OpenAIModel = ImageDescriber.DefaultModel;
            s.RecentFiles ??= [];
            return s;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException or IOException)
        {
            // 壊れている・別のユーザーの設定などで読めないときは、初めの状態にする
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stored = new UserSettings
        {
            OpenAIApiKey = Protect(settings.OpenAIApiKey),
            OpenAIModel = settings.OpenAIModel,
            AiConsentAccepted = settings.AiConsentAccepted,
            LastFolder = settings.LastFolder,
            RecentFiles = settings.RecentFiles.ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
        File.Move(temp, FilePath, overwrite: true); // 書いている途中で落ちても壊れないように、置き換える
    }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(encrypted);
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return null;
        byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
