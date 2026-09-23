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
///  • <c>ProposalEndpoints</c> — resolve-proposal และ GET proposals (สองที่ในไฟล์เดียว)
///  • <c>PlayerActionEndpoints</c> — POST actions (เช็คเจ้าของ "ตัวละคร" ไม่ใช่เจ้าของแคมเปญ)
///  • <c>AssistantEndpoints</c> — ผู้ช่วย DM ช่องเดียว
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
/// actions ส่ง campaign เข้า `OrchestratorTurnRunner` และใช้ `character.Id`)
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

    /// <summary>
    /// Sprint F งานที่ 3.1 — บังคับว่าผู้เรียกมีสิทธิ์ "เล่นแทน" ตัวละครตัวนี้ในการกระทำ combat
    /// (checks/attacks) ได้จริง ตัวละครต้องอยู่ในแคมเปญที่ระบุใน route เหมือน
    /// <see cref="RequireCharacterOwnerAsync"/> (กัน IDOR แบบเดียวกัน — 404 ก่อน 403 เสมอ)
    ///
    /// ต่างจาก <see cref="RequireCharacterOwnerAsync"/> ตรงที่เมธอดนี้มี "DM override": อนุญาตได้
    /// สองทาง —
    ///   • <c>character.OwnerUserId == callerUserId</c> (เจ้าของตัวละครเล่นเอง) หรือ
    ///   • <c>campaign.OwnerUserId == callerUserId AND character.ControlledByDm</c> (DM คุมตัวละคร
    ///     ที่ตั้งค่าให้ DM คุมได้ เช่น monster/NPC) — เช็ค <c>ControlledByDm</c> ตรง ๆ ไม่ใช่อนุมานจาก
    ///     <c>OwnerUserId == null</c> เพราะสองอย่างนี้เป็นคนละแกนกัน (ตัวละคร quick-play ที่ยังไม่มี
    ///     เจ้าของก็ <c>OwnerUserId == null</c> เหมือนกันแต่ไม่ได้แปลว่า DM คุมได้)
    ///
    /// รับ <paramref name="campaign"/> เข้ามาแทนการ query ซ้ำ — caller (CombatEndpoints) โหลด
    /// campaign มาแล้วก่อนหน้านี้ในทุก call site เพื่อเช็ค null/DiceMode อยู่แล้ว จึงไม่ต้องเรียก
    /// <see cref="RequireCampaignDmAsync"/> ซ้ำ (เทียบ <c>campaign.OwnerUserId</c> ตรง ๆ พอ เหมือนที่
    /// เมธอดอื่นในไฟล์นี้ทำ)
    ///
    /// ⚠️ ห้ามใช้เมธอดนี้แทน <see cref="RequireCharacterOwnerAsync"/> ที่ <c>PlayerActionEndpoints</c> —
    /// ที่นั่นตั้งใจไม่มี DM override (ผู้เล่นโพสต์ action แทนตัวเองเท่านั้น ตาม §6 ที่ DM override ยัง
    /// ไม่ทำ) semantics ต่างกันโดยตั้งใจ
    /// </summary>
    public static async Task<(Character? Character, IResult? Failure)> RequireCharacterActorAsync(
        DnDEzDbContext db, Guid characterId, Guid campaignId, Campaign campaign, Guid callerUserId)
    {
        var character = await db.Characters
            .SingleOrDefaultAsync(c => c.Id == characterId && c.CampaignId == campaignId);
        if (character is null)
        {
            return (null, Results.NotFound());
        }

        var isOwner = character.OwnerUserId == callerUserId;
        var isDmControlling = campaign.OwnerUserId == callerUserId && character.ControlledByDm;
        if (!isOwner && !isDmControlling)
        {
            return (null, Results.Forbid());
        }

        return (character, null);
    }

    /// <summary>
    /// Sprint F งานที่ 4 — บังคับว่าผู้เรียกต้องเป็น "DM ของแคมเปญ" หรือ "เจ้าของตัวละครที่ถึงตาเดิน
    /// อยู่ตอนนี้" เท่านั้น (ใช้กับ endpoint ใช้ทรัพยากรระหว่างเทิร์น เช่น spend action/movement)
    ///
    /// ต่างจาก <see cref="RequireCharacterActorAsync"/> ตรงที่อันนั้นผูกกับ **ตัวละคร** ("เจ้าของ
    /// ตัวละคร หรือ DM ที่คุมตัวละครที่ <c>ControlledByDm == true</c>") ส่วนเมธอดนี้ผูกกับ **เทิร์น**
    /// ("DM ของแคมเปญ หรือเจ้าของตัวละครที่ถึงตาเดินอยู่ตอนนี้") — คนละแกนกัน
    ///
    /// DM ผ่านได้เสมอโดยไม่ต้องดู <c>ControlledByDm</c> เพราะ DM เป็นคนเดินวง initiative เอง
    /// (endpoint next-turn/end เป็น DM-only อยู่แล้ว จึงไม่มีทางที่ DM จะ "ไม่ควบคุม" เทิร์นที่ตัวเอง
    /// เป็นคนเดินให้)
    ///
    /// เป็น synchronous ไม่แตะ DB โดยตั้งใจ — ลำดับ 404-ก่อน-403 ถูกจัดการไปแล้วตอน caller โหลด
    /// campaign/encounter/participant (RLS กรองให้แล้ว) โหลดไม่เจอ = 404 ก่อนจะมาถึงเมธอดนี้
    ///
    /// ตัวละคร quick-play ที่ <c>OwnerUserId == null</c> ไม่มีวันผ่าน (null != Guid เสมอ) → เหลือแค่
    /// DM สั่งแทน ตรงกับเมธอดอื่นในไฟล์นี้
    /// </summary>
    public static IResult? RequireDmOrCurrentTurnCharacterOwner(
        Campaign campaign, Character currentTurnCharacter, Guid callerUserId)
    {
        if (campaign.OwnerUserId == callerUserId)
        {
            return null;
        }

        if (currentTurnCharacter.OwnerUserId == callerUserId)
        {
            return null;
        }

        return Results.Forbid();
    }
}
