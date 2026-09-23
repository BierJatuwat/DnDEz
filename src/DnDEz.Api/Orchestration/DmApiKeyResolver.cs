using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Orchestration;

/// <summary>
/// ดึงคีย์ BYOK ของ DM ออกมาใช้งาน (2026-09-22 — แยกออกมาจาก <c>ConsultEndpoints</c> ตอน Part C
/// เพราะมีจุดเรียกครบ 3 ที่แล้ว: `/consult`, resolve-proposal, และ `/advisor` ตัวใหม่ — เป็นการรวม
/// ของซ้ำจริง ไม่ใช่ abstraction ดักหน้า)
///
/// ⚠️ **ผู้เรียกต้องยืนยันก่อนแล้วว่า caller คือ DM เจ้าของแคมเปญ** — RLS ของ `api_keys`
/// (`own_rows_only`) กรองด้วย `app.user_id` ของผู้เรียกเท่านั้น ถ้าเรียกด้วย user ที่ไม่ใช่เจ้าของคีย์
/// query จะว่างเปล่าเสมอ (ไม่ใช่ error) — ดู doc comment ของ <c>ConsultEndpoints</c> สำหรับเหตุผลเต็ม
/// ว่าทำไมทุก endpoint ที่ใช้คีย์นี้ถึงต้องเป็น DM-only
/// </summary>
internal static class DmApiKeyResolver
{
    private const string ProtectorPurpose = "DnDEz.ApiKeys.v1"; // ต้องตรงกับ ApiKeyEndpoints เป๊ะ
    private const string AnthropicProvider = "anthropic";

    /// <summary>คืน raw key ที่ถอดรหัสแล้ว — null = ผู้ใช้คนนี้ยังไม่ได้ตั้งคีย์ (ผู้เรียกแปลงเป็น
    /// 400 `dm_api_key_not_set` เอง ไม่โยน exception เพราะไม่ใช่ความผิดพลาดของระบบ)</summary>
    internal static async Task<string?> ResolveAsync(
        DnDEzDbContext db, IDataProtectionProvider dataProtectionProvider, Guid dmUserId)
    {
        ApiKey? apiKey = await db.ApiKeys.SingleOrDefaultAsync(
            k => k.UserId == dmUserId && k.Provider == AnthropicProvider && k.Status == ApiKeyStatus.Active);
        if (apiKey is null)
        {
            return null;
        }

        var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        return protector.Unprotect(apiKey.EncryptedKey);
    }
}
