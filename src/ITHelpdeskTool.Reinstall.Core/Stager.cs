using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Điều phối một lần cài lại:
    /// Prepare (chép file cài, tạo môi trường cài, sinh kịch bản — CHƯA đụng gì tới khởi động),
    /// Arm (đặt khởi động vào môi trường cài ĐÚNG MỘT LẦN), Cancel (gỡ mọi thứ, máy như cũ).
    /// </summary>
    public sealed class Stager
    {
        private readonly IRunner _run;
        private readonly Action<string> _progress;

        public Stager(IRunner run, Action<string>? progress = null)
        {
            _run = run;
            _progress = progress ?? (_ => { });
        }

        public sealed class Prepared
        {
            public string Id { get; set; } = "";
            public string StagingDir { get; set; } = "";
            public ImageEdition Edition { get; set; } = new ImageEdition();
            public List<string> StorageDrivers { get; set; } = new List<string>();
        }

        /// <summary>Chỉ dùng khi thử tự động: apply.cmd dừng an toàn trước bước format.</summary>
        public bool TestFailBeforeFormat { get; set; }

        public Prepared Prepare(ReinstallPlan p)
        {
            var staging = Path.GetFullPath(p.StagingDir).TrimEnd('\\');
            var (_, stagingRel) = BootEntry.Split(staging);
            Directory.CreateDirectory(staging);

            IsoMount? iso = null;
            try
            {
                // ---- 1. File cài ----
                MediaFiles media;
                var ext = Path.GetExtension(p.ImagePath).ToLowerInvariant();
                if (ext == ".iso")
                {
                    _progress("Đang mở file ISO…");
                    iso = IsoMount.Mount(_run, p.ImagePath);
                    media = MediaFiles.FromRoot(iso.Root);
                }
                else if (ext == ".wim" || ext == ".esd")
                {
                    // Bộ cài đã giải nén: ...\sources\install.wim cùng ...\sources\boot.wim, ...\boot\boot.sdi
                    var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(p.ImagePath))) ?? "";
                    media = MediaFiles.FromRoot(root);
                    media.InstallImage = Path.GetFullPath(p.ImagePath);
                }
                else throw new ReinstallException("Chọn file .iso, hoặc install.wim / install.esd trong bộ cài đã giải nén.");

                _progress("Đang đọc danh sách bản Windows…");
                var editions = ImageInfo.Read(_run, media.InstallImage);
                var ed = editions.FirstOrDefault(e => e.Index == p.ImageIndex)
                         ?? throw new ReinstallException($"File cài không có bản số {p.ImageIndex}. Có: {string.Join("; ", editions)}");
                var detail = ImageInfo.Read(_run, media.InstallImage, details: true).FirstOrDefault(e => e.Index == p.ImageIndex);
                if (detail != null) { ed.Architecture = detail.Architecture; ed.Version = detail.Version; }
                if (ed.Architecture.Length > 0) p.Architecture = ImageInfo.UnattendArch(ed.Architecture);

                // ---- 2. Dọn lần trước, đánh dấu thư mục đệm ----
                foreach (var old in Directory.GetFiles(staging, "ITH-STAGING-*.tag")) File.Delete(old);
                foreach (var f in new[] { "bootids.cmd", "apply.cmd", "unattend.xml", "boot.wim" }) Delete(Path.Combine(staging, f));
                var logs = Path.Combine(staging, "logs");
                if (Directory.Exists(logs)) Directory.Delete(logs, true);
                var id = Guid.NewGuid().ToString("N").Substring(0, 10);
                File.WriteAllText(Path.Combine(staging, $"ITH-STAGING-{id}.tag"), DateTime.Now.ToString("s"));

                // ---- 3. Chép file cài và boot.sdi ----
                var imageName = "install" + Path.GetExtension(media.InstallImage).ToLowerInvariant();
                foreach (var other in new[] { "install.wim", "install.esd" }.Where(n => n != imageName)) Delete(Path.Combine(staging, other));
                Copy(media.InstallImage, Path.Combine(staging, imageName), "Đang chép file cài Windows");
                Copy(media.BootSdi, Path.Combine(staging, "boot.sdi"), "Đang chép boot.sdi");

                // ---- 4. Driver ----
                var drivers = Path.Combine(staging, "drivers");
                if (Directory.Exists(drivers)) Directory.Delete(drivers, true);
                var storage = new List<string>();
                if (p.ExportDrivers && Drivers.Export(_run, drivers, _progress))
                    storage = Drivers.StorageDirs(drivers);

                // ---- 5. Môi trường cài ----
                var runCmd = Templates.Fill(Templates.Load("run.cmd"), new Dictionary<string, string>
                {
                    ["ID"] = id,
                    ["STAGING_REL"] = stagingRel,
                });
                BootWim.Build(_run, media.BootWim, Path.Combine(staging, "boot.wim"), Path.Combine(staging, "mount"), runCmd, storage, _progress);

                // ---- 6. Kịch bản ----
                _progress("Đang tạo kịch bản cài…");
                var apply = Templates.Fill(Templates.Load("apply.cmd"), new Dictionary<string, string>
                {
                    ["ID"] = id,
                    ["IMAGE"] = imageName,
                    ["INDEX"] = p.ImageIndex.ToString(),
                    ["FIRMWARE"] = p.Firmware == FirmwareMode.Uefi ? "UEFI" : "BIOS",
                    ["TEST_FAIL_BEFORE_FORMAT"] = TestFailBeforeFormat
                        ? "call :log \"TEST: forced stop before format\"\r\ngoto safe"
                        : "",
                });
                File.WriteAllText(Path.Combine(staging, "apply.cmd"), Templates.Crlf(apply), Encoding.ASCII);
                File.WriteAllText(Path.Combine(staging, "unattend.xml"), UnattendBuilder.Build(p), new UTF8Encoding(false));
                var post = Path.Combine(staging, "post");
                Directory.CreateDirectory(post);
                File.WriteAllText(Path.Combine(post, "FirstLogon.ps1"), Templates.Crlf(Templates.Load("FirstLogon.ps1")), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(post, "postinstall.json"), PostInstallBuilder.Json(p, id, stagingRel), new UTF8Encoding(false));

                // ---- 7. Đánh dấu ổ Windows sẽ bị xoá (môi trường cài tìm theo file này, không theo ký tự ổ) ----
                foreach (var old in Directory.GetFiles(p.TargetDrive.TrimEnd('\\') + "\\", "ITH-TARGET-*.tag")) File.Delete(old);
                File.WriteAllText(p.TargetDrive.TrimEnd('\\') + $"\\ITH-TARGET-{id}.tag", DateTime.Now.ToString("s"));

                _progress("Đã chuẩn bị xong.");
                return new Prepared { Id = id, StagingDir = staging, Edition = ed, StorageDrivers = storage };
            }
            finally
            {
                iso?.Dispose();
            }
        }

        /// <summary>Tạo mục khởi động tạm và đặt khởi động vào môi trường cài ở lần bật máy kế tiếp (một lần).</summary>
        public BootEntry Arm(ReinstallPlan p)
        {
            var staging = Path.GetFullPath(p.StagingDir).TrimEnd('\\');
            if (!File.Exists(Path.Combine(staging, "boot.wim")) || !File.Exists(Path.Combine(staging, "apply.cmd")))
                throw new ReinstallException("Chưa chuẩn bị xong (thiếu boot.wim / apply.cmd).");
            BootEntry.Load(staging)?.Delete(_run);
            _progress("Đang đặt khởi động vào môi trường cài (một lần)…");
            var e = BootEntry.Create(_run, Path.Combine(staging, "boot.wim"), Path.Combine(staging, "boot.sdi"), p.Firmware);
            e.Save(staging);
            return e;
        }

        /// <summary>Huỷ: xoá mục khởi động tạm và file đánh dấu. removeFiles = xoá luôn thư mục đệm.</summary>
        public void Cancel(string stagingDir, string targetDrive, bool removeFiles)
        {
            var staging = Path.GetFullPath(stagingDir).TrimEnd('\\');
            BootEntry.Load(staging)?.Delete(_run);
            Delete(Path.Combine(staging, "bootids.cmd"));
            foreach (var t in Directory.GetFiles(targetDrive.TrimEnd('\\') + "\\", "ITH-TARGET-*.tag")) Delete(t);
            if (removeFiles && Directory.Exists(staging)) Directory.Delete(staging, true);
        }

        private void Copy(string src, string dst, string label)
        {
            var len = new FileInfo(src).Length;
            if (File.Exists(dst) && new FileInfo(dst).Length == len && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(src))
            {
                _progress($"{label}: đã có sẵn, bỏ qua.");
                return;
            }
            var tmp = dst + ".part";
            var buf = new byte[4 * 1024 * 1024];
            long done = 0;
            var lastPct = -1;
            using (var i = File.OpenRead(src))
            using (var o = File.Create(tmp))
            {
                int n;
                while ((n = i.Read(buf, 0, buf.Length)) > 0)
                {
                    o.Write(buf, 0, n);
                    done += n;
                    var pct = len > 0 ? (int)(done * 100 / len) : 100;
                    if (pct / 5 != lastPct / 5) { lastPct = pct; _progress($"{label}… {pct}%"); }
                }
            }
            Delete(dst);
            File.Move(tmp, dst);
        }

        private static void Delete(string f)
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
