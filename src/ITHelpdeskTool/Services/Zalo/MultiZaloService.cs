using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Zalo;

public sealed class ZaloProfile
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>Thư mục dữ liệu Zalo của hồ sơ (tin nhắn, ảnh, phiên đăng nhập).</summary>
    public string DataDir => Path.Combine(Folder, @"AppData\Roaming\ZaloData");
    public string Launcher => Path.Combine(Folder, "MoZalo.vbs");
}

/// <summary>
/// Chạy nhiều tài khoản Zalo PC cùng lúc: mỗi hồ sơ là một thư mục AppData riêng. Zalo lưu dữ liệu theo biến môi trường
/// APPDATA / LOCALAPPDATA nên mỗi hồ sơ là một phiên Zalo độc lập (đăng nhập, tin nhắn, cài đặt tách biệt).
/// </summary>
public static class MultiZaloService
{
    private static string ConfigFile => Path.Combine(Path.GetDirectoryName(Log.Folder) ?? Log.Folder, "multizalo.json");
    public static string DefaultBase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiZalo");
    public static string MainDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZaloData");

    /// <summary>Thư mục cài Zalo (chứa Zalo.exe khởi chạy và thư mục phiên bản Zalo-x.y.z).</summary>
    public static string? InstallDir()
    {
        var c = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Zalo"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zalo"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zalo"),
        };
        return c.FirstOrDefault(d => File.Exists(Path.Combine(d, "Zalo.exe")));
    }

    /// <summary>Zalo.exe thật trong thư mục phiên bản mới nhất (Zalo.exe ở ngoài chỉ là trình khởi chạy, bỏ qua tham số).</summary>
    public static string? AppExe()
    {
        var root = InstallDir();
        if (root is null) return null;
        return Directory.GetDirectories(root, "Zalo-*")
            .Select(d => Path.Combine(d, "Zalo.exe")).Where(File.Exists)
            .OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault() ?? Path.Combine(root, "Zalo.exe");
    }

    public static string Version() => AppExe() is { } e ? FileVersionInfo.GetVersionInfo(e).ProductVersion ?? "" : "";

    // ================= Hồ sơ =================
    public static List<ZaloProfile> Load()
    {
        try { return File.Exists(ConfigFile) ? JsonSerializer.Deserialize<List<ZaloProfile>>(File.ReadAllText(ConfigFile)) ?? [] : []; }
        catch { return []; }
    }

    private static void Save(List<ZaloProfile> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Safe(string name) => string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();

    public static OpResult Create(string name, string baseDir)
    {
        name = name.Trim();
        if (Safe(name).Length == 0) return OpResult.Fail("Nhập tên hồ sơ (vd Zalo công việc).");
        var list = Load();
        if (list.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return OpResult.Fail($"Đã có hồ sơ “{name}”.");
        var p = new ZaloProfile { Name = name, Folder = Path.Combine(baseDir, Safe(name)) };
        Directory.CreateDirectory(Path.Combine(p.Folder, @"AppData\Roaming"));
        Directory.CreateDirectory(Path.Combine(p.Folder, @"AppData\Local"));
        LinkUserFolders(p);
        WriteLauncher(p);
        list.Add(p);
        Save(list);
        return OpResult.Success($"Đã tạo hồ sơ “{name}” tại {p.Folder}.");
    }

    private static readonly Environment.SpecialFolder[] Linked =
    [
        Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
        Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
    ];

    /// <summary>Thư mục trong hồ sơ trỏ (junction) tới thư mục thật của người dùng.</summary>
    private static IEnumerable<(string Link, string Target)> Links(ZaloProfile p)
    {
        foreach (var f in Linked)
        {
            var target = Environment.GetFolderPath(f);
            if (target.Length > 0) yield return (Path.Combine(p.Folder, Path.GetFileName(target)), target);
        }
        var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return (Path.Combine(p.Folder, "Downloads"), dl);
    }

    private static void LinkUserFolders(ZaloProfile p)
    {
        foreach (var (link, target) in Links(p))
        {
            if (Directory.Exists(link) || !Directory.Exists(target)) continue;
            ProcessRunner.ConsoleAsync("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"").GetAwaiter().GetResult();
        }
    }

    /// <summary>Gỡ các junction trước khi xoá hồ sơ để không bao giờ xoá nhầm vào thư mục thật.</summary>
    private static void UnlinkUserFolders(ZaloProfile p)
    {
        foreach (var (link, _) in Links(p))
        {
            try
            {
                var di = new DirectoryInfo(link);
                if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint)) di.Delete(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// Tạo file MoZalo.vbs: đặt APPDATA / LOCALAPPDATA về thư mục của hồ sơ rồi chạy Zalo (tìm thư mục phiên bản mới nhất mỗi lần,
    /// nên vẫn chạy được sau khi Zalo tự cập nhật). Chạy qua wscript nên không hiện cửa sổ đen.
    /// </summary>
    public static void WriteLauncher(ZaloProfile p)
    {
        var root = InstallDir() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Zalo");
        string V(string s) => s.Replace("\"", "\"\"");
        var vbs = $"""
            ' Mo Zalo voi ho so rieng "{V(p.Name)}" - tao boi IT Helpdesk Tool
            Set sh = CreateObject("WScript.Shell")
            Set fso = CreateObject("Scripting.FileSystemObject")
            root = "{V(root)}"
            exe = "" : newest = 0
            If fso.FolderExists(root) Then
              For Each f In fso.GetFolder(root).SubFolders
                If LCase(Left(f.Name, 5)) = "zalo-" And fso.FileExists(f.Path & "\Zalo.exe") Then
                  If CDbl(fso.GetFile(f.Path & "\Zalo.exe").DateLastModified) > newest Then
                    newest = CDbl(fso.GetFile(f.Path & "\Zalo.exe").DateLastModified) : exe = f.Path & "\Zalo.exe"
                  End If
                End If
              Next
            End If
            If exe = "" Then
              MsgBox "Khong tim thay Zalo trong " & root & ". Hay cai Zalo PC truoc.", 48, "Multi-Zalo"
              WScript.Quit 1
            End If
            Set env = sh.Environment("Process")
            env("APPDATA") = "{V(Path.Combine(p.Folder, @"AppData\Roaming"))}"
            env("LOCALAPPDATA") = "{V(Path.Combine(p.Folder, @"AppData\Local"))}"
            env("USERPROFILE") = "{V(p.Folder)}"
            sh.Run Chr(34) & exe & Chr(34), 1, False
            """;
        Directory.CreateDirectory(p.Folder);
        // VBScript đọc file theo bảng mã ANSI — chỉ dùng ký tự ASCII ở trên, đường dẫn có dấu ghi bằng UTF-16 (có BOM) để giữ nguyên
        File.WriteAllText(p.Launcher, vbs, Encoding.Unicode);
    }

    /// <summary>Mở Zalo với hồ sơ (chạy qua Explorer để Zalo chạy với quyền người dùng thường, không phải quyền admin của app).</summary>
    public static OpResult Start(ZaloProfile p)
    {
        if (AppExe() is null) return OpResult.Fail("Máy chưa cài Zalo PC — cài ở trang App Store.");
        WriteLauncher(p);
        LinkUserFolders(p);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.Launcher}\"") { UseShellExecute = true });
        return OpResult.Success($"Đang mở Zalo “{p.Name}”.");
    }

    public static OpResult StartMain()
    {
        var root = InstallDir();
        if (root is null) return OpResult.Fail("Máy chưa cài Zalo PC.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(root, "Zalo.exe")}\"") { UseShellExecute = true });
        return OpResult.Success("Đang mở Zalo chính.");
    }

    public static OpResult CreateShortcut(ZaloProfile p)
    {
        if (!File.Exists(p.Launcher)) WriteLauncher(p);
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnk = Path.Combine(desk, $"Zalo - {Safe(p.Name)}.lnk");
        var wscript = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        ShellLink.Create(lnk, wscript, $"\"{p.Launcher}\"", p.Folder, $"Zalo — {p.Name}");
        // Đặt biểu tượng Zalo thay cho biểu tượng wscript
        if (AppExe() is { } exe)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic sh = Activator.CreateInstance(t)!;
            var l = sh.CreateShortcut(lnk);
            l.IconLocation = exe + ",0";
            l.Save();
        }
        return OpResult.Success($"Đã tạo lối tắt “Zalo - {p.Name}” ngoài Desktop.");
    }

    public static OpResult Rename(ZaloProfile p, string name)
    {
        name = name.Trim();
        if (Safe(name).Length == 0) return OpResult.Fail("Tên không hợp lệ.");
        var list = Load();
        var x = list.FirstOrDefault(i => i.Folder.Equals(p.Folder, StringComparison.OrdinalIgnoreCase));
        if (x is null) return OpResult.Fail("Không thấy hồ sơ.");
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Zalo - {Safe(x.Name)}.lnk");
        var hadShortcut = File.Exists(old);
        x.Name = name;
        Save(list);
        WriteLauncher(x);
        if (hadShortcut) { File.Delete(old); CreateShortcut(x); }
        return OpResult.Success($"Đã đổi tên thành “{name}”.");
    }

    /// <summary>Hồ sơ đang mở: file lockfile của Zalo đang bị giữ.</summary>
    public static bool IsRunning(string dataDir)
    {
        var lf = Path.Combine(dataDir, "lockfile");
        if (!File.Exists(lf)) return false;
        try { using var fs = new FileStream(lf, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public static long Size(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long t = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try { t += f.Length; } catch { }
        }
        catch { }
        return t;
    }

    public static OpResult Delete(ZaloProfile p)
    {
        if (IsRunning(p.DataDir)) return OpResult.Fail($"Zalo “{p.Name}” đang mở — đóng trước khi xoá.");
        UnlinkUserFolders(p);
        if (Links(p).Any(l => Directory.Exists(l.Link) && new DirectoryInfo(l.Link).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            return OpResult.Fail("Không gỡ được liên kết thư mục trong hồ sơ — dừng để không xoá nhầm dữ liệu thật.");
        try { if (Directory.Exists(p.Folder)) Directory.Delete(p.Folder, true); }
        catch (Exception ex) { return OpResult.Fail($"Không xoá được thư mục: {ex.Message}"); }
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Zalo - {Safe(p.Name)}.lnk");
        try { File.Delete(lnk); } catch { }
        var list = Load();
        list.RemoveAll(i => i.Folder.Equals(p.Folder, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return OpResult.Success($"Đã xoá hồ sơ “{p.Name}” và dữ liệu của nó.");
    }

    /// <summary>Đóng mọi cửa sổ Zalo.</summary>
    public static async Task<int> CloseAllAsync()
    {
        var ps = Process.GetProcessesByName("Zalo");
        foreach (var p in ps) try { p.Kill(); } catch { }
        await Task.Delay(1500);
        return ps.Length;
    }
}
