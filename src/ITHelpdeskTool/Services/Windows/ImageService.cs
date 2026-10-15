using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>Một phiên bản (index) trong install.wim / install.esd.</summary>
public sealed record WimEdition(int Index, string Name, string Description, long Size, string Arch, string Version, string Language)
{
    public string Display => $"{Index}. {Name}";
}

/// <summary>Thông tin bộ cài Windows (ISO đã gắn hoặc thư mục / USB).</summary>
public sealed record SetupMedia(string Root, string ImagePath, long ImageSize, List<WimEdition> Editions)
{
    public bool IsEsd => ImagePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase);
    public bool IsSplit => ImagePath.EndsWith(".swm", StringComparison.OrdinalIgnoreCase);
    public bool Over4Gb => ImageSize > 4L * 1024 * 1024 * 1024 - 1;
    public string SetupExe => Path.Combine(Root, "setup.exe");
    public string Summary
    {
        get
        {
            var e = Editions.FirstOrDefault();
            return e is null ? Path.GetFileName(ImagePath)
                : $"Windows {Major(e.Version)} · build {e.Version} · {e.Arch} · {e.Language} · {Editions.Count} phiên bản · {Path.GetFileName(ImagePath)} {Fmt.Size(ImageSize)}";
        }
    }

    private static string Major(string v) => Version.TryParse(v, out var x) ? (x.Build >= 22000 ? "11" : x.Build >= 10240 ? "10" : x.ToString(2)) : "";
}

/// <summary>Gắn / gỡ ISO, đọc bộ cài, kiểm tra mã băm.</summary>
public static class ImageService
{
    /// <summary>Gắn ISO, trả ký tự ổ (vd "F:\").</summary>
    public static async Task<string> MountAsync(string iso, CancellationToken ct = default)
    {
        var q = ProcessRunner.PsQuote(iso);
        var script = $$"""
            $img = Get-DiskImage -ImagePath {{q}}
            if (-not $img.Attached) { $img = Mount-DiskImage -ImagePath {{q}} -PassThru }
            $v = $null
            for ($i = 0; $i -lt 20 -and -not $v; $i++) { $v = ($img | Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter | Select-Object -First 1); if (-not $v) { Start-Sleep -Milliseconds 300 } }
            if (-not $v) { throw 'ISO đã gắn nhưng chưa có ký tự ổ' }
            "$($v.DriveLetter):\"
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct);
        var d = r.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        if (!r.Success || !Regex.IsMatch(d, @"^[A-Z]:\\$")) throw new InvalidOperationException($"Không gắn được ISO: {OpResult.FirstLine(r.Text)}");
        return d;
    }

    public static Task DismountAsync(string iso) =>
        ProcessRunner.PowerShellAsync($"Dismount-DiskImage -ImagePath {ProcessRunner.PsQuote(iso)} | Out-Null");

    /// <summary>Đọc bộ cài trong thư mục gốc (ổ ISO đã gắn / USB / thư mục giải nén).</summary>
    public static async Task<SetupMedia> ReadMediaAsync(string root, CancellationToken ct = default)
    {
        var src = Path.Combine(root, "sources");
        var image = new[] { "install.wim", "install.esd", "install.swm" }.Select(f => Path.Combine(src, f)).FirstOrDefault(File.Exists)
                    ?? throw new InvalidOperationException("Không phải bộ cài Windows (thiếu sources\\install.wim / install.esd).");
        var size = image.EndsWith(".swm", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetFiles(src, "install*.swm").Sum(f => new FileInfo(f).Length)
            : new FileInfo(image).Length;
        return new SetupMedia(root, image, size, await EditionsAsync(image, ct));
    }

    /// <summary>Danh sách phiên bản trong file ảnh (dism /Get-WimInfo, ép tiếng Anh để đọc ổn định).</summary>
    public static async Task<List<WimEdition>> EditionsAsync(string image, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync("dism.exe", $"/English /Get-WimInfo /WimFile:\"{image}\"", ct: ct, encoding: ProcessRunner.Oem);
        if (!r.Success) throw new InvalidOperationException($"Không đọc được file ảnh: {OpResult.FirstLine(r.Text)}");
        var list = new List<WimEdition>();
        foreach (Match m in Regex.Matches(r.Output, @"Index\s*:\s*(\d+)\s*\r?\nName\s*:\s*(.*?)\r?\nDescription\s*:\s*(.*?)\r?\nSize\s*:\s*([\d,\.]+)"))
            list.Add(new WimEdition(int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim(),
                long.TryParse(Regex.Replace(m.Groups[4].Value, @"[,\.]", ""), out var sz) ? sz : 0, "", "", ""));
        // Kiến trúc / build / ngôn ngữ: đọc chi tiết index đầu
        if (list.Count > 0)
        {
            var d = await ProcessRunner.RunAsync("dism.exe", $"/English /Get-WimInfo /WimFile:\"{image}\" /Index:1", ct: ct, encoding: ProcessRunner.Oem);
            string F(string label) => Regex.Match(d.Output, "^" + label + @"\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var arch = F("Architecture");
            var ver = F("Version");
            var lang = Regex.Match(d.Output, @"Languages\s*:\s*\r?\n\s*(\S+)").Groups[1].Value;
            list = list.Select(e => e with { Arch = arch, Version = ver, Language = lang }).ToList();
        }
        return list;
    }

    /// <summary>SHA-256 của file lớn, báo tiến độ 0-100.</summary>
    public static async Task<string> Sha256Async(string file, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[4 << 20];
        long done = 0;
        var last = -1;
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            done += n;
            var p = (int)(done * 100 / Math.Max(1, fs.Length));
            if (p != last) { progress?.Report(p); last = p; }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static string NormalizeHash(string s) => Regex.Replace(s, @"[^0-9A-Fa-f]", "").ToUpper(CultureInfo.InvariantCulture);
}
