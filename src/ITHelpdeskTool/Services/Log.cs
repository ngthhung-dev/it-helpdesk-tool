using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ITHelpdeskTool.Services;

/// <summary>
/// Ghi log ra %LocalAppData%\ITHelpdeskTool\logs\app.log (ghi nền, không chặn UI).
/// Giữ tối đa ~1 MB, quá thì đổi sang app.old.log.
/// </summary>
public static class Log
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITHelpdeskTool", "logs");

    private static readonly string FilePath = Path.Combine(Folder, "app.log");
    private static readonly BlockingCollection<string> Queue = new(1000);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Việc UI đang làm gần nhất — ghi kèm khi phát hiện treo.</summary>
    public static volatile string Activity = "";

    static Log()
    {
        var t = new Thread(Writer) { IsBackground = true, Name = "LogWriter", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex is null ? msg : $"{msg}: {ex}");

    /// <summary>Mốc thời gian tính từ lúc app khởi động.</summary>
    public static void Mark(string what) => Info($"[+{Clock.ElapsedMilliseconds} ms] {what}");

    private static void Write(string level, string msg) =>
        Queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}");

    private static void Writer()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 1_000_000)
                File.Move(FilePath, Path.Combine(Folder, "app.old.log"), overwrite: true);
        }
        catch { /* không ghi được log thì thôi */ }

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }
}

/// <summary>
/// Canh luồng giao diện: cứ 250ms gửi một việc rỗng vào Dispatcher;
/// nếu phải chờ quá 300ms mới chạy được thì ghi log "UI bị chặn".
/// </summary>
public static class UiWatchdog
{
    public static void Start(Dispatcher ui)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                var sent = Stopwatch.GetTimestamp();
                var activity = Log.Activity;
                var op = ui.BeginInvoke(DispatcherPriority.Input, () => { });
                op.Wait(TimeSpan.FromSeconds(30));
                var ms = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                if (ms > 300)
                    Log.Warn($"UI bị chặn ~{ms:0} ms (đang làm: {(activity.Length > 0 ? activity : Log.Activity)})");
                if (ui.HasShutdownStarted) return;
                Thread.Sleep(250);
            }
        }) { IsBackground = true, Name = "UiWatchdog", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }
}

/// <summary>Trạng thái cửa sổ, để các trang tạm dừng cập nhật khi app bị thu nhỏ.</summary>
public static class AppState
{
    public static volatile bool IsMinimized;
}
