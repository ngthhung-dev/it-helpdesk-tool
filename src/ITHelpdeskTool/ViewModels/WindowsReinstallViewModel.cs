using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.ViewModels;

/// <summary>
/// Một mục "Cài lại Windows" gộp hai cách:
///  - Giữ dữ liệu: kiểm tra Windows 11, sửa lỗi / nâng cấp bằng bộ cài gốc (setup.exe), Reset this PC, môi trường khôi phục (trang Cài Windows cũ);
///  - Cài lại sạch: xoá ổ C: rồi cài mới, tự sao lưu và chép lại dữ liệu (trang Cài lại sạch).
/// Hai trang con giữ nguyên, chỉ ẩn tiêu đề lớn của chúng.
/// </summary>
public sealed partial class WindowsReinstallViewModel : PageViewModel
{
    public const string KeepMode = "keep";
    public const string CleanMode = "clean";

    public override string Title => "Cài lại Windows";

    private WinInstallViewModel? _keep;
    private ReinstallViewModel? _clean;

    public WinInstallViewModel Keep => _keep ??= new WinInstallViewModel { Embedded = true };
    public ReinstallViewModel Clean => _clean ??= new ReinstallViewModel { Embedded = true };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsKeep), nameof(IsClean), nameof(Current))]
    private string _mode;

    public bool IsKeep => Mode == KeepMode;
    public bool IsClean => Mode == CleanMode;
    public PageViewModel Current => IsKeep ? Keep : Clean;

    private bool _active;

    public WindowsReinstallViewModel()
    {
        // --page reinstall/keep (hoặc wininstall cũ) → Giữ dữ liệu; còn lại (reinstall, reinstall/download) → Cài lại sạch
        _mode = string.Equals(ShellViewModel.StartSubPage, KeepMode, StringComparison.OrdinalIgnoreCase) ? KeepMode : CleanMode;
    }

    [RelayCommand]
    private void SetMode(string mode)
    {
        if (mode == Mode) return;
        if (_active) Current.OnNavigatedFrom();
        Mode = mode;
        if (_active) Current.OnNavigatedTo();
    }

    public override void OnNavigatedTo()
    {
        _active = true;
        Current.OnNavigatedTo();
    }

    public override void OnNavigatedFrom()
    {
        _active = false;
        Current.OnNavigatedFrom();
    }
}
