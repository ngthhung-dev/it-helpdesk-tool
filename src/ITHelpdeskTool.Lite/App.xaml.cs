using System.Net;
using System.Windows;
using System.Windows.Threading;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;

namespace ITHelpdeskTool;

/// <summary>Bản nhẹ Windows 7: đăng nhập như app chính rồi mở thẳng trang "Cài lại sạch".</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Windows 7 / .NET 4.8: bật TLS 1.2 cho đăng nhập online và tải bộ cài từ Microsoft
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        Log.Mark($"Khởi động {AppInfo.Name} Lite {AppInfo.Version} · {Environment.OSVersion} · admin={AdminHelper.IsElevated()}");
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Task lỗi", ev.Exception); ev.SetObserved(); };
        base.OnStartup(e);

        var thi = Array.FindIndex(e.Args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        ThemeService.Init(thi >= 0 && thi + 1 < e.Args.Length ? e.Args[thi + 1] : null);

        var pi = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pi >= 0 && pi + 1 < e.Args.Length) ViewModels.ShellViewModel.StartPage = e.Args[pi + 1];

        if (!Services.Auth.AppGate.Pass(e.Args)) { Shutdown(); return; }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Thoát app");
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Lỗi không bắt được", e.Exception);
        MessageBox.Show(e.Exception.Message, $"{AppInfo.Name} – lỗi không mong muốn", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
