using System.Xml.Linq;
using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

public class SecurityTests
{
    private static string Sys => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\').ToUpperInvariant();

    /// <summary>Ổ đệm giả (khác ổ Windows) — chỉ dùng tên ổ trong kiểm tra BitLocker.</summary>
    private static ReinstallPlan Plan() => new() { TargetDrive = Sys, StagingDir = @"S:\ITH" };

    private static MachineFacts Facts() => new() { Firmware = FirmwareMode.Uefi, Tpm = 2, SecureBoot = true, RamBytes = 16L << 30 };

    [Fact]
    public void Parses_machine_facts_output()
    {
        var f = new MachineFacts();
        MachineFacts.Parse(f, string.Join("\n",
            "RAM=17179869184", "DOMAIN=1|CONTOSO.local", "ACCOUNT=MicrosoftAccount",
            "JOIN=AzureAdJoined:YES", "JOIN=WorkplaceJoined:NO", "MDM=1",
            "BITLOCKER=C:|1|1|100|0|0", "BITLOCKER=D:|0|3|42.5|1|0", "BITLOCKER=E:|0|0|0|1|0", "BITLOCKER=F:|0|1|100|1|1",
            "EDITION=Professional", "PRODUCT=Windows 10 Pro", "BUILD=26100"));
        Assert.True(f.Account.IsMicrosoftAccount);
        Assert.True(f.Account.AzureAdJoined && f.Account.DomainJoined && f.Account.MdmEnrolled && !f.Account.WorkplaceJoined);
        Assert.Contains("CONTOSO.local", f.Account.OrganizationText);
        Assert.True(f.BitLocker["C:"].Encrypted);
        Assert.True(f.BitLocker["D:"].Decrypting);
        Assert.Equal(42.5, f.BitLocker["D:"].Percent);
        Assert.False(f.BitLocker["E:"].Encrypted);
        Assert.True(f.BitLocker["F:"].Locked);
        Assert.Equal("Windows 11 Pro", f.ProductName);
    }

    [Fact]
    public void Organization_needs_explicit_confirmation()
    {
        var f = Facts();
        f.Account.AzureAdJoined = true;
        f.Account.MdmEnrolled = true;
        var without = Preflight.Run(Plan(), f, 0);
        var c = Assert.Single(without, x => x.ConfirmKey == ConfirmKeys.Organization);
        Assert.Equal(CheckLevel.Block, c.Level);
        Assert.Contains("Intune", c.Title);
        var with = Preflight.Run(Plan(), f, 0, confirmed: new[] { ConfirmKeys.Organization });
        Assert.Equal(CheckLevel.Warn, with.Single(x => x.ConfirmKey == ConfirmKeys.Organization).Level);
    }

    [Fact]
    public void Microsoft_account_is_informational()
    {
        var f = Facts();
        f.Account.Source = "MicrosoftAccount";
        var c = Assert.Single(Preflight.Run(Plan(), f, 0), x => x.Title.Contains("tài khoản Microsoft"));
        Assert.Equal(CheckLevel.Info, c.Level);
        Assert.Contains("aka.ms/myrecoverykey", c.Detail);
    }

    [Fact]
    public void Target_encrypted_or_decrypting_blocks()
    {
        var f = Facts();
        f.BitLocker[Sys] = new BitLockerVolume { Drive = Sys, Conversion = 1, Percent = 100, Protected = true, VolumeType = 0 };
        Assert.Contains(Preflight.Run(Plan(), f, 0), x => x.Level == CheckLevel.Block && x.Title.Contains("mã hoá") && x.Detail.Contains("decrypt"));
        f.BitLocker[Sys] = new BitLockerVolume { Drive = Sys, Conversion = 3, Percent = 37, VolumeType = 0 };
        Assert.Contains(Preflight.Run(Plan(), f, 0), x => x.Level == CheckLevel.Block && x.Title.Contains("đang giải mã"));
        // Tạm dừng bảo vệ (Suspend) nhưng dữ liệu vẫn mã hoá → vẫn chặn
        f.BitLocker[Sys] = new BitLockerVolume { Drive = Sys, Protected = false, Conversion = 1, Percent = 100, VolumeType = 0 };
        Assert.Contains(Preflight.Run(Plan(), f, 0), x => x.Level == CheckLevel.Block && x.Title.Contains("mã hoá"));
    }

