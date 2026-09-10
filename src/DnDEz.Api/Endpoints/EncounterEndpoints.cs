using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Encounters;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint F งานที่ 4 — encounter / initiative / turn order (ชั้น endpoint)
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/EncounterEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
/// </summary>
public static class EncounterEndpoints
{
    /// <summary>
    /// ระยะเคลื่อนที่ fallback เมื่อหา "ความเร็วของ participant ที่ถึงตาเดินอยู่" ไม่ได้จริง ๆ —
    /// เช่น <see cref="Encounter.CurrentParticipantId"/> เป็น null หรือ encounter กำลังถูก
    /// teardown กลางอากาศจนไม่มีผู้เข้าร่วมคนไหนตรงกับ id นั้นแล้ว **ไม่ใช่ค่าที่ใช้กับทุกตัวละคร
    /// อีกต่อไป** (ชื่อเดิม <c>DefaultMovementFeet</c> ทำให้เข้าใจผิดแบบนั้น)
    ///
    /// เดิม (Sprint F งานที่ 4) ค่านี้ hardcode ใช้กับทุกตัวละครเพราะสคีมายังไม่มีฟิลด์ speed เลย —
    /// Sprint G Part 1 เพิ่ม <see cref="Character.SpeedFeet"/> แล้ว (คอลัมน์ <c>speed_feet</c>,
    /// default 30) Sprint G Part 8 (รอบนี้) จึงเปลี่ยนทุกจุดให้อ่านความเร็วจริงของ participant
    /// ที่ถึงตาเดิน (อ่านสดทุกเทิร์น ไม่ snapshot — รองรับ effect ที่ทำให้ speed เปลี่ยนกลางเทิร์น
    /// ซึ่งเป็นเหตุผลที่ <see cref="ActionEconomyResolver.StartTurn"/> รับ maxMovementFeet ใหม่
    /// ทุกครั้ง) ค่าคงที่นี้จึงเหลือไว้เฉพาะกรณีขอบที่ไม่มีผู้เข้าร่วมให้อ่านค่าได้จริง
    /// </summary>
    private const int FallbackMovementFeet = 30;

    /// <summary>เพดานจำนวนผู้เข้าร่วมต่อ encounter — กัน payload ใหญ่ผิดปกติและกันการทอยเต๋าจำนวนมากต่อ request เดียว</summary>
    private const int MaxParticipants = 20;

    /// <summary>จำนวนครั้งสูงสุดที่ endpoint spend จะ retry เมื่อเจอ compare-and-set conflict (มี request อื่นเขียนแทรกระหว่างอ่าน-คิด-เขียน)</summary>
    private const int MaxSpendAttempts = 3;

