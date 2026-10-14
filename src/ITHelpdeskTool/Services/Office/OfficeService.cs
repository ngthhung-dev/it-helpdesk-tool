using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Uninstall;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Office;

/// <summary>Office Click-to-Run đang cài.</summary>
public sealed record OfficeC2R(
    string[] Products, string Platform, string Version, string Channel, string ChannelId, string Culture, string InstallPath, bool UpdatesEnabled)
{
    public string ProductText => string.Join(", ", Products.Select(OfficeCatalog.ProductName));
}

/// <summary>Một giấy phép đọc từ OSPP.VBS /dstatus.</summary>
public sealed record OfficeLicense(string Name, string Description, string Status, string Last5, string Grace, string Error)
{
    public bool Licensed => Status.Contains("LICENSED", StringComparison.OrdinalIgnoreCase) && !Status.Contains("UNLICENSED", StringComparison.OrdinalIgnoreCase);
    public string StatusText => Licensed ? "Đã kích hoạt"
        : Status.Contains("GRACE", StringComparison.OrdinalIgnoreCase) ? "Đang dùng thử" + (Grace.Length > 0 ? $" ({Grace})" : "")
        : Status.Contains("NOTIFICATIONS", StringComparison.OrdinalIgnoreCase) ? "Chưa kích hoạt (bị nhắc nhở)"
        : Status.Contains("UNLICENSED", StringComparison.OrdinalIgnoreCase) ? "Chưa kích hoạt" : Status;
}

/// <summary>Đọc / cài / gỡ / sửa Microsoft Office qua Office Deployment Tool (ODT) và OSPP.</summary>
public static class OfficeService
{
    private const string C2RKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
    public const string OdtUrl = "https://officecdn.microsoft.com/pr/wsus/setup.exe";

    public static string WorkDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITHelpdeskTool", "office");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string CommonFiles => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
    public static string C2RClient => Path.Combine(CommonFiles, @"microsoft shared\ClickToRun\OfficeC2RClient.exe");
    public static string C2RExe => Path.Combine(CommonFiles, @"microsoft shared\ClickToRun\OfficeClickToRun.exe");

