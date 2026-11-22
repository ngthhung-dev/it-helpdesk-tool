using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Thư mục của người dùng có dữ liệu cần sao lưu (app truyền vào theo người đang đăng nhập).</summary>
    public sealed class UserPaths
    {
        public string Profile { get; set; } = "";
        public string Desktop { get; set; } = "";
        public string Documents { get; set; } = "";
        public string Downloads { get; set; } = "";
        public string Pictures { get; set; } = "";
        public string Videos { get; set; } = "";
        public string Music { get; set; } = "";
        public string Roaming { get; set; } = "";
        public string Local { get; set; } = "";

        /// <summary>Theo tài khoản đang chạy chương trình (dùng cho công cụ dòng lệnh).</summary>
        public static UserPaths Current()
        {
            string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
            var profile = F(Environment.SpecialFolder.UserProfile);
            return new UserPaths
            {
                Profile = profile,
                Desktop = F(Environment.SpecialFolder.DesktopDirectory),
                Documents = F(Environment.SpecialFolder.MyDocuments),
                Downloads = Path.Combine(profile, "Downloads"),
                Pictures = F(Environment.SpecialFolder.MyPictures),
                Videos = F(Environment.SpecialFolder.MyVideos),
                Music = F(Environment.SpecialFolder.MyMusic),
                Roaming = F(Environment.SpecialFolder.ApplicationData),
                Local = F(Environment.SpecialFolder.LocalApplicationData),
            };
        }
    }

    public enum RestoreKind
    {
        /// <summary>Chỉ giữ trong bản sao lưu, người dùng tự lấy lại.</summary>
        None,
        /// <summary>Chép lại vào thư mục cùng tên của tài khoản mới (Desktop, Documents…).</summary>
        UserFolder,
        /// <summary>Nạp lại hồ sơ Wi-Fi (netsh wlan add profile).</summary>
        WiFi,
        /// <summary>Chép file Bookmarks vào hồ sơ mặc định của trình duyệt Chromium tương ứng.</summary>
        Bookmarks,
        /// <summary>Chép lại vào thư mục có biến môi trường (vd %APPDATA%\Microsoft\Signatures) của tài khoản mới.</summary>
        AppData,
        /// <summary>Cài lại font (chép vào Windows\Fonts + ghi registry).</summary>
        Fonts,
        /// <summary>Nhập lại gói nguồn (powercfg /import).</summary>
        PowerPlans,
    }

    /// <summary>Một mục có thể sao lưu.</summary>
    public sealed class BackupItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Group { get; set; } = "";
        /// <summary>Thư mục nguồn (rỗng = mục chạy lệnh, vd xuất Wi-Fi).</summary>
        public string Source { get; set; } = "";
        /// <summary>Chỉ chép các file khớp mẫu này (vd *.pst); rỗng = tất cả.</summary>
        public List<string> Files { get; set; } = new List<string>();
        /// <summary>Thư mục con bỏ qua (bộ nhớ đệm…).</summary>
        public List<string> ExcludeDirs { get; set; } = new List<string>();
        public long Bytes { get; set; }
        /// <summary>File chỉ có trên OneDrive (chưa tải về máy) — KHÔNG sao lưu (vẫn còn trên cloud).</summary>
        public long CloudBytes { get; set; }
        public int CloudFiles { get; set; }
        /// <summary>Thư mục nằm trong OneDrive (Desktop / Documents được OneDrive “sao lưu thư mục”).</summary>
        public bool InOneDrive => Source.IndexOf("\\OneDrive", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool Default { get; set; }
        public string Note { get; set; } = "";
        public RestoreKind Restore { get; set; }
        /// <summary>UserFolder: Desktop / Documents / Downloads / Pictures / Videos / Music. Bookmarks: đường dẫn hồ sơ trình duyệt so với %LOCALAPPDATA%.</summary>
        public string RestoreTo { get; set; } = "";

        /// <summary>Mục là một phiên bản SQL Server (sao lưu .bak + chép file dữ liệu, xem <see cref="AppData.BackupSql"/>).</summary>
        public SqlInstanceInfo? Sql { get; set; }

        /// <summary>Danh sách file cụ thể (đường dẫn đầy đủ) — vd font cài thêm nằm lẫn trong Windows\Fonts.</summary>
        public List<string> FileList { get; set; } = new List<string>();

        /// <summary>Tên thư mục con trong bản sao lưu.</summary>
        public string Folder => Id;
    }

    /// <summary>Tìm các mục dữ liệu quan trọng của người dùng trước khi cài lại.</summary>
    public static class BackupScanner
    {
        private static readonly string[] BrowserCache =
            { "Cache", "Code Cache", "GPUCache", "Service Worker", "ShaderCache", "GrShaderCache", "DawnCache", "Crashpad", "component_crx_cache", "optimization_guide_model_store" };

        // Nhóm hiển thị (theo thứ tự): dữ liệu cá nhân → thư / chat → phần mềm kế toán → trình duyệt → cài đặt máy
        public const string GroupPersonal = "Thư mục cá nhân";
        public const string GroupMail = "Email & chat (Outlook, Zalo)";
        public const string GroupBrowsers = "Trình duyệt";
        public const string GroupSettings = "Cài đặt máy (Wi-Fi, font, máy in…)";
        public static readonly string[] Groups = { GroupPersonal, GroupMail, AppData.Group, GroupBrowsers, GroupSettings };

        private static int GroupRank(string g)
        {
            var i = Array.IndexOf(Groups, g);
            return i < 0 ? Groups.Length : i;
        }

        /// <summary>Thứ tự trong nhóm: thứ quan trọng / hay cần lên trước.</summary>
        private static int ItemRank(string id)
        {
            if (id.StartsWith("outlook-pst", StringComparison.Ordinal)) return 0;
            if (id.StartsWith("outlook-ost", StringComparison.Ordinal)) return 3;
            if (id.StartsWith("multizalo-", StringComparison.Ordinal)) return 5;
            if (id.StartsWith(AppData.SqlPrefix, StringComparison.Ordinal)) return 0;
            if (id.StartsWith("htkk-", StringComparison.Ordinal)) return 1;
            switch (id)
            {
                case "outlook-signatures": return 1;
                case "outlook-autocomplete": return 2;
                case "zalo": return 4;
                case "wifi": return 0;
                case ExtraData.FontsId: return 1;
                case "printers": return 2;
                case ExtraData.PowerPlansId: return 3;
                case "apps": return 4;
                case "ithelpdesk": return 5;
                default: return 0;
            }
        }

        /// <param name="targetDrive">Ổ sẽ bị xoá: mục nằm ở ổ khác (vd Downloads đã chuyển sang E:) vẫn còn sau khi cài lại → mặc định không sao lưu.</param>
        public static List<BackupItem> Scan(UserPaths u, bool measure = true, string targetDrive = "C:")
        {
            var list = new List<BackupItem>();
            void Folder(string id, string title, string path, string restoreTo, bool def = true)
            {
                if (path.Length == 0 || !Directory.Exists(path)) return;
                list.Add(new BackupItem { Id = id, Title = title, Group = BackupScanner.GroupPersonal, Source = path, Default = def, Restore = RestoreKind.UserFolder, RestoreTo = restoreTo });
            }
            Folder("Desktop", "Desktop", u.Desktop, "Desktop");
            Folder("Documents", "Documents (Tài liệu)", u.Documents, "Documents");
            Folder("Downloads", "Downloads (Tải về)", u.Downloads, "Downloads");
            Folder("Pictures", "Pictures (Ảnh)", u.Pictures, "Pictures");
            Folder("Videos", "Videos", u.Videos, "Videos", def: false);
            Folder("Music", "Music", u.Music, "Music", def: false);

            // ---- Trình duyệt nhân Chromium: cả hồ sơ (trừ bộ nhớ đệm) + tự chép lại dấu trang ----
            // (cùng danh sách với trang "Sao lưu trình duyệt")
            var browsers = new[]
            {
                ("chrome", "Google Chrome", @"Google\Chrome\User Data"),
                ("edge", "Microsoft Edge", @"Microsoft\Edge\User Data"),
                ("coccoc", "Cốc Cốc", @"CocCoc\Browser\User Data"),
                ("brave", "Brave", @"BraveSoftware\Brave-Browser\User Data"),
                ("vivaldi", "Vivaldi", @"Vivaldi\User Data"),
                ("chromium", "Chromium", @"Chromium\User Data"),
            };
            foreach (var (id, name, rel) in browsers)
            {
                var dir = Path.Combine(u.Local, rel);
                if (!Directory.Exists(dir)) continue;
                list.Add(new BackupItem
                {
                    Id = "browser-" + id, Title = $"{name} (dấu trang, lịch sử, tiện ích)", Group = BackupScanner.GroupBrowsers, Source = dir,
                    ExcludeDirs = BrowserCache.ToList(), Default = true, Restore = RestoreKind.Bookmarks, RestoreTo = rel,
                    Note = "Dấu trang được chép lại tự động. Mật khẩu đã lưu KHÔNG dùng lại được trên Windows mới (bị mã hoá theo máy) — " +
                           "hãy bật đồng bộ tài khoản trình duyệt hoặc xuất mật khẩu bằng chính trình duyệt trước.",
                });
            }
            var firefox = Path.Combine(u.Roaming, @"Mozilla\Firefox");
            if (Directory.Exists(firefox))
                list.Add(new BackupItem { Id = "browser-firefox", Title = "Mozilla Firefox (hồ sơ)", Group = BackupScanner.GroupBrowsers, Source = firefox, Default = true,
                    ExcludeDirs = { "cache2", "startupCache", "crashes" }, Note = "Lấy lại bằng cách chép vào %APPDATA%\\Mozilla\\Firefox trước khi mở Firefox." });
            // Opera để hồ sơ trong Roaming, không có thư mục Default
            foreach (var (id, name, rel) in new[] { ("opera", "Opera", @"Opera Software\Opera Stable"), ("operagx", "Opera GX", @"Opera Software\Opera GX Stable") })
            {
                var dir = Path.Combine(u.Roaming, rel);
                if (Directory.Exists(dir))
                    list.Add(new BackupItem { Id = "browser-" + id, Title = $"{name} (dấu trang, lịch sử, tiện ích)", Group = BackupScanner.GroupBrowsers, Source = dir, Default = true,
                        ExcludeDirs = BrowserCache.ToList(),
                        Note = $"Lấy lại: cài {name}, đóng trình duyệt, chép thư mục này đè vào %APPDATA%\\{rel}. Mật khẩu đã lưu không dùng lại được — bật đồng bộ tài khoản Opera." });
            }

            // ---- Ứng dụng ----
            list.AddRange(ExtraData.Outlook(u));
            var zalo = Path.Combine(u.Roaming, "ZaloData");
            if (Directory.Exists(zalo))
                list.Add(new BackupItem { Id = "zalo", Title = "Zalo PC (tin nhắn, file đã nhận)", Group = BackupScanner.GroupMail, Source = zalo, Default = true,
                    Note = "Cài Zalo, đăng nhập rồi chép thư mục này đè vào %APPDATA%\\ZaloData (tắt Zalo trước)." });
            list.AddRange(ExtraData.MultiZalo());

            // ---- Cấu hình máy (chạy lệnh) ----
            list.Add(new BackupItem { Id = "wifi", Title = "Mật khẩu Wi-Fi đã lưu", Group = BackupScanner.GroupSettings, Default = true, Restore = RestoreKind.WiFi,
                Note = "File xuất ra chứa mật khẩu Wi-Fi dạng chữ thường — giữ bản sao lưu cẩn thận." });
            list.Add(new BackupItem { Id = "apps", Title = "Danh sách phần mềm đang cài (để đối chiếu cài lại)", Group = BackupScanner.GroupSettings, Default = true });
            list.Add(new BackupItem { Id = "printers", Title = "Danh sách máy in và cổng in", Group = BackupScanner.GroupSettings, Default = true });
            list.AddRange(ExtraData.Config(u));

            if (measure) foreach (var b in list.Where(b => b.Source.Length > 0 && b.Bytes == 0)) b.Bytes = Measure(b);

            // ---- Phần mềm kế toán / thuế / BHXH: SQL Server (MISA, ECUS, FAST, Bravo…), HTKK, kê khai BHXH ----
            try { list.AddRange(AppData.Scan(u, measure)); }
            catch (Exception) { /* không đọc được — vẫn sao lưu các mục khác */ }

            // ---- Xếp theo nhóm (thứ tự người dùng hay cần) rồi theo mức quan trọng trong nhóm ----
            list = list.Select((b, i) => (b, i))
                .OrderBy(x => GroupRank(x.b.Group)).ThenBy(x => ItemRank(x.b.Id)).ThenBy(x => x.i)
                .Select(x => x.b).ToList();

            // ---- Mục nằm ở ổ khác ổ sẽ bị xoá (vd Downloads đã chuyển sang E:, SQL để dữ liệu ở D:): vẫn còn sau khi cài lại ----
            var target = targetDrive.TrimEnd('\\', ':') + ":";
            foreach (var b in list.Where(b => b.Source.Length > 2 && !b.Source.StartsWith(target, StringComparison.OrdinalIgnoreCase) && b.Source[1] == ':'))
            {
                b.Default = false;
                b.Note = $"Nằm trên ổ {b.Source.Substring(0, 2)} — ổ này không bị xoá nên không cần sao lưu" +
                         (b.Restore == RestoreKind.UserFolder ? $" (Windows mới dùng thư mục {b.RestoreTo} mặc định trên {target}; dữ liệu cũ vẫn ở {b.Source})." : ".") +
                         (b.Note.Length > 0 ? " " + b.Note : "");
            }
            foreach (var b in list.Where(b => b.InOneDrive))
                b.Note = (b.Note.Length > 0 ? b.Note + " " : "") +
                         "Thư mục nằm trong OneDrive: chỉ sao lưu file đã có trên máy" +
                         (b.CloudFiles > 0 ? $"; {b.CloudFiles} file ({b.CloudBytes / 1048576.0:0} MB) chỉ có trên cloud sẽ bỏ qua — đăng nhập OneDrive trên Windows mới để lấy lại." : ".");
            return list;
        }

        // File giữ chỗ của OneDrive (Files On-Demand) chưa tải về: Offline / RecallOnOpen / RecallOnDataAccess
        private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

        public static bool IsCloudOnly(FileAttributes a) => (a & CloudOnly) != 0;

        /// <summary>Dung lượng sẽ chép (bỏ thư mục loại trừ, liên kết, file chỉ có trên cloud — đếm riêng vào CloudBytes).</summary>
        public static long Measure(BackupItem b)
        {
            long total = 0;
            b.CloudBytes = 0;
            b.CloudFiles = 0;
            var exclude = new HashSet<string>(b.ExcludeDirs, StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(b.Source);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    var patterns = b.Files.Count > 0 ? b.Files : new List<string> { "*" };
                    foreach (var p in patterns)
                        foreach (var f in new DirectoryInfo(dir).GetFiles(p))
                        {
                            if (IsCloudOnly(f.Attributes)) { b.CloudBytes += f.Length; b.CloudFiles++; }
                            else if ((f.Attributes & FileAttributes.ReparsePoint) == 0) total += f.Length;
                        }
                    if (b.Files.Count > 0) continue;   // .pst: chỉ thư mục gốc
                    foreach (var d in new DirectoryInfo(dir).GetDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !exclude.Contains(d.Name)) stack.Push(d.FullName);
                }
                catch { /* thư mục không đọc được */ }
            }
            return total;
        }
    }

    /// <summary>Kết quả sao lưu.</summary>
    public sealed class BackupResult
    {
        public string Dir { get; set; } = "";
        public List<BackupItem> Done { get; } = new List<BackupItem>();
        public List<string> Errors { get; } = new List<string>();
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// Sao lưu bằng robocopy (có sẵn từ Windows 7): chép lại được khi chạy lần hai, bỏ qua liên kết (/XJ),
    /// thử lại 1 lần với file đang bị khoá. Ghi backup.json + ĐỌC-TÔI.txt để biết lấy lại thế nào.
    /// </summary>
    public static class BackupRunner
    {
        /// <param name="keptStagingDir">Thư mục đệm được giữ lại khi xoá ổ Windows (chế độ đệm ngay trên C:) — sao lưu vào trong nó thì được.</param>
        public static BackupResult Run(IRunner run, IEnumerable<BackupItem> items, string destRoot, string targetDrive, Action<string>? progress = null,
            string? keptStagingDir = null)
        {
            var dest = Path.GetFullPath(destRoot).TrimEnd('\\');
            var kept = keptStagingDir != null && dest.StartsWith(Path.GetFullPath(keptStagingDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            if (!kept && string.Equals(Path.GetPathRoot(dest)?.TrimEnd('\\'), targetDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ReinstallException($"Không được sao lưu vào ổ {targetDrive} — ổ này sẽ bị xoá.");
            Directory.CreateDirectory(dest);
            var res = new BackupResult { Dir = dest };
            var logFile = Path.Combine(dest, "robocopy.log");
            foreach (var b in items)
            {
                progress?.Invoke($"Đang sao lưu {b.Title}…");
                var to = Path.Combine(dest, b.Folder);
                try
                {
                    if (b.Sql != null)
                    {
                        var err = AppData.BackupSql(run, b.Sql, to, progress);
                        if (err.Length > 0) { res.Errors.Add($"{b.Title}: {err}"); continue; }
                        res.Done.Add(b);
                        continue;
                    }
                    if (b.Id == ExtraData.FontsId || b.Id == ExtraData.PowerPlansId)
                    {
                        var err = ExtraData.Run(run, b, to);
                        if (err.Length > 0) res.Errors.Add($"{b.Title}: {err}");
                        res.Done.Add(b);   // lỗi lẻ (1 font) vẫn giữ phần đã chép để chép lại được
                        continue;
                    }
                    switch (b.Id)
                    {
                        case "wifi":
                            Directory.CreateDirectory(to);
                            run.Run("netsh.exe", $"wlan export profile key=clear folder=\"{to}\"");
                            break;
                        case "apps":
                            Directory.CreateDirectory(to);
                            run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                                "$k='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*';" +
                                "Get-ItemProperty $k -ErrorAction SilentlyContinue | ?{ $_.DisplayName -and -not $_.SystemComponent } | " +
                                "Sort-Object DisplayName -Unique | Select DisplayName,DisplayVersion,Publisher | " +
                                $"Export-Csv -NoTypeInformation -Encoding UTF8 '{Path.Combine(to, "phan-mem.csv").Replace("'", "''")}'\"");
                            break;
                        case "printers":
                            Directory.CreateDirectory(to);
                            run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                                "Get-WmiObject Win32_Printer | Select Name,DriverName,PortName,ShareName,Default | " +
                                $"Format-Table -AutoSize | Out-String -Width 300 | Set-Content -Encoding UTF8 '{Path.Combine(to, "may-in.txt").Replace("'", "''")}'\"");
                            break;
                        default:
                            var args = new StringBuilder($"\"{b.Source}\" \"{to}\" ");
                            args.Append(b.Files.Count > 0 ? string.Join(" ", b.Files.Select(f => $"\"{f}\"")) : "*.*");
                            if (b.Files.Count == 0) args.Append(" /E");
                            if (b.ExcludeDirs.Count > 0) args.Append(" /XD ").Append(string.Join(" ", b.ExcludeDirs.Select(d => $"\"{d}\"")));
                            // /XA:O: bỏ file chỉ có trên OneDrive (không ép tải về — vẫn còn trên cloud)
                            args.Append($" /COPY:DAT /DCOPY:T /XJ /XA:O /R:1 /W:1 /MT:8 /NP /NFL /NDL /UNILOG+:\"{logFile}\"");
                            var r = run.Run("robocopy.exe", args.ToString());
                            // robocopy: 0–7 là thành công (có / không có file mới, có file thừa…), ≥ 8 là có file chép lỗi
                            if (r.ExitCode >= 8) { res.Errors.Add($"{b.Title}: một số file không chép được (xem robocopy.log)"); continue; }
                            break;
                    }
                    res.Done.Add(b);
                }
                catch (Exception ex) { res.Errors.Add($"{b.Title}: {ex.Message}"); }
            }
            File.WriteAllText(Path.Combine(dest, "backup.json"), Manifest(res.Done), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dest, "ĐỌC-TÔI.txt"), Readme(res), new UTF8Encoding(true));
            return res;
        }

        private static string Manifest(IEnumerable<BackupItem> done) =>
            "[" + string.Join(",", done.Select(b =>
                $"\r\n  {{ \"id\": {PostInstallBuilder.Q(b.Id)}, \"title\": {PostInstallBuilder.Q(b.Title)}, \"source\": {PostInstallBuilder.Q(b.Source)}, " +
                $"\"restore\": {PostInstallBuilder.Q(b.Restore.ToString())}, \"restoreTo\": {PostInstallBuilder.Q(b.RestoreTo)} }}")) + "\r\n]\r\n";

        private static string Readme(BackupResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Bản sao lưu trước khi cài lại Windows — {DateTime.Now:dd/MM/yyyy HH:mm}");
            sb.AppendLine();
            foreach (var b in r.Done)
            {
                sb.AppendLine($"• {b.Folder}\\ — {b.Title}" + (b.Source.Length > 0 ? $" (từ {b.Source})" : ""));
                if (b.Note.Length > 0) sb.AppendLine("    " + b.Note);
            }
            if (r.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Lỗi:");
                foreach (var e in r.Errors) sb.AppendLine("  - " + e);
            }
            return sb.ToString();
        }
    }
}
