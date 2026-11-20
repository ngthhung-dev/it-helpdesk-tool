using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ITHelpdeskTool.Reinstall;

namespace ITHelpdeskTool.Reinstall.Cli
{
    /// <summary>
    /// ithreinstall — cài lại Windows không cần USB (dòng lệnh).
    ///   info    --image X.iso                       liệt kê bản Windows trong file cài
    ///   check   --image X --index N --staging D:\ITH  kiểm tra máy + tương thích với bản Windows đã chọn
    ///   backup  --staging D:\ITH [--backup default|all|none|id,id] [--profile C:\Users\X] [--list]
    ///   prepare --image X --index N --staging D:\ITH [--account Admin] [--password P] [--computer NAME]
    ///           [--software default|none|id,id] [--tweaks default|none|id,id] [--no-drivers] [--after "lệnh"]
    ///           [--backup default|all|none|id,id] [--profile …] [--no-restore] [--ignore-backup-errors]
    ///           [--microsoft-account] [--confirm org,bitlocker-keys]
    ///           --staging auto = máy chỉ có ổ C:: thu nhỏ C: tạo phân vùng đệm ITH-STAGING, cài xong tự gộp lại
    ///           [--arm] [--reboot] [--test-fail-before-format]
    ///   decrypt --drive C: [--wait]                 tắt BitLocker / mã hoá thiết bị, theo dõi % giải mã
    ///           Thứ tự: kiểm tra → sao lưu (vào D:\ITH\backup) → chuẩn bị → đặt khởi động. Lỗi ở bước nào thì dừng ở đó.
    ///   arm     --staging D:\ITH                    đặt khởi động vào môi trường cài (một lần)
    ///   cancel  --staging D:\ITH [--remove]         huỷ, máy như cũ
    /// </summary>
    internal static class Program
    {
        private static StreamWriter? _log;

        private static int Main(string[] argv)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var cmd = argv[0].ToLowerInvariant();
            var a = Args.Parse(argv.Skip(1));
            var run = new ProcessRunner(Log);
            try
            {
                switch (cmd)
                {
                    case "info": return Info(run, a);
                    case "check": return CheckCmd(run, a);
                    case "backup": return BackupCmd(run, a);
                    case "decrypt": return Decrypt(run, a);
                    case "prepare": return Prepare(run, a);
                    case "arm": return Arm(run, a);
                    case "cancel": return Cancel(run, a);
                    default:
                        Console.WriteLine($"Lệnh không rõ: {cmd}\n\n{Usage}");
                        return 2;
                }
            }
            catch (ReinstallException ex)
            {
                Log("LỖI: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Log("LỖI: " + ex);
                return 1;
            }
            finally
            {
                _log?.Dispose();
            }
        }

        private const string Usage =
            "ithreinstall info|check|backup|prepare|arm|cancel|decrypt …  (xem chú thích trong Program.cs)\n" +
            "Ví dụ: ithreinstall prepare --image E:\\Win11.iso --index 1 --staging D:\\ITH --account Admin --arm --reboot";

        private static void Log(string m)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {m}";
            Console.WriteLine(line);
            _log?.WriteLine(line);
            _log?.Flush();
        }

        private static string? _logPath;

        private static void OpenLog(string staging)
        {
            var dir = Path.Combine(staging, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "prepare.log");
            // Đang ghi log tạm (chưa có phân vùng đệm) → chuyển nội dung sang log thật
            if (_log != null && _logPath != null && !_logPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                _log.Dispose();
                File.AppendAllText(path, File.ReadAllText(_logPath), new UTF8Encoding(false));
                try { File.Delete(_logPath); } catch { }
            }
            _logPath = path;
            _log = new StreamWriter(path, append: true, new UTF8Encoding(false));
        }

