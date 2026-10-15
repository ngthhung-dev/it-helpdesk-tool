using System.Text.Json;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Printing;

public sealed record PrinterInfo(
    string Name, string Driver, string Port, string Status, bool IsDefault, bool Shared, string ShareName, int Jobs, string Type, string Location)
{
    public bool IsVirtual => Port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || Port is "nul:" or "SHRFAX:" || Driver.Contains("PDF") || Driver.Contains("XPS") || Driver.Contains("OneNote") || Driver.Contains("Fax");
    public bool IsNetwork => Type == "Connection" || Port.StartsWith("\\\\");
    public string StatusText => Status switch
    {
        "Normal" or "" => "Sẵn sàng", "Paused" => "Tạm dừng", "Error" => "Lỗi", "PaperJam" => "Kẹt giấy", "PaperOut" => "Hết giấy",
        "Offline" => "Ngoại tuyến", "TonerLow" => "Sắp hết mực", "NoToner" => "Hết mực", "DoorOpen" => "Mở nắp", "PaperProblem" => "Lỗi giấy",
        "Printing" => "Đang in", "Busy" => "Đang bận", "ServerUnknown" => "Không thấy máy chủ", _ => Status,
    };
    public string Tone => Status is "Normal" or "" or "Printing" or "Busy" ? "Good" : Status is "Paused" or "TonerLow" ? "Warn" : "Bad";
    public string Detail => string.Join(" · ", new[]
    {
        Driver, Port, Shared ? $"chia sẻ: {ShareName}" : "", Jobs > 0 ? $"{Jobs} lệnh in đang chờ" : "", Location,
    }.Where(s => s.Length > 0));
}

public sealed record PrintJobInfo(string Printer, int Id, string Document, string User, string Status, long Pages, DateTime? Submitted);

/// <summary>Quản lý máy in: thêm qua IP / máy chủ, mặc định, in thử, hàng đợi, chia sẻ, driver, sửa lỗi chia sẻ.</summary>
public static class PrinterService
{
    private static List<JsonElement> Arr(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
    }
    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    private static string Q(string s) => ProcessRunner.PsQuote(s);

    private static async Task<OpResult> Ps(string script, string ok, string fail)
    {
        var r = await ProcessRunner.PowerShellAsync(script);
        return r.Success ? OpResult.Success(ok) : OpResult.Fail($"{fail}: {Translate(OpResult.FirstLine(r.Error.Length > 0 ? r.Error : r.Text))}");
    }

    private static string Translate(string e) =>
        e.Contains("0x0000011b", StringComparison.OrdinalIgnoreCase) ? "lỗi 0x0000011b — bấm “Sửa lỗi kết nối máy in chia sẻ” rồi thử lại" :
        e.Contains("0x00000709", StringComparison.OrdinalIgnoreCase) ? "lỗi 0x00000709 — không đặt được máy in mặc định (thử tắt “Let Windows manage my default printer”)" :
        e.Contains("0x00000bcb", StringComparison.OrdinalIgnoreCase) ? "lỗi 0x00000bcb — máy này chưa có driver cho máy in, cần cài driver của hãng" :
        e.Contains("spooler", StringComparison.OrdinalIgnoreCase) ? "dịch vụ Print Spooler không chạy — bấm “Khởi động lại Print Spooler”" : e;

