namespace ITHelpdeskTool.Services.Store;

public enum StoreKind
{
    /// <summary>Cài qua winget theo Id.</summary>
    Winget,
    /// <summary>Tải bộ cài từ trang hãng rồi chạy (gỡ im lặng nếu nhận ra Inno/NSIS/MSI).</summary>
    Download,
    /// <summary>Bản chạy liền (zip): giải nén vào Program Files và tạo lối tắt.</summary>
    Portable,
}

/// <summary>
/// Một phần mềm trong kho.
/// Url (Download/Portable): link trực tiếp, hoặc "github:owner/repo|regex tên file" để lấy bản mới nhất trên GitHub.
/// Detect: regex tên trong danh sách phần mềm đã cài (cho mục không qua winget).
/// </summary>
public sealed record StoreApp(
    string Id, string Name, string Category, string Description,
    StoreKind Kind = StoreKind.Winget, string Url = "", string Detect = "", string Exe = "")
{
    public string Initial => Name.TrimStart('.', ' ').Length > 0 ? Name.TrimStart('.', ' ')[..1].ToUpperInvariant() : "?";
}

/// <summary>Danh sách phần mềm hay dùng. Id winget đã đối chiếu với kho microsoft/winget-pkgs.</summary>
public static class StoreCatalog
{
    public const string Browsers = "Trình duyệt";
    public const string Office = "Văn phòng & PDF";
    public const string Chat = "Chat & họp online";
    public const string Remote = "Điều khiển từ xa";
    public const string Vietnamese = "Gõ tiếng Việt";
    public const string Utils = "Tiện ích";
    public const string Media = "Media & đồ hoạ";
    public const string Tech = "Kỹ thuật & mạng";
    public const string Dev = "Lập trình";
    public const string Runtime = "Thư viện hệ thống";
    public const string Cloud = "Lưu trữ đám mây";

    public static readonly string[] Categories = [Browsers, Office, Chat, Remote, Vietnamese, Utils, Media, Tech, Dev, Runtime, Cloud];

    /// <summary>Theo nhóm (thứ tự <see cref="Categories"/>), trong nhóm xếp theo tên A→Z (kiểu tiếng Việt).</summary>
    public static readonly IReadOnlyList<StoreApp> Apps;

    static StoreCatalog()
    {
        var vi = StringComparer.Create(new System.Globalization.CultureInfo("vi-VN"), ignoreCase: true);
        Apps = Raw.OrderBy(a => Array.IndexOf(Categories, a.Category)).ThenBy(a => a.Name.TrimStart('.', ' '), vi).ToList();
    }

