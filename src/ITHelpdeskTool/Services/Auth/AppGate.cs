using System.Windows;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.Services.Auth;

/// <summary>Cổng đăng nhập trước khi mở cửa sổ chính.</summary>
public static class AppGate
{
    public static bool Pass(string[] args)
    {
        // Máy kiểm thử tự động (GitHub Actions) chụp màn hình không có người nhập mật khẩu
        if (args.Any(a => a.Equals("--no-login", StringComparison.OrdinalIgnoreCase))
            && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            return true;

        var app = Application.Current;
        var mode = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // đóng cửa sổ đăng nhập không làm tắt app trước khi kịp mở cửa sổ chính
        try
        {
            LoginWindow w = LocalAuth.HasAccount
                ? new LoginWindow(LoginWindow.Kind.Login,
                    async (u, p) => await Task.Run(() => LocalAuth.Verify(u, p)) ? null : "Sai tên đăng nhập hoặc mật khẩu.",
                    user: LocalAuth.UserName)
                : new LoginWindow(LoginWindow.Kind.Setup);
            var ok = w.ShowDialog() == true;
            Log.Info(ok ? $"Đăng nhập app: {w.User}" : "Thoát ở màn hình đăng nhập");
            return ok;
        }
        finally { app.ShutdownMode = mode; }
    }
}
