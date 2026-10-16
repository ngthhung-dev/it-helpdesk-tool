using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> _cache = new();

    public IReadOnlyList<NavGroup> Groups { get; }
    public bool IsAdmin { get; } = AdminHelper.IsElevated();
    public string Version { get; } = AppInfo.Version;
    public string AppName { get; } = AppInfo.Name;
    public string Author { get; } = AppInfo.Author;

    /// <summary>Trang Giới thiệu, mở từ chân sidebar.</summary>
    public NavItem AboutItem { get; } = new("about", "Giới thiệu", "\uE946", 1, () => new AboutViewModel());

    [ObservableProperty] private PageViewModel? _currentPage;

    /// <summary>Trang mở đầu tiên, đặt từ tham số dòng lệnh --page.</summary>
    public static string? StartPage { get; set; }

    /// <summary>Phần sau dấu "/" của --page (vd startup/boot → boot) để trang mở đúng tab.</summary>
    public static string? StartSubPage => StartPage?.Split('/').ElementAtOrDefault(1);

    public ShellViewModel()
    {
        // Giai đoạn 1: đánh số 1. Trang chưa làm hiện màn hình "Sắp có".
        Groups =
        [
            new("HỆ THỐNG",
            [
                Item("dashboard", "Tổng quan", "\uE80F", 1, () => new DashboardViewModel()),
                Item("report", "Báo cáo cấu hình", "\uE8A5", 1, () => new ReportViewModel()),
                Item("sysconfig", "Cấu hình hệ thống", "\uE770", 1, () => new SysConfigViewModel()),
                Item("policy", "Policy", "\uE8D7", 1, () => new PolicyViewModel()),
                Item("startup", "Startup & Boot", "\uE7E8", 1, () => new StartupViewModel()),
                Item("power", "Power Plan", "\uE945", 1, () => new PowerViewModel()),
            ]),
            new("TỐI ƯU",
            [
                Item("tweaks", "Tweaks Windows", "\uE713", 1, () => new TweaksViewModel()),
                Item("cleanup", "Dọn rác", "\uE894", 1, () => new CleanupViewModel()),
                Item("uninstall", "Gỡ app sạch", "\uE74D", 1, () => new UninstallViewModel()),
            ]),
            new("CÀI ĐẶT",
            [
                Item("store", "App Store", "\uE719", 1, () => new StoreViewModel()),
                Item("fonts", "Cài font", "\uE8D2", 1, () => new FontsViewModel()),
                Item("postinstall", "Kho ứng dụng", "\uE896", 1, () => new PostInstallViewModel()),
                Item("office", "Office", "\uE7C3", 1, () => new OfficeViewModel()),
            ]),
            new("WINDOWS",
            [
                Item("wininstall", "Cài Windows", "\uE7F8", 1, () => new WinInstallViewModel()),
                Item("winpe", "WinPE", "\uE756", 1, () => new WinPeViewModel()),
                Item("usb", "Tạo USB boot", "\uE88E", 1, () => new UsbViewModel()),
                Item("iso", "Tải ISO", "\uE958", 1, () => new IsoViewModel()),
                Item("repair", "Sửa lỗi Windows", "\uE90F", 1, () => new RepairViewModel()),
            ]),
            new("Ổ ĐĨA & DỮ LIỆU",
            [
                Item("bitlocker", "BitLocker", "\uE72E", 1, () => new BitLockerViewModel()),
                Item("disk", "Công cụ ổ đĩa", "\uEDA2", 1, () => new DiskViewModel()),
                Item("userfolders", "Chuyển thư mục", "\uE8DE", 1, () => new UserFoldersViewModel()),
            ]),
            new("TIỆN ÍCH",
            [
                Item("printer", "Máy in", "\uE749", 1, () => new PrinterViewModel()),
                Item("outlook", "Dữ liệu Outlook", "\uE715", 1, () => new OutlookViewModel()),
                Item("preview", "Xem trước file", "\uE890", 1, () => new PreviewViewModel()),
                Item("zalo", "Multi-Zalo", "\uE8BD", 1, () => new MultiZaloViewModel()),
            ]),
        ];

        // Mở thẳng vào trang chỉ định (tham số --page <key>), không có thì vào Tổng quan
        var start = Groups.SelectMany(g => g.Items).Append(AboutItem)
            .FirstOrDefault(i => i.Key.Equals(StartPage?.Split('/')[0], StringComparison.OrdinalIgnoreCase));
        Navigate(start ?? Groups[0].Items[0]);
    }

    private static NavItem Item(string key, string title, string glyph, int phase, Func<PageViewModel>? factory = null)
    {
        NavItem? self = null;
        self = new NavItem(key, title, glyph, phase, factory ?? (() => new PlaceholderViewModel(self!)));
        return self;
    }

    [RelayCommand]
    private void Navigate(NavItem item)
    {
        foreach (var g in Groups)
            foreach (var i in g.Items)
                i.IsSelected = ReferenceEquals(i, item);
        AboutItem.IsSelected = ReferenceEquals(AboutItem, item);

        if (!_cache.TryGetValue(item.Key, out var page))
            _cache[item.Key] = page = item.Factory();

        if (ReferenceEquals(page, CurrentPage)) return;
        CurrentPage?.OnNavigatedFrom();
        CurrentPage = page;
        page.OnNavigatedTo();
    }
}
