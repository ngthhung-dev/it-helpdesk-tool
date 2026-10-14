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

    public ShellViewModel()
    {
        // Giai đoạn 1: đánh số 1. Trang chưa làm hiện màn hình "Sắp có".
        Groups =
        [
            new("HỆ THỐNG",
            [
                Item("dashboard", "Tổng quan", "\uE80F", 1, () => new DashboardViewModel()),
                Item("report", "Báo cáo cấu hình", "\uE8A5", 2),
                Item("sysconfig", "Cấu hình hệ thống", "\uE770", 2),
                Item("policy", "Policy", "\uE8D7", 2),
                Item("startup", "Startup & Boot", "\uE7E8", 1),
                Item("power", "Power Plan", "\uE945", 2),
            ]),
            new("TỐI ƯU",
            [
                Item("tweaks", "Tweaks Windows", "\uE713", 1),
                Item("cleanup", "Dọn rác", "\uE894", 1),
                Item("uninstall", "Gỡ app sạch", "\uE74D", 2),
            ]),
            new("CÀI ĐẶT",
            [
                Item("store", "App Store", "\uE719", 1),
                Item("fonts", "Cài font", "\uE8D2", 2),
                Item("postinstall", "App sau cài Win", "\uE896", 2),
                Item("office", "Office", "\uE7C3", 2),
            ]),
            new("WINDOWS",
            [
                Item("wininstall", "Cài Windows", "\uE7F8", 3),
                Item("winpe", "WinPE", "\uE756", 3),
                Item("usb", "Tạo USB boot", "\uE88E", 3),
                Item("iso", "Tải ISO", "\uE958", 3),
                Item("repair", "Sửa lỗi Windows", "\uE90F", 2),
            ]),
            new("Ổ ĐĨA & DỮ LIỆU",
            [
                Item("bitlocker", "BitLocker", "\uE72E", 3),
                Item("disk", "Công cụ ổ đĩa", "\uEDA2", 3),
                Item("userfolders", "Chuyển thư mục", "\uE8DE", 3),
            ]),
            new("TIỆN ÍCH",
            [
                Item("printer", "Máy in", "\uE749", 3),
                Item("outlook", "Dữ liệu Outlook", "\uE715", 3),
                Item("preview", "Xem trước file", "\uE890", 3),
                Item("zalo", "Multi-Zalo", "\uE8BD", 3),
            ]),
        ];

        Navigate(Groups[0].Items[0]);
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
