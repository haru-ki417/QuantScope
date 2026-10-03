using System.Globalization;
using System.Text;

namespace QuantScope.Core.Pipeline;

/// <summary>
/// 手順の 1 つ（どの処理を、どの値で行うか）。レシピとして JSON に保存できる。
/// 値はすべて数で持ち、選択肢は 0 から始まる番号、オン・オフは 0 / 1。
/// </summary>
public sealed class Step
{
    public Step(string kind, IDictionary<string, double>? values = null, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
        Enabled = enabled;
        Values = values is null ? [] : new Dictionary<string, double>(values);
    }

    public string Kind { get; }
    public bool Enabled { get; set; }
    public Dictionary<string, double> Values { get; }

    public double Get(string key)
    {
        if (Values.TryGetValue(key, out double v)) return v;
        // 値が保存されていなければ、8bit の画像を前提にした既定値
        var p = StepCatalog.Get(Kind).Parameters.FirstOrDefault(p => p.Key == key);
        return p?.Resolve(null).Default ?? 0;
    }

    public int GetInt(string key) => (int)Math.Round(Get(key));
    public bool GetBool(string key) => Get(key) >= 0.5;

    public Step Clone() => new(Kind, Values, Enabled);

    /// <summary>同じ処理・同じ値かを比べるための文字列（途中までの結果を使い回すのに使う）</summary>
    public string Signature()
    {
        var sb = new StringBuilder(Kind).Append(Enabled ? "|on" : "|off");
        foreach (var kv in Values.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
