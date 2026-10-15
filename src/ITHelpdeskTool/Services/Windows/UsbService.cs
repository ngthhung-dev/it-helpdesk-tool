using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Windows;

public sealed record UsbDisk(int Number, string Name, long Size, string BusType, string PartitionStyle, bool IsBoot, bool IsSystem, string Letters)
{
    public string Display => $"Ổ {Number}: {Name} · {Fmt.Size(Size)}" + (Letters.Length > 0 ? $" · {Letters}" : "") + $" · {BusType}";
}

/// <summary>Tạo USB cài Windows từ bộ cài (ISO đã gắn / thư mục): xoá USB, phân vùng FAT32, chép file, chia install.wim &gt; 4 GB.</summary>
public static class UsbService
{
    /// <summary>Chỉ cho selftest: cho phép ổ đĩa ảo (VHD) như USB.</summary>
    internal static bool AllowVirtual { get; set; }

    /// <summary>File ảnh lớn hơn mức này thì chia nhỏ (giới hạn FAT32). Selftest hạ xuống để thử nhanh.</summary>
    internal static long SplitOver { get; set; } = 4L * 1024 * 1024 * 1024 - 1;

    private static readonly string[] UsbBus = ["USB", "SD", "MMC"];
    private const long Fat32Max = 32L * 1024 * 1024 * 1024;

    public static async Task<List<UsbDisk>> ListAsync(CancellationToken ct = default)
    {
        const string script = """
            @(Get-Disk | ForEach-Object {
              $l = @(Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { "$($_.DriveLetter):" }) -join ' '
              [pscustomobject]@{ Number=$_.Number; Name=$_.FriendlyName; Size=[int64]$_.Size; Bus="$($_.BusType)"; Style="$($_.PartitionStyle)"; Boot=[bool]$_.IsBoot; System=[bool]$_.IsSystem; Letters=$l }
            }) | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var json = r.Output.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        return items.Select(e => new UsbDisk(e.GetProperty("Number").GetInt32(), e.GetProperty("Name").GetString() ?? "", e.GetProperty("Size").GetInt64(),
                e.GetProperty("Bus").GetString() ?? "", e.GetProperty("Style").GetString() ?? "", e.GetProperty("Boot").GetBoolean(),
                e.GetProperty("System").GetBoolean(), e.GetProperty("Letters").GetString() ?? ""))
            .Where(IsAllowed).OrderBy(d => d.Number).ToList();
    }

    /// <summary>Chỉ ổ USB / thẻ nhớ, không bao giờ là ổ đang chạy Windows.</summary>
    public static bool IsAllowed(UsbDisk d) =>
        !d.IsBoot && !d.IsSystem && d.Size > 0 &&
        (UsbBus.Contains(d.BusType, StringComparer.OrdinalIgnoreCase) || AllowVirtual && d.BusType.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

    /// <summary>Dung lượng cần trên USB cho bộ cài.</summary>
    public static long RequiredBytes(string sourceRoot) =>
        Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) + 64L * 1024 * 1024;

    /// <summary>
    /// Ghi bộ cài vào USB. gpt = chỉ khởi động UEFI (máy mới); không thì MBR: UEFI + BIOS cũ.
    /// splitMb: kích thước mỗi phần khi chia install.wim lớn hơn 4 GB.
    /// </summary>
    public static async Task<OpResult> WriteAsync(int diskNumber, string sourceRoot, bool gpt, string label,
        Action<string> log, Action<int> progress, CancellationToken ct, int splitMb = 3800)
    {
        // 1. Kiểm tra lại ổ ngay trước khi xoá
        var disk = (await ListAsync(ct)).FirstOrDefault(d => d.Number == diskNumber)
                   ?? throw new InvalidOperationException("Ổ đã chọn không còn / không phải USB.");
        var media = await ImageService.ReadMediaAsync(sourceRoot, ct);
        var need = RequiredBytes(sourceRoot);
        if (disk.Size < need) return OpResult.Fail($"USB quá nhỏ: cần ít nhất {Fmt.Size(need)}, USB có {Fmt.Size(disk.Size)}.");
        var big = new FileInfo(media.ImagePath).Length > SplitOver && !media.IsSplit;
        if (big && !media.ImagePath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("install.esd lớn hơn 4 GB không chép được lên FAT32.");

        // 2. Xoá và phân vùng
        label = Regex.Replace(label.ToUpperInvariant(), "[^A-Z0-9_ ]", "");
        if (label.Length == 0) label = "WINSETUP";
        if (label.Length > 11) label = label[..11];
        var size = Math.Min(disk.Size - 4L * 1024 * 1024, Fat32Max);
        log($"Xoá và phân vùng {disk.Display} ({(gpt ? "GPT · UEFI" : "MBR · UEFI + BIOS")}, FAT32)…");
        progress(0);
        var script = $$"""
            $n = {{diskNumber}}
            $d = Get-Disk -Number $n
            if ($d.IsBoot -or $d.IsSystem) { throw 'Từ chối: đây là ổ hệ thống' }
            if ($d.IsOffline) { Set-Disk -Number $n -IsOffline $false }
            if ($d.IsReadOnly) { Set-Disk -Number $n -IsReadOnly $false }
            if ($d.PartitionStyle -ne 'RAW') { Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false }
            Initialize-Disk -Number $n -PartitionStyle {{(gpt ? "GPT" : "MBR")}} -ErrorAction SilentlyContinue
            $p = New-Partition -DiskNumber $n -Size {{size}} -AssignDriveLetter {{(gpt ? "" : "-IsActive")}}
            Start-Sleep -Seconds 2
            Format-Volume -Partition $p -FileSystem FAT32 -NewFileSystemLabel '{{label}}' -Confirm:$false -Force | Out-Null
            $p = Get-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber
            if (-not $p.DriveLetter -or $p.DriveLetter -eq [char]0) { $p | Add-PartitionAccessPath -AssignDriveLetter; $p = Get-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber }
            "$($p.DriveLetter):\"
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct);
        var dest = r.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        if (!r.Success || !Regex.IsMatch(dest, @"^[A-Z]:\\$")) return OpResult.Fail($"Không phân vùng / định dạng được USB: {OpResult.FirstLine(r.Text)}");
        log($"USB đã định dạng: {dest}");

