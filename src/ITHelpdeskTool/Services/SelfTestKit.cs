using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services;

/// <summary>Khung chạy selftest: mỗi bước ghi thời gian, lỗi không làm dừng các bước sau.</summary>
public sealed class SelfTestKit
{
    public List<Step> Steps { get; } = [];

    public async Task Do(string name, Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        Log.Info($"selftest: {name} …");
        try { Steps.Add(new Step(name, true, await body(), sw.ElapsedMilliseconds)); }
        catch (Exception ex) { Steps.Add(new Step(name, false, ex.Message, sw.ElapsedMilliseconds)); }
        var last = Steps[^1];
        Log.Info($"selftest: {name} → {(last.Ok ? "OK" : "ERR")} {last.Detail}");
    }

    public static void Check(bool cond, string msg) { if (!cond) throw new InvalidOperationException(msg); }
    public static string Need(SysConfig.OpResult r) => r.Ok ? r.Message : throw new InvalidOperationException(r.Message);

    public async Task<int> ToFileAsync(string path)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Steps, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        }));
        return Steps.All(s => s.Ok) ? 0 : 2;
    }
}
