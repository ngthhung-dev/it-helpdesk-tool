using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Máy Windows 7 (bản nhẹ .NET 4.8): không có Mount-DiskImage, lệnh ổ đĩa của PowerShell (Get-Partition…),
    /// và DISM 6.1 không có /Export-Image, /Apply-Image, không đọc được .esd (LZMS).
    /// Thay bằng: đọc ISO trong code (DiscUtils), đọc danh sách bản Windows từ phần XML của file WIM/ESD,
    /// và wimlib-imagex (mã nguồn mở, đi kèm bộ cài) để xuất / bung / sửa file .wim.
    /// </summary>
    public static class Platform
    {
        /// <summary>Đặt ITH_LEGACY=1 để chạy đường Win 7 trên Windows mới (thử trên máy ảo).</summary>
        public const string ForceVariable = "ITH_LEGACY";

        public static bool IsLegacy =>
            Environment.GetEnvironmentVariable(ForceVariable) == "1" ||
            (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version < new Version(6, 2));

        /// <summary>wimlib-imagex.exe đi kèm: &lt;thư mục app&gt;\wimlib\x64 (hoặc x86)\wimlib-imagex.exe.</summary>
        public static string Wimlib
        {
            get
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                foreach (var p in new[] { Path.Combine(baseDir, "wimlib", arch, "wimlib-imagex.exe"), Path.Combine(baseDir, "wimlib-imagex.exe") })
                    if (File.Exists(p)) return p;
                throw new ReinstallException("Thiếu wimlib-imagex.exe (bộ công cụ xử lý file .wim cho Windows 7) — hãy cài lại ứng dụng.");
            }
        }
    }

    /// <summary>Thao tác file .wim / .esd: DISM trên Windows 8+, wimlib-imagex trên Windows 7.</summary>
    public static class Wim
    {
        /// <summary>Bung một bản trong file .wim/.esd ra thư mục.</summary>
        public static RunResult Apply(IRunner run, string image, int index, string dir) =>
            Platform.IsLegacy
                ? run.Run(Platform.Wimlib, $"apply \"{image}\" {index} \"{dir.TrimEnd('\\')}\"")
                : run.Run("dism.exe", $"/English /Apply-Image /ImageFile:\"{image}\" /Index:{index} /ApplyDir:\"{dir.TrimEnd('\\')}\"");

        /// <summary>Xuất một bản sang file .wim mới (nén LZX — trình khởi động đọc được; .esd dùng LZMS thì không).</summary>
        public static RunResult Export(IRunner run, string source, int index, string dest, bool bootable) =>
            Platform.IsLegacy
                ? run.Run(Platform.Wimlib, $"export \"{source}\" {index} \"{dest}\" --compress=LZX" + (bootable ? " --boot" : ""))
                : run.Run("dism.exe", $"/English /Export-Image /SourceImageFile:\"{source}\" /SourceIndex:{index} /DestinationImageFile:\"{dest}\"" +
                                      (bootable ? " /Bootable" : " /Compress:max"));

        /// <summary>wimlib: có đường dẫn này trong bản <paramref name="index"/> không (vd /Windows/System32/wpeinit.exe).</summary>
        public static bool Contains(IRunner run, string wim, int index, string path) =>
            run.Run(Platform.Wimlib, $"dir \"{wim}\" {index} --path={path}").Ok;

        /// <summary>wimlib: thêm (ghi đè) một file vào bản <paramref name="index"/>, tự tạo thư mục cha.</summary>
        public static RunResult AddFile(IRunner run, string wim, int index, string localFile, string wimPath) =>
            run.Run(Platform.Wimlib, $"update \"{wim}\" {index} \"--command=add '{localFile}' '{wimPath}'\"");
    }

    /// <summary>
    /// Đọc danh sách bản Windows từ phần XML của file WIM/ESD (không cần DISM — dùng cho Windows 7 và khi đọc thẳng trong ISO).
    /// Header WIM: rhXmlData ở byte 72 (7 byte kích thước + 1 byte cờ, 8 byte vị trí); XML luôn không nén, UTF-16.
    /// </summary>
    public static class WimXml
    {
        public static List<ImageEdition> Read(string file)
        {
            using var s = File.OpenRead(file);
            return Read(s);
        }

        public static List<ImageEdition> Read(Stream s)
        {
            var head = new byte[208];
            ReadExactly(s, head, 0, head.Length);
            if (Encoding.ASCII.GetString(head, 0, 5) != "MSWIM")
                throw new ReinstallException("Không phải file .wim / .esd hợp lệ.");
            long size = 0;
            for (var i = 6; i >= 0; i--) size = (size << 8) | head[72 + i];
            var offset = BitConverter.ToInt64(head, 80);
            if (size <= 0 || size > 64L * 1024 * 1024 || offset <= 0)
                throw new ReinstallException("File .wim / .esd không có phần mô tả (XML) hợp lệ.");
            var xml = new byte[size];
            s.Seek(offset, SeekOrigin.Begin);
            ReadExactly(s, xml, 0, xml.Length);
            return Parse(Encoding.Unicode.GetString(xml).TrimStart('\uFEFF'));
        }

        public static List<ImageEdition> Parse(string xml)
        {
            var doc = XDocument.Parse(xml);
            var list = new List<ImageEdition>();
            foreach (var img in doc.Root!.Elements("IMAGE"))
            {
                var e = new ImageEdition
                {
                    Index = int.TryParse((string?)img.Attribute("INDEX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ix) ? ix : 0,
                    Name = (string?)img.Element("NAME") ?? "",
                    Description = (string?)img.Element("DESCRIPTION") ?? "",
                    Size = long.TryParse((string?)img.Element("TOTALBYTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0,
                };
                var win = img.Element("WINDOWS");
                if (win != null)
                {
                    e.Architecture = Arch((string?)win.Element("ARCH"));
                    e.Edition = (string?)win.Element("EDITIONID") ?? "";
                    e.Language = (string?)win.Element("LANGUAGES")?.Element("DEFAULT")
                                 ?? (string?)win.Element("LANGUAGES")?.Element("LANGUAGE") ?? "";
                    var v = win.Element("VERSION");
                    if (v != null) e.Version = $"{(string?)v.Element("MAJOR")}.{(string?)v.Element("MINOR")}.{(string?)v.Element("BUILD")}";
                }
                if (e.Index > 0) list.Add(e);
            }
            return list.OrderBy(e => e.Index).ToList();
        }

        /// <summary>Mã ARCH trong XML → tên như dism hiển thị.</summary>
        internal static string Arch(string? code) => code switch
        {
            "0" => "x86",
            "5" => "ARM",
            "9" => "x64",
            "12" => "ARM64",
            null => "",
            _ => code,
        };

        private static void ReadExactly(Stream s, byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                var n = s.Read(buf, off, count);
                if (n <= 0) throw new ReinstallException("File .wim / .esd bị cắt cụt.");
                off += n;
                count -= n;
            }
        }
    }

    /// <summary>Đọc file .iso ngay trong code (UDF, dự phòng ISO9660) — Windows 7 không mở được ISO.</summary>
    public sealed class IsoReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly DiscFileSystem _fs;

        public IsoReader(string iso)
        {
            _stream = File.OpenRead(iso);
            try
            {
                _fs = UdfReader.Detect(_stream) ? new UdfReader(_stream) : new CDReader(_stream, true);
            }
            catch (Exception ex)
            {
                _stream.Dispose();
                throw new ReinstallException("Không đọc được file ISO: " + ex.Message);
            }
        }

        public bool Exists(string path) => _fs.FileExists(path);

        public Stream Open(string path) => _fs.OpenFile(path, FileMode.Open, FileAccess.Read);

        /// <summary>sources\install.wim | install.esd trong ISO.</summary>
        public string InstallImage =>
            new[] { @"sources\install.wim", @"sources\install.esd" }.FirstOrDefault(Exists)
            ?? throw new ReinstallException(Exists(@"sources\install.swm")
                ? "Bộ cài dùng install.swm (chia nhiều phần) — chưa hỗ trợ, hãy dùng ISO chính thức của Microsoft."
                : "Không thấy sources\\install.wim hoặc install.esd trong ISO.");

        /// <summary>Chép install.wim/esd, boot.wim, boot.sdi ra thư mục (giữ cấu trúc bộ cài) — trả về MediaFiles.</summary>
        public MediaFiles ExtractMedia(string dir, Action<string>? progress)
        {
            var files = new[] { InstallImage, @"sources\boot.wim", @"boot\boot.sdi" };
            foreach (var f in files)
            {
                if (!Exists(f)) throw new ReinstallException($"Không thấy {f} trong ISO.");
                var dst = Path.Combine(dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using var src = Open(f);
                if (File.Exists(dst) && new FileInfo(dst).Length == src.Length) { progress?.Invoke($"{f}: đã có sẵn, bỏ qua."); continue; }
                CopyStream(src, dst, $"Đang lấy {f} từ ISO", progress);
            }
            return MediaFiles.FromRoot(dir);
        }

        internal static void CopyStream(Stream src, string dst, string label, Action<string>? progress)
        {
            var tmp = dst + ".part";
            var buf = new byte[4 * 1024 * 1024];
            long done = 0, len = src.Length;
            var lastPct = -1;
            using (var o = File.Create(tmp))
            {
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    o.Write(buf, 0, n);
                    done += n;
                    var pct = len > 0 ? (int)(done * 100 / len) : 100;
                    if (pct / 5 != lastPct / 5) { lastPct = pct; progress?.Invoke($"{label}… {pct}%"); }
                }
            }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        public void Dispose()
        {
            (_fs as IDisposable)?.Dispose();
            _stream.Dispose();
        }
    }
}
