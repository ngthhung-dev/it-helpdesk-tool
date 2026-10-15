using System.Text.Json;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Policy;

/// <summary>Một ổ USB lưu trữ (đang cắm hoặc đã có trong danh sách được phép).</summary>
public sealed record UsbStorage(string InstanceId, string Name, string Serial, long Size, string Letters, bool Present)
{
    public string Display => Name.Length > 0 ? Name : InstanceId;
    public string Detail => string.Join(" · ", new[]
    {
        Size > 0 ? Fmt.Size(Size) : "", Letters, Serial.Length > 0 ? $"S/N {Serial}" : "", Present ? "đang cắm" : "không cắm",
    }.Where(s => s.Length > 0));
}

/// <summary>
/// Chỉ cho dùng các USB lưu trữ được duyệt: dùng chính sách Device Installation Restrictions của Windows —
/// chặn cài thiết bị lớp DiskDrive (ổ đĩa mới cắm vào), ngoại lệ theo mã thiết bị (instance ID) của từng USB được phép,
/// với thứ tự đánh giá phân lớp (instance ID ưu tiên hơn lớp thiết bị — Windows 10 2004 trở lên).
/// USB khác cắm vào không được nhận ổ nên không đọc/ghi được.
/// </summary>
public static class UsbWhitelistService
{
    public const string Key = @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";
    public const string DiskDriveClass = "{4d36e967-e325-11ce-bfc1-08002be10318}";

    /// <summary>Chỉ cho selftest: coi ổ ảo (VHD) như USB.</summary>
    internal static bool AllowVirtual { get; set; }

    private static readonly string[] UsbBus = ["USB", "SD", "MMC"];

