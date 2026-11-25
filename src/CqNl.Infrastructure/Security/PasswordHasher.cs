using System.Security.Cryptography;

namespace CqNl.Infrastructure.Security;

/// <summary>PBKDF2-HMAC-SHA256, salt 16 byte, 210.000 vòng. Định dạng: pbkdf2-sha256$iter$saltB64$hashB64. Không lưu mật khẩu rõ.</summary>
public static class PasswordHasher
{
    public const int Iterations = 210_000;
    public const int MinLength = 6;   // PDF-4 §3

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out var it)) return false;
        var salt = Convert.FromBase64String(p[2]); var expect = Convert.FromBase64String(p[3]);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, expect.Length);
        return CryptographicOperations.FixedTimeEquals(h, expect);
    }

    /// <summary>Kiểm tra mật khẩu mới theo PDF-4: ≥ 6 ký tự, khác mật khẩu mặc định/tạm.</summary>
    public static string? ValidateNew(string newPassword, string confirm, string? previousTemporaryHash)
    {
        if (newPassword != confirm) return "Hai lần nhập mật khẩu không khớp";
        if (newPassword.Length < MinLength) return $"Mật khẩu tối thiểu {MinLength} ký tự";
        if (previousTemporaryHash is not null && Verify(newPassword, previousTemporaryHash)) return "Mật khẩu mới phải khác mật khẩu mặc định";
        return null;
    }
}
