using System.Diagnostics;
using System.IO;
using ITHelpdeskTool.Services.Outlook;
using ITHelpdeskTool.Services.Preview;
using ITHelpdeskTool.Services.Printing;
using ITHelpdeskTool.Services.Zalo;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services;

/// <summary>Nhóm Tiện ích: máy in, dữ liệu Outlook, xem trước / kiểm tra file, Multi-Zalo. --selftest utilities</summary>
public static class UtilitiesSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();
        var work = Path.Combine(Path.GetTempPath(), "ithd-util");
        if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch { }
        Directory.CreateDirectory(work);

        // ---------- Máy in ----------
        string? driver = null;
        await t.Do("printer.list", async () =>
        {
            var list = await PrinterService.ListAsync();
            var drivers = await PrinterService.DriversAsync();
            driver = new[] { "Generic / Text Only", "Microsoft PS Class Driver", "Microsoft Print To PDF" }.FirstOrDefault(drivers.Contains) ?? drivers.FirstOrDefault();
            Check(driver is not null, "không có driver nào");
            return $"{list.Count} máy in: {string.Join(", ", list.Select(p => $"{p.Name} ({p.StatusText})"))} · {drivers.Count} driver · dùng {driver}";
        });

        await t.Do("printer.fix.parse", async () =>
        {
            var fixes = PrinterFixCatalog.All.Where(f => f.Script.Length > 0).ToList();
            Check(PrinterFixCatalog.All.Select(f => f.Id).Distinct().Count() == PrinterFixCatalog.All.Count, "trùng Id");
            Check(PrinterFixCatalog.All.All(f => PrinterFixCatalog.GroupOrder.Contains(f.Group)), "có nhóm lạ");
            var dir = Path.Combine(work, "fixps");
            Directory.CreateDirectory(dir);
            foreach (var f in fixes) File.WriteAllText(Path.Combine(dir, f.Id + ".ps1"), PrinterFixCatalog.Prelude + f.Script, new System.Text.UTF8Encoding(true));
            var r = await ProcessRunner.PowerShellAsync(
                "Get-ChildItem " + ProcessRunner.PsQuote(dir) + " -Filter *.ps1 | ForEach-Object { $err = $null\n" +
                "  [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$err)\n" +
                "  foreach ($e in $err) { \"$($_.BaseName): dòng $($e.Extent.StartLineNumber) $($e.Message)\" } }");
            Check(r.Success && r.Output.Trim().Length == 0, "lỗi cú pháp: " + r.Output.Trim() + r.Error);
            return $"{fixes.Count} script PowerShell đúng cú pháp · {PrinterFixCatalog.All.Count} thao tác / {PrinterFixCatalog.GroupOrder.Count} nhóm";
        });

        await t.Do("printer.fix.run", async () =>
        {
            string[] ids = ["diag.printers", "diag.ports", "diag.drivers", "diag.events", "sp.queue", "svc.spooler", "svc.printnotify", "sp.mscms", "code.709",
                            "net.dns", "net.fps", "net.discovery", "net.9100", "rpc.client", "rpc.restore", "pnp.open", "pnp.lock", "smb.guest.on", "smb.guest.off",
                            "code.6ba", "code.6d9", "vendor.hp", "feat.printing-foundation-lprportmonitor", "all.services"];
            var outs = new List<string>();
            foreach (var id in ids)
            {
                var f = PrinterFixCatalog.All.First(x => x.Id == id);
                var r = await PrinterFixCatalog.RunAsync(f);
                Check(r.Ok, $"{id}: {r.Message}");
                var m = r.Message.Replace('\n', ' ');
                outs.Add($"{id}: {m[..Math.Min(170, m.Length)]}");
            }
            await ProcessRunner.PowerShellAsync("Get-NetFirewallRule -DisplayName 'IT Helpdesk Tool - *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
            var pnp = await ProcessRunner.PowerShellAsync(@"(Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint' -ErrorAction SilentlyContinue).RestrictDriverInstallationToAdministrators");
            Check(pnp.Output.Trim() == "1", "Point and Print chưa khoá lại");
            var sp = await ProcessRunner.PowerShellAsync("(Get-Service Spooler).Status");
            Check(sp.Output.Trim() == "Running", "Spooler không chạy sau khi sửa");
            return string.Join(" | ", outs);
        });

        await t.Do("printer.add.manage", async () =>
        {
            var (online, detail) = await PrinterService.ProbeAsync("127.0.0.1");
            Check(!online, "127.0.0.1 không phải máy in");
            Need(await PrinterService.AddByIpAsync("127.0.0.1", "ITHD Test", driver!));
            PrinterInfo P(string n) => PrinterService.ListAsync().GetAwaiter().GetResult().First(p => p.Name == n);
            var p = await Task.Run(() => P("ITHD Test"));
            Check(p.Port == "IP_127.0.0.1" && p.Driver == driver, p.Detail);
            Need(await PrinterService.SetDefaultAsync(p));
            p = await Task.Run(() => P("ITHD Test"));
            Check(p.IsDefault, "chưa là mặc định");
            Need(await PrinterService.RenameAsync(p, "ITHD Test 2"));
            p = await Task.Run(() => P("ITHD Test 2"));
            var share = await PrinterService.ShareAsync(p, true, "ITHDTest");
            var shared = share.Ok && (await Task.Run(() => P("ITHD Test 2"))).Shared;
            if (share.Ok) Need(await PrinterService.ShareAsync(p, false));
            var tp = await PrinterService.TestPageAsync(p);
            await Task.Delay(1500);
            var jobs = await PrinterService.JobsAsync(p.Name);
            Need(await PrinterService.ClearJobsAsync(p));
            await Task.Delay(1000);
            var after = await PrinterService.JobsAsync(p.Name);
            Need(await PrinterService.RemoveAsync(p));
            var gone = (await PrinterService.ListAsync()).All(x => x.Name != "ITHD Test 2");
            Check(gone, "chưa gỡ máy in");
            var portLeft = (await ProcessRunner.PowerShellAsync("@(Get-PrinterPort -Name 'IP_127.0.0.1' -ErrorAction SilentlyContinue).Count")).Output.Trim();

            return $"thêm / mặc định / đổi tên ok · chia sẻ: {(shared ? "ok" : share.Message)} · in thử: {tp.Message} · lệnh in {jobs.Count} → {after.Count} · gỡ ok · cổng còn lại: {portLeft} · probe: {detail}";
        });

        await t.Do("printer.rpcfix", async () =>
        {
            var before = PrinterService.RpcPrivacyEnforced();
            Need(await PrinterService.SetRpcPrivacyAsync(false));
            Check(!PrinterService.RpcPrivacyEnforced(), "chưa áp dụng");
            Need(await PrinterService.SetRpcPrivacyAsync(true));
            Check(PrinterService.RpcPrivacyEnforced(), "chưa khôi phục");
            return $"ban đầu: {(before ? "mặc định" : "đã hạ")} → hạ → khôi phục ok";
        });

        // ---------- Outlook ----------
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Outlook");
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Outlook Files");
        await t.Do("outlook.find.backup", async () =>
        {
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(docs);
            await File.WriteAllBytesAsync(Path.Combine(local, "ithd@test.com.ost"), new byte[3 * 1024 * 1024]);
            await File.WriteAllBytesAsync(Path.Combine(docs, "Archive.pst"), new byte[2 * 1024 * 1024]);
            var files = OutlookService.FindDataFiles();
            Check(files.Any(f => f.Name == "Archive.pst" && !f.IsOst) && files.Any(f => f.Name == "ithd@test.com.ost" && f.IsOst), string.Join(", ", files.Select(f => f.Name)));
            var r = await OutlookService.BackupAsync(files.Where(f => f.Name is "Archive.pst" or "ithd@test.com.ost").ToList(), work, _ => { }, CancellationToken.None);
            Check(r.Ok, r.Message);
            var dest = Directory.GetDirectories(work, "Outlook-*").Single();
            Check(File.Exists(Path.Combine(dest, "Archive.pst")) && new FileInfo(Path.Combine(dest, "ithd@test.com.ost")).Length == 3 * 1024 * 1024, "thiếu file sao lưu");
            return $"{r.Message} · Outlook: {OutlookService.OutlookExe() ?? "chưa cài"} · ScanPST: {OutlookService.ScanPstExe() ?? "không có"}";
        });

        await t.Do("outlook.rebuild.ost", async () =>
        {
            var ost = OutlookService.FindDataFiles().First(f => f.Name == "ithd@test.com.ost");
            Need(await OutlookService.RebuildOstAsync(ost));
            Check(!File.Exists(ost.Path) && Directory.GetFiles(local, "ithd@test.com.ost.*.bak").Length == 1, "chưa đổi tên OST");
            Check(!(await OutlookService.RebuildOstAsync(OutlookService.FindDataFiles().First(f => f.Name == "Archive.pst"))).Ok, "PST không được dựng lại");
            foreach (var f in Directory.GetFiles(local, "ithd@test.com.ost*")) File.Delete(f);
            File.Delete(Path.Combine(docs, "Archive.pst"));
            return "đổi tên .bak ok · chặn PST ok";
        });

        await t.Do("outlook.signatures", async () =>
        {
            Directory.CreateDirectory(OutlookService.SignaturesDir);
            var sig = Path.Combine(OutlookService.SignaturesDir, "ITHD Test.htm");
            await File.WriteAllTextAsync(sig, "<p>Chữ ký thử</p>");
            var zip = Path.Combine(work, "sig.zip");
            Need(OutlookService.BackupSignatures(zip));
            File.Delete(sig);
            Need(OutlookService.RestoreSignatures(zip));
            Check(File.ReadAllText(sig).Contains("Chữ ký thử"), "khôi phục sai");
            File.Delete(sig);
            return "sao lưu / khôi phục ok";
        });

        // ---------- Xem trước ----------
        await t.Do("preview.settings", async () =>
        {
            var a = PreviewService.PreviewHandlersOn; var b = PreviewService.ThumbnailsOn; var c = PreviewService.NetworkThumbnailsOn;
            PreviewService.PreviewHandlersOn = !a; PreviewService.ThumbnailsOn = !b; PreviewService.NetworkThumbnailsOn = !c;
            Check(PreviewService.PreviewHandlersOn == !a && PreviewService.ThumbnailsOn == !b && PreviewService.NetworkThumbnailsOn == !c, "không đổi được");
            PreviewService.PreviewHandlersOn = a; PreviewService.ThumbnailsOn = b; PreviewService.NetworkThumbnailsOn = c;
            var s = PreviewService.Supports();
            await Task.CompletedTask;
            return string.Join(" | ", s.Select(x => $"{x.Ext}: {(x.Ok ? x.Handler : "—")}"));
        });

        await t.Do("preview.inspect", async () =>
        {
            var fake = Path.Combine(work, "hoadon.pdf.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), fake);
            var r1 = await PreviewService.InspectAsync(fake);
            Check(r1.ExtMismatch && r1.RealType.Contains("GIẢ ĐUÔI"), r1.RealType);
            var renamed = Path.Combine(work, "baocao.pdf");
            File.Copy(fake, renamed);
            var r2 = await PreviewService.InspectAsync(renamed);
            Check(r2.ExtMismatch && r2.RealType.StartsWith("Chương trình"), "exe đổi đuôi .pdf phải bị phát hiện");
            var txt = Path.Combine(work, "ghichu.txt");
            await File.WriteAllTextAsync(txt, "Xin chào\nDòng 2");
            await File.WriteAllTextAsync(txt + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/ghichu.txt\r\n");
            var r3 = await PreviewService.InspectAsync(txt);
            Check(r3.FromInternet && r3.HostUrl == "https://example.com/ghichu.txt" && r3.Text!.Contains("Dòng 2") && !r3.ExtMismatch, $"{r3.ZoneText} · {r3.Text}");
            Need(PreviewService.Unblock(txt));
            Check(!(await PreviewService.InspectAsync(txt)).FromInternet, "chưa bỏ chặn");
            await File.WriteAllTextAsync(txt + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            var u = await PreviewService.UnblockFolderAsync(work);
            Check(u.Ok && u.Message.Contains(" 1 file"), u.Message);
            var real = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var r4 = await PreviewService.InspectAsync(real);
            Check(!r4.ExtMismatch && r4.Sha256.Length == 64, "notepad");
            Check(PreviewService.Sniff([0x25, 0x50, 0x44, 0x46, 0x2D]).Type == "PDF" && PreviewService.Sniff([0x89, 0x50, 0x4E, 0x47]).Type == "Ảnh PNG", "nhận dạng PDF / PNG");
            return $"giả đuôi ok · đổi đuôi ok · nguồn tải + bỏ chặn ok · notepad: {r4.Signature}";
        });

        // ---------- Multi-Zalo ----------
        await t.Do("zalo.install", async () =>
        {
            if (MultiZaloService.AppExe() is null)
            {
                var r = await Store.StoreInstaller.InstallAsync(Store.StoreCatalog.Find("zalo")!);
                Check(r.Ok, r.Message);
                for (var i = 0; i < 30 && MultiZaloService.AppExe() is null; i++) await Task.Delay(1000);
            }
            Check(MultiZaloService.AppExe() is { } e && e.Contains("Zalo-"), "không thấy Zalo.exe trong thư mục phiên bản: " + MultiZaloService.AppExe());
            await Task.Delay(8000);
            await MultiZaloService.CloseAllAsync();
            return $"{MultiZaloService.AppExe()} · {MultiZaloService.Version()}";
        });

        var zbase = Path.Combine(work, "MultiZalo");
        await t.Do("zalo.create.uithread", async () =>
        {
            // Tái hiện lỗi treo: gọi Create / chặn chờ lệnh console từ một luồng giao diện (có Dispatcher) — phải xong trong vài giây
            foreach (var old in MultiZaloService.Load()) MultiZaloService.Delete(old);
            SysConfig.OpResult? res = null; Exception? err = null; long boot = -1;
            var th = new Thread(() =>
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                try
                {
                    res = MultiZaloService.Create("Zalo UI Test", zbase);
                    boot = ProcessRunner.ConsoleAsync("cmd.exe", "/c echo ok").GetAwaiter().GetResult().ExitCode;
                }
                catch (Exception ex) { err = ex; }
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            var done = await Task.Run(() => th.Join(30_000));
            Check(done, "Create treo trên luồng giao diện (deadlock)");
            Check(err is null, err?.Message ?? "");
            Need(res!);
            var p = MultiZaloService.Load().Single(x => x.Name == "Zalo UI Test");
            var links = Directory.GetDirectories(p.Folder).Count(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Check(links >= 3, $"chỉ có {links} liên kết thư mục");
            Need(MultiZaloService.Delete(p));
            return $"tạo hồ sơ trên luồng giao diện ok · {links} liên kết thư mục · lệnh console chặn chờ: mã {boot}";
        });

        await t.Do("zalo.two.accounts", async () =>
        {
            foreach (var old in MultiZaloService.Load()) MultiZaloService.Delete(old);
            Need(MultiZaloService.Create("Zalo Test A", zbase));
            Check(!MultiZaloService.Create("zalo test a", zbase).Ok, "trùng tên phải báo lỗi");
            var p = MultiZaloService.Load().Single();
            Check(File.Exists(p.Launcher), "chưa tạo MoZalo.vbs");
            Need(MultiZaloService.StartMain());
            await Task.Delay(15000);
            var mainOk = MultiZaloService.IsRunning(MultiZaloService.MainDataDir);
            var before = Process.GetProcessesByName("Zalo").Length;
            Need(MultiZaloService.Start(p));
            for (var i = 0; i < 30 && !MultiZaloService.IsRunning(p.DataDir); i++) await Task.Delay(1000);
            if (!MultiZaloService.IsRunning(p.DataDir))
            {
                // Chẩn đoán: chạy thẳng bằng wscript để xem file .vbs có lỗi không
                var files = Directory.Exists(p.Folder) ? Directory.GetFiles(p.Folder, "*", SearchOption.AllDirectories).Length : 0;
                var after = Process.GetProcessesByName("Zalo").Length;
                var cs = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "cscript.exe"), $"//nologo \"{p.Launcher}\"");
                await Task.Delay(20000);
                var direct = MultiZaloService.IsRunning(p.DataDir);
                var files2 = Directory.Exists(p.Folder) ? Directory.GetFiles(p.Folder, "*", SearchOption.AllDirectories).Length : 0;
                throw new InvalidOperationException($"qua explorer: không chạy (main {mainOk}, tiến trình {before}→{after}, file hồ sơ {files}) · cscript: mã {cs.ExitCode} {cs.Text} · sau cscript chạy={direct}, file {files2}");
            }
            Check(MultiZaloService.IsRunning(MultiZaloService.MainDataDir), "Zalo chính không chạy");
            var windows = 0;
            for (var i = 0; i < 30 && (windows = Process.GetProcessesByName("Zalo").Count(x => x.MainWindowHandle != IntPtr.Zero)) < 2; i++) await Task.Delay(1000);
            Check(windows >= 2, $"chỉ có {windows} cửa sổ Zalo");
            Need(MultiZaloService.CreateShortcut(p));
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Zalo - Zalo Test A.lnk");
            Check(File.Exists(lnk), "chưa có lối tắt");
            Need(MultiZaloService.Rename(p, "Zalo Test B"));
            Check(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Zalo - Zalo Test B.lnk")) && !File.Exists(lnk), "đổi tên lối tắt");
            var size = Fmt.Size(MultiZaloService.Size(p.DataDir));
            // Liên kết tới thư mục thật + xoá hồ sơ không được đụng vào thư mục thật
            var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ithd-zalo-marker.txt");
            await File.WriteAllTextAsync(marker, "x");
            Check(File.Exists(Path.Combine(p.Folder, "Documents", "ithd-zalo-marker.txt")), "Documents trong hồ sơ chưa trỏ tới Documents thật");
            Check(!MultiZaloService.Delete(MultiZaloService.Load().Single()).Ok, "đang mở phải chặn xoá");
            await MultiZaloService.CloseAllAsync();
            await Task.Delay(2000);
            Need(MultiZaloService.Delete(MultiZaloService.Load().Single()));
            Check(!Directory.Exists(p.Folder) && MultiZaloService.Load().Count == 0, "chưa xoá hồ sơ");
            Check(File.Exists(marker), "XOÁ HỒ SƠ ĐÃ XOÁ NHẦM FILE TRONG DOCUMENTS THẬT");
            File.Delete(marker);
            return $"2 tài khoản chạy song song · {windows} cửa sổ · hồ sơ phụ {size} · lối tắt / đổi tên / xoá ok";
        });

        try { Directory.Delete(work, true); } catch { }
        return await t.ToFileAsync(path);
    }
}