    public static void MapEncounterEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EncounterEndpoints));

        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/encounters", async (
            Guid campaignId,
            Guid sessionId,
            CreateEncounterRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IEncounterTurnStateStore turnStateStore,
            IHubContext<CampaignHub> hubContext) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var characterIds = request.CharacterIds;
            if (characterIds is null
                || characterIds.Count == 0
                || characterIds.Count > MaxParticipants
                || characterIds.Any(id => id == Guid.Empty)
                || characterIds.Distinct().Count() != characterIds.Count)
            {
                return Results.BadRequest();
            }

            var (campaign, dmFailure) = await CampaignAuthorization.RequireCampaignDmAsync(
                db, campaignId, callerUserId.Value);
            if (dmFailure is not null)
            {
                return dmFailure;
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            var characters = await db.Characters
                .Where(c => c.CampaignId == campaignId && characterIds.Contains(c.Id))
                .ToListAsync();
            if (characters.Count != characterIds.Count)
            {
                return Results.NotFound();
            }

            var hasActiveEncounter = await db.Encounters
                .AnyAsync(e => e.SessionId == sessionId && e.EndedAt == null);
            if (hasActiveEncounter)
            {
                return Results.Conflict();
            }

            var entries = new List<(Character Character, InitiativeEntry Entry)>();
            foreach (var character in characters)
            {
                var roller = SeededDiceRoller.CreateWithSecureRandomSeed();
                var engine = new DiceEngine();
                var roll = engine.RollSystem("1d20", roller, roller.Seed, AdvantageMode.None);

                db.DiceRolls.Add(new DiceRoll
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    CharacterId = character.Id,
                    Expression = roll.Expression,
                    Rolls = JsonSerializer.Serialize(roll.Rolls),
                    Total = roll.Total,
                    Source = DiceMode.SystemRoll,
                    Seed = roller.Seed,
                    EnteredByUserId = null,
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                entries.Add((character, new InitiativeEntry(character.Id, roll.Total, character.InitiativeModifier)));
            }

            var ordered = InitiativeOrder.Resolve(entries.Select(e => e.Entry));
            var charactersById = characters.ToDictionary(c => c.Id);

            var encounter = new Encounter
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                SessionId = sessionId,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = null,
                TurnNumber = 0,
            };
            db.Encounters.Add(encounter);

            var participants = new List<EncounterParticipant>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                participants.Add(new EncounterParticipant
                {
                    Id = Guid.NewGuid(),
                    EncounterId = encounter.Id,
                    CharacterId = entry.CharacterId,
                    InitiativeRoll = entry.InitiativeRoll,
                    InitiativeModifier = entry.InitiativeModifier,
                    InitiativeTotal = entry.InitiativeTotal,
                    TurnOrder = i,
                });
            }

            db.EncounterParticipants.AddRange(participants);
            await db.SaveChangesAsync();

            // สอง SaveChanges โดยตั้งใจ — FK current_participant_id เป็นวงกลมกับ encounter_id
            // (participant ต้องมีอยู่ในตารางก่อนถึงจะตั้ง CurrentParticipantId ชี้ไปหามันได้) ยังอยู่
            // ใน transaction เดียวของ middleware จึง atomic อยู่ดี
            encounter.CurrentParticipantId = participants[0].Id;
            await db.SaveChangesAsync();

            // ผู้เข้าร่วมคนแรกในลำดับ initiative คือคนที่ถือเทิร์นแรก — งบเคลื่อนที่ของเทิร์นแรกจึง
            // ต้องมาจากความเร็วของตัวละครคนนั้นจริง ๆ (Sprint G Part 8) ไม่ใช่ค่าคงที่เดียวกันทุกคน
            var firstTurnMovementFeet = charactersById[participants[0].CharacterId].SpeedFeet;

            if (!await turnStateStore.TrySetAsync(encounter.Id, 0, ActionEconomyResolver.StartTurn(firstTurnMovementFeet)))
            {
                // การอ่านครั้งหน้า fail closed เป็น StartTurn ค่าเดียวกันเป๊ะอยู่แล้ว จึงไม่มีอะไรเสียหาย
                // — ต่างจาก endpoint spend ที่ต้อง 503 เพราะที่นั่นมี state ที่เขียนไปแล้วก่อนหน้าเสี่ยงหาย
                logger.LogWarning("เขียน turn state เริ่มต้นของเอนเคาน์เตอร์ {EncounterId} ไม่สำเร็จ", encounter.Id);
            }

            var response = BuildEncounterResponse(
                encounter, participants, charactersById, ActionEconomyResolver.StartTurn(firstTurnMovementFeet));

            try
            {
                await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync("EncounterStarted", response);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast EncounterStarted for encounter {EncounterId}", encounter.Id);
            }

            return Results.Created($"/campaigns/{campaignId}/sessions/{sessionId}/encounters/active", response);
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/encounters/{encounterId:guid}/next-turn", async (
            Guid campaignId,
            Guid encounterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IEncounterTurnStateStore turnStateStore,
            IHubContext<CampaignHub> hubContext,
            SceneTokenFlusher tokenFlusher) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, dmFailure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (dmFailure is not null)
            {
                return dmFailure;
            }

            var encounter = await db.Encounters
                .SingleOrDefaultAsync(e => e.Id == encounterId && e.CampaignId == campaignId);
            if (encounter is null)
            {
                return Results.NotFound();
            }

            if (encounter.EndedAt is not null)
            {
                return Results.Conflict();
            }

            var participants = await db.EncounterParticipants
                .Include(p => p.Character)
                .Where(p => p.EncounterId == encounterId)
                .OrderBy(p => p.TurnOrder)
                .ToListAsync();
            if (participants.Count == 0)
            {
                return Results.Conflict();
            }

            var currentIndex = participants.FindIndex(p => p.Id == encounter.CurrentParticipantId);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            var nextIndex = (currentIndex + 1) % participants.Count;
            var nextParticipant = participants[nextIndex];
            encounter.CurrentParticipantId = nextParticipant.Id;
            encounter.TurnNumber += 1;
            await db.SaveChangesAsync();

            // เทิร์นใหม่เป็นของ nextParticipant — งบเคลื่อนที่ต้องอ่านความเร็วสดของตัวละครคนนี้
            // (Sprint G Part 8) ไม่ใช่ค่าคงที่เดียวกันทุกคน
            var newState = ActionEconomyResolver.StartTurn(nextParticipant.Character.SpeedFeet);
            if (!await turnStateStore.TrySetAsync(encounterId, encounter.TurnNumber, newState))
            {
                // เขียนล้มตรงนี้ปล่อยผ่านได้อย่างปลอดภัย เพราะ key ของ turn state ผูกกับเลขเทิร์น
                // (encounterId + TurnNumber) — เทิร์นใหม่จึงอ่าน key ใหม่ที่ยังไม่เคยมีค่าเสมอ แล้ว
                // fall back เป็น "เทิร์นใหม่เต็มทรัพยากร" (ActionEconomyResolver.StartTurn) ซึ่งตรงกับ
                // ค่าที่ตั้งใจเขียนอยู่แล้วเป๊ะ ๆ ค่าของเทิร์นก่อนหน้าจึงไม่มีทางรั่วมาให้ผู้เล่นคนใหม่เห็น
                // — นี่คือผลจากการออกแบบ key ไม่ใช่การยอมรับความเสี่ยง (accept risk)
                logger.LogWarning("เขียน turn state ของเอนเคาน์เตอร์ {EncounterId} ตอน next-turn ไม่สำเร็จ", encounterId);
            }

            var response = new NextTurnResponse(
                encounter.Id, nextParticipant.Id, nextParticipant.CharacterId, nextParticipant.TurnOrder,
                ToTurnStateResponse(newState));

            try
            {
                await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync("TurnChanged", response);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast TurnChanged for encounter {EncounterId}", encounterId);
            }

            // Sprint G Part 5 (design doc §14.3) — เปลี่ยนเทิร์นเป็นจุด persist ตำแหน่ง token ที่
            // ผู้ใช้รับรู้ว่าเป็นหมุด — best-effort เหมือน broadcast ด้านบน ห้ามทำให้ next-turn ล้มเหลว
            await FlushSceneTokensForSessionAsync(db, tokenFlusher, encounter.SessionId, logger);

            return Results.Ok(response);
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/encounters/{encounterId:guid}/end", async (
            Guid campaignId,
            Guid encounterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IEncounterTurnStateStore turnStateStore,
            IHubContext<CampaignHub> hubContext,
            SceneTokenFlusher tokenFlusher) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, dmFailure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (dmFailure is not null)
            {
                return dmFailure;
            }

            var encounter = await db.Encounters
                .SingleOrDefaultAsync(e => e.Id == encounterId && e.CampaignId == campaignId);
            if (encounter is null)
            {
                return Results.NotFound();
            }

            if (encounter.EndedAt is not null)
            {
                return Results.Conflict();
            }

            encounter.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            await turnStateStore.TryRemoveAsync(encounterId, encounter.TurnNumber);

            var response = new EndEncounterResponse(encounter.Id, encounter.EndedAt.Value);

            try
            {
                await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync("EncounterEnded", response);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast EncounterEnded for encounter {EncounterId}", encounterId);
            }

            // Sprint G Part 5 (design doc §14.3) — จบ encounter เป็นอีกจุด persist ตำแหน่ง token
            await FlushSceneTokensForSessionAsync(db, tokenFlusher, encounter.SessionId, logger);

            return Results.Ok(response);
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/encounters/{encounterId:guid}/turn-actions/spend", async (
            Guid campaignId,
            Guid encounterId,
            SpendTurnResourceRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IEncounterTurnStateStore turnStateStore,
            IHubContext<CampaignHub> hubContext) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var encounter = await db.Encounters
                .SingleOrDefaultAsync(e => e.Id == encounterId && e.CampaignId == campaignId);
            if (encounter is null)
            {
                return Results.NotFound();
            }

            if (encounter.EndedAt is not null)
            {
                return Results.Conflict();
            }

            if (encounter.CurrentParticipantId is null)
            {
                return Results.Conflict();
            }

            var currentParticipant = await db.EncounterParticipants
                .Include(p => p.Character)
                .SingleOrDefaultAsync(p => p.Id == encounter.CurrentParticipantId);
            if (currentParticipant is null)
            {
                return Results.Conflict();
            }

            var authFailure = CampaignAuthorization.RequireDmOrCurrentTurnCharacterOwner(
                campaign, currentParticipant.Character, callerUserId.Value);
            if (authFailure is not null)
            {
                return authFailure;
            }

            // currentParticipant ถูก query ด้วย .Include(p => p.Character) ไว้แล้วข้างบน (บรรทัดที่ดึง
            // currentParticipant) และตรงกับ encounter.CurrentParticipantId เสมอ (เช็คแล้วก่อนหน้านี้) —
            // คำนวณครั้งเดียวนอก retry loop ด้านล่าง ไม่ query ซ้ำทุกรอบ attempt
            var fallbackMovementFeet = CurrentParticipantMovementFeet(
                encounter.CurrentParticipantId, new[] { currentParticipant });

            if (request.Resource is not ("Action" or "BonusAction" or "Reaction" or "Movement"))
            {
                return Results.BadRequest();
            }

            if (request.Resource == "Movement" && (request.Feet is null || request.Feet < 0))
            {
                return Results.BadRequest();
            }

            // retry loop ด้วย compare-and-set: ต้อง "อ่านใหม่" ทุกรอบ ไม่ใช่แค่ลดโอกาสพลาด แต่ถูกต้อง
            // เชิงความหมาย — ถ้ามี request อื่นแย่งใช้ทรัพยากรตัวเดียวกันไปแล้วระหว่างที่เรารอ รอบถัดไป
            // จะอ่านเจอ state ล่าสุดที่ทรัพยากรถูกใช้ไปแล้วจริง แล้วคิดใหม่จะโยน InvalidOperationException
            // (double-spend) ให้ตอบ 400 ซึ่งเป็นคำตอบที่ถูกต้อง — ไม่ใช่ 409 เพราะนี่ไม่ใช่ความขัดแย้งของ
            // การเขียน แต่เป็น business rule ที่ทรัพยากรของเทิร์นถูกใช้ไปแล้วจริง ๆ
            for (var attempt = 0; attempt < MaxSpendAttempts; attempt++)
            {
                var snapshot = await turnStateStore.GetSnapshotAsync(
                    encounterId, encounter.TurnNumber, ActionEconomyResolver.StartTurn(fallbackMovementFeet));

                ActionEconomyState newState;
                try
                {
                    newState = request.Resource switch
                    {
                        "Action" => ActionEconomyResolver.SpendAction(snapshot.State),
                        "BonusAction" => ActionEconomyResolver.SpendBonusAction(snapshot.State),
                        "Reaction" => ActionEconomyResolver.SpendReaction(snapshot.State),
                        "Movement" => ActionEconomyResolver.SpendMovement(snapshot.State, request.Feet!.Value),
                        _ => throw new InvalidOperationException("unreachable"),
                    };
                }
                catch (InvalidOperationException)
                {
                    // double-spend / movement เกินคงเหลือ — input error ของ caller ไม่ใช่ bug ของ server
                    return Results.BadRequest();
                }
                catch (ArgumentOutOfRangeException)
                {
                    return Results.BadRequest();
                }

                var write = await turnStateStore.CompareAndSetAsync(
                    encounterId, encounter.TurnNumber, snapshot.Version, newState);

                if (write == TurnStateWriteResult.Unavailable)
                {
                    // ต่างจาก start-encounter (ที่เขียนล้มแล้วยังตอบ 200 ได้) โดยตั้งใจ — ที่นี่เขียนไม่ลง
                    // แล้วตอบ 200 = ผู้เล่นเชื่อว่าใช้ action ไปแล้วทั้งที่ระบบไม่ได้บันทึก (fail open
                    // ของ action economy) จึงต้องตอบ 503 ให้ผู้เล่นรู้ว่าต้องลองใหม่
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                if (write == TurnStateWriteResult.Written)
                {
                    var response = new SpendTurnResourceResponse(
                        encounter.Id, currentParticipant.Id, currentParticipant.CharacterId, request.Resource,
                        ToTurnStateResponse(newState));

                    try
                    {
                        await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync("TurnStateChanged", response);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to broadcast TurnStateChanged for encounter {EncounterId}", encounterId);
                    }

                    return Results.Ok(response);
                }

                // Conflict → มี request อื่นเขียนแทรกระหว่างที่เราอ่าน-คิด วนใหม่เพื่ออ่าน version ล่าสุด
            }

            // ชนกันจนครบจำนวนรอบที่ยอมให้ retry — ให้ client ลองใหม่แทนที่จะรอวนไม่มีที่สิ้นสุด
            return Results.Conflict();
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/encounters/active", async (
            Guid campaignId,
            Guid sessionId,
            DnDEzDbContext db,
            IEncounterTurnStateStore turnStateStore) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            var encounter = await db.Encounters
                .SingleOrDefaultAsync(e => e.SessionId == sessionId && e.CampaignId == campaignId && e.EndedAt == null);
            if (encounter is null)
            {
                return Results.NoContent();
            }

            var participants = await db.EncounterParticipants
                .Include(p => p.Character)
                .Where(p => p.EncounterId == encounter.Id)
                .OrderBy(p => p.TurnOrder)
                .ToListAsync();

            var charactersById = participants.ToDictionary(p => p.CharacterId, p => p.Character);

            // participants ข้างบน query ด้วย .Include(p => p.Character) ไว้แล้ว จึงอ่าน speed ได้เลย
            // ไม่ต้อง round trip เพิ่ม — ค่า fallback นี้ถูกใช้จริงเมื่อ Redis ไม่มีค่าให้อ่าน (เช่น Redis
            // หาย) ต้องได้งบเท่ากับที่เทิร์นนั้นควรมีตั้งแต่ต้น ไม่ใช่ 30 คงที่ทุกคน
            var state = (await turnStateStore.GetSnapshotAsync(
                encounter.Id,
                encounter.TurnNumber,
                ActionEconomyResolver.StartTurn(
                    CurrentParticipantMovementFeet(encounter.CurrentParticipantId, participants)))).State;

            var response = BuildEncounterResponse(encounter, participants, charactersById, state);
            return Results.Ok(response);
        }).RequireAuthorization();
    }

    private static EncounterResponse BuildEncounterResponse(
        Encounter encounter,
        IReadOnlyList<EncounterParticipant> participants,
        IReadOnlyDictionary<Guid, Character> charactersById,
        ActionEconomyState turnState)
    {
        var ordered = participants.OrderBy(p => p.TurnOrder).ToList();
        var participantResponses = ordered
            .Select(p => new EncounterParticipantResponse(
                p.Id,
                p.CharacterId,
                charactersById[p.CharacterId].Name,
                p.InitiativeRoll,
                p.InitiativeModifier,
                p.InitiativeTotal,
                p.TurnOrder))
            .ToList();

        var current = ordered.SingleOrDefault(p => p.Id == encounter.CurrentParticipantId);

        return new EncounterResponse(
            encounter.Id,
            encounter.CampaignId,
            encounter.SessionId,
            encounter.StartedAt,
            encounter.EndedAt,
            encounter.CurrentParticipantId,
            current?.CharacterId,
            encounter.TurnNumber,
            participantResponses,
            ToTurnStateResponse(turnState));
    }

    /// <summary>
    /// ความเร็วของผู้เข้าร่วมที่ตรงกับ <paramref name="currentParticipantId"/> ใน
    /// <paramref name="participants"/> (ต้องผ่าน <c>.Include(p =&gt; p.Character)</c> มาก่อนแล้ว —
    /// ฟังก์ชันนี้ไม่ query เพิ่มเอง เพื่อไม่ให้เกิด lazy load/round trip ซ้ำ) ใช้เป็นค่า fallback
    /// ตอน turn state store (Redis) ไม่มีค่าให้อ่าน (เช่น Redis หาย) — ต้องสร้างงบเดียวกับที่เทิร์นนั้น
    /// ควรจะมีตั้งแต่ต้น ไม่ใช่ 30 คงที่ทุกคน (Sprint G Part 8)
    ///
    /// คืน <see cref="FallbackMovementFeet"/> เมื่อ <paramref name="currentParticipantId"/> เป็น null
    /// หรือไม่ตรงกับใครใน <paramref name="participants"/> เลย (เช่น encounter กำลังถูก teardown
    /// กลางอากาศ) — endpoint ต้องไม่ 500 เพราะเหตุนี้
    /// </summary>
    private static int CurrentParticipantMovementFeet(
        Guid? currentParticipantId, IEnumerable<EncounterParticipant> participants)
    {
        if (currentParticipantId is null)
        {
            return FallbackMovementFeet;
        }

        foreach (var participant in participants)
        {
            if (participant.Id == currentParticipantId)
            {
                return participant.Character.SpeedFeet;
            }
        }

        return FallbackMovementFeet;
    }

    private static TurnStateResponse ToTurnStateResponse(ActionEconomyState state) => new(
        state.ActionAvailable,
        state.BonusActionAvailable,
        state.ReactionAvailable,
        state.MovementRemainingFeet,
        state.MaxMovementFeet);

    /// <summary>
    /// Sprint G Part 5 — flush ตำแหน่ง token ที่ยัง pending ของ scene ที่ active ของ session นี้
    /// (ถ้ามี) เข้า Postgres จริง เรียกจาก next-turn/end (design doc §14.3) — best-effort เหมือน
    /// broadcast: session อาจไม่มี scene เลย (ยังไม่ถึง Sprint G Part 4 ของโต๊ะนั้น) ซึ่งไม่ใช่ error
    /// </summary>
    private static async Task FlushSceneTokensForSessionAsync(
        DnDEzDbContext db, SceneTokenFlusher tokenFlusher, Guid sessionId, ILogger logger)
    {
        try
        {
            var activeSceneId = await db.Scenes
                .Where(s => s.SessionId == sessionId && s.IsActive)
                .Select(s => (Guid?)s.Id)
                .SingleOrDefaultAsync();
            if (activeSceneId is not null)
            {
                await tokenFlusher.FlushAsync(activeSceneId.Value);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "flush token position ของ session {SessionId} ไม่สำเร็จตอนเปลี่ยนเทิร์น/จบ encounter", sessionId);
        }
    }
}

