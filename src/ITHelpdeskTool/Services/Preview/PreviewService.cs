using System.IO;
using System.Text;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Preview;

public sealed record PreviewSupport(string Ext, string Label, string Handler, string Suggest)
{
    public bool Ok => Handler.Length > 0;
}

public sealed record FileReport(
    string Path, long Size, DateTime Created, DateTime Modified, string RealType, bool ExtMismatch, string Sha256,
    string Signature, bool? Signed, string ZoneText, bool FromInternet, string HostUrl, bool IsImage, string? Text)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Xem trước file trong Explorer (preview pane, ảnh thu nhỏ, file tải từ Internet) và kiểm tra nhanh một file.</summary>
public static class PreviewService
{
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string PolExp = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string PreviewShellEx = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    // ================= Thiết lập Explorer =================
    public static bool PreviewHandlersOn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Adv); return k?.GetValue("ShowPreviewHandlers") is not int v || v != 0; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Adv, true); k.SetValue("ShowPreviewHandlers", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    public static bool ThumbnailsOn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Adv); return k?.GetValue("IconsOnly") is not int v || v == 0; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Adv, true); k.SetValue("IconsOnly", value ? 0 : 1, RegistryValueKind.DWord); }
    }

    public static bool NetworkThumbnailsOn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(PolExp); return k?.GetValue("DisableThumbnailsOnNetworkFolders") is not int v || v == 0; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(PolExp, true);
            if (value) k.DeleteValue("DisableThumbnailsOnNetworkFolders", false);
            else k.SetValue("DisableThumbnailsOnNetworkFolders", 1, RegistryValueKind.DWord);
        }
    }

    // ================= Định dạng xem trước được =================
    private static readonly (string Ext, string Label, string Suggest)[] Common =
    [
        (".pdf", "PDF", "Đặt Microsoft Edge / Adobe Reader làm trình xem PDF, hoặc cài PowerToys"),
        (".docx", "Word", "Cài Microsoft Office"),
        (".xlsx", "Excel", "Cài Microsoft Office"),
        (".pptx", "PowerPoint", "Cài Microsoft Office"),
        (".msg", "Thư Outlook (.msg)", "Cài Outlook"),
        (".eml", "Email (.eml)", "Có sẵn trong Windows"),
        (".txt", "Văn bản", "Có sẵn trong Windows"),
        (".jpg", "Ảnh JPG", "Có sẵn trong Windows"),
        (".png", "Ảnh PNG", "Có sẵn trong Windows"),
        (".heic", "Ảnh iPhone (HEIC)", "Cài HEIF Image Extensions từ Microsoft Store"),
        (".mp4", "Video MP4", "Có sẵn trong Windows"),
        (".svg", "SVG", "Cài PowerToys"),
        (".md", "Markdown", "Cài PowerToys"),
        (".json", "JSON / mã nguồn", "Cài PowerToys"),
    ];

    public static List<PreviewSupport> Supports() => Common.Select(c => new PreviewSupport(c.Ext, c.Label, HandlerFor(c.Ext), c.Suggest)).ToList();

    /// <summary>Tên preview handler đang đăng ký cho đuôi file (tìm theo đuôi, ProgID, SystemFileAssociations).</summary>
    public static string HandlerFor(string ext)
    {
        string? Clsid(string key)
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"{key}\ShellEx\{PreviewShellEx}");
            return k?.GetValue("") as string;
        }
        var clsid = Clsid(ext);
        if (clsid is null)
        {
            using var e = Registry.ClassesRoot.OpenSubKey(ext);
            if (e?.GetValue("") is string progId && progId.Length > 0) clsid = Clsid(progId);
        }
        clsid ??= Clsid($@"SystemFileAssociations\{ext}");
        if (clsid is null)
        {
            using var e = Registry.ClassesRoot.OpenSubKey(ext);
            if (e?.GetValue("PerceivedType") is string pt) clsid = Clsid($@"SystemFileAssociations\{pt}");
        }
        if (clsid is null) return "";
        using var names = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers");
        if (names?.GetValue(clsid) is string n && n.Length > 0) return n;
        using var c = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
        return c?.GetValue("") as string ?? clsid;
    }

    // ================= File tải từ Internet =================
    /// <summary>Gỡ đánh dấu “tải từ Internet” (Mark of the Web) cho mọi file trong thư mục, để Explorer xem trước được.</summary>
    public static async Task<OpResult> UnblockFolderAsync(string dir)
    {
        var r = await ProcessRunner.PowerShellAsync(
            $"$n = 0; Get-ChildItem -LiteralPath {ProcessRunner.PsQuote(dir)} -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {{ if (Get-Item -LiteralPath $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue) {{ Unblock-File -LiteralPath $_.FullName; $n++ }} }}; $n");
        return r.Success ? OpResult.Success($"Đã bỏ chặn {r.Output.Trim()} file trong {dir}.") : OpResult.Fail(OpResult.FirstLine(r.Text));
    }

    public static OpResult Unblock(string file)
    {
        try { File.Delete(file + ":Zone.Identifier"); return OpResult.Success($"Đã bỏ chặn {Path.GetFileName(file)}."); }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    // ================= Kiểm tra file =================
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp"];
    private static readonly string[] TextExt = [".txt", ".log", ".csv", ".json", ".xml", ".ini", ".cfg", ".conf", ".md", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".html", ".htm", ".css", ".reg", ".yml", ".yaml", ".cs", ".py", ".sql"];

    /// <summary>Loại file thật đọc từ vài byte đầu (không tin vào đuôi file).</summary>
    public static (string Type, string[] Exts) Sniff(byte[] h)
    {
        bool S(params int[] b) => h.Length >= b.Length && b.Select((x, i) => h[i] == x).All(x => x);
        if (S(0x4D, 0x5A)) return ("Chương trình Windows (EXE/DLL)", [".exe", ".dll", ".sys", ".scr", ".com", ".cpl", ".ocx", ".msi"]);
        if (S(0x25, 0x50, 0x44, 0x46)) return ("PDF", [".pdf"]);
        if (S(0x50, 0x4B, 0x03, 0x04)) return ("Nén ZIP / Office (docx, xlsx, pptx)", [".zip", ".docx", ".xlsx", ".pptx", ".jar", ".apk", ".odt", ".ods", ".epub", ".vsix", ".nupkg", ".xps"]);
        if (S(0x52, 0x61, 0x72, 0x21)) return ("Nén RAR", [".rar"]);
        if (S(0x37, 0x7A, 0xBC, 0xAF)) return ("Nén 7z", [".7z"]);
        if (S(0x89, 0x50, 0x4E, 0x47)) return ("Ảnh PNG", [".png"]);
        if (S(0xFF, 0xD8, 0xFF)) return ("Ảnh JPEG", [".jpg", ".jpeg", ".jfif"]);
        if (S(0x47, 0x49, 0x46, 0x38)) return ("Ảnh GIF", [".gif"]);
        if (S(0x42, 0x4D)) return ("Ảnh BMP", [".bmp"]);
        if (S(0xD0, 0xCF, 0x11, 0xE0)) return ("Office cũ / MSI / Outlook (.doc .xls .msg .msi)", [".doc", ".xls", ".ppt", ".msg", ".msi", ".pub", ".vsd"]);
        if (h.Length > 11 && h[4] == 0x66 && h[5] == 0x74 && h[6] == 0x79 && h[7] == 0x70) return ("Video / ảnh MP4 · MOV · HEIC", [".mp4", ".m4a", ".mov", ".heic", ".heif", ".m4v", ".3gp"]);
        if (S(0x4C, 0x00, 0x00, 0x00)) return ("Lối tắt Windows (.lnk)", [".lnk"]);
        if (S(0x7B, 0x5C, 0x72, 0x74, 0x66)) return ("RTF", [".rtf", ".doc"]);
        if (h.Length > 0 && h.Take(Math.Min(h.Length, 512)).All(b => b is 9 or 10 or 13 || b >= 32 && b != 127 || b >= 0x80)) return ("Văn bản", []);
        return ("Không rõ", []);
    }

    public static async Task<FileReport> InspectAsync(string path, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        var head = new byte[512];
        int n;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = await fs.ReadAsync(head, ct);
        Array.Resize(ref head, n);
        var (type, exts) = Sniff(head);
        var ext = fi.Extension.ToLowerInvariant();
        var mismatch = exts.Length > 0 && !exts.Contains(ext);
        // Tên kiểu “hoadon.pdf.exe”
        var doubleExt = System.Text.RegularExpressions.Regex.IsMatch(fi.Name, @"\.(pdf|docx?|xlsx?|jpg|png|txt|zip)\.(exe|scr|com|bat|cmd|js|vbs|ps1|lnk)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        string sha;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
            sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));

        string sig = "";
        bool? signed = null;
        if (type.StartsWith("Chương trình") || ext is ".msi" or ".ps1" or ".cab" or ".sys")
        {
            var r = await ProcessRunner.PowerShellAsync($"$s = Get-AuthenticodeSignature -LiteralPath {ProcessRunner.PsQuote(path)}; \"$($s.Status)|$($s.SignerCertificate.Subject)\"", ct: ct);
            var parts = r.Output.Trim().Split('|', 2);
            signed = parts[0] == "Valid";
            var cn = System.Text.RegularExpressions.Regex.Match(parts.ElementAtOrDefault(1) ?? "", @"CN=""?([^,""]+)").Groups[1].Value;
            sig = parts[0] switch
            {
                "Valid" => $"Chữ ký số hợp lệ: {cn}",
                "NotSigned" => "Không có chữ ký số",
                "HashMismatch" => "Chữ ký số KHÔNG khớp — file đã bị sửa sau khi ký",
                "NotTrusted" => $"Chữ ký số không được tin cậy: {cn}",
                _ => parts[0],
            };
        }

        // Zone.Identifier: nơi tải về
        string zone = "", host = "";
        var fromInternet = false;
        try
        {
            var z = File.ReadAllText(path + ":Zone.Identifier");
            var id = System.Text.RegularExpressions.Regex.Match(z, @"ZoneId=(\d)").Groups[1].Value;
            host = System.Text.RegularExpressions.Regex.Match(z, @"HostUrl=(.+)").Groups[1].Value.Trim();
            var refer = System.Text.RegularExpressions.Regex.Match(z, @"ReferrerUrl=(.+)").Groups[1].Value.Trim();
            fromInternet = id is "3" or "4";
            zone = id switch { "3" => "Tải từ Internet", "4" => "Từ trang web bị hạn chế", "2" => "Từ mạng tin cậy", "1" => "Từ mạng nội bộ", _ => $"Zone {id}" }
                   + (refer.Length > 0 ? $" · trang: {refer}" : "");
        }
        catch { zone = "Không có đánh dấu nguồn tải (file tạo trên máy / chép từ USB)"; }

        string? text = null;
        var isImage = ImageExt.Contains(ext) && type.StartsWith("Ảnh");
        if ((TextExt.Contains(ext) || type == "Văn bản") && fi.Length < 5 * 1024 * 1024)
        {
            var lines = new List<string>();
            using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? l;
            while (lines.Count < 300 && (l = await sr.ReadLineAsync(ct)) is not null) lines.Add(l.Length > 400 ? l[..400] + "…" : l);
            text = string.Join(Environment.NewLine, lines);
        }

        return new FileReport(path, fi.Length, fi.CreationTime, fi.LastWriteTime,
            type + (doubleExt ? " · TÊN FILE GIẢ ĐUÔI (vd .pdf.exe) — rất có thể là mã độc" : ""),
            mismatch || doubleExt, sha, sig, signed, zone, fromInternet, host, isImage, text);
    }
}
