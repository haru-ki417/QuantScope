using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuantScope.App.Controls;

/// <summary>列挙の値が ConverterParameter と同じなら true（ラジオボタン用。true になったらその値に戻す）</summary>
public sealed class EnumEquals : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string s && string.Equals(value.ToString(), s, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

/// <summary>true / 空でない文字列 / null でない / 0 より大きい数 → 表示（ConverterParameter=Invert で反対）</summary>
public sealed class Visible : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>数を表示用の文字に（MainViewModel.Fmt と同じ桁）</summary>
public sealed class NumberText : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? ViewModels.MainViewModel.Fmt(d) : value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>列挙の値が ConverterParameter と同じなら表示（右のパネルの切り替え）</summary>
public sealed class EnumVisible : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string s && string.Equals(value.ToString(), s, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>文字が ConverterParameter と同じなら true（単位のラジオボタン用）</summary>
public sealed class StringEquals : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string v && parameter is string s && string.Equals(v, s, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? s : Binding.DoNothing;
}

/// <summary>true なら "Selected"（左の列の「元の画像」「計測」の行の強調）</summary>
public sealed class SelectedTag : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "Selected" : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool が ConverterParameter（"True" / "False"）と同じなら true（ラジオボタン用）</summary>
public sealed class BoolIs : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && parameter is string s && b == bool.Parse(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? bool.Parse(s) : Binding.DoNothing;
}

/// <summary>陽性・陰性の文字（判定しないときは空）</summary>
public sealed class PositiveText : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { true => "陽性", false => "陰性", _ => "" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
