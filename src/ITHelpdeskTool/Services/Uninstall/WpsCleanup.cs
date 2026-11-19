using System.IO;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.Startup;
using ITHelpdeskTool.Services.Tweaks;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Uninstall;

/// <summary>
/// Phần sót riêng của WPS Office mà cách dò chung (theo tên app / hãng) không thấy: cấu hình trong AppData\Roaming\kingsoft,
/// khoá Software\Kingsoft, liên kết mở file (.docx, .xlsx, .pdf…) và menu chuột phải trỏ về WPS, tác vụ WpsUpdate / WpsNotify,
/// mục khởi động cùng Windows, luật tường lửa (kể cả luật app tạo ở Tweaks → WPS Office).
/// Mọi mục đều đối chiếu theo đường dẫn file của WPS hoặc tên riêng của WPS, không xoá theo đoán tên chung.
/// </summary>
public static class WpsCleanup
{
    private const RegexOptions I = RegexOptions.IgnoreCase;

    public static bool IsWps(InstalledApp a) =>
        Regex.IsMatch(a.Name, @"^\s*(WPS Office|Kingsoft Office|WPS\b)", I) || a.Publisher.Contains("Kingsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chuỗi (lệnh, đường dẫn) trỏ vào thư mục của WPS.</summary>
    public static bool IsWpsPath(string? s) =>
        !string.IsNullOrEmpty(s) && (s.Contains(@"\Kingsoft\", StringComparison.OrdinalIgnoreCase) || s.Contains(@"\WPS Office\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Trình gỡ của WPS (uninst.exe) nhận /S để gỡ im lặng.</summary>
    public static string? SilentArgs(string file) =>
        IsWpsPath(file) && Path.GetFileName(file).Equals("uninst.exe", StringComparison.OrdinalIgnoreCase) ? "/S" : null;

    /// <param name="machineWide">Được dọn cả phần chung của máy (Program Files, HKLM) — chỉ khi máy không còn bản WPS nào khác.</param>
    public static List<Leftover> Find(bool machineWide, Func<string, long> dirSize, Func<string, string?> shortcutTarget)
    {
        var list = new List<Leftover>();
        void Dir(string path, string detail)
        {
            if (Directory.Exists(path)) list.Add(new Leftover(LeftoverKind.Folder, path, detail, true, dirSize(path)));
        }
        void Key(RegistryKey root, string rootName, string sub, string detail, bool high = true)
        {
            using var k = root.OpenSubKey(sub);
            if (k is not null) list.Add(new Leftover(LeftoverKind.RegistryKey, $@"{rootName}\{sub}", detail, high));
        }

        // ---- Thư mục ----
        var local = ActiveUser.Folder(Environment.SpecialFolder.LocalApplicationData);
        var roaming = ActiveUser.Folder(Environment.SpecialFolder.ApplicationData);
        Dir(Path.Combine(local, "Kingsoft"), "Thư mục cài / dữ liệu WPS (AppData\\Local)");
        Dir(Path.Combine(roaming, "kingsoft"), "Cấu hình, tài khoản, bộ nhớ đệm WPS (AppData\\Roaming)");
        Dir(Path.Combine(ActiveUser.ProfilePath, "AppData", "LocalLow", "Kingsoft"), "Dữ liệu WPS (AppData\\LocalLow)");
        if (machineWide)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var d in new[] { Path.Combine(pf, "Kingsoft", "WPS Office"), Path.Combine(pf86, "Kingsoft", "WPS Office"),
                         Path.Combine(pf, "WPS Office"), Path.Combine(pf86, "WPS Office") }.Distinct(StringComparer.OrdinalIgnoreCase))
                Dir(d, "Thư mục cài WPS cho cả máy");
            Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kingsoft"), "Dữ liệu chung của WPS (ProgramData)");
        }

        // ---- Lối tắt ngoài Desktop / Start Menu trỏ vào WPS ----
        foreach (var dir in new[]
                 {
                     ActiveUser.Folder(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                     ActiveUser.Folder(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
                if (IsWpsPath(shortcutTarget(lnk))) list.Add(new Leftover(LeftoverKind.Shortcut, lnk, "Lối tắt mở WPS", true));
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (Regex.IsMatch(Path.GetFileName(sub), @"^(WPS Office|Kingsoft|WPS)\b", I))
                    list.Add(new Leftover(LeftoverKind.Shortcut, sub, "Thư mục WPS trong Start Menu", true));
        }

        // ---- Registry: cấu hình ----
        Key(ActiveUser.Registry, ActiveUser.RegRoot, @"Software\Kingsoft", "Cấu hình WPS của tài khoản");
        if (machineWide)
        {
            Key(Registry.LocalMachine, "HKEY_LOCAL_MACHINE", @"SOFTWARE\Kingsoft", "Cấu hình WPS của máy");
            Key(Registry.LocalMachine, "HKEY_LOCAL_MACHINE", @"SOFTWARE\WOW6432Node\Kingsoft", "Cấu hình WPS của máy (32-bit)");
        }

        // ---- Liên kết mở file, giao thức, menu chuột phải ----
        var classRoots = new List<(RegistryKey Root, string Name, string Sub)> { (ActiveUser.Registry, ActiveUser.RegRoot, @"Software\Classes") };
        if (machineWide) classRoots.Add((Registry.LocalMachine, "HKEY_LOCAL_MACHINE", @"SOFTWARE\Classes"));
        var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, rootName, sub) in classRoots)
        {
            using var classes = root.OpenSubKey(sub);
            if (classes is null) continue;
            foreach (var name in classes.GetSubKeyNames())
            {
                if (name.StartsWith('.') || name.Equals("CLSID", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsWpsPath(Default(classes, $@"{name}\shell\open\command")) || IsWpsPath(Default(classes, $@"{name}\DefaultIcon")))
                {
                    progIds.Add(name);
                    list.Add(new Leftover(LeftoverKind.RegistryKey, $@"{rootName}\{sub}\{name}", "Kiểu file / giao thức mở bằng WPS", true));
                }
            }
            using var clsid = classes.OpenSubKey("CLSID");
            foreach (var id in clsid?.GetSubKeyNames() ?? [])
            {
                if (!IsWpsPath(Default(clsid!, $@"{id}\InprocServer32")) && !IsWpsPath(Default(clsid!, $@"{id}\LocalServer32"))) continue;
                clsids.Add(id);
                list.Add(new Leftover(LeftoverKind.RegistryKey, $@"{rootName}\{sub}\CLSID\{id}", "Thành phần COM / menu chuột phải của WPS", true));
            }
        }
        foreach (var (root, rootName, sub) in classRoots)
        {
            using var classes = root.OpenSubKey(sub);
            if (classes is null) continue;
            // .docx, .xlsx… mà tài khoản / máy đang đặt mặc định mở bằng WPS
            foreach (var ext in classes.GetSubKeyNames().Where(n => n.StartsWith('.')))
                if (Default(classes, ext) is { } prog && progIds.Contains(prog))
                    list.Add(new Leftover(LeftoverKind.RegistryKey, $@"{rootName}\{sub}\{ext}", $"Đuôi {ext} đang đặt mở bằng WPS ({prog})", true));
            // Menu chuột phải (Upload to WPS Cloud…)
            foreach (var owner in new[] { "*", "Directory", @"Directory\Background", "Folder", "AllFilesystemObjects", "Drive" })
            {
                var hk = $@"{owner}\shellex\ContextMenuHandlers";
                using var handlers = classes.OpenSubKey(hk);
                foreach (var h in handlers?.GetSubKeyNames() ?? [])
                {
                    var id = Default(handlers!, h) ?? h;
                    if (clsids.Contains(id) || Regex.IsMatch(h, @"^(WPS|Kingsoft|KSO)", I))
                        list.Add(new Leftover(LeftoverKind.RegistryKey, $@"{rootName}\{sub}\{hk}\{h}", $"Menu chuột phải: {h}", true));
                }
            }
        }
        // Lựa chọn "Luôn mở bằng" của người dùng trỏ về WPS → xoá để Windows trả về chương trình mặc định (Word, Excel… nếu có)
        using (var fileExts = ActiveUser.Registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts"))
        {
            foreach (var ext in fileExts?.GetSubKeyNames() ?? [])
                foreach (var uc in new[] { "UserChoice", "UserChoiceLatest" })
                {
                    using var k = fileExts!.OpenSubKey($@"{ext}\{uc}");
                    var prog = k?.GetValue("ProgId") as string ?? (k?.OpenSubKey("ProgId")?.GetValue("ProgId") as string);
                    if (prog is not null && (progIds.Contains(prog) || Regex.IsMatch(prog, @"^(KWPS|KET|KWPP|WPS)\.", I)))
                        list.Add(new Leftover(LeftoverKind.RegistryKey, $@"{ActiveUser.RegRoot}\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\{uc}",
                            $"{ext} đang mở bằng WPS — trả về chương trình mặc định", true));
                }
        }

        // ---- Tác vụ đã lên lịch ----
        foreach (var t in WpsEnv.ScheduledTasks())
        {
            var dir = t.Path[..(t.Path.LastIndexOf('\\') + 1)];
            list.Add(new Leftover(LeftoverKind.Task, $"{dir}|{t.Name}", $"{t.Name} — tác vụ của WPS", true));
        }

        // ---- Khởi động cùng Windows ----
        foreach (var e in StartupService.ReadAll())
            if (IsWpsPath(e.FilePath) || IsWpsPath(e.Command))
                list.Add(new Leftover(LeftoverKind.StartupEntry, $"{e.Source}|{e.RawName}", $"{e.Name} — {e.Command}", true));

        // ---- Dịch vụ ----
        if (machineWide)
        {
            using var svcs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var n in svcs?.GetSubKeyNames() ?? [])
            {
                using var s = svcs!.OpenSubKey(n);
                if (s?.GetValue("ImagePath") is string img && IsWpsPath(img))
                    list.Add(new Leftover(LeftoverKind.Service, n, $"{s.GetValue("DisplayName") ?? n} — {img}", true));
            }
        }

        // ---- Luật tường lửa (của WPS hoặc do app tạo ở Tweaks → WPS Office) ----
        foreach (var (id, name, app) in FirewallRules())
            if (name.StartsWith("ITHelpdeskTool WPS", StringComparison.OrdinalIgnoreCase) || IsWpsPath(app))
                list.Add(new Leftover(LeftoverKind.Firewall, id, $"{name} — {app}", true));

        return list;
    }

    private static string? Default(RegistryKey parent, string sub)
    {
        try
        {
            using var k = parent.OpenSubKey(sub);
            return k?.GetValue("") as string;
        }
        catch { return null; }
    }

    private const string FwRulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>(Mã luật, tên, file chạy) của các luật tường lửa lưu trên máy.</summary>
    public static List<(string Id, string Name, string App)> FirewallRules()
    {
        var list = new List<(string, string, string)>();
        using var k = Registry.LocalMachine.OpenSubKey(FwRulesKey);
        foreach (var id in k?.GetValueNames() ?? [])
        {
            if (k!.GetValue(id) is not string rule) continue;
            string app = "", name = "";
            foreach (var part in rule.Split('|'))
            {
                if (part.StartsWith("App=", StringComparison.OrdinalIgnoreCase)) app = part[4..];
                else if (part.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)) name = part[5..];
            }
            list.Add((id, name, app));
        }
        return list;
    }

    public static bool FirewallRuleExists(string id)
    {
        using var k = Registry.LocalMachine.OpenSubKey(FwRulesKey);
        return k?.GetValue(id) is not null;
    }
}
