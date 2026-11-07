using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class AboutViewModel : PageViewModel
{
    public override string Title => "Giới thiệu";

    public string AppName => AppInfo.Name;
    public string Version => AppInfo.Version;
    public string Author => AppInfo.Author;
    public string Copyright => AppInfo.Copyright;
    public string Email => AppInfo.Email;
    public string Phone => AppInfo.Phone;
    public string Runtime => $".NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")}";

    [ObservableProperty] private string _status = "";

    // ================= Cập nhật =================
    [ObservableProperty] private string _updateText = $"Đang dùng bản {AppInfo.Version}. Bấm để tìm bản mới trên GitHub.";
    [ObservableProperty] private bool _updateDownloading;
    [ObservableProperty] private double _updatePercent;

    /// <summary>Tìm bản phát hành mới nhất; có bản mới thì hỏi, tải bộ cài, kiểm tra rồi cài đè và mở lại app.</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        try
        {
            UpdateText = "Đang tìm bản mới…";
            var u = await UpdateService.LatestAsync();
            if (u is null) { UpdateText = "Chưa có bản phát hành nào kèm bộ cài."; return; }
            var ver = u.Version.ToString(3);
            if (u.Version <= UpdateService.Current) { UpdateText = $"Đang dùng bản mới nhất ({AppInfo.Version})."; return; }

            UpdateText = $"Có bản mới {ver} (đang dùng {AppInfo.Version}).";
            var notes = u.Notes.Length == 0 ? "" : "\n\nNội dung bản mới:\n" + (u.Notes.Length > 600 ? u.Notes[..600] + "…" : u.Notes);
            if (!DialogWindow.Confirm($"Cập nhật lên bản {ver}?",
                    $"Bản đang dùng: {AppInfo.Version}\nBản mới: {ver}\n\nApp sẽ tải bộ cài từ GitHub, cài đè lên bản hiện tại rồi tự mở lại. " +
                    "Việc đang chạy trong app (cài phần mềm, tải ISO, dọn rác…) sẽ bị dừng — chờ xong rồi hãy cập nhật." + notes,
                    "Cập nhật"))
                return;

            UpdateDownloading = true;
            var path = await UpdateService.DownloadAsync(u, (done, total) =>
            {
                UpdatePercent = total > 0 ? done * 100.0 / total : 0;
                UpdateText = $"Đang tải bản {ver}: {Fmt.Size(done)}" + (total > 0 ? $" / {Fmt.Size(total)}" : "");
            });
            UpdateText = "Đã tải xong, đang mở bộ cài…";
            Log.Info($"Cập nhật: {AppInfo.Version} → {ver}, chạy {path}");
            UpdateService.StartInstaller(path);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warn($"Cập nhật: {ex.Message}");
            UpdateText = $"Không cập nhật được: {ex.Message}";
        }
        finally { UpdateDownloading = false; }
    }

    [RelayCommand]
    private void SendEmail() =>
        ShellLauncher.Open($"mailto:{Email}?subject={Uri.EscapeDataString($"[{AppName} {Version}] Hỗ trợ")}");

    /// <summary>Mở thư mục log để gửi kèm khi báo lỗi.</summary>
    [RelayCommand]
    private void OpenLogs()
    {
        System.IO.Directory.CreateDirectory(Log.Folder);
        ShellLauncher.Open(Log.Folder);
    }

#if AUTH_ONLINE
    public bool CanManageLogin => Services.Auth.OnlineAuth.Current?.Admin == true;
    public string LoginButtonText => "Quản lý tài khoản";
#else
    public bool CanManageLogin => true;
    public string LoginButtonText => "Đổi mật khẩu mở app";
#endif

    /// <summary>Bản đăng nhập trên máy: đổi mật khẩu. Bản online: quản trị viên mở trang quản lý tài khoản.</summary>
    [RelayCommand]
    private void ChangeLogin()
    {
#if AUTH_ONLINE
        new Views.AccountsWindow().ShowDialog();
#else
        var w = new Views.LoginWindow(Views.LoginWindow.Kind.Change, user: Services.Auth.LocalAuth.UserName) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() == true) _ = ShowStatusAsync("Đã đổi tài khoản mở app");
#endif
    }

    private async Task ShowStatusAsync(string text)
    {
        Status = text;
        await Task.Delay(2500);
        if (Status == text) Status = "";
    }

    [RelayCommand]
    private Task CopyEmailAsync() => CopyAsync(Email, "Đã chép email");

    [RelayCommand]
    private Task CopyPhoneAsync() => CopyAsync(Phone.Replace(" ", ""), "Đã chép số điện thoại");

    private async Task CopyAsync(string text, string ok)
    {
        try { Clipboard.SetText(text); Status = ok; }
        catch { Status = "Clipboard đang bận, thử lại"; }
        await Task.Delay(2000);
        Status = "";
    }
}
