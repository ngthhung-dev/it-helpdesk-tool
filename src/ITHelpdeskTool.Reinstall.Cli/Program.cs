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
    ///   check   --image X --staging D:\ITH          kiểm tra máy trước khi cài
    ///   prepare --image X --index N --staging D:\ITH [--account Admin] [--password P] [--computer NAME]
    ///           [--software default|none|id,id] [--tweaks default|none|id,id] [--no-drivers] [--after "lệnh"]
    ///           [--arm] [--reboot] [--test-fail-before-format]
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
            "ithreinstall info|check|prepare|arm|cancel …  (xem chú thích trong Program.cs)\n" +
            "Ví dụ: ithreinstall prepare --image E:\\Win11.iso --index 1 --staging D:\\ITH --account Admin --arm --reboot";

        private static void Log(string m)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {m}";
            Console.WriteLine(line);
            _log?.WriteLine(line);
            _log?.Flush();
        }

        private static void OpenLog(string staging)
        {
            var dir = Path.Combine(staging, "logs");
            Directory.CreateDirectory(dir);
            _log = new StreamWriter(Path.Combine(dir, "prepare.log"), append: true, new UTF8Encoding(false));
        }

        private static ReinstallPlan Plan(IRunner run, Args a)
        {
            var p = new ReinstallPlan
            {
                ImagePath = a.Get("image"),
                ImageIndex = int.Parse(a.Get("index", "1")),
                StagingDir = a.Get("staging", @"D:\ITH"),
                TargetDrive = a.Get("target", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\')),
                AccountName = a.Get("account", "Admin"),
                Password = a.Get("password", ""),
                ComputerName = a.Get("computer", "*"),
                ExportDrivers = !a.Has("no-drivers"),
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

        private static bool RunChecks(IRunner run, ReinstallPlan p)
        {
            var facts = MachineFacts.Read(run);
            var checks = Preflight.Run(p, facts, ImageBytes(p.ImagePath));
            foreach (var c in checks) Log(c.ToString());
            return checks.All(c => c.Level != CheckLevel.Block);
        }

        private static int CheckCmd(IRunner run, Args a) => RunChecks(run, Plan(run, a)) ? 0 : 3;

        private static int Prepare(IRunner run, Args a)
        {
            var p = Plan(run, a);
            Directory.CreateDirectory(p.StagingDir);
            OpenLog(p.StagingDir);
            Log($"=== Chuẩn bị cài lại Windows: {p.ImagePath} bản {p.ImageIndex} → {p.TargetDrive} (đệm {p.StagingDir}, {p.Firmware})");
            if (!RunChecks(run, p))
            {
                Log("Dừng: còn mục chặn (Block) ở trên.");
                return 3;
            }
            var stager = new Stager(run, Log) { TestFailBeforeFormat = a.Has("test-fail-before-format") };
            var r = stager.Prepare(p);
            Log($"Đã chuẩn bị: {r.Edition} · mã {r.Id} · driver ổ cứng: {r.StorageDrivers.Count}");
            if (!a.Has("arm")) return 0;
            var e = stager.Arm(p);
            Log($"Đã đặt khởi động một lần vào môi trường cài ({e.EntryId}). Khởi động lại máy để bắt đầu; huỷ: ithreinstall cancel --staging {p.StagingDir}");
            if (a.Has("reboot"))
            {
                Log("Khởi động lại sau 10 giây…");
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"IT Helpdesk Tool - cai lai Windows\"") { UseShellExecute = false, CreateNoWindow = true });
            }
            return 0;
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
