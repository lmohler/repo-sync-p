using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Converters;

/// <summary>Visible when the value is "something": true, a non-zero number, a non-empty string or list, a non-null
/// object. ConverterParameter="invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when every bound value's ToString() is the same non-empty text (e.g. a button's Uid and the repo's next-step action).</summary>
public sealed class AllEqualConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var first = values.Length > 0 ? values[0]?.ToString() : null;
        return !string.IsNullOrEmpty(first) && values.All(v => string.Equals(v?.ToString(), first, StringComparison.OrdinalIgnoreCase));
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when value.ToString() equals the parameter; ConvertBack returns the parameter when checked
/// (for binding a group of RadioButtons to one string/enum property).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime d ? TimeText.Relative(d) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The exact local time behind a relative date, for tooltips ("Tue 2026-10-27 14:05").</summary>
public sealed class ExactTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime d && d != DateTime.MinValue ? d.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm") : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Tree depth (int) to a left margin, so a file tree row sits under its folder.</summary>
public sealed class TreeIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * 16 : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>For SelectedItem bindings on a file tree list: only file rows go back to the view model, folder rows are ignored.</summary>
public sealed class FileRowOnlyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or Models.ITreeFile ? value : Binding.DoNothing;
}
