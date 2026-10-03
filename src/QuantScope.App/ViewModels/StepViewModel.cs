using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;

namespace QuantScope.App.ViewModels;

/// <summary>手順の値 1 つ（スライダーと数の入力欄、または選択肢）</summary>
public sealed partial class ParamViewModel : ObservableObject
{
    private readonly StepViewModel _owner;

    public ParamViewModel(StepViewModel owner, ParamDef def, Raster? image)
    {
        _owner = owner;
        Def = def;
        var (min, max, _) = def.Resolve(image);
        Minimum = min;
        Maximum = Math.Max(max, min + 1e-9);
        Choices = def.Choices.Select((c, i) => new ChoiceViewModel(this, i, c)).ToList();
    }

    public ParamDef Def { get; }
    public string Label => Def.Label;
    public string? Unit => Def.Unit;
    public string? Help => Def.Help;
    public bool IsNumber => Def.Kind == ParamKind.Number;
    public bool IsChoice => Def.Kind == ParamKind.Choice;

    /// <summary>いま使われる値か（たとえば、しきい値は決め方が「手動」のときだけ）</summary>
    public bool IsShown => Def.IsUsed(_owner.Step);

    internal void RefreshShown() => OnPropertyChanged(nameof(IsShown));
    public double Minimum { get; }
    public double Maximum { get; }

    /// <summary>スライダーの刻み（整数の値は 1、画像の値の範囲は範囲の 1/1000 くらい）</summary>
    public double SmallChange => Def.Scale is ValueScale.ImageValue or ValueScale.ImageSpan ? NiceStep((Maximum - Minimum) / 500) : Def.Step;

    public IReadOnlyList<ChoiceViewModel> Choices { get; }

    public double Value
    {
        get => _owner.Step.Get(Def.Key);
        set
        {
            if (!double.IsFinite(value)) return;
            double v = Math.Clamp(value, Minimum, Maximum);
            // 選択肢・整数の値（半径・個数・px）は整数に、そのほかは刻みにそろえる
            if (Def.Kind != ParamKind.Number || Def.Scale == ValueScale.ImagePixels || (Def.Step >= 1 && Def.Scale == ValueScale.Absolute)) v = Math.Round(v);
            else v = Math.Round(v / SmallChange) * SmallChange;
            if (_owner.Step.Values.TryGetValue(Def.Key, out double old) && old.Equals(v)) return;
            _owner.Step.Values[Def.Key] = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
            foreach (var c in Choices) c.Refresh();
            _owner.NotifyChanged(this);
        }
    }

    /// <summary>入力欄の文字（小数点以下は値の大きさに合わせる）</summary>
    public string ValueText
    {
        get
        {
            double v = Value, range = Maximum - Minimum;
            string fmt = Def.Step >= 1 && Def.Scale == ValueScale.Absolute ? "0" : range >= 1000 ? "0" : range >= 10 ? "0.#" : "0.##";
            return v.ToString(fmt, CultureInfo.CurrentCulture);
        }
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ||
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                Value = v;
            OnPropertyChanged();
        }
    }

    public int SelectedIndex
    {
        get => (int)Math.Round(Value);
        set => Value = value;
    }

    /// <summary>外（ヒストグラムのドラッグなど）から値を入れたとき、画面を合わせる</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        foreach (var c in Choices) c.Refresh();
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / p;
        return (m < 2 ? 1 : m < 5 ? 2 : 5) * p;
    }
}

/// <summary>選択肢 1 つ（ラジオボタン）</summary>
public sealed partial class ChoiceViewModel(ParamViewModel owner, int index, string label) : ObservableObject
{
    public string Label { get; } = label;
    public string Group => owner.Def.Key + "_" + owner.GetHashCode().ToString(CultureInfo.InvariantCulture);

    public bool IsSelected
    {
        get => owner.SelectedIndex == index;
        set
        {
            if (value) owner.SelectedIndex = index;
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>手順 1 つ（一覧の行と、値の編集）</summary>
public sealed partial class StepViewModel : ObservableObject
{
    private readonly Action<StepViewModel, ParamViewModel?> _changed;

    public StepViewModel(Step step, Raster? image, Action<StepViewModel, ParamViewModel?> changed)
    {
        Step = step;
        Definition = StepCatalog.Get(step.Kind);
        _changed = changed;
        Parameters = new ObservableCollection<ParamViewModel>(Definition.Parameters.Select(p => new ParamViewModel(this, p, image)));
    }

    public Step Step { get; }
    public StepDefinition Definition { get; }
    public ObservableCollection<ParamViewModel> Parameters { get; }

    public string Title => Definition.Title;
    public string Category => StepCatalog.CategoryTitle(Definition.Category);
    public bool HasParameters => Parameters.Count > 0;
    public bool IsCrop => Step.Kind == "crop";
    public bool IsThreshold => Step.Kind == "threshold";

    [ObservableProperty]
    private int _number;

    /// <summary>実行したときの知らせ（しきい値など）</summary>
    [ObservableProperty]
    private string? _info;

    /// <summary>使えなかった理由など</summary>
    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private double _milliseconds;

    public bool Enabled
    {
        get => Step.Enabled;
        set
        {
            if (Step.Enabled == value) return;
            Step.Enabled = value;
            OnPropertyChanged();
            _changed(this, null);
        }
    }

    public ParamViewModel? Param(string key) => Parameters.FirstOrDefault(p => p.Def.Key == key);

    internal void NotifyChanged(ParamViewModel p)
    {
        _changed(this, p);
        foreach (var q in Parameters) q.RefreshShown();
    }

    public void SetOutcome(StepOutcome? o)
    {
        Info = o?.Info;
        Warning = o?.Warning;
        Milliseconds = o?.Elapsed.TotalMilliseconds ?? 0;
    }
}
