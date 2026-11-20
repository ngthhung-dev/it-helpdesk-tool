using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Một bản Windows trong file install.wim / install.esd.</summary>
    public sealed class ImageEdition
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public long Size { get; set; }
        /// <summary>x64, x86, ARM64 (chỉ có khi đọc chi tiết).</summary>
        public string Architecture { get; set; } = "";
        public string Version { get; set; } = "";

        public override string ToString() => $"{Index}. {Name}" + (Architecture.Length > 0 ? $" ({Architecture}, {Version})" : "");
    }

    /// <summary>Đọc danh sách bản Windows trong file cài bằng dism /Get-WimInfo (có sẵn từ Windows 7).</summary>
    public static class ImageInfo
    {
        public static List<ImageEdition> Read(IRunner run, string wimOrEsd, bool details = false)
        {
            var r = run.Run("dism.exe", $"/English /Get-WimInfo /WimFile:\"{wimOrEsd}\"");
            if (!r.Ok) throw new ReinstallException($"Không đọc được file cài {Path.GetFileName(wimOrEsd)}: {ProcessRunner.FirstLines(r.Output, 3)}");
            var list = Parse(r.Output);
            if (details)
                foreach (var e in list)
                {
                    var d = run.Run("dism.exe", $"/English /Get-WimInfo /WimFile:\"{wimOrEsd}\" /Index:{e.Index}");
                    if (!d.Ok) continue;
                    var one = Parse(d.Output);
                    if (one.Count > 0) { e.Architecture = one[0].Architecture; e.Version = one[0].Version; }
                }
            return list;
        }

        /// <summary>Tách khối "Index : n / Name : … / Description : … / Size : …" trong output của dism.</summary>
        public static List<ImageEdition> Parse(string output)
        {
            var list = new List<ImageEdition>();
            ImageEdition? cur = null;
            foreach (var raw in output.Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s*([A-Za-z ]+?)\s*:\s*(.*?)\s*$");
                if (!m.Success) continue;
                var key = m.Groups[1].Value.Trim();
                var val = m.Groups[2].Value;
                switch (key)
                {
                    case "Index":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                        {
                            cur = new ImageEdition { Index = i };
                            list.Add(cur);
                        }
                        break;
                    case "Name" when cur != null: cur.Name = val; break;
                    case "Description" when cur != null: cur.Description = val; break;
                    case "Size" when cur != null:
                        var digits = Regex.Replace(val, @"[^\d]", "");
                        if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) cur.Size = s;
                        break;
                    case "Architecture" when cur != null: cur.Architecture = val; break;
                    case "Version" when cur != null: cur.Version = val; break;
                }
            }
            return list;
        }

        /// <summary>amd64 / x86 / arm64 cho unattend.xml từ tên kiến trúc của dism.</summary>
        public static string UnattendArch(string dismArch) => dismArch.Trim().ToLowerInvariant() switch
        {
            "x86" => "x86",
            "arm64" => "arm64",
            _ => "amd64",
        };
    }

    /// <summary>
    /// Mở file .iso (Windows 8 trở lên: Mount-DiskImage) để lấy install.wim/esd, boot.wim, boot.sdi.
    /// Windows 7 không có Mount-DiskImage — bản Win 7 sẽ thay bằng cách đọc ISO khác (bước sau).
    /// </summary>
    public sealed class IsoMount : IDisposable
    {
        private readonly IRunner _run;
        private readonly string _iso;

        public string Root { get; }

        private IsoMount(IRunner run, string iso, string root)
        {
            _run = run;
            _iso = iso;
            Root = root;
        }

        public static IsoMount Mount(IRunner run, string iso)
        {
            var q = iso.Replace("'", "''");
            var r = run.Run("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"$i = Mount-DiskImage -ImagePath '" + q + "' -PassThru; " +
                "($i | Get-Volume).DriveLetter\"");
            var letter = r.Output.Trim();
            if (!r.Ok || letter.Length == 0 || !char.IsLetter(letter[letter.Length - 1]))
                throw new ReinstallException($"Không mở được file ISO: {ProcessRunner.FirstLines(r.Output, 3)}");
            return new IsoMount(run, iso, letter[letter.Length - 1] + @":\");
        }

        public void Dispose()
        {
            var q = _iso.Replace("'", "''");
            _run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Dismount-DiskImage -ImagePath '" + q + "' | Out-Null\"");
        }
    }

    /// <summary>Các file cần lấy từ bộ cài Windows (ISO đã mở hoặc thư mục giải nén).</summary>
    public sealed class MediaFiles
    {
        public string InstallImage { get; set; } = "";
        public string BootWim { get; set; } = "";
        public string BootSdi { get; set; } = "";

        /// <summary>Tìm trong thư mục gốc bộ cài: sources\install.wim|esd|swm, sources\boot.wim, boot\boot.sdi.</summary>
        public static MediaFiles FromRoot(string root)
        {
            var src = Path.Combine(root, "sources");
            var m = new MediaFiles
            {
                InstallImage = FirstExisting(Path.Combine(src, "install.wim"), Path.Combine(src, "install.esd")),
                BootWim = Path.Combine(src, "boot.wim"),
                BootSdi = Path.Combine(root, "boot", "boot.sdi"),
            };
            if (m.InstallImage.Length == 0)
                throw new ReinstallException(File.Exists(Path.Combine(src, "install.swm"))
                    ? "Bộ cài dùng install.swm (chia nhiều phần) — chưa hỗ trợ, hãy dùng ISO chính thức của Microsoft."
                    : "Không thấy sources\\install.wim hoặc install.esd trong bộ cài.");
            if (!File.Exists(m.BootWim)) throw new ReinstallException("Không thấy sources\\boot.wim trong bộ cài.");
            if (!File.Exists(m.BootSdi)) throw new ReinstallException("Không thấy boot\\boot.sdi trong bộ cài.");
            return m;
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (var p in paths) if (File.Exists(p)) return p;
            return "";
        }
    }
}
