using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Tweaks;

/// <summary>
/// Tweak không thuần registry: tự đọc trạng thái thật và tự áp dụng (tường lửa, tác vụ đã lên lịch, khởi động cùng Windows…).
/// </summary>
public interface ICustomTweak
{
    bool IsAvailable();
    /// <summary>Lý do không dùng được (hiện trên giao diện).</summary>
    string UnavailableText { get; }
    bool IsOn();
    /// <summary>Null = thành công.</summary>
    Task<OpResult?> ApplyAsync(bool on);
    /// <summary>Áp dụng cho cả máy (mọi tài khoản) chứ không riêng người đang đăng nhập.</summary>
    bool MachineWide => false;
}

/// <summary>
/// Một nhóm chặn của WPS Office cho người đang đăng nhập:
/// - chặn tường lửa chiều ra cho các file chạy có tên trong <see cref="Exes"/> (null = mọi file .exe của WPS),
/// - tắt tác vụ đã lên lịch có tên khớp <see cref="Tasks"/>,
/// - tắt khởi động cùng Windows các mục chạy những file đó (như Task Manager → Startup → Disable),
/// - tắt dịch vụ có tên khớp <see cref="Services"/>.
/// Trạng thái đọc từ thực tế máy (luật tường lửa, tác vụ, Startup), không dựa vào khoá cấu hình riêng của WPS
/// vì WPS hay đổi khoá giữa các phiên bản và tự ghi đè.
/// </summary>
public sealed class WpsBlock(string category, string[]? exes, string? tasks = null, string? services = null, bool killRunning = false) : ICustomTweak
{
    public string Category { get; } = category;
    public string[]? Exes { get; } = exes;
    // Mẫu rỗng sẽ khớp mọi tác vụ / dịch vụ → coi như không có
    public string? Tasks { get; } = string.IsNullOrEmpty(tasks) ? null : tasks;
    public string? Services { get; } = string.IsNullOrEmpty(services) ? null : services;

    private string RulePrefix => $"ITHelpdeskTool WPS {Category}: ";

    /// <summary>Có WPS và bản đang cài có thành phần để chặn.</summary>
    public bool IsAvailable() => WpsEnv.Installed && (Files().Count > 0 || MyTasks().Count > 0 || MyServices().Count > 0);
    public string UnavailableText => WpsEnv.Installed
        ? "Bản WPS đang cài không có thành phần này"
        : $"Chưa cài WPS Office cho tài khoản {ActiveUser.Name}";

    private bool Wanted(string file) =>
        Exes is null || Exes.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    private List<string> Files() => WpsEnv.ExeFiles().Where(Wanted).ToList();

    private List<WpsEnv.TaskInfo> MyTasks() =>
        Tasks is null ? [] : WpsEnv.ScheduledTasks().Where(t => Regex.IsMatch(t.Name, Tasks, RegexOptions.IgnoreCase)).ToList();

