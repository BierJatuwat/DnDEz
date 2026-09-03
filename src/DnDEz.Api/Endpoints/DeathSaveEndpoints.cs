using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using DeathSaveStateEntity = DnDEz.Domain.Entities.DeathSaveState;
// alias ทีละตัวแทน `using DnDEz.Domain.Enums;` ทั้ง namespace โดยตั้งใจ — ถ้า import ทั้งก้อนจะชนกับ
// DnDEz.Rules ที่ import ไว้แล้ว (ทั้งสอง namespace มี DeathSaveStatus ชื่อเดียวกัน)
using DeathSaveVisibility = DnDEz.Domain.Enums.DeathSaveVisibility;
using DiceMode = DnDEz.Domain.Enums.DiceMode;
using DomainDeathSaveStatus = DnDEz.Domain.Enums.DeathSaveStatus;
using RulesDeathSaveState = DnDEz.Rules.DeathSaveState;
using RulesDeathSaveStatus = DnDEz.Rules.DeathSaveStatus;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint F งานที่ 5 — death save state machine ผ่าน HTTP: confirm-dying / roll / GET state
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/DeathSaveEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด (เหตุผลของ decision ที่ไม่ชัดในตัวโค้ด):
///  • **confirm-dying เป็น DM-only แม้แต่เจ้าของตัวละครเองก็ทำไม่ได้** — decision ล็อกไว้ว่าการ
///    "ประกาศว่าตัวละครกำลังจะตาย" เป็นสิทธิ์ควบคุม pacing ของ DM ไม่ใช่ของผู้เล่น (ผู้เล่นเห็นแค่ผลลัพธ์
///    ผ่าน broadcast/GET) ต่างจาก roll ที่เป็นสิทธิ์ของเจ้าของตัวละคร (หรือ DM ที่คุม NPC/monster)
///  • **roll ต้องมี <c>{sessionId}</c> ใน path แต่ confirm-dying/GET ไม่มี** — เพราะ roll เป็นการทอย
///    ลูกเต๋าจริงที่ต้องบันทึกลง <c>dice_rolls</c> ซึ่งมี <c>session_id</c> เป็น NOT NULL + FK ไป
///    <c>sessions</c> (รูปแบบเดียวกับ checks/attacks ใน CombatEndpoints เป๊ะ) ส่วน confirm-dying/GET
///    ไม่ทอยเต๋าเลยจึงไม่ต้องผูก session
///  • **มี enum ชื่อ <c>DeathSaveStatus</c> (และ record ชื่อ <c>DeathSaveState</c>) ชนกัน 2 ที่**:
///    <see cref="DnDEz.Rules.DeathSaveStatus"/> (มี NotDying เพิ่มมาสำหรับ engine — ดู doc comment ของ
///    มันเอง) กับ <see cref="DnDEz.Domain.Enums.DeathSaveStatus"/> (คอลัมน์ DB มีแค่ Rolling/Stable/Dead
///    เพราะ NotDying แทนด้วย "ไม่มีแถวอยู่เลย") จึงต้อง alias ทั้งคู่ที่หัวไฟล์ (ไม่งั้น compile ไม่ผ่าน —
///    ambiguous reference) และมี <see cref="DeathSaveMapping"/> แปลงสองทางให้ endpoint อื่น
///    (CombatEndpoints — auto-failure ตอนโดนตีที่ 0 HP) เรียกใช้ต่อได้โดยไม่ต้องเขียน mapping ซ้ำ
///  • **roll ตอน nat20 (RevivedWithOneHp) ต้องลบแถว <c>death_save_states</c> ทิ้ง** ไม่ใช่แค่ set
///    Status เป็นค่าไหนสักค่า — เพราะ Domain enum ไม่มีค่า NotDying ให้ set (ไม่มีแถว = NotDying ตาม
///    convention เดียวกับ GET ตอนไม่มีแถว)
/// </summary>
public static class DeathSaveEndpoints
{
    public static void MapDeathSaveEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/characters/{characterId:guid}/death-saves/confirm-dying", async (
            Guid campaignId,
            Guid characterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IHubContext<CampaignHub> hubContext,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(typeof(DeathSaveEndpoints));

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (campaign, dmFailure) = await CampaignAuthorization.RequireCampaignDmAsync(
                db, campaignId, callerUserId.Value);
            if (dmFailure is not null)
            {
                return dmFailure;
            }

            var character = await db.Characters
                .SingleOrDefaultAsync(c => c.Id == characterId && c.CampaignId == campaignId);
            if (character is null)
            {
                return Results.NotFound();
            }

            if (character.CurrentHp != 0)
            {
                return Results.BadRequest();
            }

            var existing = await db.DeathSaveStates.SingleOrDefaultAsync(d => d.CharacterId == characterId);
            if (existing is not null)
            {
                return Results.Conflict();
            }

