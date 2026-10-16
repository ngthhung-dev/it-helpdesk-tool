using System.Globalization;

namespace ITHelpdeskTool.Core;

/// <summary>Sắp xếp chữ tiếng Việt A→Z: không phân biệt hoa/thường, bỏ qua dấu câu / ngoặc kép.</summary>
public static class ViText
{
    public static readonly StringComparer Comparer =
        StringComparer.Create(new CultureInfo("vi-VN"), CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols);

    public static IOrderedEnumerable<T> OrderByVi<T>(this IEnumerable<T> src, Func<T, string> key) => src.OrderBy(key, Comparer);
    public static IOrderedEnumerable<T> ThenByVi<T>(this IOrderedEnumerable<T> src, Func<T, string> key) => src.ThenBy(key, Comparer);
}
