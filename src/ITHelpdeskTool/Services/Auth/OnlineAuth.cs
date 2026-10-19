using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Auth;

public sealed class OnlineAccount
{
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("iter")] public int Iter { get; set; } = 210_000;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Hết hạn sau ngày này (yyyy-MM-dd), để trống = không hết hạn.</summary>
    [JsonPropertyName("expires")] public string? Expires { get; set; }
    [JsonPropertyName("admin")] public bool Admin { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }

    [JsonIgnore] public DateTime? ExpiresDate => DateTime.TryParse(Expires, out var d) ? d.Date : null;
    [JsonIgnore] public bool Expired => ExpiresDate is { } d && DateTime.Today > d;
}

public sealed class OnlineAccountsFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>Thông báo hiện ở màn hình đăng nhập (tuỳ chọn).</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("accounts")] public List<OnlineAccount> Accounts { get; set; } = [];
}

/// <summary>
/// Đăng nhập kiểm tra qua mạng: danh sách tài khoản nằm ở file accounts.json trên nhánh "accounts" của repo GitHub.
/// File chỉ chứa mã băm PBKDF2-SHA256 có muối (không có mật khẩu). Sửa file (thêm / khoá / đặt lại mật khẩu)
/// có tác dụng ngay ở lần đăng nhập sau, không cần build lại app.
/// </summary>
public static class OnlineAuth
{
    public const string Owner = "ngthhung-dev";
    public const string Repo = "it-helpdesk-tool";
    public const string Branch = "accounts";
    public const string FileName = "accounts.json";
    private const string UserKey = @"HKEY_CURRENT_USER\Software\ngthhung\IT Helpdesk Tool";
    private const int Iterations = 210_000;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Tài khoản đã đăng nhập ở phiên này.</summary>
    public static OnlineAccount? Current { get; private set; }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ITHelpdeskTool/" + AppInfo.Version);
        return h;
    }

    public static string? LastUser
    {
        get { try { return Registry.GetValue(UserKey, "LastUser", null) as string; } catch { return null; } }
        set { try { Registry.SetValue(UserKey, "LastUser", value ?? ""); } catch { } }
    }

    // ================= Đọc danh sách =================
    /// <summary>Tải danh sách tài khoản (không cần đăng nhập GitHub). Thử API trước (luôn mới), lỗi thì đọc bản raw.</summary>
    public static async Task<OnlineAccountsFile> FetchAsync(CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in new[]
                 {
                     $"https://api.github.com/repos/{Owner}/{Repo}/contents/{FileName}?ref={Branch}",
                     $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{FileName}?t={DateTime.UtcNow.Ticks}",
                 })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/vnd.github.raw");
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var res = await Http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) { last = new HttpRequestException($"HTTP {(int)res.StatusCode}"); continue; }
                var text = await res.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<OnlineAccountsFile>(text) ?? throw new InvalidDataException("file rỗng");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { last = ex; }
        }
        throw new HttpRequestException("Không tải được danh sách tài khoản: " + last?.Message, last);
    }

    public static byte[] HashOf(string password, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, 32);

    public static void SetPassword(OnlineAccount a, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        a.Salt = Convert.ToBase64String(salt);
        a.Iter = Iterations;
        a.Hash = Convert.ToBase64String(HashOf(password, salt, Iterations));
    }

    private static bool Matches(OnlineAccount a, string password)
    {
        try { return CryptographicOperations.FixedTimeEquals(HashOf(password, Convert.FromBase64String(a.Salt), a.Iter), Convert.FromBase64String(a.Hash)); }
        catch { return false; }
    }

    /// <summary>Đăng nhập: trả null nếu đúng, ngược lại trả câu báo lỗi.</summary>
    public static async Task<string?> SignInAsync(string user, string password)
    {
        OnlineAccountsFile file;
        try { file = await FetchAsync(); }
        catch (Exception ex)
        {
            Log.Warn("Đăng nhập online: " + ex.Message);
            return "Không kết nối được máy chủ để kiểm tra tài khoản. Kiểm tra mạng (Internet) rồi thử lại.";
        }
        var a = file.Accounts.FirstOrDefault(x => x.User.Equals(user.Trim(), StringComparison.OrdinalIgnoreCase));
        var ok = a is not null && await Task.Run(() => Matches(a, password));
        if (!ok) return "Sai tên đăng nhập hoặc mật khẩu.";
        if (!a!.Enabled) return "Tài khoản này đã bị khoá. Liên hệ quản trị viên.";
        if (a.Expired) return $"Tài khoản đã hết hạn từ {a.ExpiresDate:dd/MM/yyyy}. Liên hệ quản trị viên để gia hạn.";
        Current = a;
        LastUser = a.User;
        return null;
    }

    public static async Task<string?> LoginMessageAsync()
    {
        try { return (await FetchAsync()).Message; } catch { return null; }
    }

    // ================= Quản trị: ghi file qua GitHub API =================
    private static string TokenFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ngthhung", "IT Helpdesk Tool", "gh.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ITHelpdeskTool.gh.v1");

    public static string? Token
    {
        get
        {
            try { return File.Exists(TokenFile) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), Entropy, DataProtectionScope.CurrentUser)) : null; }
            catch { return null; }
        }
        set
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) { if (File.Exists(TokenFile)) File.Delete(TokenFile); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
                File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Log.Warn("Lưu token: " + ex.Message); }
        }
    }

    private static HttpRequestMessage Api(HttpMethod m, string token, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(m, $"https://api.github.com/repos/{Owner}/{Repo}/contents/{FileName}" + (m == HttpMethod.Get ? $"?ref={Branch}" : ""));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Content = body;
        return req;
    }

    /// <summary>Đọc danh sách kèm sha (để ghi đè đúng phiên bản).</summary>
    public static async Task<(OnlineAccountsFile File, string Sha)> ReadForEditAsync(string token)
    {
        using var res = await Http.SendAsync(Api(HttpMethod.Get, token));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException(ApiError(res, text));
        using var doc = JsonDocument.Parse(text);
        var content = Encoding.UTF8.GetString(Convert.FromBase64String(doc.RootElement.GetProperty("content").GetString()!.Replace("\n", "")));
        return (JsonSerializer.Deserialize<OnlineAccountsFile>(content) ?? new(), doc.RootElement.GetProperty("sha").GetString()!);
    }

    public static async Task SaveAsync(string token, OnlineAccountsFile file, string sha, string message)
    {
        var json = JsonSerializer.Serialize(file, Json) + "\n";
        var body = JsonSerializer.Serialize(new
        {
            message,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
            sha,
            branch = Branch,
        });
        using var res = await Http.SendAsync(Api(HttpMethod.Put, token, new StringContent(body, Encoding.UTF8, "application/json")));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException(ApiError(res, text));
    }

    private static string ApiError(HttpResponseMessage res, string body) => (int)res.StatusCode switch
    {
        401 => "Token GitHub không hợp lệ hoặc đã hết hạn.",
        403 or 404 => "Token không có quyền ghi vào repo (cần quyền Contents: Read and write cho repo " + Repo + ").",
        409 => "Danh sách vừa được sửa ở nơi khác — bấm Tải lại rồi làm lại.",
        _ => $"GitHub báo lỗi {(int)res.StatusCode}: {body[..Math.Min(200, body.Length)]}",
    };
}
