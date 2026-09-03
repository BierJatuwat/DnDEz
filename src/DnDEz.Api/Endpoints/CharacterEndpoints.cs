using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>Sprint F งานที่ 7 — helper กำหนดว่าตัวเลข stat (AC/HP/resistance) ของตัวละครหนึ่งตัว
/// ควรส่งให้ caller คนนี้เห็นจริงไหม เกณฑ์: ไม่ใช่ NPC (ControlledByDm=false) เห็นได้เสมอ, เป็น NPC
/// ที่ NpcStatsVisibleToParty=true เห็นได้เสมอ, เป็น NPC ที่ปิดการมองเห็นไว้ (false) เห็นได้เฉพาะ DM
/// เท่านั้น — ใช้ทั้งใน list และ detail ให้เกณฑ์ตรงกันเป๊ะ ไม่ให้สองจุดเพี้ยนจากกัน</summary>
internal static class CharacterStatsVisibility
{
    public static bool IsVisibleTo(bool controlledByDm, bool npcStatsVisibleToParty, bool callerIsDm) =>
        !controlledByDm || npcStatsVisibleToParty || callerIsDm;
}

/// <summary>
/// Sprint A Part 3 — thin CRUD ของ `characters` ภายใต้แคมเปญที่เลือกแล้ว (create + list + get)
/// Sprint B Part 2 — เพิ่ม combat stats (HP/AC/resistance) ที่ promote จาก Sheet jsonb เป็น typed
/// column แล้ว (ดู migration `AddCharacterCombatStats` + PROJECT-CONTEXT §4 "Sprint B Part 2")
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/CharacterCrudTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด:
///  • `OwnerUserId` มาจาก claim ของ JWT เท่านั้น (ClaimsPrincipalExtensions.GetUserId) ห้ามรับจาก body
///    ไม่งั้นใครก็สร้างตัวละคร "ของคนอื่น" ได้
///  • ลิสต์ห้ามส่ง `Sheet` (jsonb ทั้งก้อน) ออกไป — เฉพาะ endpoint ตัวเดียวเท่านั้นที่ส่ง
///  • Level ต้องอยู่ในช่วง 1–20 (ck_characters_level) — ดักที่ handler ก่อนแตะ DB
///  • ไม่ส่ง MaxHp มา = default 10 (safe placeholder ไม่ใช่ค่าที่ถูกต้องสำหรับตัวละครจริง — DM/ผู้เล่น
///    ต้องมา update ทีหลัง) · ไม่ส่ง CurrentHp มา = full health ตาม MaxHp ที่ resolve แล้ว (ตัวละคร
///    ใหม่เริ่มเต็มเลือดเสมอ) · CurrentHp ต้องอยู่ใน [0, MaxHp] เสมอ (ck_characters_current_hp) —
///    validate ที่ handler ก่อนแตะ DB เพื่อคืน 400 ไม่ใช่ 500 (transaction เดียวคลุมทั้ง request)
///  • damage type string ต้องตรงกับ `DnDEz.Rules.DamageType` enum เป๊ะ — validate ด้วย Enum.TryParse
///    ไม่ hardcode รายชื่อซ้ำ (กัน list สองที่ไม่ตรงกันในอนาคต)
/// </summary>
public static class CharacterEndpoints
{
    private const int MaxNameLength = 100;
    private const int MinLevel = 1;
    private const int MaxLevel = 20;
    private const int DefaultMaxHp = 10;
    private const int DefaultArmorClass = 10;
    private const int MinArmorClass = 1;
    private const int MaxArmorClass = 50;

    public static void MapCharacterEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/characters", async (
            Guid campaignId,
            CreateCharacterRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var name = request.Name;
            var level = request.Level ?? 1;

            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            {
                return Results.BadRequest();
            }

            if (level < MinLevel || level > MaxLevel)
            {
                return Results.BadRequest();
            }

            var maxHp = request.MaxHp ?? DefaultMaxHp;
            if (maxHp < 1)
            {
                return Results.BadRequest();
            }

            // ไม่ส่ง CurrentHp มา = เต็มเลือดตาม maxHp ที่ resolve แล้ว (รวม default ด้วย) ไม่ใช่ตาม
            // DefaultMaxHp เฉย ๆ — ถ้า caller ส่ง MaxHp=42 มาแต่ไม่ส่ง CurrentHp ต้องได้ CurrentHp=42
            var currentHp = request.CurrentHp ?? maxHp;
            if (currentHp < 0 || currentHp > maxHp)
            {
                return Results.BadRequest();
            }

            var currentTempHp = request.CurrentTempHp ?? 0;
            if (currentTempHp < 0)
            {
                return Results.BadRequest();
            }

            var armorClass = request.ArmorClass ?? DefaultArmorClass;
            if (armorClass < MinArmorClass || armorClass > MaxArmorClass)
            {
                return Results.BadRequest();
            }