    private static readonly IReadOnlyList<StoreApp> Raw =
    [
        // ---- Trình duyệt ----
        new("Google.Chrome", "Google Chrome", Browsers, "Trình duyệt phổ biến nhất"),
        new("CocCoc.CocCoc", "Cốc Cốc", Browsers, "Trình duyệt Việt, tải video, sửa lỗi chính tả tiếng Việt"),
        new("Mozilla.Firefox", "Mozilla Firefox", Browsers, "Trình duyệt mã nguồn mở, chú trọng riêng tư"),
        new("Microsoft.Edge", "Microsoft Edge", Browsers, "Trình duyệt mặc định của Windows"),
        new("Brave.Brave", "Brave", Browsers, "Chặn quảng cáo sẵn"),
        new("Opera.Opera", "Opera", Browsers, "Có VPN miễn phí tích hợp"),
        new("CentStudio.CentBrowser", "Cent Browser", Browsers, "Nhân Chromium, nhẹ, nhiều tuỳ chỉnh tab và chuột"),

        // ---- Văn phòng ----
        new("Foxit.FoxitReader", "Foxit PDF Reader", Office, "Đọc, ký, điền form PDF — nhẹ"),
        new("Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat Reader", Office, "Trình đọc PDF chuẩn của Adobe"),
        new("SumatraPDF.SumatraPDF", "Sumatra PDF", Office, "Đọc PDF/EPUB siêu nhẹ"),
        new("TheDocumentFoundation.LibreOffice", "LibreOffice", Office, "Bộ văn phòng miễn phí, mở được file Word/Excel"),
        new("ONLYOFFICE.DesktopEditors", "ONLYOFFICE", Office, "Bộ văn phòng miễn phí, giao diện giống MS Office"),
        new("Kingsoft.WPSOffice", "WPS Office", Office, "Bộ văn phòng miễn phí"),
        new("Notion.Notion", "Notion", Office, "Ghi chú, quản lý công việc"),
        new("Obsidian.Obsidian", "Obsidian", Office, "Ghi chú Markdown"),
        new("Mozilla.Thunderbird", "Thunderbird", Office, "Ứng dụng email miễn phí"),
        new("Cyanfish.NAPS2", "NAPS2", Office, "Scan tài liệu ra PDF, có OCR"),
        // Phần mềm của ngthhung — lấy bản mới nhất trên GitHub Releases
        new("pdf-editor-lite", "PDF Editor Lite", Office, "Xem, sửa PDF tiếng Việt: ghi chú, sắp xếp trang, OCR, ký số — miễn phí", StoreKind.Download,
            @"github:ngthhung-dev/pdf-editor-lite|Setup\.exe$", @"^PDF Editor Lite\b"),
        new("mail-lite", "Mail Lite", Office, "Đọc mail nhiều tài khoản (Gmail, Microsoft 365, IMAP), giao diện kiểu Outlook", StoreKind.Download,
            @"github:ngthhung-dev/mail-lite|Setup\.exe$", @"^Mail Lite\b"),
        new("bartender-lite", "Bartender Lite", Office, "Thiết kế và in tem nhãn, mã vạch — miễn phí", StoreKind.Download,
            @"github:ngthhung-dev/bartender-lite|Setup\.exe$", @"^Bartender Lite\b"),

        // ---- Chat ----
        new("zalo", "Zalo PC", Chat, "Chat, gọi video — tải từ trang Zalo", StoreKind.Download,
            "https://zalo.me/download/zalo-pc?utm=90000", @"^Zalo\b"),
        new("Telegram.TelegramDesktop", "Telegram", Chat, "Chat, gửi file lớn"),
        new("Zoom.Zoom", "Zoom", Chat, "Họp trực tuyến"),
        new("Microsoft.Teams", "Microsoft Teams", Chat, "Họp, chat công việc"),
        new("SlackTechnologies.Slack", "Slack", Chat, "Chat nhóm công việc"),
        new("Rakuten.Viber", "Viber", Chat, "Chat, gọi miễn phí"),
        new("Discord.Discord", "Discord", Chat, "Chat thoại cộng đồng"),
        new("Tencent.WeChat.Universal", "WeChat", Chat, "Chat, gọi video — phổ biến với đối tác Trung Quốc"),
        new("9NKSQGP7F2NH", "WhatsApp", Chat, "Chat, gọi video (cài từ Microsoft Store)"),

        // ---- Điều khiển từ xa ----
        new("DucFabulous.UltraViewer", "UltraViewer", Remote, "Điều khiển máy tính từ xa — phổ biến ở Việt Nam"),
        new("TeamViewer.TeamViewer", "TeamViewer", Remote, "Điều khiển từ xa"),
        new("AnyDesk.AnyDesk", "AnyDesk", Remote, "Điều khiển từ xa, nhanh và nhẹ"),
        new("TeamViewer.TeamViewer.QuickSupport", "TeamViewer QuickSupport", Remote, "Bản cho người cần được hỗ trợ, không cần cài"),
        new("Microsoft.RemoteDesktopClient", "Remote Desktop", Remote, "Kết nối RDP / Azure Virtual Desktop"),
        new("mRemoteNG.mRemoteNG", "mRemoteNG", Remote, "Quản lý nhiều kết nối RDP/SSH/VNC"),
        new("rustdesk", "RustDesk", Remote, "Điều khiển từ xa mã nguồn mở, miễn phí — thay TeamViewer/UltraViewer", StoreKind.Download,
            @"github:rustdesk/rustdesk|x86_64\.msi$", @"^RustDesk\b"),

        // ---- Gõ tiếng Việt ----
        new("unikey", "UniKey", Vietnamese, "Bộ gõ tiếng Việt quen thuộc (bản chạy liền)", StoreKind.Portable,
            "https://www.unikey.org/assets/release/unikey46RC2-230919-win64.zip", "", "UniKeyNT.exe"),
        new("evkey", "EVKey", Vietnamese, "Bộ gõ tiếng Việt, sửa lỗi gõ trên Chrome/Excel (bản chạy liền)", StoreKind.Portable,
            @"github:lamquangminh/EVKey|\.zip$", "", "EVKey64.exe"),

        // ---- Tiện ích ----
        new("7zip.7zip", "7-Zip", Utils, "Nén / giải nén zip, rar, 7z"),
        new("RARLab.WinRAR", "WinRAR", Utils, "Nén / giải nén rar"),
        new("Bandisoft.Bandizip", "Bandizip", Utils, "Nén / giải nén, giao diện đẹp"),
        new("voidtools.Everything", "Everything", Utils, "Tìm file tức thì trên toàn ổ đĩa"),
        new("Microsoft.PowerToys", "PowerToys", Utils, "Bộ tiện ích của Microsoft: đổi tên hàng loạt, chọn màu, chia màn hình…"),
        new("ShareX.ShareX", "ShareX", Utils, "Chụp / quay màn hình"),
        new("Greenshot.Greenshot", "Greenshot", Utils, "Chụp màn hình, ghi chú nhanh"),
        new("Skillbrains.Lightshot", "Lightshot", Utils, "Chụp một vùng màn hình bằng phím PrtSc, chia sẻ link nhanh"),
        new("Tonec.InternetDownloadManager", "Internet Download Manager", Utils, "Tăng tốc tải file (bản dùng thử)"),
        new("qBittorrent.qBittorrent", "qBittorrent", Utils, "Tải torrent, không quảng cáo"),
        new("Ookla.Speedtest.Desktop", "Speedtest", Utils, "Đo tốc độ mạng"),

        // ---- Media ----
        new("VideoLAN.VLC", "VLC", Media, "Xem mọi định dạng video"),
        new("Daum.PotPlayer", "PotPlayer", Media, "Xem video, phụ đề tốt"),
        new("CodecGuide.K-LiteCodecPack.Standard", "K-Lite Codec Pack", Media, "Bộ codec xem video"),
        new("Spotify.Spotify", "Spotify", Media, "Nghe nhạc"),
        new("OBSProject.OBSStudio", "OBS Studio", Media, "Quay màn hình, livestream"),
        new("GIMP.GIMP.3", "GIMP", Media, "Chỉnh sửa ảnh miễn phí"),
        new("Inkscape.Inkscape", "Inkscape", Media, "Vẽ vector miễn phí"),
        new("IrfanSkiljan.IrfanView", "IrfanView", Media, "Xem / chuyển định dạng ảnh"),
        new("Audacity.Audacity", "Audacity", Media, "Thu âm, cắt ghép âm thanh"),
        new("HandBrake.HandBrake", "HandBrake", Media, "Nén / chuyển định dạng video"),
        new("Canva.Canva", "Canva", Media, "Thiết kế nhanh"),
        new("Figma.Figma", "Figma", Media, "Thiết kế giao diện"),
        new("ByteDance.CapCut", "CapCut", Media, "Dựng video, phụ đề tự động"),

        // ---- Kỹ thuật ----
        new("CPUID.CPU-Z", "CPU-Z", Tech, "Thông tin CPU, RAM, mainboard"),
        new("TechPowerUp.GPU-Z", "GPU-Z", Tech, "Thông tin card đồ hoạ"),
        new("REALiX.HWiNFO", "HWiNFO", Tech, "Thông tin phần cứng, nhiệt độ"),
        new("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", Tech, "Sức khoẻ ổ cứng"),
        new("CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", Tech, "Đo tốc độ ổ cứng"),
        new("WinDirStat.WinDirStat", "WinDirStat", Tech, "Xem thư mục nào chiếm dung lượng"),
        new("Rufus.Rufus", "Rufus", Tech, "Tạo USB cài Windows"),
        new("Ventoy.Ventoy", "Ventoy", Tech, "USB boot nhiều file ISO"),
        new("Microsoft.Sysinternals.Suite", "Sysinternals Suite", Tech, "Bộ công cụ chẩn đoán của Microsoft"),
        new("Klocman.BulkCrapUninstaller", "Bulk Crap Uninstaller", Tech, "Gỡ hàng loạt phần mềm"),
        new("PuTTY.PuTTY", "PuTTY", Tech, "SSH / Telnet"),
        new("WinSCP.WinSCP", "WinSCP", Tech, "Truyền file SFTP / FTP"),
        new("WiresharkFoundation.Wireshark", "Wireshark", Tech, "Bắt gói tin mạng"),
        new("angryziber.AngryIPScanner", "Angry IP Scanner", Tech, "Quét IP trong mạng LAN"),
        new("OpenVPNTechnologies.OpenVPN", "OpenVPN", Tech, "Kết nối VPN"),
        new("WireGuard.WireGuard", "WireGuard", Tech, "Kết nối VPN nhanh"),
        new("Tailscale.Tailscale", "Tailscale", Tech, "Mạng riêng ảo giữa các máy"),
        new("Malwarebytes.Malwarebytes", "Malwarebytes", Tech, "Quét mã độc"),
        new("Bitwarden.Bitwarden", "Bitwarden", Tech, "Quản lý mật khẩu"),
        new("KeePassXCTeam.KeePassXC", "KeePassXC", Tech, "Quản lý mật khẩu offline"),
        new("PassMark.KeyboardTest", "KeyboardTest", Tech, "Test bàn phím: phím nào liệt, kẹt, nhận đúp (bản dùng thử)"),
        new("PassMark.MonitorTest", "MonitorTest", Tech, "Test màn hình: điểm chết, sọc, màu, độ đồng đều (bản dùng thử)"),
        new("PassMark.SoundCheck", "SoundCheck", Tech, "Test loa, tai nghe, micro: kênh trái/phải, âm lượng"),

        // ---- Lập trình ----
        new("Microsoft.VisualStudioCode", "Visual Studio Code", Dev, "Trình soạn mã"),
        new("Git.Git", "Git", Dev, "Quản lý mã nguồn"),
        new("Python.Python.3.13", "Python 3.13", Dev, "Ngôn ngữ Python"),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", Dev, "JavaScript runtime"),
        new("Microsoft.WindowsTerminal", "Windows Terminal", Dev, "Cửa sổ dòng lệnh nhiều tab"),
        new("Microsoft.PowerShell", "PowerShell 7", Dev, "PowerShell bản mới"),
        new("Postman.Postman", "Postman", Dev, "Thử API"),
        new("DBeaver.DBeaver.Community", "DBeaver", Dev, "Quản lý cơ sở dữ liệu"),
        new("Microsoft.SQLServerManagementStudio", "SQL Server Management Studio", Dev, "Quản lý SQL Server"),
        new("Docker.DockerDesktop", "Docker Desktop", Dev, "Container"),
        new("Oracle.VirtualBox", "VirtualBox", Dev, "Máy ảo"),
        new("JetBrains.Toolbox", "JetBrains Toolbox", Dev, "Cài IDE của JetBrains"),
        new("Notepad++.Notepad++", "Notepad++", Dev, "Soạn thảo văn bản / mã"),
        new("Google.Antigravity", "Google Antigravity", Dev, "Trình soạn mã có AI agent của Google"),
        new("Anthropic.Claude", "Claude", Dev, "Trợ lý AI Claude bản desktop"),
        new("9NT1R1C2HH7J", "ChatGPT", Dev, "Trợ lý AI ChatGPT bản desktop (cài từ Microsoft Store)"),
        new("ApacheFriends.Xampp.8.2", "XAMPP", Dev, "Apache + MySQL (MariaDB) + PHP chạy web trên máy"),

        // ---- Thư viện hệ thống ----
        new("Microsoft.VCRedist.2015+.x64", "Visual C++ 2015–2022 (x64)", Runtime, "Thư viện nhiều phần mềm / game cần"),
        new("Microsoft.VCRedist.2015+.x86", "Visual C++ 2015–2022 (x86)", Runtime, "Bản 32-bit, cho phần mềm cũ"),
        new("Microsoft.VCRedist.2013.x64", "Visual C++ 2013 (x64)", Runtime, "Cho phần mềm cũ"),
        new("Microsoft.VCRedist.2013.x86", "Visual C++ 2013 (x86)", Runtime, "Cho phần mềm cũ"),
        new("Microsoft.VCRedist.2012.x64", "Visual C++ 2012 (x64)", Runtime, "Cho phần mềm cũ"),
        new("Microsoft.VCRedist.2012.x86", "Visual C++ 2012 (x86)", Runtime, "Cho phần mềm cũ"),
        new("Microsoft.VCRedist.2010.x64", "Visual C++ 2010 (x64)", Runtime, "Cho phần mềm cũ"),
        new("Microsoft.VCRedist.2010.x86", "Visual C++ 2010 (x86)", Runtime, "Cho phần mềm cũ"),
        new("Microsoft.VCRedist.2008.x64", "Visual C++ 2008 (x64)", Runtime, "Cho phần mềm rất cũ"),
        new("Microsoft.VCRedist.2008.x86", "Visual C++ 2008 (x86)", Runtime, "Cho phần mềm rất cũ"),
        new("Microsoft.DotNet.DesktopRuntime.8", ".NET Desktop Runtime 8", Runtime, "Cho phần mềm viết bằng .NET 8"),
        new("Microsoft.DotNet.DesktopRuntime.9", ".NET Desktop Runtime 9", Runtime, "Cho phần mềm viết bằng .NET 9"),
        new("Microsoft.DirectX", "DirectX End-User Runtime", Runtime, "Thư viện DirectX 9 cho game / phần mềm cũ"),
        new("Oracle.JavaRuntimeEnvironment", "Java 8 (JRE)", Runtime, "Cho phần mềm Java: chữ ký số, khai thuế…"),
        new("Microsoft.OpenJDK.21", "Microsoft OpenJDK 21", Runtime, "Java bản mới"),

        // ---- Đám mây ----
        new("Google.GoogleDrive", "Google Drive", Cloud, "Đồng bộ Google Drive"),
        new("Microsoft.OneDrive", "OneDrive", Cloud, "Đồng bộ OneDrive"),
        new("Dropbox.Dropbox", "Dropbox", Cloud, "Đồng bộ Dropbox"),
    ];

    public static StoreApp? Find(string id) => Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
