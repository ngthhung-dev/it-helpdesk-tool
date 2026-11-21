using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Tạo môi trường cài (WinPE) từ boot.wim của bộ cài Windows — không cần Windows ADK:
    /// lấy bản index 1 (Microsoft Windows PE), đánh dấu khởi động được, gắn winpeshl.ini + run.cmd để tự chạy,
    /// nạp driver ổ cứng (RAID / Intel RST / NVMe lạ) để WinPE thấy ổ.
    /// </summary>
    public static class BootWim
    {
        public const string WinPeshl =
            "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMROOT%\\System32\\cmd.exe, /c %SYSTEMROOT%\\System32\\ith\\run.cmd\r\n";

        public static void Build(IRunner run, string sourceBootWim, string destBootWim, string mountDir, string runCmd,
            IEnumerable<string> storageDriverDirs, Action<string>? progress = null)
        {
            if (File.Exists(destBootWim)) File.Delete(destBootWim);
            CleanMount(run, mountDir);

            progress?.Invoke("Đang tạo môi trường cài từ boot.wim…");
            Must(run.Run("dism.exe", $"/English /Export-Image /SourceImageFile:\"{sourceBootWim}\" /SourceIndex:1 /DestinationImageFile:\"{destBootWim}\" /Bootable"),
                "Không lấy được môi trường cài từ boot.wim");

            Directory.CreateDirectory(mountDir);
            Must(run.Run("dism.exe", $"/English /Mount-Image /ImageFile:\"{destBootWim}\" /Index:1 /MountDir:\"{mountDir}\""),
                "Không mở được boot.wim để chỉnh");
            var committed = false;
            try
            {
                var sys32 = Path.Combine(mountDir, "Windows", "System32");
                if (!File.Exists(Path.Combine(sys32, "wpeinit.exe")))
                    throw new ReinstallException("boot.wim không phải môi trường WinPE (thiếu wpeinit.exe) — hãy dùng ISO chính thức của Microsoft.");
                File.WriteAllText(Path.Combine(sys32, "winpeshl.ini"), WinPeshl, Encoding.ASCII);
                Directory.CreateDirectory(Path.Combine(sys32, "ith"));
                File.WriteAllText(Path.Combine(sys32, "ith", "run.cmd"), Templates.Crlf(runCmd), Encoding.ASCII);

                foreach (var dir in storageDriverDirs)
                {
                    progress?.Invoke($"Đang nạp driver ổ cứng {Path.GetFileName(dir)} vào môi trường cài…");
                    var r = run.Run("dism.exe", $"/English /Image:\"{mountDir}\" /Add-Driver /Driver:\"{dir}\" /Recurse");
                    if (!r.Ok) progress?.Invoke($"Cảnh báo: không nạp được driver {Path.GetFileName(dir)}");
                }

                progress?.Invoke("Đang lưu môi trường cài…");
                Must(run.Run("dism.exe", $"/English /Unmount-Image /MountDir:\"{mountDir}\" /Commit"), "Không lưu được boot.wim");
                committed = true;
            }
            finally
            {
                if (!committed) run.Run("dism.exe", $"/English /Unmount-Image /MountDir:\"{mountDir}\" /Discard");
                try { Directory.Delete(mountDir, true); } catch { }
            }
        }

        /// <summary>Lần trước dừng giữa chừng → gỡ thư mục mount cũ.</summary>
        private static void CleanMount(IRunner run, string mountDir)
        {
            if (!Directory.Exists(mountDir)) return;
            run.Run("dism.exe", $"/English /Unmount-Image /MountDir:\"{mountDir}\" /Discard");
            run.Run("dism.exe", "/English /Cleanup-Mountpoints");
            try { Directory.Delete(mountDir, true); } catch { }
        }

        private static void Must(RunResult r, string message)
        {
            if (!r.Ok) throw new ReinstallException($"{message}: {ProcessRunner.FirstLines(r.Output, 4)}");
        }
    }

    /// <summary>Xuất driver của máy đang chạy (Windows 8.1+) để nạp vào Windows mới; chọn ra driver ổ cứng cho WinPE.</summary>
    public static class Drivers
    {
        // Lớp thiết bị của driver bộ điều khiển ổ cứng: SCSIAdapter (RAID, RST, NVMe hãng) và HDC (SATA/AHCI)
        private static readonly Regex StorageClass = new Regex(
            @"^\s*(Class\s*=\s*(SCSIAdapter|HDC)\b|ClassGuid\s*=\s*\{(4D36E97B-E325-11CE-BFC1-08002BE10318|4D36E96A-E325-11CE-BFC1-08002BE10318)\})",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static bool Export(IRunner run, string dest, Action<string>? progress = null)
        {
            progress?.Invoke("Đang xuất driver của máy…");
            Directory.CreateDirectory(dest);
            var r = run.Run("dism.exe", $"/English /Online /Export-Driver /Destination:\"{dest}\"");
            if (!r.Ok) progress?.Invoke("Cảnh báo: không xuất được driver (Windows mới sẽ dùng driver có sẵn).");
            return r.Ok;
        }

        /// <summary>Thư mục driver có file .inf thuộc lớp bộ điều khiển ổ cứng.</summary>
        public static List<string> StorageDirs(string driversRoot)
        {
            var list = new List<string>();
            if (!Directory.Exists(driversRoot)) return list;
            foreach (var dir in Directory.GetDirectories(driversRoot))
            {
                foreach (var inf in Directory.GetFiles(dir, "*.inf"))
                {
                    string text;
                    try { text = File.ReadAllText(inf); } catch { continue; }
                    // .inf thường là UTF-16 hoặc ANSI; đọc lại theo Unicode nếu có ký tự 0
                    if (text.IndexOf('\0') >= 0) text = File.ReadAllText(inf, Encoding.Unicode);
                    if (StorageClass.IsMatch(text)) { list.Add(dir); break; }
                }
            }
            return list;
        }

        /// <summary>Tổng dung lượng thư mục (byte).</summary>
        public static long Size(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
    }
}