            var damageResistances = request.DamageResistances ?? [];
            var damageVulnerabilities = request.DamageVulnerabilities ?? [];
            var damageImmunities = request.DamageImmunities ?? [];
            if (!AreValidDamageTypes(damageResistances) ||
                !AreValidDamageTypes(damageVulnerabilities) ||
                !AreValidDamageTypes(damageImmunities))
            {
                return Results.BadRequest();
            }

            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // Sprint F งานที่ 7 — สร้าง NPC/monster ได้เฉพาะ DM เท่านั้น (IsNpc=true) ตัวละครผู้เล่น
            // ปกติ (IsNpc=false/null) ใครก็สร้างของตัวเองได้เหมือนเดิมทุกประการ ไม่กระทบ path เดิม
            var isNpc = request.IsNpc ?? false;
            Guid? ownerUserId = callerUserId.Value;

            if (isNpc)
            {
                // ใช้ helper กลางเดียวกับที่ endpoint อื่นใช้ทั่วระบบ (Sprint F งานที่ 3) ไม่หมุน DM
                // check เองซ้ำ — กันไม่ให้เกิด pattern ซ้ำกระจายที่ต่างกันแบบก่อนงานที่ 3 อีก
                var (_, dmFailure) = await CampaignAuthorization.RequireCampaignDmAsync(
                    db, campaignId, callerUserId.Value);
                if (dmFailure is not null)
                {
                    return dmFailure;
                }

                // NPC ไม่มีเจ้าของเป็นผู้เล่น — DM คุมผ่าน ControlledByDm (ดู
                // CampaignAuthorization.RequireCharacterActorAsync) ไม่ใช่ OwnerUserId
                ownerUserId = null;
            }

            var statsVisibleToParty = request.StatsVisibleToParty ?? true;

            var character = new Character
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                OwnerUserId = ownerUserId,
                Name = name,
                Level = level,
                Sheet = "{}",
                CurrentHp = currentHp,
                MaxHp = maxHp,
                CurrentTempHp = currentTempHp,
                ArmorClass = armorClass,
                DamageResistances = damageResistances,
                DamageVulnerabilities = damageVulnerabilities,
                DamageImmunities = damageImmunities,
                ControlledByDm = isNpc,
                NpcStatsVisibleToParty = statsVisibleToParty,
                HarvestConsent = false,
                IsOrphan = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Characters.Add(character);
            await db.SaveChangesAsync();

            return Results.Created(
                $"/campaigns/{campaignId}/characters/{character.Id}",
                new CharacterCreatedResponse(
                    character.Id,
                    character.CampaignId,
                    character.Name,
                    character.Level,
                    character.OwnerUserId,
                    character.ControlledByDm,
                    character.NpcStatsVisibleToParty,
                    character.CurrentHp,
                    character.MaxHp,
                    character.CurrentTempHp,
                    character.ArmorClass,
                    character.DamageResistances,
                    character.DamageVulnerabilities,
                    character.DamageImmunities));
        }).RequireAuthorization();

        // Sprint F งานที่ 7 — DM toggle การมองเห็น stat ของ NPC ระหว่างเล่นได้ (เช่น เผยค่าที่แน่นอน
        // หลังผู้เล่นรู้จักมอนสเตอร์ตัวนั้นแล้ว) 400 ถ้าไม่ใช่ NPC เพราะ flag นี้ไม่มีความหมายกับ
        // ตัวละครผู้เล่น (เห็นได้เสมอไม่ว่าค่าไหน) — กัน DM เผลอเซ็ตแล้วคิดว่ามีผลทั้งที่ไม่มี
        app.MapPut("/campaigns/{campaignId:guid}/characters/{characterId:guid}/npc-visibility", async (
            Guid campaignId,
            Guid characterId,
            SetNpcVisibilityRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (campaign, failure) = await CampaignAuthorization.RequireCampaignDmAsync(
                db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var character = await db.Characters
                .SingleOrDefaultAsync(c => c.Id == characterId && c.CampaignId == campaignId);
            if (character is null)
            {
                return Results.NotFound();
            }

            if (!character.ControlledByDm)
            {
                return Results.BadRequest();
            }

            character.NpcStatsVisibleToParty = request.Visible;
            await db.SaveChangesAsync();

            return Results.Ok(new SetNpcVisibilityResponse(character.Id, character.NpcStatsVisibleToParty));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/characters", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var callerUserId = principal.GetUserId();
            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            var callerIsDm = campaign is not null && callerUserId is not null
                && campaign.OwnerUserId == callerUserId.Value;

            var characters = await db.Characters
                .Where(c => c.CampaignId == campaignId)
                .OrderBy(c => c.Id)
                .Select(c => new
                {
                    c.Id, c.Name, c.Level, c.OwnerUserId, c.ControlledByDm, c.NpcStatsVisibleToParty,
                    c.CurrentHp, c.MaxHp, c.CurrentTempHp,
                })
                .ToListAsync();

            var items = characters.Select(c =>
                CharacterStatsVisibility.IsVisibleTo(c.ControlledByDm, c.NpcStatsVisibleToParty, callerIsDm)
                ? new CharacterListItem(c.Id, c.Name, c.Level, c.OwnerUserId, c.ControlledByDm, c.CurrentHp, c.MaxHp, c.CurrentTempHp)
                : new CharacterListItem(c.Id, c.Name, c.Level, c.OwnerUserId, c.ControlledByDm, null, null, null));

            return Results.Ok(items);
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/characters/{characterId:guid}", async (
            Guid campaignId,
            Guid characterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var callerUserId = principal.GetUserId();
            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            var callerIsDm = campaign is not null && callerUserId is not null
                && campaign.OwnerUserId == callerUserId.Value;

            var character = await db.Characters
                .Where(c => c.Id == characterId && c.CampaignId == campaignId)
                .SingleOrDefaultAsync();

            if (character is null)
            {
                return Results.NotFound();
            }

            var statsVisible = CharacterStatsVisibility.IsVisibleTo(
                character.ControlledByDm, character.NpcStatsVisibleToParty, callerIsDm);

            // Sheet (jsonb เต็ม) เป็น stat เหมือนกัน — NPC ที่ปิดการมองเห็นไว้ต้องไม่ส่ง Sheet ให้
            // non-DM เห็นเช่นกัน (จะเผลอมี AC/HP ที่แท้จริงปนอยู่ใน jsonb ก็ได้ในอนาคต ไม่ใช่แค่คอลัมน์
            // typed ที่ promote ออกมาแล้ว)
            return Results.Ok(new CharacterDetailResponse(
                character.Id, character.CampaignId, character.Name, character.Level,
                statsVisible ? character.Sheet : null,
                character.OwnerUserId, character.ControlledByDm, character.NpcStatsVisibleToParty,
                character.HarvestConsent, character.IsOrphan, character.CreatedAt,
                statsVisible ? character.CurrentHp : null,
                statsVisible ? character.MaxHp : null,
                statsVisible ? character.CurrentTempHp : null,
                statsVisible ? character.ArmorClass : null,
                statsVisible ? character.DamageResistances : null,
                statsVisible ? character.DamageVulnerabilities : null,
                statsVisible ? character.DamageImmunities : null));
        }).RequireAuthorization();
    }

    private static bool AreValidDamageTypes(IEnumerable<string> values) =>
        values.All(v => Enum.TryParse<DnDEz.Rules.DamageType>(v, out _));
}