// ── response contracts ──────────────────────────────────────────────────────
// ชื่อ property ต้องตรงเป๊ะกับ record ที่ประกาศไว้ท้าย EncounterEndpointsTests.cs

internal record TurnStateResponse(
    bool ActionAvailable,
    bool BonusActionAvailable,
    bool ReactionAvailable,
    int MovementRemainingFeet,
    int MaxMovementFeet);

internal record EncounterParticipantResponse(
    Guid Id,
    Guid CharacterId,
    string CharacterName,
    int InitiativeRoll,
    int InitiativeModifier,
    int InitiativeTotal,
    int TurnOrder);

internal record EncounterResponse(
    Guid Id,
    Guid CampaignId,
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    Guid? CurrentParticipantId,
    Guid? CurrentCharacterId,
    /// <summary>
    /// นับรวมทุกเทิร์นที่ผ่านไปตั้งแต่เปิด encounter (เริ่มที่ 0, +1 ทุกครั้งที่ next-turn) —
    /// ไม่ใช่ "รอบ" ของ D&D · UI คำนวณรอบเองด้วย TurnNumber / จำนวนผู้ร่วมต่อสู้
    ///
    /// เก็บใน `encounters.turn_number` มาตั้งแต่งานที่ 4 (ใช้เป็นส่วนหนึ่งของ Redis key ของ
    /// turn state) แต่ไม่เคยส่งออก API จนถึง defect รอบ B — UI จึงแสดง "รอบที่เท่าไร" ไม่ได้เลย
    /// </summary>
    int TurnNumber,
    IReadOnlyList<EncounterParticipantResponse> Participants,
    TurnStateResponse? TurnState);

internal record NextTurnResponse(
    Guid EncounterId,
    Guid CurrentParticipantId,
    Guid CurrentCharacterId,
    int TurnOrder,
    TurnStateResponse TurnState);

internal record SpendTurnResourceResponse(
    Guid EncounterId,
    Guid ParticipantId,
    Guid CharacterId,
    string Resource,
    TurnStateResponse TurnState);

internal record EndEncounterResponse(Guid EncounterId, DateTimeOffset EndedAt);

internal record CreateEncounterRequest(List<Guid>? CharacterIds);

internal record SpendTurnResourceRequest(string? Resource, int? Feet);
