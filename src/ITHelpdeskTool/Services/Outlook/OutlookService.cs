using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Outlook;

public sealed record OutlookDataFile(string Path, long Size, DateTime Modified, bool IsOst)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    /// <summary>Giới hạn mặc định của Outlook 2010 trở lên: 50 GB.</summary>
    public bool NearLimit => Size > 45L * 1024 * 1024 * 1024;
    public string Detail => $"{(IsOst ? "OST · bản sao hộp thư (Exchange / Microsoft 365 / IMAP)" : "PST · dữ liệu lưu trên máy")} · {Fmt.Size(Size)} · sửa {Fmt.Stamp(Modified)}";
}

/// <summary>Dữ liệu Outlook: tìm PST/OST, sao lưu, dựng lại OST, sửa PST (ScanPST), hồ sơ, chữ ký, chế độ an toàn.</summary>
public static class OutlookService
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string SignaturesDir => Path.Combine(Roaming, @"Microsoft\Signatures");
    public static string RoamCacheDir => Path.Combine(Local, @"Microsoft\Outlook\RoamCache");

    public static string? OutlookExe()
    {
        var p = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE", "", null) as string;
        return p is not null && File.Exists(p.Trim('"')) ? p.Trim('"') : null;
    }

    public static string? ScanPstExe()
    {
        var o = OutlookExe();
        var dir = o is null ? null : Path.GetDirectoryName(o);
        var c = new List<string>();
        if (dir is not null) c.Add(Path.Combine(dir, "SCANPST.EXE"));
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            c.Add(Path.Combine(pf, @"Microsoft Office\root\Office16\SCANPST.EXE"));
            c.Add(Path.Combine(pf, @"Microsoft Office\Office16\SCANPST.EXE"));
            c.Add(Path.Combine(pf, @"Microsoft Office\Office15\SCANPST.EXE"));
        }
        return c.FirstOrDefault(File.Exists);
    }

    public static bool IsRunning() => Process.GetProcessesByName("OUTLOOK").Length > 0;

    /// <summary>Đóng Outlook (gửi yêu cầu đóng cửa sổ, chờ, rồi mới buộc dừng).</summary>
    public static async Task<bool> CloseAsync()
    {
        foreach (var p in Process.GetProcessesByName("OUTLOOK")) try { p.CloseMainWindow(); } catch { }
        for (var i = 0; i < 30 && IsRunning(); i++) await Task.Delay(500);
        if (IsRunning()) foreach (var p in Process.GetProcessesByName("OUTLOOK")) try { p.Kill(); } catch { }
        await Task.Delay(1000);
        return !IsRunning();
    }

    // ================= Tìm file dữ liệu =================
    /// <summary>File PST/OST ở các vị trí Outlook dùng + đường dẫn ghi trong hồ sơ Outlook.</summary>
    public static List<OutlookDataFile> FindDataFiles(IEnumerable<string>? extraDirs = null)
    {
        var dirs = new List<string>
        {
            Path.Combine(Local, @"Microsoft\Outlook"),
            Path.Combine(Docs, "Outlook Files"),
            Path.Combine(Roaming, @"Microsoft\Outlook"),
        };
        if (extraDirs is not null) dirs.AddRange(extraDirs);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*.*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                         .Where(f => f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)))
                files.Add(f);
        foreach (var f in ProfilePaths().Where(File.Exists)) files.Add(f);
        return files.Select(f => { var i = new FileInfo(f); return new OutlookDataFile(f, i.Length, i.LastWriteTime, f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)); })
            .OrderByDescending(f => f.Size).ToList();
    }

    /// <summary>Đường dẫn PST/OST ghi trong registry hồ sơ (giá trị nhị phân Unicode chứa “.pst”/“.ost”).</summary>
    private static IEnumerable<string> ProfilePaths()
    {
        var result = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles");
        if (root is null) return result;
        void Walk(RegistryKey k, int depth)
        {
            foreach (var n in k.GetValueNames())
                if (k.GetValue(n) is byte[] b && b.Length > 10 && b.Length < 2048)
                {
                    try
                    {
                        var s = System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
                        if ((s.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)) && Path.IsPathRooted(s))
                            result.Add(s);
                    }
                    catch { }
                }
            if (depth < 3)
                foreach (var sub in k.GetSubKeyNames())
                    using (var c = k.OpenSubKey(sub)) if (c is not null) Walk(c, depth + 1);
        }
        try { Walk(root, 0); } catch { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static (List<string> Profiles, string Default) Profiles()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles");
        using var o = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook");
        return (k?.GetSubKeyNames().ToList() ?? [], o?.GetValue("DefaultProfile") as string ?? "");
    }

    // ================= Sao lưu =================
    /// <summary>Chép file dữ liệu sang thư mục khác (Outlook phải đóng). Báo tiến độ 0-100.</summary>
    public static async Task<OpResult> BackupAsync(IReadOnlyList<OutlookDataFile> files, string destDir, Action<int> progress, CancellationToken ct)
    {
        if (IsRunning()) return OpResult.Fail("Outlook đang mở — đóng Outlook trước khi sao lưu (file đang bị khoá).");
        var dest = Path.Combine(destDir, $"Outlook-{Environment.UserName}-{DateTime.Now:yyyyMMdd-HHmm}");
        Directory.CreateDirectory(dest);
        long total = files.Sum(f => f.Size), done = 0;
        var buf = new byte[4 << 20];
        foreach (var f in files)
        {
            var target = Path.Combine(dest, f.Name);
            if (File.Exists(target)) target = Path.Combine(dest, $"{Path.GetFileNameWithoutExtension(f.Name)}-{Guid.NewGuid().ToString("N")[..4]}{Path.GetExtension(f.Name)}");
            await using var src = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            await using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress((int)(done * 100 / Math.Max(1, total)));
            }
        }
        // Kèm chữ ký email
        if (Directory.Exists(SignaturesDir) && Directory.EnumerateFileSystemEntries(SignaturesDir).Any())
            ZipFile.CreateFromDirectory(SignaturesDir, Path.Combine(dest, "Signatures.zip"));
        return OpResult.Success($"Đã sao lưu {files.Count} file ({Fmt.Size(total)}) vào {dest}.");
    }

    /// <summary>Dựng lại OST: đổi tên file cũ thành .bak — Outlook tự tải lại hộp thư từ máy chủ lần mở tới.</summary>
    public static async Task<OpResult> RebuildOstAsync(OutlookDataFile f)
    {
        if (!f.IsOst) return OpResult.Fail("Chỉ dựng lại được file OST.");
        if (IsRunning() && !await CloseAsync()) return OpResult.Fail("Không đóng được Outlook.");
        var bak = f.Path + $".{DateTime.Now:yyyyMMdd-HHmm}.bak";
        try { File.Move(f.Path, bak); }
        catch (Exception ex) { return OpResult.Fail($"Không đổi tên được: {ex.Message}"); }
        return OpResult.Success($"Đã đổi {f.Name} thành {Path.GetFileName(bak)}. Mở Outlook — hộp thư sẽ được tải lại từ máy chủ (lâu với hộp thư lớn). Xoá file .bak khi mọi thứ ổn.");
    }

    public static OpResult OpenScanPst()
    {
        var exe = ScanPstExe();
        if (exe is null) return OpResult.Fail("Không tìm thấy SCANPST.EXE (Inbox Repair Tool) — Outlook chưa cài?");
        ShellLauncher.Open(exe);
        return OpResult.Success("Đã mở Inbox Repair Tool — chọn file PST/OST cần sửa (đóng Outlook trước).");
    }

    /// <summary>Mở Outlook với tham số sửa lỗi: /safe, /resetnavpane, /cleanviews, /cleanreminders, /resetfolders.</summary>
    public static async Task<OpResult> StartWithAsync(string sw)
    {
        var exe = OutlookExe();
        if (exe is null) return OpResult.Fail("Máy chưa cài Outlook (classic).");
        if (IsRunning() && !await CloseAsync()) return OpResult.Fail("Không đóng được Outlook đang mở.");
        // Outlook không nên chạy quyền admin → mở qua Explorer để chạy với quyền người dùng thường
        var cmd = Path.Combine(Path.GetTempPath(), $"ithd-outlook-{sw.Trim('/')}.cmd");
        await File.WriteAllTextAsync(cmd, $"@start \"\" \"{exe}\" {sw}\r\n");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{cmd}\"") { UseShellExecute = true });
        return OpResult.Success($"Đã mở Outlook với {sw}.");
    }

    public static OpResult OpenMailControlPanel()
    {
        var exe = OutlookExe();
        var cpl = exe is null ? null : Path.Combine(Path.GetDirectoryName(exe)!, "MLCFG32.CPL");
        if (cpl is null || !File.Exists(cpl)) return OpResult.Fail("Không tìm thấy Mail (Microsoft Outlook) trong Control Panel.");
        ShellLauncher.Open("control.exe", $"\"{cpl}\"");
        return OpResult.Success("Đã mở Mail Setup — quản lý hồ sơ và file dữ liệu.");
    }

    // ================= Chữ ký & danh sách gợi ý =================
    public static OpResult BackupSignatures(string zipPath)
    {
        if (!Directory.Exists(SignaturesDir) || !Directory.EnumerateFileSystemEntries(SignaturesDir).Any())
            return OpResult.Fail("Chưa có chữ ký email nào.");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(SignaturesDir, zipPath);
        return OpResult.Success($"Đã sao lưu chữ ký vào {zipPath}.");
    }

    public static OpResult RestoreSignatures(string zipPath)
    {
        Directory.CreateDirectory(SignaturesDir);
        ZipFile.ExtractToDirectory(zipPath, SignaturesDir, overwriteFiles: true);
        return OpResult.Success("Đã khôi phục chữ ký email. Mở lại Outlook để thấy chữ ký.");
    }
}
