using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Policy;

/// <summary>Policy ghi vào máy (HKLM) hay vào hồ sơ một người dùng (HKU\SID, tức HKCU của người đó).</summary>
public enum PolicyScope { Machine, User }

/// <summary>Khi nào có hiệu lực.</summary>
public enum PolicyEffect { Immediate, Relogon, Restart }

/// <summary>
/// Một giá trị registry của policy. <see cref="Off"/> = null nghĩa là xoá giá trị khi tắt (trả về mặc định Windows).
/// </summary>
public sealed record RegSet(string Path, string Name, RegistryValueKind Kind, Func<object> On, object? Off = null)
{
    public RegSet(string path, string name, int on, int? off = null)
        : this(path, name, RegistryValueKind.DWord, () => on, off) { }

    public RegSet(string path, string name, string on)
        : this(path, name, RegistryValueKind.String, () => on) { }
}

public sealed record PolicyDef(
    string Key, string Group, string Title, string Description,
    PolicyScope Scope, PolicyEffect Effect, IReadOnlyList<RegSet> Values,
    bool ProOnly = false, string? Warning = null);

public static class PolicyCatalog
{
    private const string WU = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string AU = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string SysPol = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ExpPol = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public const string GroupUpdate = "Windows Update";
    public const string GroupUser = "Hạn chế người dùng";
    public const string GroupDevice = "Thiết bị & USB";
    public const string GroupApps = "Ứng dụng & nội dung";
    public const string GroupLogon = "Đăng nhập & khoá máy";

    public static IReadOnlyList<string> Groups { get; } = [GroupUpdate, GroupUser, GroupDevice, GroupApps, GroupLogon];

