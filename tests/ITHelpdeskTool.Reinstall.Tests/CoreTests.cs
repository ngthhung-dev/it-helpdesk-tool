using System.Text.Json;
using System.Xml.Linq;
using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

/// <summary>Chạy lệnh giả: ghi lại lệnh, trả output soạn sẵn theo lệnh.</summary>
internal sealed class FakeRunner(Func<string, string, RunResult> answer) : IRunner
{
    public List<string> Calls { get; } = [];

    public RunResult Run(string file, string args)
    {
        Calls.Add($"{file} {args}");
        return answer(file, args);
    }
}

public class ImageInfoTests
{
    private const string DismList = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1

        Details for image : D:\sources\install.wim

        Index : 1
        Name : Windows 11 Home
        Description : Windows 11 Home
        Size : 18,621,365,433 bytes

        Index : 6
        Name : Windows 11 Pro
        Description : Windows 11 Pro
        Size : 18,880,140,066 bytes

        The operation completed successfully.
        """;

    [Fact]
    public void Parses_editions()
    {
        var list = ImageInfo.Parse(DismList);
        Assert.Equal(2, list.Count);
        Assert.Equal(6, list[1].Index);
        Assert.Equal("Windows 11 Pro", list[1].Name);
        Assert.Equal(18_880_140_066L, list[1].Size);
    }

    [Fact]
    public void Parses_details()
    {
        var one = ImageInfo.Parse("Index : 6\nName : Windows 11 Pro\nArchitecture : x64\nVersion : 10.0.26100\nEdition : Professional\nLanguages :\n        vi-VN (Default)\n");
        Assert.Equal("vi-VN", one[0].Language);
        Assert.Equal("Professional", one[0].Edition);
        Assert.True(one[0].IsWindows11);
        Assert.Equal("x64", one[0].Architecture);
        Assert.Equal("10.0.26100", one[0].Version);
        Assert.Equal("amd64", ImageInfo.UnattendArch("x64"));
        Assert.Equal("x86", ImageInfo.UnattendArch("x86"));
    }
}

public class BootEntryTests
{
    private const string G1 = "{11111111-2222-3333-4444-555555555555}";
    private const string G2 = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

    [Theory]
    [InlineData(FirmwareMode.Uefi, @"\windows\system32\boot\winload.efi")]
    [InlineData(FirmwareMode.Bios, @"\windows\system32\boot\winload.exe")]
    public void Builds_ramdisk_entry_with_one_time_boot(FirmwareMode fw, string winload)
    {
        var cmds = BootEntry.CreateCommands(@"D:\ITH\boot.wim", @"D:\ITH\boot.sdi", fw, G1, G2).ToList();
        Assert.Contains($"/set {G1} ramdisksdidevice partition=D:", cmds);
        Assert.Contains($@"/set {G1} ramdisksdipath \ITH\boot.sdi", cmds);
        Assert.Contains($@"/set {G2} device ramdisk=[D:]\ITH\boot.wim,{G1}", cmds);
        Assert.Contains($"/set {G2} path {winload}", cmds);
        Assert.Contains($"/set {G2} winpe yes", cmds);
        // Khởi động vào môi trường cài đúng một lần — không đổi {default}
        Assert.Equal($"/bootsequence {G2}", cmds[^1]);
        Assert.DoesNotContain(cmds, c => c.Contains("/default"));
    }

    [Fact]
    public void Create_parses_localized_bcdedit_output_and_rolls_back_on_error()
    {
        var n = 0;
        var run = new FakeRunner((_, args) =>
        {
            if (args.StartsWith("/create")) return new RunResult(0, $"Mục {(n++ == 0 ? G1 : G2)} đã được tạo thành công.");
            if (args.Contains("winpe")) return new RunResult(1, "The parameter is incorrect.");
            return new RunResult(0, "ok");
        });
        Assert.Throws<ReinstallException>(() => BootEntry.Create(run, @"D:\ITH\boot.wim", @"D:\ITH\boot.sdi", FirmwareMode.Uefi));
        // Lỗi giữa chừng → xoá cả hai đối tượng vừa tạo
        Assert.Contains(run.Calls, c => c == $"bcdedit.exe /delete {G2} /cleanup");
        Assert.Contains(run.Calls, c => c == $"bcdedit.exe /delete {G1}");
    }

    [Fact]
    public void Save_and_load_ids()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        new BootEntry { EntryId = G2, RamdiskId = G1 }.Save(dir);
        var e = BootEntry.Load(dir)!;
        Assert.Equal(G2, e.EntryId);
        Assert.Equal(G1, e.RamdiskId);
    }
}

public class UnattendTests
{
    private static ReinstallPlan Plan() => new()
    {
        AccountName = "Kế toán & <IT>",
        Password = "p\"a'ss",
        ComputerName = "PC-01",
        Architecture = "amd64",
    };

    [Fact]
    public void Is_valid_xml_with_escaped_values()
    {
        var xml = XDocument.Parse(UnattendBuilder.Build(Plan()));
        XNamespace u = "urn:schemas-microsoft-com:unattend";
        var names = xml.Descendants(u + "Name").Select(e => e.Value).ToList();
        Assert.Contains("Kế toán & <IT>", names);
        Assert.Equal("p\"a'ss", xml.Descendants(u + "Value").First().Value);
        Assert.Equal("PC-01", xml.Descendants(u + "ComputerName").Single().Value);
    }

    [Fact]
    public void Skips_oobe_autologons_once_and_runs_first_logon()
    {
        var xml = XDocument.Parse(UnattendBuilder.Build(Plan()));
        XNamespace u = "urn:schemas-microsoft-com:unattend";
        Assert.Equal("true", xml.Descendants(u + "HideOnlineAccountScreens").Single().Value);
        Assert.Equal("1", xml.Descendants(u + "LogonCount").Single().Value);
        Assert.Contains("FirstLogon.ps1", xml.Descendants(u + "CommandLine").Single().Value);
        Assert.All(xml.Descendants(u + "component"), c => Assert.Equal("amd64", c.Attribute("processorArchitecture")!.Value));
        // Không có key / activate
        Assert.Empty(xml.Descendants(u + "ProductKey"));
    }
}

public class PostInstallTests
{
    [Fact]
    public void Json_is_valid_and_splits_machine_user()
    {
        var p = new ReinstallPlan
        {
            Software = Catalog.DefaultSoftware(),
            Tweaks = Catalog.DefaultTweaks(),
            AfterPostInstall = "shutdown /s /t 5",
        };
        var json = PostInstallBuilder.Json(p, "abc123", @"\ITH");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal(@"\ITH", r.GetProperty("stagingRel").GetString());
        Assert.Contains(r.GetProperty("machine").EnumerateArray(), e => e.GetString()!.Contains("LongPathsEnabled"));
        Assert.Contains(r.GetProperty("user").EnumerateArray(), e => e.GetString()!.Contains("HideFileExt"));
        Assert.Contains(r.GetProperty("software").EnumerateArray(), e => e.GetProperty("id").GetString() == "Google.Chrome");
        Assert.Equal("shutdown /s /t 5", r.GetProperty("after").GetString());
    }

    [Fact]
    public void Dangerous_tweaks_are_never_default()
    {
        Assert.All(Catalog.Tweaks.Where(t => t.Danger), t =>
        {
            Assert.False(t.Default);
            Assert.NotEmpty(t.Warning);
        });
        Assert.DoesNotContain(Catalog.DefaultTweaks(), t => t.Danger);
    }
}

public class TemplateTests
{
    private static string Apply(bool testFail) => Templates.Fill(Templates.Load("apply.cmd"), new Dictionary<string, string>
    {
        ["ID"] = "abc123",
        ["IMAGE"] = "install.esd",
        ["INDEX"] = "6",
        ["FIRMWARE"] = "UEFI",
        ["STAGING_CHECK"] = Stager.ApplyOtherDriveCheck,
        ["WIPE"] = Stager.ApplyFormat,
        ["TEST_FAIL_BEFORE_FORMAT"] = testFail ? "call :log \"TEST\"\r\ngoto safe" : "",
    });

    [Fact]
    public void Apply_script_is_filled_and_checks_before_format()
    {
        var s = Apply(false);
        Assert.DoesNotContain("{{", s);
        Assert.Contains("ITH-TARGET-abc123.tag", s);
        Assert.Contains("/Index:6", s);
        // bcdboot: /f bắt buộc đi kèm /s → lần đầu không /f, dự phòng mới gán ổ hệ thống và chỉ rõ /s /f
        Assert.Contains("bcdboot %TGT%\\Windows >>", s);
        Assert.Contains("bcdboot %TGT%\\Windows /s Y: /f UEFI", s);
        Assert.DoesNotContain("Windows /f UEFI", s);
        // Máy UEFI nhiều ổ cứng: gắn đúng phân vùng EFI vừa khởi động (mountvol /s) trước, lỗi mới để bcdboot tự tìm
        Assert.Contains("mountvol %SL%: /s", s);
        Assert.Contains("bcdboot %TGT%\\Windows /s %SL%: /f UEFI", s);
        Assert.True(s.IndexOf("mountvol", StringComparison.Ordinal) < s.IndexOf("bcdboot %TGT%\\Windows >>", StringComparison.Ordinal));
        Assert.DoesNotContain("->", s);   // cmd hiểu "->" trong echo là chuyển hướng ra file
        // Mọi kiểm tra (tìm ổ, đọc file cài) nằm TRƯỚC bước format; lỗi trước format → nhãn safe (về Windows cũ)
        var format = s.IndexOf("format fs=ntfs", StringComparison.Ordinal);
        Assert.True(s.IndexOf("Get-WimInfo", StringComparison.Ordinal) < format);
        Assert.True(s.IndexOf("ITH-TARGET-abc123.tag", StringComparison.Ordinal) < format);
        Assert.Contains(":safe", s);
    }

    [Theory]
    [InlineData("SYSPART=0|1|{0ac75bd3-1c5e-4f07-9a5e-3c1e7f2b9d10}|0|3", 0, 1, "0AC75BD3-1C5E-4F07-9A5E-3C1E7F2B9D10", true, false)]
    [InlineData("SYSPART=1|2|6E1D7B2A|0|2", 1, 2, "6E1D7B2A", true, true)]     // EFI/System Reserved trên ổ cứng khác
    [InlineData("SYSPART=0|1|6E1D7B2A|0|1", 0, 1, "6E1D7B2A", false, false)]   // BIOS: khởi động ngay trên ổ C:
    [InlineData("SYSPART=0|1|bad id|0|3", 0, 1, "", false, false)]
    [InlineData("", -1, -1, "", false, false)]
    public void Parses_system_partition(string output, int disk, int part, string id, bool found, bool otherDisk)
    {
        var s = SystemPartition.Parse(output);
        Assert.Equal(disk, s.Disk);
        Assert.Equal(part, s.Partition);
        Assert.Equal(id, s.DiskId);
        Assert.Equal(found, s.Found);
        Assert.Equal(otherDisk, s.OnOtherDisk);
    }

    [Fact]
    public void Crlf_for_cmd()
    {
        var s = Templates.Crlf(Apply(true));
        Assert.DoesNotContain("\r\r", s);
        Assert.Equal(s.Split('\n').Length - 1, s.Split("\r\n").Length - 1);
    }

    [Fact]
    public void Run_cmd_and_first_logon_exist()
    {
        var run = Templates.Fill(Templates.Load("run.cmd"), new Dictionary<string, string> { ["ID"] = "x", ["STAGING_REL"] = @"\ITH" });
        Assert.Contains(@":\ITH\ITH-STAGING-x.tag", run);
        Assert.Contains("postinstall.json", Templates.Load("FirstLogon.ps1"));
    }
}

public class PreflightTests
{
    [Fact]
    public void Blocks_staging_on_target_and_bitlocker()
    {
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
        var p = new ReinstallPlan { TargetDrive = sys, StagingDir = sys + @"\ITH" };
        var f = new MachineFacts { Firmware = FirmwareMode.Uefi, Tpm = 2, SecureBoot = true, RamBytes = 8L << 30 };
        f.BitLocker[sys] = new BitLockerVolume { Drive = sys, Protected = true, Conversion = 1, Percent = 100, VolumeType = 0 };
        var checks = Preflight.Run(p, f, 5L << 30);
        // Đệm ngay trên ổ Windows giờ là chế độ hợp lệ (không format, giữ thư mục đệm) → thông tin, không chặn
        Assert.Contains(checks, c => c.Level == CheckLevel.Info && c.Title.Contains("ngay trên ổ Windows"));
        Assert.Contains(checks, c => c.Level == CheckLevel.Block && c.Title.Contains("BitLocker"));
    }

    [Fact]
    public void Recommends_win10_when_missing_tpm_or_uefi()
    {
        var old = new MachineFacts { Firmware = FirmwareMode.Bios, Tpm = 0, RamBytes = 8L << 30 };
        var r = Preflight.Recommend(old, true);
        Assert.Contains("Windows 10", r.Title);
        Assert.Contains("TPM 2.0", r.Detail);

        var ok = new MachineFacts { Firmware = FirmwareMode.Uefi, Tpm = 2, SecureBoot = false, RamBytes = 16L << 30 };
        Assert.Contains("Windows 11", Preflight.Recommend(ok, true).Title);
        Assert.Contains("CPU", Preflight.Recommend(ok, false).Detail);

        var tiny = new MachineFacts { Firmware = FirmwareMode.Uefi, Tpm = 2, RamBytes = 2L << 30 };
        Assert.Contains("32-bit hoặc LTSC", Preflight.Recommend(tiny, true).Title);
    }

    [Fact]
    public void Picks_storage_driver_dirs_by_inf_class()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var rst = Directory.CreateDirectory(Path.Combine(root, "iastorvd.inf_amd64_1")).FullName;
        File.WriteAllText(Path.Combine(rst, "iaStorVD.inf"), "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=SCSIAdapter\r\n", System.Text.Encoding.Unicode);
        var net = Directory.CreateDirectory(Path.Combine(root, "netrtwlane.inf_amd64_2")).FullName;
        File.WriteAllText(Path.Combine(net, "netrtwlane.inf"), "[Version]\r\nClass=Net\r\n");
        var dirs = Drivers.StorageDirs(root);
        Assert.Single(dirs);
        Assert.Equal(rst, dirs[0]);
    }

    [Fact]
    public void Flush_script_flushes_bcd_and_both_drives_once()
    {
        var s = Stager.FlushScript("S:\\", "C:");
        Assert.Contains("OpenSubKey('BCD00000000')", s);
        Assert.Contains("@('S','C')", s);
        Assert.DoesNotContain("\"", s);
        Assert.Contains("@('C')", Stager.FlushScript("C:\\", "C:"));
    }
}
