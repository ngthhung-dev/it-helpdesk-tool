using System.IO;
using System.Text.Json;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Store;

public sealed record PostInstallPreset(string Key, string Name, string Description, string[] Ids);

/// <summary>Danh sách cài sau khi cài Windows: lưu / mở được để dùng lại cho nhiều máy.</summary>
public sealed class PostInstallList
{
    public List<string> Apps { get; set; } = [];
    public bool NetFx3 { get; set; }
    public bool UpgradeAll { get; set; }
}

public static class PostInstallService
{
    private static readonly string[] Runtimes =
    [
        "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DotNet.DesktopRuntime.8",
    ];

    public static readonly IReadOnlyList<PostInstallPreset> Presets =
    [
        new("office", "Văn phòng cơ bản", "Trình duyệt, Zalo, PDF, nén file, gõ tiếng Việt, điều khiển từ xa",
        [
            "Google.Chrome", "CocCoc.CocCoc", "zalo", "evkey", "Foxit.FoxitReader", "7zip.7zip", "DucFabulous.UltraViewer",
            "VideoLAN.VLC", .. Runtimes,
        ]),
        new("tech", "Kỹ thuật viên", "Bộ văn phòng cơ bản + công cụ kiểm tra phần cứng, mạng, ổ đĩa",
        [
            "Google.Chrome", "zalo", "evkey", "7zip.7zip", "Notepad++.Notepad++", "voidtools.Everything", "DucFabulous.UltraViewer",
            "AnyDesk.AnyDesk", "CPUID.CPU-Z", "REALiX.HWiNFO", "CrystalDewWorld.CrystalDiskInfo", "WinDirStat.WinDirStat", "Rufus.Rufus",
            "angryziber.AngryIPScanner", "PuTTY.PuTTY", "WinSCP.WinSCP", "Microsoft.PowerToys", "Microsoft.Sysinternals.Suite", .. Runtimes,
        ]),
        new("dev", "Lập trình viên", "Trình soạn mã, Git, Python, Node.js, Terminal, Docker",
        [
            "Google.Chrome", "7zip.7zip", "Microsoft.VisualStudioCode", "Git.Git", "Python.Python.3.13", "OpenJS.NodeJS.LTS",
            "Microsoft.WindowsTerminal", "Microsoft.PowerShell", "Postman.Postman", "Notepad++.Notepad++", "Microsoft.PowerToys",
            "DBeaver.DBeaver.Community", .. Runtimes,
        ]),
        new("media", "Đồ hoạ & media", "Xem video, chỉnh ảnh, quay màn hình, cắt ghép âm thanh",
        [
            "Google.Chrome", "7zip.7zip", "VideoLAN.VLC", "CodecGuide.K-LiteCodecPack.Standard", "GIMP.GIMP.3", "Inkscape.Inkscape",
            "OBSProject.OBSStudio", "IrfanSkiljan.IrfanView", "HandBrake.HandBrake", "Audacity.Audacity", "ShareX.ShareX", .. Runtimes,
        ]),
        new("runtime", "Thư viện hệ thống", "Toàn bộ Visual C++, .NET, DirectX, Java — sửa lỗi thiếu DLL khi mở phần mềm / game",
            StoreCatalog.Apps.Where(a => a.Category == StoreCatalog.Runtime).Select(a => a.Id).ToArray()),
    ];

    /// <summary>Thứ tự cài: thư viện hệ thống trước, rồi tới phần mềm.</summary>
    public static int Order(StoreApp a) => a.Category == StoreCatalog.Runtime ? 0 : 1;

    // ================= .NET Framework 3.5 =================
    public static async Task<bool?> NetFx3EnabledAsync()
    {
        var r = await ProcessRunner.PowerShellAsync("(Get-WindowsOptionalFeature -Online -FeatureName NetFx3).State.ToString()");
        var s = r.Output.Trim();
        return s.Length == 0 ? null : s.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<OpResult> EnableNetFx3Async(Action<string>? status = null, CancellationToken ct = default)
    {
        status?.Invoke("Đang bật .NET Framework 3.5 (tải từ Windows Update, có thể mất vài phút)…");
        var r = await ProcessRunner.RunAsync("dism.exe", "/Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart /English",
            line => { var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+(?:\.\d+)?)%"); if (m.Success) status?.Invoke($"Đang bật .NET 3.5… {m.Groups[1].Value}%"); },
            ct, ProcessRunner.Oem);
        return r.ExitCode is 0 or 3010
            ? OpResult.Success("Đã bật .NET Framework 3.5" + (r.ExitCode == 3010 ? " — cần khởi động lại máy." : "."), r.ExitCode == 3010)
            : OpResult.Fail($"Không bật được .NET 3.5: {OpResult.FirstLine(r.Text.Split('\n').LastOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)) ?? r.Text)}");
    }

    // ================= Lưu / mở danh sách =================
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string path, PostInstallList list) => File.WriteAllText(path, JsonSerializer.Serialize(list, Json));

    public static PostInstallList Load(string path) =>
        JsonSerializer.Deserialize<PostInstallList>(File.ReadAllText(path)) ?? new PostInstallList();

    // ================= Bộ cài trong thư mục =================
    public static List<string> ScanFolder(string dir) =>
        Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Where(f => InstallerRunner.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
}
