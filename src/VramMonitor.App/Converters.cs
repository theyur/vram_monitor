using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VramMonitor.App.ViewModels;

namespace VramMonitor.App;

/// <summary>Collapses an element when its bound string is empty.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows a row's detail block only while that row is expanded.
/// </summary>
/// <remarks>
/// Expansion state lives on the view model rather than on the row, so it survives the row objects being
/// rebuilt on every sampling cycle.
/// </remarks>
public sealed class ExpansionConverter : IValueConverter
{
    public static MainViewModel? Model { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Model?.IsExpanded(key) == true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