        private static ReinstallPlan Plan(IRunner run, Args a)
        {
            var p = new ReinstallPlan
            {
                ImagePath = a.Get("image"),
                ImageIndex = int.Parse(a.Get("index", "1")),
                StagingDir = a.Get("staging", @"D:\ITH"),
                AutoStaging = a.Get("staging", "").Equals("auto", StringComparison.OrdinalIgnoreCase),
                TargetDrive = a.Get("target", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\')),
                AccountName = a.Get("account", "Admin"),
                Password = a.Get("password", ""),
                ComputerName = a.Get("computer", "*"),
                ExportDrivers = !a.Has("no-drivers"),
                MicrosoftAccount = a.Has("microsoft-account"),
                AfterPostInstall = a.Get("after", ""),
                Firmware = BootEntry.DetectFirmware(run),
            };
            p.Software = Pick(a.Get("software", "default"), Catalog.Software, Catalog.DefaultSoftware, s => s.Id, s => s.Clone());
            p.Tweaks = Pick(a.Get("tweaks", "default"), Catalog.Tweaks, Catalog.DefaultTweaks, t => t.Id, t => t.Clone());
            return p;
        }

        private static List<T> Pick<T>(string spec, IReadOnlyList<T> all, Func<List<T>> defaults, Func<T, string> id, Func<T, T> clone)
        {
            if (spec == "default") return defaults();
            if (spec == "none" || spec.Length == 0) return new List<T>();
            var ids = spec.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var unknown = ids.Where(i => all.All(x => !id(x).Equals(i, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) throw new ReinstallException("Không có trong danh sách: " + string.Join(", ", unknown));
            return all.Where(x => ids.Contains(id(x), StringComparer.OrdinalIgnoreCase)).Select(clone).ToList();
        }

        private static int Info(IRunner run, Args a)
        {
            var image = a.Get("image");
            IsoMount? iso = null;
            try
            {
                if (image.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    iso = IsoMount.Mount(run, image);
                    image = MediaFiles.FromRoot(iso.Root).InstallImage;
                }
                foreach (var e in ImageInfo.Read(run, image, details: true)) Console.WriteLine(e);
                return 0;
            }
            finally { iso?.Dispose(); }
        }

        private static long ImageBytes(string image) => File.Exists(image) ? new FileInfo(image).Length : 0;

        /// <summary>Đọc chi tiết bản Windows đã chọn (kiến trúc, build, mã bản) — mở ISO nếu cần.</summary>
        private static ImageEdition ReadEdition(IRunner run, string image, int index)
        {
            IsoMount? iso = null;
            try
            {
                if (image.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    iso = IsoMount.Mount(run, image);
                    image = MediaFiles.FromRoot(iso.Root).InstallImage;
                }
                var all = ImageInfo.Read(run, image, details: true);
                return all.FirstOrDefault(e => e.Index == index)
                       ?? throw new ReinstallException($"File cài không có bản số {index}. Có: {string.Join("; ", all)}");
            }
            finally { iso?.Dispose(); }
        }

        private static HashSet<string> Confirmed(Args a) =>
            new HashSet<string>(a.Get("confirm", "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);

        private static bool RunChecks(IRunner run, ReinstallPlan p, long backupBytes, ICollection<string> confirmed, out MachineFacts facts)
        {
            facts = MachineFacts.Read(run);
            var ed = ReadEdition(run, p.ImagePath, p.ImageIndex);
            Log($"Bản sẽ cài: {ed} [{ed.Edition}]");
            Log($"Máy: {facts.ProductName} ({facts.EditionId}) · {facts.CpuName} · RAM {facts.RamBytes / 1073741824.0:0.#} GB · {facts.Firmware} · TPM {facts.Tpm}");
            Log($"Tài khoản: {(facts.Account.Source.Length > 0 ? facts.Account.Source : "?")}" +
                (facts.Account.ManagedByOrganization ? " · quản lý bởi " + facts.Account.OrganizationText : "") +
                " · BitLocker: " + (facts.BitLocker.Count > 0 ? string.Join(", ", facts.BitLocker.Values) : "không"));
            var checks = Preflight.Run(p, facts, ImageBytes(p.ImagePath) + backupBytes, null, confirmed);
            long targetBytes = 0;
            try { targetBytes = new DriveInfo(p.TargetDrive).TotalSize; } catch { }
            checks.AddRange(Compatibility.Evaluate(facts, ed, targetBytes));
            foreach (var c in checks) Log(c.ToString());
            return checks.All(c => c.Level != CheckLevel.Block);
        }

        private static int CheckCmd(IRunner run, Args a)
        {
            var items = BackupItems(a);
            return RunChecks(run, Plan(run, a), items.Sum(i => i.Bytes), Confirmed(a), out _) ? 0 : 3;
        }

        // ---- Sao lưu ----
        private static UserPaths Paths(Args a)
        {
            if (!a.Has("profile")) return UserPaths.Current();
            var pr = a.Get("profile");
            return new UserPaths
            {
                Profile = pr,
                Desktop = Path.Combine(pr, "Desktop"),
                Documents = Path.Combine(pr, "Documents"),
                Downloads = Path.Combine(pr, "Downloads"),
                Pictures = Path.Combine(pr, "Pictures"),
                Videos = Path.Combine(pr, "Videos"),
                Music = Path.Combine(pr, "Music"),
                Roaming = Path.Combine(pr, @"AppData\Roaming"),
                Local = Path.Combine(pr, @"AppData\Local"),
            };
        }

        private static List<BackupItem> BackupItems(Args a)
        {
            var spec = a.Get("backup", "default");
            if (spec == "none" || spec.Length == 0) return new List<BackupItem>();
            var all = BackupScanner.Scan(Paths(a));
            if (spec == "all") return all;
            if (spec == "default") return all.Where(b => b.Default).ToList();
            var ids = spec.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var unknown = ids.Where(i => all.All(b => !b.Id.Equals(i, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) throw new ReinstallException("Máy không có mục sao lưu: " + string.Join(", ", unknown) + ". Có: " + string.Join(", ", all.Select(b => b.Id)));
            return all.Where(b => ids.Contains(b.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private static string Size(long b) => b >= 1073741824 ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:0} MB";

        private static BackupResult? DoBackup(IRunner run, Args a, List<BackupItem> items, string staging, string target)
        {
            if (items.Count == 0) { Log("Không sao lưu (--backup none)."); return null; }
            foreach (var b in items) Log($"Sao lưu: {b.Title} {(b.Bytes > 0 ? Size(b.Bytes) : "")} {(b.Source.Length > 0 ? "← " + b.Source : "")}");
            // Thư mục đệm ngay trên ổ Windows được giữ lại khi xoá ổ → cho sao lưu vào đó
            var sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(staging))?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            var res = BackupRunner.Run(run, items, Path.Combine(staging, "backup"), target, Log, sameDrive ? staging : null);
            foreach (var e in res.Errors) Log("LỖI sao lưu: " + e);
            Log($"Sao lưu xong {res.Done.Count}/{items.Count} mục vào {res.Dir}");
            return res;
        }

        private static int BackupCmd(IRunner run, Args a)
        {
            var staging = a.Get("staging", @"D:\ITH");
            var target = a.Get("target", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\'));
            var items = BackupItems(a);
            if (a.Has("list"))
            {
                foreach (var b in BackupScanner.Scan(Paths(a)))
                    Console.WriteLine($"{(b.Default ? "[x]" : "[ ]")} {b.Id,-18} {Size(b.Bytes),9}  {b.Group} · {b.Title}" + (b.Note.Length > 0 ? $"\n      {b.Note}" : ""));
                return 0;
            }
            Directory.CreateDirectory(staging);
            OpenLog(staging);
            var res = DoBackup(run, a, items, staging, target);
            return res == null || res.Ok ? 0 : 4;
        }

        private static int Prepare(IRunner run, Args a)
        {
            var p = Plan(run, a);
            var existing = p.AutoStaging ? StagingPartition.FindExisting() : null;
            if (existing != null) p.StagingDir = existing + "\\ITH";        // chạy lại: dùng phân vùng đệm đã tạo
            if (p.AutoStaging && existing == null) OpenLog(Path.Combine(Path.GetTempPath(), "ithreinstall"));
            else { Directory.CreateDirectory(p.StagingDir); OpenLog(p.StagingDir); }
            Log($"=== Chuẩn bị cài lại Windows: {p.ImagePath} bản {p.ImageIndex} → {p.TargetDrive} (đệm {(p.AutoStaging && existing == null ? "tự tạo bằng cách thu nhỏ " + p.TargetDrive : p.StagingDir)}, {p.Firmware})");

            // 1. Kiểm tra máy + tương thích
            var items = BackupItems(a);
            var confirmed = Confirmed(a);
            if (!RunChecks(run, p, items.Sum(i => i.Bytes), confirmed, out var facts))
            {
                Log("Dừng: còn mục chặn (Block) ở trên.");
                return 3;
            }

            // 1a. Máy chỉ có ổ C:: thu nhỏ C: tạo phân vùng đệm (lần chạy lại dùng phân vùng đã tạo)
            if (p.AutoStaging && existing == null)
            {
                var needed = StagingPartition.NeededBytes(ImageBytes(p.ImagePath), items.Sum(i => i.Bytes));
                var info = StagingPartition.Probe(run, p.TargetDrive);
                var shrink = StagingPartition.Check(info, needed);
                foreach (var c in shrink) Log(c.ToString());
                if (!shrink.Any(c => c.Level == CheckLevel.Block))
                {
                    Log($"Đang thu nhỏ {p.TargetDrive} {needed / 1073741824} GB để tạo phân vùng đệm…");
                    var drive = StagingPartition.Create(run, info, needed);
                    p.StagingDir = drive + "\\ITH";
                    Log($"Đã tạo phân vùng đệm {drive} ({StagingPartition.Label}). Huỷ: ithreinstall cancel --staging {p.StagingDir} --remove");
                }
                else if (info.Free >= needed + (2L << 30))
                {
                    // Không thu nhỏ được (file hệ thống nằm cuối ổ) → để thư mục đệm ngay trên ổ Windows, môi trường cài xoá mọi thứ trừ nó
                    p.StagingDir = p.TargetDrive.TrimEnd('\\') + "\\ITH";
                    Log($"Không thu nhỏ được {p.TargetDrive} — dùng thư mục đệm ngay trên ổ Windows: {p.StagingDir} (không format, xoá mọi thứ trừ thư mục này).");
                }
                else
                {
                    Log($"Dừng: {p.TargetDrive} không đủ chỗ trống cho thư mục đệm (~{needed / 1073741824} GB). Bớt mục sao lưu hoặc dọn ổ.");
                    return 3;
                }
                Directory.CreateDirectory(p.StagingDir);
                OpenLog(p.StagingDir);
            }

            // 1b. Ổ dữ liệu mã hoá BitLocker: lưu khoá khôi phục, bắt xác nhận đã chép trước khi đi tiếp
            var encrypted = Preflight.EncryptedDataDrives(p, facts);
            if (encrypted.Count > 0)
            {
                var keys = new List<RecoveryKey>();
                foreach (var v in encrypted)
                {
                    var k = Encryption.RecoveryKeys(run, v.Drive);
                    if (k.Count == 0)
                    {
                        Log($"Dừng: ổ {v.Drive} mã hoá nhưng không có khoá khôi phục dạng 48 số — Windows mới sẽ không mở được ổ này. " +
                            $"Thêm khoá: manage-bde -protectors -add {v.Drive} -RecoveryPassword (rồi chạy lại).");
                        return 5;
                    }
                    keys.AddRange(k);
                }
                var keyDir = Path.Combine(p.StagingDir, "backup", "bitlocker");
                Directory.CreateDirectory(keyDir);
                var keyFile = Path.Combine(keyDir, "khoa-khoi-phuc-bitlocker.txt");
                File.WriteAllText(keyFile, Encryption.KeysText(keys), new UTF8Encoding(true));
                foreach (var k in keys) Console.WriteLine($"  Ổ {k.Drive}  khoá khôi phục: {k.Key}");
                Log($"Đã lưu khoá khôi phục BitLocker của {string.Join(", ", encrypted.Select(v => v.Drive))} vào {keyFile}");
                if (!confirmed.Contains(ConfirmKeys.BitLockerKeys))
                {
                    Log($"Dừng: chép các khoá trên ra giấy / nơi khác ngoài máy, rồi chạy lại với --confirm {ConfirmKeys.BitLockerKeys}.");
                    return 6;
                }
            }

            // 2. Sao lưu — chưa xong thì không đi tiếp
            var backup = DoBackup(run, a, items, p.StagingDir, p.TargetDrive);
            if (backup != null && !backup.Ok && !a.Has("ignore-backup-errors"))
            {
                Log("Dừng: sao lưu có lỗi. Sửa lỗi rồi chạy lại, hoặc thêm --ignore-backup-errors nếu chấp nhận thiếu các file trên.");
                return 4;
            }
            if (backup != null && !a.Has("no-restore")) p.Restore = backup.Done;

            // 3. Chuẩn bị
            var stager = new Stager(run, Log) { TestFailBeforeFormat = a.Has("test-fail-before-format") };
            var r = stager.Prepare(p);
            Log($"Đã chuẩn bị: {r.Edition} · mã {r.Id} · driver ổ cứng: {r.StorageDrivers.Count} · tự chép lại: {p.Restore.Count} mục");
            if (!a.Has("arm")) return 0;

            // 4. Đặt khởi động một lần
            var e = stager.Arm(p);
            Log($"Đã đặt khởi động một lần vào môi trường cài ({e.EntryId}). Khởi động lại máy để bắt đầu; huỷ: ithreinstall cancel --staging {p.StagingDir}");
            if (a.Has("reboot"))
            {
                Log("Khởi động lại sau 10 giây…");
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"IT Helpdesk Tool - cai lai Windows\"") { UseShellExecute = false, CreateNoWindow = true });
            }
            return 0;
        }

        private static int Decrypt(IRunner run, Args a)
        {
            var drive = a.Get("drive", "C:");
            var before = Encryption.Status(run, drive);
            if (before == null || !before.Encrypted)
            {
                Log($"Ổ {drive} không mã hoá.");
                return 0;
            }
            if (!before.Decrypting)
            {
                var r = Encryption.TurnOff(run, drive);
                if (!r.Ok) { Log("Không tắt được mã hoá: " + r.Output.Trim()); return 1; }
                Log($"Đã bắt đầu giải mã ổ {drive}. Vẫn dùng máy bình thường được; tắt máy thì lần sau chạy tiếp.");
            }
            if (!a.Has("wait")) return 0;
            while (true)
            {
                var st = Encryption.Status(run, drive);
                if (st == null || !st.Encrypted) { Log($"Ổ {drive} đã giải mã xong."); return 0; }
                Log($"Ổ {drive}: còn {st.Percent:0.#}% đang mã hoá…");
                System.Threading.Thread.Sleep(15000);
            }
        }

        private static int Arm(IRunner run, Args a)
        {
            var p = Plan(run, a);
            OpenLog(p.StagingDir);
            var e = new Stager(run, Log).Arm(p);
            Log($"Đã đặt khởi động một lần vào môi trường cài ({e.EntryId}).");
            return 0;
        }

        private static int Cancel(IRunner run, Args a)
        {
            var staging = a.Get("staging", @"D:\ITH");
            var target = a.Get("target", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\'));
            if (staging.Equals("auto", StringComparison.OrdinalIgnoreCase))
                staging = (StagingPartition.FindExisting() ?? throw new ReinstallException("Không có phân vùng đệm do app tạo.")) + "\\ITH";
            new Stager(run, Log).Cancel(staging, target, a.Has("remove"));
            Log("Đã huỷ: bỏ mục khởi động tạm và file đánh dấu.");
            return 0;
        }
    }

    internal sealed class Args
    {
        private readonly Dictionary<string, string> _v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Args Parse(IEnumerable<string> argv)
        {
            var a = new Args();
            string? key = null;
            foreach (var s in argv)
            {
                if (s.StartsWith("--", StringComparison.Ordinal))
                {
                    if (key != null) a._v[key] = "";
                    key = s.Substring(2);
                }
                else if (key != null)
                {
                    a._v[key] = s;
                    key = null;
                }
            }
            if (key != null) a._v[key] = "";
            return a;
        }

        public bool Has(string k) => _v.ContainsKey(k);

        public string Get(string k, string? def = null)
        {
            if (_v.TryGetValue(k, out var v)) return v;
            return def ?? throw new ReinstallException($"Thiếu --{k}");
        }
    }
}
