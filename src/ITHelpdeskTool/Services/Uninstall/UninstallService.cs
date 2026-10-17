using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ITHelpdeskTool.Services.Startup;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Uninstall;

public enum AppScope { Machine64, Machine32, User }

public sealed record InstalledApp(
    string KeyName, AppScope Scope, string Name, string Version, string Publisher, string InstallDate, long SizeKb,
    string UninstallString, string QuietUninstallString, string InstallLocation, string DisplayIcon, ImageSource? Icon)
{
    public string RegistryPath => Scope switch
    {
        AppScope.User => $@"HKEY_CURRENT_USER\{UninstallService.UninstallKey}\{KeyName}",
        AppScope.Machine32 => $@"HKEY_LOCAL_MACHINE\{UninstallService.Uninstall32Key}\{KeyName}",
        _ => $@"HKEY_LOCAL_MACHINE\{UninstallService.UninstallKey}\{KeyName}",
    };

    public string? ProductCode => UninstallService.MsiCode(UninstallString) ?? (Guid.TryParse(KeyName, out _) && KeyName.StartsWith('{') ? KeyName : null);
    public bool IsMsi => UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && ProductCode is not null;
    public bool HasUninstaller => UninstallString.Length > 0 || QuietUninstallString.Length > 0;
    public string SizeText => SizeKb > 0 ? Fmt.Size(SizeKb * 1024d) : "";
    public string Detail => string.Join(" · ", new[] { Publisher, Version, InstallDate, SizeText, Scope == AppScope.User ? "chỉ tài khoản này" : "" }.Where(x => x.Length > 0));
}

public enum LeftoverKind { Folder, File, RegistryKey, Shortcut, StartupEntry, Service, Task }

public sealed record Leftover(LeftoverKind Kind, string Path, string Detail, bool HighConfidence, long Bytes = 0)
{
    public string KindText => Kind switch
    {
        LeftoverKind.Folder => "Thư mục",
        LeftoverKind.File => "File",
        LeftoverKind.RegistryKey => "Registry",
        LeftoverKind.Shortcut => "Lối tắt",
        LeftoverKind.StartupEntry => "Khởi động",
        LeftoverKind.Service => "Dịch vụ",
        _ => "Tác vụ",
    };
}