internal record CreateCharacterRequest(
    string? Name,
    int? Level,
    int? MaxHp,
    int? CurrentHp,
    int? CurrentTempHp,
    int? ArmorClass,
    string[]? DamageResistances,
    string[]? DamageVulnerabilities,
    string[]? DamageImmunities,
    // Sprint F งานที่ 7 — DM เท่านั้นที่ตั้ง IsNpc=true ได้ (403 ถ้าไม่ใช่) StatsVisibleToParty มีผล
    // เฉพาะตอน IsNpc=true เท่านั้น (default true ถ้าไม่ส่งมา)
    bool? IsNpc,
    bool? StatsVisibleToParty);

internal record SetNpcVisibilityRequest(bool Visible);

internal record SetNpcVisibilityResponse(Guid CharacterId, bool Visible);

internal record CharacterCreatedResponse(
    Guid Id,
    Guid CampaignId,
    string Name,
    int Level,
    Guid? OwnerUserId,
    bool ControlledByDm,
    bool NpcStatsVisibleToParty,
    int CurrentHp,
    int MaxHp,
    int CurrentTempHp,
    int ArmorClass,
    string[] DamageResistances,
    string[] DamageVulnerabilities,
    string[] DamageImmunities);

// Sprint E Part 3 — เพิ่ม HP fields เข้า list (Sheet jsonb เต็มยังไม่ส่งเหมือนเดิม แค่คอลัมน์ HP
// ที่ promote ออกมาแล้วตั้งแต่ Sprint B Part 2 ราคาถูกพอจะส่งใน list ได้) ให้ frontend แสดง HP
// realtime ในรายการตัวละครได้โดยไม่ต้อง fetch detail ทีละตัว
//
// Sprint F งานที่ 7 — HP fields เป็น nullable แล้ว: null หมายถึง "ตัวละครนี้เป็น NPC ที่ DM ปิดการ
// มองเห็น stat ไว้ และ caller ไม่ใช่ DM" (ดู CharacterStatsVisibility.IsVisibleTo) ตัวละครผู้เล่นปกติ
// หรือ NPC ที่เปิดให้เห็น จะยังได้ค่าจริงเหมือนเดิมทุกประการ ไม่ใช่ breaking change สำหรับเคสเดิม
internal record CharacterListItem(
    Guid Id, string Name, int Level, Guid? OwnerUserId, bool ControlledByDm,
    int? CurrentHp, int? MaxHp, int? CurrentTempHp);

internal record CharacterDetailResponse(
    Guid Id,
    Guid CampaignId,
    string Name,
    int Level,
    string? Sheet,
    Guid? OwnerUserId,
    bool ControlledByDm,
    bool NpcStatsVisibleToParty,
    bool HarvestConsent,
    bool IsOrphan,
    DateTimeOffset CreatedAt,
    int? CurrentHp,
    int? MaxHp,
    int? CurrentTempHp,
    int? ArmorClass,
    string[]? DamageResistances,
    string[]? DamageVulnerabilities,
    string[]? DamageImmunities);
