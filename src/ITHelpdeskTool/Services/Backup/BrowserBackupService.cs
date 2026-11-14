using System.Diagnostics;
using System.IO;
using System.Text;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Backup;

/// <summary>Một trình duyệt có trên máy. Root là thư mục dữ liệu được sao lưu nguyên (gồm mọi profile).</summary>
public sealed record Browser(string Key, string Name, string Glyph, string Family, string Root, string[] ProcNames)
{
    public long Size { get; init; }
    public int Profiles { get; init; }
    public bool Running => ProcNames.SelectMany(Process.GetProcessesByName).Any();
    public string Detail => string.Join(" · ", new[]
    {
        Family == "Firefox" ? "Firefox" : "Chromium",
        Profiles > 0 ? $"{Profiles} hồ sơ" : "",
        Size > 0 ? Fmt.Size(Size) : "",
    }.Where(s => s.Length > 0));
}

/// <summary>
/// Sao lưu / khôi phục nguyên profile của các trình duyệt (bookmark, mật khẩu đã lưu, cookie, tiện ích, lịch sử…)
/// bằng cách CHÉP FILE — không giải mã gì. Mật khẩu mở lại được khi khôi phục trên CÙNG máy / tài khoản Windows
/// (khóa DPAPI còn nguyên); hợp với việc cài lại trình duyệt. Cài lại Windows thì mật khẩu cần Export / đồng bộ của trình duyệt.
/// </summary>
public static class BrowserBackupService
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // Thư mục bộ nhớ đệm — bỏ qua khi sao lưu cho nhanh, nhẹ (không ảnh hưởng bookmark / mật khẩu)
    private static readonly string[] SkipDirs =
    [
        "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "Service Worker", "Crashpad",
        "component_crx_cache", "Component CRX Cache", "extensions_crx_cache", "Safe Browsing", "Greaselion",
        "optimization_guide_model_store", "segmentation_platform", "cache2", "startupCache", "shader-cache", "thumbnails",
    ];

    private static IEnumerable<Browser> Known() =>
    [
        new("chrome", "Google Chrome", "", "Chromium", Path.Combine(Local, @"Google\Chrome\User Data"), ["chrome"]),
        new("edge", "Microsoft Edge", "", "Chromium", Path.Combine(Local, @"Microsoft\Edge\User Data"), ["msedge"]),
        new("coccoc", "Cốc Cốc", "", "Chromium", Path.Combine(Local, @"CocCoc\Browser\User Data"), ["browser"]),
        new("brave", "Brave", "", "Chromium", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), ["brave"]),
        new("vivaldi", "Vivaldi", "", "Chromium", Path.Combine(Local, @"Vivaldi\User Data"), ["vivaldi"]),
        new("chromium", "Chromium", "", "Chromium", Path.Combine(Local, @"Chromium\User Data"), ["chrome"]),
        new("opera", "Opera", "", "Opera", Path.Combine(Roaming, @"Opera Software\Opera Stable"), ["opera"]),
        new("operagx", "Opera GX", "", "Opera", Path.Combine(Roaming, @"Opera Software\Opera GX Stable"), ["opera"]),
        new("firefox", "Mozilla Firefox", "", "Firefox", Path.Combine(Roaming, @"Mozilla\Firefox"), ["firefox"]),
    ];

    public static async Task<List<Browser>> DetectAsync() => await Task.Run(() =>
        Known().Where(b => Directory.Exists(b.Root))
               .Select(b => b with { Size = Measure(b.Root), Profiles = CountProfiles(b) })
               .Where(b => b.Size > 0)
               .ToList());

    private static int CountProfiles(Browser b)
    {
        try
        {
            return b.Family switch
            {
                "Firefox" => Directory.Exists(Path.Combine(b.Root, "Profiles")) ? Directory.GetDirectories(Path.Combine(b.Root, "Profiles")).Length : 1,
                "Opera" => 1,
                _ => Directory.GetDirectories(b.Root).Count(d => File.Exists(Path.Combine(d, "Preferences"))),   // Default, Profile 1…
            };
        }
        catch { return 0; }
    }

    private static long Measure(string dir)
    {
        long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (!SkipDirs.Any(s => f.FullName.Contains($"\\{s}\\", StringComparison.OrdinalIgnoreCase)))
                    bytes += f.Length;
        }
        catch { }
        return bytes;
    }

    public static List<Process> RunningProcs(Browser b) => b.ProcNames.SelectMany(Process.GetProcessesByName).ToList();

    public static async Task CloseAsync(Browser b)
    {
        foreach (var p in RunningProcs(b)) try { p.CloseMainWindow(); } catch { }
        for (var i = 0; i < 30 && RunningProcs(b).Count > 0; i++) await Task.Delay(500).ConfigureAwait(false);
        foreach (var p in RunningProcs(b)) try { p.Kill(true); } catch { }
        await Task.Delay(800).ConfigureAwait(false);
    }

    private static Task<ProcessResult> Robo(string src, string dst, Action<string> status, string name, CancellationToken ct)
    {
        var xd = string.Join(" ", SkipDirs.Select(d => $"\"{d}\""));
        var done = 0;
        return ProcessRunner.RunAsync("robocopy.exe",
            $"\"{src}\" \"{dst}\" /E /COPY:DAT /DCOPY:DAT /XJ /XD {xd} /R:1 /W:1 /MT:16 /NP /NDL /NJH /NJS /BYTES",
            line => { if (line.Trim().Length > 0 && ++done % 40 == 0) status($"Đang chép {name}: {done:N0} file…"); },
            ct, ProcessRunner.Oem);
    }

    /// <summary>Thư mục sao lưu của lần chạy.</summary>
    public static string NewSetDir(string root) => Path.Combine(root, $"TrinhDuyet-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}");

    /// <summary>Sao lưu nguyên dữ liệu một trình duyệt vào &lt;setDir&gt;\&lt;Key&gt; (đóng trình duyệt trước).</summary>
    public static async Task<OpResult> BackupAsync(Browser b, string setDir, Action<string> status, CancellationToken ct)
    {
        if (!Directory.Exists(b.Root)) return OpResult.Fail($"Không thấy dữ liệu {b.Name}.");
        if (RunningProcs(b).Count > 0) return OpResult.Fail($"{b.Name} đang mở — đóng {b.Name} trước khi sao lưu.");
        var dst = Path.Combine(setDir, b.Key);
        Directory.CreateDirectory(dst);
        status($"Đang chép {b.Name}…");
        var r = await Robo(b.Root, dst, status, b.Name, ct).ConfigureAwait(false);
        if (r.ExitCode >= 8) return OpResult.Fail($"{b.Name}: chép lỗi (robocopy {r.ExitCode}) — đóng hẳn {b.Name} rồi thử lại.");
        return OpResult.Success($"{b.Name}: đã sao lưu {Fmt.Size(Measure(dst))}.");
    }

    /// <summary>
    /// Khôi phục dữ liệu một trình duyệt từ thư mục sao lưu (đóng trình duyệt trước). Chỉ chép đè, không xoá dữ liệu mới hơn.
    /// Khôi phục trên máy / tài khoản Windows khác thì bookmark về được, mật khẩu đã lưu có thể không mở được (khóa DPAPI khác).
    /// </summary>
    public static async Task<OpResult> RestoreAsync(Browser b, string backupDir, Action<string> status, CancellationToken ct)
    {
        var src = Path.Combine(backupDir, b.Key);
        if (!Directory.Exists(src)) return OpResult.Fail($"Thư mục sao lưu không có dữ liệu {b.Name}.");
        if (RunningProcs(b).Count > 0) return OpResult.Fail($"{b.Name} đang mở — đóng {b.Name} trước khi khôi phục.");
        Directory.CreateDirectory(b.Root);
        status($"Đang khôi phục {b.Name}…");
        var r = await Robo(src, b.Root, status, b.Name, ct).ConfigureAwait(false);
        if (r.ExitCode >= 8) return OpResult.Fail($"{b.Name}: khôi phục lỗi (robocopy {r.ExitCode}).");
        return OpResult.Success($"{b.Name}: đã khôi phục. Mở {b.Name} kiểm tra bookmark và mật khẩu.");
    }

    /// <summary>Các trình duyệt có trong một thư mục sao lưu (để khôi phục).</summary>
    public static List<Browser> InBackup(string backupDir)
    {
        var known = Known().ToList();
        var list = new List<Browser>();
        try
        {
            foreach (var d in Directory.GetDirectories(backupDir))
            {
                var b = known.FirstOrDefault(x => x.Key.Equals(Path.GetFileName(d), StringComparison.OrdinalIgnoreCase));
                if (b is not null) list.Add(b with { Size = Measure(d) });
            }
        }
        catch { }
        return list;
    }

    public static void WriteManifest(string setDir, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SAO LƯU TRÌNH DUYỆT (IT Helpdesk Tool)");
        sb.AppendLine($"Máy: {Environment.MachineName} · Người sao lưu: {Environment.UserName} · Lúc: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Gồm bookmark, mật khẩu đã lưu, cookie, tiện ích, lịch sử của mọi hồ sơ.");
        sb.AppendLine("Khôi phục: mở lại IT Helpdesk Tool trên máy này, trang Sao lưu trình duyệt → Khôi phục, chọn thư mục này.");
        sb.AppendLine("- Cài lại TRÌNH DUYỆT (vẫn Windows cũ): bookmark và mật khẩu về đầy đủ.");
        sb.AppendLine("- Cài lại WINDOWS / máy khác: bookmark về được; mật khẩu đã lưu có thể KHÔNG mở được (khóa mã hóa của Windows đã khác)");
        sb.AppendLine("  — khi đó dùng Export mật khẩu hoặc đồng bộ tài khoản của chính trình duyệt.");
        sb.AppendLine("Bản sao lưu này chứa dữ liệu đăng nhập — giữ ở nơi an toàn (USB cất riêng), đừng để lẫn.");
        sb.AppendLine(new string('=', 78));
        foreach (var l in lines) sb.AppendLine(l);
        try { File.WriteAllText(Path.Combine(setDir, "DOC-TRUOC-KHI-KHOI-PHUC.txt"), sb.ToString(), new UTF8Encoding(true)); } catch { }
    }

    // ================= Mở trang Export / đồng bộ của trình duyệt (cho trường hợp cài lại Windows) =================
    private static string? ExeOf(Browser b)
    {
        string[] cands = b.Key switch
        {
            "chrome" => [@"Google\Chrome\Application\chrome.exe"],
            "edge" => [@"Microsoft\Edge\Application\msedge.exe"],
            "coccoc" => [@"CocCoc\Browser\Application\browser.exe"],
            "brave" => [@"BraveSoftware\Brave-Browser\Application\brave.exe"],
            "vivaldi" => [@"Vivaldi\Application\vivaldi.exe"],
            "opera" => [@"..\..\Local\Programs\Opera\opera.exe"],
            "operagx" => [@"..\..\Local\Programs\Opera GX\opera.exe"],
            "firefox" => [@"Mozilla Firefox\firefox.exe"],
            _ => [],
        };
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Local })
            foreach (var c in cands)
            {
                var p = Path.GetFullPath(Path.Combine(root, c));
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Mở trang quản lý mật khẩu (để bấm Export) của trình duyệt. Firefox mở Lockwise.</summary>
    public static OpResult OpenPasswordPage(Browser b)
    {
        var exe = ExeOf(b);
        if (exe is null) return OpResult.Fail($"Không tìm thấy file chạy của {b.Name}.");
        var url = b.Family == "Firefox" ? "about:logins" :
                  b.Key is "edge" ? "edge://settings/passwords" : "chrome://password-manager/passwords";
        try { Process.Start(new ProcessStartInfo(exe, url) { UseShellExecute = false }); return OpResult.Success($"Đã mở trang mật khẩu của {b.Name} — bấm Export để xuất file (trình duyệt sẽ hỏi xác thực Windows)."); }
        catch (Exception ex) { return OpResult.Fail($"Không mở được: {ex.Message}"); }
    }
}
