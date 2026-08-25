using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace DnDEz.Api.Auth;

/// <summary>
/// ออก JWT bearer ให้ /auth/register และ /auth/login (Sprint A Part 2)
/// claim ที่ระบบพึ่งจริงมีตัวเดียว: user id — ใส่ทั้ง "sub" และ ClaimTypes.NameIdentifier
/// เพื่อไม่ต้องพึ่งพฤติกรรม inbound claim mapping ของ JwtBearer (อ่านกลับผ่าน
/// <see cref="ClaimsPrincipalExtensions.GetUserId"/> ซึ่งรองรับทั้งสองรูปแบบ)
///
/// ⚠️ โทเคนนี้ไม่มี revocation list — อายุสั้นคือกลไกจำกัดความเสียหายเดียวที่มีตอนนี้
/// </summary>
public sealed class JwtTokenIssuer(SymmetricSecurityKey signingKey)
{
    public const string Issuer = "dndez";
    public const string Audience = "dndez-api";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly SigningCredentials _credentials =
        new(signingKey, SecurityAlgorithms.HmacSha256);

    public string Issue(Guid userId)
    {
        var now = DateTime.UtcNow;
        var subject = userId.ToString();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now.AddMinutes(-1),
            expires: now.Add(Lifetime),
            signingCredentials: _credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// อ่าน user id จาก principal — รองรับทุกทางที่ claim อาจโผล่มา ("sub" ที่ถูก map เป็น
    /// NameIdentifier, NameIdentifier ที่เขียนตรง ๆ, หรือ "nameid" กรณีปิด inbound mapping)
    /// คืน null ถ้าไม่มีหรือ parse เป็น Guid ไม่ได้ — caller ต้องปฏิเสธ request ไม่ใช่เดาต่อ
    /// </summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? principal.FindFirstValue("nameid");

        return Guid.TryParse(raw, out var userId) ? userId : null;
    }
}
