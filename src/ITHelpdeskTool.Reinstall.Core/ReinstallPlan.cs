using System.Collections.Generic;

namespace ITHelpdeskTool.Reinstall
{
    public enum FirmwareMode { Uefi, Bios }

    /// <summary>Mọi lựa chọn của một lần cài lại Windows.</summary>
    public sealed class ReinstallPlan
    {
        /// <summary>File cài: .iso, hoặc install.wim / install.esd (đã giải nén).</summary>
        public string ImagePath { get; set; } = "";

        /// <summary>Số thứ tự bản Windows trong file cài (Home, Pro…), xem bằng <see cref="ImageInfo.Read"/>.</summary>
        public int ImageIndex { get; set; } = 1;

        /// <summary>Thư mục đệm trên ổ KHÁC ổ Windows sẽ bị xoá, vd D:\ITH.</summary>
        public string StagingDir { get; set; } = @"D:\ITH";

        /// <summary>
        /// Thư mục đệm nằm trên phân vùng "ITH-STAGING" do app tạo bằng cách thu nhỏ ổ Windows (máy chỉ có ổ C:).
        /// Cài xong, Windows mới chép bản sao lưu về C:\ITH-Backup rồi xoá phân vùng này và nới C: lại.
        /// </summary>
        public bool AutoStaging { get; set; }

        /// <summary>Ổ Windows đang chạy sẽ bị format (vd C:).</summary>
        public string TargetDrive { get; set; } = "C:";

        /// <summary>Kiểu khởi động của máy (đọc bằng <see cref="BootEntry.DetectFirmware"/>).</summary>
        public FirmwareMode Firmware { get; set; } = FirmwareMode.Uefi;

        /// <summary>Kiến trúc của bản Windows sẽ cài: amd64 hoặc x86.</summary>
        public string Architecture { get; set; } = "amd64";

        // ---- Tài khoản và vùng ----
        /// <summary>
        /// true = Windows mới hiện màn hình đăng nhập tài khoản Microsoft / tạo tài khoản lúc khởi động lần đầu (cần mạng),
        /// không tạo tài khoản cục bộ, không tự đăng nhập. false = tạo tài khoản cục bộ <see cref="AccountName"/>.
        /// </summary>
        public bool MicrosoftAccount { get; set; }
        public string AccountName { get; set; } = "Admin";
        /// <summary>Rỗng = không đặt mật khẩu.</summary>
        public string Password { get; set; } = "";
        /// <summary>"*" = Windows tự đặt tên máy.</summary>
        public string ComputerName { get; set; } = "*";
        public string TimeZone { get; set; } = "SE Asia Standard Time";
        /// <summary>Ngôn ngữ giao diện = ngôn ngữ có sẵn trong bộ cài (tự lấy từ file cài). Rỗng = để Windows tự chọn.</summary>
        public string UiLanguage { get; set; } = "";
        public string UserLocale { get; set; } = "vi-VN";
        public string SystemLocale { get; set; } = "vi-VN";
        /// <summary>Bàn phím: US (gõ tiếng Việt bằng UniKey / EVKey).</summary>
        public string InputLocale { get; set; } = "0409:00000409";

        // ---- Sau khi cài ----
        public List<SoftwareItem> Software { get; set; } = new List<SoftwareItem>();
        public List<TweakItem> Tweaks { get; set; } = new List<TweakItem>();

        /// <summary>Xuất driver của máy hiện tại và nạp vào Windows mới.</summary>
        public bool ExportDrivers { get; set; } = true;

        /// <summary>Mục đã sao lưu (trong &lt;thư mục đệm&gt;\backup) sẽ tự chép lại vào Windows mới ở lần đăng nhập đầu.</summary>
        public List<BackupItem> Restore { get; set; } = new List<BackupItem>();

        /// <summary>Lệnh chạy cuối cùng sau khi cài phần mềm (dùng khi thử tự động, vd tắt máy ảo).</summary>
        public string AfterPostInstall { get; set; } = "";
    }
}
