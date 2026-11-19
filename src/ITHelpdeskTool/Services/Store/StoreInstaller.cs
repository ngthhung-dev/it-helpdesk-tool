using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Uninstall;

namespace ITHelpdeskTool.Services.Store;

/// <summary>Cài / gỡ một mục trong kho, bất kể nguồn (winget, tải trực tiếp, bản chạy liền).</summary>
public static class StoreInstaller
{
    public static string PortableRoot => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static string PortableDir(StoreApp a) => Path.Combine(PortableRoot, a.Name);
    private static string StartMenuLnk(StoreApp a) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), a.Name + ".lnk");
    private static string DesktopLnk(StoreApp a) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), a.Name + ".lnk");

    /// <summary>Mục không qua winget đã cài chưa (winget thì xem danh sách export).</summary>
    public static bool IsInstalledLocally(StoreApp a, IReadOnlyList<InstalledApp> registryApps) => a.Kind switch
    {
        StoreKind.Portable => FindExe(PortableDir(a), a.Exe) is not null,
        StoreKind.Download => a.Detect.Length > 0 && registryApps.Any(r => Regex.IsMatch(r.Name, a.Detect, RegexOptions.IgnoreCase)),
        _ => false,
    };

    /// <summary>
    /// Mục winget có trong danh sách winget export không. Ngoài Id đúng, chấp nhận biến thể winget tự đặt khi
    /// đối chiếu bản cài tay (Zoom.Zoom.EXE, Google.Chrome.EXE, Mozilla.Firefox.vi…), trừ khi biến thể đó là mục khác trong kho.
    /// </summary>
    public static string? FindWingetId(StoreApp a, IReadOnlyDictionary<string, string> installed)
    {
        if (installed.ContainsKey(a.Id)) return a.Id;
        foreach (var k in installed.Keys)
            if (k.Length > a.Id.Length + 1 && k.StartsWith(a.Id + ".", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(k[(a.Id.Length + 1)..], @"^(EXE|MSI|MSIX|Machine|User|[a-z]{2}(-[a-z]{2,4})?)$", RegexOptions.IgnoreCase)
                && StoreCatalog.Find(k) is null)
                return k;
        return null;
    }

    /// <summary>Dò theo tên trong Programs and Features (dự phòng khi winget không nhận ra gói).</summary>
    public static InstalledApp? FindByName(StoreApp a, IReadOnlyList<InstalledApp> registryApps)
    {
        if (a.DetectPattern is not { } p) return null;
        try { return registryApps.FirstOrDefault(r => Regex.IsMatch(r.Name, p, RegexOptions.IgnoreCase)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Tên các gói ứng dụng (MSIX / Microsoft Store) đã cài cho tài khoản này, vd "5319275A.WhatsAppDesktop".</summary>
    public static HashSet<string> ReadAppxNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = ActiveUser.Registry.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            foreach (var n in k?.GetSubKeyNames() ?? []) set.Add(n.Split('_')[0]);
        }
        catch (Exception ex) { Log.Warn($"Đọc danh sách gói ứng dụng: {ex.Message}"); }
        return set;
    }

    private static string? FindExe(string dir, string exe)
    {
        if (!Directory.Exists(dir) || exe.Length == 0) return null;
        return Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).FirstOrDefault();
    }

    public static async Task<OpResult> InstallAsync(StoreApp a, Action<string>? status = null, CancellationToken ct = default)
    {
        if (a.Id == "Kingsoft.WPSOffice" && Tweaks.WpsInstallBlock.Instance.IsOn())
            return OpResult.Fail("Máy đang bật chặn cài WPS Office (Tweaks → WPS Office). Tắt mục đó rồi cài lại.");
        try
        {
            switch (a.Kind)
            {
                case StoreKind.Winget when a.UserScope && ActiveUser.IsOther:
                    return await InstallForActiveUserAsync(a, status, ct);

                case StoreKind.Winget:
                    status?.Invoke("Đang tải và cài…");
                    return await WingetService.InstallAsync(a.Id, a.Name, line => { if (Stage(line) is { } s) status?.Invoke(s); }, ct);

                case StoreKind.Download:
                {
                    status?.Invoke("Đang tìm bản mới nhất…");
                    var url = await Downloader.ResolveAsync(a.Url, ct);
                    var file = await Downloader.DownloadAsync(url, Path.Combine(Downloader.CacheDir, "store"), null, (d, t) => status?.Invoke(Progress(d, t)), ct);
                    status?.Invoke("Đang cài…");
                    var r = await InstallerRunner.RunAsync(file, a.Name, true, ct);
                    try { File.Delete(file); } catch { }
                    return r;
                }

                default:
                {
                    status?.Invoke("Đang tìm bản mới nhất…");
                    var url = await Downloader.ResolveAsync(a.Url, ct);
                    var file = await Downloader.DownloadAsync(url, Path.Combine(Downloader.CacheDir, "store"), null, (d, t) => status?.Invoke(Progress(d, t)), ct);
                    status?.Invoke("Đang giải nén…");
                    var dir = PortableDir(a);
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(a.Exe))) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                    Directory.CreateDirectory(dir);
                    ZipFile.ExtractToDirectory(file, dir, overwriteFiles: true);
                    try { File.Delete(file); } catch { }
                    var exe = FindExe(dir, a.Exe) ?? throw new InvalidOperationException($"không thấy {a.Exe} trong gói tải về");
                    ShellLink.Create(StartMenuLnk(a), exe, description: a.Description);
                    ShellLink.Create(DesktopLnk(a), exe, description: a.Description);
                    return OpResult.Success($"Đã cài {a.Name} vào {dir}, có lối tắt ở Start Menu và Desktop.");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Cài {a.Name} lỗi: {ex.Message}");
            return OpResult.Fail($"Không cài được {a.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gói chỉ cài theo tài khoản trong khi app chạy bằng admin khác: winget tải bộ cài (đã kiểm tra mã băm) vào TEMP
    /// của người đang đăng nhập, rồi chạy bộ cài im lặng dưới tên họ để phần mềm vào đúng hồ sơ của họ.
    /// Lệnh cài im lặng và mã thành công lấy từ manifest winget tải kèm.
    /// </summary>
    private static async Task<OpResult> InstallForActiveUserAsync(StoreApp a, Action<string>? status, CancellationToken ct)
    {
        var dir = Path.Combine(ActiveUser.TempPath, "ITHelpdeskTool-store", a.Id);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            status?.Invoke("Đang tải bộ cài…");
            var d = await WingetService.DownloadAsync(a.Id, dir, line => { if (Stage(line) is { } s) status?.Invoke(s); }, ct);
            var installer = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".msi");
            if (d.ExitCode != 0 || installer is null)
                return OpResult.Fail($"Không tải được bộ cài {a.Name}: {(d.ExitCode != 0 ? WingetService.ExplainCode(d.ExitCode) : "winget không trả về file cài")}");

            var manifest = string.Join("\n", Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories).Select(File.ReadAllText));
            var silent = Regex.Match(manifest, @"^\s*Silent:\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim('\'', '"') : null;
            if (silent is null && Path.GetExtension(installer).Equals(".msi", StringComparison.OrdinalIgnoreCase)) silent = "/qn /norestart";
            if (silent is null) return OpResult.Fail($"Manifest winget của {a.Name} không có lệnh cài im lặng.");
            // Mã thoát được coi là thành công (vd WPS trả 1223)
            var okCodes = new HashSet<int> { 0 };
            var codes = Regex.Match(manifest, @"^\s*InstallerSuccessCodes:[ \t]*\r?\n((?:[ \t]*-[ \t]*-?\d+[ \t]*\r?\n?)+)", RegexOptions.Multiline);
            foreach (Match c in Regex.Matches(codes.Groups[1].Value, @"-?\d+")) okCodes.Add(int.Parse(c.Value));

            status?.Invoke($"Đang cài cho {ActiveUser.Name}…");
            var cmd = Path.GetExtension(installer).Equals(".msi", StringComparison.OrdinalIgnoreCase)
                ? $"msiexec.exe /i \"{installer}\" {silent}"
                : $"\"{installer}\" {silent}";
            Log.Info($"Cài {a.Name} dưới tài khoản {ActiveUser.Account}: {cmd}");
            var code = await ActiveUser.RunAsUserAsync(cmd, Path.GetDirectoryName(installer), ct);
            if (code is null) return OpResult.Fail($"Không chạy được bộ cài {a.Name} dưới tài khoản {ActiveUser.Account}.");
            return okCodes.Contains(code.Value)
                ? OpResult.Success($"Đã cài {a.Name} cho tài khoản {ActiveUser.Account}.")
                : OpResult.Fail($"Bộ cài {a.Name} báo lỗi (mã {code.Value}).");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static async Task<OpResult> UninstallAsync(StoreApp a, CancellationToken ct = default)
    {
        try
        {
            switch (a.Kind)
            {
                case StoreKind.Winget:
                    return await WingetService.UninstallAsync(a.Id, a.Name, ct: ct);
                case StoreKind.Portable:
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(a.Exe))) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                    if (Directory.Exists(PortableDir(a))) Directory.Delete(PortableDir(a), true);
                    File.Delete(StartMenuLnk(a));
                    File.Delete(DesktopLnk(a));
                    return OpResult.Success($"Đã gỡ {a.Name}.");
                default:
                    var app = UninstallService.ReadApps().FirstOrDefault(r => a.Detect.Length > 0 && Regex.IsMatch(r.Name, a.Detect, RegexOptions.IgnoreCase));
                    if (app is null) return OpResult.Fail($"Không thấy {a.Name} trong danh sách đã cài.");
                    return await UninstallService.RunUninstallerAsync(app, quiet: true, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return OpResult.Fail($"Không gỡ được {a.Name}: {ex.Message}"); }
    }

    /// <summary>Dòng output của winget → trạng thái ngắn cho người dùng.</summary>
    public static string? Stage(string line)
    {
        var l = line.Trim();
        if (l.Length == 0) return null;
        var pct = Regex.Match(l, @"(\d+(?:\.\d+)?)\s*(KB|MB|GB)\s*/\s*(\d+(?:\.\d+)?)\s*(KB|MB|GB)");
        if (pct.Success) return $"Đang tải {pct.Groups[1].Value} {pct.Groups[2].Value} / {pct.Groups[3].Value} {pct.Groups[4].Value}";
        var p = Regex.Match(l, @"\b(\d{1,3})%");
        if (p.Success) return $"Đang tải… {p.Groups[1].Value}%";
        if (l.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)) return "Đang tải bộ cài…";
        if (l.Contains("hash", StringComparison.OrdinalIgnoreCase) && l.Contains("verified", StringComparison.OrdinalIgnoreCase)) return "Đã kiểm tra chữ ký bộ cài";
        if (l.StartsWith("Starting package install", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Starting package uninstall", StringComparison.OrdinalIgnoreCase)) return "Đang cài…";
        if (l.StartsWith("Successfully", StringComparison.OrdinalIgnoreCase)) return "Xong";
        return null;
    }

    public static string Progress(long done, long total) =>
        total > 0 ? $"Đang tải {Fmt.Size(done)} / {Fmt.Size(total)} ({done * 100 / total}%)" : $"Đang tải {Fmt.Size(done)}";
}
