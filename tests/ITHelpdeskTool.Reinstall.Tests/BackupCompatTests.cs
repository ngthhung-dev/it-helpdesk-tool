using System.Text.Json;
using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

public class CompatibilityTests
{
    private static MachineFacts Win11Home() => new()
    {
        Firmware = FirmwareMode.Uefi, Tpm = 2, SecureBoot = true, RamBytes = 16L << 30, CpuBits = 64,
        EditionId = "CoreSingleLanguage", CpuName = "i7",
    };

    private static ImageEdition Img(string name, string edition, string version = "10.0.26100", string arch = "x64") =>
        new() { Index = 1, Name = name, Edition = edition, Version = version, Architecture = arch };

    [Fact]
    public void Same_family_activates_other_family_warns()
    {
        var same = Compatibility.Evaluate(Win11Home(), Img("Windows 11 Home", "Core"), 500L << 30);
        Assert.Contains(same, c => c.Level == CheckLevel.Ok && c.Title.Contains("Cùng bản Home"));
        var pro = Compatibility.Evaluate(Win11Home(), Img("Windows 11 Pro", "Professional"), 500L << 30);
        Assert.Contains(pro, c => c.Level == CheckLevel.Warn && c.Title.Contains("Home sang Pro"));
    }

    [Fact]
    public void Blocks_64bit_image_on_32bit_cpu_and_arm_image()
    {
        var f = Win11Home();
        f.CpuBits = 32;
        Assert.Contains(Compatibility.Evaluate(f, Img("Windows 10 Pro", "Professional", "10.0.19045"), 200L << 30), c => c.Level == CheckLevel.Block);
        Assert.Contains(Compatibility.Evaluate(Win11Home(), Img("Windows 11 Pro", "Professional", arch: "ARM64"), 200L << 30),
            c => c.Level == CheckLevel.Block && c.Title.Contains("ARM"));
    }

    [Fact]
    public void Win11_on_old_machine_warns_but_does_not_block()
    {
        var old = new MachineFacts { Firmware = FirmwareMode.Bios, Tpm = 0, RamBytes = 8L << 30, EditionId = "Professional" };
        var r = Compatibility.Evaluate(old, Img("Windows 11 Pro", "Professional"), 240L << 30, cpuSupportsWin11: false);
        var w = Assert.Single(r, c => c.Title.Contains("chưa đủ điều kiện Windows 11"));
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Contains("TPM 2.0", w.Title);
        Assert.Contains("CPU", w.Title);
        Assert.DoesNotContain(r, c => c.Level == CheckLevel.Block);
    }

    [Fact]
    public void Small_disk_blocks_and_win10_suggests_win11_on_capable_machine()
    {
        Assert.Contains(Compatibility.Evaluate(Win11Home(), Img("Windows 11 Home", "Core"), 25L << 30), c => c.Level == CheckLevel.Block);
        var r = Compatibility.Evaluate(Win11Home(), Img("Windows 10 Home", "Core", "10.0.19045"), 500L << 30);
        Assert.Contains(r, c => c.Detail.Contains("hết hỗ trợ"));
    }

    [Theory]
    [InlineData("CoreSingleLanguage", "home")]
    [InlineData("Professional", "pro")]
    [InlineData("ProfessionalWorkstation", "pro")]
    [InlineData("EnterpriseS", "ltsc")]
    [InlineData("EnterpriseEval", "eval")]
    [InlineData("Windows 11 Pro N", "pro")]
    public void Edition_family(string edition, string family) => Assert.Equal(family, Compatibility.Family(edition));
}

