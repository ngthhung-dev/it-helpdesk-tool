using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ITHelpdeskTool.Core;

/// <summary>true → Visible. Đặt Invert=True để đảo.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        (value is Visibility.Visible) ^ Invert;
}

/// <summary>null → Collapsed (Invert để ngược lại).</summary>
public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is not null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>So sánh số với ngưỡng, dùng trong DataTrigger.</summary>
public sealed class Thresholds(double limit) : IValueConverter
{
    public static readonly Thresholds Over90 = new(90);

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is double d && d >= limit;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Đảo bool.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Số &gt; 0 → Visible.</summary>
public sealed class CountToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Các converter dùng chung qua x:Static.</summary>
public static class Converters
{
    public static readonly NotConverter Not = new();
    public static readonly CountToVisibility CountVis = new();
}

/// <summary>Chuỗi rỗng → "—".</summary>
public sealed class EmptyToDash : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? s : "—";

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
