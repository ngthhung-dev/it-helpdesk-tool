using System.IO;

namespace ITHelpdeskTool.Services.Fonts;

/// <summary>Font miễn phí (giấy phép OFL) từ kho github.com/google/fonts, đều có đủ dấu tiếng Việt.</summary>
public sealed record WebFont(string Name, string Style, string Dir, string[] Files)
{
    public IEnumerable<string> Urls => Files.Select(f => $"https://raw.githubusercontent.com/google/fonts/main/ofl/{Dir}/{Uri.EscapeDataString(f)}");
}

public static class GoogleFontCatalog
{
    public static readonly IReadOnlyList<WebFont> Fonts =
    [
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
