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

        public static IReadOnlyList<SoftwareItem> Software { get; } = new List<SoftwareItem>
        {
            new SoftwareItem { Id = "Google.Chrome", Name = "Google Chrome", Default = true },
            new SoftwareItem { Id = "unikey", Name = "UniKey", Kind = SoftwareKind.Portable, Default = true,
                Url = "https://www.unikey.org/assets/release/unikey46RC2-230919-win64.zip", Exe = "UniKeyNT.exe" },
            new SoftwareItem { Id = "rustdesk", Name = "RustDesk", Kind = SoftwareKind.Installer, Default = true,
                Url = @"github:rustdesk/rustdesk|x86_64\.msi$" },
            new SoftwareItem { Id = "RARLab.WinRAR", Name = "WinRAR", Default = true },
            new SoftwareItem { Id = "7zip.7zip", Name = "7-Zip" },
            new SoftwareItem { Id = "Foxit.FoxitReader", Name = "Foxit PDF Reader" },
            new SoftwareItem { Id = "Zoom.Zoom", Name = "Zoom" },
            new SoftwareItem { Id = "VideoLAN.VLC", Name = "VLC" },
        };

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
