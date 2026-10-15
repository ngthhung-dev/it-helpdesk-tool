using System.IO;
using System.Text;
using System.Text.Json;
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
    public bool Encrypted => Status is "FullyEncrypted" or "EncryptionInProgress" or "DecryptionInProgress" || Protection == "On" || Locked;
    public bool Suspended => Status == "FullyEncrypted" && Protection == "Off" && !Locked;
    public bool InProgress => Status is "EncryptionInProgress" or "DecryptionInProgress";
    public bool HasRecovery => Protectors.Any(p => p.Type == "RecoveryPassword");

    public string StateText =>
        Locked ? "Đang khoá" :
        Status switch
        {
            "FullyDecrypted" => "Chưa mã hoá",
            "EncryptionInProgress" => $"Đang mã hoá {Percent:0}%",
            "DecryptionInProgress" => $"Đang giải mã ({Percent:0}% còn mã hoá)",
            "FullyEncrypted" when Protection == "On" => "Đã mã hoá · đang bảo vệ",
            "FullyEncrypted" => "Đã mã hoá · tạm dừng bảo vệ",
            _ => Status,
        };

    public string Tone => Locked ? "Warn" : On ? "Good" : Suspended ? "Warn" : "";

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
        const string script = """
            @(Get-BitLockerVolume | ForEach-Object {
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
