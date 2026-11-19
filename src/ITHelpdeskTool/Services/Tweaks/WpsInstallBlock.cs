using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Tweaks;

/// <summary>
/// Chặn cài và chạy WPS Office trên cả máy bằng Software Restriction Policies (luật "Không cho chạy" theo thư mục cài
/// và tên bộ cài). Có hiệu lực ngay với tiến trình mới, kể cả tài khoản admin, chạy được trên Windows Home.
/// Chỉ thêm / xoá đúng các luật của app; giá trị cấu hình chung của SRP chỉ tạo khi máy chưa có và ghi nhớ lại
/// để khi tắt chỉ xoá những gì app đã tạo, không đụng cấu hình SRP sẵn có của máy.
/// </summary>
public sealed class WpsInstallBlock : ICustomTweak
{
    public static WpsInstallBlock Instance { get; } = new();

    private const string Base = @"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers";
    private const string DisallowedPaths = Base + @"\0\Paths";   // cấp 0 = Disallowed
    private const string Marker = "ITHelpdeskTool.Created";

    /// <summary>Luật chặn: GUID cố định của app → đường dẫn / mẫu tên file.</summary>
    public static IReadOnlyList<(string Guid, string Path)> Rules { get; } =
    [
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7001}", @"%LOCALAPPDATA%\Kingsoft\WPS Office"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7002}", @"%ProgramFiles%\Kingsoft\WPS Office"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7003}", @"%ProgramFiles(x86)%\Kingsoft\WPS Office"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7004}", @"%ProgramFiles%\WPS Office"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7005}", @"%ProgramFiles(x86)%\WPS Office"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7006}", "WPSOffice*.exe"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7007}", "WPS_Setup*.exe"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7008}", "WPSSetup*.exe"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B7009}", "wps_installer*.exe"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B700A}", "wps_lid*.exe"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B700B}", "ksolaunch.exe"),
        ("{6B1F2C40-57A1-4C8E-9D3A-0C1E5A1B700C}", "wpsoffice.exe"),
    ];

    /// <summary>Giá trị cấu hình chung SRP cần có để luật có hiệu lực (chỉ đặt khi máy chưa có).</summary>
    private static readonly (string Name, RegistryValueKind Kind, object Value)[] BaseValues =
    [
        ("DefaultLevel", RegistryValueKind.DWord, 0x40000),          // mặc định: được chạy
        ("TransparentEnabled", RegistryValueKind.DWord, 1),          // không kiểm DLL
        ("PolicyScope", RegistryValueKind.DWord, 0),                 // áp cho mọi người, kể cả admin
        ("ExecutableTypes", RegistryValueKind.MultiString, new[] { "BAT", "CMD", "COM", "EXE", "MSI", "SCR" }),
    ];

    public bool IsAvailable() => true;
    public string UnavailableText => "";
    public bool MachineWide => true;

    public bool IsOn()
    {
        using var b = Registry.LocalMachine.OpenSubKey(Base);
        if (b?.GetValue("DefaultLevel") is null) return false;
        if (b.GetValue("ExecutableTypes") is string[] types && !types.Contains("EXE", StringComparer.OrdinalIgnoreCase)) return false;
        using var paths = Registry.LocalMachine.OpenSubKey(DisallowedPaths);
        if (paths is null) return false;
        return Rules.All(r =>
        {
            using var k = paths.OpenSubKey(r.Guid);
            return k?.GetValue("ItemData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string p
                   && p.Equals(r.Path, StringComparison.OrdinalIgnoreCase)
                   && k.GetValue("SaferFlags") is int f && f == 0;
        });
    }

    public async Task<OpResult?> ApplyAsync(bool on)
    {
        try
        {
            if (on) Enable(); else Disable();
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Không có quyền ghi chính sách — cần chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail($"Chặn cài WPS: {ex.Message}"); }
        // Mỗi tiến trình chỉ nạp chính sách SRP một lần (gpupdate / RefreshPolicyEx cũng không làm nó nạp lại) →
        // khởi động lại Explorer để mở file từ thư mục / Desktop có hiệu lực ngay; ứng dụng khác nhận sau khi khởi động lại máy.
        var r = await TweakService.RestartExplorerAsync();
        if (!r.Ok) Log.Warn($"Chặn cài WPS: {r.Message}");
        return null;
    }

    private static void Enable()
    {
        using var b = Registry.LocalMachine.CreateSubKey(Base, writable: true);
        var created = (b.GetValue(Marker) as string[] ?? []).ToList();
        foreach (var (name, kind, value) in BaseValues)
        {
            if (b.GetValue(name) is not null) continue;      // máy đã có cấu hình SRP → giữ nguyên
            b.SetValue(name, value, kind);
            if (!created.Contains(name)) created.Add(name);
        }
        if (created.Count > 0) b.SetValue(Marker, created.ToArray(), RegistryValueKind.MultiString);

        using var paths = Registry.LocalMachine.CreateSubKey(DisallowedPaths, writable: true);
        var stamp = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var (guid, path) in Rules)
        {
            using var k = paths.CreateSubKey(guid, writable: true);
            k.SetValue("ItemData", path, RegistryValueKind.ExpandString);
            k.SetValue("SaferFlags", 0, RegistryValueKind.DWord);
            k.SetValue("Description", "IT Helpdesk Tool — chặn cài WPS Office", RegistryValueKind.String);
            k.SetValue("LastModified", stamp, RegistryValueKind.QWord);
        }
    }

    private static void Disable()
    {
        using (var paths = Registry.LocalMachine.OpenSubKey(DisallowedPaths, writable: true))
            if (paths is not null)
                foreach (var (guid, _) in Rules) paths.DeleteSubKeyTree(guid, throwOnMissingSubKey: false);

        using var b = Registry.LocalMachine.OpenSubKey(Base, writable: true);
        if (b is null) return;
        // Xoá khoá rỗng còn lại (0\Paths, 0)
        DeleteIfEmpty(b, @"0\Paths");
        DeleteIfEmpty(b, "0");
        // Còn luật của người / công cụ khác → giữ cấu hình chung để luật của họ vẫn chạy
        if (HasOtherRules(b)) return;
        foreach (var name in b.GetValue(Marker) as string[] ?? []) b.DeleteValue(name, throwOnMissingValue: false);
        b.DeleteValue(Marker, throwOnMissingValue: false);
    }

    private static void DeleteIfEmpty(RegistryKey parent, string sub)
    {
        using (var k = parent.OpenSubKey(sub))
            if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
        parent.DeleteSubKey(sub, throwOnMissingSubKey: false);
    }

    private static bool HasOtherRules(RegistryKey b) =>
        b.GetSubKeyNames().Any(level =>
        {
            using var l = b.OpenSubKey(level);
            return l is not null && l.GetSubKeyNames().Any(kind =>
            {
                using var r = l.OpenSubKey(kind);
                return r is not null && r.SubKeyCount > 0;
            });
        });
}
