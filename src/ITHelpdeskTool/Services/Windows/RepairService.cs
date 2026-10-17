using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>Một công cụ sửa lỗi. Run nhận (dòng log, tiến độ 0-100 hoặc -1) và trả kết quả.</summary>
public sealed record RepairTool(
    string Key, string Group, string Title, string Description, string Glyph, bool NeedsRestart, string Confirm,
    Func<Action<string>, Action<int>, CancellationToken, Task<OpResult>> Run);

/// <summary>Màn hình xanh gần đây (file minidump).</summary>
public sealed record CrashDump(DateTime Time, string File, long Size);

/// <summary>Các công cụ sửa lỗi Windows có sẵn của Microsoft: SFC, DISM, CHKDSK, đặt lại Windows Update, mạng...</summary>
public static class RepairService
{
    public const string GSystem = "File hệ thống";
    public const string GUpdate = "Windows Update & Store";
    public const string GNetwork = "Mạng";
    public const string GShell = "Giao diện & dịch vụ";
    public const string GBoot = "Ổ đĩa & khởi động";

    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string SysDrive => Path.GetPathRoot(Win)!.TrimEnd('\\');

    /// <summary>Nguồn sửa cho DISM (install.wim / .esd từ bộ cài cùng bản), rỗng = lấy từ Windows Update.</summary>
    public static string DismSource { get; set; } = "";

    public static readonly IReadOnlyList<RepairTool> Tools =
    [
        new("restorepoint", GSystem, "Tạo điểm khôi phục", "Lưu trạng thái hệ thống trước khi sửa, để quay lại nếu cần.", "\uE777", false, "",
            (log, p, ct) => CreateRestorePointAsync(log, ct)),
        new("dism", GSystem, "Sửa kho thành phần (DISM RestoreHealth)",
            "Tải lại file hệ thống hỏng từ Windows Update (hoặc từ bộ cài ISO). Nên chạy trước SFC. 10–30 phút.", "\uE90F", false, "",
            (log, p, ct) => DismRestoreAsync(log, p, ct)),
        new("sfc", GSystem, "Quét và sửa file hệ thống (SFC)", "sfc /scannow — thay file Windows bị hỏng bằng bản gốc. 5–15 phút.", "\uE9F5", false, "",
            (log, p, ct) => SfcAsync(log, p, ct)),
        new("wu", GUpdate, "Đặt lại Windows Update", "Dừng dịch vụ, làm mới SoftwareDistribution và catroot2 — sửa lỗi cập nhật treo / báo lỗi liên tục.", "\uE895", false,
            "Lịch sử cập nhật hiển thị trong Settings sẽ bị xoá (các bản đã cài vẫn giữ nguyên).",
            (log, p, ct) => ResetWindowsUpdateAsync(log, ct)),
        new("store", GUpdate, "Đặt lại Microsoft Store", "Xoá bộ đệm Store (wsreset) — sửa lỗi Store không mở / không tải được app.", "\uE719", false, "",
            (log, p, ct) => RunLoggedAsync("wsreset.exe", "-i", log, ct, ok: "Đã đặt lại bộ đệm Microsoft Store.")),
        new("network", GNetwork, "Đặt lại mạng", "Winsock, TCP/IP, xoá cache DNS — sửa lỗi có mạng nhưng không vào được web. Cần khởi động lại.", "\uE968", true,
            "Các thiết lập IP tĩnh và proxy WinHTTP có thể bị xoá. Kết nối mạng ngắt trong giây lát.",
            (log, p, ct) => ResetNetworkAsync(log, ct)),
        new("dns", GNetwork, "Xoá bộ đệm DNS", "ipconfig /flushdns — sửa lỗi vào nhầm / không vào được một trang web.", "\uE774", false, "",
            (log, p, ct) => RunLoggedAsync("ipconfig.exe", "/flushdns", log, ct, ok: "Đã xoá bộ đệm DNS.")),
        new("icons", GShell, "Làm mới bộ đệm biểu tượng", "Sửa biểu tượng / ảnh thu nhỏ hiển thị sai hoặc trắng. Explorer sẽ khởi động lại.", "\uE8B9", false, "",
            (log, p, ct) => RebuildIconCacheAsync(log, ct)),
        new("spooler", GShell, "Sửa hàng đợi máy in", "Xoá lệnh in bị kẹt và khởi động lại dịch vụ Print Spooler.", "\uE749", false,
            "Mọi lệnh in đang chờ sẽ bị huỷ.",
            (log, p, ct) => ResetSpoolerAsync(log, ct)),
        new("wmi", GShell, "Sửa kho WMI", "Kiểm tra và sửa kho WMI — sửa lỗi phần mềm quản lý / báo cáo cấu hình không đọc được thông tin máy.", "\uE9D9", false, "",
            (log, p, ct) => RepairWmiAsync(log, ct)),
        new("startmenu", GShell, "Sửa Start menu / thanh tìm kiếm", "Đăng ký lại Start, Search và các thành phần giao diện của Windows.", "\uE8FC", false, "",
            (log, p, ct) => ReRegisterShellAsync(log, ct)),
        new("chkdsk", GBoot, "Quét lỗi ổ hệ thống", "chkdsk /scan — kiểm tra ổ C: khi đang chạy, không cần khởi động lại.", "\uEDA2", false, "",
            (log, p, ct) => ChkdskScanAsync(log, p, ct)),
        new("chkdskboot", GBoot, "Sửa lỗi ổ hệ thống khi khởi động", "Đánh dấu ổ C: để Windows quét và sửa lỗi ở lần khởi động tới.", "\uE7E8", true,
            "Lần khởi động tới Windows sẽ quét ổ C: trước khi vào màn hình đăng nhập (có thể mất lâu với ổ lớn).",
            (log, p, ct) => ScheduleChkdskAsync(log, ct)),
        new("bcdboot", GBoot, "Dựng lại file khởi động", "bcdboot — tạo lại trình khởi động Windows trên phân vùng hệ thống khi máy báo lỗi boot.", "\uE7F8", false,
            "Ghi lại file khởi động của Windows đang chạy vào phân vùng EFI / System Reserved. Menu khởi động tuỳ chỉnh (nếu có) có thể bị thay.",
            (log, p, ct) => RunLoggedAsync("bcdboot.exe", $"\"{Win}\"", log, ct, ok: "Đã dựng lại file khởi động.")),
    ];

