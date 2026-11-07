using System.IO;
using System.Security.Cryptography;
using System.Text;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Backup;

/// <summary>Một phiên bản SQL Server cài trên máy (MISA, ECUS, FAST, Bravo… đều cài kèm SQL Server riêng hoặc dùng chung).</summary>
public sealed record SqlInstance(string Name, string Id, string Service, bool Running, string DataDir, string BackupDir)
{
    public bool IsDefault => Name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
    public string Display => IsDefault ? "(mặc định)" : Name;
    public string Server => IsDefault ? "lpc:(local)" : $@"lpc:(local)\{Name}";   // shared memory: chỉ trong máy, không cần TCP / SQL Browser
}

public enum BackupKind { Database, Folder, NeedLogin, Stopped, Error }

/// <summary>Một mục có thể sao lưu (cơ sở dữ liệu / thư mục) hoặc một dòng báo trạng thái phiên bản SQL (cần đăng nhập, đang tắt…).</summary>
public sealed record BackupItem(BackupKind Kind, string App, string Name, string Detail, long Size, SqlInstance? Instance = null, string Path = "")
{
    public bool CanBackup => Kind is BackupKind.Database or BackupKind.Folder;
}

/// <summary>
/// Sao lưu dữ liệu phần mềm kế toán / hải quan / thuế trước khi cài lại Windows:
/// cơ sở dữ liệu SQL Server (MISA SME, ECUS, FAST, Bravo…) bằng BACKUP DATABASE … COPY_ONLY + RESTORE VERIFYONLY,
/// và thư mục HTKK. Chỉ sao lưu, không khôi phục — khôi phục bằng chức năng của từng phần mềm.
/// </summary>
public static class AppBackupService
{
    private const string SqlKey = @"SOFTWARE\Microsoft\Microsoft SQL Server";