    [Fact]
    public void Encrypted_data_drives_need_keys_locked_ones_block()
    {
        var f = Facts();
        f.BitLocker["D:"] = new BitLockerVolume { Drive = "D:", Protected = true, Conversion = 1, Percent = 100, VolumeType = 1 };
        f.BitLocker["F:"] = new BitLockerVolume { Drive = "F:", Conversion = 1, Percent = 100, VolumeType = 1, Locked = true };
        f.BitLocker["G:"] = new BitLockerVolume { Drive = "G:", Conversion = 1, Percent = 100, VolumeType = 2 };   // USB: bỏ qua
        var checks = Preflight.Run(Plan(), f, 0);
        Assert.Contains(checks, x => x.Level == CheckLevel.Warn && x.Title.Contains("D:"));
        Assert.Contains(checks, x => x.Level == CheckLevel.Block && x.Title.Contains("F:") && x.Title.Contains("khoá"));
        Assert.Equal(new[] { "D:" }, Preflight.EncryptedDataDrives(Plan(), f).Select(v => v.Drive));
    }

    [Fact]
    public void Staging_on_encrypted_drive_blocks()
    {
        var f = Facts();
        f.BitLocker["S:"] = new BitLockerVolume { Drive = "S:", Conversion = 1, Percent = 100, VolumeType = 1 };
        Assert.Contains(Preflight.Run(Plan(), f, 0), x => x.Level == CheckLevel.Block && x.Title.Contains("Ổ đệm S:"));
        Assert.Empty(Preflight.EncryptedDataDrives(Plan(), f));
    }

    [Fact]
    public void Parses_recovery_keys_in_any_language()
    {
        const string en = """
            Volume D: [Data]
            Key Protectors of Type RecoveryPassword:

                Numerical Password:
                  ID: {1A2B3C4D-1111-2222-3333-444455556666}
                  Password:
                    123456-234567-345678-456789-567890-678901-789012-890123
            """;
        var keys = Encryption.ParseKeys(en, "D:");
        var k = Assert.Single(keys);
        Assert.Equal("{1A2B3C4D-1111-2222-3333-444455556666}", k.Id);
        Assert.Equal("123456-234567-345678-456789-567890-678901-789012-890123", k.Key);
        const string vi = "Mật khẩu số:\n  Mã: {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}\n  Mật khẩu:\n    111111-222222-333333-444444-555555-666666-777777-888888\n";
        Assert.Single(Encryption.ParseKeys(vi, "E:"));
        Assert.Contains("888888", Encryption.KeysText(keys.Concat(Encryption.ParseKeys(vi, "E:"))));
    }

    [Fact]
    public void Microsoft_account_mode_shows_oobe_account_screen()
    {
        var xml = XDocument.Parse(UnattendBuilder.Build(new ReinstallPlan { MicrosoftAccount = true, AccountName = "x", Password = "secret" }));
        XNamespace u = "urn:schemas-microsoft-com:unattend";
        Assert.Equal("false", xml.Descendants(u + "HideOnlineAccountScreens").Single().Value);
        Assert.Empty(xml.Descendants(u + "LocalAccount"));
        Assert.Empty(xml.Descendants(u + "AutoLogon"));
        Assert.DoesNotContain("secret", xml.ToString());
        Assert.Single(xml.Descendants(u + "FirstLogonCommands"));
    }

    [Fact]
    public void Cloud_only_onedrive_files_are_not_counted_or_copied()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var desk = Directory.CreateDirectory(Path.Combine(root, "OneDrive", "Desktop")).FullName;
        File.WriteAllText(Path.Combine(desk, "local.txt"), new string('a', 100));
        var cloud = Path.Combine(desk, "cloud.docx");
        File.WriteAllText(cloud, new string('b', 700));
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        var item = new BackupItem { Id = "Desktop", Source = desk };
        Assert.Equal(100, BackupScanner.Measure(item));
        Assert.Equal(700, item.CloudBytes);
        Assert.Equal(1, item.CloudFiles);
        Assert.True(item.InOneDrive);
        Assert.True(BackupScanner.IsCloudOnly((FileAttributes)0x400000));

        var calls = new List<string>();
        var run = new FakeRunner((f, a) => { calls.Add(a); return new RunResult(1, ""); });
        BackupRunner.Run(run, new[] { item }, Path.Combine(root, "bk"), "Q:");
        Assert.Contains(calls, c => c.Contains("/XA:O"));
    }
}
