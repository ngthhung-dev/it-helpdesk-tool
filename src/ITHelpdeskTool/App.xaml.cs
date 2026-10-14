using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using ITHelpdeskTool.Services;

namespace ITHelpdeskTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Lần sau mở app nhanh hơn: .NET ghi lại các hàm đã JIT và nạp sẵn ở lần sau
        try
        {
            ProfileOptimization.SetProfileRoot(Log.Folder);
            ProfileOptimization.StartProfile("startup.profile");
        }
        catch { }

        Log.Mark($"Khởi động {AppInfo.Name} {AppInfo.Version} · {Environment.OSVersion} · admin={AdminHelper.IsElevated()}");
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Task lỗi", ev.Exception); ev.SetObserved(); };
        UiWatchdog.Start(Dispatcher);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Thoát app");
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Lỗi không bắt được", e.Exception);
        MessageBox.Show(e.Exception.Message, $"{AppInfo.Name} – lỗi không mong muốn",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
