using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Startup;

/// <summary>Nguồn của mục khởi động.</summary>
public enum StartupSource { RunUser, RunMachine, RunMachine32, FolderUser, FolderCommon }

public sealed record StartupEntry(
    string Name, string Command, StartupSource Source, bool Enabled,
    string FilePath, bool FileExists, string Publisher, ImageSource? Icon)
{
    /// <summary>Tên gốc trong registry / tên file trong thư mục Startup (khoá của StartupApproved).</summary>
    public string RawName { get; init; } = "";

    public string SourceText => Source switch
    {
        StartupSource.RunUser => "Registry · tài khoản này",
        StartupSource.RunMachine => "Registry · mọi tài khoản",
        StartupSource.RunMachine32 => "Registry 32-bit · mọi tài khoản",
        StartupSource.FolderUser => "Thư mục Startup · tài khoản này",
        _ => "Thư mục Startup · mọi tài khoản",
    };
    public bool IsMicrosoft => Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    public string PublisherText => Publisher.Length > 0 ? Publisher : "Không rõ nhà phát hành";
}

/// <summary>
/// Quản lý ứng dụng tự chạy khi đăng nhập, giống tab Startup của Task Manager:
/// bật/tắt ghi vào khoá StartupApproved (không xoá lệnh gốc) nên luôn bật lại được.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static string UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    public static string CommonFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    // ================= Đọc =================
    public static List<StartupEntry> ReadAll()
    {
        var list = new List<StartupEntry>();
        ReadRun(list, Registry.CurrentUser, RunKey, StartupSource.RunUser);
        ReadRun(list, Registry.LocalMachine, RunKey, StartupSource.RunMachine);
        ReadRun(list, Registry.LocalMachine, Run32Key, StartupSource.RunMachine32);
        ReadFolder(list, UserFolder, StartupSource.FolderUser);
        ReadFolder(list, CommonFolder, StartupSource.FolderCommon);
        return list.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupEntry> list, RegistryKey root, string path, StartupSource src)
    {
        using var k = root.OpenSubKey(path);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            if (name.Length == 0 || k.GetValue(name) is not string cmd || cmd.Trim().Length == 0) continue;
            list.Add(Make(name, cmd, src));
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource src)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder))
        {
            var fn = Path.GetFileName(file);
            if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) ?? file : file;
            list.Add(Make(fn, file, src, target));
        }
    }

    private static StartupEntry Make(string name, string command, StartupSource src, string? fileOverride = null)
    {
        var exe = fileOverride ?? ExtractExe(command);
        var exists = exe.Length > 0 && File.Exists(exe);
        var publisher = "";
        if (exists)
        {
            try { publisher = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() ?? ""; } catch { }
        }
        var display = src is StartupSource.FolderUser or StartupSource.FolderCommon ? Path.GetFileNameWithoutExtension(name) : name;
        return new StartupEntry(display, command, src, IsEnabled(src, name), exe, exists, publisher,
            IconHelper.Get(exists ? exe : (src is StartupSource.FolderUser or StartupSource.FolderCommon ? command : null)))
        {
            RawName = name,
        };
    }

    /// <summary>Lấy đường dẫn file chạy từ dòng lệnh (có/không có ngoặc kép, có biến môi trường).</summary>
    public static string ExtractExe(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            path = end > 1 ? c[1..end] : c.Trim('"');
        }
        else
        {
            var i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = i > 0 ? c[..(i + 4)] : c.Split(' ')[0];
        }
        // rundll32 xxx.dll,Entry → lấy file dll để biết nhà phát hành
        if (Path.GetFileName(path).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            var rest = c[(c.IndexOf(path, StringComparison.OrdinalIgnoreCase) + path.Length)..].Trim().Trim('"');
            var dll = rest.Split(',')[0].Trim('"');
            if (dll.Length > 0) path = dll;
        }
        if (!Path.IsPathRooted(path) && path.Length > 0)
        {
            var sys = Path.Combine(Environment.SystemDirectory, path);
            if (File.Exists(sys)) path = sys;
        }
        return path;
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            var target = (string)shell.CreateShortcut(lnk).TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    // ================= Bật / tắt (StartupApproved) =================
    private static (RegistryKey Root, string Sub) ApprovedLocation(StartupSource src) => src switch
    {
        StartupSource.RunUser => (Registry.CurrentUser, "Run"),
        StartupSource.RunMachine => (Registry.LocalMachine, "Run"),
        StartupSource.RunMachine32 => (Registry.LocalMachine, "Run32"),
        StartupSource.FolderUser => (Registry.CurrentUser, "StartupFolder"),
        _ => (Registry.LocalMachine, "StartupFolder"),
    };

    private static bool IsEnabled(StartupSource src, string rawName)
    {
        var (root, sub) = ApprovedLocation(src);
        using var k = root.OpenSubKey($@"{Approved}\{sub}");
        // Không có giá trị = đang bật; byte đầu chẵn (02/06) = bật, lẻ (03/07) = tắt
        return k?.GetValue(rawName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
    }

    public static OpResult SetEnabled(StartupEntry e, bool enabled)
    {
        try
        {
            var (root, sub) = ApprovedLocation(e.Source);
            using var k = root.CreateSubKey($@"{Approved}\{sub}", writable: true);
            var data = new byte[12];
            data[0] = (byte)(enabled ? 0x02 : 0x03);
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);   // thời điểm tắt, như Task Manager
            k.SetValue(e.RawName, data, RegistryValueKind.Binary);
            return OpResult.Success($"{(enabled ? "Đã bật" : "Đã tắt")} khởi động cùng Windows: {e.Name}.");
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Không có quyền — cần chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    // ================= Xoá / thêm =================
    /// <summary>Nơi cất bản sao lưu khi xoá: file .reg (nhấp đúp để khôi phục) hoặc shortcut gốc.</summary>
    public static string BackupFolder =>
        Path.Combine(Path.GetDirectoryName(Log.Folder) ?? Log.Folder, "backup", "startup");

    private static (RegistryKey Root, string RootName, string Path) RunLocation(StartupSource src) => src switch
    {
        StartupSource.RunUser => (Registry.CurrentUser, "HKEY_CURRENT_USER", RunKey),
        StartupSource.RunMachine => (Registry.LocalMachine, "HKEY_LOCAL_MACHINE", RunKey),
        _ => (Registry.LocalMachine, "HKEY_LOCAL_MACHINE", Run32Key),
    };

    private static string RegEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static OpResult Delete(StartupEntry e)
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup;
            if (e.Source is StartupSource.FolderUser or StartupSource.FolderCommon)
            {
                backup = Path.Combine(BackupFolder, $"{stamp}_{Path.GetFileName(e.Command)}");
                if (File.Exists(e.Command)) File.Move(e.Command, backup);
            }
            else
            {
                var (root, rootName, path) = RunLocation(e.Source);
                var safe = string.Concat(e.RawName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                backup = Path.Combine(BackupFolder, $"{stamp}_{safe}.reg");
                File.WriteAllText(backup,
                    "Windows Registry Editor Version 5.00\r\n\r\n" +
                    $"[{rootName}\\{path}]\r\n\"{RegEscape(e.RawName)}\"=\"{RegEscape(e.Command)}\"\r\n",
                    System.Text.Encoding.Unicode);
                using var k = root.OpenSubKey(path, writable: true);
                k?.DeleteValue(e.RawName, throwOnMissingValue: false);
            }
            var (ar, sub) = ApprovedLocation(e.Source);
            using (var a = ar.OpenSubKey($@"{Approved}\{sub}", writable: true)) a?.DeleteValue(e.RawName, throwOnMissingValue: false);

            Log.Info($"Startup: đã xoá \"{e.RawName}\" [{e.Source}] lệnh: {e.Command} · sao lưu: {backup}");
            return OpResult.Success($"Đã xoá {e.Name} khỏi khởi động. Bản sao lưu: {Path.GetFileName(backup)} (nhấp đúp để khôi phục).");
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Không có quyền — cần chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail($"Không xoá được: {ex.Message}"); }
    }

    public static OpResult Add(string name, string exePath, string args, bool allUsers)
    {
        name = name.Trim();
        if (name.Length == 0) return OpResult.Fail("Nhập tên hiển thị.");
        if (!File.Exists(exePath)) return OpResult.Fail("Không tìm thấy file chương trình.");
        var root = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
        using var k = root.CreateSubKey(RunKey, writable: true);
        if (k.GetValue(name) is not null) return OpResult.Fail($"Đã có mục khởi động tên \"{name}\".");
        var cmd = $"\"{exePath}\"" + (string.IsNullOrWhiteSpace(args) ? "" : " " + args.Trim());
        k.SetValue(name, cmd, RegistryValueKind.String);
        return OpResult.Success($"Đã thêm {name} chạy khi {(allUsers ? "mọi tài khoản" : "tài khoản này")} đăng nhập.");
    }

    public static void OpenLocation(StartupEntry e)
    {
        var target = e.FileExists ? e.FilePath
            : e.Source is StartupSource.FolderUser or StartupSource.FolderCommon ? e.Command : null;
        if (target is not null && File.Exists(target))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        else if (e.Source is StartupSource.FolderUser)
            Process.Start(new ProcessStartInfo(UserFolder) { UseShellExecute = true });
        else if (e.Source is StartupSource.FolderCommon)
            Process.Start(new ProcessStartInfo(CommonFolder) { UseShellExecute = true });
    }
}
