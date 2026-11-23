using System;
using System.Collections.Generic;
using System.Linq;

namespace ITHelpdeskTool.Reinstall
{
    public enum SoftwareKind
    {
        /// <summary>Cài bằng winget theo Id.</summary>
        Winget,
        /// <summary>Tải file .msi / .exe rồi chạy im lặng (Args).</summary>
        Installer,
        /// <summary>Tải .zip bản chạy liền, giải nén vào Program Files, tạo lối tắt cho Exe.</summary>
        Portable,
    }

    /// <summary>Phần mềm cài ở lần đăng nhập đầu tiên của Windows mới.</summary>
    public sealed class SoftwareItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public SoftwareKind Kind { get; set; } = SoftwareKind.Winget;
        /// <summary>Link tải; "github:owner/repo|regex" = file trong bản phát hành mới nhất trên GitHub.</summary>
        public string Url { get; set; } = "";
        /// <summary>Tham số cài im lặng cho .exe (msi tự dùng /qn).</summary>
        public string Args { get; set; } = "";
        /// <summary>File chạy chính (bản Portable) để tạo lối tắt.</summary>
        public string Exe { get; set; } = "";
        public bool Default { get; set; }
        /// <summary>Mô tả ngắn hiện dưới tên trong danh sách chọn.</summary>
        public string Description { get; set; } = "";