    /// <summary>"Windows 11"/"Windows 10" và bản hiện tại (24H2...) để giữ phiên bản.</summary>
    private static (string Product, string Version) CurrentRelease()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = int.TryParse(k?.GetValue("CurrentBuild") as string, out var b) ? b : 0;
        var ver = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
        return (build >= 22000 ? "Windows 11" : "Windows 10", ver);
    }

    public static IReadOnlyList<PolicyDef> All { get; } =
    [
        // ===================== Windows Update =====================
        new("wu.noauto", GroupUpdate, "Tắt tự động cập nhật",
            "Windows không tự tải và cài bản cập nhật. Vẫn cập nhật tay được trong Settings.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(AU, "NoAutoUpdate", 1)],
            Warning: "Máy sẽ thiếu bản vá bảo mật nếu quên cập nhật tay."),
        new("wu.notify", GroupUpdate, "Hỏi trước khi tải bản cập nhật",
            "Chỉ thông báo có bản mới; người dùng bấm thì mới tải và cài.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(AU, "AUOptions", 2)]),
        new("wu.noreboot", GroupUpdate, "Không tự khởi động lại khi đang có người dùng",
            "Tránh mất dữ liệu đang làm vì máy tự restart sau khi cập nhật.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(AU, "NoAutoRebootWithLoggedOnUsers", 1)]),
        new("wu.nodrivers", GroupUpdate, "Không cập nhật driver qua Windows Update",
            "Giữ nguyên driver đang chạy ổn (máy in, card đồ hoạ, âm thanh...).",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(WU, "ExcludeWUDriversInQualityUpdate", 1)]),
        new("wu.deferfeature", GroupUpdate, "Hoãn bản nâng cấp lớn 365 ngày",
            "Bản vá bảo mật vẫn cài bình thường, chỉ hoãn các đợt nâng cấp tính năng (vd 24H2 → 25H2).",
            PolicyScope.Machine, PolicyEffect.Immediate,
            [new(WU, "DeferFeatureUpdates", 1), new(WU, "DeferFeatureUpdatesPeriodInDays", 365)], ProOnly: true),
        new("wu.pinversion", GroupUpdate, "Giữ nguyên phiên bản Windows hiện tại",
            "Không cho nâng lên phiên bản mới hơn (vd giữ 24H2). Vẫn nhận bản vá của phiên bản này.",
            PolicyScope.Machine, PolicyEffect.Immediate,
            [
                new(WU, "TargetReleaseVersion", 1),
                new(WU, "ProductVersion", RegistryValueKind.String, () => CurrentRelease().Product),
                new(WU, "TargetReleaseVersionInfo", RegistryValueKind.String, () => CurrentRelease().Version),
            ], ProOnly: true),

        // ===================== Hạn chế người dùng =====================
        new("user.taskmgr", GroupUser, "Chặn Task Manager",
            "Người dùng không mở được Task Manager (Ctrl+Shift+Esc).",
            PolicyScope.User, PolicyEffect.Immediate, [new(SysPol, "DisableTaskMgr", 1)]),
        new("user.regedit", GroupUser, "Chặn Registry Editor",
            "Không mở được regedit và không nhập được file .reg.",
            PolicyScope.User, PolicyEffect.Immediate, [new(SysPol, "DisableRegistryTools", 1)]),
        new("user.cmd", GroupUser, "Chặn Command Prompt",
            "Không mở được cửa sổ CMD. File .bat (script đăng nhập) vẫn chạy được.",
            PolicyScope.User, PolicyEffect.Immediate, [new(@"SOFTWARE\Policies\Microsoft\Windows\System", "DisableCMD", 2)]),
        new("user.controlpanel", GroupUser, "Chặn Control Panel và Settings",
            "Không mở được Control Panel lẫn ứng dụng Settings.",
            PolicyScope.User, PolicyEffect.Relogon, [new(ExpPol, "NoControlPanel", 1)]),
        new("user.run", GroupUser, "Ẩn hộp thoại Run (Win+R)",
            "Bỏ lệnh Run khỏi Start và phím tắt Win+R.",
            PolicyScope.User, PolicyEffect.Relogon, [new(ExpPol, "NoRun", 1)]),
        new("user.wallpaper", GroupUser, "Không cho đổi hình nền",
            "Giữ hình nền công ty, người dùng không đổi được.",
            PolicyScope.User, PolicyEffect.Relogon, [new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop", "NoChangingWallPaper", 1)]),
        new("user.websearch", GroupUser, "Tắt kết quả web (Bing) khi tìm trong Start",
            "Ô tìm kiếm chỉ tìm file và ứng dụng trong máy, nhanh hơn và không gửi từ khoá ra ngoài.",
            PolicyScope.User, PolicyEffect.Relogon, [new(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)]),

        // ===================== Thiết bị =====================
        new("dev.noautorun", GroupDevice, "Tắt AutoRun/AutoPlay mọi ổ",
            "Cắm USB/đĩa không tự chạy gì — chặn kiểu virus lây qua USB.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(ExpPol, "NoDriveTypeAutoRun", 255)]),
        new("dev.usbblock", GroupDevice, "Chặn USB lưu trữ",
            "USB, ổ cứng gắn ngoài không nhận nữa. Chuột, bàn phím, máy in USB vẫn dùng bình thường.",
            PolicyScope.Machine, PolicyEffect.Immediate,
            [new(@"SYSTEM\CurrentControlSet\Services\USBSTOR", "Start", 4, off: 3)],
            Warning: "USB đang cắm sẵn vẫn dùng được cho tới khi rút ra."),
        new("dev.usbreadonly", GroupDevice, "USB chỉ đọc",
            "Đọc được dữ liệu trong USB nhưng không chép được gì vào USB (chống mang dữ liệu ra ngoài).",
            PolicyScope.Machine, PolicyEffect.Immediate,
            [new(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", "WriteProtect", 1)],
            Warning: "Rút USB ra cắm lại mới có hiệu lực."),

        // ===================== Ứng dụng & nội dung =====================
        new("app.store", GroupApps, "Tắt Microsoft Store",
            "Người dùng không cài được ứng dụng từ Store.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(@"SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", 1)], ProOnly: true),
        new("app.consumer", GroupApps, "Không tự cài app quảng cáo",
            "Chặn Windows tự cài Candy Crush, TikTok... và gợi ý ứng dụng trong Start.",
            PolicyScope.Machine, PolicyEffect.Relogon,
            [new(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1)]),
        new("app.copilot", GroupApps, "Tắt Copilot",
            "Ẩn và tắt Windows Copilot.",
            PolicyScope.Machine, PolicyEffect.Relogon,
            [new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)]),
        new("app.recall", GroupApps, "Tắt Recall (chụp màn hình AI)",
            "Không lưu ảnh chụp màn hình định kỳ để tìm kiếm bằng AI.",
            PolicyScope.Machine, PolicyEffect.Restart,
            [new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)]),
        new("app.widgets", GroupApps, "Tắt Widgets / Tin tức",
            "Bỏ bảng tin tức, thời tiết trên thanh taskbar.",
            PolicyScope.Machine, PolicyEffect.Relogon, [new(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)]),
        new("app.onedrive", GroupApps, "Tắt OneDrive",
            "Không đồng bộ file lên OneDrive. File đã có trên máy vẫn giữ nguyên.",
            PolicyScope.Machine, PolicyEffect.Relogon,
            [new(@"SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1)],
            Warning: "Người đang dùng OneDrive để sao lưu Desktop/Documents sẽ dừng đồng bộ."),

        // ===================== Đăng nhập & khoá máy =====================
        new("logon.hidelast", GroupLogon, "Không hiện tên người dùng cuối",
            "Màn hình đăng nhập để trống tên, phải gõ cả tên lẫn mật khẩu.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(SysPol, "DontDisplayLastUserName", 1)]),
        new("logon.cad", GroupLogon, "Bắt nhấn Ctrl+Alt+Del trước khi đăng nhập",
            "Chống phần mềm giả màn hình đăng nhập để lấy mật khẩu.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(SysPol, "DisableCAD", 0)]),
        new("logon.autolock", GroupLogon, "Tự khoá máy sau 10 phút không dùng",
            "Rời bàn quên khoá máy thì sau 10 phút máy tự khoá.",
            PolicyScope.Machine, PolicyEffect.Restart, [new(SysPol, "InactivityTimeoutSecs", 600)]),
        new("logon.nolockscreen", GroupLogon, "Bỏ màn hình khoá (ảnh nền trước khi đăng nhập)",
            "Vào thẳng ô nhập mật khẩu, không phải vuốt màn hình khoá.",
            PolicyScope.Machine, PolicyEffect.Immediate, [new(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1)], ProOnly: true),
    ];
}
