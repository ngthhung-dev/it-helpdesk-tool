using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>
/// Đoán CPU có thuộc dòng Windows 11 hỗ trợ không, theo tên CPU: Intel Core thế hệ 8+ / Core Ultra / Core 3·5·7 đời mới,
/// AMD Ryzen 2000+. Quy tắc gần đúng theo danh sách của Microsoft (có vài ngoại lệ, vd. một số CPU Intel thế hệ 7 dòng doanh nghiệp);
/// dòng khác (Pentium, Celeron, Xeon, Athlon…) trả null = cần tra danh sách chi tiết.
/// </summary>
public static class CpuSupport
{
    private static readonly Regex IntelCoreI = new(@"\bi[3579]-(\d{3,5})", RegexOptions.IgnoreCase);
    private static readonly Regex IntelNew = new(@"Core(\(TM\))?\s+(Ultra\b|[3579]\s+\d{3})", RegexOptions.IgnoreCase);
    private static readonly Regex Ryzen = new(@"Ryzen(\s+Threadripper(\s+PRO)?)?\s+(\d\s+)?(PRO\s+)?(\d)\d{3}", RegexOptions.IgnoreCase);
    private static readonly Regex RyzenNew = new(@"Ryzen\s+(AI\b|Z\d)", RegexOptions.IgnoreCase);

    public static (bool? Pass, string Note) Check(string name)
    {
        name ??= "";
        if (IntelNew.IsMatch(name)) return (true, "Intel Core Ultra / Core đời mới — thuộc dòng Windows 11 hỗ trợ.");
        var m = IntelCoreI.Match(name);
        if (m.Success)
        {
            var gen = IntelGeneration(m.Groups[1].Value);
            return gen >= 8
                ? (true, $"Intel Core thế hệ {gen} — thuộc dòng Windows 11 hỗ trợ (từ thế hệ 8).")
                : (false, $"Intel Core thế hệ {gen} — ngoài danh sách Windows 11 hỗ trợ (cần thế hệ 8 trở lên; vài mẫu thế hệ 7 dòng doanh nghiệp là ngoại lệ).");
        }
        if (RyzenNew.IsMatch(name)) return (true, "AMD Ryzen đời mới — thuộc dòng Windows 11 hỗ trợ.");
        var r = Ryzen.Match(name);
        if (r.Success)
        {
            var series = int.Parse(r.Groups[5].Value);
            return series >= 2
                ? (true, $"AMD Ryzen {series}000 series — thuộc dòng Windows 11 hỗ trợ (từ Ryzen 2000).")
                : (false, "AMD Ryzen 1000 series — ngoài danh sách Windows 11 hỗ trợ (cần Ryzen 2000 trở lên).");
        }
        return (null, "Đạt số nhân / xung nhịp; dòng CPU cần nằm trong danh sách Microsoft hỗ trợ (Intel thế hệ 8+, AMD Ryzen 2000+).");
    }

    /// <summary>
    /// Số model → thế hệ: 5 chữ số (12700) và 4 chữ số bắt đầu bằng 1 (1065G7, 1165G7, 1255U) lấy 2 chữ số đầu;
    /// 4 chữ số khác (8250U, 9700K) lấy chữ số đầu; 3 chữ số (i7-920) là thế hệ 1.
    /// </summary>
    public static int IntelGeneration(string model) => model.Length switch
    {
        5 => int.Parse(model[..2]),
        4 when model[0] == '1' => int.Parse(model[..2]),
        4 => model[0] - '0',
        _ => 1,
    };
}
