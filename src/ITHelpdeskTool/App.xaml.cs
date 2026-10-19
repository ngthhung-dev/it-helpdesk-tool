using ITHelpdeskTool.Core;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Report;

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
        base.OnStartup(e);

        // Giao diện Sáng / Tối / Theo hệ thống (--theme light|dark để chụp ảnh kiểm thử)
        var thi = Array.FindIndex(e.Args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        ThemeService.Init(thi >= 0 && thi + 1 < e.Args.Length ? e.Args[thi + 1] : null);

        // Chế độ dòng lệnh: ITHelpdeskTool.exe --report <đường dẫn[.html|.txt|.json]>
        var i = Array.FindIndex(e.Args, a => a.Equals("--report", StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var path = i + 1 < e.Args.Length ? e.Args[i + 1] : ReportExporter.SuggestedFileName(DateTime.Now);
            _ = RunReportCliAsync(path);
            return;
        }

        // Tự kiểm tra trên máy test: ITHelpdeskTool.exe --selftest sysconfig <kết quả.json>
        var ti = Array.FindIndex(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0 && ti + 2 < e.Args.Length && e.Args[ti + 1] is "sysconfig" or "policy" or "startup" or "power" or "tweaks" or "cleanup" or "uninstall" or "store" or "fonts" or "office" or "windows" or "storage" or "utilities" or "ui")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var which = e.Args[ti + 1];
            var outPath = e.Args[ti + 2];
            _ = Task.Run(async () =>
            {
                int code;
                try
                {
                    code = which switch
                    {
                        "policy" => await Services.Policy.PolicySelfTest.RunToFileAsync(outPath),
                        "startup" => await Services.Startup.StartupSelfTest.RunToFileAsync(outPath),
                        "power" => await Services.Power.PowerSelfTest.RunToFileAsync(outPath),
                        "tweaks" => await Services.Tweaks.TweakSelfTest.RunToFileAsync(outPath),
                        "cleanup" => await Services.Cleanup.CleanupSelfTest.RunToFileAsync(outPath),
                        "uninstall" => await Services.Uninstall.UninstallSelfTest.RunToFileAsync(outPath),
                        "store" => await Services.Store.StoreSelfTest.RunToFileAsync(outPath),
                        "fonts" => await Services.Fonts.FontSelfTest.RunToFileAsync(outPath),
                        "office" => await Services.Office.OfficeSelfTest.RunToFileAsync(outPath),
                        "windows" => await Services.Windows.WindowsSelfTest.RunToFileAsync(outPath),
                        "storage" => await Services.Disk.StorageSelfTest.RunToFileAsync(outPath),
                        "utilities" => await Services.UtilitiesSelfTest.RunToFileAsync(outPath),
                        "ui" => await Services.UiSelfTest.RunToFileAsync(outPath),
                        _ => await Services.SysConfig.SysConfigSelfTest.RunToFileAsync(outPath),
                    };
                }
                catch (Exception ex) { Log.Error("Selftest lỗi", ex); code = 1; }
                await Dispatcher.InvokeAsync(() => Shutdown(code));
            });
            return;
        }

        var pi = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pi >= 0 && pi + 1 < e.Args.Length) ViewModels.ShellViewModel.StartPage = e.Args[pi + 1];

        if (!Services.Auth.AppGate.Pass(e.Args)) { Shutdown(); return; }
        new MainWindow().Show();
    }

    /// <summary>Quét và xuất báo cáo không mở giao diện (dùng cho script/triển khai hàng loạt).</summary>
    private async Task RunReportCliAsync(string path)
    {
        var code = 0;
        try
        {
            var sections = ReportCollector.CreateSections();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ReportCollector.RunAsync(sections);
            var issues = ReportCollector.FindIssues(sections);
            var meta = new ReportExporter.Meta(DateTime.Now, sw.Elapsed);

            var full = System.IO.Path.GetFullPath(path);
            var ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
            var stem = ext is ".html" or ".htm" or ".txt" or ".json" ? full[..^ext.Length] : full;
            if (ext is "" or ".html" or ".htm") ReportExporter.Save(stem + ".html", ReportExporter.ToHtml(sections, issues, meta));
            if (ext is "" or ".txt") ReportExporter.Save(stem + ".txt", ReportExporter.ToText(sections, issues, meta));
            if (ext is "" or ".json") ReportExporter.Save(stem + ".json", ReportExporter.ToJson(sections, issues, meta));
            Log.Info($"CLI: đã xuất báo cáo {stem}.* sau {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Error("CLI: xuất báo cáo lỗi", ex);
            code = 1;
        }
        Shutdown(code);
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
