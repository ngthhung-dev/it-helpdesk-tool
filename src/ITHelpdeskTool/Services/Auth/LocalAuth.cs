using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ITHelpdeskTool.Services.Auth;

/// <summary>
/// Tài khoản mở app lưu trên máy (đặt ở lần mở đầu tiên). Chỉ lưu mã băm PBKDF2-SHA256 có muối, không lưu mật khẩu;
/// file được mã hoá thêm bằng DPAPI (gắn với máy) để không sửa tay được.
/// </summary>
public static class LocalAuth
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ngthhung", "IT Helpdesk Tool");
    public static string FilePath => Path.Combine(Folder, "auth.dat");

    private const int Iterations = 210_000;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ITHelpdeskTool.auth.v1");

    private sealed record Store(string User, string Salt, string Hash, int Iter);

    public static bool HasAccount => Read() is not null;
    public static string? UserName => Read()?.User;

    private static Store? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<Store>(raw);
        }
        catch (Exception ex) { Log.Warn($"Đọc tài khoản app lỗi: {ex.Message}"); return null; }
    }

    private static void Write(Store s)
    {
        Directory.CreateDirectory(Folder);
        var raw = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), Entropy, DataProtectionScope.LocalMachine);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, raw);
        File.Move(tmp, FilePath, true);
    }

    private static byte[] Hash(string password, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, 32);

    public static string? ValidateNew(string user, string pw, string pw2)
    {
        if (user.Trim().Length < 3) return "Tên đăng nhập tối thiểu 3 ký tự.";
        if (pw.Length < 6) return "Mật khẩu tối thiểu 6 ký tự.";
        if (pw != pw2) return "Hai lần nhập mật khẩu không khớp.";
        return null;
    }

    public static void Create(string user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        Write(new Store(user.Trim(), Convert.ToBase64String(salt), Convert.ToBase64String(Hash(password, salt, Iterations)), Iterations));
        Log.Info($"Đã tạo tài khoản mở app: {user.Trim()}");
    }

    public static bool Verify(string user, string password)
    {
        var s = Read();
        if (s is null) return false;
        var h = Hash(password, Convert.FromBase64String(s.Salt), s.Iter);
        var userOk = string.Equals(s.User, user.Trim(), StringComparison.OrdinalIgnoreCase);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(s.Hash)) && userOk;
    }

    public static string? Change(string oldPw, string newUser, string newPw, string newPw2)
    {
        var s = Read();
        if (s is null) return "Chưa có tài khoản.";
        if (!Verify(s.User, oldPw)) return "Mật khẩu hiện tại không đúng.";
        if (ValidateNew(newUser, newPw, newPw2) is { } err) return err;
        Create(newUser, newPw);
        return null;
    }
}
