using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Một phiên bản SQL Server trên máy (MISA, ECUS, FAST, Bravo, phần mềm BHXH… cài kèm SQL Server riêng hoặc dùng chung).</summary>
    public sealed class SqlInstanceInfo
    {
        public string Name { get; set; } = "";
        public string Service { get; set; } = "";
        /// <summary>Thư mục chứa file dữ liệu (.mdf / .ndf / .ldf): DATA mặc định, DefaultData, DefaultLog.</summary>
        public List<string> DataDirs { get; set; } = new List<string>();
        public string BackupDir { get; set; } = "";
        public bool IsDefault => Name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
        /// <summary>Kết nối shared memory: chỉ trong máy, không cần TCP / SQL Browser.</summary>
        public string Server => IsDefault ? "lpc:(local)" : $@"lpc:(local)\{Name}";
    }

    /// <summary>
    /// Dữ liệu phần mềm kế toán / thuế / hải quan / BHXH (cùng nguồn với trang "Sao lưu phần mềm"):
    /// cơ sở dữ liệu SQL Server và thư mục HTKK, phần mềm kê khai BHXH. Chỉ sao lưu — cài lại phần mềm rồi khôi phục bằng chức năng của nó.
    /// </summary>
    public static class AppData
    {
        public const string Group = "Phần mềm kế toán, thuế, BHXH";
        public const string SqlPrefix = "sql-";
        private const string SqlKey = @"SOFTWARE\Microsoft\Microsoft SQL Server";
        private static readonly string[] DbFiles = { "*.mdf", "*.ndf", "*.ldf" };

        /// <summary>Đoán phần mềm theo tên phiên bản SQL.</summary>
        public static string AppOf(string instance)
        {
            if (instance.IndexOf("MISA", StringComparison.OrdinalIgnoreCase) >= 0) return "MISA";
            if (instance.IndexOf("ECUS", StringComparison.OrdinalIgnoreCase) >= 0) return "ECUS";
            if (instance.IndexOf("FAST", StringComparison.OrdinalIgnoreCase) >= 0) return "FAST";
            if (instance.IndexOf("BRAVO", StringComparison.OrdinalIgnoreCase) >= 0) return "Bravo";
            if (Regex.IsMatch(instance, @"BHXH|(?<![A-Za-z])(EBH|IBH|TS24)(?![A-Za-z])", RegexOptions.IgnoreCase)) return "BHXH";
            return "";
        }

        public static List<SqlInstanceInfo> SqlInstances()
        {
            var list = new List<SqlInstanceInfo>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var names = hklm.OpenSubKey($@"{SqlKey}\Instance Names\SQL");
                    if (names == null) continue;
                    foreach (var name in names.GetValueNames())
                    {
                        if (list.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        var id = names.GetValue(name) as string ?? "";
                        using var setup = hklm.OpenSubKey($@"{SqlKey}\{id}\Setup");
                        using var srv = hklm.OpenSubKey($@"{SqlKey}\{id}\MSSQLServer");
                        var root = setup?.GetValue("SQLDataRoot") as string ?? "";
                        var dirs = new[] { root.Length > 0 ? Path.Combine(root, "DATA") : "", srv?.GetValue("DefaultData") as string ?? "", srv?.GetValue("DefaultLog") as string ?? "" }
                            .Select(d => d.Trim().TrimEnd('\\')).Where(d => d.Length > 3 && Directory.Exists(d))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        list.Add(new SqlInstanceInfo
                        {
                            Name = name,
                            Service = name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "MSSQLSERVER" : "MSSQL$" + name,
                            DataDirs = dirs,
                            BackupDir = srv?.GetValue("BackupDirectory") as string ?? (root.Length > 0 ? Path.Combine(root, "Backup") : ""),
                        });
                    }
                }
                catch (Exception) { /* không đọc được registry của SQL */ }
            }
            return list.OrderBy(i => i.Name).ToList();
        }

        /// <summary>Các mục sao lưu dữ liệu phần mềm (SQL Server + thư mục HTKK / BHXH).</summary>
        public static List<BackupItem> Scan(UserPaths u, bool measure)
        {
            var list = new List<BackupItem>();
            foreach (var i in SqlInstances())
            {
                var app = AppOf(i.Name);
                var item = new BackupItem
                {
                    Id = SqlPrefix + Safe(i.IsDefault ? "MSSQLSERVER" : i.Name),
                    Title = $"SQL Server {(i.IsDefault ? "(mặc định)" : i.Name)}" + (app.Length > 0 ? $" — dữ liệu {app}" : " (MISA, ECUS, FAST, Bravo… dùng SQL Server)"),
                    Group = Group, Default = true, Sql = i,
                    Source = i.DataDirs.FirstOrDefault() ?? "",
                    Files = DbFiles.ToList(),
                    Note = "Sao lưu từng cơ sở dữ liệu ra file .bak (khôi phục bằng chức năng của phần mềm), và chép nguyên file dữ liệu .mdf/.ldf " +
                           "(tạm dừng dịch vụ SQL Server vài phút — đóng phần mềm kế toán trước). Cài lại phần mềm rồi khôi phục từ thư mục sao lưu.",
                };
                if (measure) item.Bytes = i.DataDirs.Sum(d => FilesSize(d, DbFiles));
                list.Add(item);
            }
            foreach (var f in Folders(u))
            {
                if (measure) f.Bytes = BackupScanner.Measure(f);
                list.Add(f);
            }
            return list;
        }

        // ================= Phần mềm lưu dữ liệu dạng thư mục (HTKK, kê khai BHXH) =================
        private sealed class FolderApp
        {
            public FolderApp(string app, string pattern, string note) { App = app; Name = new Regex(pattern, RegexOptions.IgnoreCase); Note = note; }
            public string App { get; }
            public Regex Name { get; }
            public string Note { get; }
        }

        private static readonly FolderApp[] FolderApps =
        {
            new FolderApp("HTKK", @"HTKK|Hỗ trợ kê khai", "tờ khai thuế đã lập theo từng mã số thuế"),
            new FolderApp("BHXH", @"BHXH|(?<![A-Za-z])(eBH|IBH|TS24)(?![A-Za-z])|Bảo hiểm xã hội|Bao hiem xa hoi", "hồ sơ kê khai bảo hiểm xã hội"),
        };

        internal static List<BackupItem> Folders(UserPaths u)
        {
            var found = new List<(string App, string Dir, string Where)>();
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            void Add(string app, string dir, string where)
            {
                dir = dir.Trim('"', ' ').TrimEnd('\\');
                if (dir.Length < 4 || !Directory.Exists(dir)) return;
                if (dir.Equals(pf, StringComparison.OrdinalIgnoreCase) || dir.Equals(pf86, StringComparison.OrdinalIgnoreCase) || dir.Equals(win, StringComparison.OrdinalIgnoreCase)) return;
                if (found.Any(x => dir.Equals(x.Dir, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(x.Dir + "\\", StringComparison.OrdinalIgnoreCase))) return;
                found.RemoveAll(x => x.Dir.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase));
                found.Add((app, dir, where));
            }

            // 1. Danh sách phần mềm đã cài
            foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
            {
                try
                {
                    using var b = RegistryKey.OpenBaseKey(hive, view);
                    using var un = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (un == null) continue;
                    foreach (var n in un.GetSubKeyNames())
                    {
                        using var k = un.OpenSubKey(n);
                        var name = k?.GetValue("DisplayName") as string ?? "";
                        var app = FolderApps.FirstOrDefault(a => a.Name.IsMatch(name));
                        if (app == null) continue;
                        var loc = (k?.GetValue("InstallLocation") as string ?? "").Trim('"', ' ');
                        if (loc.Length == 0 && k?.GetValue("DisplayIcon") is string icon) loc = Path.GetDirectoryName(icon.Split(',')[0].Trim('"')) ?? "";
                        if (loc.Length > 0) Add(app.App, loc, $"thư mục cài đặt {name}");
                    }
                }
                catch (Exception) { }
            }
            // 2. Vị trí mặc định của HTKK
            foreach (var d in new[] { pf86, pf, @"C:\" })
                if (d.Length > 0) Add("HTKK", Path.Combine(d, "HTKK"), "thư mục cài đặt HTKK");
            // 3. Thư mục dữ liệu (cấp 1) trong ProgramData / AppData / ổ C: có tên phần mềm
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), u.Roaming, u.Local, @"C:\" })
            {
                if (root.Length == 0) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(root); } catch (Exception) { continue; }
                foreach (var d in subs)
                {
                    var app = FolderApps.FirstOrDefault(a => a.Name.IsMatch(Path.GetFileName(d)));
                    if (app != null) Add(app.App, d, $"thư mục dữ liệu trong {root.TrimEnd('\\')}");
                }
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return found.Select(x =>
            {
                var note = FolderApps.First(a => a.App == x.App).Note;
                // HTKK: dữ liệu nằm trong DataFiles (chia theo mã số thuế) — giống chức năng sao lưu của chính HTKK; phần còn lại cài lại là có
                var data = Path.Combine(x.Dir, "DataFiles");
                var onlyData = x.App == "HTKK" && Directory.Exists(data);
                var dir = onlyData ? data : x.Dir;
                var id = x.App.ToLowerInvariant() + "-" + Safe(Path.GetFileName(x.Dir));
                for (var i = 2; !ids.Add(id); i++) id = x.App.ToLowerInvariant() + "-" + Safe(Path.GetFileName(x.Dir)) + "-" + i;
                return new BackupItem
                {
                    Id = id, Group = Group, Default = true, Source = dir,
                    Title = x.App == "HTKK" ? "HTKK — tờ khai thuế" : "Phần mềm kê khai BHXH",
                    Note = (onlyData ? $"Chép thư mục DataFiles của {x.Where} ({note}) — giống chức năng sao lưu của HTKK. " : $"Chép nguyên {x.Where} ({note}). ") +
                           $"Khôi phục: cài lại phần mềm, đóng phần mềm, chép thư mục đã sao lưu đè vào {dir}.",
                };
            }).ToList();
        }

        // ================= Sao lưu SQL Server =================
        /// <summary>
        /// 1) BACKUP DATABASE … COPY_ONLY từng cơ sở dữ liệu (tài khoản Windows đang chạy — Administrator) vào thư mục Backup của SQL Server
        ///    rồi chuyển sang <paramref name="to"/>\bak (tài khoản dịch vụ SQL có thể không ghi được ra ổ khác). Không có quyền thì bỏ qua bước này.
        /// 2) Tạm dừng dịch vụ, chép nguyên file .mdf / .ndf / .ldf sang <paramref name="to"/>\files, bật lại dịch vụ — luôn làm được, không cần mật khẩu SQL.
        /// </summary>
        /// <returns>Lỗi (rỗng = ổn). Có .bak hoặc có file dữ liệu là coi như sao lưu được.</returns>
        public static string BackupSql(IRunner run, SqlInstanceInfo i, string to, Action<string>? progress)
        {
            Directory.CreateDirectory(to);
            var report = new StringBuilder();
            report.AppendLine($"SQL Server {(i.IsDefault ? "(mặc định)" : i.Name)} — dịch vụ {i.Service}");

            // ---- 1. File .bak ----
            var bakCount = 0;
            if (i.BackupDir.Length > 0 && IsRunning(run, i.Service))
            {
                progress?.Invoke($"SQL Server {i.Name}: sao lưu từng cơ sở dữ liệu ra file .bak…");
                var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
                                                  Convert.ToBase64String(Encoding.Unicode.GetBytes(BakScript(i))));
                var bakDir = Path.Combine(to, "bak");
                foreach (var line in r.Output.Split('\n').Select(l => l.Trim()))
                {
                    if (line.StartsWith("BAK=", StringComparison.Ordinal))
                    {
                        var p = line.Substring(4).Split('|');
                        if (p.Length < 2 || !File.Exists(p[1])) continue;
                        Directory.CreateDirectory(bakDir);
                        var dest = Path.Combine(bakDir, Safe(p[0]) + ".bak");
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Copy(p[1], dest);
                        try { File.Delete(p[1]); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        bakCount++;
                        report.AppendLine($"  .bak  {p[0]} → bak\\{Path.GetFileName(dest)} (đã kiểm tra đọc lại được)");
                    }
                    else if (line.StartsWith("ERR=", StringComparison.Ordinal)) report.AppendLine("  lỗi  " + line.Substring(4));
                }
                if (bakCount == 0) report.AppendLine("  Không tạo được file .bak (tài khoản Windows không có quyền vào SQL Server) — dùng bản chép file dữ liệu bên dưới.");
            }

            // ---- 2. Chép nguyên file dữ liệu (dừng dịch vụ để file nhất quán) ----
            var copied = 0;
            if (i.DataDirs.Count > 0)
            {
                var wasRunning = IsRunning(run, i.Service);
                if (wasRunning)
                {
                    progress?.Invoke($"SQL Server {i.Name}: tạm dừng dịch vụ để chép file dữ liệu…");
                    run.Run("net.exe", $"stop \"{i.Service}\" /y");
                }
                try
                {
                    for (var n = 0; n < i.DataDirs.Count; n++)
                    {
                        var dest = Path.Combine(to, "files", n == 0 ? "DATA" : "DATA" + (n + 1));
                        var r = run.Run("robocopy.exe", $"\"{i.DataDirs[n]}\" \"{dest}\" " + string.Join(" ", DbFiles.Select(f => $"\"{f}\"")) +
                                                        " /COPY:DAT /R:1 /W:1 /NP /NFL /NDL");
                        if (r.ExitCode < 8) { copied++; report.AppendLine($"  file  {i.DataDirs[n]} → files\\{Path.GetFileName(dest)}"); }
                        else report.AppendLine($"  lỗi  chép {i.DataDirs[n]} (robocopy mã {r.ExitCode})");
                    }
                }
                finally
                {
                    if (wasRunning)
                    {
                        progress?.Invoke($"SQL Server {i.Name}: bật lại dịch vụ…");
                        run.Run("net.exe", $"start \"{i.Service}\"");
                    }
                }
            }
            File.WriteAllText(Path.Combine(to, "ĐỌC-TÔI.txt"), report + RestoreHelp, new UTF8Encoding(true));
            return bakCount > 0 || copied > 0 ? "" : "không sao lưu được cơ sở dữ liệu nào (xem ĐỌC-TÔI.txt trong thư mục sao lưu)";
        }

        private const string RestoreHelp =
            "\r\nKhôi phục:\r\n" +
            "- Cách 1 (file .bak trong thư mục bak): cài lại phần mềm (MISA, ECUS, FAST…), dùng chức năng Khôi phục / Phục hồi dữ liệu của phần mềm,\r\n" +
            "  hoặc SQL Server Management Studio → Restore Database. Chỉ khôi phục được vào SQL Server CÙNG PHIÊN BẢN HOẶC MỚI HƠN.\r\n" +
            "- Cách 2 (file .mdf/.ldf trong thư mục files): cài lại SQL Server cùng tên phiên bản, rồi Attach cơ sở dữ liệu trong\r\n" +
            "  SQL Server Management Studio (hoặc dừng dịch vụ, chép đè cả thư mục DATA rồi bật lại — chỉ khi cùng phiên bản SQL Server).\r\n" +
            "- Mở thử vài chứng từ cũ trước khi xoá bản sao lưu.\r\n";

        private static bool IsRunning(IRunner run, string service) =>
            run.Run("sc.exe", $"query \"{service}\"").Output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Script PowerShell (chạy được với PowerShell 2.0 của Windows 7): in BAK=&lt;db&gt;|&lt;file&gt; hoặc ERR=&lt;db&gt;|&lt;lỗi&gt;.</summary>
        internal static string BakScript(SqlInstanceInfo i)
        {
            var dir = i.BackupDir.Replace("'", "''");
            var srv = i.Server.Replace("'", "''");
            return "$ErrorActionPreference='Stop'\n" +
                   "Add-Type -AssemblyName System.Data\n" +
                   $"$c = New-Object System.Data.SqlClient.SqlConnection 'Data Source={srv};Initial Catalog=master;Integrated Security=True;Connect Timeout=15'\n" +
                   "try { $c.Open() } catch { 'ERR=*|' + $_.Exception.Message; exit 0 }\n" +
                   "$q = $c.CreateCommand(); $q.CommandTimeout = 0\n" +
                   "$q.CommandText = 'SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0'\n" +
                   "$r = $q.ExecuteReader(); $dbs = @(); while ($r.Read()) { $dbs += [string]$r[0] }; $r.Close()\n" +
                   "foreach ($d in $dbs) {\n" +
                   "  $safe = ($d -replace '[^A-Za-z0-9_.-]', '_')\n" +
                   $"  $f = Join-Path '{dir}' ('ITH-' + $safe + '.bak')\n" +
                   "  try {\n" +
                   "    $q.Parameters.Clear(); [void]$q.Parameters.AddWithValue('@p', $f)\n" +
                   "    $q.CommandText = 'BACKUP DATABASE [' + $d.Replace(']', ']]') + '] TO DISK = @p WITH COPY_ONLY, INIT, CHECKSUM'; [void]$q.ExecuteNonQuery()\n" +
                   "    $q.CommandText = 'RESTORE VERIFYONLY FROM DISK = @p WITH CHECKSUM'; [void]$q.ExecuteNonQuery()\n" +
                   "    'BAK=' + $d + '|' + $f\n" +
                   "  } catch { 'ERR=' + $d + '|' + $_.Exception.Message }\n" +
                   "}\n" +
                   "$c.Close()\n";
        }

        private static long FilesSize(string dir, IEnumerable<string> patterns)
        {
            try { return patterns.SelectMany(p => new DirectoryInfo(dir).GetFiles(p)).Sum(f => f.Length); }
            catch (Exception) { return 0; }
        }

        internal static string Safe(string s) => Regex.Replace(s, @"[^A-Za-z0-9_.-]", "_");
    }
}
