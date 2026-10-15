using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Disk;

public sealed record PhysicalDiskInfo(
    int Number, string Model, string Media, string Bus, long Size, string Health, string Style, bool IsSystem,
    int? Temperature, int? Wear, long? PowerOnHours, long? ReadErrors, long? WriteErrors)
{
    public string Kind => Media is "SSD" ? (Bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "SSD NVMe" : "SSD") : Media is "HDD" ? "HDD" : Bus;
    public string HealthText => Health switch { "Healthy" => "Tốt", "Warning" => "Cảnh báo", "Unhealthy" => "Hỏng", _ => Health.Length > 0 ? Health : "Không rõ" };
    public string Tone => Health == "Healthy" && (Wear ?? 0) < 90 ? "Good" : Health == "Unhealthy" ? "Bad" : "Warn";
    public string Detail => string.Join(" · ", new[]
    {
        Kind, Fmt.Size(Size), Style, Temperature is > 0 ? $"{Temperature}°C" : "", Wear is { } w ? $"đã dùng {w}% tuổi thọ" : "",
        PowerOnHours is > 0 ? $"{PowerOnHours:N0} giờ chạy" : "", (ReadErrors ?? 0) + (WriteErrors ?? 0) > 0 ? $"{ReadErrors + WriteErrors} lỗi đọc/ghi" : "",
    }.Where(s => s.Length > 0));
}

public sealed record VolumeInfo(
    string Letter, string Label, string FileSystem, long Size, long Free, int DiskNumber, int PartitionNumber, bool IsSystem, bool IsBoot,
    string Health, long MaxSize)
{
    public string Root => Letter.Length > 0 ? Letter + ":\\" : "";
    public double UsedPercent => Size > 0 ? 100.0 * (Size - Free) / Size : 0;
    public string Display => $"{(Letter.Length > 0 ? Letter + ":" : "(không ký tự)")} {(Label.Length > 0 ? Label : "Local Disk")}";
    public string SpaceText => $"{Fmt.Size(Size - Free)} / {Fmt.Size(Size)} · trống {Fmt.Size(Free)} · {FileSystem}";
    public bool CanExtend => MaxSize - Size > 64L * 1024 * 1024;
    public bool IsProtected => IsSystem || IsBoot || Letter.Equals(Path.GetPathRoot(Environment.SystemDirectory)?[..1], StringComparison.OrdinalIgnoreCase);
}

public sealed record FolderSize(string Path, string Name, long Bytes, int Files, bool IsDir);

