using System.Text.Json;
using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

public class StagingPartitionTests
{
    private const long GB = 1L << 30;

    [Fact]
    public void Needed_size_rounds_up_with_minimum()
    {
        Assert.Equal(12 * GB, StagingPartition.NeededBytes(1 * GB, 0));
        Assert.Equal(17 * GB, StagingPartition.NeededBytes(7 * GB, 5 * GB));
        Assert.Equal(18 * GB, StagingPartition.NeededBytes(7 * GB + 1, 5 * GB));
    }

    [Fact]
    public void Parses_probe_output()
    {
        var s = StagingPartition.Parse("SIZE=403302285312\nMIN=389572931584\nFREE=105226366976\nDISK=1\nPART=3\nOFFSET=290455552\nSTYLE=GPT\nPRIMARY=0\nHIBER=1\n");
        Assert.True(s.Read);
        Assert.Equal(1, s.Disk);
        Assert.Equal(3, s.Partition);
        Assert.Equal("GPT", s.Style);
        Assert.True(s.Hibernation);
        Assert.Equal(403302285312 - 389572931584, s.MaxShrink);
        Assert.False(StagingPartition.Parse("ERROR=Get-Partition not found\n").Read);
    }

    [Fact]
    public void Blocks_when_unmovable_files_limit_shrink_and_suggests_fixes()
    {
        // Đúng như máy thật: C: 375 GB, trống 98 GB nhưng chỉ thu nhỏ được 12,8 GB
        var s = new ShrinkInfo { Size = 375 * GB, MinSize = 362 * GB, Free = 98 * GB, Disk = 1, Partition = 3, Style = "GPT", Hibernation = true };
        var c = Assert.Single(StagingPartition.Check(s, 17 * GB));
        Assert.Equal(CheckLevel.Block, c.Level);
        Assert.Contains("powercfg /h off", c.Detail);
        Assert.Equal(CheckLevel.Ok, StagingPartition.Check(s, 12 * GB).Single().Level);
    }

    [Fact]
    public void Blocks_full_mbr_and_low_free_space()
    {
        var mbr = new ShrinkInfo { Size = 200 * GB, MinSize = 50 * GB, Free = 100 * GB, Disk = 0, Partition = 2, Style = "MBR", MbrPrimary = 4 };
        Assert.Contains(StagingPartition.Check(mbr, 20 * GB), c => c.Level == CheckLevel.Block && c.Title.Contains("MBR"));
        var full = new ShrinkInfo { Size = 200 * GB, MinSize = 50 * GB, Free = 22 * GB, Disk = 0, Partition = 2, Style = "GPT" };
        Assert.Contains(StagingPartition.Check(full, 20 * GB), c => c.Level == CheckLevel.Block && c.Title.Contains("không đủ chỗ"));
    }

    [Fact]
    public void Create_shrinks_then_places_partition_right_after_windows()
    {
        var s = new ShrinkInfo { Size = 100 * GB, MinSize = 30 * GB, Free = 60 * GB, Disk = 0, Partition = 3, Offset = 290455552, Style = "GPT" };
        string? script = null;
        var run = new FakeRunner((_, a) => { script = a; return new RunResult(0, "LETTER=\r\n"); });
        Assert.Throws<ReinstallException>(() => StagingPartition.Create(run, s, 20 * GB));   // không có ký tự ổ → lỗi rõ ràng
        Assert.Contains($"-Size {80 * GB}", script);
        Assert.Contains($"-Offset {290455552 + 80 * GB}", script);
        Assert.Contains($"-NewFileSystemLabel '{StagingPartition.Label}'", script);
        Assert.DoesNotContain("-MbrType", script);
    }

    [Fact]
    public void Merge_script_checks_label_before_removing()
    {
        var m = StagingPartition.MergeScript('F', 'C');
        Assert.True(m.IndexOf(StagingPartition.Label, StringComparison.Ordinal) < m.IndexOf("Remove-Partition", StringComparison.Ordinal));
        Assert.Contains("Resize-Partition", m);
    }
}

public class SameDriveTests
{
    [Fact]
    public void Purge_mode_keeps_staging_and_never_follows_junctions()
    {
        var purge = Stager.ApplyPurge;
        // Gỡ junction / symlink trước (chỉ gỡ đường dẫn) rồi mới xoá
        Assert.True(purge.IndexOf("dir /s /b /al", StringComparison.Ordinal) < purge.IndexOf("robocopy", StringComparison.Ordinal));
        Assert.Contains("/XJ", purge);
        Assert.Contains("/XD \"%STG%\"", purge);
        Assert.Contains("goto hard", purge);
        Assert.DoesNotContain("format fs=", purge);
        Assert.DoesNotContain("diskpart", purge);
        Assert.Contains("format fs=ntfs", Stager.ApplyFormat);
        // Hai chế độ kiểm tra ngược nhau: đệm khác ổ / đệm cùng ổ
        Assert.Contains("if /i \"%TGT%\"", Stager.ApplyOtherDriveCheck);
        Assert.Contains("if /i not \"%TGT%\"", Stager.ApplySameDriveCheck);
        var apply = Templates.Load("apply.cmd");
        Assert.Contains("{{WIPE}}", apply);
        Assert.Contains("{{STAGING_CHECK}}", apply);
    }

    [Fact]
    public void Preflight_allows_staging_on_windows_drive_as_info()
    {
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
        var p = new ReinstallPlan { TargetDrive = sys, StagingDir = sys + @"\ITH" };
        var checks = Preflight.Run(p, new MachineFacts { Firmware = FirmwareMode.Uefi, Tpm = 2, SecureBoot = true, RamBytes = 8L << 30 }, 0);
        Assert.Contains(checks, c => c.Level == CheckLevel.Info && c.Title.Contains("ngay trên ổ Windows"));
        Assert.DoesNotContain(checks, c => c.Title.Contains("phải nằm trên ổ khác"));
    }

    [Fact]
    public void Backup_into_kept_staging_on_target_is_allowed()
    {
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
        var run = new FakeRunner((_, _) => new RunResult(0, ""));
        var staging = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ITH");   // nằm trên ổ hệ thống
        var res = BackupRunner.Run(run, [], Path.Combine(staging, "backup"), sys, null, staging);
        Assert.True(res.Ok);
        Assert.Throws<ReinstallException>(() => BackupRunner.Run(run, [], Path.Combine(staging, "backup"), sys));
    }

    [Fact]
    public void Auto_staging_merges_after_install()
    {
        var on = JsonDocument.Parse(PostInstallBuilder.Json(new ReinstallPlan { AutoStaging = true }, "x", @"\ITH"));
        Assert.True(on.RootElement.GetProperty("mergeStaging").GetBoolean());
        var off = JsonDocument.Parse(PostInstallBuilder.Json(new ReinstallPlan(), "x", @"\ITH"));
        Assert.False(off.RootElement.GetProperty("mergeStaging").GetBoolean());
        var ps = Templates.Load("FirstLogon.ps1");
        Assert.True(ps.IndexOf("ITH-AUTOPART.tag", StringComparison.Ordinal) < ps.IndexOf("Remove-Partition", StringComparison.Ordinal));
        Assert.Contains("ITH-Backup", ps);
    }
}
