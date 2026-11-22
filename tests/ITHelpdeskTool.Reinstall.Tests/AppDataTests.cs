using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

public class AppDataTests
{
    [Theory]
    [InlineData("MISASME2023", "MISA")]
    [InlineData("ECUS5VNACCS", "ECUS")]
    [InlineData("FAST2008", "FAST")]
    [InlineData("TS24", "BHXH")]
    [InlineData("SQLEXPRESS", "")]
    public void Guesses_app_from_instance(string instance, string app) => Assert.Equal(app, AppData.AppOf(instance));

    [Fact]
    public void Bak_script_is_copy_only_verified_and_powershell2_safe()
    {
        var s = AppData.BakScript(new SqlInstanceInfo { Name = "MISASME2023", BackupDir = @"C:\SQL\Back'up" });
        Assert.Contains(@"lpc:(local)\MISASME2023", s);
        Assert.Contains("COPY_ONLY", s);
        Assert.Contains("RESTORE VERIFYONLY", s);
        Assert.Contains(@"C:\SQL\Back''up", s);          // dấu nháy đơn trong đường dẫn được nhân đôi
        Assert.DoesNotContain("[pscustomobject]", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Sqlcmd", s);
    }

    [Fact]
    public void Sql_backup_moves_bak_copies_files_and_restarts_service()
    {
        var root = Path.Combine(Path.GetTempPath(), "ith-sql-" + Guid.NewGuid().ToString("N"));
        var sqlBackup = Directory.CreateDirectory(Path.Combine(root, "sqlbackup")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(root, "DATA")).FullName;
        var to = Path.Combine(root, "out");
        var bak = Path.Combine(sqlBackup, "ITH-ketoan.bak");
        File.WriteAllText(bak, "bak");
        var calls = new List<string>();
        var stopped = false;
        var run = new FakeRunner((f, a) =>
        {
            calls.Add(f + " " + a);
            return f switch
            {
                "sc.exe" => new RunResult(0, stopped ? "STATE : 1 STOPPED" : "STATE : 4 RUNNING"),
                "powershell.exe" => new RunResult(0, $"BAK=ketoan|{bak}\nERR=hong|Login failed\n"),
                "net.exe" when a.StartsWith("stop") => Stop(),
                "robocopy.exe" => new RunResult(1, ""),
                _ => new RunResult(0, ""),
            };
            RunResult Stop() { stopped = true; return new RunResult(0, ""); }
        });
        var i = new SqlInstanceInfo { Name = "MISASME2023", Service = "MSSQL$MISASME2023", BackupDir = sqlBackup, DataDirs = { data } };

        var err = AppData.BackupSql(run, i, to, null);

        Assert.Equal("", err);
        Assert.True(File.Exists(Path.Combine(to, "bak", "ketoan.bak")));
        Assert.False(File.Exists(bak));                                     // file tạm trong thư mục Backup của SQL đã dọn
        Assert.Contains(calls, c => c.StartsWith("net.exe stop \"MSSQL$MISASME2023\""));
        Assert.Contains(calls, c => c.StartsWith("net.exe start \"MSSQL$MISASME2023\""));   // bật lại như cũ
        Assert.Contains(calls, c => c.StartsWith("robocopy.exe") && c.Contains("*.mdf") && c.Contains("*.ldf"));
        var readme = File.ReadAllText(Path.Combine(to, "ĐỌC-TÔI.txt"));
        Assert.Contains("hong", readme);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Sql_backup_stopped_service_is_not_started()
    {
        var root = Path.Combine(Path.GetTempPath(), "ith-sql-" + Guid.NewGuid().ToString("N"));
        var data = Directory.CreateDirectory(Path.Combine(root, "DATA")).FullName;
        var calls = new List<string>();
        var run = new FakeRunner((f, a) =>
        {
            calls.Add(f + " " + a);
            return f == "sc.exe" ? new RunResult(0, "STATE : 1 STOPPED") : new RunResult(f == "robocopy.exe" ? 1 : 0, "");
        });
        var i = new SqlInstanceInfo { Name = "X", Service = "MSSQL$X", BackupDir = Path.Combine(root, "b"), DataDirs = { data } };

        Assert.Equal("", AppData.BackupSql(run, i, Path.Combine(root, "out"), null));
        Assert.DoesNotContain(calls, c => c.StartsWith("net.exe"));
        Assert.DoesNotContain(calls, c => c.StartsWith("powershell.exe"));   // dịch vụ tắt: không thử .bak
        Directory.Delete(root, true);
    }

    [Fact]
    public void Sql_backup_fails_when_nothing_saved()
    {
        var root = Path.Combine(Path.GetTempPath(), "ith-sql-" + Guid.NewGuid().ToString("N"));
        var run = new FakeRunner((f, a) => f == "sc.exe" ? new RunResult(0, "RUNNING") : new RunResult(f == "robocopy.exe" ? 16 : 0, "ERR=*|Login failed"));
        var i = new SqlInstanceInfo { Name = "X", Service = "MSSQL$X", BackupDir = root, DataDirs = { root } };
        Assert.NotEqual("", AppData.BackupSql(run, i, Path.Combine(root, "out"), null));
        Directory.Delete(root, true);
    }
}
