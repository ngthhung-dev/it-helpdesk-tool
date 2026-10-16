using System.Diagnostics;
using System.Management;
using System.Text.Json;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.ProcessRunner;

namespace ITHelpdeskTool.Services.Startup;

public sealed record BootEntry(string Id, string Description, bool IsDefault, bool IsCurrent);

public sealed record BootConfig(IReadOnlyList<BootEntry> Entries, int TimeoutSeconds, string SafeBoot, bool IsUefi);

public sealed record BootTime(DateTime When, double Seconds, double MainPathSeconds);

/// <summary>
/// Menu khởi động (BCD) đọc qua WMI — không phụ thuộc ngôn ngữ như phân tích chữ của bcdedit;
/// ghi bằng bcdedit với ID GUID.
/// </summary>
public static class BootService
{
    private const string BootMgr = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
    private const string Current = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";
    private const uint OsLoaderType = 0x10200003;
    private const uint ElDescription = 0x12000004;
    private const uint ElTimeout = 0x25000004;
    private const uint ElDefault = 0x23000003;
    private const uint ElDisplayOrder = 0x24000001;
    private const uint ElSafeBoot = 0x25000080;

    private static ManagementObject Obj(string id) =>
        new(@"root\WMI", $"BcdObject.Id=\"{id}\",StoreFilePath=\"\"", null);

    private static ManagementBaseObject? GetElement(string id, uint type)
    {
        using var o = Obj(id);
        var inp = o.GetMethodParameters("GetElement");
        inp["Type"] = type;
        var outp = o.InvokeMethod("GetElement", inp, null);
        return outp?["ReturnValue"] is true ? outp["Element"] as ManagementBaseObject : null;
    }

    /// <summary>GUID thật của mục đang chạy (thay cho alias {current}).</summary>
    private static string? ResolveCurrentId()
    {
        try
        {
            using var store = new ManagementObject(@"root\WMI", "BcdStore.FilePath=\"\"", null);
            var inp = store.GetMethodParameters("OpenObject");
            inp["Id"] = Current;
            var outp = store.InvokeMethod("OpenObject", inp, null);
            return (outp?["Object"] as ManagementBaseObject)?["Id"] as string;
        }
        catch { return null; }
    }

    public static BootConfig ReadConfig()
    {
        var entries = new List<BootEntry>();
        var timeout = 0;
        string? defId = null;
        string[] order = [];
        try
        {
            timeout = Convert.ToInt32(GetElement(BootMgr, ElTimeout)?["Integer"] ?? 0);
            defId = GetElement(BootMgr, ElDefault)?["Id"] as string;
            order = GetElement(BootMgr, ElDisplayOrder)?["Ids"] as string[] ?? [];
        }
        catch (Exception ex) { Log.Warn($"Đọc Boot Manager lỗi: {ex.Message}"); }

        var cur = ResolveCurrentId();
        var ids = order.Length > 0 ? order : EnumerateLoaders();
        foreach (var id in ids)
        {
            try
            {
                var desc = GetElement(id, ElDescription)?["String"] as string;
                if (desc is null) continue;   // không phải mục hệ điều hành (vd memtest) vẫn có mô tả; null = lỗi
                entries.Add(new BootEntry(id, desc,
                    id.Equals(defId, StringComparison.OrdinalIgnoreCase),
                    id.Equals(cur, StringComparison.OrdinalIgnoreCase)));
            }
            catch { }
        }

        var safe = "";
        try
        {
            if (GetElement(Current, ElSafeBoot)?["Integer"] is { } v)
                safe = Convert.ToInt32(v) switch { 0 => "minimal", 1 => "network", 2 => "dsrepair", _ => "" };
        }
        catch { }

        return new BootConfig(entries, timeout, safe, Firmware.IsUefi);
    }

    private static string[] EnumerateLoaders()
    {
        try
        {
            using var store = new ManagementObject(@"root\WMI", "BcdStore.FilePath=\"\"", null);
            var inp = store.GetMethodParameters("EnumerateObjects");
            inp["Type"] = OsLoaderType;
            var outp = store.InvokeMethod("EnumerateObjects", inp, null);
            return (outp?["Objects"] as ManagementBaseObject[])?.Select(o => (string)o["Id"]).ToArray() ?? [];
        }
        catch { return []; }
    }

    // ================= Ghi =================
    public static async Task<OpResult> SetTimeoutAsync(int seconds)
    {
        if (seconds is < 0 or > 999) return OpResult.Fail("Thời gian chờ từ 0 đến 999 giây.");
        var r = await ConsoleAsync("bcdedit.exe", $"/timeout {seconds}");
        return OpResult.From(r, $"Menu khởi động chờ {seconds} giây.", "Không đặt được thời gian chờ");
    }

