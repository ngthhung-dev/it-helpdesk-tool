using System.IO;
using System.Text;
using System.Text.Json;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Disk;

public sealed record KeyProtector(string Id, string Type, string RecoveryPassword);

public sealed record BitLockerVolume(
    string MountPoint, string VolumeType, string Protection, string Status, double Percent, string Method, string Lock, bool AutoUnlock,
    List<KeyProtector> Protectors, long Capacity)
{
    public bool IsOs => VolumeType == "OperatingSystem";
    public bool Locked => Lock == "Locked";
    public bool On => Protection == "On";
    // VolumeStatus còn có EncryptionSuspended / DecryptionSuspended / FullyEncryptedWipe*: ổ vẫn đang (một phần) mã hoá.
    // ProtectionStatus = Off không có nghĩa là chưa mã hoá (tạm dừng bảo vệ, Device encryption chưa kích hoạt).
    public bool Encrypted => Locked || Protection == "On" || (Status.Length > 0 && Status != "FullyDecrypted");
    // Đã mã hoá nhưng chưa có khoá bảo vệ nào (Device encryption chờ kích hoạt): không “tiếp tục bảo vệ” được, phải thêm khoá trước.
    public bool NotActivated => Encrypted && Protection == "Off" && !Locked && Protectors.Count == 0 && !Status.StartsWith("Decryption");
    public bool Suspended => Encrypted && Protection == "Off" && !Locked && Protectors.Count > 0 && !Status.StartsWith("Decryption");
    // Enable-BitLocker ổ hệ điều hành (không -SkipHardwareTest): đã có khoá TPM / mật khẩu nhưng VolumeStatus vẫn FullyDecrypted
    // tới khi khởi động lại. Chỉ có mã khôi phục thì chưa tính (lần bật trước thất bại) — vẫn cho bấm “Bật BitLocker”.
    public bool Pending => Status == "FullyDecrypted" && !Locked && Protectors.Any(p => p.Type != "RecoveryPassword");
    public bool InProgress => Status is "EncryptionInProgress" or "DecryptionInProgress" or "FullyEncryptedWipeInProgress";
    public bool HasRecovery => Protectors.Any(p => p.Type == "RecoveryPassword");
    public bool NeedsRecovery => (Encrypted || Pending) && !HasRecovery && !Locked;

    public string StateText =>
        Locked ? "Đang khoá" :
        Pending ? (IsOs ? "Đã bật · chờ khởi động lại để mã hoá" : "Đã bật · chờ bắt đầu mã hoá") :
        Status switch
        {
            "FullyDecrypted" => "Chưa mã hoá",
            "EncryptionInProgress" => $"Đang mã hoá {Percent:0}%",
            "EncryptionSuspended" => $"Tạm dừng mã hoá ở {Percent:0}%",
            "DecryptionInProgress" => $"Đang giải mã ({Percent:0}% còn mã hoá)",
            "DecryptionSuspended" => $"Tạm dừng giải mã ({Percent:0}% còn mã hoá)",
            "FullyEncrypted" or "FullyEncryptedWipeInProgress" or "FullyEncryptedWipeSuspended" when Protection == "On" => "Đã mã hoá · đang bảo vệ",
            "FullyEncrypted" or "FullyEncryptedWipeInProgress" or "FullyEncryptedWipeSuspended" when Protectors.Count == 0 => "Đã mã hoá · chưa kích hoạt bảo vệ",
            "FullyEncrypted" or "FullyEncryptedWipeInProgress" or "FullyEncryptedWipeSuspended" => "Đã mã hoá · tạm dừng bảo vệ",
            _ => Status,
        };

    public string Tone => Locked ? "Warn" : On ? "Good" : Suspended || NotActivated || Pending ? "Warn" : "";

    public string Detail => string.Join(" · ", new[]
    {
        IsOs ? "Ổ hệ điều hành" : VolumeType == "Data" ? "Ổ dữ liệu" : VolumeType,
        Capacity > 0 ? Fmt.Size(Capacity) : "",
        Method is "" or "None" ? "" : Method,
        Protectors.Count == 0 ? "" : "khoá: " + string.Join(", ", Protectors.Select(p => ProtectorName(p.Type)).Distinct()),
        AutoUnlock ? "tự mở khoá" : "",
    }.Where(s => s.Length > 0));

    public static string ProtectorName(string t) => t switch
    {
        "Tpm" => "TPM", "TpmPin" => "TPM + PIN", "RecoveryPassword" => "mã khôi phục", "Password" => "mật khẩu",
        "ExternalKey" => "khoá USB", "TpmNetworkKey" => "TPM + mạng", _ => t,
    };
}

