using ITHelpdeskTool.Core;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;
using static ITHelpdeskTool.Services.ProcessRunner;

namespace ITHelpdeskTool.Services.Power;

public sealed record PowerPlan(string Guid, string Name, bool IsActive)
{
    public bool IsBuiltIn => PowerService.BuiltIn.ContainsKey(Guid);
    public bool IsBalanced => Guid == PowerService.Balanced;
}

/// <summary>Kiểu giá trị của một thiết lập nguồn → quyết định danh sách lựa chọn.</summary>
public enum PowerValueKind { Seconds, Action, PowerButton, Percent, OnOff, Aspm }

/// <summary>Một thiết lập trong gói nguồn (GUID nhóm + GUID thiết lập của Windows).</summary>
public sealed record PowerSettingDef(string Key, string Title, string Hint, string Sub, string Setting, PowerValueKind Kind, bool LaptopOnly = false);

public sealed record PowerValue(int Ac, int Dc);

public sealed record WakeDevice(string Name, bool Armed);

/// <summary>
/// Quản lý gói nguồn qua powercfg. Đọc dữ liệu chỉ dựa vào GUID và số hex nên không phụ thuộc
/// ngôn ngữ Windows (chữ trong output của powercfg bị dịch theo ngôn ngữ máy).
/// </summary>
public static class PowerService
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static readonly Dictionary<string, string> BuiltIn = new()
    {
        [Balanced] = "Balanced",
        [HighPerf] = "High performance",
        [PowerSaver] = "Power saver",
        [Ultimate] = "Ultimate Performance",
    };

    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex HexRx = new(@"0x([0-9a-fA-F]{1,8})\b", RegexOptions.Compiled);

    // ================= Thiết lập hay dùng =================
    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string SubButtons = "4f971e89-eebd-4455-a8de-9e59040e7347";
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SubPci = "501a4d13-42af-4429-9fd1-a8218c268e20";

    public static IReadOnlyList<PowerSettingDef> Settings { get; } =
    [
        new("display", "Tắt màn hình sau", "Không dùng máy bao lâu thì tắt màn hình.", SubVideo, "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", PowerValueKind.Seconds),
        new("sleep", "Cho máy ngủ (Sleep) sau", "Máy bàn chạy phần mềm chấm công, camera, chia sẻ máy in nên để “Không bao giờ”.", SubSleep, "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", PowerValueKind.Seconds),
        new("hibernate", "Ngủ đông (Hibernate) sau", "Lưu toàn bộ phiên làm việc ra ổ cứng rồi tắt hẳn — đỡ hao pin khi để lâu.", SubSleep, "9d7815a6-7ee4-497e-8888-515a05f02364", PowerValueKind.Seconds),
        new("lid", "Khi gập màn hình", "Gắn màn hình ngoài và gập laptop lại thì chọn “Không làm gì”.", SubButtons, "5ca83367-6e45-459f-a27b-476b1d01c936", PowerValueKind.Action, LaptopOnly: true),
        new("button", "Khi bấm nút nguồn", "", SubButtons, "7648efa3-dd9c-4e3e-b566-50f929386280", PowerValueKind.PowerButton),
        new("cpumax", "CPU tối đa", "Đặt 99% để tắt Turbo Boost: máy mát và êm hơn, giảm hiệu năng khoảng 20–30%.", SubProcessor, "bc5038f7-23e0-4960-96da-33abaf5935ec", PowerValueKind.Percent),
        new("cpumin", "CPU tối thiểu", "Để thấp (5%) cho tiết kiệm điện; 100% luôn chạy hết công suất.", SubProcessor, "893dee8e-2bef-41e0-89c6-b55d0929964c", PowerValueKind.Percent),
        new("disk", "Tắt ổ cứng sau", "Chỉ ảnh hưởng ổ HDD; nên để lâu hoặc “Không bao giờ” nếu ổ hay bị đánh thức.", SubDisk, "6738e2c4-e8a5-4a42-b16a-e040e769756e", PowerValueKind.Seconds),
        new("usb", "Tạm ngắt cổng USB khi rảnh", "Tắt nếu chuột/bàn phím/USB hay bị mất kết nối, máy quét mã vạch không nhận.", SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", PowerValueKind.OnOff),
        new("pcie", "Tiết kiệm điện PCI Express", "Tắt khi card mạng/Wi-Fi/card đồ hoạ hay chập chờn.", SubPci, "ee12f906-d277-404b-b6da-e5fa1a576df5", PowerValueKind.Aspm),
    ];

    /// <summary>Danh sách lựa chọn (giá trị, chữ hiển thị) theo kiểu thiết lập.</summary>
    public static IReadOnlyList<KeyValuePair<int, string>> Options(PowerValueKind kind) => kind switch
    {
        PowerValueKind.Seconds =>
        [
            new(60, "1 phút"), new(120, "2 phút"), new(180, "3 phút"), new(300, "5 phút"), new(600, "10 phút"),
            new(900, "15 phút"), new(1200, "20 phút"), new(1800, "30 phút"), new(2700, "45 phút"), new(3600, "1 giờ"),
            new(7200, "2 giờ"), new(10800, "3 giờ"), new(18000, "5 giờ"), new(0, "Không bao giờ"),
        ],
        PowerValueKind.Action => [new(0, "Không làm gì"), new(1, "Ngủ (Sleep)"), new(2, "Ngủ đông (Hibernate)"), new(3, "Tắt máy")],
        PowerValueKind.PowerButton => [new(0, "Không làm gì"), new(1, "Ngủ (Sleep)"), new(2, "Ngủ đông (Hibernate)"), new(3, "Tắt máy"), new(4, "Tắt màn hình")],
        PowerValueKind.Percent => [new(100, "100%"), new(99, "99% (tắt Turbo)"), new(90, "90%"), new(80, "80%"), new(70, "70%"), new(50, "50%"), new(20, "20%"), new(5, "5%")],
        PowerValueKind.OnOff => [new(1, "Bật"), new(0, "Tắt")],
        _ => [new(0, "Tắt"), new(1, "Vừa phải"), new(2, "Tiết kiệm tối đa")],
    };

    public static string Describe(PowerValueKind kind, int v)
    {
        var o = Options(kind).FirstOrDefault(x => x.Key == v);
        if (o.Value is not null) return o.Value;
        return kind switch
        {
            PowerValueKind.Seconds => v % 3600 == 0 ? $"{v / 3600} giờ" : v % 60 == 0 ? $"{v / 60} phút" : $"{v} giây",
            PowerValueKind.Percent => $"{v}%",
            _ => v.ToString(),
        };
    }

    // ================= Gói nguồn =================
    public static async Task<List<PowerPlan>> ListAsync()
    {
        // Ưu tiên API powrprof (không phụ thuộc chữ/ngôn ngữ/định dạng output của powercfg)
        try
        {
            var api = await Task.Run(ListViaApi);
            if (api.Count > 0) return api;
        }
        catch (Exception ex) { Log.Warn($"Power API: {ex.Message}"); }

        var r = await ConsoleAsync("powercfg.exe", "/list");
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var list = new List<PowerPlan>();
        foreach (var line in r.Output.Split('\n'))
        {
            var m = GuidRx.Match(line);
            if (!m.Success) continue;
            var guid = m.Value.ToLowerInvariant();
            var open = line.IndexOf('(', m.Index + m.Length);
            var close = line.LastIndexOf(')');
            var name = open > 0 && close > open ? line[(open + 1)..close].Trim() : FriendlyName(guid) ?? BuiltIn.GetValueOrDefault(guid, guid);
            list.Add(new PowerPlan(guid, name, line.TrimEnd().EndsWith('*')));
        }
        return list;
    }

    // ================= API powrprof.dll =================
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint flags, uint index, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr user, out IntPtr active);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    /// <summary>Danh sách gói nguồn qua PowerEnumerate + tên qua PowerReadFriendlyName.</summary>
    public static List<PowerPlan> ListViaApi()
    {
        var active = ActiveScheme();
        var list = new List<PowerPlan>();
        for (uint i = 0; ; i++)
        {
            var buf = new byte[16];
            uint size = 16;
            var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /*ACCESS_SCHEME*/, i, buf, ref size);
            if (rc == 259 /*ERROR_NO_MORE_ITEMS*/) break;
            if (rc != 0) throw new InvalidOperationException($"PowerEnumerate lỗi {rc}");
            var g = new Guid(buf).ToString();
            list.Add(new PowerPlan(g, FriendlyName(g) ?? BuiltIn.GetValueOrDefault(g, g), g == active));
        }
        return list;
    }

    public static string? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p).ToString(); }
        finally { LocalFree(p); }
    }

    /// <summary>Tên gói nguồn (theo ngôn ngữ Windows), null nếu không đọc được.</summary>
    public static string? FriendlyName(string scheme)
    {
        try
        {
            var g = Guid.Parse(scheme);
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0) return null;
            var buf = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return null;
            var name = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0').Trim();
            return name.Length == 0 ? null : name;
        }
        catch { return null; }
    }

    /// <summary>Giá trị AC/DC đang có hiệu lực của một thiết lập (null nếu máy không có thiết lập này).</summary>
    public static PowerValue? ReadValue(string scheme, string sub, string setting)
    {
        var g = Guid.Parse(scheme); var s = Guid.Parse(sub); var t = Guid.Parse(setting);
        if (PowerReadACValueIndex(IntPtr.Zero, ref g, ref s, ref t, out var ac) != 0) return null;
        if (PowerReadDCValueIndex(IntPtr.Zero, ref g, ref s, ref t, out var dc) != 0) dc = ac;
        return new PowerValue((int)ac, (int)dc);
    }

    public static async Task<OpResult> SetActiveAsync(PowerPlan p)
    {
        var r = await ConsoleAsync("powercfg.exe", $"/setactive {p.Guid}");
        return OpResult.From(r, $"Đang dùng gói nguồn: {p.Name}.", "Không đổi được gói nguồn");
    }

    /// <summary>Tạo bản sao gói High performance / Ultimate (máy laptop mới thường bị ẩn).</summary>
    public static async Task<(OpResult Result, string? Guid)> AddBuiltInAsync(string template)
    {
        var r = await ConsoleAsync("powercfg.exe", $"/duplicatescheme {template}");
        var m = GuidRx.Match(r.Output);
        if (!r.Success || !m.Success)
            return (OpResult.Fail($"Không thêm được gói: {OpResult.FirstLine(r.Text)}"), null);
        var name = BuiltIn.GetValueOrDefault(template, "gói nguồn");
        return (OpResult.Success($"Đã thêm gói {name}."), m.Value.ToLowerInvariant());
    }

    public static async Task<OpResult> RenameAsync(PowerPlan p, string name)
    {
        name = name.Trim().Replace("\"", "");
        if (name.Length == 0) return OpResult.Fail("Tên không được để trống.");
        var r = await ConsoleAsync("powercfg.exe", $"/changename {p.Guid} \"{name}\"");
        return OpResult.From(r, $"Đã đổi tên thành {name}.", "Không đổi được tên");
    }

    public static async Task<OpResult> DeleteAsync(PowerPlan p)
    {
        if (p.IsActive) return OpResult.Fail("Không xoá được gói đang dùng — chuyển sang gói khác trước.");
        if (p.IsBalanced) return OpResult.Fail("Không xoá gói Balanced mặc định của Windows.");
        var r = await ConsoleAsync("powercfg.exe", $"/delete {p.Guid}");
        return OpResult.From(r, $"Đã xoá gói {p.Name}.", "Không xoá được");
    }

    public static async Task<OpResult> ExportAsync(PowerPlan p, string file)
    {
        var r = await ConsoleAsync("powercfg.exe", $"/export \"{file}\" {p.Guid}");
        return File.Exists(file) ? OpResult.Success($"Đã xuất gói {p.Name} ra {Path.GetFileName(file)}.")
                                 : OpResult.Fail($"Không xuất được: {OpResult.FirstLine(r.Text)}");
    }

    public static async Task<(OpResult Result, string? Guid)> ImportAsync(string file)
    {
        if (!File.Exists(file)) return (OpResult.Fail("Không thấy file."), null);
        var r = await ConsoleAsync("powercfg.exe", $"/import \"{file}\"");
        var m = GuidRx.Match(r.Output);
        return r.Success && m.Success
            ? (OpResult.Success($"Đã nhập gói nguồn từ {Path.GetFileName(file)}."), m.Value.ToLowerInvariant())
            : (OpResult.Fail($"Không nhập được: {OpResult.FirstLine(r.Text)}"), null);
    }

    public static async Task<OpResult> RestoreDefaultsAsync()
    {
        var r = await ConsoleAsync("powercfg.exe", "-restoredefaultschemes");
        return OpResult.From(r, "Đã khôi phục các gói nguồn mặc định của Windows (gói tự tạo đã bị xoá).", "Không khôi phục được");
    }

    // ================= Đọc / ghi thiết lập =================
    /// <summary>
    /// Đọc giá trị AC/DC của các thiết lập trong một gói. Mỗi khối thiết lập trong output của powercfg /qh
    /// bắt đầu bằng dòng có GUID; hai số hex cuối cùng của khối là giá trị đang đặt khi cắm điện và khi dùng pin.
    /// </summary>
    public static async Task<Dictionary<string, PowerValue>> ReadSettingsAsync(string planGuid)
    {
        // API trước — không phụ thuộc định dạng output powercfg (đổi theo bản Windows / ngôn ngữ)
        var map = await Task.Run(() =>
        {
            var d = new Dictionary<string, PowerValue>();
            foreach (var s in Settings)
                try { if (ReadValue(planGuid, s.Sub, s.Setting) is { } v) d[s.Key] = v; } catch { }
            return d;
        });
        if (map.Count > 0) return map;
        Log.Warn($"Power API không đọc được thiết lập của {planGuid} — dùng powercfg /qh");
        return await ReadSettingsQhAsync(planGuid);
    }

    /// <summary>Đọc qua powercfg /qh: khối của mỗi thiết lập bắt đầu bằng dòng có GUID, hai số hex cuối là AC/DC.</summary>
    public static async Task<Dictionary<string, PowerValue>> ReadSettingsQhAsync(string planGuid)
    {
        var r = await ConsoleAsync("powercfg.exe", $"/qh {planGuid}");
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var lines = r.Output.Split('\n');
        var map = new Dictionary<string, PowerValue>();
        foreach (var s in Settings)
        {
            var start = Array.FindIndex(lines, l => l.Contains(s.Setting, StringComparison.OrdinalIgnoreCase));
            if (start < 0) continue;   // máy không có thiết lập này (vd máy bàn không có nắp gập)
            var hex = new List<int>();
            for (var i = start + 1; i < lines.Length && !GuidRx.IsMatch(lines[i]); i++)
                foreach (Match m in HexRx.Matches(lines[i]))
                    hex.Add(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (hex.Count >= 2) map[s.Key] = new PowerValue(hex[^2], hex[^1]);
        }
        return map;
    }

    /// <summary>Ghi các thay đổi (null = giữ nguyên) rồi kích hoạt lại gói để có hiệu lực ngay.</summary>
    public static async Task<OpResult> ApplySettingsAsync(PowerPlan plan, IEnumerable<(PowerSettingDef Def, int? Ac, int? Dc)> changes)
    {
        var n = 0;
        foreach (var (d, ac, dc) in changes)
        {
            if (ac is { } a)
            {
                var r = await ConsoleAsync("powercfg.exe", $"/setacvalueindex {plan.Guid} {d.Sub} {d.Setting} {a}");
                if (!r.Success) return OpResult.Fail($"{d.Title} (cắm điện): {OpResult.FirstLine(r.Text)}");
                n++;
            }
            if (dc is { } b)
            {
                var r = await ConsoleAsync("powercfg.exe", $"/setdcvalueindex {plan.Guid} {d.Sub} {d.Setting} {b}");
                if (!r.Success) return OpResult.Fail($"{d.Title} (dùng pin): {OpResult.FirstLine(r.Text)}");
                n++;
            }
        }
        if (plan.IsActive) await ConsoleAsync("powercfg.exe", $"/setactive {plan.Guid}");
        return n == 0 ? OpResult.Success("Không có gì thay đổi.") : OpResult.Success($"Đã lưu {n} thiết lập cho gói {plan.Name}.");
    }

    /// <summary>Mẫu thiết lập nhanh: key → (AC, DC).</summary>
    public static IReadOnlyDictionary<string, (int Ac, int Dc)> Preset(string name) => name switch
    {
        // Máy văn phòng: tắt màn hình sớm, ngủ khi rảnh lâu
        "office" => new Dictionary<string, (int, int)>
        {
            ["display"] = (600, 300), ["sleep"] = (1800, 900), ["hibernate"] = (0, 7200), ["lid"] = (1, 1),
            ["button"] = (3, 3), ["cpumax"] = (100, 100), ["cpumin"] = (5, 5), ["disk"] = (1200, 600), ["usb"] = (1, 1), ["pcie"] = (1, 2),
        },
        // Máy luôn bật: camera, chấm công, máy chủ nhỏ, chạy job đêm
        "alwayson" => new Dictionary<string, (int, int)>
        {
            ["display"] = (900, 300), ["sleep"] = (0, 0), ["hibernate"] = (0, 0), ["lid"] = (0, 0),
            ["button"] = (3, 3), ["disk"] = (0, 0), ["usb"] = (0, 0), ["pcie"] = (0, 0),
        },
        // Hiệu năng: CPU tối đa, không tiết kiệm điện thiết bị
        "performance" => new Dictionary<string, (int, int)>
        {
            ["display"] = (1800, 600), ["sleep"] = (0, 1800), ["cpumax"] = (100, 100), ["cpumin"] = (100, 50),
            ["disk"] = (0, 1200), ["usb"] = (0, 1), ["pcie"] = (0, 1),
        },
        // Laptop mát, êm: tắt Turbo
        _ => new Dictionary<string, (int, int)> { ["cpumax"] = (99, 99) },
    };

    // ================= Thiết bị đánh thức =================
    private static List<string> ParseDeviceList(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Equals("NONE", StringComparison.OrdinalIgnoreCase)).ToList();

    public static async Task<List<WakeDevice>> ReadWakeDevicesAsync()
    {
        var armed = ParseDeviceList((await ConsoleAsync("powercfg.exe", "/devicequery wake_armed")).Output);
        var programmable = ParseDeviceList((await ConsoleAsync("powercfg.exe", "/devicequery wake_programmable")).Output);
        return programmable.Union(armed)
            .Select(n => new WakeDevice(n, armed.Contains(n)))
            .OrderByVi(d => d.Name)
            .ToList();
    }

    public static async Task<OpResult> SetWakeAsync(WakeDevice d, bool allow)
    {
        var r = await ConsoleAsync("powercfg.exe", $"/{(allow ? "deviceenablewake" : "devicedisablewake")} \"{d.Name}\"");
        return OpResult.From(r, allow ? $"{d.Name}: được đánh thức máy." : $"{d.Name}: không còn đánh thức máy.", "Không đổi được");
    }

    // ================= Chẩn đoán =================
    public static string ReportFolder => Path.Combine(Path.GetDirectoryName(Log.Folder) ?? Log.Folder, "reports");

    public static async Task<(OpResult Result, string? File)> BatteryReportAsync()
    {
        Directory.CreateDirectory(ReportFolder);
        var f = Path.Combine(ReportFolder, $"battery-report_{DateTime.Now:yyyyMMdd-HHmmss}.html");
        var r = await ConsoleAsync("powercfg.exe", $"/batteryreport /output \"{f}\"");
        return File.Exists(f) ? (OpResult.Success("Đã tạo báo cáo pin."), f)
                              : (OpResult.Fail($"Không tạo được báo cáo pin (máy không có pin?): {OpResult.FirstLine(r.Text)}"), null);
    }

    public static async Task<(OpResult Result, string? File)> EnergyReportAsync(int seconds = 15)
    {
        Directory.CreateDirectory(ReportFolder);
        var f = Path.Combine(ReportFolder, $"energy-report_{DateTime.Now:yyyyMMdd-HHmmss}.html");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120));
        var r = await ConsoleAsync("powercfg.exe", $"/energy /duration {seconds} /output \"{f}\"", cts.Token);
        // powercfg /energy trả mã khác 0 khi phát hiện lỗi/cảnh báo — vẫn có file báo cáo
        return File.Exists(f) ? (OpResult.Success($"Đã tạo báo cáo năng lượng ({seconds} giây theo dõi)."), f)
                              : (OpResult.Fail($"Không tạo được báo cáo năng lượng: {OpResult.FirstLine(r.Text)}"), null);
    }

    public static async Task<string> RequestsAsync() => (await ConsoleAsync("powercfg.exe", "/requests")).Text;
    public static async Task<string> LastWakeAsync() => (await ConsoleAsync("powercfg.exe", "/lastwake")).Text;
    public static async Task<string> SleepStatesAsync() => (await ConsoleAsync("powercfg.exe", "/a")).Text;
}