        public SoftwareItem Clone() => (SoftwareItem)MemberwiseClone();
    }

    public enum TweakScope
    {
        /// <summary>Cả máy — chạy ở SetupComplete.cmd (quyền SYSTEM, trước lần đăng nhập đầu).</summary>
        Machine,
        /// <summary>Tài khoản mới — chạy ở lần đăng nhập đầu.</summary>
        User,
    }

    /// <summary>
    /// Một tinh chỉnh áp sau khi cài: danh sách lệnh cmd (reg add, sc config, dism…).
    /// <see cref="Danger"/> = ảnh hưởng bảo mật / cập nhật → mặc định không chọn, có cảnh báo.
    /// </summary>
    public sealed class TweakItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public TweakScope Scope { get; set; } = TweakScope.User;
        public List<string> Commands { get; set; } = new List<string>();
        public bool Danger { get; set; }
        public string Warning { get; set; } = "";
        public bool Default { get; set; }

        public TweakItem Clone()
        {
            var t = (TweakItem)MemberwiseClone();
            t.Commands = new List<string>(Commands);
            return t;
        }
    }

    /// <summary>Danh sách gợi ý mặc định (giao diện cho người dùng chọn bớt / thêm).</summary>
    public static class Catalog
    {
        private const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        // Bộ cài Inno Setup (IT Helpdesk Tool, PDF Editor Lite): cài im lặng
        private const string InnoSilent = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";

        /// <summary>Phần mềm gợi ý cài sẵn (cùng nguồn với App Store của app), xếp A-Z.</summary>
        public static IReadOnlyList<SoftwareItem> Software { get; } = new List<SoftwareItem>
        {
            // ---- Mặc định chọn ----
            new SoftwareItem { Id = "7zip.7zip", Name = "7-Zip", Default = true, Description = "Nén / giải nén zip, rar, 7z" },
            // Không dùng winget: gói CocCoc.CocCoc trên winget còn bản 120 cũ, bộ cài mồi treo trên Windows mới (VM 45048174171).
            // Bộ cài chính thức (Chromium updater): /silent /install, cài cho cả máy.
            new SoftwareItem { Id = "CocCoc.CocCoc", Name = "Cốc Cốc", Kind = SoftwareKind.Installer, Default = true, Description = "Trình duyệt Việt, tải video",
                Url = "https://files.coccoc.com/browser/download/vi?plat=win&arch=x64", Args = "/silent /install" },
            new SoftwareItem { Id = "evkey", Name = "EVKey", Kind = SoftwareKind.Portable, Default = true, Description = "Bộ gõ tiếng Việt (bản chạy liền)",
                Url = @"github:lamquangminh/EVKey|^EVKey\.zip$", Exe = "EVKey64.exe" },
            new SoftwareItem { Id = "Google.Chrome", Name = "Google Chrome", Default = true, Description = "Trình duyệt" },
            new SoftwareItem { Id = "it-helpdesk-tool", Name = "IT Helpdesk Tool", Kind = SoftwareKind.Installer, Default = true, Description = "Bộ công cụ IT này",
                Url = @"github:ngthhung-dev/it-helpdesk-tool|Setup-[\d.]+\.exe$", Args = InnoSilent },
            new SoftwareItem { Id = "CodecGuide.K-LiteCodecPack.Standard", Name = "K-Lite Codec Pack", Default = true, Description = "Bộ codec xem mọi định dạng video" },
            new SoftwareItem { Id = "mail-lite", Name = "Mail Lite", Kind = SoftwareKind.Installer, Default = true, Description = "Đọc mail nhiều tài khoản, kiểu Outlook",
                Url = @"github:ngthhung-dev/mail-lite|Setup\.exe$", Args = "/S" },
            new SoftwareItem { Id = "Mozilla.Firefox", Name = "Mozilla Firefox", Default = true, Description = "Trình duyệt" },
            new SoftwareItem { Id = "pdf-editor-lite", Name = "PDF Editor Lite", Kind = SoftwareKind.Installer, Default = true, Description = "Xem, sửa PDF tiếng Việt",
                Url = @"github:ngthhung-dev/pdf-editor-lite|Setup\.exe$", Args = InnoSilent },
            new SoftwareItem { Id = "rustdesk", Name = "RustDesk", Kind = SoftwareKind.Installer, Default = true, Description = "Điều khiển từ xa, miễn phí",
                Url = @"github:rustdesk/rustdesk|x86_64\.msi$" },
            new SoftwareItem { Id = "DucFabulous.UltraViewer", Name = "UltraViewer", Default = true, Description = "Điều khiển từ xa" },
            // Args trống = FirstLogon tự nhận kiểu bộ cài (Inno / NSIS…) như App Store
            new SoftwareItem { Id = "zalo", Name = "Zalo PC", Kind = SoftwareKind.Installer, Default = true, Description = "Chat, gọi video",
                Url = "https://zalo.me/download/zalo-pc?utm=90000" },

            // ---- Không chọn sẵn ----
            new SoftwareItem { Id = "bartender-lite", Name = "Bartender Lite", Kind = SoftwareKind.Installer, Description = "Thiết kế, in tem nhãn, mã vạch",
                Url = @"github:ngthhung-dev/bartender-lite|Setup\.exe$" },
            new SoftwareItem { Id = "OpenAI.ChatGPT", Name = "ChatGPT", Description = "Trợ lý AI ChatGPT bản desktop" },
            new SoftwareItem { Id = "Anthropic.Claude", Name = "Claude", Description = "Trợ lý AI Claude bản desktop" },
            new SoftwareItem { Id = "Oracle.JavaRuntimeEnvironment", Name = "Java 8 (JRE)", Description = "Cho phần mềm Java: chữ ký số, khai thuế…" },
            new SoftwareItem { Id = "Skillbrains.Lightshot", Name = "Lightshot", Description = "Chụp vùng màn hình bằng phím PrtSc" },
            new SoftwareItem { Id = "Python.Python.3.13", Name = "Python 3.13", Description = "Ngôn ngữ lập trình Python" },
            new SoftwareItem { Id = "Foxit.FoxitReader", Name = "Foxit PDF Reader", Description = "Đọc, ký, điền form PDF" },
            new SoftwareItem { Id = "Telegram.TelegramDesktop", Name = "Telegram", Description = "Chat, gửi file lớn" },
            new SoftwareItem { Id = "unikey", Name = "UniKey", Kind = SoftwareKind.Portable, Description = "Bộ gõ tiếng Việt (bản chạy liền)",
                Url = "https://www.unikey.org/assets/release/unikey46RC2-230919-win64.zip", Exe = "UniKeyNT.exe" },
            new SoftwareItem { Id = "VideoLAN.VLC", Name = "VLC", Description = "Xem video" },
            new SoftwareItem { Id = "Tencent.WeChat.Universal", Name = "WeChat", Description = "Chat với đối tác Trung Quốc" },
            new SoftwareItem { Id = "9NKSQGP7F2NH", Name = "WhatsApp", Description = "Chat (từ Microsoft Store)" },
            new SoftwareItem { Id = "RARLab.WinRAR", Name = "WinRAR", Description = "Nén / giải nén rar" },
            new SoftwareItem { Id = "Zoom.Zoom", Name = "Zoom", Description = "Họp trực tuyến" },
        }.OrderBy(s => s.Name, StringComparer.InvariantCultureIgnoreCase).ToList();

        public static IReadOnlyList<TweakItem> Tweaks { get; } = new List<TweakItem>
        {
            // ---- An toàn, mặc định chọn ----
            new TweakItem { Id = "ext", Title = "Hiện đuôi file", Default = true,
                Commands = { $@"reg add ""{Adv}"" /v HideFileExt /t REG_DWORD /d 0 /f" } },
            new TweakItem { Id = "thispc", Title = "Mở File Explorer vào This PC", Default = true,
                Commands = { $@"reg add ""{Adv}"" /v LaunchTo /t REG_DWORD /d 1 /f" } },
            new TweakItem { Id = "classicmenu", Title = "Menu chuột phải kiểu cũ (Windows 11)", Default = true,
                Commands = { @"reg add ""HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"" /ve /f" } },
            new TweakItem { Id = "taskbarleft", Title = "Taskbar căn trái (Windows 11)",
                Commands = { $@"reg add ""{Adv}"" /v TaskbarAl /t REG_DWORD /d 0 /f" } },
            new TweakItem { Id = "suggestions", Title = "Tắt gợi ý, quảng cáo trong Start và màn hình khoá", Default = true,
                Commands =
                {
                    @"reg add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f",
                    @"reg add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338388Enabled /t REG_DWORD /d 0 /f",
                    @"reg add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SoftLandingEnabled /t REG_DWORD /d 0 /f",
                } },
            new TweakItem { Id = "longpath", Title = "Cho phép đường dẫn dài trên 260 ký tự", Scope = TweakScope.Machine, Default = true,
                Commands = { @"reg add ""HKLM\SYSTEM\CurrentControlSet\Control\FileSystem"" /v LongPathsEnabled /t REG_DWORD /d 1 /f" } },
            new TweakItem { Id = "highperf", Title = "Không cho ổ cứng / USB ngủ khi cắm sạc", Scope = TweakScope.Machine,
                Commands = { "powercfg /change disk-timeout-ac 0", "powercfg /change standby-timeout-ac 0" } },

            // ---- Nguy hiểm: mặc định KHÔNG chọn ----
            new TweakItem { Id = "nouac", Title = "Tắt UAC", Scope = TweakScope.Machine, Danger = true,
                Warning = "Mọi chương trình chạy với quyền cao nhất mà không hỏi — virus dễ chiếm quyền máy.",
                Commands = { @"reg add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"" /v EnableLUA /t REG_DWORD /d 0 /f" } },
            new TweakItem { Id = "nowu", Title = "Tắt Windows Update tự động", Scope = TweakScope.Machine, Danger = true,
                Warning = "Máy không nhận bản vá bảo mật. Chỉ dùng cho máy chuyên dụng không ra Internet.",
                Commands = { @"reg add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"" /v NoAutoUpdate /t REG_DWORD /d 1 /f" } },
            new TweakItem { Id = "smb1", Title = "Bật SMB1 (máy in, NAS đời cũ)", Scope = TweakScope.Machine, Danger = true,
                Warning = "SMB1 là giao thức cũ có lỗ hổng (WannaCry). Chỉ bật khi thiết bị cũ bắt buộc.",
                Commands = { "dism /online /enable-feature /featurename:SMB1Protocol /all /norestart" } },
            new TweakItem { Id = "nodefender", Title = "Tắt quét thời gian thực của Microsoft Defender", Scope = TweakScope.Machine, Danger = true,
                Warning = "Máy không còn diệt virus. Windows có thể tự bật lại (Tamper Protection).",
                Commands = { @"reg add ""HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection"" /v DisableRealtimeMonitoring /t REG_DWORD /d 1 /f" } },
        };

        public static List<SoftwareItem> DefaultSoftware() => Software.Where(s => s.Default).Select(s => s.Clone()).ToList();
        public static List<TweakItem> DefaultTweaks() => Tweaks.Where(t => t.Default && !t.Danger).Select(t => t.Clone()).ToList();
    }
}