    // ================= Trạng thái =================
    public static bool IsEnabled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key);
        if (k?.GetValue("DenyDeviceClasses") is not int deny || deny != 1) return false;
        using var c = Registry.LocalMachine.OpenSubKey(Key + @"\DenyDeviceClasses");
        return c?.GetValueNames().Any(n => string.Equals(c.GetValue(n) as string, DiskDriveClass, StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>Mã thiết bị đang được phép (do app ghi).</summary>
    public static List<string> AllowedIds()
    {
        using var c = Registry.LocalMachine.OpenSubKey(Key + @"\AllowInstanceIDs");
        return c?.GetValueNames().Select(n => c.GetValue(n) as string).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList() ?? [];
    }

    // ================= Liệt kê ổ =================
    /// <summary>Ổ USB lưu trữ đang cắm (không bao giờ là ổ Windows / ổ khởi động).</summary>
    public static async Task<List<UsbStorage>> PresentAsync(CancellationToken ct = default)
    {
        const string script = """
            @(Get-Disk | Where-Object { -not $_.IsBoot -and -not $_.IsSystem } | ForEach-Object {
              $l = @(Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { "$($_.DriveLetter):" }) -join ' '
              [pscustomobject]@{ Path="$($_.Path)"; Name="$($_.FriendlyName)"; Serial="$($_.SerialNumber)".Trim(); Size=[int64]$_.Size; Bus="$($_.BusType)"; Letters=$l }
            }) | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var json = r.Output.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var list = new List<UsbStorage>();
        foreach (var e in items)
        {
            var bus = e.GetProperty("Bus").GetString() ?? "";
            if (!UsbBus.Contains(bus, StringComparer.OrdinalIgnoreCase) && !(AllowVirtual && bus.Contains("Virtual", StringComparison.OrdinalIgnoreCase))) continue;
            var id = PathToInstanceId(e.GetProperty("Path").GetString() ?? "");
            if (id.Length == 0) continue;
            list.Add(new UsbStorage(id, e.GetProperty("Name").GetString() ?? "", e.GetProperty("Serial").GetString() ?? "",
                e.GetProperty("Size").GetInt64(), e.GetProperty("Letters").GetString() ?? "", true));
        }
        return list;
    }

    /// <summary>\\?\usbstor#disk&amp;ven_x#123&amp;0#{53f56307-…} → USBSTOR\DISK&amp;VEN_X\123&amp;0</summary>
    public static string PathToInstanceId(string path)
    {
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) return "";
        var p = path[4..];
        var g = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (g > 0) p = p[..g];
        return p.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>Danh sách hiển thị: ổ đang cắm + ổ đã được phép (kể cả đang rút ra).</summary>
    public static async Task<List<UsbStorage>> ListAsync(CancellationToken ct = default)
    {
        var present = await PresentAsync(ct);
        var allowed = AllowedIds();
        var list = new List<UsbStorage>(present);
        foreach (var id in allowed.Where(a => !present.Any(p => p.InstanceId.Equals(a, StringComparison.OrdinalIgnoreCase))))
            list.Add(new UsbStorage(id, await FriendlyNameAsync(id), "", 0, "", false));
        return list;
    }

    private static async Task<string> FriendlyNameAsync(string id)
    {
        var r = await ProcessRunner.PowerShellAsync("(Get-PnpDevice -InstanceId $env:ITHD_ID -ErrorAction SilentlyContinue).FriendlyName",
            env: new Dictionary<string, string> { ["ITHD_ID"] = id });
        return r.Output.Trim();
    }

    // ================= Bật / tắt =================
    /// <summary>
    /// Chỉ cho phép các ổ USB có mã trong danh sách. Ổ đĩa trong máy đang có giữ nguyên (đã cài, và cũng được thêm vào danh sách phép
    /// để cập nhật driver không bị chặn). Xoá dấu vết các USB lạ từng cắm trước đây để lần cắm sau phải "cài" lại → bị chặn.
    /// </summary>
    public static async Task<OpResult> EnableAsync(IReadOnlyCollection<string> allowUsbIds, CancellationToken ct = default)
    {
        if (allowUsbIds.Count == 0) return OpResult.Fail("Chọn ít nhất một USB được phép.");
        try
        {
            // Mọi ổ đĩa đang có trong máy (ổ trong, ổ đang cắm) — giữ quyền cài lại driver
            var internalIds = await PresentDiskIdsAsync(ct);
            var present = (await PresentAsync(ct)).Select(p => p.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keep = internalIds.Where(i => !present.Contains(i)).Concat(allowUsbIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            using (var k = Registry.LocalMachine.CreateSubKey(Key, true))
            {
                k.SetValue("DenyDeviceClasses", 1, RegistryValueKind.DWord);
                k.SetValue("DenyDeviceClassesRetroactive", 0, RegistryValueKind.DWord);   // không gỡ thiết bị đang chạy
                k.SetValue("AllowInstanceIDs", 1, RegistryValueKind.DWord);
                k.SetValue("AllowDenyLayered", 1, RegistryValueKind.DWord);                // mã thiết bị ưu tiên hơn lớp
            }
            AddToList(Key + @"\DenyDeviceClasses", [DiskDriveClass], replace: false);
            AddToList(Key + @"\AllowInstanceIDs", keep, replace: true);

            // USB còn cắm mà không được phép: gỡ khỏi máy luôn (không đọc/ghi được nữa)
            var removed = new List<string>();
            foreach (var id in present.Where(p => !allowUsbIds.Contains(p, StringComparer.OrdinalIgnoreCase)))
                if (await RemoveDeviceAsync(id, ct)) removed.Add(id);
            // Dấu vết USB đã từng cắm (không còn cắm): xoá để lần cắm sau bị chặn
            var ghosts = await RemoveGhostUsbDisksAsync(keep, ct);
            return OpResult.Success($"Đã bật: chỉ {allowUsbIds.Count} USB được phép dùng. " +
                                    (removed.Count > 0 ? $"Đã ngắt {removed.Count} USB lạ đang cắm. " : "") +
                                    (ghosts > 0 ? $"Đã xoá {ghosts} USB từng cắm trước đây khỏi danh sách nhận. " : "") +
                                    "USB khác cắm vào sẽ không được nhận.");
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Cần chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail($"Không bật được: {ex.Message}"); }
    }

    /// <summary>Thêm / bớt một USB khi đang bật.</summary>
    public static OpResult SetAllowed(string instanceId, bool allow)
    {
        try
        {
            var list = AllowedIds();
            if (allow) { if (!list.Contains(instanceId, StringComparer.OrdinalIgnoreCase)) list.Add(instanceId); }
            else list.RemoveAll(i => i.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
            AddToList(Key + @"\AllowInstanceIDs", list, replace: true);
            return OpResult.Success(allow ? "Đã cho phép USB này. Rút ra cắm lại để dùng." : "Đã bỏ USB này khỏi danh sách được phép.");
        }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    /// <summary>Tắt: xoá các giá trị app đã ghi, USB nào cũng dùng được lại (cắm lại / quét lại phần cứng).</summary>
    public static async Task<OpResult> DisableAsync(CancellationToken ct = default)
    {
        try
        {
            using (var c = Registry.LocalMachine.OpenSubKey(Key + @"\DenyDeviceClasses", true))
                if (c is not null)
                    foreach (var n in c.GetValueNames().Where(n => string.Equals(c.GetValue(n) as string, DiskDriveClass, StringComparison.OrdinalIgnoreCase)))
                        c.DeleteValue(n, false);
            var denyLeft = Registry.LocalMachine.OpenSubKey(Key + @"\DenyDeviceClasses")?.GetValueNames().Length ?? 0;
            Registry.LocalMachine.DeleteSubKeyTree(Key + @"\AllowInstanceIDs", false);
            using (var k = Registry.LocalMachine.OpenSubKey(Key, true))
                if (k is not null)
                {
                    if (denyLeft == 0) { Registry.LocalMachine.DeleteSubKeyTree(Key + @"\DenyDeviceClasses", false); k.DeleteValue("DenyDeviceClasses", false); }
                    foreach (var v in new[] { "DenyDeviceClassesRetroactive", "AllowInstanceIDs", "AllowDenyLayered" }) k.DeleteValue(v, false);
                }
            // Quét lại phần cứng để USB đang cắm (bị chặn trước đó) được nhận ngay
            await ProcessRunner.ConsoleAsync("pnputil.exe", "/scan-devices", ct);
            return OpResult.Success("Đã tắt: mọi USB lưu trữ dùng lại bình thường (USB đang cắm chưa nhận thì rút ra cắm lại).");
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Cần chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail($"Không tắt được: {ex.Message}"); }
    }

    // ================= Hỗ trợ =================
    private static void AddToList(string key, IEnumerable<string> values, bool replace)
    {
        if (replace) Registry.LocalMachine.DeleteSubKeyTree(key, false);
        using var k = Registry.LocalMachine.CreateSubKey(key, true);
        var existing = k.GetValueNames().Select(n => k.GetValue(n) as string).Where(v => v is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var i = k.GetValueNames().Select(n => int.TryParse(n, out var x) ? x : 0).DefaultIfEmpty(0).Max();
        foreach (var v in values.Where(v => !existing.Contains(v)))
            k.SetValue((++i).ToString(), v, RegistryValueKind.String);
    }

    /// <summary>Mã mọi ổ đĩa (lớp DiskDrive) đang có mặt.</summary>
    private static async Task<List<string>> PresentDiskIdsAsync(CancellationToken ct)
    {
        var r = await ProcessRunner.PowerShellAsync("Get-PnpDevice -Class DiskDrive -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object { $_.InstanceId }", ct: ct);
        return r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private static async Task<bool> RemoveDeviceAsync(string id, CancellationToken ct)
    {
        var r = await ProcessRunner.ConsoleAsync("pnputil.exe", $"/remove-device \"{id}\"", ct);
        return r.Success;
    }

    /// <summary>Xoá ổ USB không còn cắm (dấu vết cũ) — trừ ổ được phép.</summary>
    private static async Task<int> RemoveGhostUsbDisksAsync(IReadOnlyCollection<string> keep, CancellationToken ct)
    {
        var r = await ProcessRunner.PowerShellAsync(
            "Get-PnpDevice -Class DiskDrive -ErrorAction SilentlyContinue | Where-Object { -not $_.Present } | ForEach-Object { $_.InstanceId }", ct: ct);
        var n = 0;
        foreach (var id in r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (keep.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            var usb = id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) || id.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                      AllowVirtual && id.Contains("VIRTUAL_DISK", StringComparison.OrdinalIgnoreCase);
            if (usb && await RemoveDeviceAsync(id, ct)) n++;
        }
        return n;
    }
}
