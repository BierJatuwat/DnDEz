using System.Security.Cryptography.X509Certificates;

namespace DnDEz.Api.Security;

/// <summary>
/// Sprint D Part 1 — เลือกว่า Data Protection key ring ต้องเข้ารหัสตัวเอง (ProtectKeysWithCertificate)
/// หรือไม่ ก่อนที่จะเก็บลง Redis (ดู CampaignApiKeyEncryption/Program.cs)
///
/// ⚠️ ทำไมต้องมีคลาสนี้เลย — ค่า default ของ ASP.NET Core Data Protection ถ้าไม่ตั้ง cert เอง:
/// Windows ใช้ DPAPI ป้องกันให้อัตโนมัติ (ปลอดภัย) แต่ **Linux (คือที่ container นี้รันจริงทุกที่ตาม
/// docker-compose) ไม่มีกลไกเทียบเท่า** — ASP.NET Core จะ log คำเตือนแล้วเก็บ key-encryption-key
/// "ไม่เข้ารหัส" ไว้เฉย ๆ ถ้าไม่ตั้ง cert เอง ซึ่งขัดกับจุดประสงค์ทั้งหมดของ Sprint D ข้อ 9 ("เข้ารหัส
/// เก็บ API key") เพราะถ้า key ring เองไม่ถูกป้องกัน คนที่เข้าถึง Redis ตรง ๆ ก็ถอดรหัส ApiKey.EncryptedKey
/// ได้อยู่ดี — เดินตามแพทเทิร์นเดียวกับ JwtSigningKeyResolver: fail-fast นอก Development ถ้าไม่ได้ตั้ง
/// cert แต่ยอมให้ Development รันแบบ unprotected ได้ (มี log คำเตือนจาก framework เองอยู่แล้ว)
/// </summary>
public static class DataProtectionCertificateResolver
{
    public const string PathEnvVarName = "DNDEZ_DATA_PROTECTION_CERT_PATH";
    public const string PasswordEnvVarName = "DNDEZ_DATA_PROTECTION_CERT_PASSWORD";

    /// <summary>
    /// คืน certificate ที่ต้องใช้ (ProtectKeysWithCertificate) หรือ null ถ้าไม่ต้องตั้ง (เฉพาะ
    /// Development เท่านั้นที่คืน null ได้ — นอกนั้น throw ถ้าไม่ได้ตั้ง env var ทั้งสองตัว)
    /// </summary>
    public static X509Certificate2? Resolve(IHostEnvironment environment)
    {
        var certPath = Environment.GetEnvironmentVariable(PathEnvVarName);
        var certPassword = Environment.GetEnvironmentVariable(PasswordEnvVarName);

        if (string.IsNullOrWhiteSpace(certPath))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{PathEnvVarName} ต้องถูกตั้งค่าเมื่อรันนอก Development — ไม่งั้น Data " +
                    "Protection key ring จะไม่ถูกเข้ารหัสด้วยอะไรเลยบน Linux (ผิดจุดประสงค์ของ " +
                    "การเข้ารหัส ApiKey.EncryptedKey ทั้งหมด)");
            }

            // Development เท่านั้น — ปล่อยให้ ASP.NET Core ใช้พฤติกรรม default (unprotected บน
            // Linux, DPAPI บน Windows) framework เองจะ log คำเตือนให้อยู่แล้วถ้า unprotected จริง
            return null;
        }

        if (string.IsNullOrWhiteSpace(certPassword))
        {
            throw new InvalidOperationException(
                $"{PathEnvVarName} ถูกตั้งแล้วแต่ {PasswordEnvVarName} ยังไม่ได้ตั้ง — ต้องมีทั้งคู่");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
    }
}