/// <summary>
/// BitLocker qua module BitLocker của Windows: xem trạng thái, sao lưu mã khôi phục, bật / tạm dừng / tắt mã hoá,
/// mở khoá ổ dữ liệu bằng mật khẩu hoặc mã khôi phục của chính chủ.
/// </summary>
public static class BitLockerService
{
    /// <summary>Máy có BitLocker không (Windows Home không có; Windows Server cần cài tính năng BitLocker).</summary>
    public static async Task<(bool Ok, string Message)> AvailableAsync()
    {
        var r = await ProcessRunner.PowerShellAsync("if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }");
        if (r.Output.Trim() == "yes") return (true, "");
        var ed = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "") as string ?? "";
        return (false, ed.Contains("Core", StringComparison.OrdinalIgnoreCase)
            ? "Windows Home không có BitLocker (chỉ có “Device encryption” trong Settings › Privacy & security nếu máy hỗ trợ)."
            : "Máy chưa có tính năng BitLocker (Windows Server: Install-WindowsFeature BitLocker, rồi khởi động lại).");
    }

    public static async Task<List<BitLockerVolume>> VolumesAsync()
    {
        // Lỗi ở một ổ (ổ RAW, đầu đọc thẻ, ổ ảo...) không được làm hỏng cả danh sách: bỏ qua ổ lỗi,
        // chỉ báo lỗi khi không đọc được ổ nào.
        const string script = """
            $vols = @(Get-BitLockerVolume -ErrorAction SilentlyContinue -ErrorVariable blErr)
            if ($vols.Count -eq 0 -and $blErr) { throw $blErr[0] }
            @($vols | ForEach-Object {
              [pscustomobject]@{ Mount=$_.MountPoint; Type="$($_.VolumeType)"; Prot="$($_.ProtectionStatus)"; Status="$($_.VolumeStatus)";
                Pct=$_.EncryptionPercentage; Method="$($_.EncryptionMethod)"; Lock="$($_.LockStatus)"; Auto=[bool]$_.AutoUnlockEnabled; Cap=[int64]($_.CapacityGB * 1GB);
                Keys=@($_.KeyProtector | ForEach-Object { [pscustomobject]@{ Id="$($_.KeyProtectorId)"; Type="$($_.KeyProtectorType)"; Rp="$($_.RecoveryPassword)" } }) }
            }) | ConvertTo-Json -Depth 4 -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var json = r.Output.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
        return items.Select(e =>
        {
            var keys = new List<KeyProtector>();
            if (e.TryGetProperty("Keys", out var k))
                foreach (var x in k.ValueKind == JsonValueKind.Array ? k.EnumerateArray().ToList() : [k])
                    keys.Add(new KeyProtector(S(x, "Id"), S(x, "Type"), S(x, "Rp")));
            return new BitLockerVolume(S(e, "Mount"), S(e, "Type"), S(e, "Prot"), S(e, "Status"),
                double.TryParse(S(e, "Pct"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
                S(e, "Method"), S(e, "Lock"), S(e, "Auto").Equals("True", StringComparison.OrdinalIgnoreCase), keys,
                long.TryParse(S(e, "Cap"), out var c) ? c : 0);
        }).OrderBy(v => !v.IsOs).ThenBy(v => v.MountPoint).ToList();
    }

    /// <summary>
    /// Trạng thái BitLocker từ WMI Win32_EncryptableVolume cho báo cáo. Phải xét cả ConversionStatus:
    /// ProtectionStatus = 0 khi tạm dừng bảo vệ hoặc Device encryption chưa kích hoạt dù ổ đã mã hoá toàn bộ —
    /// WMI không cho biết số khoá bảo vệ nên không tách được hai trường hợp này, ghi chung “chưa bật bảo vệ”.
    /// </summary>
    public static (string Text, Tone Tone) WmiState(WmiRow r)
    {
        var prot = r.Long("ProtectionStatus");
        var conv = r["ConversionStatus"] is null ? -1 : r.Long("ConversionStatus");
        if (prot == 2) return ("Đang khoá", Tone.Warn);
        if (prot == 1) return conv switch
        {
            2 => ("Đang bật · đang mã hoá", Tone.Good),
            3 => ("Đang giải mã", Tone.Warn),
            _ => ("Đang bật", Tone.Good),
        };
        return conv switch
        {
            0 => ("Tắt", Tone.Normal),
            1 => ("Đã mã hoá · chưa bật bảo vệ", Tone.Warn),
            2 => ("Đang mã hoá", Tone.Warn),
            3 => ("Đang giải mã", Tone.Warn),
            4 => ("Tạm dừng mã hoá", Tone.Warn),
            5 => ("Tạm dừng giải mã", Tone.Warn),
            _ => ("Tắt", Tone.Normal),
        };
    }

    private static async Task<OpResult> Ps(string script, string ok, string fail, IDictionary<string, string>? env = null)
    {
        var r = await ProcessRunner.PowerShellAsync(script, env: env);
        return r.Success ? OpResult.Success(ok) : OpResult.Fail($"{fail}: {OpResult.FirstLine(r.Error.Length > 0 ? r.Error : r.Text)}");
    }

    private static string Q(string s) => ProcessRunner.PsQuote(s);

    // ================= Mã khôi phục =================
    /// <summary>Nội dung file sao lưu mã khôi phục (giống file Windows tạo khi bật BitLocker).</summary>
    public static string RecoveryText(BitLockerVolume v)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mã khôi phục BitLocker / BitLocker recovery key");
        sb.AppendLine($"Máy: {Environment.MachineName} · Ổ: {v.MountPoint} · Lưu lúc: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine("Giữ file này ở nơi an toàn, KHÔNG lưu trên chính ổ đã mã hoá.");
        sb.AppendLine();
        foreach (var p in v.Protectors.Where(p => p.Type == "RecoveryPassword"))
        {
            sb.AppendLine($"Mã định danh (Key ID): {p.Id.Trim('{', '}')}");
            sb.AppendLine($"Mã khôi phục:          {p.RecoveryPassword}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Lưu mã khôi phục ra file .txt. Không cho lưu lên chính ổ đó.</summary>
    public static OpResult SaveRecovery(BitLockerVolume v, string path)
    {
        if (!v.HasRecovery) return OpResult.Fail("Ổ này chưa có mã khôi phục — bấm “Thêm mã khôi phục” trước.");
        var root = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\') ?? "";
        if (root.Equals(v.MountPoint.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("Không lưu mã khôi phục lên chính ổ đã mã hoá — chọn ổ khác hoặc USB.");
        File.WriteAllText(path, RecoveryText(v), Encoding.UTF8);
        return OpResult.Success($"Đã lưu mã khôi phục ổ {v.MountPoint} vào {path}.");
    }

    /// <summary>
    /// Sao lưu mã khôi phục của mọi ổ đã mã hoá vào một file (.txt, kèm .csv nếu muốn).
    /// Không cho lưu lên ổ đã mã hoá đang có trong danh sách (mất ổ đó là mất luôn mã).
    /// </summary>
    public static OpResult SaveAllRecovery(IReadOnlyList<BitLockerVolume> volumes, string path, bool csv = true)
    {
        var withKeys = volumes.Where(v => v.HasRecovery).ToList();
        if (withKeys.Count == 0)
            return OpResult.Fail(volumes.Any(v => v.Encrypted)
                ? "Các ổ đã mã hoá chưa có mã khôi phục — bấm “Thêm mã khôi phục” ở từng ổ trước."
                : "Chưa có ổ nào bật BitLocker nên chưa có mã khôi phục để sao lưu.");
        var root = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\') ?? "";
        if (volumes.Any(v => (v.Encrypted || v.Pending) && v.MountPoint.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase)))
            return OpResult.Fail($"Ổ {root} đang được mã hoá — lưu mã khôi phục ra USB / ổ khác / máy khác.");

        var sb = new StringBuilder();
        sb.AppendLine("SAO LƯU MÃ KHÔI PHỤC BITLOCKER / BITLOCKER RECOVERY KEYS");
        sb.AppendLine($"Máy: {Environment.MachineName} · Người lưu: {Environment.UserName} · Lúc: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine("Khi Windows hỏi mã khôi phục, đối chiếu “Mã định danh” hiện trên màn hình với mã bên dưới.");
        sb.AppendLine("Giữ file này ở nơi an toàn (USB cất riêng, in giấy, kho mật khẩu). Ai có mã này đều mở được ổ.");
        sb.AppendLine(new string('=', 78));
        foreach (var v in withKeys)
        {
            sb.AppendLine();
            sb.AppendLine($"Ổ {v.MountPoint} · {v.Detail} · {v.StateText}");
            foreach (var p in v.Protectors.Where(p => p.Type == "RecoveryPassword"))
            {
                sb.AppendLine($"  Mã định danh (Key ID): {p.Id.Trim('{', '}')}");
                sb.AppendLine($"  Mã khôi phục:          {p.RecoveryPassword}");
            }
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

        if (csv)
        {
            var c = new StringBuilder("Computer,Drive,VolumeType,KeyId,RecoveryPassword,SavedAt\r\n");
            foreach (var v in withKeys)
                foreach (var p in v.Protectors.Where(p => p.Type == "RecoveryPassword"))
                    c.Append($"{Environment.MachineName},{v.MountPoint},{v.VolumeType},{p.Id.Trim('{', '}')},{p.RecoveryPassword},{DateTime.Now:yyyy-MM-dd HH:mm}\r\n");
            File.WriteAllText(Path.ChangeExtension(path, ".csv"), c.ToString(), new UTF8Encoding(true));
        }
        var n = withKeys.Sum(v => v.Protectors.Count(p => p.Type == "RecoveryPassword"));
        return OpResult.Success($"Đã sao lưu {n} mã khôi phục của {withKeys.Count} ổ vào {path}" + (csv ? " (kèm file .csv)." : "."));
    }

    /// <summary>Sao lưu mọi mã khôi phục lên AD / Entra (máy đã tham gia domain / Entra ID).</summary>
    public static async Task<OpResult> BackupAllToDirectoryAsync(IReadOnlyList<BitLockerVolume> volumes, bool entra)
    {
        var list = volumes.Where(v => v.HasRecovery).ToList();
        if (list.Count == 0) return OpResult.Fail("Chưa có mã khôi phục nào để sao lưu.");
        var fails = new List<string>();
        foreach (var v in list)
        {
            var r = await BackupToDirectoryAsync(v, entra);
            if (!r.Ok) fails.Add(r.Message);
        }
        return fails.Count == 0
            ? OpResult.Success($"Đã sao lưu mã khôi phục của {list.Count} ổ lên {(entra ? "Microsoft Entra ID" : "Active Directory")}.")
            : OpResult.Fail(string.Join(" ", fails));
    }

    public static Task<OpResult> AddRecoveryPasswordAsync(BitLockerVolume v) =>
        Ps($"Add-BitLockerKeyProtector -MountPoint {Q(v.MountPoint)} -RecoveryPasswordProtector | Out-Null",
            $"Đã thêm mã khôi phục cho ổ {v.MountPoint} — hãy lưu lại ngay.", "Không thêm được mã khôi phục");

    /// <summary>Sao lưu mã khôi phục lên Active Directory / Microsoft Entra ID (máy đã tham gia domain / Entra).</summary>
    public static Task<OpResult> BackupToDirectoryAsync(BitLockerVolume v, bool entra)
    {
        var p = v.Protectors.FirstOrDefault(x => x.Type == "RecoveryPassword");
        if (p is null) return Task.FromResult(OpResult.Fail("Ổ này chưa có mã khôi phục."));
        var cmd = entra ? "BackupToAAD-BitLockerKeyProtector" : "Backup-BitLockerKeyProtector";
        return Ps($"{cmd} -MountPoint {Q(v.MountPoint)} -KeyProtectorId {Q(p.Id)} | Out-Null",
            $"Đã sao lưu mã khôi phục ổ {v.MountPoint} lên {(entra ? "Microsoft Entra ID" : "Active Directory")}.", "Không sao lưu được");
    }

    // ================= Bật / tắt / tạm dừng =================
    /// <summary>
    /// Bật BitLocker. Ổ hệ điều hành: TPM + mã khôi phục. Ổ dữ liệu: mật khẩu + mã khôi phục (+ tự mở khoá trên máy này).
    /// Chỉ mã hoá phần đã dùng (nhanh, an toàn với ổ mới).
    /// </summary>
    public static async Task<OpResult> EnableAsync(BitLockerVolume v, string? password, bool autoUnlock, bool skipHardwareTest)
    {
        // Luôn tạo mã khôi phục trước để không thể bị khoá ngoài
        if (!v.HasRecovery)
        {
            var a = await AddRecoveryPasswordAsync(v);
            if (!a.Ok) return a;
        }
        if (v.IsOs)
        {
            return await Ps($"Enable-BitLocker -MountPoint {Q(v.MountPoint)} -EncryptionMethod XtsAes128 -UsedSpaceOnly -TpmProtector {(skipHardwareTest ? "-SkipHardwareTest" : "")} -ErrorAction Stop | Out-Null",
                skipHardwareTest ? $"Đã bật BitLocker cho {v.MountPoint} — đang mã hoá nền." : $"Đã bật BitLocker cho {v.MountPoint}. Khởi động lại máy để Windows kiểm tra TPM rồi bắt đầu mã hoá.",
                "Không bật được BitLocker");
        }
        if (string.IsNullOrEmpty(password)) return OpResult.Fail("Ổ dữ liệu cần mật khẩu để mở khoá trên máy khác.");
        var r = await Ps($"$p = ConvertTo-SecureString $env:ITHD_PW -AsPlainText -Force; Enable-BitLocker -MountPoint {Q(v.MountPoint)} -EncryptionMethod XtsAes128 -UsedSpaceOnly -PasswordProtector -Password $p -ErrorAction Stop | Out-Null" +
                         (autoUnlock ? $"; Enable-BitLockerAutoUnlock -MountPoint {Q(v.MountPoint)} -ErrorAction SilentlyContinue | Out-Null" : ""),
            $"Đã bật BitLocker cho {v.MountPoint} — đang mã hoá nền, vẫn dùng ổ bình thường.", "Không bật được BitLocker",
            new Dictionary<string, string> { ["ITHD_PW"] = password });
        return r;
    }

    /// <summary>Tạm dừng bảo vệ (trước khi cập nhật BIOS / thay phần cứng) — tự bật lại sau số lần khởi động.</summary>
    public static Task<OpResult> SuspendAsync(BitLockerVolume v, int reboots) =>
        Ps($"Suspend-BitLocker -MountPoint {Q(v.MountPoint)} -RebootCount {reboots} | Out-Null",
            $"Đã tạm dừng BitLocker trên {v.MountPoint}" + (reboots > 0 ? $", tự bật lại sau {reboots} lần khởi động." : " cho tới khi bấm “Tiếp tục”."), "Không tạm dừng được");

    public static Task<OpResult> ResumeAsync(BitLockerVolume v) =>
        Ps($"Resume-BitLocker -MountPoint {Q(v.MountPoint)} | Out-Null", $"Đã bật lại bảo vệ BitLocker trên {v.MountPoint}.", "Không bật lại được");

    /// <summary>Tắt BitLocker: giải mã toàn bộ ổ (chạy nền, có thể mất nhiều giờ).</summary>
    public static Task<OpResult> DisableAsync(BitLockerVolume v) =>
        Ps($"Disable-BitLocker -MountPoint {Q(v.MountPoint)} | Out-Null", $"Đang giải mã {v.MountPoint} — chạy nền, vẫn dùng máy bình thường.", "Không tắt được BitLocker");

    // ================= Khoá / mở khoá ổ dữ liệu =================
    public static Task<OpResult> UnlockWithPasswordAsync(BitLockerVolume v, string password) =>
        Ps($"$p = ConvertTo-SecureString $env:ITHD_PW -AsPlainText -Force; Unlock-BitLocker -MountPoint {Q(v.MountPoint)} -Password $p | Out-Null",
            $"Đã mở khoá {v.MountPoint}.", "Không mở khoá được (sai mật khẩu?)", new Dictionary<string, string> { ["ITHD_PW"] = password });

    public static Task<OpResult> UnlockWithRecoveryAsync(BitLockerVolume v, string recovery)
    {
        var key = new string(recovery.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return Ps($"Unlock-BitLocker -MountPoint {Q(v.MountPoint)} -RecoveryPassword $env:ITHD_RK | Out-Null",
            $"Đã mở khoá {v.MountPoint} bằng mã khôi phục.", "Không mở khoá được (sai mã khôi phục?)", new Dictionary<string, string> { ["ITHD_RK"] = key });
    }

    public static Task<OpResult> LockAsync(BitLockerVolume v) =>
        Ps($"Lock-BitLocker -MountPoint {Q(v.MountPoint)} -ForceDismount | Out-Null", $"Đã khoá {v.MountPoint}.", "Không khoá được");

    public static async Task<OpResult> SetAutoUnlockAsync(BitLockerVolume v, bool on)
    {
        if (on && !(await VolumesAsync()).Any(x => x.IsOs && x.On))
            return OpResult.Fail("Tự mở khoá ổ dữ liệu chỉ dùng được khi ổ Windows đã bật BitLocker.");
        return await Ps($"{(on ? "Enable" : "Disable")}-BitLockerAutoUnlock -MountPoint {Q(v.MountPoint)} | Out-Null",
            on ? $"{v.MountPoint} sẽ tự mở khoá trên máy này." : $"Đã tắt tự mở khoá {v.MountPoint}.", "Không đổi được tự mở khoá");
    }

    /// <summary>Định dạng đúng của mã khôi phục: 8 nhóm 6 chữ số.</summary>
    public static bool IsRecoveryFormat(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^\d{6}(-\d{6}){7}$");
}