            var state = new DeathSaveStateEntity
            {
                CharacterId = characterId,
                Successes = 0,
                Failures = 0,
                Status = DomainDeathSaveStatus.Rolling,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.DeathSaveStates.Add(state);
            await db.SaveChangesAsync();

            await BroadcastAsync(hubContext, logger, campaign!, "DeathSaveStateChanged",
                new DeathSaveStateChangedBroadcast(characterId, 0, 0, "Rolling"));

            return Results.Ok(new DeathSaveStateResponse(characterId, 0, 0, "Rolling"));
        }).RequireAuthorization();

        app.MapPost(
            "/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/characters/{characterId:guid}/death-saves/roll",
            async (
            Guid campaignId,
            Guid sessionId,
            Guid characterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IHubContext<CampaignHub> hubContext,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(typeof(DeathSaveEndpoints));

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            var (actorCharacter, actorFailure) = await CampaignAuthorization.RequireCharacterActorAsync(
                db, characterId, campaignId, campaign, callerUserId.Value);
            if (actorFailure is not null)
            {
                return actorFailure;
            }

            var character = actorCharacter!;

            if (campaign.DiceMode != DiceMode.SystemRoll)
            {
                return Results.BadRequest();
            }

            var stateRow = await db.DeathSaveStates.SingleOrDefaultAsync(d => d.CharacterId == characterId);
            if (stateRow is null || stateRow.Status != DomainDeathSaveStatus.Rolling)
            {
                return Results.Conflict();
            }

            var currentState = new RulesDeathSaveState(
                stateRow.Successes, stateRow.Failures, RulesDeathSaveStatus.Rolling);

            var roller = SeededDiceRoller.CreateWithSecureRandomSeed();
            var engine = new DiceEngine();
            var roll = engine.RollSystem("1d20", roller, roller.Seed, AdvantageMode.None);

            // Status ถูกเช็คเป็น Rolling ไปแล้วด้านบน — DeathSaveResolver.Resolve โยน
            // InvalidOperationException ถ้า currentState.Status != Rolling เท่านั้น จึงไม่มีทางหลุดเป็น
            // 500 จากจุดนี้ (กันไว้ตามข้อกำหนดคุณภาพ)
            var resolveResult = DeathSaveResolver.Resolve(roll, currentState);

            var diceRoll = new DiceRoll
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                CharacterId = characterId,
                Expression = roll.Expression,
                Rolls = JsonSerializer.Serialize(roll.Rolls),
                Total = roll.Total,
                Source = DiceMode.SystemRoll,
                Seed = roller.Seed,
                EnteredByUserId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.DiceRolls.Add(diceRoll);

            var currentHp = character.CurrentHp;
            var revived = resolveResult.RevivedWithOneHp;
            int successes;
            int failures;
            string statusString;

            if (revived)
            {
                var healResult = HitPointResolver.ApplyHealing(0, character.MaxHp, 1);
                character.CurrentHp = healResult.NewHp;
                currentHp = healResult.NewHp;

                // nat20 = จบกระบวนการทั้งหมด — ลบแถวทิ้งแทนการ set สถานะ เพราะ Domain enum ไม่มีค่า
                // NotDying ให้ set (ดู doc comment หัว class)
                db.DeathSaveStates.Remove(stateRow);

                successes = 0;
                failures = 0;
                statusString = "NotDying";
            }
            else
            {
                stateRow.Successes = resolveResult.NewState.Successes;
                stateRow.Failures = resolveResult.NewState.Failures;
                stateRow.Status = DeathSaveMapping.ToDomain(resolveResult.NewState.Status);
                stateRow.UpdatedAt = DateTimeOffset.UtcNow;

                successes = stateRow.Successes;
                failures = stateRow.Failures;
                statusString = stateRow.Status.ToString();
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // security fix (Sprint F งานที่ 5 review) — อีก request ทอยพร้อมกันแล้วเขียนทับ
                // stateRow นี้ไปก่อนแล้ว (xmin ที่อ่านมาตอนต้น handler ไม่ตรงกับของจริงใน DB อีกต่อไป)
                // ปฏิเสธเป็น 409 ไม่ retry เอง — ให้ client re-fetch สถานะล่าสุดแล้วลองใหม่เอง (เหมือน
                // เช็ค Status != Rolling ด้านบนที่ก็ตอบ 409 อยู่แล้วสำหรับเคส "ทอยไม่ได้อีกต่อไป")
                return Results.Conflict();
            }

            var keptD20Value = roll.Rolls.Single(r => r is { Sides: 20, Dropped: false }).Value;

            await BroadcastAsync(hubContext, logger, campaign, "DeathSaveRolled",
                new DeathSaveRolledBroadcast(
                    characterId, keptD20Value, resolveResult.Outcome.ToString(),
                    successes, failures, statusString, revived, currentHp));

            return Results.Ok(new DeathSaveRollResponse(
                diceRoll.Id,
                characterId,
                roll.Expression,
                roll.Rolls.Select(r => new RollDetailResponse(r.Sides, r.Value, r.Dropped, r.Sign)).ToList(),
                roll.Total,
                resolveResult.Outcome.ToString(),
                successes,
                failures,
                statusString,
                revived,
                currentHp));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/characters/{characterId:guid}/death-saves", async (
            Guid campaignId,
            Guid characterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // RLS policy `campaign_member_read` กรองแคมเปญที่ไม่ได้เป็นสมาชิกทิ้งไปแล้ว — คืน null =
            // 404 เหมือนไม่มีอยู่จริง (pattern เดียวกับ CampaignAuthorization/CampaignVisibility)
            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var character = await db.Characters
                .SingleOrDefaultAsync(c => c.Id == characterId && c.CampaignId == campaignId);
            if (character is null)
            {
                return Results.NotFound();
            }

            if (campaign.DeathSaveVisibility == DeathSaveVisibility.DmOnly
                && campaign.OwnerUserId != callerUserId)
            {
                return Results.Forbid();
            }

            var stateRow = await db.DeathSaveStates.SingleOrDefaultAsync(d => d.CharacterId == characterId);
            if (stateRow is null)
            {
                // ไม่มีแถว = "ไม่ได้กำลังจะตาย" เป็นสถานะปกติ ไม่ใช่ของหาย — 200 ไม่ใช่ 404
                return Results.Ok(new DeathSaveStateResponse(characterId, 0, 0, "NotDying"));
            }

            return Results.Ok(new DeathSaveStateResponse(
                characterId, stateRow.Successes, stateRow.Failures, stateRow.Status.ToString()));
        }).RequireAuthorization();
    }

    /// <summary>เลือกปลายทาง broadcast ตาม <c>DeathSaveVisibility</c> ของแคมเปญ — PartyVisible เข้ากลุ่ม
    /// SignalR ของแคมเปญ (ทุกคนเห็น) ส่วน DmOnly ส่งตรงถึง user เดียวคือเจ้าของแคมเปญ (แพทเทิร์นเดียวกับ
    /// ProposalCreated ใน OrchestratorTurnRunner.cs)</summary>
    private static async Task BroadcastAsync(
        IHubContext<CampaignHub> hubContext, ILogger logger, Campaign campaign, string eventName, object payload)
    {
        try
        {
            var target = campaign.DeathSaveVisibility == DeathSaveVisibility.DmOnly
                ? hubContext.Clients.User(campaign.OwnerUserId.ToString())
                : hubContext.Clients.Group(CampaignHub.GroupName(campaign.Id));

            await target.SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast {EventName} for campaign {CampaignId}", eventName, campaign.Id);
        }
    }
}

