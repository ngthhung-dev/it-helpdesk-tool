using System.IO;
using ITHelpdeskTool.Services.Store;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>Thành phần thêm vào WinPE (gói WinPE_OCs của Windows ADK).</summary>
public sealed record PeComponent(string Key, string Name, string Description, string[] Packages, bool DefaultOn);

public sealed class PeOptions
{
    public string WorkDir { get; set; } = @"C:\WinPE_amd64";
    public List<PeComponent> Components { get; set; } = [];
    public string DriversDir { get; set; } = "";
    public string ToolsDir { get; set; } = "";
    public string TimeZone { get; set; } = "SE Asia Standard Time";
    public string StartCommands { get; set; } = "";
    public bool HighPerformance { get; set; } = true;
}

/// <summary>Tạo WinPE bằng Windows ADK chính thức của Microsoft: copype → thêm thành phần / driver / công cụ → ISO hoặc USB.</summary>
public static class WinPeService
{
    public static readonly IReadOnlyList<PeComponent> Components =
    [
        new("powershell", "PowerShell", "PowerShell, WMI, .NET, lệnh DISM và quản lý ổ đĩa trong WinPE.",
            ["WinPE-WMI", "WinPE-NetFX", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI", "WinPE-DismCmdlets"], true),
        new("bitlocker", "BitLocker", "Mở khoá / quản lý ổ mã hoá BitLocker (manage-bde) trong WinPE.",
            ["WinPE-WMI", "WinPE-SecureStartup", "WinPE-EnhancedStorage"], true),
        new("hta", "HTA / VBScript", "Chạy script VBS và ứng dụng HTA.", ["WinPE-WMI", "WinPE-Scripting", "WinPE-HTA"], false),
        new("network", "Mạng nâng cao", "PPPoE, RNDIS (chia sẻ mạng qua điện thoại), WDS tools.", ["WinPE-PPPoE", "WinPE-RNDIS", "WinPE-WDS-Tools"], false),
    ];

    // ================= Windows ADK =================
    public static string? KitsRoot =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots", "KitsRoot10", null) as string;

    public static string? AdkDir => KitsRoot is { } r ? Path.Combine(r, "Assessment and Deployment Kit") : null;
    public static string? EnvBat => AdkDir is { } d && File.Exists(Path.Combine(d, @"Deployment Tools\DandISetEnv.bat")) ? Path.Combine(d, @"Deployment Tools\DandISetEnv.bat") : null;
    public static string? PeRoot => AdkDir is { } d && Directory.Exists(Path.Combine(d, @"Windows Preinstallation Environment\amd64")) ? Path.Combine(d, @"Windows Preinstallation Environment\amd64") : null;
    public static bool HasAdk => EnvBat is not null;
    public static bool HasPeAddon => PeRoot is not null && File.Exists(Path.Combine(PeRoot, @"en-us\winpe.wim"));

    public static string AdkVersion()
    {
        try
        {
            var dism = AdkDir is { } d ? Path.Combine(d, @"Deployment Tools\amd64\DISM\dism.exe") : "";
            return File.Exists(dism) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(dism).ProductVersion ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>Phiên bản ADK có WinPE add-on tương ứng trên winget (hai gói phải cùng phiên bản).</summary>
    public const string PairedVersion = "10.1.26100.2454";

    /// <summary>Cài Windows ADK (Deployment Tools) và WinPE add-on qua winget (nguồn Microsoft).</summary>
    public static async Task<OpResult> InstallAdkAsync(Action<string> log, CancellationToken ct)
    {
        if (!HasAdk)
        {
            log("Đang cài Windows ADK (Deployment Tools)…");
            var r = await WingetService.InstallAsync("Microsoft.WindowsADK", "Windows ADK", l => { if (StoreInstaller.Stage(l) is { } s) log(s); }, ct,
                $"--version {PairedVersion} --override \"/quiet /norestart /features OptionId.DeploymentTools\"");
            log(r.Message);
            if (!r.Ok) return r;
        }
        if (!HasPeAddon)
        {
            log("Đang cài Windows PE add-on (khoảng 1–2 GB)…");
            var r = await WingetService.InstallAsync("Microsoft.WindowsADK.WinPEAddon", "Windows PE add-on", l => { if (StoreInstaller.Stage(l) is { } s) log(s); }, ct,
                $"--version {PairedVersion}");
            log(r.Message);
            if (!r.Ok)
                return OpResult.Fail($"{r.Message}. WinPE add-on phải cùng phiên bản với Windows ADK ({AdkVersion()}) — gỡ ADK rồi cài lại từ đây, hoặc tải cặp ADK + WinPE add-on cùng phiên bản trên trang Microsoft.");
        }
        return HasAdk && HasPeAddon ? OpResult.Success("Windows ADK và WinPE add-on đã sẵn sàng.") : OpResult.Fail("Đã chạy bộ cài nhưng vẫn chưa thấy Windows ADK / WinPE add-on.");
    }

    // ================= Tạo WinPE =================
    /// <summary>Chạy lệnh trong môi trường “Deployment and Imaging Tools”.</summary>
    private static async Task<ProcessResult> AdkCmdAsync(string command, Action<string> log, CancellationToken ct)
    {
        log($"> {command}");
        var full = $"/d /c \"call \"{EnvBat}\" >nul && {command}\"";
        var r = await ProcessRunner.RunAsync("cmd.exe", full, l => { if (l.Trim().Length > 0) log(l.TrimEnd()); }, ct, ProcessRunner.Oem);
        foreach (var e in r.Error.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)) log(e);
        return r;
    }

    private static async Task<ProcessResult> DismAsync(string args, Action<string> log, CancellationToken ct, bool quiet = false)
    {
        if (!quiet) log($"> dism {args}");
        var dism = Path.Combine(AdkDir!, @"Deployment Tools\amd64\DISM\dism.exe");
        var r = await ProcessRunner.RunAsync(File.Exists(dism) ? dism : "dism.exe", "/English " + args,
            l => { var t = l.Trim(); if (t.Length > 0 && !quiet && !t.StartsWith('[')) log(t); }, ct, ProcessRunner.Oem);
        return r;
    }

    /// <summary>
    /// Dựng thư mục WinPE (media + boot.wim đã tuỳ biến). Trả đường dẫn thư mục làm việc.
    /// step(tên bước, % ước tính).
    /// </summary>
    public static async Task<OpResult> BuildAsync(PeOptions o, Action<string> log, Action<string, int> step, CancellationToken ct)
    {
        if (!HasAdk || !HasPeAddon) return OpResult.Fail("Chưa có Windows ADK + WinPE add-on.");
        var work = o.WorkDir.TrimEnd('\\');
        var mount = Path.Combine(work, "mount");
        var wim = Path.Combine(work, @"media\sources\boot.wim");

        step("Chuẩn bị thư mục", 2);
        if (Directory.Exists(work))
        {
            // Thư mục cũ có thể còn ảnh đang gắn từ lần trước bị dừng giữa chừng
            if (Directory.Exists(mount) && Directory.EnumerateFileSystemEntries(mount).Any())
                await DismAsync($"/Unmount-Image /MountDir:\"{mount}\" /Discard", log, ct);
            await DismAsync("/Cleanup-Mountpoints", log, ct, quiet: true);
            Directory.Delete(work, true);
        }

        step("Sao chép WinPE gốc (copype)", 8);
        var r = await AdkCmdAsync($"copype amd64 \"{work}\"", log, ct);
        if (!File.Exists(wim)) return OpResult.Fail($"copype lỗi: {OpResult.FirstLine(r.Text)}");

        step("Gắn boot.wim", 15);
        r = await DismAsync($"/Mount-Image /ImageFile:\"{wim}\" /Index:1 /MountDir:\"{mount}\"", log, ct);
        if (!r.Success) return OpResult.Fail($"Không gắn được boot.wim: {OpResult.FirstLine(r.Text)}");

        var committed = false;
        try
        {
            // Thành phần (mỗi gói kèm gói ngôn ngữ en-us), bỏ trùng, giữ thứ tự phụ thuộc
            var ocs = Path.Combine(PeRoot!, "WinPE_OCs");
            var packages = o.Components.SelectMany(c => c.Packages).Distinct().ToList();
            var i = 0;
            foreach (var pkg in packages)
            {
                ct.ThrowIfCancellationRequested();
                step($"Thêm {pkg}", 20 + 50 * i++ / Math.Max(1, packages.Count));
                foreach (var cab in new[] { Path.Combine(ocs, pkg + ".cab"), Path.Combine(ocs, "en-us", pkg + "_en-us.cab") }.Where(File.Exists))
                {
                    r = await DismAsync($"/Image:\"{mount}\" /Add-Package /PackagePath:\"{cab}\"", log, ct, quiet: true);
                    log((r.Success ? "  + " : "  ! ") + Path.GetFileName(cab) + (r.Success ? "" : $": {OpResult.FirstLine(r.Text)}"));
                }
            }

            if (o.DriversDir.Length > 0 && Directory.Exists(o.DriversDir))
            {
                step("Thêm driver", 72);
                r = await DismAsync($"/Image:\"{mount}\" /Add-Driver /Driver:\"{o.DriversDir}\" /Recurse", log, ct);
                log(r.Success ? "Đã thêm driver." : $"! Driver: {OpResult.FirstLine(r.Text)}");
            }

            if (o.ToolsDir.Length > 0 && Directory.Exists(o.ToolsDir))
            {
                step("Chép công cụ", 76);
                var dest = Path.Combine(mount, "Tools");
                foreach (var f in Directory.EnumerateFiles(o.ToolsDir, "*", SearchOption.AllDirectories))
                {
                    var t = Path.Combine(dest, Path.GetRelativePath(o.ToolsDir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    File.Copy(f, t, true);
                }
                log($"Đã chép công cụ vào X:\\Tools");
            }

            step("Thiết lập", 80);
            if (o.TimeZone.Length > 0) await DismAsync($"/Image:\"{mount}\" /Set-TimeZone:\"{o.TimeZone}\"", log, ct);
            await DismAsync($"/Image:\"{mount}\" /Set-ScratchSpace:512", log, ct);
            var startnet = Path.Combine(mount, @"Windows\System32\startnet.cmd");
            var lines = new List<string> { "wpeinit" };
            if (o.HighPerformance) lines.Add("powercfg /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            if (Directory.Exists(Path.Combine(mount, "Tools"))) lines.Add("set PATH=%PATH%;X:\\Tools");
            lines.AddRange(o.StartCommands.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0));
            await File.WriteAllLinesAsync(startnet, lines, ct);
            log("startnet.cmd: " + string.Join(" | ", lines));

            step("Lưu boot.wim", 88);
            r = await DismAsync($"/Unmount-Image /MountDir:\"{mount}\" /Commit", log, ct);
            if (!r.Success) return OpResult.Fail($"Không lưu được boot.wim: {OpResult.FirstLine(r.Text)}");
            committed = true;
        }
        finally
        {
            if (!committed) await DismAsync($"/Unmount-Image /MountDir:\"{mount}\" /Discard", log, CancellationToken.None);
        }
        step("Xong", 92);
        return OpResult.Success($"Đã dựng WinPE ({Fmt.Size(new FileInfo(wim).Length)}) tại {work}.");
    }

    public static async Task<OpResult> MakeIsoAsync(string workDir, string iso, Action<string> log, CancellationToken ct)
    {
        if (File.Exists(iso)) File.Delete(iso);
        var r = await AdkCmdAsync($"MakeWinPEMedia /ISO \"{workDir.TrimEnd('\\')}\" \"{iso}\"", log, ct);
        return File.Exists(iso)
            ? OpResult.Success($"Đã tạo {Path.GetFileName(iso)} ({Fmt.Size(new FileInfo(iso).Length)}).")
            : OpResult.Fail($"Không tạo được ISO: {OpResult.FirstLine(r.Text)}");
    }

    /// <summary>Ghi WinPE ra USB (MakeWinPEMedia /UFD định dạng lại USB).</summary>
    public static async Task<OpResult> MakeUsbAsync(string workDir, UsbDisk disk, Action<string> log, CancellationToken ct)
    {
        var fresh = (await UsbService.ListAsync(ct)).FirstOrDefault(d => d.Number == disk.Number);
        if (fresh is null) return OpResult.Fail("USB đã chọn không còn / không phải USB.");
        var letter = fresh.Letters.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (letter is null) return OpResult.Fail("USB chưa có ký tự ổ — rút ra cắm lại hoặc định dạng USB trước.");
        var r = await AdkCmdAsync($"MakeWinPEMedia /UFD /F \"{workDir.TrimEnd('\\')}\" {letter}", log, ct);
        return File.Exists(Path.Combine(letter + "\\", @"sources\boot.wim"))
            ? OpResult.Success($"Đã ghi WinPE ra USB {letter}.")
            : OpResult.Fail($"Không ghi được USB: {OpResult.FirstLine(r.Text)}");
    }
}
