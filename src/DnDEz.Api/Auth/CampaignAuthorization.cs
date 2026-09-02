using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Auth;

/// <summary>
/// Sprint F งานที่ 3 — รวมเช็คสิทธิ์ที่เคยเขียนซ้ำ inline อยู่ 5 ที่ให้เหลือที่เดียว
/// (refactor ล้วน ๆ — พฤติกรรม HTTP ที่มองเห็นจากภายนอกต้องไม่เปลี่ยนแม้แต่นิดเดียว)
///
/// เดิมกระจายอยู่ที่:
///  • <c>AutonomySettingEndpoints</c> — PUT autonomy-settings
///  • <c>CampaignMembershipEndpoints</c> — PUT join-code
///  • <c>ConsultEndpoints</c> — POST /consult และ GET proposals (สองที่ในไฟล์เดียว)
///  • <c>PlayerActionEndpoints</c> — POST actions (เช็คเจ้าของ "ตัวละคร" ไม่ใช่เจ้าของแคมเปญ)
///
/// ⚠️ **ลำดับ 404-ก่อน-403 คือ contract ไม่ใช่รายละเอียดการเขียน** — แคมเปญ/ตัวละครที่ผู้เรียก
/// มองไม่เห็นต้องตอบ 404 เหมือน "ไม่มีอยู่จริง" ห้ามตอบ 403 เพราะ 403 = ยืนยันว่าของชิ้นนี้มีอยู่
/// (เปิดช่อง enumeration) · ที่ทำได้แบบนี้เพราะ RLS policy `campaign_member_read` กรอง `campaigns`
/// ให้เหลือเฉพาะแคมเปญที่ผู้เรียกเป็นสมาชิกอยู่แล้ว → query ด้านล่างคืน null เองโดยไม่ต้องเช็คซ้ำ
/// 403 จึงเหลือความหมายเดียว: "เห็นได้ (เป็นสมาชิก) แต่ไม่ใช่ DM/ไม่ใช่เจ้าของตัวละคร"
///
/// ⚠️⚠️ **ห้ามเปลี่ยนนิยาม "DM" ตรงนี้เป็นการ query `campaign_members.role` เด็ดขาด** —
/// ระบบนี้มีนิยาม "DM" อยู่ **สองชั้นที่อิสระต่อกัน** และตอนนี้บังเอิญตรงกัน:
///   • ชั้น C# (ไฟล์นี้): <c>campaigns.owner_user_id == caller</c>
///   • ชั้น DB (<c>app_is_dm()</c> ที่ RLS policy ใช้): <c>campaign_members.role = 'Dm'</c>
/// ที่ตรงกันได้เพราะ invariant ว่า <c>app_create_campaign()</c> insert เจ้าของเป็นสมาชิก role `'Dm'`
/// เสมอ (ดู doc comment ข้อ 4 ของ migration `20260830122509_AddAutonomySettingDmOnlyRls`)
/// การย้ายชั้น C# ไปใช้นิยามของชั้น DB (หรือกลับกัน) จะทำให้ defense-in-depth สองชั้นยุบเหลือชั้นเดียว
/// เงียบ ๆ — ถ้าชั้นไหนพลาด อีกชั้นจะพลาดตามทันทีเพราะใช้แหล่งความจริงเดียวกันแล้ว
///
/// รูปแบบ return เป็น tuple <c>(entity, failure)</c> แทนการโยน exception — endpoint เรียกแล้ว
/// `if (failure is not null) return failure;` ได้ตรง ๆ และยังได้ entity ที่โหลดมาแล้วไปใช้ต่อ
/// โดยไม่ต้อง query ซ้ำ (สาม call site ใช้ entity ต่อจริง: join-code แก้ `JoinCode`,
/// /consult ส่ง campaign เข้า `OrchestratorTurnRunner`, actions ใช้ `character.Id`)
/// </summary>
public static class CampaignAuthorization
{
    /// <summary>
    /// บังคับว่าผู้เรียกต้องเป็น DM (เจ้าของ) ของแคมเปญนี้ — คืน campaign ที่โหลดแล้ว (tracked)
    /// ให้ใช้ต่อได้เลยถ้าผ่าน
    ///
    /// ลำดับเป๊ะ ๆ ตามของเดิมทั้ง 4 ที่:
    ///   1. โหลด campaign (RLS กรองแคมเปญที่ไม่ได้เป็นสมาชิกทิ้งไปแล้ว) → null = 404
    ///   2. ไม่ใช่เจ้าของ → 403
    /// </summary>
    public static async Task<(Campaign? Campaign, IResult? Failure)> RequireCampaignDmAsync(
        DnDEzDbContext db, Guid campaignId, Guid callerUserId)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
        if (campaign is null)
        {
            return (null, Results.NotFound());
        }

        if (campaign.OwnerUserId != callerUserId)
        {
            return (null, Results.Forbid());
        }

        return (campaign, null);
    }

    /// <summary>
    /// บังคับว่าผู้เรียกต้องเป็นเจ้าของตัวละครตัวนี้ และตัวละครต้องอยู่ในแคมเปญที่ระบุใน route
    /// (กัน IDOR: ส่ง characterId ของโต๊ะอื่นเข้ามาแล้วเล่นแทนตัวละครนั้น)
    ///
    /// เงื่อนไข <c>CampaignId == campaignId</c> อยู่ใน query เดียวกับการโหลดโดยตั้งใจ — ตัวละคร
    /// ที่มีอยู่จริงแต่คนละแคมเปญต้องได้ 404 เหมือนไม่มีอยู่จริง ไม่ใช่ 403 (ลำดับเดียวกับ
    /// <see cref="RequireCampaignDmAsync"/>)
    ///
    /// หมายเหตุ: ตัวละคร quick-play ที่ <c>OwnerUserId</c> เป็น null จะไม่มีวันผ่านเช็คนี้
    /// (null != caller เสมอ) — ตรงกับของเดิมที่สงวน "เล่นแทนตัวละครไร้เจ้าของ" ไว้ให้ DM override
    /// ซึ่งยังไม่ได้ทำ
    /// </summary>
    public static async Task<(Character? Character, IResult? Failure)> RequireCharacterOwnerAsync(
        DnDEzDbContext db, Guid characterId, Guid campaignId, Guid callerUserId)
    {
        var character = await db.Characters
            .SingleOrDefaultAsync(c => c.Id == characterId && c.CampaignId == campaignId);
        if (character is null)
        {
            return (null, Results.NotFound());
        }

        if (character.OwnerUserId != callerUserId)
        {
            return (null, Results.Forbid());
        }

        return (character, null);
    }
}
