using System.Security.Cryptography;
using System.Text;

namespace ITHelpdeskTool.Services.Auth;

/// <summary>
/// Băm mật khẩu PBKDF2-SHA256 dùng chung cho app chính (.NET 10) và bản nhẹ Windows 7 (.NET Framework 4.8 — không có các hàm tĩnh mới).
/// Hai bản cho cùng kết quả nên tài khoản tạo ở bản nào cũng đăng nhập được ở bản kia.
/// </summary>
internal static class AuthCrypto
{
    public static byte[] Pbkdf2(string password, byte[] salt, int iter)
    {
#if NETFRAMEWORK
        using var k = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256);
        return k.GetBytes(32);
#else
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, 32);
#endif
    }

    public static byte[] RandomBytes(int count)
    {
#if NETFRAMEWORK
        var b = new byte[count];
        using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
        return b;
#else
        return RandomNumberGenerator.GetBytes(count);
#endif
    }

    /// <summary>So sánh thời gian cố định (không lộ vị trí byte khác nhau).</summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b)
    {
#if NETFRAMEWORK
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
#else
        return CryptographicOperations.FixedTimeEquals(a, b);
#endif
    }
}
