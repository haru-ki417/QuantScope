namespace QuantScope.Web.State;

/// <summary>キー操作から画像の表示を動かすための入り口（画像の表示の部品が登録する）</summary>
public static class ViewerCommands
{
    public static Action? Fit { get; set; }
    public static Action? Actual { get; set; }
    public static Action<double, double, double>? CenterOn { get; set; }
}