/// <summary>Ổ đĩa: sức khoẻ, phân vùng, tối ưu, kiểm tra, đổi ký tự / nhãn, định dạng, mở rộng, đo tốc độ, phân tích dung lượng.</summary>
public static class DiskService
{
    private static List<JsonElement> Arr(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
    }

    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    private static long L(JsonElement e, string n) => long.TryParse(S(e, n), out var v) ? v : 0;
    private static long? NL(JsonElement e, string n) => long.TryParse(S(e, n), out var v) ? v : null;
    private static bool B(JsonElement e, string n) => S(e, n).Equals("True", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<PhysicalDiskInfo>> DisksAsync()
    {
        const string script = """
            $sys = (Get-Partition -DriveLetter ($env:SystemDrive[0]) -ErrorAction SilentlyContinue).DiskNumber
            @(Get-PhysicalDisk | ForEach-Object {
              $pd = $_
              $d = Get-Disk | Where-Object { $_.Number -eq [int]$pd.DeviceId } | Select-Object -First 1
              $r = $pd | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
              [pscustomobject]@{ Number=[int]$pd.DeviceId; Model="$($pd.FriendlyName)".Trim(); Media="$($pd.MediaType)"; Bus="$($pd.BusType)"; Size=[int64]$pd.Size;
                Health="$($pd.HealthStatus)"; Style="$($d.PartitionStyle)"; System=($pd.DeviceId -eq "$sys");
                Temp=$r.Temperature; Wear=$r.Wear; Hours=$r.PowerOnHours; RErr=$r.ReadErrorsTotal; WErr=$r.WriteErrorsTotal }
            }) | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        return Arr(r.Output).Select(e => new PhysicalDiskInfo((int)L(e, "Number"), S(e, "Model"), S(e, "Media"), S(e, "Bus"), L(e, "Size"), S(e, "Health"),
                S(e, "Style"), B(e, "System"), (int?)NL(e, "Temp") is > 0 and < 150 ? (int?)NL(e, "Temp") : null, (int?)NL(e, "Wear"),
                NL(e, "Hours"), NL(e, "RErr"), NL(e, "WErr")))
            .OrderBy(d => d.Number).ToList();
    }

    public static async Task<List<VolumeInfo>> VolumesAsync()
    {
        const string script = """
            @(Get-Partition | Where-Object { $_.DriveLetter -or $_.IsBoot } | ForEach-Object {
              $p = $_; $v = $p | Get-Volume -ErrorAction SilentlyContinue
              if ($v -and ($v.DriveType -eq 'Fixed' -or $v.DriveType -eq 'Removable')) {
                $max = 0; try { $max = ($p | Get-PartitionSupportedSize -ErrorAction Stop).SizeMax } catch {}
                [pscustomobject]@{ Letter="$($p.DriveLetter)".Trim([char]0); Label="$($v.FileSystemLabel)"; FS="$($v.FileSystem)"; Size=[int64]$v.Size; Free=[int64]$v.SizeRemaining;
                  Disk=$p.DiskNumber; Part=$p.PartitionNumber; System=[bool]$p.IsSystem; Boot=[bool]$p.IsBoot; Health="$($v.HealthStatus)"; Max=[int64]$max }
              }
            }) | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        return Arr(r.Output).Select(e => new VolumeInfo(S(e, "Letter"), S(e, "Label"), S(e, "FS"), L(e, "Size"), L(e, "Free"), (int)L(e, "Disk"), (int)L(e, "Part"),
                B(e, "System"), B(e, "Boot"), S(e, "Health"), L(e, "Max")))
            .Where(v => v.Size > 0).OrderBy(v => v.Letter.Length == 0).ThenBy(v => v.Letter).ToList();
    }

    // ================= Thao tác =================
    /// <summary>Tối ưu: SSD → TRIM, HDD → chống phân mảnh (defrag /O tự chọn đúng cách theo loại ổ).</summary>
    public static async Task<OpResult> OptimizeAsync(VolumeInfo v, Action<string> log, Action<int> progress, CancellationToken ct)
    {
        log($"> defrag {v.Letter}: /O /U /V");
        var r = await ProcessRunner.RunAsync("defrag.exe", $"{v.Letter}: /O /U /V", line =>
        {
            var t = line.Replace("\0", "").Split('\r').LastOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
            var m = Regex.Match(t, @"(\d{1,3})\s?%");
            if (m.Success) progress(int.Parse(m.Groups[1].Value));
            if (t.Length > 0 && !m.Success) log(t);
        }, ct, ProcessRunner.Oem);
        return r.ExitCode == 0 ? OpResult.Success($"Đã tối ưu ổ {v.Letter}:.") : OpResult.Fail($"Tối ưu ổ {v.Letter}: lỗi (mã {r.ExitCode}).");
    }

    public static async Task<OpResult> CheckAsync(VolumeInfo v, Action<string> log, Action<int> progress, CancellationToken ct)
    {
        log($"> chkdsk {v.Letter}: /scan");
        var r = await ProcessRunner.RunAsync("chkdsk.exe", $"{v.Letter}: /scan", line =>
        {
            var t = line.Replace("\0", "").Split('\r').LastOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
            var m = Regex.Match(t, @"(\d{1,3})\s?%");
            if (m.Success) progress(int.Parse(m.Groups[1].Value));
            else if (t.Length > 0) log(t);
        }, ct, ProcessRunner.Oem);
        return r.ExitCode switch
        {
            0 => OpResult.Success($"Ổ {v.Letter}: không có lỗi."),
            1 => OpResult.Success($"Ổ {v.Letter}: đã tìm và sửa lỗi."),
            2 => OpResult.Success($"Ổ {v.Letter}: cần dọn dẹp thêm khi khởi động lại."),
            _ => OpResult.Fail($"Ổ {v.Letter}: phát hiện lỗi chưa sửa được khi đang chạy — dùng “Sửa lỗi ổ hệ thống khi khởi động” ở trang Sửa lỗi Windows hoặc chạy chkdsk {v.Letter}: /f."),
        };
    }

    public static async Task<OpResult> SetLabelAsync(VolumeInfo v, string label)
    {
        var r = await ProcessRunner.PowerShellAsync($"Set-Volume -DriveLetter {v.Letter} -NewFileSystemLabel {ProcessRunner.PsQuote(label.Trim())}");
        return OpResult.From(r, $"Đã đổi tên ổ {v.Letter}: thành “{label.Trim()}”.", "Không đổi được tên ổ");
    }

    public static async Task<OpResult> SetLetterAsync(VolumeInfo v, char letter)
    {
        if (v.IsProtected) return OpResult.Fail("Không đổi ký tự ổ hệ thống.");
        var r = await ProcessRunner.PowerShellAsync($"Set-Partition -DiskNumber {v.DiskNumber} -PartitionNumber {v.PartitionNumber} -NewDriveLetter {letter}");
        return OpResult.From(r, $"Đã đổi ổ {v.Letter}: thành {letter}:.", "Không đổi được ký tự ổ");
    }

    public static async Task<OpResult> ExtendAsync(VolumeInfo v)
    {
        var r = await ProcessRunner.PowerShellAsync(
            $"$s = Get-PartitionSupportedSize -DiskNumber {v.DiskNumber} -PartitionNumber {v.PartitionNumber}; Resize-Partition -DiskNumber {v.DiskNumber} -PartitionNumber {v.PartitionNumber} -Size $s.SizeMax");
        return OpResult.From(r, $"Đã mở rộng ổ {v.Letter}: thêm {Fmt.Size(v.MaxSize - v.Size)}.", "Không mở rộng được");
    }

    public static async Task<OpResult> FormatAsync(VolumeInfo v, string fs, string label, bool quick = true)
    {
        if (v.IsProtected) return OpResult.Fail("Không định dạng ổ hệ thống.");
        var r = await ProcessRunner.PowerShellAsync(
            $"Format-Volume -DriveLetter {v.Letter} -FileSystem {fs} -NewFileSystemLabel {ProcessRunner.PsQuote(label)} -Confirm:$false -Force {(quick ? "" : "-Full")} | Out-Null");
        return OpResult.From(r, $"Đã định dạng ổ {v.Letter}: ({fs}).", "Không định dạng được");
    }

    /// <summary>Ký tự ổ còn trống.</summary>
    public static List<char> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).ToList();
    }

    // ================= Đo tốc độ =================
    /// <summary>Ghi rồi đọc tuần tự một file thử (không qua bộ đệm Windows) để đo MB/s.</summary>
    public static Task<(double WriteMBs, double ReadMBs)> SpeedTestAsync(VolumeInfo v, int sizeMb, Action<int> progress, CancellationToken ct) =>
        Task.Run(() => SpeedTest(v, sizeMb, progress, ct), ct);

    private static unsafe (double, double) SpeedTest(VolumeInfo v, int sizeMb, Action<int> progress, CancellationToken ct)
    {
        var dir = Path.Combine(v.Root, "ithd-speedtest");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.bin");
        const int block = 4 << 20;
        // FILE_FLAG_NO_BUFFERING cần bộ đệm căn theo sector → cấp phát bộ nhớ căn 4 KB
        var ptr = System.Runtime.InteropServices.NativeMemory.AlignedAlloc(block, 4096);
        try
        {
            var span = new Span<byte>(ptr, block);
            Random.Shared.NextBytes(span);
            long total = (long)sizeMb << 20;
            const FileOptions NoBuffering = (FileOptions)0x20000000;
            var sw = Stopwatch.StartNew();
            using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering, total))
            {
                for (long off = 0; off < total; off += block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, span, off);
                    progress((int)(off * 50 / total));
                }
            }
            var w = sizeMb / sw.Elapsed.TotalSeconds;
            sw.Restart();
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                for (long off = 0; off < total; off += block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, span, off);
                    progress(50 + (int)(off * 50 / total));
                }
            }
            return (w, sizeMb / sw.Elapsed.TotalSeconds);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree(ptr);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ================= Phân tích dung lượng =================
    /// <summary>Dung lượng từng thư mục / file con trực tiếp của <paramref name="dir"/>, lớn nhất trước.</summary>
    public static List<FolderSize> Analyze(string dir, CancellationToken ct)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var result = new List<FolderSize>();
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch { subs = []; }
        Parallel.ForEach(subs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, d =>
        {
            long bytes = 0;
            var files = 0;
            try
            {
                foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", opts))
                {
                    if (ct.IsCancellationRequested) break;
                    try { bytes += f.Length; files++; } catch { }
                }
            }
            catch { }
            lock (result) result.Add(new FolderSize(d, Path.GetFileName(d), bytes, files, true));
        });
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
                result.Add(new FolderSize(f.FullName, f.Name, f.Length, 1, false));
        }
        catch { }
        return result.OrderByDescending(r => r.Bytes).ToList();
    }
}
