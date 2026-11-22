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
            var (stagingDrive, stagingRel) = BootEntry.Split(staging);
            // Thư mục đệm nằm ngay trên ổ Windows (máy một ổ, không thu nhỏ được C:): không format, xoá mọi thứ trừ thư mục đệm
            var sameDrive = stagingDrive.Equals(p.TargetDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
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
                if (detail != null) { ed.Architecture = detail.Architecture; ed.Version = detail.Version; ed.Edition = detail.Edition; ed.Language = detail.Language; }
                if (p.UiLanguage.Length == 0) p.UiLanguage = ed.Language;
                if (ed.Architecture.Length > 0) p.Architecture = ImageInfo.UnattendArch(ed.Architecture);

                // ---- 2. Dọn lần trước, đánh dấu thư mục đệm ----
                foreach (var old in Directory.GetFiles(staging, "ITH-STAGING-*.tag")) File.Delete(old);
                foreach (var f in new[] { "bootids.cmd", "apply.cmd", "unattend.xml", "boot.wim" }) Delete(Path.Combine(staging, f));
                // Chỉ xoá dấu kết quả của lần trước (prepare.log đang mở để ghi — không xoá cả thư mục logs)
                var logs = Path.Combine(staging, "logs");
                foreach (var f in new[] { "winpe.log", "winpe.done", "winpe.safe", "winpe.failed" }) Delete(Path.Combine(logs, f));
                if (Directory.Exists(Path.Combine(logs, "post"))) Directory.Delete(Path.Combine(logs, "post"), true);
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
                // Phân vùng khởi động đang dùng (ghi vào log để đối chiếu; môi trường cài gắn đúng phân vùng EFI này bằng mountvol /s)
                var sys = SystemPartition.Probe(_run, p.TargetDrive);
                _progress("Phân vùng khởi động: " + sys + (sys.OnOtherDisk ? " — nằm trên ổ cứng khác với Windows, giữ nguyên như hiện tại" : ""));
                _progress("Đang tạo kịch bản cài…");
                var apply = Templates.Fill(Templates.Load("apply.cmd"), new Dictionary<string, string>
                {
                    ["ID"] = id,
                    ["IMAGE"] = imageName,
                    ["INDEX"] = p.ImageIndex.ToString(),
                    ["FIRMWARE"] = p.Firmware == FirmwareMode.Uefi ? "UEFI" : "BIOS",
                    ["STAGING_CHECK"] = sameDrive ? ApplySameDriveCheck : ApplyOtherDriveCheck,
                    ["WIPE"] = sameDrive ? ApplyPurge : ApplyFormat,
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

        // ---- Đoạn apply.cmd theo chế độ ổ đệm ----
        internal const string ApplyOtherDriveCheck =
            "if /i \"%TGT%\"==\"%STG:~0,2%\" (\r\n  call :log \"ERROR: staging is on the target drive\"\r\n  goto safe\r\n)";

        internal const string ApplySameDriveCheck =
            "if /i not \"%TGT%\"==\"%STG:~0,2%\" (\r\n  call :log \"ERROR: staging expected on the target drive\"\r\n  goto safe\r\n)";

        internal const string ApplyFormat =
            "rem ---- 3. Format o Windows (chi phan vung nay) ----\r\n" +
            "call :log \"Formatting %TGT% ...\"\r\n" +
            "> \"%TEMP%\\ith-fmt.txt\" echo select volume %TGT:~0,1%\r\n" +
            ">>\"%TEMP%\\ith-fmt.txt\" echo format fs=ntfs quick label=Windows override\r\n" +
            "diskpart /s \"%TEMP%\\ith-fmt.txt\" >>\"%LOG%\" 2>&1\r\n" +
            "if errorlevel 1 (\r\n  call :log \"ERROR: format failed\"\r\n  goto safe_after_format_attempt\r\n)";

        /// <summary>
        /// Không format: gỡ mọi junction / symlink trên ổ (CHỈ gỡ đường dẫn — không đi theo sang ổ khác, vd thư mục đã “Chuyển thư mục” sang D:),
        /// rồi robocopy /MIR từ thư mục rỗng để xoá mọi thứ trừ thư mục đệm.
        /// </summary>
        internal const string ApplyPurge =
            "rem ---- 3. Xoa moi thu tren o Windows tru thu muc dem (khong format) ----\r\n" +
            "call :log \"Clearing %TGT% except %STG% ...\"\r\n" +
            "for /f \"delims=\" %%j in ('dir /s /b /al \"%TGT%\\\" 2^>nul') do (\r\n" +
            "  if exist \"%%j\\\" (rmdir \"%%j\" 2>nul) else (del /f /a \"%%j\" 2>nul)\r\n" +
            ")\r\n" +
            "mkdir \"%TEMP%\\ith-empty\" 2>nul\r\n" +
            "robocopy \"%TEMP%\\ith-empty\" %TGT%\\ /MIR /B /XJ /R:0 /W:0 /NFL /NDL /NP /XD \"%STG%\" \"%TGT%\\System Volume Information\" >>\"%LOG%\" 2>&1\r\n" +
            "if errorlevel 8 (\r\n  call :log \"ERROR: clearing failed\"\r\n  goto hard\r\n)\r\n" +
            "if exist \"%TGT%\\Windows\\System32\" (\r\n  call :log \"ERROR: old Windows still present after clearing\"\r\n  goto hard\r\n)";

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
            FlushToDisk(staging, p.TargetDrive);
            return e;
        }

        /// <summary>
        /// Ép ghi hẳn xuống đĩa: kho cấu hình khởi động (BCD là một nhánh registry, Windows ghi trễ) và bộ đệm ghi của ổ đệm / ổ Windows.
        /// Nếu lần khởi động lại không tắt sạch (treo / màn hình xanh lúc tắt máy) thì mục khởi động vừa tạo và file đánh dấu vẫn còn
        /// — trước đây mất hết, máy lại vào Windows cũ (thấy trên máy ảo: bcdedit sau khi quay về không còn mục ITH, log cuối bị cắt).
        /// </summary>
        internal static string FlushScript(string stagingDrive, string targetDrive)
        {
            var letters = string.Join(",", new[] { stagingDrive, targetDrive }
                .Select(d => d.TrimEnd('\\', ':')).Where(d => d.Length == 1).Distinct(StringComparer.OrdinalIgnoreCase).Select(d => "'" + d + "'"));
            return "$ErrorActionPreference='SilentlyContinue';" +
                   "$k=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('BCD00000000'); if($k){ $k.Flush(); 'BCD flushed' };" +
                   $"foreach($l in @({letters})){{ Write-VolumeCache -DriveLetter $l; 'Volume ' + $l + ': flushed' }}";
        }

        private void FlushToDisk(string staging, string targetDrive)
        {
            _progress("Đang ghi hẳn cấu hình khởi động xuống đĩa…");
            var stgRoot = Path.GetPathRoot(staging) ?? "";
            _run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + FlushScript(stgRoot, targetDrive) + "\"");
        }

        /// <summary>Huỷ: xoá mục khởi động tạm và file đánh dấu. removeFiles = xoá luôn thư mục đệm.</summary>
        public void Cancel(string stagingDir, string targetDrive, bool removeFiles)
        {
            var staging = Path.GetFullPath(stagingDir).TrimEnd('\\');
            BootEntry.Load(staging)?.Delete(_run);
            Delete(Path.Combine(staging, "bootids.cmd"));
            foreach (var t in Directory.GetFiles(targetDrive.TrimEnd('\\') + "\\", "ITH-TARGET-*.tag")) Delete(t);
            if (!removeFiles) return;
            var root = Path.GetPathRoot(staging) ?? "";
            if (StagingPartition.IsOurs(root))
            {
                // Phân vùng đệm do app tạo (thu nhỏ C:) → xoá và trả dung lượng lại cho ổ Windows
                _progress($"Đang xoá phân vùng đệm {root.TrimEnd('\\')} và nới {targetDrive} lại…");
                StagingPartition.Remove(_run, root, targetDrive);
            }
            else if (Directory.Exists(staging)) Directory.Delete(staging, true);
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
