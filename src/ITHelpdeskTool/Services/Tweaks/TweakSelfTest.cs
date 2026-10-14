using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ITHelpdeskTool.Services.SysConfig;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services.Tweaks;

/// <summary>Bật/tắt thật từng tweak rồi đọc lại, cuối cùng trả về trạng thái ban đầu. --selftest tweaks</summary>
public static class TweakSelfTest
{
    public static async Task<List<Step>> RunAsync()
    {
        var steps = new List<Step>();
        async Task Do(string name, Func<Task<string>> body)
        {
            var sw = Stopwatch.StartNew();
            try { steps.Add(new Step(name, true, await body(), sw.ElapsedMilliseconds)); }
            catch (Exception ex) { steps.Add(new Step(name, false, ex.Message, sw.ElapsedMilliseconds)); }
        }
        static void Check(bool cond, string msg) { if (!cond) throw new InvalidOperationException(msg); }
        static string Need(OpResult r) => r.Ok ? r.Message : throw new InvalidOperationException(r.Message);

        var original = TweakService.ReadAll();
        await Do("info", () => Task.FromResult(
            $"win11={TweakService.IsWin11} · {TweakCatalog.All.Count} tweak · không dùng được: " +
            string.Join(", ", TweakCatalog.All.Where(t => !TweakService.IsAvailable(t)).Select(t => t.Key)) +
            " · đang bật: " + string.Join(", ", original.Where(x => x.Value).Select(x => x.Key))));

        foreach (var t in TweakCatalog.All.Where(TweakService.IsAvailable))
        {
            await Do(t.Key, async () =>
            {
                var orig = TweakService.IsOn(t);
                Need(await TweakService.ApplyAsync(t, !orig));
                var mid = TweakService.IsOn(t);
                Need(await TweakService.ApplyAsync(t, orig));
                var end = TweakService.IsOn(t);
                Check(mid == !orig && end == orig, $"{orig} → {mid} → {end}");
                return $"{orig} → {mid} → {end}";
            });
        }

        await Do("recommended.then.reset", async () =>
        {
            var rec = TweakCatalog.All.Where(t => t.Recommended && TweakService.IsAvailable(t)).ToList();
            Need(await TweakService.ApplyManyAsync(rec.Select(t => (t, true))));
            Check(rec.All(TweakService.IsOn), "còn mục khuyên dùng chưa bật");
            Need(await TweakService.ApplyManyAsync(rec.Select(t => (t, original[t.Key]))));
            var bad = rec.Where(t => TweakService.IsOn(t) != original[t.Key]).Select(t => t.Key).ToList();
            Check(bad.Count == 0, "không trả lại được: " + string.Join(", ", bad));
            return $"{rec.Count} mục khuyên dùng: bật → trả lại như cũ";
        });

        await Do("explorer.restart", async () =>
        {
            var m = Need(await TweakService.RestartExplorerAsync());
            await Task.Delay(2000);
            Check(Process.GetProcessesByName("explorer").Length > 0, "Explorer không chạy lại");
            return m;
        });

        await Do("final.state", () =>
        {
            var now = TweakService.ReadAll();
            var diff = now.Where(x => x.Value != original[x.Key]).Select(x => x.Key).ToList();
            Check(diff.Count == 0, "khác trạng thái ban đầu: " + string.Join(", ", diff));
            return Task.FromResult("giống hệt lúc đầu");
        });
        return steps;
    }

    public static async Task<int> RunToFileAsync(string path)
    {
        var steps = await RunAsync();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(steps, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        }));
        return steps.All(s => s.Ok) ? 0 : 2;
    }
}
