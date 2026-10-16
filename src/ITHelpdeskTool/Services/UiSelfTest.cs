using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ITHelpdeskTool.ViewModels;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services;

/// <summary>
/// Đo độ mượt giao diện: mở cửa sổ chính, lần lượt vào từng trang, đo thời gian chuyển trang và
/// thời gian luồng giao diện bị chặn trong 4 giây đầu trang tải dữ liệu. --selftest ui
/// </summary>
public static class UiSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();
        var d = Application.Current.Dispatcher;
        MainWindow? win = null;
        ShellViewModel? shell = null;
        await d.InvokeAsync(() =>
        {
            win = new MainWindow { Width = 1280, Height = 860 };
            win.Show();
            shell = (ShellViewModel)win.DataContext;
        });
        await Task.Delay(3000);

        var items = await d.InvokeAsync(() => shell!.Groups.SelectMany(g => g.Items).ToList());
        var slow = new List<string>();
        foreach (var item in items)
        {
            await t.Do($"page.{item.Key}", async () =>
            {
                // Thời gian chuyển trang (tạo trang + dựng giao diện), chạy trên luồng giao diện
                var nav = await d.InvokeAsync(() =>
                {
                    var sw = Stopwatch.StartNew();
                    shell!.NavigateCommand.Execute(item);
                    win!.UpdateLayout();
                    return sw.ElapsedMilliseconds;
                });
                // Trong 4 giây sau đó, gửi "ping" mỗi 40 ms — ping trễ nghĩa là luồng giao diện đang bị chặn
                long worst = 0;
                var end = Stopwatch.StartNew();
                while (end.ElapsedMilliseconds < 4000)
                {
                    var ping = Stopwatch.StartNew();
                    await d.InvokeAsync(() => { }, DispatcherPriority.Input);
                    worst = Math.Max(worst, ping.ElapsedMilliseconds);
                    await Task.Delay(40);
                }
                if (nav > 1500 || worst > 700) slow.Add($"{item.Title} ({nav}/{worst} ms)");
                Check(worst < 3000, $"luồng giao diện bị chặn {worst} ms");
                return $"{item.Title}: chuyển trang {nav} ms · chặn lâu nhất {worst} ms";
            });
        }
        await t.Do("summary", async () =>
        {
            await Task.CompletedTask;
            return slow.Count == 0 ? "Mọi trang đều mượt (chuyển trang < 1,5 s, không chặn > 0,7 s)" : "Chậm: " + string.Join(", ", slow);
        });
        await d.InvokeAsync(() => win!.Close());
        return await t.ToFileAsync(path);
    }
}
