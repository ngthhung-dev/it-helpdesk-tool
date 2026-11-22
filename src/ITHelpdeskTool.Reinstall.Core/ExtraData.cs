using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Các mục sao lưu lấy từ những trang khác của app (để "Sao lưu trước khi xoá" không sót):
    /// Dữ liệu Outlook (PST ở mọi nơi hồ sơ trỏ tới, OST, chữ ký, danh sách gợi ý), Multi-Zalo, Cài font (font cài thêm),
    /// Power Plan (gói nguồn tự tạo), dữ liệu của chính app (bản sao lưu Startup / Gỡ app sạch, cấu hình Multi-Zalo).
    /// </summary>
    public static class ExtraData
    {
        // ================= Outlook =================
        public static List<BackupItem> Outlook(UserPaths u)
        {
            var list = new List<BackupItem>();
            // File .pst / .ost: thư mục Outlook hay dùng (sâu 2 cấp) + đường dẫn ghi trong hồ sơ Outlook (có thể ở D:\, Documents\…)
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in new[] { Path.Combine(u.Local, @"Microsoft\Outlook"), Path.Combine(u.Documents, "Outlook Files"), Path.Combine(u.Roaming, @"Microsoft\Outlook") })
                foreach (var f in Find(d, 2)) files.Add(f);
            foreach (var f in ProfilePaths().Where(File.Exists)) files.Add(Path.GetFullPath(f));

            var n = 0;
            foreach (var g in files.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            {
                var pst = g.Where(f => f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase)).ToList();
                var ost = g.Where(f => f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)).ToList();
                if (pst.Count > 0)
                    list.Add(new BackupItem
                    {
                        Id = "outlook-pst" + (n++ == 0 ? "" : n.ToString()), Group = BackupScanner.GroupMail, Default = true, Source = g.Key,
                        Title = $"Outlook: file thư lưu trên máy (.pst) — {pst.Count} file",
                        Files = pst.Select(Path.GetFileName).ToList()!, Bytes = pst.Sum(Size),
                        Note = "Đóng Outlook trước khi sao lưu. Mở lại trong Outlook: File → Open & Export → Open Outlook Data File.",
                    });
                if (ost.Count > 0)
                    list.Add(new BackupItem
                    {
                        Id = "outlook-ost" + (n++ == 0 ? "" : n.ToString()), Group = BackupScanner.GroupMail, Default = false, Source = g.Key,
                        Title = $"Outlook: bộ nhớ đệm thư (.ost) — {ost.Count} file",
                        Files = ost.Select(Path.GetFileName).ToList()!, Bytes = ost.Sum(Size),
                        Note = "Thường KHÔNG cần: thư tài khoản Microsoft 365 / Exchange / IMAP tự tải lại khi đăng nhập Outlook. " +
                               "Chỉ chọn khi hộp thư trên máy chủ đã bị xoá thư. File .ost không mở trực tiếp được — cần công cụ chuyển đổi.",
                    });
            }
            var sig = Path.Combine(u.Roaming, @"Microsoft\Signatures");
            if (Directory.Exists(sig) && Directory.EnumerateFileSystemEntries(sig).Any())
                list.Add(new BackupItem
                {
                    Id = "outlook-signatures", Title = "Outlook: chữ ký email", Group = BackupScanner.GroupMail, Source = sig, Default = true,
                    Restore = RestoreKind.AppData, RestoreTo = @"%APPDATA%\Microsoft\Signatures",
                    Note = "Tự chép lại vào tài khoản mới — mở Outlook là có chữ ký (chọn lại chữ ký mặc định trong File → Options → Mail → Signatures).",
                });
            var roam = Path.Combine(u.Local, @"Microsoft\Outlook\RoamCache");
            if (Directory.Exists(roam) && Directory.GetFiles(roam, "Stream_Autocomplete*.dat").Length > 0)
                list.Add(new BackupItem
                {
                    Id = "outlook-autocomplete", Title = "Outlook: danh sách gợi ý địa chỉ khi gõ (Auto-Complete)", Group = BackupScanner.GroupMail, Source = roam,
                    Files = { "Stream_Autocomplete*.dat" }, Default = true,
                    Note = "Lấy lại: cài Outlook, thêm tài khoản, đóng Outlook, chép file .dat đè vào %LOCALAPPDATA%\\Microsoft\\Outlook\\RoamCache (giữ tên file mới mà Outlook tạo).",
                });
            return list;
        }

        private static IEnumerable<string> Find(string dir, int depth)
        {
            if (!Directory.Exists(dir)) yield break;
            string[] here;
            try { here = Directory.GetFiles(dir); } catch (Exception) { yield break; }
            foreach (var f in here)
                if (f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)) yield return f;
            if (depth <= 0) yield break;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch (Exception) { yield break; }
            foreach (var s in subs)
                foreach (var f in Find(s, depth - 1)) yield return f;
        }

        /// <summary>Đường dẫn PST/OST trong registry hồ sơ Outlook (giá trị nhị phân Unicode chứa “.pst”/“.ost”) — như trang Dữ liệu Outlook.</summary>
        private static List<string> ProfilePaths()
        {
            var result = new List<string>();
            void Walk(RegistryKey k, int depth)
            {
                foreach (var name in k.GetValueNames())
                {
                    if (!(k.GetValue(name) is byte[] b) || b.Length <= 10 || b.Length >= 2048) continue;
                    try
                    {
                        var s = Encoding.Unicode.GetString(b).TrimEnd('\0');
                        if ((s.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)) && Path.IsPathRooted(s))
                            result.Add(s);
                    }
                    catch (Exception) { }
                }
                if (depth >= 3) return;
                foreach (var sub in k.GetSubKeyNames())
                    using (var c = k.OpenSubKey(sub)) if (c != null) Walk(c, depth + 1);
            }
            foreach (var ver in new[] { "16.0", "15.0" })
            {
                try
                {
                    using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{ver}\Outlook\Profiles");
                    if (root != null) Walk(root, 0);
                }
                catch (Exception) { }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ================= Multi-Zalo =================
        /// <summary>Thư mục dữ liệu riêng của IT Helpdesk Tool (nhật ký, bản sao lưu của Startup / Gỡ app sạch, cấu hình Multi-Zalo).</summary>
        public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITHelpdeskTool");

        /// <summary>Các hồ sơ Zalo tạo ở trang Multi-Zalo (đọc multizalo.json của app).</summary>
        public static List<BackupItem> MultiZalo()
        {
            var list = new List<BackupItem>();
            var cfg = Path.Combine(AppDataDir, "multizalo.json");
            if (!File.Exists(cfg)) return list;
            string json;
            try { json = File.ReadAllText(cfg); } catch (Exception) { return list; }
            foreach (Match m in Regex.Matches(json, "\"Name\"\\s*:\\s*\"(?<n>(?:[^\"\\\\]|\\\\.)*)\"[^{}]*?\"Folder\"\\s*:\\s*\"(?<f>(?:[^\"\\\\]|\\\\.)*)\"|\"Folder\"\\s*:\\s*\"(?<f>(?:[^\"\\\\]|\\\\.)*)\"[^{}]*?\"Name\"\\s*:\\s*\"(?<n>(?:[^\"\\\\]|\\\\.)*)\""))
            {
                var name = Unescape(m.Groups["n"].Value);
                var folder = Unescape(m.Groups["f"].Value);
                if (!Directory.Exists(folder)) continue;
                list.Add(new BackupItem
                {
                    Id = "multizalo-" + AppData.Safe(name), Title = $"Multi-Zalo: hồ sơ “{name}”", Group = BackupScanner.GroupMail, Source = folder, Default = true,
                    ExcludeDirs = { "Cache", "GPUCache", "Code Cache" },
                    Note = $"Lấy lại: cài Zalo, tạo lại hồ sơ cùng tên ở trang Multi-Zalo, tắt Zalo rồi chép thư mục này đè vào thư mục hồ sơ mới.",
                });
            }
            return list;
        }

        internal static string Unescape(string s) =>
            Regex.Replace(s, @"\\u(?<h>[0-9a-fA-F]{4})|\\(?<c>.)", m => m.Groups["h"].Success
                ? ((char)Convert.ToInt32(m.Groups["h"].Value, 16)).ToString()
                : m.Groups["c"].Value switch { "n" => "\n", "t" => "\t", "r" => "\r", var c => c });

        // ================= Cấu hình: font cài thêm, gói nguồn, dữ liệu app =================
        public const string FontsId = "fonts";
        public const string PowerPlansId = "powerplans";

        public static List<BackupItem> Config(UserPaths u)
        {
            var list = new List<BackupItem>();
            var fonts = AddedFonts(u);
            if (fonts.Count > 0)
                list.Add(new BackupItem
                {
                    Id = FontsId, Title = $"Font cài thêm ({fonts.Count} font, không tính font của Windows)", Group = BackupScanner.GroupSettings, Default = true,
                    FileList = fonts, Bytes = fonts.Sum(Size), Restore = RestoreKind.Fonts,
                    Note = "Tự cài lại trên Windows mới (font tiếng Việt VNI / TCVN3 / .VnTime… cho văn bản cũ).",
                });
            var plans = CustomPowerPlans();
            if (plans.Count > 0)
                list.Add(new BackupItem
                {
                    Id = PowerPlansId, Title = $"Gói nguồn (Power Plan) tự tạo: {string.Join(", ", plans.Select(p => p.Name))}", Group = BackupScanner.GroupSettings, Default = true,
                    Restore = RestoreKind.PowerPlans, Note = "Tự nhập lại trên Windows mới (chọn lại gói đang dùng trong Power Options).",
                });
            if (Directory.Exists(AppDataDir) && Directory.EnumerateFileSystemEntries(AppDataDir).Any())
                list.Add(new BackupItem
                {
                    Id = "ithelpdesk", Title = "Dữ liệu của IT Helpdesk Tool (nhật ký, bản sao lưu Startup / Gỡ app sạch, cấu hình Multi-Zalo)", Group = BackupScanner.GroupSettings,
                    Source = AppDataDir, Default = true, Note = "File .reg sao lưu khi xoá mục khởi động / gỡ app: nhấp đúp để khôi phục nếu cần.",
                });
            return list;
        }

        /// <summary>
        /// Font không phải của Windows: font cài cho cả máy (file trong Windows\Fonts không thuộc TrustedInstaller — như trang Cài font)
        /// và font cài riêng cho người dùng (%LOCALAPPDATA%\Microsoft\Windows\Fonts).
        /// </summary>
        public static List<string> AddedFonts(UserPaths u)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (k != null)
                    foreach (var n in k.GetValueNames())
                    {
                        if (!(k.GetValue(n) is string v) || v.Length == 0) continue;
                        var file = Path.IsPathRooted(v) ? v : Path.Combine(winFonts, v);
                        if (File.Exists(file) && !IsSystemFile(file)) result.Add(file);
                    }
            }
            catch (Exception) { }
            var userFonts = Path.Combine(u.Local, @"Microsoft\Windows\Fonts");
            try
            {
                if (Directory.Exists(userFonts))
                    foreach (var f in Directory.GetFiles(userFonts).Where(IsFontFile)) result.Add(f);
            }
            catch (Exception) { }
            return result.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static bool IsFontFile(string f) =>
            new[] { ".ttf", ".otf", ".ttc", ".fon", ".fnt", ".otc" }.Contains(Path.GetExtension(f).ToLowerInvariant());

        private static readonly SecurityIdentifier TrustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        private static bool IsSystemFile(string file)
        {
            try { return new FileInfo(file).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier == TrustedInstaller; }
            catch (Exception) { return true; }   // không đọc được chủ sở hữu → coi là của Windows (không sao lưu nhầm hàng trăm font)
        }

        // Gói nguồn có sẵn của Windows (Balanced, High performance, Power saver, Ultimate Performance)
        private static readonly string[] BuiltInPlans =
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            "a1841308-3541-4fab-bc81-f71556f20b4a", "e9a42b02-d5df-448d-aa00-03f14749eb61",
            // Chế độ nguồn (thanh trượt pin) của Windows 10/11 — "… Overlay", không phải gói tự tạo
            "961cc777-2547-4f9d-8174-7d86181b8a7a", "3af9b8d9-7c97-431d-ad78-34a8bfea439f", "ded574b5-45a0-4f42-8737-46345c09c238",
        };

        public static List<(string Guid, string Name)> CustomPowerPlans()
        {
            var list = new List<(string, string)>();
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
                if (k == null) return list;
                foreach (var g in k.GetSubKeyNames())
                {
                    if (!Guid.TryParse(g, out _) || BuiltInPlans.Contains(g.ToLowerInvariant())) continue;
                    using var s = k.OpenSubKey(g);
                    var name = s?.GetValue("FriendlyName") as string ?? g;
                    // FriendlyName dạng "@%SystemRoot%\...dll,-123,Tên" → lấy phần sau dấu phẩy cuối
                    if (name.StartsWith("@", StringComparison.Ordinal) && name.LastIndexOf(',') > 0) name = name.Substring(name.LastIndexOf(',') + 1);
                    if (name.EndsWith("Overlay", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add((g, name));
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>Sao lưu mục chạy lệnh của lớp này (font, gói nguồn). Trả lỗi (rỗng = ổn).</summary>
        public static string Run(IRunner run, BackupItem b, string to)
        {
            Directory.CreateDirectory(to);
            if (b.Id == FontsId)
            {
                var failed = 0;
                foreach (var f in b.FileList)
                {
                    try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); }
                    catch (Exception) { failed++; }
                }
                return failed == 0 ? "" : $"{failed} font không chép được";
            }
            if (b.Id == PowerPlansId)
            {
                var failed = 0;
                foreach (var (g, name) in CustomPowerPlans())
                    if (!run.Run("powercfg.exe", $"/export \"{Path.Combine(to, AppData.Safe(name) + ".pow")}\" {g}").Ok) failed++;
                return failed == 0 ? "" : $"{failed} gói nguồn không xuất được";
            }
            return "";
        }

        private static long Size(string f)
        {
            try { return new FileInfo(f).Length; } catch (Exception) { return 0; }
        }
    }
}
