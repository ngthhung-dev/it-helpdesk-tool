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
    ///           [--software default|all|none|id,id] [--tweaks default|none|id,id] [--no-drivers] [--after "lệnh"]
    ///           [--backup default|all|none|id,id] [--profile …] [--no-restore] [--ignore-backup-errors]
    ///           [--microsoft-account] [--confirm org,bitlocker-keys]
    ///           --staging auto = máy chỉ có ổ C:: thu nhỏ C: tạo phân vùng đệm ITH-STAGING, cài xong tự gộp lại
    ///           [--arm] [--reboot] [--test-fail-before-format]
    ///   decrypt --drive C: [--wait]                 tắt BitLocker / mã hoá thiết bị, theo dõi % giải mã
    ///           Thứ tự: kiểm tra → sao lưu (vào D:\ITH\backup) → chuẩn bị → đặt khởi động. Lỗi ở bước nào thì dừng ở đó.
    ///   arm     --staging D:\ITH                    đặt khởi động vào môi trường cài (một lần)
    ///   cancel  --staging D:\ITH [--remove]         huỷ, máy như cũ
    ///   download --dest D:\Win11 [--product 11|10] [--lang en-us] [--arch x64|ARM64] [--list]
    ///                                               tải bộ cài .esd gốc của Microsoft (kiểm SHA1) rồi dựng thư mục bộ cài
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
                    case "download": return Download(run, a);
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
                PreventDeviceEncryption = a.Has("no-device-encryption"),
                AfterPostInstall = a.Get("after", ""),
                Firmware = BootEntry.DetectFirmware(run),
            };
            // --edition Professional: chọn bản theo mã (file .esd tải về có nhiều bản, số thứ tự khác ISO)
            if (a.Has("edition"))
            {
                var ed = a.Get("edition");
                p.ImageIndex = Workflow.ReadEditions(run, p.ImagePath).FirstOrDefault(e => e.Edition.Equals(ed, StringComparison.OrdinalIgnoreCase))?.Index
                               ?? throw new ReinstallException($"File cài không có bản {ed}.");
            }
            // --software all: cài mọi phần mềm trong danh mục (chỉ cho phần mềm — tinh chỉnh "all" sẽ bật cả mục nguy hiểm)
            var software = a.Get("software", "default");
            p.Software = software == "all"
                ? Catalog.Software.Select(s => s.Clone()).ToList()
                : Pick(software, Catalog.Software, Catalog.DefaultSoftware, s => s.Id, s => s.Clone());
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
            foreach (var e in Workflow.ReadEditions(run, a.Get("image"))) Console.WriteLine(e);
            return 0;
        }

        private static HashSet<string> Confirmed(Args a) =>
            new HashSet<string>(a.Get("confirm", "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);

        private static WorkflowOptions Options(Args a, List<BackupItem> items) => new WorkflowOptions
        {
            Backup = items,
            Confirmed = Confirmed(a),
            IgnoreBackupErrors = a.Has("ignore-backup-errors"),
            NoRestore = a.Has("no-restore"),
            TestFailBeforeFormat = a.Has("test-fail-before-format"),
            Arm = a.Has("arm"),
        };

        private static int CheckCmd(IRunner run, Args a)
        {
            var r = new Workflow(run, Log).Inspect(Plan(run, a), Options(a, BackupItems(a)));
            return r.Ok ? 0 : (int)r.Stop;
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

        private static string Size(long b) => Workflow.Size(b);

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
            if (items.Count == 0) { Log("Không sao lưu (--backup none)."); return 0; }
            var sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(staging))?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            var res = BackupRunner.Run(run, items, Path.Combine(staging, "backup"), target, Log, sameDrive ? staging : null);
            foreach (var e in res.Errors) Log("LỖI sao lưu: " + e);
            Log($"Sao lưu xong {res.Done.Count}/{items.Count} mục vào {res.Dir}");
            return res.Ok ? 0 : 4;
        }

        private static int Prepare(IRunner run, Args a)
        {
            var p = Plan(run, a);
            // Máy một ổ chưa có phân vùng đệm: ghi log tạm, có thư mục đệm thật thì chuyển sang đó
            if (p.AutoStaging && StagingPartition.FindExisting() == null) OpenLog(Path.Combine(Path.GetTempPath(), "ithreinstall"));
            var wf = new Workflow(run, Log) { StagingReady = OpenLog };
            var r = wf.Prepare(p, Options(a, BackupItems(a)));
            foreach (var k in r.Keys) Console.WriteLine($"  Ổ {k.Drive}  khoá khôi phục: {k.Key}");
            if (r.Stop == WorkflowStop.NeedKeyConfirm)
                Log($"Chạy lại với --confirm {ConfirmKeys.BitLockerKeys} sau khi đã chép khoá.");
            if (r.Stop == WorkflowStop.StagingEncrypted)
                Log($"Tắt mã hoá: ithreinstall decrypt --drive {Path.GetPathRoot(Path.GetFullPath(p.StagingDir))!.TrimEnd('\\')} --wait, rồi chạy: ithreinstall arm --staging {p.StagingDir}");
            if (!r.Ok) return (int)r.Stop;
            if (r.Entry != null && a.Has("reboot"))
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

        private static int Download(IRunner run, Args a)
        {
            var product = a.Get("product", "11") == "10" ? EsdCatalog.Windows10 : EsdCatalog.Windows11;
            var dest = Path.GetFullPath(a.Get("dest", Path.Combine(Path.GetTempPath(), "ITH-Windows")));
            var files = EsdCatalog.LoadAsync(run, product, Path.Combine(dest, "catalog")).GetAwaiter().GetResult();
            if (a.Has("list"))
            {
                foreach (var f in files.Where(f => f.Consumer)) Console.WriteLine($"{f.LanguageCode,-6} {f}");
                return 0;
            }
            var lang = a.Get("lang", "en-us");
            var arch = a.Get("arch", EsdCatalog.MachineArchitecture());
            var esd = EsdCatalog.Pick(files, lang, arch) ?? throw new ReinstallException($"Danh mục {product} không có bản {lang} {arch}.");
            Log($"Tải: {esd} · {esd.Url}");
            var media = Path.Combine(dest, $"{product.Replace(" ", "")}-{esd.Build}-{esd.LanguageCode}-{esd.Architecture}");
            var existing = MediaBuilder.Existing(media);
            if (existing != null) { Log("Đã có sẵn: " + existing); return 0; }
            var file = Path.Combine(dest, esd.FileName);
            var lastPct = -1;
            EsdDownloader.DownloadAsync(esd, file, (d, t) =>
            {
                var pct = t > 0 ? (int)(d * 100 / t) : 0;
                if (pct / 5 != lastPct / 5) { lastPct = pct; Log($"… {pct}% ({d / 1048576:N0}/{t / 1048576:N0} MB)"); }
            }, Log).GetAwaiter().GetResult();
            Log("SHA1 khớp danh mục của Microsoft.");
            var install = MediaBuilder.Build(run, file, media, Log);
            foreach (var e in Workflow.ReadEditions(run, install)) Console.WriteLine(e + " [" + e.Edition + "]");
            Log("Chọn file cài: " + install);
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
