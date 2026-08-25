using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DnDEz.Api.Auth;

/// <summary>
/// Sprint A Part 2 — หา key สำหรับ sign/validate JWT (HS256)
///
/// pattern เดียวกับ DNDEZ_APP_DB ใน Program.cs: อ่าน env var ก่อน แล้วค่อย fallback
/// แต่ fallback เป็น **dev-only เท่านั้น** — ถ้าไม่ใช่ Development แล้วยังไม่ตั้ง env var
/// ให้ล้มตั้งแต่ตอน startup (fail fast) ดีกว่าปล่อยให้ production sign ด้วย key ที่อยู่ใน git
/// ใครก็อ่านได้ = ปลอมโทเคนเป็นใครก็ได้
/// </summary>
public static class JwtSigningKeyResolver
{
    public const string EnvVarName = "DNDEZ_JWT_SIGNING_KEY";

    /// <summary>HS256 บังคับ key อย่างน้อย 256 bit ไม่งั้น validation จะ throw</summary>
    private const int MinimumKeyBytes = 32;

    /// <summary>
    /// dev-only — ค่านี้เป็นสาธารณะโดยเจตนา (อยู่ใน git) ห้ามใช้นอก Development เด็ดขาด
    /// ความยาว &gt;= 32 bytes ตามข้อบังคับของ HS256
    /// </summary>
    private const string DevOnlyFallbackKey =
        "dndez-dev-only-insecure-jwt-signing-key-do-not-use-outside-development";

    public static SymmetricSecurityKey Resolve(IHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable(EnvVarName);

        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{EnvVarName} ต้องถูกตั้งค่าเมื่อรันนอก Development " +
                    "(dev fallback key อยู่ใน source control จึงใช้จริงไม่ได้)");
            }

            configured = DevOnlyFallbackKey;
        }

        var keyBytes = Encoding.UTF8.GetBytes(configured);
        if (keyBytes.Length < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"{EnvVarName} สั้นเกินไป ({keyBytes.Length} bytes) — HS256 ต้องการอย่างน้อย {MinimumKeyBytes} bytes");
        }

        return new SymmetricSecurityKey(keyBytes);
    }
}