        // 3. Chép file (bỏ install.wim lớn — sẽ chia ở bước sau)
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(f => !(big && string.Equals(f, media.ImagePath, StringComparison.OrdinalIgnoreCase))).ToList();
        var total = files.Sum(f => new FileInfo(f).Length) + (big ? new FileInfo(media.ImagePath).Length : 0);
        long done = 0;
        log($"Chép {files.Count} file ({Fmt.Size(total)})…");
        var buf = new byte[4 << 20];
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceRoot, f);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress((int)(done * 100 / Math.Max(1, total)));
                }
            }
            File.SetAttributes(target, FileAttributes.Normal);   // file từ ISO mang thuộc tính chỉ đọc
        }

        // 4. Chia install.wim > 4 GB thành install.swm, install2.swm...
        if (big)
        {
            log($"install.wim {Fmt.Size(new FileInfo(media.ImagePath).Length)} lớn hơn giới hạn 4 GB của FAT32 → chia thành các phần {splitMb} MB…");
            var baseDone = done;
            var wimSize = new FileInfo(media.ImagePath).Length;
            var swm = Path.Combine(dest, "sources", "install.swm");
            var s = await ProcessRunner.RunAsync("dism.exe", $"/English /Split-Image /ImageFile:\"{media.ImagePath}\" /SWMFile:\"{swm}\" /FileSize:{splitMb}",
                line =>
                {
                    var m = Regex.Match(line, @"(\d{1,3}(?:\.\d)?)%");
                    if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        progress((int)((baseDone + wimSize * v / 100) * 100 / total));
                }, ct, ProcessRunner.Oem);
            if (!s.Success) return OpResult.Fail($"Không chia được install.wim: {OpResult.FirstLine(s.Text)}");
            log($"Đã chia thành {Directory.GetFiles(Path.Combine(dest, "sources"), "install*.swm").Length} phần.");
        }
        progress(100);

        // 5. MBR: ghi mã khởi động cho máy BIOS cũ
        if (!gpt)
        {
            var bootsect = Path.Combine(sourceRoot, "boot", "bootsect.exe");
            if (File.Exists(bootsect))
            {
                var b = await ProcessRunner.ConsoleAsync(bootsect, $"/nt60 {dest.TrimEnd('\\')} /mbr /force", ct);
                log(b.Success ? "Đã ghi mã khởi động BIOS (bootsect /nt60)." : $"! bootsect: {OpResult.FirstLine(b.Text)}");
            }
            else log("Bộ cài không có boot\\bootsect.exe — USB chỉ khởi động được ở chế độ UEFI.");
        }

        var uefi = File.Exists(Path.Combine(dest, @"efi\boot\bootx64.efi")) || File.Exists(Path.Combine(dest, @"efi\boot\bootaa64.efi"));
        return OpResult.Success($"Đã tạo USB cài {media.Summary.Split(" · ")[0]} trên {dest}" +
                                (uefi ? "" : " (cảnh báo: không thấy efi\\boot — có thể không khởi động được UEFI)") + ".");
    }
}
