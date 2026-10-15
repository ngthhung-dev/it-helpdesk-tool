using System.IO;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>
/// Nhóm Windows: công cụ sửa lỗi, yêu cầu Windows 11, nguồn tải Microsoft, Windows ADK + WinPE,
/// bộ cài giả (DISM capture + oscdimg) → gắn ISO, đọc phiên bản, SHA-256, ghi ra USB ảo (VHDX). --selftest windows
/// </summary>
public static class WindowsSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();
        var work = Path.Combine(Path.GetTempPath(), "ithd-win");
        if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch { }
        Directory.CreateDirectory(work);
        var logs = new List<string>();
        void L(string s) { lock (logs) logs.Add(s); }
        string Tail(int n = 3) { lock (logs) return string.Join(" / ", logs.TakeLast(n)); }

        // ---------- Sửa lỗi ----------
        foreach (var key in new[] { "dns", "spooler", "wmi", "icons", "chkdsk" })
        {
            await t.Do($"repair.{key}", async () =>
            {
                var tool = RepairService.Tools.First(x => x.Key == key);
                var r = await tool.Run(L, _ => { }, CancellationToken.None);
                Check(r.Ok, r.Message + " · " + Tail());
                return r.Message;
            });
        }

        await t.Do("repair.wu", async () =>
        {
            var r = await RepairService.ResetWindowsUpdateAsync(L, CancellationToken.None);
            Check(r.Ok, r.Message);
            var q = await ProcessRunner.ConsoleAsync("sc.exe", "query wuauserv");
            Check(Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution")), "SoftwareDistribution chưa được tạo lại");
            return r.Message;
        });

        await t.Do("repair.sfc", async () =>
        {
            var pct = 0;
            var r = await RepairService.SfcAsync(L, p => pct = Math.Max(pct, p), CancellationToken.None);
            Check(r.Ok, r.Message + " · " + Tail());
            return $"{r.Message} · tiến độ cao nhất {pct}%";
        });

        await t.Do("repair.status", async () =>
        {
            var shut = await RepairService.UnexpectedShutdownsAsync();
            var crashes = RepairService.RecentCrashes();
            var dirty = await RepairService.IsDirtyAsync();
            return $"chờ khởi động lại: {RepairService.PendingReboot()} · tắt đột ngột 30 ngày: {shut} · BSOD: {crashes.Count} · dirty: {dirty}";
        });

        // ---------- Cài Windows ----------
        await t.Do("setup.readiness", async () =>
        {
            var list = await SetupService.Windows11ReadinessAsync();
            Check(list.Count == 6, $"{list.Count} mục");
            Check(SetupService.ExplainCompat(unchecked((int)0xC1900210)).Ok && !SetupService.ExplainCompat(unchecked((int)0xC1900204)).Ok, "giải mã compat");
            return string.Join(" | ", list.Select(i => $"{i.Name}: {i.Value} ({(i.Pass is null ? "?" : i.Pass.Value ? "đạt" : "chưa đạt")})")) +
                   $" · WinRE {await SetupService.WinReStatusAsync()}";
        });

        // ---------- Nguồn tải Microsoft ----------
        await t.Do("downloads.tools", async () =>
        {
            var res = new List<string>();
            foreach (var d in WindowsDownloads.Items.Where(x => x.Tool))
            {
                var p = await WindowsDownloads.DownloadToolAsync(d);
                res.Add($"{Path.GetFileName(p)} {Fmt.Size(new FileInfo(p).Length)}");
                File.Delete(p);
            }
            return "chữ ký Microsoft hợp lệ: " + string.Join(", ", res);
        });

        // ---------- Windows ADK + WinPE ----------
        await t.Do("adk.install", async () =>
        {
            var r = await WinPeService.InstallAdkAsync(L, CancellationToken.None);
            Check(r.Ok, r.Message + " · " + Tail());
            return $"{r.Message} {WinPeService.AdkVersion()}";
        });

        var peWork = Path.Combine(work, "pe");
        await t.Do("winpe.build", async () =>
        {
            var steps = new List<string>();
            var r = await WinPeService.BuildAsync(new PeOptions
            {
                WorkDir = peWork,
                Components = WinPeService.Components.Where(c => c.Key is "powershell" or "bitlocker").ToList(),
                StartCommands = "echo IT Helpdesk Tool",
            }, L, (s, p) => steps.Add(s), CancellationToken.None);
            Check(r.Ok, r.Message + " · " + Tail(5));
            return $"{r.Message} · {steps.Count} bước";
        });

        var peIso = Path.Combine(work, "winpe.iso");
        await t.Do("winpe.iso", async () =>
        {
            var r = await WinPeService.MakeIsoAsync(peWork, peIso, L, CancellationToken.None);
            Check(r.Ok, r.Message + " · " + Tail());
            var root = await ImageService.MountAsync(peIso);
            try
            {
                var boot = Path.Combine(root, @"sources\boot.wim");
                Check(File.Exists(boot), "ISO không có boot.wim");
                var eds = await ImageService.EditionsAsync(boot);
                Check(eds.Count >= 1 && eds[0].Arch == "x64", $"boot.wim: {eds.Count} · {eds.FirstOrDefault()?.Arch}");
                return $"{r.Message} · {eds[0].Name} {eds[0].Version} {eds[0].Arch}";
            }
            finally { await ImageService.DismountAsync(peIso); }
        });

        // ---------- Bộ cài giả: media WinPE + install.wim tự chụp ----------
        var media = Path.Combine(work, "media");
        var fakeIso = Path.Combine(work, "fake-setup.iso");
        await t.Do("media.fake", async () =>
        {
            CopyDir(Path.Combine(peWork, "media"), media);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(media, "setup.exe"), true);
            var wim = Path.Combine(media, @"sources\install.wim");
            var r = await ProcessRunner.RunAsync("dism.exe", $"/English /Capture-Image /ImageFile:\"{wim}\" /CaptureDir:\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts")}\" /Name:\"Windows 11 Pro\" /Compress:none", encoding: ProcessRunner.Oem);
            Check(r.Success, "capture: " + Err(r));
            r = await ProcessRunner.RunAsync("dism.exe", $"/English /Append-Image /ImageFile:\"{wim}\" /CaptureDir:\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Help")}\" /Name:\"Windows 11 Home\"", encoding: ProcessRunner.Oem);
            Check(r.Success, "append: " + Err(r));
            var oscdimg = Path.Combine(WinPeService.AdkDir!, @"Deployment Tools\amd64\Oscdimg\oscdimg.exe");
            r = await ProcessRunner.RunAsync(oscdimg, $"-m -o -u2 -udfver102 \"{media}\" \"{fakeIso}\"", encoding: ProcessRunner.Oem);
            Check(File.Exists(fakeIso), "oscdimg: " + Err(r));
            return $"install.wim {Fmt.Size(new FileInfo(wim).Length)} · ISO {Fmt.Size(new FileInfo(fakeIso).Length)}";
        });

        await t.Do("iso.mount.read", async () =>
        {
            var root = await ImageService.MountAsync(fakeIso);
            try
            {
                var m = await ImageService.ReadMediaAsync(root);
                Check(m.Editions.Count == 2 && m.Editions[0].Name == "Windows 11 Pro" && m.Editions[1].Name == "Windows 11 Home", string.Join(", ", m.Editions.Select(e => e.Name)));
                var again = await ImageService.MountAsync(fakeIso);
                Check(again == root, "gắn lần 2 phải trả cùng ổ");
                return $"{root} · {m.Summary}";
            }
            finally { await ImageService.DismountAsync(fakeIso); }
        });

        await t.Do("iso.sha256", async () =>
        {
            var last = 0;
            var h = await ImageService.Sha256Async(fakeIso, new Progress<int>(p => last = p));
            var r = await ProcessRunner.PowerShellAsync($"(Get-FileHash -Algorithm SHA256 -LiteralPath {ProcessRunner.PsQuote(fakeIso)}).Hash");
            Check(h == r.Output.Trim(), $"{h} ≠ {r.Output.Trim()}");
            Check(ImageService.NormalizeHash(" " + h.ToLower().Insert(8, " ") + "\n") == h, "chuẩn hoá mã băm");
            return h;
        });

        // ---------- USB ảo ----------
        var vhd = Path.Combine(work, "usb.vhdx");
        await t.Do("usb.write.vhd", async () =>
        {
            UsbService.AllowVirtual = true;
            UsbService.SplitOver = 1024 * 1024;   // ép chia install.wim để thử bước Split-Image
            var dp = Path.Combine(work, "dp.txt");
            await File.WriteAllTextAsync(dp, $"create vdisk file=\"{vhd}\" maximum=2048 type=expandable\r\nattach vdisk\r\n");
            var a = await ProcessRunner.ConsoleAsync("diskpart.exe", $"/s \"{dp}\"");
            Check(a.Success, "diskpart: " + a.Text);
            await Task.Delay(2000);
            var disks = await UsbService.ListAsync();
            var disk = disks.FirstOrDefault(d => d.BusType.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && d.Size <= 2L * 1024 * 1024 * 1024 + 1)
                       ?? throw new InvalidOperationException("không thấy ổ ảo: " + string.Join(", ", disks.Select(d => d.Display)));
            var root = await ImageService.MountAsync(fakeIso);
            try
            {
                var lines = new List<string>();
                var r = await UsbService.WriteAsync(disk.Number, root, gpt: false, "ITHD TEST", lines.Add, _ => { }, CancellationToken.None, splitMb: 64);
                Check(r.Ok, r.Message + " · " + string.Join(" / ", lines.TakeLast(4)));
                var fresh = (await UsbService.ListAsync()).First(d => d.Number == disk.Number);
                var letter = fresh.Letters.Split(' ')[0] + "\\";
                var swm = Directory.GetFiles(Path.Combine(letter, "sources"), "install*.swm");
                Check(swm.Length >= 2, $"chỉ có {swm.Length} file .swm");
                Check(!File.Exists(Path.Combine(letter, @"sources\install.wim")), "không được chép install.wim lớn");
                Check(File.Exists(Path.Combine(letter, @"efi\boot\bootx64.efi")), "thiếu efi\\boot\\bootx64.efi");
                var info = await ProcessRunner.PowerShellAsync($"$v = Get-Volume -DriveLetter {letter[0]}; \"$($v.FileSystem)|$($v.FileSystemLabel)\"; (Get-Disk -Number {disk.Number}).PartitionStyle");
                Check(info.Output.Contains("FAT32|ITHD TEST") && info.Output.Contains("MBR"), info.Output.Trim());
                var m = await ImageService.ReadMediaAsync(letter);
                Check(m.IsSplit && m.Editions.Count == 2, "đọc lại USB: " + m.Summary);
                return $"{r.Message} · {swm.Length} file .swm · {info.Output.Trim().Replace('\n', ' ')}";
            }
            finally
            {
                await ImageService.DismountAsync(fakeIso);
                await File.WriteAllTextAsync(dp, $"select vdisk file=\"{vhd}\"\r\ndetach vdisk\r\n");
                await ProcessRunner.ConsoleAsync("diskpart.exe", $"/s \"{dp}\"");
            }
        });

        await t.Do("usb.guard", async () =>
        {
            UsbService.AllowVirtual = false;
            var all = await UsbService.ListAsync();
            Check(all.All(d => !d.IsBoot && !d.IsSystem), "danh sách có ổ hệ thống");
            Check(!UsbService.IsAllowed(new UsbDisk(0, "Ổ hệ thống", 1L << 40, "NVMe", "GPT", true, true, "C:")), "phải chặn ổ hệ thống");
            Check(!UsbService.IsAllowed(new UsbDisk(1, "Ổ phụ", 1L << 40, "SATA", "GPT", false, false, "D:")), "phải chặn ổ SATA");
            Check(UsbService.IsAllowed(new UsbDisk(2, "USB", 16L << 30, "USB", "MBR", false, false, "E:")), "USB phải được phép");
            return $"{all.Count} USB thật trên máy test";
        });

        try { Directory.Delete(work, true); } catch { }
        return await t.ToFileAsync(path);

        static string Err(ProcessResult r) => SysConfig.OpResult.FirstLine(r.Text);
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }
}
