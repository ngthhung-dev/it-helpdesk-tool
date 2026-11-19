using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Tweaks;

public enum TweakHive { CurrentUser, LocalMachine, DefaultUser }

/// <summary>Khi nào có hiệu lực.</summary>
public enum TweakEffect { Immediate, Explorer, Relogon, Restart }

/// <summary>
/// Một giá trị registry của tweak. <see cref="Off"/> = null nghĩa là xoá giá trị khi tắt (về mặc định Windows);
/// <see cref="DeleteKeyOnOff"/> = xoá cả khoá khi tắt (dùng cho các tweak dựa vào việc khoá có tồn tại hay không).
/// <see cref="Test"/> = cách nhận "đang bật" khi không so khớp đúng giá trị (ngưỡng số, bit cờ) — đối số là giá trị dạng số.
/// <see cref="MissingOk"/> = không có giá trị vẫn coi như đạt (giá trị phụ, mặc định của Windows đã đúng).
/// </summary>
public sealed record TweakReg(TweakHive Hive, string Path, string Name, RegistryValueKind Kind, object On, object? Off = null, bool DeleteKeyOnOff = false,
    Func<long, bool>? Test = null, bool MissingOk = false)
{
    public static TweakReg Dword(TweakHive h, string path, string name, int on, int? off = null) =>
        new(h, path, name, RegistryValueKind.DWord, on, off);

    public static TweakReg Str(TweakHive h, string path, string name, string on, string? off = null) =>
        new(h, path, name, RegistryValueKind.String, on, off);
}

public sealed record TweakDef(
    string Key, string Group, string Title, string Description,
    IReadOnlyList<TweakReg> Values, TweakEffect Effect = TweakEffect.Immediate,
    string? DisableService = null, bool Recommended = false, bool Win11Only = false, string? Warning = null,
    IReadOnlyList<TweakReg>? Alt = null, ICustomTweak? Custom = null);

// Alt: cách bật khác cùng tác dụng (Group Policy hoặc do công cụ khác đặt). Có một giá trị Alt khớp là coi như đã bật;
// khi trả về mặc định thì xoá luôn các giá trị Alt để trạng thái thật sự tắt.
// Custom: phần không thuần registry (tường lửa, tác vụ, Startup…) — tự đọc trạng thái thật và tự áp dụng.

public static class TweakCatalog
{
    private const TweakHive CU = TweakHive.CurrentUser;
    private const TweakHive LM = TweakHive.LocalMachine;
    private const TweakHive DU = TweakHive.DefaultUser;

    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public const string GroupUi = "Giao diện & Explorer";
    public const string GroupPerf = "Hiệu năng";
    public const string GroupPrivacy = "Quyền riêng tư & quảng cáo";
    public const string GroupUtil = "Tiện ích";
    public const string GroupWps = "WPS Office";

    public static IReadOnlyList<string> Groups { get; } = [GroupUi, GroupPerf, GroupPrivacy, GroupUtil, GroupWps];

