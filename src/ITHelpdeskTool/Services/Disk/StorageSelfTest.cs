using System.IO;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services.Disk;

/// <summary>Ổ đĩa (trên ổ ảo VHDX), BitLocker, chuyển thư mục người dùng. --selftest storage</summary>
public static class StorageSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();
        var work = Path.Combine(Path.GetTempPath(), "ithd-storage");
        if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch { }
        Directory.CreateDirectory(work);
        var vhd = Path.Combine(work, "test.vhdx");
        var dp = Path.Combine(work, "dp.txt");
        async Task Diskpart(string script) { await File.WriteAllTextAsync(dp, script); await ProcessRunner.ConsoleAsync("diskpart.exe", $"/s \"{dp}\""); }

        await t.Do("disks.list", async () =>
        {
            var d = await DiskService.DisksAsync();
            Check(d.Count > 0 && d.Any(x => x.IsSystem), "không thấy ổ hệ thống");
            return string.Join(" | ", d.Select(x => $"{x.Number}: {x.Model} · {x.HealthText} · {x.Detail}"));
        });

        await t.Do("volumes.list", async () =>
        {
            var v = await DiskService.VolumesAsync();
            var c = v.FirstOrDefault(x => x.Letter == "C");
            Check(c is { IsProtected: true }, "C: phải là ổ được bảo vệ");
            Check(!(await DiskService.SetLetterAsync(c!, 'Q')).Ok && !(await DiskService.FormatAsync(c!, "NTFS", "x")).Ok, "phải chặn đổi ký tự / định dạng C:");
            return string.Join(" | ", v.Select(x => $"{x.Display} {x.SpaceText}"));
        });

        VolumeInfo? vol = null;
        async Task<VolumeInfo> Find(string label) =>
            (await DiskService.VolumesAsync()).FirstOrDefault(x => x.Label == label) ?? throw new InvalidOperationException($"không thấy ổ {label}");

        await t.Do("vhd.create", async () =>
        {
            await Diskpart($"create vdisk file=\"{vhd}\" maximum=3072 type=expandable\r\nselect vdisk file=\"{vhd}\"\r\nattach vdisk\r\nconvert gpt\r\ncreate partition primary size=1024\r\nformat fs=ntfs quick label=ITHDTEST\r\nassign\r\n");
            await Task.Delay(2000);
            vol = await Find("ITHDTEST");
            Check(!vol.IsProtected && vol.CanExtend, $"ổ thử: protected={vol.IsProtected} extend={vol.CanExtend}");
            return $"{vol.Display} {vol.SpaceText} · có thể mở rộng {Fmt.Size(vol.MaxSize - vol.Size)}";
        });
        if (vol is null) return await t.ToFileAsync(path);

        await t.Do("disk.label.letter", async () =>
        {
            Need(await DiskService.SetLabelAsync(vol!, "ITHD DATA"));
            vol = await Find("ITHD DATA");
            var free = DiskService.FreeLetters().Last();
            Need(await DiskService.SetLetterAsync(vol, free));
            vol = await Find("ITHD DATA");
            Check(vol.Letter == free.ToString(), $"ký tự {vol.Letter} ≠ {free}");
            return $"{vol.Display}";
        });

        await t.Do("disk.extend", async () =>
        {
            var before = vol!.Size;
            Need(await DiskService.ExtendAsync(vol));
            vol = await Find("ITHD DATA");
            Check(vol.Size > before + 1024L * 1024 * 1024, $"{Fmt.Size(before)} → {Fmt.Size(vol.Size)}");
            return $"{Fmt.Size(before)} → {Fmt.Size(vol.Size)}";
        });

        await t.Do("disk.analyze", async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var d = Directory.CreateDirectory(Path.Combine(vol!.Root, $"folder{i}"));
                await File.WriteAllBytesAsync(Path.Combine(d.FullName, "a.bin"), new byte[(i + 1) * 1024 * 1024]);
            }
            await File.WriteAllBytesAsync(Path.Combine(vol!.Root, "root.bin"), new byte[512 * 1024]);
            var list = DiskService.Analyze(vol.Root, CancellationToken.None);
            Check(list.First().Name == "folder2" && list.First().Bytes == 3 * 1024 * 1024, $"{list.First().Name} {list.First().Bytes}");
            Check(list.Any(x => !x.IsDir && x.Name == "root.bin"), "thiếu file ở gốc");
            return string.Join(", ", list.Select(x => $"{x.Name} {Fmt.Size(x.Bytes)}"));
        });

        await t.Do("disk.optimize", async () =>
        {
            var r = await DiskService.OptimizeAsync(vol!, _ => { }, _ => { }, CancellationToken.None);
            Check(r.Ok, r.Message);
            return r.Message;
        });

        await t.Do("disk.check", async () =>
        {
            var max = 0;
            var r = await DiskService.CheckAsync(vol!, _ => { }, p => max = Math.Max(max, p), CancellationToken.None);
            Check(r.Ok, r.Message);
            return $"{r.Message} · tiến độ {max}%";
        });

        await t.Do("disk.speed", async () =>
        {
            var (w, r) = await DiskService.SpeedTestAsync(vol!, 256, _ => { }, CancellationToken.None);
            Check(w > 1 && r > 1, $"ghi {w} đọc {r}");
            Check(!Directory.Exists(Path.Combine(vol!.Root, "ithd-speedtest")), "chưa dọn file thử");
            return $"đọc {r:N0} MB/s · ghi {w:N0} MB/s";
        });

        await t.Do("disk.format", async () =>
        {
            Need(await DiskService.FormatAsync(vol!, "NTFS", "ITHD FMT"));
            vol = await Find("ITHD FMT");
            Check(!Directory.Exists(Path.Combine(vol.Root, "folder0")), "dữ liệu cũ vẫn còn");
            return $"{vol.Display} {vol.SpaceText}";
        });

        // ---------- BitLocker ----------
        var (blOk, blMsg) = await BitLockerService.AvailableAsync();
        await t.Do("bitlocker.available", async () =>
        {
            Check(BitLockerService.IsRecoveryFormat("123456-123456-123456-123456-123456-123456-123456-123456") && !BitLockerService.IsRecoveryFormat("1234-5678"), "định dạng mã khôi phục");
            await Task.CompletedTask;
            return blOk ? "có BitLocker" : $"không có BitLocker: {blMsg}";
        });

        if (blOk)
        {
            BitLockerVolume? bv = null;
            async Task<BitLockerVolume> B() => (await BitLockerService.VolumesAsync()).First(x => x.MountPoint.StartsWith(vol!.Letter + ":"));
            await t.Do("bitlocker.list", async () =>
            {
                var all = await BitLockerService.VolumesAsync();
                bv = await B();
                Check(bv.Status == "FullyDecrypted", bv.Status);
                return string.Join(" | ", all.Select(x => $"{x.MountPoint} {x.StateText} · {x.Detail}"));
            });
            await t.Do("bitlocker.enable.data", async () =>
            {
                Need(await BitLockerService.EnableAsync(bv!, "Ithd@Test2026!", autoUnlock: false, skipHardwareTest: true));
                for (var i = 0; i < 60 && (bv = await B()).InProgress; i++) await Task.Delay(2000);
                Check(bv!.On && bv.HasRecovery && bv.Protectors.Any(p => p.Type == "Password"), $"{bv.StateText} · {bv.Detail}");
                var file = Path.Combine(work, "key.txt");
                Need(BitLockerService.SaveRecovery(bv, file));
                Check(File.ReadAllText(file).Contains(bv.Protectors.First(p => p.Type == "RecoveryPassword").RecoveryPassword), "file thiếu mã");
                Check(!BitLockerService.SaveRecovery(bv, Path.Combine(vol!.Root, "key.txt")).Ok, "phải chặn lưu mã lên chính ổ đó");
                var all = await BitLockerService.VolumesAsync();
                var allFile = Path.Combine(work, "all-keys.txt");
                Need(BitLockerService.SaveAllRecovery(all, allFile));
                var rp = bv.Protectors.First(p => p.Type == "RecoveryPassword").RecoveryPassword;
                Check(File.ReadAllText(allFile).Contains(rp) && File.ReadAllText(Path.ChangeExtension(allFile, ".csv")).Contains(rp), "file sao lưu tất cả thiếu mã");
                Check(!BitLockerService.SaveAllRecovery(all, Path.Combine(vol!.Root, "all.txt")).Ok, "phải chặn lưu lên ổ đã mã hoá");
                Check(!BitLockerService.SaveAllRecovery(all.Where(x => !x.HasRecovery).ToList(), allFile).Ok, "không có mã phải báo lỗi");
                return $"{bv.StateText} · {bv.Detail}";
            });
            await t.Do("bitlocker.lock.unlock", async () =>
            {
                Need(await BitLockerService.LockAsync(bv!));
                Check((await B()).Locked, "chưa khoá");
                Check(!(await BitLockerService.UnlockWithPasswordAsync(bv!, "sai-mat-khau")).Ok, "mật khẩu sai phải thất bại");
                Need(await BitLockerService.UnlockWithPasswordAsync(bv!, "Ithd@Test2026!"));
                Check(!(await B()).Locked, "chưa mở khoá");
                Need(await BitLockerService.LockAsync(bv!));
                var rk = bv!.Protectors.First(p => p.Type == "RecoveryPassword").RecoveryPassword;
                Need(await BitLockerService.UnlockWithRecoveryAsync(bv, rk));
                Check(!(await B()).Locked, "mã khôi phục không mở được");
                return "khoá → mật khẩu → khoá → mã khôi phục ok";
            });
            await t.Do("bitlocker.autounlock.disable", async () =>
            {
                Check(!(await BitLockerService.SuspendAsync(bv!, 0)).Ok, "ổ dữ liệu không tạm dừng được — giao diện chỉ hiện nút này cho ổ hệ điều hành");
                var osOn = (await BitLockerService.VolumesAsync()).Any(x => x.IsOs && x.On);
                var au = await BitLockerService.SetAutoUnlockAsync(bv!, true);
                Check(au.Ok == osOn, $"tự mở khoá: {au.Message}");
                Need(await BitLockerService.DisableAsync(bv!));
                for (var i = 0; i < 60 && (bv = await B()).InProgress; i++) await Task.Delay(2000);
                Check(bv!.Status == "FullyDecrypted", bv.StateText);
                return bv.StateText;
            });
        }

        await Diskpart($"select vdisk file=\"{vhd}\"\r\ndetach vdisk\r\n");

        // ---------- Chuyển thư mục ----------
        await t.Do("folders.read", async () =>
        {
            var lines = UserFolderService.Folders.Select(f => $"{f.Name}: {UserFolderService.GetPath(f)}{(UserFolderService.IsDefault(f) ? "" : " (đã chuyển)")}").ToList();
            Check(UserFolderService.Folders.All(f => UserFolderService.GetPath(f).Length > 3), "không đọc được vị trí");
            await Task.CompletedTask;
            return string.Join(" | ", lines) + " · gợi ý: " + string.Join(", ", UserFolderService.SuggestedBases());
        });

        var dl = UserFolderService.Folders.First(f => f.Key == "downloads");
        var baseDir = UserFolderService.SuggestedBases().FirstOrDefault() ?? Path.Combine(work, "Users");
        var target = Path.Combine(baseDir, "ITHDTest", "Downloads");
        await t.Do("folders.move", async () =>
        {
            var src = UserFolderService.GetPath(dl);
            Directory.CreateDirectory(Path.Combine(src, "ithd-sub"));
            for (var i = 0; i < 30; i++) await File.WriteAllTextAsync(Path.Combine(src, i % 2 == 0 ? "ithd-sub" : "", $"ithd-{i}.txt"), new string('x', 1000 + i));
            Check(!(await UserFolderService.MoveAsync(dl, Path.Combine(src, "inside"), _ => { }, CancellationToken.None)).Ok, "phải chặn chuyển vào chính nó");
            var busy = Path.Combine(work, "busy");
            Directory.CreateDirectory(busy);
            await File.WriteAllTextAsync(Path.Combine(busy, "x.txt"), "x");
            Check(!(await UserFolderService.MoveAsync(dl, busy, _ => { }, CancellationToken.None)).Ok, "phải chặn thư mục đích có dữ liệu");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            var r = await UserFolderService.MoveAsync(dl, target, _ => { }, CancellationToken.None);
            Check(r.Ok, r.Message);
            Check(UserFolderService.GetPath(dl).Equals(target, StringComparison.OrdinalIgnoreCase), "Known Folder chưa trỏ sang chỗ mới: " + UserFolderService.GetPath(dl));
            Check(File.Exists(Path.Combine(target, "ithd-sub", "ithd-0.txt")) && File.Exists(Path.Combine(target, "ithd-1.txt")), "thiếu file ở chỗ mới");
            Check(!File.Exists(Path.Combine(src, "ithd-1.txt")), "file vẫn còn ở chỗ cũ");
            var reg = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "{374DE290-123F-4565-9164-39C4925E467B}", "") as string;
            return $"{r.Message} · registry: {reg}";
        });

        await t.Do("folders.restore", async () =>
        {
            var r = await UserFolderService.MoveAsync(dl, UserFolderService.DefaultPath(dl), _ => { }, CancellationToken.None);
            Check(r.Ok, r.Message);
            Check(UserFolderService.IsDefault(dl), "chưa trả về mặc định: " + UserFolderService.GetPath(dl));
            var back = Path.Combine(UserFolderService.DefaultPath(dl), "ithd-1.txt");
            Check(File.Exists(back), "file chưa về chỗ cũ");
            foreach (var f in Directory.GetFiles(UserFolderService.DefaultPath(dl), "ithd-*.txt")) File.Delete(f);
            Directory.Delete(Path.Combine(UserFolderService.DefaultPath(dl), "ithd-sub"), true);
            try { Directory.Delete(Path.Combine(baseDir, "ITHDTest"), true); } catch { }
            return r.Message;
        });

        try { Directory.Delete(work, true); } catch { }
        return await t.ToFileAsync(path);
    }
}
