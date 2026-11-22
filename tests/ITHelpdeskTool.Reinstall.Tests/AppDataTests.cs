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

public class ExtraDataTests
{
    [Theory]
    [InlineData(@"C:\\Users\\a\\MultiZalo\\Ke toan", @"C:\Users\a\MultiZalo\Ke toan")]
    [InlineData(@"K\u1ebf to\u00e1n", "Kế toán")]
    [InlineData("a\\\"b", "a\"b")]
    public void Unescapes_json_strings(string raw, string expected) => Assert.Equal(expected, ExtraData.Unescape(raw));

    [Theory]
    [InlineData("VNI-Times.TTF", true)]
    [InlineData("arial.otf", true)]
    [InlineData("font.ttc", true)]
    [InlineData("readme.txt", false)]
    public void Detects_font_files(string f, bool yes) => Assert.Equal(yes, ExtraData.IsFontFile(f));

    private static UserPaths Temp(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ith-scan-" + Guid.NewGuid().ToString("N"));
        var desk = Directory.CreateDirectory(Path.Combine(root, "Desktop")).FullName;
        File.WriteAllText(Path.Combine(desk, "a.txt"), "x");
        var docs = Directory.CreateDirectory(Path.Combine(root, "Documents")).FullName;
        var sig = Directory.CreateDirectory(Path.Combine(root, "Roaming", "Microsoft", "Signatures")).FullName;
        File.WriteAllText(Path.Combine(sig, "Cty.htm"), "<p>chu ky</p>");
        var pst = Directory.CreateDirectory(Path.Combine(docs, "Outlook Files", "Cu")).FullName;
        File.WriteAllText(Path.Combine(pst, "luu-tru.pst"), "pst");
        File.WriteAllText(Path.Combine(docs, "Outlook Files", "a@b.com.ost"), "ost");
        Directory.CreateDirectory(Path.Combine(root, "Local"));
        return new UserPaths { Profile = root, Desktop = desk, Documents = docs, Roaming = Path.Combine(root, "Roaming"), Local = Path.Combine(root, "Local") };
    }

    [Fact]
    public void Scan_finds_outlook_pst_in_subfolder_ost_optional_and_signatures_restored()
    {
        var u = Temp(out var root);
        var list = BackupScanner.Scan(u, measure: false, targetDrive: root.Substring(0, 2));
        var pst = Assert.Single(list, b => b.Id.StartsWith("outlook-pst"));
        Assert.True(pst.Default);
        Assert.Contains("luu-tru.pst", pst.Files);
        var ost = Assert.Single(list, b => b.Id.StartsWith("outlook-ost"));
        Assert.False(ost.Default);                                  // .ost: tự tải lại khi đăng nhập Outlook
        var sig = Assert.Single(list, b => b.Id == "outlook-signatures");
        Assert.Equal(RestoreKind.AppData, sig.Restore);
        Assert.Equal(@"%APPDATA%\Microsoft\Signatures", sig.RestoreTo);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Items_on_other_drive_are_not_selected_by_default()
    {
        var u = Temp(out var root);
        var other = root[0] == 'Z' ? "Y:" : "Z:";                    // giả sử ổ Windows là ổ khác → thư mục tạm "nằm ở ổ khác"
        var list = BackupScanner.Scan(u, measure: false, targetDrive: other);
        var desk = Assert.Single(list, b => b.Id == "Desktop");
        Assert.False(desk.Default);
        Assert.Contains("không bị xoá", desk.Note);
        Assert.True(Assert.Single(list, b => b.Id == "Desktop" ).Restore == RestoreKind.UserFolder);
        var same = BackupScanner.Scan(u, measure: false, targetDrive: root.Substring(0, 2));
        Assert.True(Assert.Single(same, b => b.Id == "Desktop").Default);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Font_item_copies_listed_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ith-font-" + Guid.NewGuid().ToString("N"));
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "VNI-Times.TTF"), "font");
        var item = new BackupItem { Id = ExtraData.FontsId, Title = "Font", FileList = { Path.Combine(src, "VNI-Times.TTF") }, Restore = RestoreKind.Fonts };
        var res = BackupRunner.Run(new FakeRunner((_, _) => new RunResult(0, "")), [item], Path.Combine(root, "backup"), "Q:");
        Assert.True(res.Ok);
        Assert.True(File.Exists(Path.Combine(root, "backup", "fonts", "VNI-Times.TTF")));
        Directory.Delete(root, true);
    }
}

public class CatalogAndOrderTests
{
    [Fact]
    public void Software_catalog_matches_requested_defaults_sorted_a_to_z()
    {
        var names = Catalog.Software.Select(s => s.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.InvariantCultureIgnoreCase), names);
        var on = Catalog.Software.Where(s => s.Default).Select(s => s.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "7-Zip", "Cốc Cốc", "EVKey", "Google Chrome", "IT Helpdesk Tool", "K-Lite Codec Pack", "Mail Lite",
                             "Mozilla Firefox", "PDF Editor Lite", "RustDesk", "UltraViewer", "Zalo PC" }.OrderBy(n => n), on);
        var off = Catalog.Software.Where(s => !s.Default).Select(s => s.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Bartender Lite", "ChatGPT", "Claude", "Foxit PDF Reader", "Java 8 (JRE)", "Lightshot", "Python 3.13",
                             "Telegram", "UniKey", "VLC", "WeChat", "WhatsApp", "WinRAR", "Zoom" }.OrderBy(n => n), off);
        Assert.All(Catalog.Software, s => Assert.False(string.IsNullOrEmpty(s.Description)));
        Assert.Equal(Catalog.Software.Count, Catalog.Software.Select(s => s.Id).Distinct().Count());
        // Bộ cài Inno của chính mình: tham số cố định; còn lại Args trống = FirstLogon tự nhận kiểu bộ cài
        Assert.All(Catalog.Software.Where(s => s.Id is "it-helpdesk-tool" or "pdf-editor-lite"), s => Assert.Contains("/VERYSILENT", s.Args));
        // FirstLogon có đoán tham số im lặng + User-Agent trình duyệt (zalo.me cần)
        var ps = Templates.Load("FirstLogon.ps1");
        Assert.Contains("function Get-SilentArgs", ps);
        Assert.Contains("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", ps);
    }

    [Fact]
    public void Backup_list_is_grouped_in_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "ith-order-" + Guid.NewGuid().ToString("N"));
        var desk = Directory.CreateDirectory(Path.Combine(root, "Desktop")).FullName;
        var roaming = Directory.CreateDirectory(Path.Combine(root, "Roaming")).FullName;
        Directory.CreateDirectory(Path.Combine(roaming, "ZaloData"));
        var local = Directory.CreateDirectory(Path.Combine(root, "Local")).FullName;
        Directory.CreateDirectory(Path.Combine(local, "Google", "Chrome", "User Data"));
        var u = new UserPaths { Profile = root, Desktop = desk, Roaming = roaming, Local = local };
        var groups = BackupScanner.Scan(u, measure: false, targetDrive: root.Substring(0, 2)).Select(b => b.Group).Distinct().ToList();
        var expected = BackupScanner.Groups.Where(groups.Contains).ToList();
        Assert.Equal(expected, groups);                 // nhóm liền nhau, đúng thứ tự
        Assert.Equal(BackupScanner.GroupPersonal, groups[0]);
        Assert.Equal(BackupScanner.GroupSettings, groups[^1]);
        Directory.Delete(root, true);
    }
}
