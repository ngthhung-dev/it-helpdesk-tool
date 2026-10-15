using System.Globalization;
using System.Windows.Data;
using ITHelpdeskTool.Services.Fonts;

namespace ITHelpdeskTool.Core;

/// <summary>
/// [chữ mẫu, tên họ font] → chữ mẫu đã đổi sang bảng mã TCVN3 / VNI nếu là font mã cũ (.VnTime, VNI-Times…),
/// để khung xem trước hiện đúng dấu tiếng Việt thay vì ký tự lạ.
/// </summary>
public sealed class LegacySampleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Length > 0 ? values[0] as string ?? "" : "";
        var family = values.Length > 1 ? values[1] as string ?? "" : "";
        return LegacyVnEncoding.Convert(text, family);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
