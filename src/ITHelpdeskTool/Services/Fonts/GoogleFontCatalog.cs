using System.IO;
using System.IO.Compression;

namespace ITHelpdeskTool.Services.Fonts;

/// <summary>
/// Font tải về miễn phí, đều có đủ dấu tiếng Việt: font Google (giấy phép OFL, kho github.com/google/fonts)
/// và font mặc định của Microsoft 365 (Aptos) — Zip = gói zip chính thức trên download.microsoft.com.
/// </summary>
public sealed record WebFont(string Name, string Style, string Dir, string[] Files, string? Zip = null)
{
    public IEnumerable<string> Urls => Files.Select(f => $"https://raw.githubusercontent.com/google/fonts/main/ofl/{Dir}/{Uri.EscapeDataString(f)}");
}

public static class GoogleFontCatalog
{
    /// <summary>Gói font Aptos chính thức của Microsoft (28 kiểu: Aptos, Narrow, Display, Serif, Mono…).</summary>
    public const string AptosZip = "https://download.microsoft.com/download/8/6/0/860a94fa-7feb-44ef-ac79-c072d9113d69/Microsoft%20Aptos%20Fonts.zip";

    public static readonly IReadOnlyList<WebFont> Fonts =
    [
        // Font mặc định của Excel / Word / PowerPoint 365 từ 2024. Máy dùng WPS, Office 2016–2021 thiếu font này
        // nên file nhận từ Microsoft 365 hiển thị sai font (chữ kiểu máy đánh chữ).
        new("Aptos", "Font mặc định Office 365 · sửa lỗi file Excel/Word 365 mở trên WPS, Office cũ bị sai font · của Microsoft", "aptos",
            [], AptosZip),
        new("Be Vietnam Pro", "Không chân · thiết kế cho tiếng Việt", "bevietnampro",
            ["BeVietnamPro-Regular.ttf", "BeVietnamPro-Italic.ttf", "BeVietnamPro-Medium.ttf", "BeVietnamPro-SemiBold.ttf", "BeVietnamPro-Bold.ttf", "BeVietnamPro-BoldItalic.ttf", "BeVietnamPro-Light.ttf"]),
        new("Roboto", "Không chân · phổ biến", "roboto", ["Roboto[wdth,wght].ttf", "Roboto-Italic[wdth,wght].ttf"]),
        new("Open Sans", "Không chân · dễ đọc", "opensans", ["OpenSans[wdth,wght].ttf", "OpenSans-Italic[wdth,wght].ttf"]),
        new("Montserrat", "Không chân · tiêu đề", "montserrat", ["Montserrat[wght].ttf", "Montserrat-Italic[wght].ttf"]),
        new("Inter", "Không chân · giao diện", "inter", ["Inter[opsz,wght].ttf", "Inter-Italic[opsz,wght].ttf"]),
        new("Noto Sans", "Không chân · đa ngôn ngữ", "notosans", ["NotoSans[wdth,wght].ttf", "NotoSans-Italic[wdth,wght].ttf"]),
        new("Noto Serif", "Có chân · văn bản", "notoserif", ["NotoSerif[wdth,wght].ttf", "NotoSerif-Italic[wdth,wght].ttf"]),
        new("Lora", "Có chân · văn bản, sách", "lora", ["Lora[wght].ttf", "Lora-Italic[wght].ttf"]),
        new("Merriweather", "Có chân · đọc trên màn hình", "merriweather", ["Merriweather[opsz,wdth,wght].ttf", "Merriweather-Italic[opsz,wdth,wght].ttf"]),
        new("Playfair Display", "Có chân · tiêu đề sang trọng", "playfairdisplay", ["PlayfairDisplay[wght].ttf", "PlayfairDisplay-Italic[wght].ttf"]),
        new("Nunito", "Không chân · bo tròn", "nunito", ["Nunito[wght].ttf", "Nunito-Italic[wght].ttf"]),
        new("Source Sans 3", "Không chân · văn phòng", "sourcesans3", ["SourceSans3[wght].ttf", "SourceSans3-Italic[wght].ttf"]),
        new("Work Sans", "Không chân · hiện đại", "worksans", ["WorkSans[wght].ttf", "WorkSans-Italic[wght].ttf"]),
        new("Mulish", "Không chân · tối giản", "mulish", ["Mulish[wght].ttf", "Mulish-Italic[wght].ttf"]),
        new("Lexend", "Không chân · dễ đọc cho trẻ em", "lexend", ["Lexend[wght].ttf"]),
        new("Quicksand", "Không chân · bo tròn, thân thiện", "quicksand", ["Quicksand[wght].ttf"]),
        new("Baloo 2", "Trang trí · tròn, vui", "baloo2", ["Baloo2[wght].ttf"]),
        new("Dancing Script", "Viết tay", "dancingscript", ["DancingScript[wght].ttf"]),
    ];

    /// <summary>Tải các file của một font vào thư mục tạm, trả đường dẫn.</summary>
    public static async Task<List<string>> DownloadAsync(WebFont f, Action<string>? status = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Downloader.CacheDir, "googlefonts", f.Dir);
        if (f.Zip is { } zipUrl)
        {
            status?.Invoke($"Đang tải {f.Name}…");
            var zip = await Downloader.DownloadAsync(zipUrl, Path.Combine(Downloader.CacheDir, "fontzip"), f.Dir + ".zip", ct: ct);
            status?.Invoke($"Đang giải nén {f.Name}…");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var files = new List<string>();
            using (var archive = ZipFile.OpenRead(zip))
                foreach (var e in archive.Entries)
                {
                    // Chỉ lấy file font, bỏ thư mục con trong gói (tránh đường dẫn lạ)
                    if (e.Name.Length == 0 || !FontService.IsFontFile(e.Name)) continue;
                    var target = Path.Combine(dir, e.Name);
                    e.ExtractToFile(target, overwrite: true);
                    files.Add(target);
                }
            try { File.Delete(zip); } catch { }
            if (files.Count == 0) throw new InvalidOperationException($"gói tải về không có file font {f.Name}");
            return files;
        }
        var list = new List<string>();
        var i = 0;
        foreach (var (url, file) in f.Urls.Zip(f.Files))
        {
            status?.Invoke($"Đang tải {f.Name} ({++i}/{f.Files.Length})…");
            list.Add(await Downloader.DownloadAsync(url, dir, file, ct: ct));
        }
        return list;
    }
}
