using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Store;

/// <summary>Một dòng trong bảng kết quả của winget (search / upgrade / list).</summary>
public sealed record WingetRow(string Name, string Id, string Version, string Available, string Source)
{
    /// <summary>Id bị winget cắt bớt (kết thúc bằng "…") vì bảng quá rộng.</summary>
    public bool Truncated => Id.EndsWith('…') || Name.EndsWith('…');
}

/// <summary>Bọc winget.exe: tìm, cài, gỡ, cập nhật phần mềm. Không cần module PowerShell.</summary>
public static class WingetService
{
    private const string Common = "--accept-source-agreements --disable-interactivity";
    private const string Agree = "--accept-package-agreements " + Common;

    private static string? _path;

    /// <summary>Đường dẫn winget.exe, null nếu máy chưa có.</summary>
    public static string? Locate()
    {
        if (_path is not null && File.Exists(_path)) return _path;
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return _path = alias;
        // Chạy quyền admin từ tài khoản khác / chưa có alias → tìm thẳng trong WindowsApps
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var best = Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .Select(d => Path.Combine(d, "winget.exe")).Where(File.Exists)
                .OrderByDescending(p => VersionOf(Path.GetFileName(Path.GetDirectoryName(p)!))).FirstOrDefault();
            if (best is not null) return _path = best;
        }
        catch { }
        return null;
    }

    private static Version VersionOf(string dir)
    {
        var parts = dir.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
    }

    public static async Task<string> VersionAsync()
    {
        var w = Locate();
        if (w is null) return "";
        var r = await ProcessRunner.RunAsync(w, "--version");
        return r.Output.Trim();
    }

    /// <summary>Cài / đăng ký lại App Installer (winget) khi máy chưa có.</summary>
    public static async Task<OpResult> RepairAsync()
    {
        // Máy mới cài: gói đã có sẵn nhưng chưa đăng ký cho tài khoản → đăng ký lại là đủ
        var r = await ProcessRunner.PowerShellAsync(
            "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop");
        _path = null;
        if (Locate() is not null) return OpResult.Success("Đã bật winget.");

        // Chưa có gói → tải bản mới nhất từ GitHub của Microsoft cùng thư viện phụ thuộc
        const string script = """
            $d = Join-Path $env:TEMP 'ithd-winget'; New-Item -ItemType Directory -Force -Path $d | Out-Null
            [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
            $rel = Invoke-RestMethod 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -UseBasicParsing
            $bundle = $rel.assets | Where-Object name -like '*.msixbundle' | Select-Object -First 1
            $deps = $rel.assets | Where-Object name -like 'DesktopAppInstaller_Dependencies.zip' | Select-Object -First 1
            if (-not $bundle) { throw 'Không tìm thấy bộ cài winget trên GitHub' }
            if ($deps) {
              Invoke-WebRequest $deps.browser_download_url -OutFile "$d\deps.zip" -UseBasicParsing
              Expand-Archive "$d\deps.zip" -DestinationPath "$d\deps" -Force
              Get-ChildItem "$d\deps" -Recurse -Filter *.appx | Where-Object { $_.FullName -match 'x64' } |
                ForEach-Object { try { Add-AppxPackage $_.FullName -ErrorAction Stop } catch { } }
            }
            Invoke-WebRequest $bundle.browser_download_url -OutFile "$d\winget.msixbundle" -UseBasicParsing
            Add-AppxPackage "$d\winget.msixbundle" -ErrorAction Stop
            """;
        r = await ProcessRunner.PowerShellAsync(script);
        _path = null;
        return Locate() is not null ? OpResult.Success("Đã cài winget.") : OpResult.Fail($"Không cài được winget: {OpResult.FirstLine(r.Text)}");
    }

    // ================= Chạy lệnh =================
    private static async Task<ProcessResult> RunAsync(string args, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var w = Locate() ?? throw new InvalidOperationException("Máy chưa có winget (App Installer) — bấm “Cài winget”.");
        Log.Info($"winget {args}");
        return await ProcessRunner.RunAsync(w, args, line => onLine?.Invoke(Clean(line)), ct);
    }

    /// <summary>Bỏ ký tự quay vòng / thanh tiến trình mà winget in đè bằng \r.</summary>
    internal static string Clean(string line)
    {
        var s = line.Split('\r').Select(x => x.TrimEnd()).LastOrDefault(x => x.Trim().Length > 0) ?? "";
        return s.Trim().Length == 1 && "-\\|/".Contains(s.Trim()) ? "" : s;
    }

    /// <summary>Mã lỗi thường gặp của winget → tiếng Việt.</summary>
    public static string Explain(int code) => unchecked((uint)code) switch
    {
        0x8A150011 => "file tải về không khớp mã kiểm tra (nhà phát hành vừa cập nhật) — thử lại sau",
        0x8A150014 => "không tìm thấy gói này trong kho winget",
        0x8A15002B => "không có bản cập nhật phù hợp",
        0x8A150061 or 0x8A15010D => "đã cài sẵn",
        0x8A150101 => "phần mềm đang chạy — hãy đóng lại rồi thử lại",
        0x8A150102 => "một bộ cài khác đang chạy — chờ xong rồi thử lại",
        0x8A150103 => "file đang bị dùng — đóng phần mềm rồi thử lại",
        0x8A150104 => "thiếu thành phần phụ thuộc",
        0x8A150105 => "ổ đĩa đầy",
        0x8A150106 => "không đủ bộ nhớ",
        0x8A150107 => "không có mạng",
        0x8A150109 or 0x8A15010A or 0x8A15010B => "cần khởi động lại máy để hoàn tất",
        0x8A15010C => "đã bị huỷ",
        0x8A15010E => "máy đang có bản mới hơn",
        0x8A15010F => "bị chặn bởi chính sách",
        0x8A150044 => "chưa đồng ý điều khoản nguồn tải",
        _ => $"mã lỗi 0x{code:X8}",
    };

    private static bool IsOkCode(int code) => code == 0 || unchecked((uint)code) is 0x8A150061 or 0x8A15010D or 0x8A150109 or 0x8A15010A or 0x8A15010B;
    private static bool NeedsRestart(int code) => unchecked((uint)code) is 0x8A150109 or 0x8A15010A or 0x8A15010B;

    private static OpResult ToResult(ProcessResult r, string okMsg, string failPrefix)
    {
        if (IsOkCode(r.ExitCode))
        {
            var extra = unchecked((uint)r.ExitCode) is 0x8A150061 or 0x8A15010D ? " (đã có sẵn)" : NeedsRestart(r.ExitCode) ? " — cần khởi động lại máy" : "";
            return OpResult.Success(okMsg + extra, NeedsRestart(r.ExitCode));
        }
        return OpResult.Fail($"{failPrefix}: {Explain(r.ExitCode)}");
    }

    // ================= Cài / gỡ / cập nhật =================
    public static async Task<OpResult> InstallAsync(string id, string name, Action<string>? onLine = null, CancellationToken ct = default, string extra = "")
    {
        var r = await RunAsync($"install --id \"{id}\" --exact --source winget --silent {Agree} {extra}".Trim(), onLine, ct);
        return ToResult(r, $"Đã cài {name}", $"Không cài được {name}");
    }

    public static async Task<OpResult> UpgradeAsync(string id, string name, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var r = await RunAsync($"upgrade --id \"{id}\" --exact --silent --include-unknown {Agree}", onLine, ct);
        return unchecked((uint)r.ExitCode) == 0x8A15002B
            ? OpResult.Success($"{name} đã là bản mới nhất")
            : ToResult(r, $"Đã cập nhật {name}", $"Không cập nhật được {name}");
    }

    public static async Task<OpResult> UpgradeAllAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        var r = await RunAsync($"upgrade --all --silent --include-unknown {Agree}", onLine, ct);
        return unchecked((uint)r.ExitCode) == 0x8A15002B || r.ExitCode == 0
            ? OpResult.Success("Đã cập nhật tất cả phần mềm có bản mới.")
            : OpResult.Fail($"Cập nhật chưa xong hết: {Explain(r.ExitCode)}");
    }

    public static async Task<OpResult> UninstallAsync(string id, string name, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var r = await RunAsync($"uninstall --id \"{id}\" --exact --silent {Common}", onLine, ct);
        return ToResult(r, $"Đã gỡ {name}", $"Không gỡ được {name}");
    }

    // ================= Đọc danh sách =================
    /// <summary>Id các gói đang cài mà winget nhận ra (đọc qua winget export → JSON, không phụ thuộc độ rộng bảng).</summary>
    public static async Task<Dictionary<string, string>> InstalledAsync(CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ithd-winget-{Guid.NewGuid():N}.json");
        try
        {
            var r = await RunAsync($"export -o \"{file}\" --include-versions {Common}", ct: ct);
            if (!File.Exists(file)) throw new InvalidOperationException($"winget export lỗi: {Explain(r.ExitCode)}");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in doc.RootElement.GetProperty("Sources").EnumerateArray())
                foreach (var p in src.GetProperty("Packages").EnumerateArray())
                    map[p.GetProperty("PackageIdentifier").GetString() ?? ""] = p.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
            return map;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Phần mềm có bản mới. Id bị cắt được ghép lại từ danh sách đã cài.</summary>
    public static async Task<List<WingetRow>> UpgradesAsync(IReadOnlyDictionary<string, string>? installed = null, CancellationToken ct = default)
    {
        var r = await RunAsync($"upgrade --include-unknown {Common}", ct: ct);
        var rows = ParseTable(r.Output);
        if (installed is not null)
            rows = rows.Select(row =>
            {
                if (!row.Id.EndsWith('…')) return row;
                var prefix = row.Id.TrimEnd('…');
                var hits = installed.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                return hits.Count == 1 ? row with { Id = hits[0] } : row;
            }).ToList();
        return rows.Where(x => x.Available.Length > 0).ToList();
    }

    public static async Task<List<WingetRow>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.Replace("\"", "");
        var r = await RunAsync($"search \"{q}\" --source winget {Common}", ct: ct);
        return ParseTable(r.Output);
    }

    /// <summary>
    /// Đọc bảng của winget. Vị trí cột lấy từ dòng tiêu đề (dòng ngay trên dòng "-----"),
    /// nên không phụ thuộc ngôn ngữ của tiêu đề.
    /// </summary>
    internal static List<WingetRow> ParseTable(string output)
    {
        var lines = output.Split('\n').Select(Clean).ToList();
        var result = new List<WingetRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^-{10,}\s*$")) continue;
            var header = lines[i - 1];
            var starts = Regex.Matches(header, @"(?<=^|\s)\S").Select(m => m.Index).ToList();
            if (starts.Count < 3) continue;
            for (var j = i + 1; j < lines.Count; j++)
            {
                var l = lines[j];
                if (l.Trim().Length == 0 || l.Length <= starts[1]) break;      // hết bảng (dòng trống / dòng tổng kết)
                string Col(int k)
                {
                    if (k >= starts.Count || starts[k] >= l.Length) return "";
                    var end = k + 1 < starts.Count ? Math.Min(starts[k + 1], l.Length) : l.Length;
                    return l[starts[k]..end].Trim();
                }
                var cols = Enumerable.Range(0, starts.Count).Select(Col).ToList();
                // search: Name Id Version [Match] Source · upgrade: Name Id Version Available Source
                var name = cols[0];
                var id = cols[1];
                if (id.Contains(' ') || id.Length == 0) continue;
                var version = cols.ElementAtOrDefault(2) ?? "";
                var available = starts.Count >= 5 && !header.Contains("Match", StringComparison.OrdinalIgnoreCase) ? cols[3] : "";
                var source = cols[^1];
                result.Add(new WingetRow(name, id, version, available, source));
            }
            i++;
        }
        return result.DistinctBy(r => r.Id).ToList();
    }
}