/// <summary>Gỡ phần mềm: chạy trình gỡ của hãng, rồi tìm và dọn phần còn sót (file, registry, dịch vụ...).</summary>
public static class UninstallService
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Uninstall32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static string BackupRoot => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.Folder) ?? Log.Folder, "backup", "uninstall");

    // ================= Danh sách =================
    public static List<InstalledApp> ReadApps()
    {
        var list = new List<InstalledApp>();
        void Scan(RegistryKey root, string path, AppScope scope)
        {
            using var k = root.OpenSubKey(path);
            if (k is null) return;
            foreach (var sub in k.GetSubKeyNames())
            {
                try
                {
                    using var a = k.OpenSubKey(sub);
                    if (a?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (a.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (a.GetValue("ParentKeyName") is not null) continue;
                    if (a.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                    string S(string n) => (a.GetValue(n) as string ?? "").Trim();
                    var date = S("InstallDate");
                    date = DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? Fmt.Date(dt) : "";
                    var icon = IconPath(S("DisplayIcon"), S("InstallLocation"), S("UninstallString"));
                    list.Add(new InstalledApp(sub, scope, name.Trim(), S("DisplayVersion"), S("Publisher"), date,
                        a.GetValue("EstimatedSize") is int kb ? kb : 0,
                        S("UninstallString"), S("QuietUninstallString"), S("InstallLocation").Trim('"').TrimEnd('\\'), S("DisplayIcon"),
                        IconHelper.Get(icon)));
                }
                catch { }
            }
        }
        Scan(Registry.LocalMachine, UninstallKey, AppScope.Machine64);
        Scan(Registry.LocalMachine, Uninstall32Key, AppScope.Machine32);
        Scan(Registry.CurrentUser, UninstallKey, AppScope.User);
        return list.GroupBy(a => (a.Name, a.Version)).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? IconPath(string displayIcon, string installLocation, string uninstall)
    {
        if (displayIcon.Length > 0)
        {
            var p = Environment.ExpandEnvironmentVariables(displayIcon.Trim('"'));
            var comma = p.LastIndexOf(',');
            if (comma > 2 && !File.Exists(p)) p = p[..comma].Trim('"');
            if (File.Exists(p)) return p;
        }
        var u = StartupService.ExtractExe(uninstall);
        if (File.Exists(u) && !Path.GetFileName(u).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return u;
        return null;
    }

    public static bool StillInstalled(InstalledApp a)
    {
        var (root, path) = KeyLocation(a);
        using var k = root.OpenSubKey($@"{path}\{a.KeyName}");
        return k is not null;
    }

    private static (RegistryKey Root, string Path) KeyLocation(InstalledApp a) => a.Scope switch
    {
        AppScope.User => (Registry.CurrentUser, UninstallKey),
        AppScope.Machine32 => (Registry.LocalMachine, Uninstall32Key),
        _ => (Registry.LocalMachine, UninstallKey),
    };

    // ================= Chạy trình gỡ =================
    internal static string? MsiCode(string s)
    {
        var m = Regex.Match(s, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        return s.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && m.Success ? m.Value : null;
    }

    /// <summary>Kiểu bộ cài để đoán tham số gỡ im lặng.</summary>
    public static string DetectInstaller(string exe)
    {
        var name = Path.GetFileName(exe);
        if (Regex.IsMatch(name, @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)) return "inno";
        try
        {
            if (!File.Exists(exe)) return "";
            using var fs = File.OpenRead(exe);
            var buf = new byte[Math.Min(fs.Length, 2 * 1024 * 1024)];
            var n = fs.Read(buf, 0, buf.Length);
            var text = Encoding.ASCII.GetString(buf, 0, n);
            if (text.Contains("Nullsoft", StringComparison.Ordinal)) return "nsis";
            if (text.Contains("Inno Setup", StringComparison.Ordinal)) return "inno";
        }
        catch { }
        return "";
    }

    /// <summary>Lệnh gỡ sẽ chạy (file, tham số). quiet = gỡ im lặng nếu đoán được cách.</summary>
    public static (string File, string Args, bool Silent) BuildCommand(InstalledApp a, bool quiet)
    {
        if (a.ProductCode is { } code && a.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return ("msiexec.exe", $"/x {code}" + (quiet ? " /qn /norestart" : ""), quiet);

        var cmd = quiet && a.QuietUninstallString.Length > 0 ? a.QuietUninstallString : a.UninstallString;
        var silent = quiet && a.QuietUninstallString.Length > 0;
        var c = cmd.Trim();
        string file, args;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            file = end > 0 ? c[1..end] : c.Trim('"');
            args = end > 0 ? c[(end + 1)..].Trim() : "";
        }
        else
        {
            file = StartupService.ExtractExe(c);
            var idx = c.IndexOf(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
            args = idx >= 0 ? c[(idx + Path.GetFileName(file).Length)..].Trim().TrimStart('"').Trim() : "";
            if (!File.Exists(file)) { file = c; args = ""; }
        }
        file = Environment.ExpandEnvironmentVariables(file);

        if (quiet && !silent)
        {
            switch (DetectInstaller(file))
            {
                case "inno": args = (args + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim(); silent = true; break;
                case "nsis": args = (args + " /S").Trim(); silent = true; break;
            }
        }
        return (file, args, silent);
    }

    /// <summary>Chạy trình gỡ và chờ nó thoát. Trả mã thoát (hoặc lỗi khởi chạy).</summary>
    public static async Task<OpResult> RunUninstallerAsync(InstalledApp a, bool quiet, CancellationToken ct = default)
    {
        if (!a.HasUninstaller) return OpResult.Fail("Phần mềm này không có trình gỡ — dùng “Dọn phần còn sót” để xoá.");
        var (file, args, silent) = BuildCommand(a, quiet);
        // MSI im lặng: ghi nhật ký để biết lý do khi trình gỡ từ chối (không có cửa sổ báo lỗi nào hiện ra)
        string? msiLog = null;
        if (silent && file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            msiLog = Path.Combine(Path.GetTempPath(), $"ITHelpdesk-uninstall-{Guid.NewGuid():N}.log");
            args += $" /l*v \"{msiLog}\"";
        }
        Log.Info($"Gỡ {a.Name}: {file} {args}");
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = !silent,
                CreateNoWindow = silent,
                WorkingDirectory = File.Exists(file) ? Path.GetDirectoryName(file)! : Environment.SystemDirectory,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("không khởi chạy được");
            await p.WaitForExitAsync(ct);
            var code = p.ExitCode;
            // 0 = xong, 3010/1641 = cần khởi động lại, 1605 = MSI không còn cài
            if (code is 0 or 3010 or 1641 or 1605)
            {
                TryDelete(msiLog);
                return OpResult.Success($"Trình gỡ {a.Name} đã chạy xong" + (code is 3010 or 1641 ? " (cần khởi động lại)." : "."), code is 3010 or 1641);
            }
            var why = MsiCodeText(code);
            var detail = msiLog is null ? null : MsiLogReason(msiLog);
            TryDelete(msiLog);
            return OpResult.Fail($"Không gỡ được {a.Name} (mã {code}{(why is null ? "" : " — " + why)})." +
                (detail is null ? "" : $"\nLý do từ trình gỡ: {detail}") +
                (silent ? "\nThử bỏ tích “Gỡ im lặng nếu được” rồi gỡ lại để xem thông báo của hãng." : ""));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return OpResult.Fail($"Không chạy được trình gỡ: {ex.Message}"); }
    }

    private static void TryDelete(string? f) { if (f is null) return; try { File.Delete(f); } catch { } }

    private static string? MsiCodeText(int code) => code switch
    {
        1602 => "người dùng huỷ",
        1603 => "trình gỡ báo lỗi nghiêm trọng",
        1618 => "đang có một trình cài/gỡ khác chạy, chờ nó xong rồi thử lại",
        1619 or 1620 => "không mở được gói cài đặt",
        1624 => "lỗi áp dụng gói cấu hình",
        1638 => "đang có phiên bản khác của sản phẩm",
        1639 => "tham số không hợp lệ",
        1722 or 1721 => "một bước tuỳ chỉnh của trình gỡ bị lỗi",
        1730 => "cần quyền quản trị",
        _ => null,
    };

    /// <summary>Đọc lý do lỗi trong nhật ký msiexec (thông báo của hãng, hoặc dòng "Error ...").</summary>
    private static string? MsiLogReason(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);   // msiexec ghi UTF-16 có BOM — ReadAllLines tự nhận
            string? Pick(Func<string, bool> f) => lines.Reverse().FirstOrDefault(f)?.Trim();
            var m = Pick(l => Regex.IsMatch(l, @"^(MSI \(s\) .*?)?Product: .* -- ", RegexOptions.IgnoreCase))
                    ?? Pick(l => l.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) || l.Contains("-- Error ", StringComparison.OrdinalIgnoreCase));
            if (m is null) return null;
            var i = m.IndexOf(" -- ", StringComparison.Ordinal);
            var t = i >= 0 ? m[(i + 4)..] : m;
            return t.Length > 400 ? t[..400] + "…" : t;
        }
        catch { return null; }
    }

    // ================= Tìm phần còn sót =================
    private static readonly string[] Generic =
    [
        "microsoft", "windows", "common files", "programs", "program files", "programdata", "appdata", "local", "roaming",
        "packages", "temp", "users", "public", "system32", "syswow64", "google", "intel", "nvidia", "amd", "classes", "policies",
        "wow6432node", "software", "default", "app", "apps", "data", "bin", "setup", "update", "updater", "tools", "drivers",
    ];

    /// <summary>Bỏ số phiên bản, chữ x64/64-bit... khỏi tên để so với tên thư mục.</summary>
    public static string CleanName(string name)
    {
        var s = Regex.Replace(name, @"\((x64|x86|64-bit|32-bit|amd64|arm64)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(x64|x86|64-bit|32-bit)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+v?\d+(\.\d+)+.*$", "");          // " 1.2.3", " v2.0 (build...)"
        s = Regex.Replace(s, @"\s+(19|20)\d{2}$", "");               // năm ở cuối
        return Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '–');
    }

    private const string Suffix = @",?\s*(Inc|Ltd|LLC|GmbH|Corp|Corporation|Co|Limited|S\.?A|AB|Pty|Software|Technologies|Technology)\.?$";

    public static string CleanPublisher(string pub) =>
        Regex.Replace(pub.Trim(' ', ',', '.'), Suffix, "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');

    /// <summary>Các tên thư mục hãng có thể dùng: "HDTest Corp, Inc." → "HDTest Corp", "HDTest".</summary>
    public static List<string> PublisherNames(string pub)
    {
        var a = CleanPublisher(pub);
        return new[] { a, CleanPublisher(a) }.Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Usable(string n) => n.Length >= 3 && !Generic.Contains(n.ToLowerInvariant());

    private static bool IsUnder(string path, string root) =>
        root.Length > 3 && path.TrimEnd('\\').StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
        || path.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Thư mục không bao giờ được đề xuất xoá.</summary>
    private static bool IsProtected(string dir)
    {
        var d = dir.TrimEnd('\\');
        var roots = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
        }.Select(Environment.GetFolderPath).Where(x => x.Length > 0).ToList();
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        roots.Add(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'));
        roots.Add(@"C:\Users");
        // Thư mục gốc hoặc thư mục cha của các vị trí hệ thống → cấm
        return d.Length <= 3 || roots.Any(r => r.TrimEnd('\\').Equals(d, StringComparison.OrdinalIgnoreCase) || IsUnder(r, d))
               || IsUnder(d, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    private static long DirSize(string dir)
    {
        long t = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                try { t += f.Length; } catch { }
        }
        catch { }
        return t;
    }

    /// <summary>
    /// Tìm phần còn sót của một phần mềm. Chỉ đề xuất mục khớp tên chính xác hoặc nằm trong thư mục cài đặt,
    /// và bỏ qua thư mục đang thuộc phần mềm khác còn cài.
    /// </summary>
    public static async Task<List<Leftover>> FindLeftoversAsync(InstalledApp a, IReadOnlyList<InstalledApp>? others = null)
    {
        others ??= await Task.Run(ReadApps);
        var otherLocations = others.Where(o => o.KeyName != a.KeyName && o.InstallLocation.Length > 3)
            .Select(o => o.InstallLocation).ToList();

        var name = CleanName(a.Name);
        var pubs = PublisherNames(a.Publisher);
        var loc = a.InstallLocation.Length > 3 ? Environment.ExpandEnvironmentVariables(a.InstallLocation) : "";
        if (loc.Length > 0 && IsProtected(loc)) loc = "";           // dữ liệu registry sai (vd InstallLocation = C:\Program Files)
        if (loc.Length == 0)
        {
            var u = StartupService.ExtractExe(a.UninstallString);
            var dir = Path.GetDirectoryName(u) ?? "";
            if (File.Exists(u) && !IsProtected(dir) && !Path.GetFileName(u).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                loc = dir;
        }
        var names = new[] { name, loc.Length > 0 ? Path.GetFileName(loc) : "" }
            .Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var list = new List<Leftover>();
        void AddDir(string dir, bool high, string detail)
        {
            if (!Directory.Exists(dir) || IsProtected(dir)) return;
            if (otherLocations.Any(o => IsUnder(o, dir) || IsUnder(dir, o))) return;   // thuộc phần mềm khác
            if (list.Any(l => l.Kind == LeftoverKind.Folder && l.HighConfidence && IsUnder(dir, l.Path))) return;
            // Thư mục cha tin cậy cao thay cho các thư mục con; cha tin cậy thấp thì giữ con (đã tích sẵn)
            if (high) list.RemoveAll(l => l.Kind == LeftoverKind.Folder && IsUnder(l.Path, dir));
            list.Add(new Leftover(LeftoverKind.Folder, dir, detail, high, DirSize(dir)));
        }

        await Task.Run(() =>
        {
            // --- Thư mục ---
            if (loc.Length > 0) AddDir(loc, true, "Thư mục cài đặt");
            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            }.Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var b in bases)
            {
                foreach (var n in names) AddDir(Path.Combine(b, n), true, "Dữ liệu / cấu hình còn lại");
                foreach (var pub in pubs)
                {
                    var pubDir = Path.Combine(b, pub);
                    foreach (var n in names) AddDir(Path.Combine(pubDir, n), true, "Dữ liệu / cấu hình còn lại");
                    // Thư mục hãng chỉ còn mỗi phần của app này → đề xuất xoá cả thư mục hãng (độ tin cậy vừa)
                    if (Directory.Exists(pubDir))
                    {
                        var children = Directory.EnumerateFileSystemEntries(pubDir).ToList();
                        if (children.All(c => names.Any(n => Path.GetFileName(c).Equals(n, StringComparison.OrdinalIgnoreCase))))
                            AddDir(pubDir, false, $"Thư mục của hãng {a.Publisher} (không còn phần mềm nào khác)");
                    }
                }
            }

            // --- Lối tắt Start Menu / Desktop ---
            foreach (var sm in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
            {
                if (!Directory.Exists(sm)) continue;
                foreach (var n in names.Concat(pubs))
                {
                    var d = Path.Combine(sm, n);
                    if (Directory.Exists(d)) list.Add(new Leftover(LeftoverKind.Shortcut, d, "Thư mục trong Start Menu", true));
                    var f = Path.Combine(sm, n + ".lnk");
                    if (File.Exists(f)) list.Add(new Leftover(LeftoverKind.Shortcut, f, "Lối tắt trong Start Menu", true));
                }
            }
            if (loc.Length > 0)
            {
                foreach (var desk in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
                {
                    if (!Directory.Exists(desk)) continue;
                    foreach (var lnk in Directory.GetFiles(desk, "*.lnk"))
                        if (ShortcutTarget(lnk) is { } t && IsUnder(t, loc))
                            list.Add(new Leftover(LeftoverKind.Shortcut, lnk, "Lối tắt ngoài Desktop", true));
                }
            }

            // --- Registry ---
            if (StillInstalled(a))
                list.Add(new Leftover(LeftoverKind.RegistryKey, a.RegistryPath, "Mục trong danh sách phần mềm đã cài", true));
            foreach (var (root, rootName) in new[] { (Registry.CurrentUser, "HKEY_CURRENT_USER"), (Registry.LocalMachine, "HKEY_LOCAL_MACHINE") })
            {
                foreach (var sw in new[] { "Software", @"Software\WOW6432Node" })
                {
                    if (root == Registry.CurrentUser && sw.Contains("WOW")) continue;
                    foreach (var n in names)
                    {
                        AddKey(root, rootName, $@"{sw}\{n}", true, "Cấu hình trong registry");
                        foreach (var pub in pubs) AddKey(root, rootName, $@"{sw}\{pub}\{n}", true, "Cấu hình trong registry");
                    }
                    foreach (var pub in pubs)
                    {
                        using var pk = root.OpenSubKey($@"{sw}\{pub}");
                        if (pk is not null && pk.GetValueNames().Length == 0 &&
                            pk.GetSubKeyNames().All(s => names.Any(n => s.Equals(n, StringComparison.OrdinalIgnoreCase))))
                            AddKey(root, rootName, $@"{sw}\{pub}", false, $"Khoá của hãng {a.Publisher} (không còn gì khác)");
                    }
                }
            }

            // --- Mục khởi động ---
            foreach (var e in StartupService.ReadAll())
            {
                var hit = loc.Length > 0 && IsUnder(e.FilePath, loc)
                          || !e.FileExists && names.Any(n => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (hit) list.Add(new Leftover(LeftoverKind.StartupEntry, $"{e.Source}|{e.RawName}", $"{e.Name} — {e.Command}", true));
            }

            // --- Dịch vụ ---
            if (loc.Length > 0)
                foreach (var s in Wmi.TryQuery("SELECT Name, DisplayName, PathName FROM Win32_Service"))
                {
                    var exe = StartupService.ExtractExe(s.Str("PathName"));
                    if (exe.Length > 0 && IsUnder(exe, loc))
                        list.Add(new Leftover(LeftoverKind.Service, s.Str("Name"), $"{s.Str("DisplayName")} — {exe}", true));
                }
        });

        // --- Tác vụ lập lịch ---
        if (loc.Length > 0)
        {
            try
            {
                foreach (var t in await AllTasksAsync())
                    if (StartupService.ExtractExe(t.Action) is { Length: > 0 } exe && IsUnder(exe, loc))
                        list.Add(new Leftover(LeftoverKind.Task, $"{t.Path}|{t.Name}", $"{t.Name} — {t.Action}", true));
            }
            catch (Exception ex) { Log.Warn($"Quét tác vụ lỗi: {ex.Message}"); }
        }

        return list.DistinctBy(l => (l.Kind, l.Path.ToLowerInvariant())).ToList();

        void AddKey(RegistryKey root, string rootName, string sub, bool high, string detail)
        {
            using var k = root.OpenSubKey(sub);
            if (k is null) return;
            var full = $@"{rootName}\{sub}";
            if (list.Any(l => l.Kind == LeftoverKind.RegistryKey && l.HighConfidence && IsUnder(full, l.Path))) return;
            if (high) list.RemoveAll(l => l.Kind == LeftoverKind.RegistryKey && IsUnder(l.Path, full));
            list.Add(new Leftover(LeftoverKind.RegistryKey, full, detail, high));
        }
    }

    private sealed record TaskInfo(string Name, string Path, string Action);

    private static async Task<List<TaskInfo>> AllTasksAsync()
    {
        const string script = """
            Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object {
              $a = @($_.Actions | Where-Object { $_.Execute } | Select-Object -First 1)
              if ($a) { "{0}|{1}|{2} {3}" -f $_.TaskPath, $_.TaskName, $a[0].Execute, $a[0].Arguments }
            }
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        return r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Count(c => c == '|') >= 2)
            .Select(l => l.Split('|', 3)).Select(p => new TaskInfo(p[1], p[0], Environment.ExpandEnvironmentVariables(p[2].Trim()))).ToList();
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            return (string)shell.CreateShortcut(lnk).TargetPath;
        }
        catch { return null; }
    }

    // ================= Xoá phần còn sót =================
    public static async Task<OpResult> RemoveLeftoversAsync(InstalledApp a, IEnumerable<Leftover> items)
    {
        var safe = string.Concat(CleanName(a.Name).Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var backup = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe}");
        Directory.CreateDirectory(backup);
        int ok = 0, fail = 0;
        long bytes = 0;
        var errors = new List<string>();
        var regIndex = 0;

        // Dừng dịch vụ và tác vụ trước để file không còn bị giữ
        foreach (var l in items.OrderBy(l => l.Kind switch { LeftoverKind.Service => 0, LeftoverKind.Task => 1, LeftoverKind.StartupEntry => 2, LeftoverKind.RegistryKey => 4, _ => 3 }))
        {
            try
            {
                switch (l.Kind)
                {
                    case LeftoverKind.Service:
                        await ProcessRunner.ConsoleAsync("sc.exe", $"stop \"{l.Path}\"");
                        await Task.Delay(500);
                        var r = await ProcessRunner.ConsoleAsync("sc.exe", $"delete \"{l.Path}\"");
                        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
                        break;
                    case LeftoverKind.Task:
                        var parts = l.Path.Split('|', 2);
                        var t = await ProcessRunner.PowerShellAsync($"Unregister-ScheduledTask -TaskPath {ProcessRunner.PsQuote(parts[0])} -TaskName {ProcessRunner.PsQuote(parts[1])} -Confirm:$false");
                        if (!t.Success) throw new InvalidOperationException(OpResult.FirstLine(t.Text));
                        break;
                    case LeftoverKind.StartupEntry:
                        var se = l.Path.Split('|', 2);
                        var entry = StartupService.ReadAll().FirstOrDefault(e => e.Source.ToString() == se[0] && e.RawName == se[1]);
                        if (entry is not null && !StartupService.Delete(entry).Ok) throw new InvalidOperationException("không xoá được mục khởi động");
                        break;
                    case LeftoverKind.RegistryKey:
                        var file = Path.Combine(backup, $"reg{++regIndex:00}.reg");
                        var ex = await ProcessRunner.ConsoleAsync("reg.exe", $"export \"{l.Path}\" \"{file}\" /y");
                        if (!ex.Success) throw new InvalidOperationException($"không sao lưu được: {OpResult.FirstLine(ex.Text)}");
                        var (root, sub) = SplitKey(l.Path);
                        root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                        break;
                    default:
                        var size = l.Bytes > 0 ? l.Bytes : File.Exists(l.Path) ? new FileInfo(l.Path).Length : 0;
                        if (!FileOps.SendToRecycleBin(l.Path))
                        {
                            // Ổ không có Thùng rác hoặc lỗi Shell → xoá thẳng
                            if (Directory.Exists(l.Path)) Directory.Delete(l.Path, recursive: true);
                            else if (File.Exists(l.Path)) File.Delete(l.Path);
                        }
                        bytes += size;
                        break;
                }
                ok++;
            }
            catch (Exception e)
            {
                fail++;
                errors.Add($"{l.KindText} {l.Path}: {e.Message}");
                Log.Warn($"Gỡ sạch {a.Name}: không xoá được {l.Path}: {e.Message}");
            }
        }
        if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup);

        var msg = $"{a.Name}: đã dọn {ok} mục còn sót" + (bytes > 0 ? $", {Fmt.Size(bytes)} vào Thùng rác" : "") +
                  (regIndex > 0 ? $"; registry đã sao lưu tại {Path.GetFileName(backup)}" : "") + ".";
        return fail == 0 ? OpResult.Success(msg) : OpResult.Fail($"{msg} Còn {fail} mục lỗi: {string.Join("; ", errors.Take(3))} (thử khởi động lại máy rồi quét lại).");
    }

    private static (RegistryKey Root, string Sub) SplitKey(string full)
    {
        var i = full.IndexOf('\\');
        var hive = full[..i];
        var sub = full[(i + 1)..];
        return (hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine, sub);
    }
}
