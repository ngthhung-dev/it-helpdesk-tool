using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Url, string FileName, long Size, string Sha256, string Notes);

/// <summary>
/// Cập nhật app từ GitHub Releases: tìm bản phát hành mới nhất, tải bộ cài, đối chiếu kích thước / SHA256 / phiên bản,
/// rồi chạy bộ cài im lặng — bộ cài tự đóng app nếu còn mở và mở lại app khi cài xong.
/// </summary>
public static class UpdateService
{
    public const string Repo = "ngthhung-dev/it-helpdesk-tool";
    private static readonly Regex SetupName = new(@"^ITHelpdeskTool-Setup-(\d+\.\d+\.\d+)\.exe$", RegexOptions.IgnoreCase);

    public static Version Current => Version.TryParse(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>Bản phát hành mới nhất có bộ cài (null nếu chưa có).</summary>
    public static async Task<UpdateInfo?> LatestAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Downloader.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var m = SetupName.Match(name);
                if (!m.Success) continue;
                // GitHub ghi sẵn "digest": "sha256:<hex>" cho từng file đính kèm
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                return new UpdateInfo(Version.Parse(m.Groups[1].Value), tag, a.GetProperty("browser_download_url").GetString()!, name,
                    a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "", notes.Trim());
            }
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            // API giới hạn 60 lần/giờ cho mỗi IP → lấy tag từ trang phát hành (không có kích thước / SHA256 để đối chiếu)
            Log.Warn("GitHub API bị giới hạn, đọc trang phát hành để kiểm tra cập nhật");
            var tag = (await Downloader.FinalUrlAsync($"https://github.com/{Repo}/releases/latest", ct))?.AbsolutePath.Split('/').LastOrDefault() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || v.Build < 0) return null;
            var name = $"ITHelpdeskTool-Setup-{v.ToString(3)}.exe";
            return new UpdateInfo(v, tag, $"https://github.com/{Repo}/releases/download/{tag}/{name}", name, 0, "", "");
        }
    }

    /// <summary>Tải bộ cài rồi kiểm tra: đủ kích thước, đúng SHA256 GitHub công bố, đúng tên sản phẩm và phiên bản.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var path = await Downloader.DownloadAsync(u.Url, Path.Combine(Downloader.CacheDir, "update"), u.FileName, progress, ct);
        var len = new FileInfo(path).Length;
        if (u.Size > 0 && len != u.Size) Reject(path, $"file tải về không đủ ({len:N0}/{u.Size:N0} byte)");
        if (u.Sha256.Length > 0)
        {
            string hash;
            await using (var f = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!hash.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) Reject(path, "mã SHA256 không khớp với bản phát hành");
        }
        // Inno Setup đệm dấu cách vào sau các chuỗi version info ("1.0.40" + 44 dấu cách) → cắt bỏ,
        // so phiên bản bằng phần số (ProductMajor/Minor/Build) thay vì đọc chuỗi
        var info = FileVersionInfo.GetVersionInfo(path);
        var product = (info.ProductName ?? "").Trim().TrimEnd('\0');
        var fv = new Version(info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart);
        if (!product.Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase) || fv != u.Version)
            Reject(path, $"bộ cài không đúng ({product} {fv.ToString(3)})");
        return path;
    }

    private static void Reject(string path, string why)
    {
        try { File.Delete(path); } catch { }
        throw new InvalidOperationException(why);
    }

    /// <summary>
    /// Chạy bộ cài im lặng (chỉ hiện thanh tiến độ): /CLOSEAPPLICATIONS để bộ cài tự đóng app nếu còn mở,
    /// /relaunch=1 để bộ cài mở lại app sau khi cài xong (mục [Run] trong ITHelpdeskTool.iss).
    /// </summary>
    public static void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1") { UseShellExecute = true });
}
