using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ITHelpdeskTool.Services.SysConfig;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services.Power;

/// <summary>
/// Chạy thật các thao tác gói nguồn rồi đọc lại (máy test/CI).
/// ITHelpdeskTool.exe --selftest power kết_quả.json
/// </summary>
public static class PowerSelfTest
{
    public static async Task<List<Step>> RunAsync()
    {
        var steps = new List<Step>();
        async Task Do(string name, Func<Task<string>> body)
        {
            var sw = Stopwatch.StartNew();
            Log.Info($"selftest: {name} …");
            try { steps.Add(new Step(name, true, await body(), sw.ElapsedMilliseconds)); }
            catch (Exception ex) { steps.Add(new Step(name, false, ex.Message, sw.ElapsedMilliseconds)); }
        }
        static void Check(bool cond, string msg) { if (!cond) throw new InvalidOperationException(msg); }
        static string Need(OpResult r) => r.Ok ? r.Message : throw new InvalidOperationException(r.Message);
        static async Task<PowerPlan> Active() => (await PowerService.ListAsync()).First(p => p.IsActive);

        PowerPlan? original = null;
        string? ultimate = null, high = null, imported = null;

        await Do("list", async () =>
        {
            var list = await PowerService.ListAsync();
            original = list.FirstOrDefault(p => p.IsActive);
            Check(original is not null, "không thấy gói đang dùng");
            return string.Join(" | ", list.Select(p => $"{p.Name}{(p.IsActive ? " *" : "")} {p.Guid}"));
        });

        await Do("read.settings", async () =>
        {
            var v = await PowerService.ReadSettingsAsync(original!.Guid);
            Check(v.ContainsKey("display") && v.ContainsKey("sleep") && v.ContainsKey("cpumax"), $"thiếu thiết lập, chỉ có: {string.Join(",", v.Keys)}");
            return string.Join(" | ", v.Select(x => $"{x.Key}: AC={x.Value.Ac} DC={x.Value.Dc}"));
        });

        await Do("add.ultimate", async () =>
        {
            var (r, g) = await PowerService.AddBuiltInAsync(PowerService.Ultimate);
            Need(r);
            ultimate = g;
            Check((await PowerService.ListAsync()).Any(p => p.Guid == g), "gói mới không có trong danh sách");
            return $"{r.Message} {g}";
        });
        await Do("add.high", async () =>
        {
            var (r, g) = await PowerService.AddBuiltInAsync(PowerService.HighPerf);
            high = g;
            return $"{Need(r)} {g}";
        });

        await Do("activate", async () =>
        {
            var p = (await PowerService.ListAsync()).First(x => x.Guid == ultimate);
            Need(await PowerService.SetActiveAsync(p));
            Check((await Active()).Guid == ultimate, "chưa chuyển sang gói mới");
            return $"đang dùng: {(await Active()).Name}";
        });

        await Do("rename", async () =>
        {
            var p = (await PowerService.ListAsync()).First(x => x.Guid == ultimate);
            Need(await PowerService.RenameAsync(p, "HD Test Ultimate"));
            Check((await PowerService.ListAsync()).First(x => x.Guid == ultimate).Name == "HD Test Ultimate", "tên chưa đổi");
            return "HD Test Ultimate";
        });

        // Ghi từng thiết lập (AC và DC) rồi đọc lại
        await Do("settings.roundtrip", async () =>
        {
            var p = await Active();
            var before = await PowerService.ReadSettingsAsync(p.Guid);
            var test = new Dictionary<string, (int, int)>
            {
                ["display"] = (420, 240), ["sleep"] = (0, 900), ["hibernate"] = (7200, 3600), ["button"] = (4, 1),
                ["cpumax"] = (99, 80), ["cpumin"] = (5, 5), ["disk"] = (0, 600), ["usb"] = (0, 1), ["pcie"] = (0, 2), ["lid"] = (0, 1),
            };
            var changes = PowerService.Settings.Where(d => before.ContainsKey(d.Key) && test.ContainsKey(d.Key))
                .Select(d => (d, (int?)test[d.Key].Item1, (int?)test[d.Key].Item2)).ToList();
            Need(await PowerService.ApplySettingsAsync(p, changes));
            var after = await PowerService.ReadSettingsAsync(p.Guid);
            var bad = changes.Where(c => after[c.d.Key].Ac != c.Item2 || after[c.d.Key].Dc != c.Item3)
                .Select(c => $"{c.d.Key}: muốn {c.Item2}/{c.Item3} đọc {after[c.d.Key].Ac}/{after[c.d.Key].Dc}").ToList();
            Check(bad.Count == 0, string.Join("; ", bad));
            return $"{changes.Count} thiết lập ghi/đọc khớp";
        });

        await Do("presets", async () =>
        {
            var p = await Active();
            foreach (var name in new[] { "office", "alwayson", "performance", "cool" })
            {
                var pre = PowerService.Preset(name);
                var cur = await PowerService.ReadSettingsAsync(p.Guid);
                var changes = PowerService.Settings.Where(d => pre.ContainsKey(d.Key) && cur.ContainsKey(d.Key))
                    .Select(d => (d, (int?)pre[d.Key].Ac, (int?)pre[d.Key].Dc)).ToList();
                Need(await PowerService.ApplySettingsAsync(p, changes));
                var after = await PowerService.ReadSettingsAsync(p.Guid);
                Check(changes.All(c => after[c.d.Key].Ac == c.Item2 && after[c.d.Key].Dc == c.Item3), $"mẫu {name} lệch");
            }
            return "4 mẫu áp dụng khớp";
        });

        await Do("export.import", async () =>
        {
            var p = (await PowerService.ListAsync()).First(x => x.Guid == ultimate);
            var file = Path.Combine(Path.GetTempPath(), $"hdtest_{Guid.NewGuid():N}.pow");
            Need(await PowerService.ExportAsync(p, file));
            var (r, g) = await PowerService.ImportAsync(file);
            Need(r);
            imported = g;
            var list = await PowerService.ListAsync();
            Check(list.Any(x => x.Guid == g), "gói nhập không có trong danh sách");
            File.Delete(file);
            return $"xuất {new FileInfo(file).Name} → nhập {g}";
        });

        await Do("delete.guards", async () =>
        {
            var act = await Active();
            var r1 = await PowerService.DeleteAsync(act);
            var bal = (await PowerService.ListAsync()).FirstOrDefault(x => x.IsBalanced) ?? new PowerPlan(PowerService.Balanced, "Balanced", false);
            var r2 = await PowerService.DeleteAsync(bal with { IsActive = false });
            Check(!r1.Ok && !r2.Ok, "phải chặn xoá gói đang dùng và gói Balanced");
            return $"{r1.Message} | {r2.Message}";
        });

        await Do("restore.original", async () =>
        {
            Need(await PowerService.SetActiveAsync(original!));
            Check((await Active()).Guid == original!.Guid, "chưa về gói ban đầu");
            return original!.Name;
        });

        await Do("delete", async () =>
        {
            var list = await PowerService.ListAsync();
            var n = 0;
            foreach (var g in new[] { ultimate, high, imported }.Where(g => g is not null))
            {
                var p = list.FirstOrDefault(x => x.Guid == g);
                if (p is null) continue;
                Need(await PowerService.DeleteAsync(p));
                n++;
            }
            var left = (await PowerService.ListAsync()).Where(x => x.Guid == ultimate || x.Guid == high || x.Guid == imported).ToList();
            Check(left.Count == 0, "còn sót gói test");
            return $"đã xoá {n} gói test";
        });

        await Do("wake.devices", async () =>
        {
            var d = await PowerService.ReadWakeDevicesAsync();
            return d.Count == 0 ? "không có thiết bị (bình thường trên máy ảo)" : string.Join(" | ", d.Select(x => $"{x.Name} armed={x.Armed}"));
        });
        await Do("diag.text", async () =>
        {
            var req = await PowerService.RequestsAsync();
            var wake = await PowerService.LastWakeAsync();
            var st = await PowerService.SleepStatesAsync();
            Check(req.Length > 0 && st.Length > 0, "output rỗng");
            return $"requests {req.Length} ký tự · lastwake {wake.Length} · states {st.Length}";
        });
        await Do("diag.battery", async () =>
        {
            var (r, f) = await PowerService.BatteryReportAsync();
            return r.Ok ? $"có pin: {f}" : $"không có pin (đúng với máy ảo): {r.Message}";
        });
        await Do("diag.energy", async () =>
        {
            var (r, f) = await PowerService.EnergyReportAsync(5);
            return r.Ok ? $"{r.Message} {new FileInfo(f!).Length / 1024} KB" : $"không tạo được trên máy này: {r.Message}";
        });

        // Làm cuối cùng vì xoá mọi gói tự tạo trên máy
        await Do("restore.defaults", async () =>
        {
            Need(await PowerService.RestoreDefaultsAsync());
            var list = await PowerService.ListAsync();
            Check(list.Any(p => p.IsBalanced), "không còn gói Balanced");
            return string.Join(" | ", list.Select(p => $"{p.Name}{(p.IsActive ? " *" : "")}"));
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