    // ================= Phát hiện =================
    public static OfficeC2R? ReadC2R()
    {
        using var k = Registry.LocalMachine.OpenSubKey(C2RKey);
        if (k is null) return null;
        string S(string n) => (k.GetValue(n) as string ?? "").Trim();
        var products = S("ProductReleaseIds").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (products.Length == 0) return null;
        // Kênh: ưu tiên UpdateChannel (do chính sách / changesetting), không có thì CDNBaseUrl
        var url = S("UpdateChannel").Length > 0 ? S("UpdateChannel") : S("CDNBaseUrl");
        var guid = Regex.Match(url, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value.ToLowerInvariant();
        var ch = OfficeCatalog.Channels.FirstOrDefault(c => c.Guid == guid);
        return new OfficeC2R(products, S("Platform"), S("VersionToReport"), ch?.Name ?? (guid.Length > 0 ? guid : "Không rõ"), ch?.Id ?? "",
            S("ClientCulture"), S("InstallationPath"), !S("UpdatesEnabled").Equals("False", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Office cài kiểu MSI cũ (2010 / 2013 / 2016 MSI).</summary>
    public static List<InstalledApp> ReadMsiOffice() =>
        UninstallService.ReadApps().Where(a =>
            Regex.IsMatch(a.Name, @"^Microsoft (Office|Visio|Project)\b.*\b(2007|2010|2013|2016)\b", RegexOptions.IgnoreCase) &&
            (a.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || a.UninstallString.Contains("Office Setup Controller", StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>Có giấy phép Microsoft 365 (đăng nhập tài khoản) trên máy không.</summary>
    public static bool HasSubscriptionLicense()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Office\Licenses");
        try { return Directory.Exists(d) && Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    // ================= Giấy phép =================
    public static string? OsppPath()
    {
        var c2r = ReadC2R();
        var roots = new List<string>();
        if (c2r is not null && c2r.InstallPath.Length > 0) roots.Add(Path.Combine(c2r.InstallPath, "Office16"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\Office16"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Office\Office16"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\Office15"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Office\Office15"));
        return roots.Select(r => Path.Combine(r, "OSPP.VBS")).FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessResult> OsppAsync(string args)
    {
        var ospp = OsppPath() ?? throw new InvalidOperationException("Không tìm thấy OSPP.VBS (Office chưa cài?)");
        var cscript = Path.Combine(Environment.SystemDirectory, "cscript.exe");
        return await ProcessRunner.RunAsync(cscript, $"//nologo \"{ospp}\" {args}", encoding: ProcessRunner.Oem);
    }

    public static async Task<List<OfficeLicense>> LicensesAsync()
    {
        if (OsppPath() is null) return [];
        var r = await OsppAsync("/dstatus");
        return ParseDstatus(r.Output);
    }

    internal static List<OfficeLicense> ParseDstatus(string output)
    {
        var list = new List<OfficeLicense>();
        foreach (var block in Regex.Split(output, @"-{20,}"))
        {
            string F(string label)
            {
                var m = Regex.Match(block, "^" + Regex.Escape(label) + @"\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : "";
            }
            var name = F("LICENSE NAME:");
            if (name.Length == 0) continue;
            list.Add(new OfficeLicense(name, F("LICENSE DESCRIPTION:"), F("LICENSE STATUS:").Trim('-', ' '),
                F("Last 5 characters of installed product key:"), F("REMAINING GRACE:"), F("ERROR CODE:")));
        }
        return list;
    }

    private static readonly Regex KeyRx = new(@"^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$", RegexOptions.IgnoreCase);
    public static bool IsKeyFormat(string key) => KeyRx.IsMatch(key.Trim());

    /// <summary>Nhập key bản quyền rồi kích hoạt qua mạng.</summary>
    public static async Task<OpResult> ActivateWithKeyAsync(string key)
    {
        key = key.Trim().ToUpperInvariant();
        if (!IsKeyFormat(key)) return OpResult.Fail("Key phải có dạng XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.");
        var r = await OsppAsync($"/inpkey:{key}");
        if (!r.Output.Contains("successful", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail($"Key không hợp lệ cho bản Office này: {OsppError(r.Output)}");
        return await ActivateAsync();
    }

    public static async Task<OpResult> ActivateAsync()
    {
        var r = await OsppAsync("/act");
        return r.Output.Contains("Product activation successful", StringComparison.OrdinalIgnoreCase)
            ? OpResult.Success("Kích hoạt Office thành công.")
            : OpResult.Fail($"Chưa kích hoạt được: {OsppError(r.Output)}");
    }

    public static async Task<OpResult> RemoveKeyAsync(string last5)
    {
        var r = await OsppAsync($"/unpkey:{last5}");
        return r.Output.Contains("successful", StringComparison.OrdinalIgnoreCase)
            ? OpResult.Success($"Đã gỡ key …{last5}.")
            : OpResult.Fail($"Không gỡ được key: {OsppError(r.Output)}");
    }

    private static string OsppError(string output)
    {
        var code = Regex.Match(output, @"ERROR CODE:\s*(0x[0-9A-F]+)", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
        var desc = Regex.Match(output, @"ERROR DESCRIPTION:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        var vi = code switch
        {
            "0xC004F074" => "không liên lạc được máy chủ kích hoạt KMS",
            "0xC004C008" => "key đã dùng quá số lần cho phép",
            "0xC004C003" => "key bị chặn / không hợp lệ",
            "0xC004F050" => "key không hợp lệ",
            "0xC004F069" => "key không khớp với phiên bản Office",
            "0x8007232B" => "không tìm thấy máy chủ KMS (bản Volume cần KMS / MAK của tổ chức)",
            "0xC004C060" => "key không còn được hỗ trợ",
            _ => "",
        };
        return vi.Length > 0 ? $"{vi} ({code})" : desc.Length > 0 ? $"{desc} {code}".Trim() : OpResult.FirstLine(output);
    }

    // ================= Cập nhật / sửa / đổi kênh =================
    public static async Task<OpResult> UpdateNowAsync()
    {
        if (!File.Exists(C2RClient)) return OpResult.Fail("Không thấy OfficeC2RClient.exe — Office không phải bản Click-to-Run.");
        var r = await ProcessRunner.RunAsync(C2RClient, "/update user displaylevel=true forceappshutdown=false");
        return r.ExitCode == 0
            ? OpResult.Success("Đã mở trình cập nhật Office — theo dõi tiến độ trên cửa sổ của Office.")
            : OpResult.Fail($"Không chạy được cập nhật (mã {r.ExitCode}).");
    }

    public static async Task<OpResult> ChangeChannelAsync(OfficeChannel ch)
    {
        if (!File.Exists(C2RClient)) return OpResult.Fail("Office không phải bản Click-to-Run.");
        var r = await ProcessRunner.RunAsync(C2RClient, $"/changesetting Channel={ch.Id}");
        if (r.ExitCode != 0) return OpResult.Fail($"Không đổi được kênh (mã {r.ExitCode}).");
        await ProcessRunner.RunAsync(C2RClient, "/update user displaylevel=true");
        return OpResult.Success($"Đã chuyển sang kênh {ch.Name}, đang tải bản cập nhật của kênh mới.");
    }

    /// <summary>Sửa Office: quick = sửa nhanh offline, không thì sửa trực tuyến (tải lại toàn bộ).</summary>
    public static async Task<OpResult> RepairAsync(bool quick)
    {
        var c = ReadC2R();
        if (c is null || !File.Exists(C2RExe)) return OpResult.Fail("Không thấy Office Click-to-Run để sửa.");
        var platform = c.Platform.Length > 0 ? c.Platform : "x64";
        var culture = c.Culture.Length > 0 ? c.Culture : "en-us";
        var r = await ProcessRunner.RunAsync(C2RExe,
            $"scenario=Repair platform={platform} culture={culture} forceappshutdown=True RepairType={(quick ? "QuickRepair" : "FullRepair")} DisplayLevel=True");
        return r.ExitCode == 0
            ? OpResult.Success((quick ? "Đã chạy sửa nhanh" : "Đã chạy sửa trực tuyến") + " — theo dõi trên cửa sổ của Office.")
            : OpResult.Fail($"Không chạy được trình sửa (mã {r.ExitCode}).");
    }

    /// <summary>Xoá thông tin đăng nhập / giấy phép tài khoản đã lưu (lỗi đăng nhập, “Unlicensed Product”, đổi tài khoản).</summary>
    public static async Task<OpResult> ResetSignInAsync()
    {
        foreach (var p in new[] { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "MSPUB", "lync", "Teams", "ms-teams" })
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName(p))
                try { proc.CloseMainWindow(); } catch { }
        var n = 0;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common", true))
        {
            foreach (var sub in new[] { "Identity", "Licensing" })
                try { if (k?.OpenSubKey(sub) is { } x) { x.Dispose(); k.DeleteSubKeyTree(sub, false); n++; } } catch { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var d in new[] { @"Microsoft\Office\Licenses", @"Microsoft\Office\16.0\Licensing" })
        {
            var path = Path.Combine(local, d);
            try { if (Directory.Exists(path)) { Directory.Delete(path, true); n++; } } catch { }
        }
        // Thông tin đăng nhập lưu trong Credential Manager
        var list = await ProcessRunner.ConsoleAsync("cmdkey.exe", "/list");
        foreach (Match m in Regex.Matches(list.Output, @"(?:Target|Đích|Mục tiêu)\s*:\s*(?:LegacyGeneric:target=|Domain:target=)?(MicrosoftOffice1\S*|MicrosoftAccount\S*Office\S*)", RegexOptions.IgnoreCase))
        {
            await ProcessRunner.ConsoleAsync("cmdkey.exe", $"/delete:{m.Groups[1].Value}");
            n++;
        }
        return OpResult.Success($"Đã xoá {n} mục đăng nhập / giấy phép đã lưu. Mở lại Word và đăng nhập lại tài khoản Microsoft.");
    }

    // ================= ODT: cài / gỡ / tải =================
    /// <summary>Tải Office Deployment Tool (setup.exe) từ CDN của Microsoft, kiểm tra chữ ký số.</summary>
    public static async Task<string> EnsureOdtAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        var path = Path.Combine(WorkDir, "setup.exe");
        if (!File.Exists(path) || (DateTime.Now - File.GetLastWriteTime(path)).TotalDays > 30)
        {
            status?.Invoke("Đang tải Office Deployment Tool…");
            await Downloader.DownloadAsync(OdtUrl, WorkDir, "setup.exe", ct: ct);
        }
        var sig = await ProcessRunner.PowerShellAsync(
            $"$s = Get-AuthenticodeSignature -LiteralPath {ProcessRunner.PsQuote(path)}; \"$($s.Status)|$($s.SignerCertificate.Subject)\"", ct: ct);
        var parts = sig.Output.Trim().Split('|', 2);
        if (parts[0] != "Valid" || parts.Length < 2 || !parts[1].Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException($"File ODT tải về không có chữ ký hợp lệ của Microsoft ({sig.Output.Trim()}).");
        }
        return path;
    }

    /// <summary>Chạy setup.exe với file cấu hình. mode: configure | download.</summary>
    public static async Task<OpResult> RunOdtAsync(string xml, string mode, Action<string>? status = null, CancellationToken ct = default, string? workDir = null)
    {
        var setup = await EnsureOdtAsync(status, ct);
        var cfg = Path.Combine(workDir ?? WorkDir, $"config-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        await File.WriteAllTextAsync(cfg, xml, new UTF8Encoding(false), ct);
        status?.Invoke(mode == "download" ? "Đang tải bộ cài Office (vài GB, có thể mất lâu)…" : "Đang chạy bộ cài Office — xem tiến độ trên cửa sổ của Microsoft…");
        Log.Info($"ODT /{mode} {cfg}");
        var r = await ProcessRunner.RunAsync(setup, $"/{mode} \"{cfg}\"", ct: ct, env: null);
        // Thư mục làm việc của setup.exe: nơi đặt file cấu hình (để SourcePath tương đối hoạt động)
        return r.ExitCode == 0
            ? OpResult.Success(mode == "download" ? "Đã tải xong bộ cài Office." : "Bộ cài Office đã chạy xong.")
            : OpResult.Fail($"Bộ cài Office kết thúc với mã {r.ExitCode}{OdtHint(r.ExitCode)}. Nhật ký: %TEMP%\\{Environment.MachineName}-*.log");
    }

    private static string OdtHint(int code) => code switch
    {
        17002 => " (bị huỷ hoặc Office đang mở)",
        17004 => " (không tải được — kiểm tra mạng / sai mã sản phẩm, ngôn ngữ)",
        30088 or 30183 => " (cấu hình không hợp lệ)",
        30015 => " (không tải được tệp cài đặt)",
        _ => "",
    };

    /// <summary>File cấu hình gỡ toàn bộ Office Click-to-Run (và bản MSI cũ nếu chọn).</summary>
    public static string RemoveAllXml(bool removeMsi) =>
        new XDocument(new XElement("Configuration",
            new XElement("Remove", new XAttribute("All", "TRUE")),
            removeMsi ? new XElement("RemoveMSI") : null,
            new XElement("Property", new XAttribute("Name", "FORCEAPPSHUTDOWN"), new XAttribute("Value", "TRUE")),
            new XElement("Display", new XAttribute("Level", "Full"), new XAttribute("AcceptEULA", "TRUE")))).ToString();
}
