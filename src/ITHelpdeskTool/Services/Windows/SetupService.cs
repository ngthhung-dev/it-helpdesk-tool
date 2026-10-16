using ITHelpdeskTool.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>Một tiêu chí yêu cầu phần cứng của Windows 11.</summary>
public sealed record ReadinessItem(string Name, string Value, bool? Pass, string Note);

/// <summary>Cài / nâng cấp / sửa chữa Windows bằng setup.exe gốc trong bộ cài của Microsoft.</summary>
public static class SetupService
{
    // ================= Kiểm tra yêu cầu Windows 11 (chỉ đọc) =================
    [DllImport("kernel32.dll")] private static extern bool GetFirmwareType(out int type);

    public static async Task<List<ReadinessItem>> Windows11ReadinessAsync()
    {
        var list = new List<ReadinessItem>();
        await Task.Run(() =>
        {
            // Firmware UEFI
            var uefi = GetFirmwareType(out var fw) && fw == 2;
            list.Add(new("Chế độ khởi động", uefi ? "UEFI" : "Legacy BIOS", uefi, uefi ? "" : "Windows 11 cần UEFI — chuyển ổ sang GPT (mbr2gpt) rồi bật UEFI trong BIOS."));

            // TPM
            var tpm = Wmi.TryQuery("SELECT SpecVersion, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm").FirstOrDefault();
            var spec = tpm?.Str("SpecVersion").Split(',').FirstOrDefault()?.Trim() ?? "";
            var tpmOk = tpm is not null && spec.StartsWith("2.0") && tpm.Bool("IsEnabled_InitialValue") != false;
            list.Add(new("TPM", tpm is null ? "Không có / đang tắt" : $"{spec}" + (tpm.Bool("IsEnabled_InitialValue") == false ? " (đang tắt)" : ""), tpmOk,
                tpmOk ? "" : "Cần TPM 2.0 — bật Intel PTT / AMD fTPM trong BIOS."));

            // CPU
            var cpu = Wmi.TryQuery("SELECT Name, NumberOfCores, MaxClockSpeed, AddressWidth FROM Win32_Processor").FirstOrDefault();
            var cores = (int)(cpu?.Long("NumberOfCores") ?? 0);
            var mhz = cpu?.Long("MaxClockSpeed") ?? 0;
            var x64 = Environment.Is64BitOperatingSystem;
            var cpuBasic = cores >= 2 && mhz >= 1000 && x64;
            list.Add(new("Bộ xử lý", $"{cpu?.Str("Name")} · {cores} nhân · {mhz / 1000.0:0.0} GHz", cpuBasic ? null : false,
                cpuBasic ? "Đạt số nhân / xung nhịp; dòng CPU cần nằm trong danh sách Microsoft hỗ trợ (Intel thế hệ 8+, AMD Ryzen 2000+)." : "Cần 64-bit, 2 nhân, 1 GHz trở lên."));

            // RAM
            var ram = Wmi.TryQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault()?.Long("TotalPhysicalMemory") ?? 0;
            list.Add(new("RAM", Fmt.Size(ram), ram >= 3.8 * 1024 * 1024 * 1024, ram >= 3.8 * 1024 * 1024 * 1024 ? "" : "Cần 4 GB trở lên."));

            // Ổ hệ thống
            var sys = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!);
            var big = sys.TotalSize >= 60L * 1024 * 1024 * 1024;
            list.Add(new("Ổ hệ thống", $"{Fmt.Size(sys.TotalSize)} · trống {Fmt.Size(sys.AvailableFreeSpace)}", big && sys.AvailableFreeSpace > 20L * 1024 * 1024 * 1024,
                !big ? "Cần ổ 64 GB trở lên." : sys.AvailableFreeSpace <= 20L * 1024 * 1024 * 1024 ? "Nên trống 20 GB trở lên để nâng cấp." : ""));
        });