/// <summary>
/// แปลง <see cref="DnDEz.Rules.DeathSaveStatus"/> (มี NotDying) ↔ <see cref="DnDEz.Domain.Enums.DeathSaveStatus"/>
/// (ไม่มี NotDying — ใช้ "ไม่มีแถวในตาราง death_save_states" แทน) — ประกาศ public ในไฟล์นี้เพราะ
/// CombatEndpoints (auto-failure ตอนโดนตีที่ 0 HP) ต้องเรียกใช้ต่อด้วย ไม่ใช่แค่ไฟล์นี้
/// </summary>
internal static class DeathSaveMapping
{
    public static RulesDeathSaveStatus ToRules(DomainDeathSaveStatus status) => status switch
    {
        DomainDeathSaveStatus.Rolling => RulesDeathSaveStatus.Rolling,
        DomainDeathSaveStatus.Stable => RulesDeathSaveStatus.Stable,
        DomainDeathSaveStatus.Dead => RulesDeathSaveStatus.Dead,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "ค่า Domain DeathSaveStatus ที่ไม่รู้จัก"),
    };

    public static DomainDeathSaveStatus ToDomain(RulesDeathSaveStatus status) => status switch
    {
        RulesDeathSaveStatus.Rolling => DomainDeathSaveStatus.Rolling,
        RulesDeathSaveStatus.Stable => DomainDeathSaveStatus.Stable,
        RulesDeathSaveStatus.Dead => DomainDeathSaveStatus.Dead,
        RulesDeathSaveStatus.NotDying => throw new InvalidOperationException(
            "NotDying ไม่มีค่าตรงกันใน Domain enum — caller ต้องลบแถว DeathSaveState แทนการ map ค่านี้ " +
            "(ดู doc comment หัว DeathSaveEndpoints)"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "ค่า Rules DeathSaveStatus ที่ไม่รู้จัก"),
    };
}

internal record DeathSaveStateResponse(Guid CharacterId, int Successes, int Failures, string Status);

internal record DeathSaveRollResponse(
    Guid Id,
    Guid CharacterId,
    string Expression,
    IReadOnlyList<RollDetailResponse> Rolls,
    int Total,
    string Outcome,
    int Successes,
    int Failures,
    string Status,
    bool Revived,
    int CurrentHp);

internal record DeathSaveStateChangedBroadcast(Guid CharacterId, int Successes, int Failures, string Status);

internal record DeathSaveRolledBroadcast(
    Guid CharacterId,
    int Roll,
    string Outcome,
    int Successes,
    int Failures,
    string Status,
    bool Revived,
    int CurrentHp);