public class BackupTests
{
    private static UserPaths FakeProfile(out string root)
    {
        root = Directory.CreateTempSubdirectory().FullName;
        string D(string rel) => Directory.CreateDirectory(Path.Combine(root, rel)).FullName;
        var u = new UserPaths
        {
            Profile = root, Desktop = D("Desktop"), Documents = D("Documents"), Downloads = D("Downloads"),
            Pictures = D("Pictures"), Videos = D("Videos"), Music = D("Music"),
            Roaming = D(@"AppData\Roaming"), Local = D(@"AppData\Local"),
        };
        File.WriteAllText(Path.Combine(u.Desktop, "a.txt"), new string('x', 1000));
        var chrome = D(@"AppData\Local\Google\Chrome\User Data\Default");
        File.WriteAllText(Path.Combine(chrome, "Bookmarks"), "{}");
        File.WriteAllText(Path.Combine(D(@"AppData\Local\Google\Chrome\User Data\Default\Cache"), "big.bin"), new string('c', 5000));
        File.WriteAllText(Path.Combine(D(@"Documents\Outlook Files"), "mail.pst"), new string('p', 300));
        D(@"AppData\Roaming\ZaloData");
        return u;
    }

    [Fact]
    public void Scans_folders_browsers_outlook_zalo_and_excludes_cache()
    {
        var items = BackupScanner.Scan(FakeProfile(out _));
        Assert.Equal(1000, items.Single(i => i.Id == "Desktop").Bytes);
        var chrome = items.Single(i => i.Id == "browser-chrome");
        Assert.Equal(2, chrome.Bytes);                       // chỉ Bookmarks "{}", bỏ Cache
        Assert.Equal(RestoreKind.Bookmarks, chrome.Restore);
        Assert.Contains("Mật khẩu", chrome.Note);
        Assert.Contains(items, i => i.Id == "outlook-pst" && i.Bytes == 300);
        Assert.Contains(items, i => i.Id == "zalo");
        Assert.DoesNotContain(items, i => i.Id == "browser-edge");   // không cài thì không liệt kê
        Assert.False(items.Single(i => i.Id == "Videos").Default);
    }

    [Fact]
    public void Refuses_to_back_up_onto_the_drive_being_erased()
    {
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
        var run = new FakeRunner((_, _) => new RunResult(0, ""));
        Assert.Throws<ReinstallException>(() => BackupRunner.Run(run, [], sys + @"\ITH\backup", sys));
    }

    [Fact]
    public void Robocopy_exit_codes_below_8_are_success()
    {
        var u = FakeProfile(out var root);
        var items = BackupScanner.Scan(u).Where(i => i.Id is "Desktop" or "Documents").ToList();
        var dest = Path.Combine(root, "backup");
        var calls = 0;
        var run = new FakeRunner((file, _) => new RunResult(file == "robocopy.exe" ? (calls++ == 0 ? 1 : 8) : 0, ""));
        var res = BackupRunner.Run(run, items, dest, "Q:");
        Assert.Single(res.Done);                 // Desktop: mã 1 = đã chép file → OK
        Assert.Single(res.Errors);               // Documents: mã 8 = có file lỗi
        Assert.True(File.Exists(Path.Combine(dest, "backup.json")));
        Assert.Contains("Documents", File.ReadAllText(Path.Combine(dest, "ĐỌC-TÔI.txt")));
    }

    [Fact]
    public void Restore_entries_go_to_postinstall_json()
    {
        var items = BackupScanner.Scan(FakeProfile(out _));
        var p = new ReinstallPlan { Restore = items.Where(i => i.Id is "Desktop" or "browser-chrome" or "wifi" or "apps").ToList() };
        using var doc = JsonDocument.Parse(PostInstallBuilder.Json(p, "id1", @"\ITH"));
        var restore = doc.RootElement.GetProperty("restore").EnumerateArray().ToList();
        Assert.Equal(3, restore.Count);          // "apps" chỉ để đối chiếu, không chép lại
        Assert.Contains(restore, r => r.GetProperty("kind").GetString() == "UserFolder" && r.GetProperty("to").GetString() == "Desktop");
        Assert.Contains(restore, r => r.GetProperty("kind").GetString() == "Bookmarks" && r.GetProperty("to").GetString() == @"Google\Chrome\User Data");
        Assert.Contains(restore, r => r.GetProperty("kind").GetString() == "WiFi");
        Assert.Contains("restore.json", Templates.Load("FirstLogon.ps1"));
    }
}
