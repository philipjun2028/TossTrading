using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TossTrading.App.Converters;

/// <summary>숫자 부호 → 색 (국내 관례: + 빨강, − 파랑)</summary>
public sealed class SignBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var d = value switch
        {
            decimal m => m,
            double x => (decimal)x,
            int i => i,
            _ => 0m,
        };
        var key = d > 0 ? "Up" : d < 0 ? "Down" : "Neutral";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>TimeOnly ↔ "HH:mm"</summary>
public sealed class TimeOnlyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TimeOnly t ? t.ToString("HH\\:mm", CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        TimeOnly.TryParse(value as string, CultureInfo.InvariantCulture, out var t) ? t : DependencyProperty.UnsetValue;
}

/// <summary>−1~1 비율 → 0~100 (목표 진행률 바)</summary>
public sealed class ProgressConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is decimal d ? (double)Math.Clamp(d, 0, 1) * 100.0 : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