    // ================= Tìm SQL Server =================
    public static List<SqlInstance> Instances()
    {
        var list = new List<SqlInstance>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var names = hklm.OpenSubKey($@"{SqlKey}\Instance Names\SQL");
            if (names is null) continue;
            foreach (var name in names.GetValueNames())
            {
                if (list.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                var id = names.GetValue(name) as string ?? "";
                using var setup = hklm.OpenSubKey($@"{SqlKey}\{id}\Setup");
                using var srv = hklm.OpenSubKey($@"{SqlKey}\{id}\MSSQLServer");
                var root = setup?.GetValue("SQLDataRoot") as string ?? "";
                var service = name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "MSSQLSERVER" : $"MSSQL${name}";
                var state = Wmi.First($"SELECT State FROM Win32_Service WHERE Name = '{service.Replace("'", "")}'")?.Str("State") ?? "";
                list.Add(new SqlInstance(name, id, service, state == "Running",
                    root.Length > 0 ? System.IO.Path.Combine(root, "DATA") : "",
                    srv?.GetValue("BackupDirectory") as string ?? (root.Length > 0 ? System.IO.Path.Combine(root, "Backup") : "")));
            }
        }
        return list.OrderBy(i => i.Name).ToList();
    }

    /// <summary>Đoán phần mềm theo tên phiên bản SQL / tên cơ sở dữ liệu.</summary>
    public static string AppOf(string instance, string db)
    {
        var s = $"{instance} {db}";
        if (s.Contains("MISA", StringComparison.OrdinalIgnoreCase)) return "MISA";
        if (s.Contains("ECUS", StringComparison.OrdinalIgnoreCase)) return "ECUS";
        if (s.Contains("FAST", StringComparison.OrdinalIgnoreCase)) return "FAST";
        if (s.Contains("BRAVO", StringComparison.OrdinalIgnoreCase)) return "Bravo";
        return "SQL Server";
    }

    private static Dictionary<string, string> Env(SqlInstance i, (string User, string Password)? login, params (string K, string V)[] extra)
    {
        var env = new Dictionary<string, string> { ["ITHD_SRV"] = i.Server, ["ITHD_USER"] = login?.User ?? "", ["ITHD_PW"] = login?.Password ?? "" };
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    // Kết nối: tài khoản Windows (app chạy quyền Administrator) hoặc tài khoản SQL (vd. sa) truyền qua biến môi trường
    private const string Connect = """
        Add-Type -AssemblyName System.Data
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $b['Data Source'] = $env:ITHD_SRV; $b['Initial Catalog'] = 'master'; $b['Connect Timeout'] = 15
        if ($env:ITHD_USER) { $b['User ID'] = $env:ITHD_USER; $b['Password'] = $env:ITHD_PW } else { $b['Integrated Security'] = $true }
        $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
        $c.Open()
        $q = $c.CreateCommand(); $q.CommandTimeout = 0

        """;

    /// <summary>Đọc danh sách cơ sở dữ liệu của một phiên bản SQL. Không đăng nhập được thì trả một dòng NeedLogin.</summary>
    public static async Task<List<BackupItem>> DatabasesAsync(SqlInstance i, (string User, string Password)? login = null)
    {
        if (!i.Running)
            return [new BackupItem(BackupKind.Stopped, AppOf(i.Name, ""), $"SQL Server {i.Display}", "Dịch vụ SQL Server đang tắt — bật lên để đọc danh sách cơ sở dữ liệu.", 0, i)];
        const string script = Connect + """
            try {
              $q.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(ISNULL(SERVERPROPERTY('InstanceDefaultBackupPath'), '') AS nvarchar(512))"
              $r = $q.ExecuteReader(); [void]$r.Read(); "S`t$($r[0])`t$($r[1])`t$($r[2])"; $r.Close()
              $q.CommandText = "SELECT d.name, d.state_desc, SUM(CAST(f.size AS bigint)) * 8192 FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id WHERE d.database_id > 4 GROUP BY d.name, d.state_desc ORDER BY d.name"
              $r = $q.ExecuteReader(); while ($r.Read()) { "D`t$($r[0])`t$($r[1])`t$($r[2])" }; $r.Close()
            } finally { $c.Close() }
            """;
        var r = await ProcessRunner.PowerShellAsync(script, env: Env(i, login));
        if (!r.Success)
        {
            var err = OpResult.FirstLine(r.Error.Length > 0 ? r.Error : r.Text);
            var needLogin = err.Contains("Login failed", StringComparison.OrdinalIgnoreCase) || err.Contains("18456") || err.Contains("đăng nhập", StringComparison.OrdinalIgnoreCase);
            return [new BackupItem(needLogin ? BackupKind.NeedLogin : BackupKind.Error, AppOf(i.Name, ""), $"SQL Server {i.Display}",
                needLogin ? "Tài khoản Windows không có quyền vào SQL Server này — bấm “Đăng nhập SQL…” bằng tài khoản SQL (vd. sa) của phần mềm."
                          : $"Không đọc được danh sách cơ sở dữ liệu: {err}", 0, i)];
        }
        var items = new List<BackupItem>();
        var ver = "";
        var inst = i;
        foreach (var line in r.Output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var p = line.Split('\t');
            if (p[0] == "S" && p.Length >= 4)
            {
                ver = $"SQL Server {p[1]} {p[2]}".Trim();
                if (p[3].Trim().Length > 0) inst = i with { BackupDir = p[3].Trim() };
            }
            else if (p[0] == "D" && p.Length >= 4)
            {
                long.TryParse(p[3], out var size);
                var online = p[2] == "ONLINE";
                items.Add(new BackupItem(online ? BackupKind.Database : BackupKind.Error, AppOf(i.Name, p[1]), p[1],
                    $"SQL Server {i.Display} · {ver}" + (online ? "" : $" · trạng thái {p[2]} — không sao lưu được"), size, inst));
            }
        }
        return items;   // script in dòng S (phiên bản, thư mục backup) trước các dòng D
    }

    // ================= Tìm HTKK =================
    public static List<BackupItem> Folders()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var b = RegistryKey.OpenBaseKey(hive, view);
            using var un = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (un is null) continue;
            foreach (var n in un.GetSubKeyNames())
            {
                using var k = un.OpenSubKey(n);
                var name = k?.GetValue("DisplayName") as string ?? "";
                if (!name.Contains("HTKK", StringComparison.OrdinalIgnoreCase) && !name.Contains("Hỗ trợ kê khai", StringComparison.OrdinalIgnoreCase)) continue;
                var loc = (k?.GetValue("InstallLocation") as string ?? "").Trim('"', ' ');
                if (loc.Length == 0 && k?.GetValue("DisplayIcon") is string icon) loc = System.IO.Path.GetDirectoryName(icon.Split(',')[0].Trim('"')) ?? "";
                if (loc.Length > 0 && Directory.Exists(loc)) dirs.Add(loc.TrimEnd('\\'));
            }
        }
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\" })
        {
            var p = System.IO.Path.Combine(d, "HTKK");
            if (Directory.Exists(p)) dirs.Add(p);
        }
        return dirs.Select(d => new BackupItem(BackupKind.Folder, "HTKK", System.IO.Path.GetFileName(d),
            "Sao lưu nguyên thư mục cài đặt HTKK (gồm tờ khai đã lập theo từng mã số thuế).", FolderSize(d), Path: d)).ToList();
    }

    private static long FolderSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    // ================= Sao lưu =================
    /// <summary>Thư mục sao lưu của lần chạy: &lt;nơi chọn&gt;\SaoLuu-&lt;tên máy&gt;-&lt;ngày giờ&gt;.</summary>
    public static string NewSetDir(string root) => System.IO.Path.Combine(root, $"SaoLuu-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}");

