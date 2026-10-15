using System.IO;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Windows;

/// <summary>Nguồn tải Windows chính thức của Microsoft. Tool = file .exe tải về chạy luôn; còn lại mở trang web.</summary>
public sealed record OfficialDownload(string Key, string Name, string Description, string Url, bool Tool, string FileName = "");

public static class WindowsDownloads
{
    public static readonly IReadOnlyList<OfficialDownload> Items =
    [
        new("win11-page", "Windows 11 — trang tải ISO", "Trang chính thức của Microsoft: chọn phiên bản, ngôn ngữ rồi tải file ISO đa phiên bản (x64).",
            "https://www.microsoft.com/software-download/windows11", false),
        new("win11-mct", "Windows 11 — Media Creation Tool", "Công cụ của Microsoft tạo USB cài đặt hoặc tải file ISO Windows 11.",
            "https://go.microsoft.com/fwlink/?linkid=2156295", true, "MediaCreationToolW11.exe"),
        new("win11-assistant", "Windows 11 — Installation Assistant", "Nâng cấp máy đang chạy Windows 10 lên Windows 11, giữ nguyên dữ liệu.",
            "https://go.microsoft.com/fwlink/?linkid=2171764", true, "Windows11InstallationAssistant.exe"),
        new("win10-page", "Windows 10 — trang tải", "Trang chính thức tải Windows 10 (Media Creation Tool / ISO).",
            "https://www.microsoft.com/software-download/windows10", false),
        new("win10-mct", "Windows 10 — Media Creation Tool", "Tạo USB cài đặt hoặc tải ISO Windows 10 22H2.",
            "https://go.microsoft.com/fwlink/?LinkId=691209", true, "MediaCreationTool22H2.exe"),
        new("eval", "Windows Enterprise / Server — bản dùng thử", "Microsoft Evaluation Center: ISO bản đánh giá cho doanh nghiệp và máy chủ.",
            "https://www.microsoft.com/evalcenter", false),
        new("vlsc", "Bản cấp phép số lượng lớn (Volume)", "Doanh nghiệp có hợp đồng Volume Licensing tải ISO trong Microsoft 365 admin center.",
            "https://admin.microsoft.com/adminportal/home#/subscriptions/vlnew/downloadsandkeys", false),
    ];

    public static string DownloadDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Tải công cụ của Microsoft về thư mục Downloads, kiểm tra chữ ký số rồi trả đường dẫn.</summary>
    public static async Task<string> DownloadToolAsync(OfficialDownload d, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var path = await Downloader.DownloadAsync(d.Url, DownloadDir, d.FileName.Length > 0 ? d.FileName : null, progress, ct);
        var (ok, detail) = await Downloader.IsMicrosoftSignedAsync(path, ct);
        if (!ok)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException($"File tải về không có chữ ký hợp lệ của Microsoft — đã xoá ({detail}).");
        }
        return path;
    }
}
