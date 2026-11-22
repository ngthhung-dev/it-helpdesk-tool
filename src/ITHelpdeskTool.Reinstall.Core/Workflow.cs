using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Tuỳ chọn cho một lần chạy kiểm tra / chuẩn bị.</summary>
    public sealed class WorkflowOptions
    {
        /// <summary>Các mục sao lưu đã chọn (BackupScanner.Scan).</summary>
        public List<BackupItem> Backup { get; set; } = new List<BackupItem>();
        /// <summary>Khoá các mục người dùng đã xác nhận (<see cref="ConfirmKeys"/>).</summary>
        public ICollection<string> Confirmed { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tài khoản đang đăng nhập (xét tài khoản Microsoft); null = tài khoản chạy chương trình.</summary>
        public string? UserName { get; set; }
        public bool? CpuSupportsWin11 { get; set; }
        public bool IgnoreBackupErrors { get; set; }
        /// <summary>Không tự chép lại dữ liệu đã sao lưu vào Windows mới.</summary>
        public bool NoRestore { get; set; }
        public bool TestFailBeforeFormat { get; set; }
        /// <summary>Chuẩn bị xong thì đặt khởi động một lần vào môi trường cài.</summary>
        public bool Arm { get; set; }
    }

    /// <summary>Lý do dừng (mã thoát của ithreinstall prepare).</summary>
    public enum WorkflowStop
    {
        None = 0,
        Blocked = 3,
        BackupFailed = 4,
        NoRecoveryKey = 5,
        NeedKeyConfirm = 6,
        StagingEncrypted = 7,
    }

    public sealed class WorkflowResult
    {
        public WorkflowStop Stop { get; set; }
        public string Message { get; set; } = "";
        public MachineFacts? Facts { get; set; }
        public ImageEdition? Edition { get; set; }
        public List<Check> Checks { get; } = new List<Check>();
        /// <summary>Khoá khôi phục BitLocker của các ổ dữ liệu (đã lưu vào <see cref="KeyFile"/>).</summary>
        public List<RecoveryKey> Keys { get; } = new List<RecoveryKey>();
        public string KeyFile { get; set; } = "";
        public BackupResult? Backup { get; set; }
        public Stager.Prepared? Prepared { get; set; }
        public BootEntry? Entry { get; set; }
        public bool Ok => Stop == WorkflowStop.None;
        public bool Blocked => Checks.Any(c => c.Level == CheckLevel.Block);
    }

    /// <summary>
    /// Luồng cài lại dùng chung cho app, ithreinstall và bản Windows 7:
    /// kiểm tra → (máy một ổ) tạo chỗ đệm → khoá BitLocker ổ dữ liệu → sao lưu → chuẩn bị → đặt khởi động.
    /// Lỗi ở bước nào thì dừng ở đó, máy chưa bị thay đổi gì ngoài các bước đã xong (đều huỷ được bằng Stager.Cancel).
    /// </summary>
    public sealed class Workflow
    {
        private readonly IRunner _run;
        private readonly Action<string> _log;

        public Workflow(IRunner run, Action<string>? log = null)
        {
            _run = run;
            _log = log ?? (_ => { });
        }

        /// <summary>Gọi khi đã biết thư mục đệm thật (vd sau khi thu nhỏ C:) — để chuyển log về đó.</summary>
        public Action<string>? StagingReady { get; set; }

        public static long ImageBytes(string image) => File.Exists(image) ? new FileInfo(image).Length : 0;

        public static string Size(long b) => b >= 1073741824 ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:0} MB";

        /// <summary>Đọc các bản Windows trong ISO / WIM / ESD (mở ISO nếu cần).</summary>
        public static List<ImageEdition> ReadEditions(IRunner run, string image)
        {
            IsoMount? iso = null;
            try
            {
                if (image.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && Platform.IsLegacy)
                {
                    // Windows 7: đọc thẳng trong ISO, không cần mở ổ ảo
                    using var reader = new IsoReader(image);
                    using var s = reader.Open(reader.InstallImage);
                    return WimXml.Read(s).Where(e => !MediaBuilder.IsSetupImage(e)).ToList();
                }
                if (image.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    iso = IsoMount.Mount(run, image);
                    image = MediaFiles.FromRoot(iso.Root).InstallImage;
                }
                // File .esd của Media Creation Tool có thêm ảnh thư mục bộ cài / WinPE / trình cài đặt — không phải bản để cài
                return ImageInfo.Read(run, image, details: true).Where(e => !MediaBuilder.IsSetupImage(e)).ToList();
            }
            finally { iso?.Dispose(); }
        }

        public static ImageEdition ReadEdition(IRunner run, string image, int index)
        {
            var all = ReadEditions(run, image);
            return all.FirstOrDefault(e => e.Index == index)
                   ?? throw new ReinstallException($"File cài không có bản số {index}. Có: {string.Join("; ", all)}");
        }

        /// <summary>Kiểm tra máy + tương thích với bản Windows đã chọn. Không thay đổi gì trên máy.</summary>
        public WorkflowResult Inspect(ReinstallPlan p, WorkflowOptions o)
        {
            var r = new WorkflowResult();
            var facts = MachineFacts.Read(_run, o.UserName);
            var ed = ReadEdition(_run, p.ImagePath, p.ImageIndex);
            r.Facts = facts;
            r.Edition = ed;
            _log($"Bản sẽ cài: {ed} [{ed.Edition}]");
            _log($"Máy: {facts.ProductName} ({facts.EditionId}) · {facts.CpuName} · RAM {facts.RamBytes / 1073741824.0:0.#} GB · {facts.Firmware} · TPM {facts.Tpm}");
            _log($"Tài khoản: {(facts.Account.Source.Length > 0 ? facts.Account.Source : "?")}" +
                 (facts.Account.ManagedByOrganization ? " · quản lý bởi " + facts.Account.OrganizationText : "") +
                 " · BitLocker: " + (facts.BitLocker.Count > 0 ? string.Join(", ", facts.BitLocker.Values) : "không"));
            r.Checks.AddRange(Preflight.Run(p, facts, ImageBytes(p.ImagePath) + o.Backup.Sum(i => i.Bytes), o.CpuSupportsWin11, o.Confirmed));
            long targetBytes = 0;
            try { targetBytes = new DriveInfo(p.TargetDrive).TotalSize; } catch { }
            r.Checks.AddRange(Compatibility.Evaluate(facts, ed, targetBytes, o.CpuSupportsWin11));

            // Máy chỉ có ổ C:: xem trước có thu nhỏ được không (chưa làm gì)
            if (p.AutoStaging && StagingPartition.FindExisting() == null)
            {
                var needed = StagingPartition.NeededBytes(ImageBytes(p.ImagePath), o.Backup.Sum(i => i.Bytes));
                var info = StagingPartition.Probe(_run, p.TargetDrive);
                var shrink = StagingPartition.Check(info, needed);
                if (shrink.Any(c => c.Level == CheckLevel.Block))
                {
                    if (info.Free >= needed + (2L << 30))
                        r.Checks.Add(new Check(CheckLevel.Info, $"Không thu nhỏ được {p.TargetDrive} — dùng thư mục đệm ngay trên ổ ({p.TargetDrive}\\ITH)",
                            $"Không format {p.TargetDrive}: môi trường cài xoá mọi thứ trên ổ trừ thư mục này rồi cài Windows mới."));
                    else
                        r.Checks.Add(new Check(CheckLevel.Block, $"{p.TargetDrive} không đủ chỗ trống cho thư mục đệm",
                            $"Cần ~{Size(needed)}, còn {Size(info.Free)}. Bớt mục sao lưu hoặc dọn ổ."));
                }
                else r.Checks.AddRange(shrink);
            }
            foreach (var c in r.Checks) _log(c.ToString());
            if (r.Blocked) { r.Stop = WorkflowStop.Blocked; r.Message = "Còn mục chặn — xử lý xong rồi kiểm tra lại."; }
            return r;
        }

        /// <summary>Chạy toàn bộ (và đặt khởi động nếu <see cref="WorkflowOptions.Arm"/>). Lỗi bất thường ném <see cref="ReinstallException"/>.</summary>
        public WorkflowResult Prepare(ReinstallPlan p, WorkflowOptions o)
        {
            var existing = p.AutoStaging ? StagingPartition.FindExisting() : null;
            if (existing != null) p.StagingDir = existing + "\\ITH";        // chạy lại: dùng phân vùng đệm đã tạo
            if (!(p.AutoStaging && existing == null))
            {
                Directory.CreateDirectory(p.StagingDir);
                StagingReady?.Invoke(p.StagingDir);
            }
            _log($"=== Chuẩn bị cài lại Windows: {p.ImagePath} bản {p.ImageIndex} → {p.TargetDrive} (đệm {(p.AutoStaging && existing == null ? "tự tạo bằng cách thu nhỏ " + p.TargetDrive : p.StagingDir)}, {p.Firmware})");

            // 1. Kiểm tra máy + tương thích
            var r = Inspect(p, o);
            if (!r.Ok) { _log("Dừng: còn mục chặn (Block) ở trên."); return r; }
            var facts = r.Facts!;

            // 1a. Máy chỉ có ổ C:: thu nhỏ C: tạo phân vùng đệm, không được thì đệm ngay trên C:
            if (p.AutoStaging && existing == null)
            {
                var needed = StagingPartition.NeededBytes(ImageBytes(p.ImagePath), o.Backup.Sum(i => i.Bytes));
                var info = StagingPartition.Probe(_run, p.TargetDrive);
                if (!StagingPartition.Check(info, needed).Any(c => c.Level == CheckLevel.Block))
                {
                    _log($"Đang thu nhỏ {p.TargetDrive} {needed / 1073741824} GB để tạo phân vùng đệm…");
                    var drive = StagingPartition.Create(_run, info, needed);
                    p.StagingDir = drive + "\\ITH";
                    _log($"Đã tạo phân vùng đệm {drive} ({StagingPartition.Label}).");
                    // Windows 11 24H2 tự mã hoá ổ mới khi có mã hoá thiết bị → tắt ngay (ổ còn trống nên giải mã gần như tức thì)
                    var enc = Encryption.Status(_run, drive);
                    if (enc != null && enc.Encrypted)
                    {
                        _log($"Windows vừa tự mã hoá {drive} — đang tắt…");
                        Encryption.TurnOff(_run, drive);
                    }
                }
                else
                {
                    p.StagingDir = p.TargetDrive.TrimEnd('\\') + "\\ITH";
                    _log($"Không thu nhỏ được {p.TargetDrive} — dùng thư mục đệm ngay trên ổ Windows: {p.StagingDir} (không format, xoá mọi thứ trừ thư mục này).");
                }
                Directory.CreateDirectory(p.StagingDir);
                StagingReady?.Invoke(p.StagingDir);
            }

            // 1b. Ổ dữ liệu mã hoá BitLocker: lưu khoá khôi phục, bắt xác nhận đã chép trước khi đi tiếp
            var encrypted = Preflight.EncryptedDataDrives(p, facts);
            if (encrypted.Count > 0)
            {
                foreach (var v in encrypted)
                {
                    var k = Encryption.RecoveryKeys(_run, v.Drive);
                    if (k.Count == 0)
                    {
                        r.Stop = WorkflowStop.NoRecoveryKey;
                        r.Message = $"Ổ {v.Drive} mã hoá nhưng không có khoá khôi phục dạng 48 số — Windows mới sẽ không mở được ổ này. " +
                                    $"Thêm khoá: manage-bde -protectors -add {v.Drive} -RecoveryPassword rồi chạy lại.";
                        _log("Dừng: " + r.Message);
                        return r;
                    }
                    r.Keys.AddRange(k);
                }
                var keyDir = Path.Combine(p.StagingDir, "backup", "bitlocker");
                Directory.CreateDirectory(keyDir);
                r.KeyFile = Path.Combine(keyDir, "khoa-khoi-phuc-bitlocker.txt");
                File.WriteAllText(r.KeyFile, Encryption.KeysText(r.Keys), new UTF8Encoding(true));
                _log($"Đã lưu khoá khôi phục BitLocker của {string.Join(", ", encrypted.Select(v => v.Drive))} vào {r.KeyFile}");
                if (!o.Confirmed.Contains(ConfirmKeys.BitLockerKeys))
                {
                    r.Stop = WorkflowStop.NeedKeyConfirm;
                    r.Message = "Chép các khoá khôi phục BitLocker ra giấy / nơi khác ngoài máy rồi xác nhận để chạy tiếp.";
                    _log("Dừng: " + r.Message);
                    return r;
                }
            }

            // 2. Sao lưu — chưa xong thì không đi tiếp
            if (o.Backup.Count == 0) _log("Không sao lưu.");
            else
            {
                foreach (var b in o.Backup) _log($"Sao lưu: {b.Title} {(b.Bytes > 0 ? Size(b.Bytes) : "")} {(b.Source.Length > 0 ? "← " + b.Source : "")}");
                // Thư mục đệm ngay trên ổ Windows được giữ lại khi xoá ổ → cho sao lưu vào đó
                var sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(p.StagingDir))?.TrimEnd('\\'), p.TargetDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                var res = BackupRunner.Run(_run, o.Backup, Path.Combine(p.StagingDir, "backup"), p.TargetDrive, _log, sameDrive ? p.StagingDir : null);
                foreach (var e in res.Errors) _log("LỖI sao lưu: " + e);
                _log($"Sao lưu xong {res.Done.Count}/{o.Backup.Count} mục vào {res.Dir}");
                r.Backup = res;
                if (!res.Ok && !o.IgnoreBackupErrors)
                {
                    r.Stop = WorkflowStop.BackupFailed;
                    r.Message = "Sao lưu có lỗi: " + string.Join("; ", res.Errors.Take(3)) + (res.Errors.Count > 3 ? "…" : "") +
                                ". Sửa lỗi rồi chạy lại, hoặc chọn bỏ qua nếu chấp nhận thiếu các file này.";
                    _log("Dừng: sao lưu có lỗi.");
                    return r;
                }
                if (!o.NoRestore) p.Restore = res.Done;
            }

            // 3. Chuẩn bị
            var stager = new Stager(_run, _log) { TestFailBeforeFormat = o.TestFailBeforeFormat };
            r.Prepared = stager.Prepare(p);
            _log($"Đã chuẩn bị: {r.Prepared.Edition} · mã {r.Prepared.Id} · driver ổ cứng: {r.Prepared.StorageDrivers.Count} · tự chép lại: {p.Restore.Count} mục");
            if (!o.Arm) return r;

            // 4. Đặt khởi động một lần — trước đó kiểm tra lại ổ đệm: Windows 11 24H2 có thể tự mã hoá ổ mới,
            //    trình khởi động không đọc được ổ mã hoá → máy sẽ dừng ở màn hình lỗi khởi động
            var stagingDrive = Path.GetPathRoot(Path.GetFullPath(p.StagingDir))!.TrimEnd('\\');
            var st = Encryption.Status(_run, stagingDrive);
            if (st != null && st.Encrypted)
            {
                r.Stop = WorkflowStop.StagingEncrypted;
                r.Message = $"Ổ đệm {stagingDrive} đang mã hoá BitLocker ({st}) — chưa đặt khởi động. Tắt mã hoá ổ này rồi bấm lại.";
                _log("Dừng: " + r.Message);
                return r;
            }
            r.Entry = stager.Arm(p);
            _log($"Đã đặt khởi động một lần vào môi trường cài ({r.Entry.EntryId}). Huỷ: ithreinstall cancel --staging {p.StagingDir}");
            return r;
        }
    }
}