    private List<(string Name, string Data)> MyRunEntries() =>
        WpsEnv.RunEntries().Where(e => Exes is null
            ? WpsEnv.IsWpsPath(e.Data)
            : Exes.Any(x => e.Data.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();

    private List<string> MyServices() => Services is null ? [] : WpsEnv.ServiceNames(Services);

    public bool IsOn()
    {
        var files = Files();
        var tasks = MyTasks();
        var svcs = MyServices();
        // Không có gì để chặn (bản WPS này không có các thành phần đó) thì không coi là đang bật
        if (files.Count == 0 && tasks.Count == 0 && svcs.Count == 0) return false;
        var blocked = WpsEnv.BlockedPrograms();
        return files.All(blocked.Contains)
               && tasks.All(t => !t.Enabled)
               && MyRunEntries().All(e => WpsEnv.StartupDisabled(e.Name))
               && svcs.All(WpsEnv.ServiceDisabled);
    }

    public async Task<OpResult?> ApplyAsync(bool on)
    {
        var errors = new List<string>();

        // ---- Tường lửa ----
        if (on)
        {
            var blocked = WpsEnv.BlockedPrograms();
            var todo = Files().Where(f => !blocked.Contains(f)).ToList();
            if (todo.Count > 0)
            {
                var list = string.Join(",", todo.Select(ProcessRunner.PsQuote));
                var r = await ProcessRunner.PowerShellAsync(
                    $"foreach($p in @({list})){{ New-NetFirewallRule -DisplayName ({ProcessRunner.PsQuote(RulePrefix)} + [IO.Path]::GetFileName($p)) " +
                    $"-Group 'ITHelpdeskTool WPS' -Direction Outbound -Action Block -Program $p -Profile Any | Out-Null }}");
                if (!r.Success) errors.Add($"tường lửa: {OpResult.FirstLine(r.Text)}");
            }
        }
        else
        {
            var r = await ProcessRunner.PowerShellAsync(
                $"Get-NetFirewallRule -DisplayName {ProcessRunner.PsQuote(RulePrefix + "*")} -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
            if (!r.Success) errors.Add($"tường lửa: {OpResult.FirstLine(r.Text)}");
        }

        // ---- Tác vụ đã lên lịch ----
        foreach (var t in MyTasks().Where(t => t.Enabled == on))
        {
            var r = await ProcessRunner.ConsoleAsync("schtasks.exe", $"/change /tn \"{t.Path}\" {(on ? "/disable" : "/enable")}");
            if (!r.Success) errors.Add($"tác vụ {t.Name}: {OpResult.FirstLine(r.Text)}");
        }

        // ---- Khởi động cùng Windows ----
        foreach (var e in MyRunEntries())
        {
            try { WpsEnv.SetStartupDisabled(e.Name, on); }
            catch (Exception ex) { errors.Add($"khởi động cùng Windows {e.Name}: {ex.Message}"); }
        }

        // ---- Dịch vụ ----
        foreach (var s in MyServices())
        {
            var r = await ProcessRunner.ConsoleAsync("sc.exe", $"config \"{s}\" start= {(on ? "disabled" : "auto")}");
            if (!r.Success) { errors.Add($"dịch vụ {s}: {OpResult.FirstLine(r.Text)}"); continue; }
            if (on) await ProcessRunner.ConsoleAsync("sc.exe", $"stop \"{s}\"");
        }

        // Tắt luôn tiến trình đang chạy (vd cửa sổ quảng cáo đang mở) để có hiệu lực ngay
        if (on && killRunning && Exes is not null)
            WpsEnv.KillProcesses(Exes);

        WpsEnv.Invalidate();
        return errors.Count == 0 ? null : OpResult.Fail($"WPS – {Category}: {string.Join("; ", errors.Take(3))}");
    }
}

/// <summary>Tìm bản WPS Office đã cài, luật tường lửa, tác vụ, mục khởi động… của người đang đăng nhập.</summary>
public static class WpsEnv
{
    public sealed record TaskInfo(string Name, string Path, bool Enabled);

    /// <summary>Thư mục gốc có thể chứa WPS: bản cài theo tài khoản (AppData\Local) và bản cài cho cả máy.</summary>
    public static IEnumerable<string> Roots()
    {
        var local = ActiveUser.Folder(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return new[]
        {
            Path.Combine(local, "Kingsoft", "WPS Office"),
            Path.Combine(pf86, "Kingsoft", "WPS Office"),
            Path.Combine(pf, "Kingsoft", "WPS Office"),
            Path.Combine(pf, "WPS Office"),
            Path.Combine(pf86, "WPS Office"),
        }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
    }

    private static readonly Regex VersionDir = new(@"^\d+(\.\d+){2,3}$");

    // Quét thư mục WPS tốn thời gian → nhớ kết quả vài giây (một lượt đọc trạng thái gọi nhiều lần)
    private static List<string>? _exes;
    private static DateTime _exesAt;
    private static readonly object Gate = new();

    public static void Invalidate() { lock (Gate) _exes = null; }

    /// <summary>Mọi file .exe của WPS (gồm các thư mục phiên bản).</summary>
    public static List<string> ExeFiles()
    {
        lock (Gate)
        {
            if (_exes is not null && DateTime.UtcNow - _exesAt < TimeSpan.FromSeconds(5)) return _exes;
            var list = new List<string>();
            foreach (var root in Roots())
            {
                try { list.AddRange(Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)); }
                catch (Exception ex) { Log.Warn($"Quét {root}: {ex.Message}"); }
            }
            _exes = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _exesAt = DateTime.UtcNow;
            return _exes;
        }
    }

    /// <summary>Có WPS (một thư mục phiên bản có wps.exe hoặc ksolaunch.exe).</summary>
    public static bool Installed => ExeFiles().Any(f =>
        Path.GetFileName(f) is var n && (n.Equals("wps.exe", StringComparison.OrdinalIgnoreCase) || n.Equals("ksolaunch.exe", StringComparison.OrdinalIgnoreCase)));

    public static bool IsWpsPath(string s) =>
        Roots().Any(r => s.Contains(r, StringComparison.OrdinalIgnoreCase))
        || s.Contains(@"\Kingsoft\WPS Office\", StringComparison.OrdinalIgnoreCase)
        || s.Contains(@"\WPS Office\", StringComparison.OrdinalIgnoreCase);

    // ================= Tường lửa =================
    private const string FwRulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>Các file chạy đang bị một luật tường lửa (đang bật) chặn chiều ra. Đọc thẳng registry cho nhanh.</summary>
    public static HashSet<string> BlockedPrograms()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(FwRulesKey);
            if (k is null) return set;
            foreach (var name in k.GetValueNames())
            {
                if (k.GetValue(name) is not string rule) continue;
                string? app = null, action = null, dir = null, active = null;
                foreach (var part in rule.Split('|'))
                {
                    var i = part.IndexOf('=');
                    if (i <= 0) continue;
                    var val = part[(i + 1)..];
                    switch (part[..i])
                    {
                        case "App": app = val; break;
                        case "Action": action = val; break;
                        case "Dir": dir = val; break;
                        case "Active": active = val; break;
                    }
                }
                if (app is null || !"Block".Equals(action, StringComparison.OrdinalIgnoreCase)
                    || !"Out".Equals(dir, StringComparison.OrdinalIgnoreCase) || !"TRUE".Equals(active, StringComparison.OrdinalIgnoreCase)) continue;
                set.Add(ExpandPath(app));
            }
        }
        catch (Exception ex) { Log.Warn($"Đọc luật tường lửa: {ex.Message}"); }
        return set;
    }

    private static string ExpandPath(string p)
    {
        // Luật tạo khi chạy bằng tài khoản khác có thể ghi %LOCALAPPDATA% / %USERPROFILE% — đổi theo người đang đăng nhập
        var local = ActiveUser.Folder(Environment.SpecialFolder.LocalApplicationData);
        p = p.Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase)
             .Replace("%USERPROFILE%", ActiveUser.ProfilePath, StringComparison.OrdinalIgnoreCase);
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)); }
        catch { return p; }
    }

    // ================= Tác vụ đã lên lịch =================
    private static readonly string TasksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

    /// <summary>
    /// Tác vụ đã lên lịch có tên bắt đầu bằng Wps / PPTAssistant / Kingsoft của người đang đăng nhập
    /// (WPS tạo mỗi tài khoản một bộ, vd WpsUpdateTask_D152). Đọc file XML trong System32\Tasks.
    /// </summary>
    public static List<TaskInfo> ScheduledTasks()
    {
        var list = new List<TaskInfo>();
        if (!Directory.Exists(TasksDir)) return list;
        try
        {
            foreach (var file in Directory.EnumerateFiles(TasksDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!Regex.IsMatch(name, @"^(wps|pptassistant|kingsoft|ksoqing)", RegexOptions.IgnoreCase)) continue;
                try
                {
                    var x = XDocument.Load(file);
                    XNamespace ns = x.Root?.Name.Namespace ?? XNamespace.None;
                    var user = x.Descendants(ns + "UserId").FirstOrDefault()?.Value ?? "";
                    if (!BelongsToActiveUser(name, user)) continue;
                    var enabled = x.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
                    list.Add(new TaskInfo(name, "\\" + Path.GetRelativePath(TasksDir, file), !"false".Equals(enabled, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) { Log.Warn($"Đọc tác vụ {name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Đọc danh sách tác vụ: {ex.Message}"); }
        return list;
    }

    private static bool BelongsToActiveUser(string taskName, string userId)
    {
        if (userId.Length > 0)
            return userId.Equals(ActiveUser.Sid, StringComparison.OrdinalIgnoreCase)
                   || userId.Equals(ActiveUser.Account, StringComparison.OrdinalIgnoreCase)
                   || userId.Equals(ActiveUser.Name, StringComparison.OrdinalIgnoreCase)
                   || userId.EndsWith("\\" + ActiveUser.Name, StringComparison.OrdinalIgnoreCase);
        // Không ghi người chạy: tên tác vụ thường kèm "_tên tài khoản"
        return !taskName.Contains('_')
               || Regex.IsMatch(taskName, "_" + Regex.Escape(ActiveUser.Name) + "(_|$)", RegexOptions.IgnoreCase);
    }

    // ================= Khởi động cùng Windows =================
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Mục trong HKCU\...\Run (của người đang đăng nhập) chạy file của WPS.</summary>
    public static List<(string Name, string Data)> RunEntries()
    {
        var list = new List<(string, string)>();
        using var k = ActiveUser.Registry.OpenSubKey(RunKey);
        if (k is null) return list;
        foreach (var n in k.GetValueNames())
            if (k.GetValue(n) is string d && IsWpsPath(d)) list.Add((n, d));
        return list;
    }

    /// <summary>Đã tắt trong Startup (byte đầu lẻ = tắt, giống Task Manager).</summary>
    public static bool StartupDisabled(string name)
    {
        using var k = ActiveUser.Registry.OpenSubKey(ApprovedKey);
        return k?.GetValue(name) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
    }

    public static void SetStartupDisabled(string name, bool disabled)
    {
        using var k = ActiveUser.Registry.CreateSubKey(ApprovedKey, writable: true);
        var data = new byte[12];
        data[0] = disabled ? (byte)3 : (byte)2;
        if (disabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(name, data, RegistryValueKind.Binary);
    }

    // ================= Dịch vụ =================
    /// <summary>Dịch vụ có tên khớp mẫu và file chạy nằm trong thư mục WPS.</summary>
    public static List<string> ServiceNames(string pattern)
    {
        var list = new List<string>();
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (k is null) return list;
        foreach (var n in k.GetSubKeyNames())
        {
            if (!Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase)) continue;
            using var s = k.OpenSubKey(n);
            if (s?.GetValue("ImagePath") is string img && (IsWpsPath(img) || img.Contains("kingsoft", StringComparison.OrdinalIgnoreCase)))
                list.Add(n);
        }
        return list;
    }

    public static bool ServiceDisabled(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return k?.GetValue("Start") is int s && s == 4;
    }

    // ================= Tiến trình =================
    public static void KillProcesses(IEnumerable<string> exeNames)
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var exe in exeNames)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                using (p)
                {
                    if (p.SessionId != session) continue;
                    try { p.Kill(); } catch { }
                }
    }
}
