using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;

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

    /// <summary>Đổi tên đăng nhập / mật khẩu mở app.</summary>
    [RelayCommand]
    private void ChangeLogin()
    {
        var w = new Views.LoginWindow(Views.LoginWindow.Kind.Change, user: Services.Auth.LocalAuth.UserName) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() == true) _ = ShowStatusAsync("Đã đổi tài khoản mở app");
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