        // Secure Boot
        var sb = await ProcessRunner.PowerShellAsync("try { [string](Confirm-SecureBootUEFI) } catch { 'unsupported' }");
        var s = sb.Output.Trim();
        list.Insert(1, new("Secure Boot", s == "True" ? "Đang bật" : s == "False" ? "Đang tắt" : "Không hỗ trợ (BIOS)", s == "True",
            s == "True" ? "" : "Máy cần hỗ trợ Secure Boot (bật trong BIOS ở chế độ UEFI)."));
        return list.OrderByVi(i => i.Name).ToList();
    }

    // ================= setup.exe =================
    /// <summary>Kết quả kiểm tra tương thích (setup.exe /compat scanonly) → tiếng Việt.</summary>
    public static (bool Ok, string Text) ExplainCompat(int code) => unchecked((uint)code) switch
    {
        0xC1900210 => (true, "Tương thích — có thể nâng cấp / sửa chữa bằng bộ cài này."),
        0xC1900208 => (false, "Có ứng dụng hoặc driver không tương thích — xem chi tiết trong C:\\$WINDOWS.~BT\\Sources\\Panther\\CompatData*.xml."),
        0xC1900204 => (false, "Không giữ được ứng dụng và dữ liệu: bộ cài khác phiên bản / ngôn ngữ / kiến trúc với Windows đang chạy."),
        0xC1900200 => (false, "Máy không đạt yêu cầu phần cứng của phiên bản Windows này."),
        0xC190020E => (false, "Ổ hệ thống không đủ dung lượng trống."),
        0xC1900107 => (false, "Còn thư mục cài đặt cũ đang chờ dọn — khởi động lại máy rồi thử lại."),
        0xC1800118 or 0xC1900101 => (false, $"Lỗi driver khi cài (0x{code:X8}) — cập nhật driver / rút thiết bị ngoại vi rồi thử lại."),
        0 => (true, "setup.exe đã chạy xong."),
        _ => (false, $"setup.exe trả mã 0x{code:X8}."),
    };

    /// <summary>Kiểm tra máy có nâng cấp / sửa chữa tại chỗ được bằng bộ cài này không (không thay đổi gì).</summary>
    public static async Task<OpResult> CompatScanAsync(SetupMedia m, Action<string> log, CancellationToken ct)
    {
        log($"> setup.exe /auto upgrade /compat scanonly /quiet /noreboot /dynamicupdate disable");
        var r = await ProcessRunner.RunAsync(m.SetupExe, "/auto upgrade /compat scanonly /quiet /noreboot /dynamicupdate disable /eula accept", ct: ct);
        var (ok, text) = ExplainCompat(r.ExitCode);
        return ok ? OpResult.Success(text) : OpResult.Fail(text);
    }

    /// <summary>
    /// Nâng cấp / sửa chữa tại chỗ giữ nguyên ứng dụng và dữ liệu (repair install). Hiện giao diện setup để theo dõi;
    /// máy tự khởi động lại vài lần.
    /// </summary>
    public static OpResult StartRepairUpgrade(SetupMedia m, bool dynamicUpdate)
    {
        var args = $"/auto upgrade /eula accept /dynamicupdate {(dynamicUpdate ? "enable" : "disable")} /showoobe none";
        Log.Info($"setup.exe {args}");
        try
        {
            Process.Start(new ProcessStartInfo(m.SetupExe, args) { UseShellExecute = true, WorkingDirectory = m.Root });
            return OpResult.Success("Đã mở trình cài đặt Windows ở chế độ giữ nguyên ứng dụng và dữ liệu. Theo dõi trên cửa sổ cài đặt.");
        }
        catch (Exception ex) { return OpResult.Fail($"Không chạy được setup.exe: {ex.Message}"); }
    }

    public static OpResult StartSetup(SetupMedia m)
    {
        try
        {
            Process.Start(new ProcessStartInfo(m.SetupExe) { UseShellExecute = true, WorkingDirectory = m.Root });
            return OpResult.Success("Đã mở trình cài đặt Windows.");
        }
        catch (Exception ex) { return OpResult.Fail($"Không chạy được setup.exe: {ex.Message}"); }
    }

    /// <summary>Khởi động lại vào menu Advanced Startup (WinRE): khôi phục, Startup Repair, boot từ USB...</summary>
    public static Task<ProcessResult> RebootToRecoveryAsync() => ProcessRunner.ConsoleAsync("shutdown.exe", "/r /o /t 10");

    /// <summary>Thông tin WinRE (môi trường khôi phục) có đang bật không.</summary>
    public static async Task<string> WinReStatusAsync()
    {
        var r = await ProcessRunner.ConsoleAsync("reagentc.exe", "/info");
        var on = System.Text.RegularExpressions.Regex.IsMatch(r.Output, @":\s*Enabled", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var off = System.Text.RegularExpressions.Regex.IsMatch(r.Output, @":\s*Disabled", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return on ? "Đang bật" : off ? "Đang tắt" : "Không rõ";
    }
}
