using System;
using System.Collections.Generic;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Máy có hợp với bản Windows đã chọn không: kiến trúc CPU, RAM, dung lượng ổ, điều kiện Windows 11,
    /// bản quyền số (đổi Pro ↔ Home sẽ không tự kích hoạt). Block = không cài được; Warn = cài được nhưng có hệ quả.
    /// </summary>
    public static class Compatibility
    {
        private const long GB = 1L << 30;

        /// <param name="targetDriveBytes">Tổng dung lượng phân vùng Windows sẽ cài vào.</param>
        /// <param name="cpuSupportsWin11">Kết quả kiểm tra CPU của app (null = không rõ).</param>
        public static List<Check> Evaluate(MachineFacts f, ImageEdition image, long targetDriveBytes, bool? cpuSupportsWin11 = null)
        {
            var list = new List<Check>();
            var arch = image.Architecture.Trim().ToLowerInvariant();
            var win11 = image.IsWindows11;
            var name = image.Name.Length > 0 ? image.Name : "bản đã chọn";

            // ---- Kiến trúc ----
            if (arch == "arm64")
                list.Add(new Check(CheckLevel.Block, $"{name} là bản cho máy ARM", "Máy này dùng CPU Intel / AMD — chọn bản x64."));
            else if (arch == "x64" && f.CpuBits == 32)
                list.Add(new Check(CheckLevel.Block, "CPU chỉ chạy được Windows 32-bit", $"{f.CpuName} không chạy được {name} (64-bit)."));
            else if (arch == "x86" && f.CpuBits == 64 && f.RamBytes >= 4 * GB)
                list.Add(new Check(CheckLevel.Info, "Nên dùng bản 64-bit", "Bản 32-bit chỉ dùng được tối đa ~3,5 GB RAM."));

            // ---- RAM ----
            var ram = f.RamBytes / (double)GB;
            if (f.RamBytes > 0)
            {
                var min = win11 ? 4 : arch == "x86" ? 1 : 2;
                if (ram < 1)
                    list.Add(new Check(CheckLevel.Block, $"RAM chỉ {ram:0.#} GB", "Không đủ để chạy Windows 10/11."));
                else if (ram < min - 0.5)
                    list.Add(new Check(CheckLevel.Warn, $"RAM {ram:0.#} GB thấp hơn mức tối thiểu {min} GB của {name}", "Máy sẽ chạy rất chậm."));
            }

            // ---- Dung lượng ổ Windows ----
            if (targetDriveBytes > 0)
            {
                var size = targetDriveBytes / (double)GB;
                if (size < 30)
                    list.Add(new Check(CheckLevel.Block, $"Ổ Windows chỉ {size:0} GB", "Cần tối thiểu 32 GB."));
                else if (win11 && size < 60)
                    list.Add(new Check(CheckLevel.Warn, $"Ổ Windows {size:0} GB", "Windows 11 cần 64 GB; sẽ hay báo đầy ổ và lỗi cập nhật."));
            }

            // ---- Điều kiện Windows 11 ----
            if (win11)
            {
                var missing = new List<string>();
                if (f.Firmware != FirmwareMode.Uefi) missing.Add("khởi động UEFI");
                if (f.Tpm < 2) missing.Add("TPM 2.0");
                if (f.SecureBoot == null) missing.Add("Secure Boot");
                if (cpuSupportsWin11 == false) missing.Add("CPU trong danh sách hỗ trợ");
                list.Add(missing.Count == 0
                    ? new Check(CheckLevel.Ok, "Máy đủ điều kiện Windows 11")
                    : new Check(CheckLevel.Warn, "Máy chưa đủ điều kiện Windows 11: " + string.Join(", ", missing),
                        "Vẫn cài được, nhưng Microsoft không hỗ trợ chính thức — có thể không nhận bản nâng cấp lớn. Nên chọn Windows 10 (hoặc LTSC)."));
            }
            else if (image.Build > 0 && image.Build < 22000 && f.Firmware == FirmwareMode.Uefi && f.Tpm >= 2 && cpuSupportsWin11 != false && ram >= 4)
                list.Add(new Check(CheckLevel.Info, "Máy đủ điều kiện Windows 11",
                    "Windows 10 (trừ bản LTSC) đã hết hỗ trợ từ 14/10/2025 — nên chọn Windows 11."));

            // ---- Bản quyền số: chỉ tự kích hoạt lại khi cùng loại bản (Home ↔ Home, Pro ↔ Pro) ----
            var cur = Family(f.EditionId);
            var img = Family(image.Edition.Length > 0 ? image.Edition : image.Name);
            if (cur.Length > 0 && img.Length > 0 && cur != img)
                list.Add(new Check(CheckLevel.Warn, $"Đổi từ {Label(cur)} sang {Label(img)}",
                    $"Bản quyền số của máy là {Label(cur)} — Windows mới sẽ KHÔNG tự kích hoạt. Chọn đúng bản {Label(cur)} để tự kích hoạt lại."));
            else if (cur.Length > 0 && cur == img)
                list.Add(new Check(CheckLevel.Ok, $"Cùng bản {Label(cur)} với Windows đang dùng", "Bản quyền số (nếu có) sẽ tự kích hoạt lại khi có mạng."));
            return list;
        }

        /// <summary>Nhóm bản để so bản quyền: Home / Pro / Enterprise / Education / LTSC / Eval.</summary>
        internal static string Family(string edition)
        {
            var e = edition.Replace(" ", "").ToLowerInvariant();
            if (e.Length == 0) return "";
            if (e.Contains("eval")) return "eval";
            if (e.Contains("ltsc") || e.Contains("enterprises")) return "ltsc";
            if (e.Contains("enterprise")) return "enterprise";
            if (e.Contains("education")) return "education";
            if (e.Contains("professional") || e.Contains("pro")) return "pro";
            if (e.Contains("core") || e.Contains("home")) return "home";
            return e;
        }

        private static string Label(string family) => family switch
        {
            "home" => "Home",
            "pro" => "Pro",
            "enterprise" => "Enterprise",
            "education" => "Education",
            "ltsc" => "LTSC",
            "eval" => "bản dùng thử",
            _ => family,
        };
    }
}