    /// <summary>
    /// BACKUP DATABASE … COPY_ONLY (không làm hỏng chuỗi sao lưu sẵn có của phần mềm) vào thư mục Backup của chính SQL Server
    /// (tài khoản dịch vụ SQL có thể không ghi được ra USB / ổ khác), RESTORE VERIFYONLY, rồi chép sang nơi lưu.
    /// </summary>
    public static async Task<(OpResult Result, string File, string Sha256)> BackupDatabaseAsync(BackupItem item, string setDir, (string User, string Password)? login)
    {
        var i = item.Instance!;
        if (i.BackupDir.Length == 0) return (OpResult.Fail("không xác định được thư mục Backup của SQL Server"), "", "");
        var safe = string.Concat(item.Name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var tmp = System.IO.Path.Combine(i.BackupDir, $"ITHD-{safe}-{DateTime.Now:yyyyMMddHHmmss}.bak");
        const string script = Connect + """
            try {
              $q.CommandText = "BACKUP DATABASE " + $env:ITHD_DB + " TO DISK = @p WITH COPY_ONLY, INIT, CHECKSUM"
              [void]$q.Parameters.AddWithValue('@p', $env:ITHD_FILE)
              [void]$q.ExecuteNonQuery()
              $q.CommandText = "RESTORE VERIFYONLY FROM DISK = @p WITH CHECKSUM"
              [void]$q.ExecuteNonQuery()
              'OK'
            } finally { $c.Close() }
            """;
        var r = await ProcessRunner.PowerShellAsync(script, env: Env(i, login, ("ITHD_DB", "[" + item.Name.Replace("]", "]]") + "]"), ("ITHD_FILE", tmp)));
        if (!r.Success || !r.Output.Contains("OK"))
        {
            TryDelete(tmp);
            return (OpResult.Fail(OpResult.FirstLine(r.Error.Length > 0 ? r.Error : r.Text)), "", "");
        }
        try
        {
            var dir = System.IO.Path.Combine(setDir, "SQL", i.IsDefault ? "MSSQLSERVER" : i.Name);
            Directory.CreateDirectory(dir);
            var dest = System.IO.Path.Combine(dir, $"{safe}.bak");
            await Task.Run(() => File.Copy(tmp, dest, overwrite: true));
            string hash;
            await using (var f = File.OpenRead(dest)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f));
            return (OpResult.Success($"{item.Name}: {Fmt.Size(new FileInfo(dest).Length)}, đã kiểm tra đọc lại được"), dest, hash);
        }
        finally { TryDelete(tmp); }
    }

    public static async Task<OpResult> BackupFolderAsync(BackupItem item, string setDir)
    {
        var dest = System.IO.Path.Combine(setDir, item.App, item.Name);
        var errors = 0;
        await Task.Run(() =>
        {
            foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var target = System.IO.Path.Combine(dest, System.IO.Path.GetRelativePath(item.Path, f));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    File.Copy(f, target, overwrite: true);
                }
                catch { errors++; }
            }
        });
        return errors == 0 ? OpResult.Success($"{item.App} ({item.Path}): đã chép {Fmt.Size(item.Size)}")
                           : OpResult.Fail($"{item.App} ({item.Path}): {errors} file không chép được (đang bị phần mềm mở?) — đóng HTKK rồi sao lưu lại");
    }

    /// <summary>File ghi chú đi kèm bộ sao lưu: cần biết phiên bản SQL Server để khôi phục (chỉ khôi phục được vào bản cùng hoặc mới hơn).</summary>
    public static void WriteManifest(string setDir, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SAO LƯU DỮ LIỆU PHẦN MỀM (IT Helpdesk Tool)");
        sb.AppendLine($"Máy: {Environment.MachineName} · Người sao lưu: {Environment.UserName} · Lúc: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Khôi phục:");
        sb.AppendLine("- File .bak: dùng chức năng khôi phục / phục hồi dữ liệu của chính phần mềm (MISA, ECUS, FAST…),");
        sb.AppendLine("  hoặc SQL Server Management Studio. Chỉ khôi phục được vào SQL Server CÙNG PHIÊN BẢN HOẶC MỚI HƠN bản ghi bên dưới.");
        sb.AppendLine("- Thư mục HTKK: cài lại HTKK rồi chép đè thư mục đã sao lưu vào thư mục cài đặt (đóng HTKK trước).");
        sb.AppendLine("- Mở thử vài chứng từ / tờ khai cũ trước khi xoá bản sao lưu.");
        sb.AppendLine(new string('=', 78));
        foreach (var l in lines) sb.AppendLine(l);
        File.WriteAllText(System.IO.Path.Combine(setDir, "DANH-SACH-SAO-LUU.txt"), sb.ToString(), new UTF8Encoding(true));
    }

    public static Task<OpResult> StartServiceAsync(SqlInstance i) =>
        ProcessRunner.PowerShellAsync($"Start-Service -Name {ProcessRunner.PsQuote(i.Service)}")
            .ContinueWith(t => OpResult.From(t.Result, $"Đã bật dịch vụ {i.Service}.", "Không bật được dịch vụ"));

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
}