    public static IReadOnlyList<TweakDef> All { get; } =
    [
        // ===================== Giao diện & Explorer =====================
        new("ui.ext", GroupUi, "Hiện đuôi file (.exe, .pdf, .docx…)",
            "Dễ nhận ra file giả mạo kiểu “hoadon.pdf.exe”.",
            [TweakReg.Dword(CU, Adv, "HideFileExt", 0, 1)], TweakEffect.Explorer, Recommended: true),
        new("ui.hidden", GroupUi, "Hiện file và thư mục ẩn",
            "Thấy được AppData, ProgramData và các file ẩn khi sửa lỗi.",
            [TweakReg.Dword(CU, Adv, "Hidden", 1, 2)], TweakEffect.Explorer),
        new("ui.thispc", GroupUi, "Mở File Explorer vào “This PC”",
            "Thấy ngay các ổ đĩa thay vì trang Home/Quick access.",
            [TweakReg.Dword(CU, Adv, "LaunchTo", 1)], TweakEffect.Explorer, Recommended: true),
        new("ui.classicmenu", GroupUi, "Menu chuột phải kiểu cũ (Windows 11)",
            "Hiện đủ lệnh ngay, không phải bấm “Show more options”.",
            [new(CU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", RegistryValueKind.String, "", DeleteKeyOnOff: true)],
            TweakEffect.Explorer, Recommended: true, Win11Only: true),
        new("ui.taskbarleft", GroupUi, "Thanh taskbar căn trái (Windows 11)",
            "Nút Start nằm góc trái như Windows 10.",
            [TweakReg.Dword(CU, Adv, "TaskbarAl", 0, 1)], Win11Only: true),
        new("ui.taskview", GroupUi, "Ẩn nút Task View trên taskbar", "",
            [TweakReg.Dword(CU, Adv, "ShowTaskViewButton", 0, 1)]),
        new("ui.searchbox", GroupUi, "Ẩn ô tìm kiếm trên taskbar",
            "Vẫn tìm được bằng cách bấm Start rồi gõ.",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0)]),
        new("ui.dark", GroupUi, "Chế độ tối (Dark mode)", "Cho cả Windows và ứng dụng.",
            [TweakReg.Dword(CU, Personalize, "AppsUseLightTheme", 0, 1), TweakReg.Dword(CU, Personalize, "SystemUsesLightTheme", 0, 1)]),
        new("ui.shortcutname", GroupUi, "Bỏ chữ “- Shortcut” khi tạo lối tắt", "",
            [TweakReg.Str(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\NamingTemplates", "ShortcutNameTemplate", "%s.lnk")],
            TweakEffect.Explorer, Recommended: true),
        new("ui.thispcdesktop", GroupUi, "Hiện “This PC” ngoài Desktop", "",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0)],
            TweakEffect.Explorer),
        new("ui.cpldesktop", GroupUi, "Hiện “Control Panel” ngoài Desktop", "Mở nhanh Control Panel kiểu cũ (Programs and Features, Devices and Printers…).",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", 0)],
            TweakEffect.Explorer),
        new("ui.seconds", GroupUi, "Hiện giây trên đồng hồ taskbar", "",
            [TweakReg.Dword(CU, Adv, "ShowSecondsInSystemClock", 1)], TweakEffect.Explorer),
        new("ui.endtask", GroupUi, "Thêm “End task” khi chuột phải ứng dụng trên taskbar (Windows 11)",
            "Tắt ứng dụng bị treo mà không cần mở Task Manager.",
            [TweakReg.Dword(CU, $@"{Adv}\TaskbarDeveloperSettings", "TaskbarEndTask", 1)], Recommended: true, Win11Only: true),

        // ===================== Hiệu năng =====================
        new("perf.animations", GroupPerf, "Giảm hiệu ứng động",
            "Tắt hiệu ứng thu/phóng cửa sổ và taskbar — máy yếu thấy nhanh hơn rõ rệt.",
            [
                TweakReg.Str(CU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1"),
                TweakReg.Dword(CU, Adv, "TaskbarAnimations", 0, 1),
            ], TweakEffect.Relogon),
        new("perf.menudelay", GroupPerf, "Menu hiện ngay (bỏ độ trễ)",
            "Menu con mở ngay khi rê chuột thay vì chờ 0,4 giây.",
            [TweakReg.Str(CU, @"Control Panel\Desktop", "MenuShowDelay", "50", "400") with { Test = v => v <= 100 }], TweakEffect.Relogon, Recommended: true),
        new("perf.transparency", GroupPerf, "Tắt hiệu ứng trong suốt", "Giảm tải card đồ hoạ tích hợp.",
            [TweakReg.Dword(CU, Personalize, "EnableTransparency", 0, 1)]),
        new("perf.gamedvr", GroupPerf, "Tắt Game Bar ghi hình nền",
            "Game DVR ngầm ghi hình làm giảm FPS khi chơi game.",
            [
                TweakReg.Dword(CU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0) with { MissingOk = true },
            ], Recommended: true,
            Alt: [TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0)]),
        new("perf.bgapps", GroupPerf, "Không cho ứng dụng Store chạy nền",
            "Ứng dụng như Mail, Weather, Xbox không tự chạy ngầm.",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0)],
            Alt:
            [
                TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0),
                TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2),
            ]),
        new("perf.sysmain", GroupPerf, "Tắt SysMain (Superfetch)",
            "Giảm tình trạng ổ cứng chạy 100% trên máy dùng HDD.",
            [], DisableService: "SysMain",
            Warning: "Máy dùng SSD và đủ RAM thì nên để bật — SysMain giúp mở ứng dụng nhanh hơn."),
        new("perf.search", GroupPerf, "Tắt dịch vụ lập chỉ mục tìm kiếm (Windows Search)",
            "Giảm đọc ghi ổ cứng nền. Tìm kiếm trong Start và Outlook sẽ chậm hơn.",
            [], DisableService: "WSearch",
            Warning: "Outlook và ô tìm kiếm Start sẽ tìm chậm và có thể thiếu kết quả."),

        // ===================== Quyền riêng tư =====================
        new("priv.telemetry", GroupPrivacy, "Giảm thu thập dữ liệu (Telemetry)",
            "Đặt mức gửi dữ liệu chẩn đoán thấp nhất và tắt dịch vụ DiagTrack.",
            [TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0) with { Test = v => v <= 1 }],
            DisableService: "DiagTrack", Recommended: true,
            Alt: [TweakReg.Dword(LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0) with { Test = v => v <= 1 }]),
        new("priv.adid", GroupPrivacy, "Tắt mã quảng cáo (Advertising ID)",
            "Ứng dụng không theo dõi được bạn để hiện quảng cáo cá nhân hoá.",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)], Recommended: true,
            Alt: [TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1)]),
        new("priv.suggestions", GroupPrivacy, "Tắt gợi ý và quảng cáo trong Start, Settings, màn hình khoá",
            "Bỏ “mẹo”, ứng dụng gợi ý, quảng cáo trên lock screen.",
            [
                TweakReg.Dword(CU, Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                TweakReg.Dword(CU, Cdm, "SubscribedContent-338388Enabled", 0, 1),
                TweakReg.Dword(CU, Cdm, "SubscribedContent-338389Enabled", 0, 1),
                TweakReg.Dword(CU, Cdm, "SubscribedContent-353694Enabled", 0, 1),
                TweakReg.Dword(CU, Cdm, "SubscribedContent-353696Enabled", 0, 1),
                TweakReg.Dword(CU, Cdm, "SoftLandingEnabled", 0, 1),
                TweakReg.Dword(CU, Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
            ], Recommended: true),
        new("priv.tailored", GroupPrivacy, "Tắt “trải nghiệm cá nhân hoá” từ dữ liệu chẩn đoán", "",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)], Recommended: true,
            Alt: [TweakReg.Dword(CU, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1)]),
        new("priv.activity", GroupPrivacy, "Tắt lịch sử hoạt động (Activity History)",
            "Windows không ghi lại các file, trang web bạn đã mở để đồng bộ.",
            [
                TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0) with { MissingOk = true },
            ],
            Alt: [TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0)]),
        new("priv.bing", GroupPrivacy, "Không tìm Bing trong Start",
            "Ô tìm kiếm chỉ tìm trong máy, nhanh hơn.",
            [TweakReg.Dword(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)], TweakEffect.Explorer,
            Alt:
            [
                TweakReg.Dword(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
            ]),

        // ===================== Tiện ích =====================
        new("util.longpath", GroupUtil, "Cho phép đường dẫn dài trên 260 ký tự",
            "Tránh lỗi chép/giải nén file có đường dẫn quá dài.",
            [TweakReg.Dword(LM, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)], TweakEffect.Restart, Recommended: true),
        new("util.stickykeys", GroupUtil, "Tắt hỏi Sticky Keys khi bấm Shift 5 lần",
            "Hay bật nhầm khi chơi game hoặc gõ nhanh.",
            [TweakReg.Str(CU, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510") with { Test = v => (v & 4) == 0 }], Recommended: true),
        new("util.numlock", GroupUtil, "Bật NumLock khi khởi động",
            "Bàn phím số dùng được ngay ở màn hình đăng nhập.",
            [
                TweakReg.Str(DU, @"Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0") with { Test = v => (v & 2) != 0 },
                TweakReg.Str(CU, @"Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0") with { Test = v => (v & 2) != 0 },
            ], TweakEffect.Restart, Recommended: true),
        new("util.verbose", GroupUtil, "Hiện chi tiết khi khởi động/tắt máy",
            "Màn hình ghi rõ đang làm gì (“Applying settings…”) — dễ biết máy treo ở bước nào.",
            [TweakReg.Dword(LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1)], TweakEffect.Restart,
            Alt: [TweakReg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "VerboseStatus", 1)]),
        new("util.autoreboot", GroupUtil, "Không tự khởi động lại khi bị màn hình xanh",
            "Giữ màn hình xanh để đọc mã lỗi thay vì máy tự khởi động lại.",
            [TweakReg.Dword(LM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 0, 1)], TweakEffect.Restart),
        // ===================== WPS Office (của người đang đăng nhập) =====================
        new("wps.ads", GroupWps, "WPS: chặn quảng cáo và cửa sổ quảng cáo",
            "Chặn mạng các tiến trình hiện quảng cáo/tin nóng (wpscenter, wpsnotify), tắt tác vụ hiện thông báo và khởi động cùng Windows của chúng. Soạn thảo, đăng nhập vẫn bình thường. Quảng cáo nhúng sẵn trong trang chủ WPS có thể vẫn còn — muốn hết hẳn thì dùng mục chặn toàn bộ Internet.",
            [], Custom: new WpsBlock("quảng cáo", ["wpscenter.exe", "wpsnotify.exe"], tasks: @"^(WpsNotify|PPTAssistant|WpsExternal)", killRunning: true)),
        new("wps.cloud", GroupWps, "WPS: không đồng bộ tài liệu lên cloud",
            "Chặn mạng tiến trình đồng bộ / sao lưu tài liệu lên WPS Cloud (wpscloudsvr, wpscloudlaunch) và tắt khởi động cùng Windows của nó. Nên đăng nhập tài khoản trước rồi mới bật.",
            [], Custom: new WpsBlock("cloud", ["wpscloudsvr.exe", "wpscloudlaunch.exe"]),
            Warning: "Tài liệu sẽ không lên WPS Cloud nữa (kể cả khi đã đăng nhập Gmail).

Nếu sau khi bật mà WPS báo lỗi đăng nhập hoặc khoá tính năng, hãy tắt lại mục này."),
        new("wps.telemetry", GroupWps, "WPS: chặn gửi báo lỗi và thống kê",
            "Chặn mạng các tiến trình gửi báo cáo lỗi và dữ liệu sử dụng (kcrashdumper, transerr). Tiến trình chính của WPS vẫn liên lạc máy chủ để đăng nhập, kiểm tra tài khoản.",
            [], Custom: new WpsBlock("thu thập dữ liệu", ["kcrashdumper.exe", "kcrashdumper64.exe", "transerr.exe"])),
        new("wps.update", GroupWps, "WPS: tắt tự cập nhật",
            "Chặn mạng trình cập nhật (wpsupdate, updateself), tắt tác vụ WpsUpdateTask và dịch vụ cập nhật của WPS.",
            [], Custom: new WpsBlock("cập nhật", ["wpsupdate.exe", "updateself.exe", "wpsupdatesvr.exe"], tasks: @"^WpsUpdate", services: @"(wps|kingsoft).*update", killRunning: true),
            Warning: "WPS sẽ không nhận bản vá lỗi / bảo mật. Thỉnh thoảng nên tắt mục này để cập nhật rồi bật lại."),
        new("wps.firewall", GroupWps, "WPS: chặn toàn bộ truy cập Internet (tường lửa)",
            "Chặn mọi file chạy của WPS kết nối Internet và tắt mọi mục khởi động cùng Windows của WPS. Không còn quảng cáo, đồng bộ hay gửi dữ liệu nào.",
            [], Custom: new WpsBlock("tường lửa", null),
            Warning: "WPS sẽ không đăng nhập được, không dùng được cloud, mẫu tài liệu online, dịch, AI…
Bản miễn phí có thể khoá một số tính năng khi không kiểm tra được tài khoản.

Nếu chỉ muốn không đồng bộ dữ liệu mà vẫn đăng nhập, dùng các mục WPS khác thay vì mục này."),
        new("wps.blockinstall", GroupWps, "WPS: chặn cài và chạy WPS Office trên máy này",
            "Đặt chính sách Windows (Software Restriction Policies) không cho chạy bộ cài WPS (WPSOffice*.exe…) và mọi file trong thư mục cài WPS. Áp dụng cho mọi tài khoản, kể cả admin — tải bộ cài về cũng không chạy được. Explorer được khởi động lại để có hiệu lực ngay; trình duyệt và ứng dụng đang mở chỉ nhận sau khi khởi động lại máy.",
            [], TweakEffect.Restart, Custom: WpsInstallBlock.Instance,
            Warning: "WPS đang cài trên máy (nếu có) cũng sẽ không mở được, kể cả trình gỡ của nó.
Nếu cần gỡ WPS, hãy gỡ trước rồi mới bật mục này (Gỡ app sạch → WPS Office).

Tắt mục này để cho cài lại."),
    ];
}
