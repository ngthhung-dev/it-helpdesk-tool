using System.Globalization;
using System.Management;

namespace ITHelpdeskTool.Services;

/// <summary>Một dòng kết quả WMI, đã chép hết thuộc tính ra để dùng ngoài COM.</summary>
public sealed class WmiRow(Dictionary<string, object?> props)
{
    public object? this[string name] => props.TryGetValue(name, out var v) ? v : null;

    public string Str(string name)
    {
        var v = this[name];
        return v switch
        {
            null => "",
            string s => s.Trim().TrimEnd('\0'),
            ushort[] chars => new string(chars.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim(),
            string[] arr => string.Join(", ", arr.Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "",
        };
    }

    public long Long(string name)
    {
        try { return this[name] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; }
        catch { return 0; }
    }

    public bool? Bool(string name) => this[name] is bool b ? b : null;

    public string[] Arr(string name) => this[name] switch
    {
        string[] a => a,
        string s => [s],
        _ => [],
    };

    public ushort[] UShorts(string name) => this[name] as ushort[] ?? [];

    /// <summary>Ngày giờ dạng DMTF (20260101093000.000000+420) → DateTime.</summary>
    public DateTime? Date(string name)
    {
        var s = Str(name);
        if (s.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
    }
}

/// <summary>Truy vấn WMI có giới hạn thời gian, chạy trên luồng gọi (nên gọi từ luồng nền).</summary>
public static class Wmi
{
    public static List<WmiRow> Query(string wql, string scope = @"root\cimv2", int timeoutSec = 20)
    {
        var ms = new ManagementScope(scope);
        ms.Connect();
        var opts = new EnumerationOptions
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec),
            ReturnImmediately = true,
            Rewindable = false,
        };
        using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql), opts);
        var list = new List<WmiRow>();
        foreach (var o in searcher.Get())
        {
            using (o)
            {
                var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in o.Properties) d[p.Name] = p.Value;
                list.Add(new WmiRow(d));
            }
        }
        return list;
    }

    /// <summary>Như Query nhưng lỗi (namespace không có, thiếu quyền...) thì trả danh sách rỗng.</summary>
    public static List<WmiRow> TryQuery(string wql, string scope = @"root\cimv2", int timeoutSec = 20)
    {
        try { return Query(wql, scope, timeoutSec); }
        catch (Exception ex)
        {
            Log.Warn($"WMI lỗi [{scope}] {wql}: {ex.Message}");
            return [];
        }
    }

    public static WmiRow? First(string wql, string scope = @"root\cimv2") => TryQuery(wql, scope).FirstOrDefault();
}

/// <summary>Định dạng số liệu hiển thị.</summary>
public static class Fmt
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static string Size(double bytes)
    {
        if (bytes <= 0) return "";
        const double K = 1024;
        return bytes switch
        {
            >= K * K * K * K => $"{bytes / (K * K * K * K):0.##} TB",
            >= K * K * K => $"{bytes / (K * K * K):0.#} GB",
            >= K * K => $"{bytes / (K * K):0} MB",
            _ => $"{bytes / K:0} KB",
        };
    }

    public static string Date(DateTime? d) => d is { } x ? x.ToString("dd/MM/yyyy", Vi) : "";
    public static string Stamp(DateTime? d) => d is { } x ? x.ToString("dd/MM/yyyy HH:mm", Vi) : "";
    public static string Num(double n, string format = "N0") => n.ToString(format, Vi);

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} ngày {t.Hours} giờ" : $"{t.Hours} giờ {t.Minutes} phút";

    public static string YesNo(bool? b, string yes = "Bật", string no = "Tắt") => b switch
    {
        true => yes,
        false => no,
        _ => "Không rõ",
    };
}