    public static async Task<List<PrinterInfo>> ListAsync()
    {
        const string script = """
            $def = (Get-CimInstance Win32_Printer -Filter 'Default=True' -ErrorAction SilentlyContinue).Name
            @(Get-Printer | ForEach-Object {
              $n = $_.Name
              [pscustomobject]@{ Name=$n; Driver=$_.DriverName; Port=$_.PortName; Status="$($_.PrinterStatus)"; Default=($n -eq $def);
                Shared=[bool]$_.Shared; Share="$($_.ShareName)"; Type="$($_.Type)"; Location="$($_.Location)";
                Jobs=@(Get-PrintJob -PrinterName $n -ErrorAction SilentlyContinue).Count }
            }) | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(Translate(OpResult.FirstLine(r.Text)));
        return Arr(r.Output).Select(e => new PrinterInfo(S(e, "Name"), S(e, "Driver"), S(e, "Port"), S(e, "Status"), S(e, "Default") == "True",
                S(e, "Shared") == "True", S(e, "Share"), int.TryParse(S(e, "Jobs"), out var j) ? j : 0, S(e, "Type"), S(e, "Location")))
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.IsVirtual).ThenBy(p => p.Name).ToList();
    }

    public static async Task<List<string>> DriversAsync()
    {
        var r = await ProcessRunner.PowerShellAsync("@(Get-PrinterDriver | Select-Object -ExpandProperty Name | Sort-Object -Unique) | ConvertTo-Json -Compress");
        return Arr(r.Output).Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }

    public static async Task<List<PrintJobInfo>> JobsAsync(string printer)
    {
        var r = await ProcessRunner.PowerShellAsync(
            $"@(Get-PrintJob -PrinterName {Q(printer)} | ForEach-Object {{ [pscustomobject]@{{ Id=$_.Id; Doc=$_.DocumentName; User=$_.UserName; Status=\"$($_.JobStatus)\"; Pages=$_.TotalPages; Time=$_.SubmittedTime.ToString('o') }} }}) | ConvertTo-Json -Compress");
        return Arr(r.Output).Select(e => new PrintJobInfo(printer, int.TryParse(S(e, "Id"), out var i) ? i : 0, S(e, "Doc"), S(e, "User"), S(e, "Status"),
            long.TryParse(S(e, "Pages"), out var p) ? p : 0, DateTime.TryParse(S(e, "Time"), out var t) ? t : null)).ToList();
    }

    // ================= Thao tác =================
    public static Task<OpResult> SetDefaultAsync(PrinterInfo p) =>
        Ps($$"""
            Set-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' -Name LegacyDefaultPrinterMode -Value 1 -Type DWord
            $pr = Get-CimInstance Win32_Printer -Filter "Name='{{p.Name.Replace("\\", "\\\\").Replace("'", "\\'")}}'"
            $r = Invoke-CimMethod -InputObject $pr -MethodName SetDefaultPrinter
            if ($r.ReturnValue -ne 0) { throw "mã $($r.ReturnValue)" }
            """, $"Đã đặt {p.Name} làm máy in mặc định (tắt chế độ Windows tự chọn máy in mặc định).", "Không đặt được máy in mặc định");

    public static Task<OpResult> TestPageAsync(PrinterInfo p) =>
        Ps($$"""
            $pr = Get-CimInstance Win32_Printer -Filter "Name='{{p.Name.Replace("\\", "\\\\").Replace("'", "\\'")}}'"
            $r = Invoke-CimMethod -InputObject $pr -MethodName PrintTestPage
            if ($r.ReturnValue -ne 0) { throw "mã $($r.ReturnValue)" }
            """, $"Đã gửi trang in thử tới {p.Name}.", "Không in thử được");

    public static Task<OpResult> ClearJobsAsync(PrinterInfo p) =>
        Ps($"Get-PrintJob -PrinterName {Q(p.Name)} | Remove-PrintJob", $"Đã xoá các lệnh in đang chờ của {p.Name}.", "Không xoá được lệnh in");

    public static Task<OpResult> RemoveJobAsync(PrintJobInfo j) =>
        Ps($"Remove-PrintJob -PrinterName {Q(j.Printer)} -ID {j.Id}", $"Đã huỷ lệnh in “{j.Document}”.", "Không huỷ được lệnh in");

    public static Task<OpResult> RenameAsync(PrinterInfo p, string name) =>
        Ps($"Rename-Printer -Name {Q(p.Name)} -NewName {Q(name.Trim())}", $"Đã đổi tên thành {name.Trim()}.", "Không đổi tên được");

    public static Task<OpResult> ShareAsync(PrinterInfo p, bool on, string shareName = "") =>
        on ? Ps($"Set-Printer -Name {Q(p.Name)} -Shared $true -ShareName {Q(shareName.Length > 0 ? shareName : p.Name)}",
                $"Đã chia sẻ {p.Name} trong mạng nội bộ (\\\\{Environment.MachineName}\\{(shareName.Length > 0 ? shareName : p.Name)}).", "Không chia sẻ được")
           : Ps($"Set-Printer -Name {Q(p.Name)} -Shared $false", $"Đã ngừng chia sẻ {p.Name}.", "Không ngừng chia sẻ được");

    public static async Task<OpResult> RemoveAsync(PrinterInfo p)
    {
        var r = await Ps($"Remove-Printer -Name {Q(p.Name)}", $"Đã gỡ máy in {p.Name}.", "Không gỡ được máy in");
        // Dọn cổng TCP/IP không còn máy in nào dùng
        if (r.Ok && !p.IsNetwork && Regex.IsMatch(p.Port, @"^(IP_|\d+\.\d+\.\d+\.\d+)"))
            await ProcessRunner.PowerShellAsync($"if (-not (Get-Printer | Where-Object PortName -eq {Q(p.Port)})) {{ Remove-PrinterPort -Name {Q(p.Port)} -ErrorAction SilentlyContinue }}");
        return r;
    }

    public static Task<OpResult> RemoveDriverAsync(string driver) =>
        Ps($"Remove-PrinterDriver -Name {Q(driver)}", $"Đã gỡ driver {driver}.", "Không gỡ được driver (còn máy in đang dùng?)");

    public static void OpenQueue(PrinterInfo p) => ShellLauncher.Open("rundll32.exe", $"printui.dll,PrintUIEntry /o /n \"{p.Name}\"");
    public static void OpenProperties(PrinterInfo p) => ShellLauncher.Open("rundll32.exe", $"printui.dll,PrintUIEntry /p /n \"{p.Name}\"");
    public static void OpenPreferences(PrinterInfo p) => ShellLauncher.Open("rundll32.exe", $"printui.dll,PrintUIEntry /e /n \"{p.Name}\"");

    // ================= Thêm máy in =================
    /// <summary>Máy in mạng có phản hồi ở cổng RAW 9100 / IPP 631 / LPD 515 không.</summary>
    public static async Task<(bool Online, string Detail)> ProbeAsync(string ip)
    {
        var open = new List<int>();
        foreach (var port in new[] { 9100, 631, 515 })
        {
            using var c = new System.Net.Sockets.TcpClient();
            try
            {
                var t = c.ConnectAsync(ip, port);
                if (await Task.WhenAny(t, Task.Delay(1500)) == t && c.Connected) open.Add(port);
            }
            catch { }
        }
        return open.Count > 0 ? (true, "mở cổng " + string.Join(", ", open)) : (false, "không thấy cổng in 9100 / 631 / 515");
    }

    public static async Task<OpResult> AddByIpAsync(string ip, string name, string driver)
    {
        ip = ip.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _) && !Regex.IsMatch(ip, @"^[A-Za-z0-9][A-Za-z0-9.\-]+$"))
            return OpResult.Fail("Địa chỉ IP / tên máy in không hợp lệ.");
        var port = "IP_" + ip;
        return await Ps($"""
            if (-not (Get-PrinterPort -Name {Q(port)} -ErrorAction SilentlyContinue)) {"{"} Add-PrinterPort -Name {Q(port)} -PrinterHostAddress {Q(ip)} {"}"}
            if (-not (Get-PrinterDriver -Name {Q(driver)} -ErrorAction SilentlyContinue)) {"{"} Add-PrinterDriver -Name {Q(driver)} {"}"}
            Add-Printer -Name {Q(name.Trim())} -DriverName {Q(driver)} -PortName {Q(port)}
            """, $"Đã thêm máy in {name.Trim()} ({ip}).", "Không thêm được máy in");
    }

    /// <summary>Kết nối máy in chia sẻ từ máy khác: \\máy\tên.</summary>
    public static Task<OpResult> AddSharedAsync(string unc)
    {
        unc = unc.Trim();
        if (!Regex.IsMatch(unc, @"^\\\\[^\\]+\\.+")) return Task.FromResult(OpResult.Fail("Đường dẫn có dạng \\\\TEN-MAY\\TenMayIn."));
        return Ps($"Add-Printer -ConnectionName {Q(unc)}", $"Đã kết nối {unc}.", "Không kết nối được");
    }

    // ================= Sửa lỗi =================
    private const string PrintKey = @"SYSTEM\CurrentControlSet\Control\Print";

    /// <summary>Đang bật bản vá bảo mật RPC gây lỗi 0x0000011b khi kết nối máy in chia sẻ.</summary>
    public static bool RpcPrivacyEnforced()
    {
        using var k = Registry.LocalMachine.OpenSubKey(PrintKey);
        return k?.GetValue("RpcAuthnLevelPrivacyEnabled") is not int v || v != 0;
    }

    /// <summary>
    /// Sửa lỗi 0x0000011b: đặt RpcAuthnLevelPrivacyEnabled = 0 trên MÁY CHIA SẺ máy in (hạ mức xác thực RPC của dịch vụ in).
    /// Microsoft khuyến nghị cập nhật Windows cả hai máy thay vì cách này; chỉ dùng trong mạng nội bộ tin cậy.
    /// </summary>
    public static async Task<OpResult> SetRpcPrivacyAsync(bool enforced)
    {
        using (var k = Registry.LocalMachine.CreateSubKey(PrintKey, true))
        {
            if (enforced) k.DeleteValue("RpcAuthnLevelPrivacyEnabled", false);
            else k.SetValue("RpcAuthnLevelPrivacyEnabled", 0, RegistryValueKind.DWord);
        }
        await RestartSpoolerAsync();
        return OpResult.Success(enforced ? "Đã bật lại mức bảo mật mặc định cho dịch vụ in." : "Đã áp dụng cách sửa lỗi 0x0000011b trên máy này và khởi động lại Print Spooler.");
    }

    public static async Task<OpResult> RestartSpoolerAsync()
    {
        var r = await Windows.RepairService.ResetSpoolerAsync(_ => { }, CancellationToken.None);
        return r;
    }

    public static void OpenSettings() => ShellLauncher.Open("ms-settings:printers");
}