    public static async Task<OpResult> SetDefaultAsync(BootEntry e)
    {
        var r = await ConsoleAsync("bcdedit.exe", $"/default {e.Id}");
        return OpResult.From(r, $"Mặc định khởi động vào: {e.Description}.", "Không đặt được mục mặc định");
    }

    /// <summary>mode: "" = tắt, "minimal" = Safe Mode, "network" = Safe Mode có mạng.</summary>
    public static async Task<OpResult> SetSafeBootAsync(string mode)
    {
        var r = mode.Length == 0
            ? await ConsoleAsync("bcdedit.exe", "/deletevalue {current} safeboot")
            : await ConsoleAsync("bcdedit.exe", $"/set {{current}} safeboot {mode}");
        // Xoá khi vốn không có giá trị → bcdedit báo lỗi nhưng kết quả vẫn đúng
        if (!r.Success && mode.Length == 0 && ReadConfig().SafeBoot.Length == 0) return OpResult.Success("Lần sau khởi động bình thường.");
        return OpResult.From(r,
            mode.Length == 0 ? "Đã tắt Safe Mode — lần sau khởi động bình thường."
                             : $"Lần khởi động sau sẽ vào Safe Mode{(mode == "network" ? " (có mạng)" : "")}. Nhớ tắt lại sau khi sửa xong.",
            "Không đổi được Safe Mode", restart: mode.Length > 0);
    }

    // ================= Khởi động lại đặc biệt / hẹn giờ =================
    public static OpResult RestartToFirmware() => Shutdown("/r /fw /t 5", "Máy sẽ khởi động lại vào BIOS/UEFI sau 5 giây.");
    public static OpResult RestartToRecovery() => Shutdown("/r /o /t 5", "Máy sẽ khởi động lại vào Advanced Startup (Windows RE) sau 5 giây.");

    public static OpResult Schedule(bool restart, int minutes)
    {
        if (minutes is < 1 or > 1440) return OpResult.Fail("Hẹn từ 1 đến 1440 phút (24 giờ).");
        return Shutdown($"{(restart ? "/r" : "/s")} /t {minutes * 60} /c \"IT Helpdesk Tool: hẹn giờ {(restart ? "khởi động lại" : "tắt máy")}\"",
            $"Máy sẽ {(restart ? "khởi động lại" : "tắt")} lúc {DateTime.Now.AddMinutes(minutes):HH:mm} ({minutes} phút nữa).");
    }

    public static OpResult CancelSchedule()
    {
        var r = ConsoleAsync("shutdown.exe", "/a").GetAwaiter().GetResult();
        return r.Success ? OpResult.Success("Đã huỷ hẹn giờ tắt/khởi động lại.")
                         : OpResult.Fail("Không có hẹn giờ nào đang chờ.");
    }

    private static OpResult Shutdown(string args, string ok)
    {
        var r = ConsoleAsync("shutdown.exe", args).GetAwaiter().GetResult();
        return r.Success ? OpResult.Success(ok) : OpResult.Fail(OpResult.FirstLine(r.Text));
    }

    // ================= Thời gian khởi động =================
    /// <summary>Các lần khởi động gần nhất (nhật ký Diagnostics-Performance, chỉ có trên Windows client).</summary>
    public static async Task<List<BootTime>> ReadBootTimesAsync(int max = 8)
    {
        var script = $$"""
            Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Diagnostics-Performance/Operational'; Id = 100 } -MaxEvents {{max}} -ErrorAction SilentlyContinue |
            ForEach-Object {
              $d = @{}; ([xml]$_.ToXml()).Event.EventData.Data | ForEach-Object { $d[$_.Name] = $_.'#text' }
              [pscustomobject]@{ When = $_.TimeCreated.ToString('o'); Boot = [double]$d['BootTime']; Main = [double]$d['MainPathBootTime'] }
            } | ConvertTo-Json -Compress
            """;
        var r = await PowerShellAsync(script);
        var list = new List<BootTime>();
        var json = r.Output.Trim();
        if (!r.Success || json.Length == 0) return list;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            if (!DateTime.TryParse(e.GetProperty("When").GetString(), out var when)) continue;
            list.Add(new BootTime(when, e.GetProperty("Boot").GetDouble() / 1000, e.GetProperty("Main").GetDouble() / 1000));
        }
        return list;
    }

    public static DateTime LastBoot => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