    // ================= Chạy lệnh có log =================
    private static async Task<OpResult> RunLoggedAsync(string exe, string args, Action<string> log, CancellationToken ct,
        string ok, Encoding? enc = null, Action<string>? onLine = null, int[]? okCodes = null)
    {
        log($"> {exe} {args}");
        var r = await ProcessRunner.RunAsync(exe, args, line =>
        {
            var l = line.Replace("\0", "").Split('\r').LastOrDefault(x => x.Trim().Length > 0)?.TrimEnd() ?? "";
            if (l.Trim().Length == 0) return;
            onLine?.Invoke(l);
            log(l);
        }, ct, enc ?? ProcessRunner.Oem);
        foreach (var e in r.Error.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)) log(e);
        return (okCodes ?? [0]).Contains(r.ExitCode) ? OpResult.Success(ok) : OpResult.Fail($"{Path.GetFileName(exe)} kết thúc với mã {r.ExitCode}.");
    }

    private static async Task Ps(string script, Action<string> log, CancellationToken ct)
    {
        var r = await ProcessRunner.PowerShellAsync(script, l => { if (l.Trim().Length > 0) log(l); }, ct);
        if (!r.Success) log("! " + OpResult.FirstLine(r.Error.Length > 0 ? r.Error : r.Output));
    }

    private static Action<string> Percent(Action<int> p) => line =>
    {
        var m = Regex.Match(line, @"(\d{1,3}(?:[\.,]\d)?)\s?%");
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            p((int)Math.Min(100, v));
    };

    // ================= Công cụ =================
    public static async Task<OpResult> CreateRestorePointAsync(Action<string> log, CancellationToken ct)
    {
        log("> Checkpoint-Computer");
        // Windows chỉ cho tạo 1 điểm / 24 giờ → tạm bỏ giới hạn
        var r = await ProcessRunner.PowerShellAsync("""
            Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $old = (Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            try { Checkpoint-Computer -Description 'IT Helpdesk Tool - truoc khi sua loi' -RestorePointType MODIFY_SETTINGS }
            finally { if ($null -eq $old) { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue } else { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old } }
            """, ct: ct);
        if (!r.Success) log("! " + OpResult.FirstLine(r.Error));
        return r.Success ? OpResult.Success("Đã tạo điểm khôi phục.") : OpResult.Fail($"Không tạo được điểm khôi phục: {OpResult.FirstLine(r.Error)} (máy có thể đang tắt System Protection).");
    }

    private static int WuStartType()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\wuauserv", "Start", 3) is int v ? v : 3; }
        catch { return 3; }
    }

    public static async Task<OpResult> DismRestoreAsync(Action<string> log, Action<int> p, CancellationToken ct)
    {
        var src = DismSource.Length > 0 ? $" /Source:{DismSource} /LimitAccess" : "";
        // Sửa qua Windows Update cần dịch vụ Windows Update: nếu đang bị tắt (Disabled) thì bật tạm, sửa xong trả lại như cũ
        var wuDisabled = src.Length == 0 && WuStartType() == 4;
        if (wuDisabled)
        {
            log("Dịch vụ Windows Update đang bị tắt — bật tạm để DISM tải file sửa…");
            await ProcessRunner.ConsoleAsync("sc.exe", "config wuauserv start= demand", ct);
        }
        OpResult r;
        try
        {
            if (src.Length == 0) await ProcessRunner.ConsoleAsync("sc.exe", "start wuauserv", ct);
            r = await RunLoggedAsync("dism.exe", $"/English /Online /Cleanup-Image /RestoreHealth{src}", log, ct,
                ok: "DISM: kho thành phần đã được kiểm tra và sửa.", onLine: Percent(p));
        }
        finally
        {
            if (wuDisabled)
            {
                await ProcessRunner.ConsoleAsync("sc.exe", "stop wuauserv", CancellationToken.None);
                await ProcessRunner.ConsoleAsync("sc.exe", "config wuauserv start= disabled", CancellationToken.None);
                log("Đã trả dịch vụ Windows Update về trạng thái tắt như trước.");
            }
        }
        if (!r.Ok)
            return OpResult.Fail(src.Length == 0
                ? "DISM không tải được file sửa từ Windows Update (máy không có mạng, đang dùng WSUS hoặc Windows Update bị chặn). Bấm “Dùng bộ cài ISO làm nguồn…” và chọn ISO cùng phiên bản rồi chạy lại. Nhật ký: C:\\Windows\\Logs\\DISM\\dism.log"
                : "DISM chưa sửa được bằng bộ cài này — kiểm tra ISO đúng phiên bản và build của máy. Nhật ký: C:\\Windows\\Logs\\DISM\\dism.log");
        return r;
    }

    public static async Task<OpResult> SfcAsync(Action<string> log, Action<int> p, CancellationToken ct)
    {
        var lines = new StringBuilder();
        // sfc ghi UTF-16 khi bị chuyển hướng output
        await RunLoggedAsync("sfc.exe", "/scannow", log, ct, ok: "", enc: Encoding.Unicode,
            onLine: l => { lines.AppendLine(l); Percent(p)(l); }, okCodes: [0, 1, 2, 3]);
        var cbs = Path.Combine(Win, @"Logs\CBS\CBS.log");
        var text = lines.ToString();
        // Đọc kết luận: theo chuỗi tiếng Anh (máy tiếng Việt vẫn in tiếng Anh với sfc), dự phòng CBS.log
        if (text.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase)) return OpResult.Success("SFC: không phát hiện file hệ thống hỏng.");
        if (text.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase)) return OpResult.Success("SFC: đã tìm thấy và sửa xong file hỏng. Nên khởi động lại máy.", true);
        if (text.Contains("unable to fix", StringComparison.OrdinalIgnoreCase)) return OpResult.Fail("SFC: có file hỏng nhưng không sửa được — chạy DISM RestoreHealth rồi chạy lại SFC.");
        if (text.Contains("could not perform", StringComparison.OrdinalIgnoreCase)) return OpResult.Fail("SFC không chạy được — có thể đang chờ khởi động lại để hoàn tất cập nhật.");
        return OpResult.Success($"SFC đã chạy xong — xem chi tiết trong {cbs}.");
    }

    public static async Task<OpResult> ResetWindowsUpdateAsync(Action<string> log, CancellationToken ct)
    {
        string[] services = ["wuauserv", "bits", "cryptsvc", "msiserver", "usosvc"];
        foreach (var s in services) { log($"> dừng {s}"); await ProcessRunner.ConsoleAsync("net.exe", $"stop {s} /y", ct); }
        var errors = new List<string>();
        foreach (var dir in new[] { Path.Combine(Win, "SoftwareDistribution"), Path.Combine(Win, @"System32\catroot2") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var bak = dir + ".bak";
                if (Directory.Exists(bak)) Directory.Delete(bak, true);
                Directory.Move(dir, bak);
                log($"Đổi tên {dir} → {Path.GetFileName(bak)}");
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(dir)}: {ex.Message}"); log("! " + ex.Message); }
        }
        foreach (var s in services.Reverse()) { log($"> chạy {s}"); await ProcessRunner.ConsoleAsync("net.exe", $"start {s}", ct); }
        return errors.Count == 0
            ? OpResult.Success("Đã đặt lại Windows Update. Mở Settings › Windows Update và bấm kiểm tra cập nhật.")
            : OpResult.Fail($"Đặt lại chưa trọn vẹn: {string.Join("; ", errors)} — thử khởi động lại máy rồi chạy lại.");
    }

    public static async Task<OpResult> ResetNetworkAsync(Action<string> log, CancellationToken ct)
    {
        await RunLoggedAsync("netsh.exe", "winsock reset", log, ct, ok: "");
        await RunLoggedAsync("netsh.exe", "int ip reset", log, ct, ok: "", okCodes: [0, 1]);
        await RunLoggedAsync("netsh.exe", "winhttp reset proxy", log, ct, ok: "");
        await RunLoggedAsync("ipconfig.exe", "/flushdns", log, ct, ok: "");
        return OpResult.Success("Đã đặt lại mạng. Khởi động lại máy để hoàn tất.", true);
    }

    public static async Task<OpResult> RebuildIconCacheAsync(Action<string> log, CancellationToken ct)
    {
        await RunLoggedAsync("ie4uinit.exe", "-show", log, ct, ok: "", okCodes: [0, 1]);
        log("> dừng explorer");
        await ProcessRunner.ConsoleAsync("taskkill.exe", "/f /im explorer.exe", ct);
        await Task.Delay(800, ct);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
        var n = 0;
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "iconcache*.db").Concat(Directory.GetFiles(dir, "thumbcache*.db")))
                try { File.Delete(f); n++; } catch { }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconCache.db");
        if (File.Exists(legacy)) try { File.Delete(legacy); n++; } catch { }
        log($"Đã xoá {n} file bộ đệm");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        return OpResult.Success($"Đã làm mới bộ đệm biểu tượng ({n} file).");
    }

    public static async Task<OpResult> ResetSpoolerAsync(Action<string> log, CancellationToken ct)
    {
        await RunLoggedAsync("net.exe", "stop spooler /y", log, ct, ok: "", okCodes: [0, 2]);
        var dir = Path.Combine(Win, @"System32\spool\PRINTERS");
        var n = 0;
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir)) try { File.Delete(f); n++; } catch { }
        log($"Đã xoá {n} lệnh in kẹt");
        var r = await RunLoggedAsync("net.exe", "start spooler", log, ct, ok: "", okCodes: [0, 2]);
        return OpResult.Success($"Đã khởi động lại Print Spooler, xoá {n} lệnh in kẹt.");
    }

    public static async Task<OpResult> RepairWmiAsync(Action<string> log, CancellationToken ct)
    {
        var v = await RunLoggedAsync("winmgmt.exe", "/verifyrepository", log, ct, ok: "", okCodes: [0, 1]);
        var r = await RunLoggedAsync("winmgmt.exe", "/salvagerepository", log, ct, ok: "Đã kiểm tra và sửa kho WMI.");
        return r;
    }

    public static async Task<OpResult> ReRegisterShellAsync(Action<string> log, CancellationToken ct)
    {
        await Ps("""
            $names = 'Microsoft.Windows.StartMenuExperienceHost','Microsoft.Windows.ShellExperienceHost','Microsoft.Windows.Search','MicrosoftWindows.Client.CBS','Microsoft.Windows.Client.WebExperience'
            foreach ($n in $names) {
              Get-AppxPackage -AllUsers -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
                try { Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $_.InstallLocation 'AppxManifest.xml') -ErrorAction Stop; "Đã đăng ký lại $($_.Name)" }
                catch { "Bỏ qua $($_.Name): $($_.Exception.Message)" }
              }
            }
            """, log, ct);
        log("> khởi động lại explorer");
        await ProcessRunner.ConsoleAsync("taskkill.exe", "/f /im StartMenuExperienceHost.exe", ct);
        await ProcessRunner.ConsoleAsync("taskkill.exe", "/f /im SearchHost.exe", ct);
        return OpResult.Success("Đã đăng ký lại Start menu và tìm kiếm. Nếu vẫn lỗi, khởi động lại máy.");
    }

    public static Task<OpResult> ChkdskScanAsync(Action<string> log, Action<int> p, CancellationToken ct) =>
        RunLoggedAsync("chkdsk.exe", $"{SysDrive} /scan", log, ct, ok: $"Đã quét ổ {SysDrive} — xem kết quả ở nhật ký bên dưới.", onLine: Percent(p), okCodes: [0, 1, 2, 3]);

    public static async Task<OpResult> ScheduleChkdskAsync(Action<string> log, CancellationToken ct)
    {
        var r = await RunLoggedAsync("fsutil.exe", $"dirty set {SysDrive}", log, ct, ok: "");
        return r.Ok ? OpResult.Success($"Ổ {SysDrive} sẽ được quét và sửa ở lần khởi động tới.", true) : OpResult.Fail("Không đặt được lịch quét ổ.");
    }

    /// <summary>Ổ hệ thống đang được đánh dấu cần quét chưa.</summary>
    public static async Task<bool> IsDirtyAsync()
    {
        var r = await ProcessRunner.ConsoleAsync("fsutil.exe", $"dirty query {SysDrive}");
        return r.Output.Contains(" is Dirty", StringComparison.OrdinalIgnoreCase) && !r.Output.Contains("NOT Dirty", StringComparison.OrdinalIgnoreCase);
    }

    // ================= Tình trạng máy =================
    public static List<CrashDump> RecentCrashes()
    {
        var list = new List<CrashDump>();
        var dir = Path.Combine(Win, "Minidump");
        try
        {
            if (Directory.Exists(dir))
                list.AddRange(new DirectoryInfo(dir).GetFiles("*.dmp").Select(f => new CrashDump(f.LastWriteTime, f.FullName, f.Length)));
            var full = Path.Combine(Win, "MEMORY.DMP");
            if (File.Exists(full)) { var f = new FileInfo(full); list.Add(new CrashDump(f.LastWriteTime, f.FullName, f.Length)); }
        }
        catch { }
        return list.OrderByDescending(c => c.Time).ToList();
    }

    /// <summary>Máy đang chờ khởi động lại (sau cập nhật / cài đặt).</summary>
    public static bool PendingReboot()
    {
        using var cbs = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wu = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return cbs is not null || wu is not null;
    }

    /// <summary>Số lần tắt máy đột ngột (Kernel-Power 41) trong 30 ngày.</summary>
    public static async Task<int> UnexpectedShutdownsAsync()
    {
        var r = await ProcessRunner.PowerShellAsync(
            "@(Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41; StartTime=(Get-Date).AddDays(-30)} -ErrorAction SilentlyContinue).Count");
        return int.TryParse(r.Output.Trim(), out var n) ? n : 0;
    }
}
