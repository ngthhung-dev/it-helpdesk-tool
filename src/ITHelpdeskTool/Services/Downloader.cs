using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Services;

/// <summary>Tải file qua HTTPS, báo tiến độ.</summary>
public static class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(60),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) ITHelpdeskTool/{AppInfo.Version}");
        return c;
    }

    /// <summary>Thư mục tải về của app (xoá được bất cứ lúc nào).</summary>
    public static string CacheDir
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "ITHelpdeskTool");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>
    /// Tải <paramref name="url"/> vào thư mục <paramref name="dir"/>. Tên file lấy từ máy chủ (Content-Disposition / link cuối sau chuyển hướng).
    /// progress: (đã tải, tổng — 0 nếu không rõ).
    /// </summary>
    public static async Task<string> DownloadAsync(string url, string dir, string? fileName = null,
        Action<long, long>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        fileName ??= resp.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                     ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                     ?? Path.GetFileName(Uri.UnescapeDataString(resp.RequestMessage?.RequestUri?.AbsolutePath ?? ""));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";
        fileName = string.Concat(fileName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var path = Path.Combine(dir, fileName);
        var tmp = path + ".part";
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[256 * 1024];
            long done = 0;
            var last = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.Now - last).TotalMilliseconds > 250) { progress?.Invoke(done, total); last = DateTime.Now; }
            }
            progress?.Invoke(done, total);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>"github:owner/repo|regex" → link tải asset của bản phát hành mới nhất. Link thường thì trả nguyên.</summary>
    public static async Task<string> ResolveAsync(string url, CancellationToken ct = default)
    {
        if (!url.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) return url;
        var spec = url[7..].Split('|', 2);
        var repo = spec[0];
        var rx = new Regex(spec.ElementAtOrDefault(1) ?? ".", RegexOptions.IgnoreCase);
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (rx.IsMatch(name)) return a.GetProperty("browser_download_url").GetString()!;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            // API giới hạn 60 lần/giờ cho mỗi IP → đọc trang phát hành thay thế
            Log.Warn($"GitHub API bị giới hạn, đọc trang phát hành của {repo}");
            using var resp = await Http.GetAsync($"https://github.com/{repo}/releases/latest", ct);
            var tag = resp.RequestMessage?.RequestUri?.AbsolutePath.Split('/').LastOrDefault() ?? "";
            var html = await Http.GetStringAsync($"https://github.com/{repo}/releases/expanded_assets/{tag}", ct);
            foreach (Match m in Regex.Matches(html, $@"href=""(/{Regex.Escape(repo)}/releases/download/[^""]+)""", RegexOptions.IgnoreCase))
            {
                var link = m.Groups[1].Value;
                if (rx.IsMatch(Uri.UnescapeDataString(link.Split('/').Last()))) return "https://github.com" + link;
            }
        }
        throw new InvalidOperationException($"Bản phát hành mới nhất của {repo} không có file phù hợp");
    }

    public static Task<string> GetStringAsync(string url, CancellationToken ct = default) => Http.GetStringAsync(url, ct);
}
