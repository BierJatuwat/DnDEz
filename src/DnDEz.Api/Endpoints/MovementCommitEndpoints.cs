using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint G Part 10 (ส่วน ค) — เดินตามเส้นทางจริง พร้อมกลไก interrupt ของ opportunity attack
///
/// **นี่คือจุดที่ dispatch เรียกว่ายากที่สุด**: turn state เดิมเดินหน้าอย่างเดียว (DM กด "เทิร์นถัดไป")
/// ไม่เคยมีอะไรหยุดค้างกลางทางแล้วรอ input จากคน**อื่น**ที่ไม่ใช่เจ้าของเทิร์นมาก่อน
///
/// ## วงจรชีวิตของการเดินหนึ่งครั้ง
/// <code>
/// commit ──▶ ไม่มีใครได้สวน ──────────────────▶ Completed (token อยู่ปลายทาง)
///        └▶ มีคนได้สวน ──▶ AwaitingReaction ──┬▶ respond(สวน/ไม่สวน) ──▶ เดินต่อ
///                          (token ค้างที่ช่อง  ├▶ หมดเวลา → sweeper ปิดให้ ──▶ เดินต่อ
///                           ก่อนหลุดระยะ)      └▶ Redis ล่ม/คีย์หาย ─────────▶ ค้างที่ช่องนั้น
/// </code>
///
/// สามทางล่างจบเหมือนกันหมดคือ "ไม่มี OA แล้วเดินต่อ" ตาม player-side-design §4 จึงไม่มี failure
/// path ใหม่ให้ออกแบบ — ดู doc comment ของ <see cref="IPendingReactionStore"/>
///
/// ## ขอบเขตที่ตั้งใจไม่ทำในรอบนี้
/// **การทอยลูกเต๋าของ OA ไม่ได้อยู่ที่นี่** — endpoint นี้ตัดสินแค่ "หยุดไหม ใครได้สิทธิ์ เดินต่อเมื่อไร"
/// ส่วนการทอยจริงใช้ทางเดิมที่มีอยู่แล้ว (<c>POST .../characters/{id}/attacks</c>) ซึ่งคุม dice mode,
/// DM-entered roll, damage type, การหัก HP และ broadcast ไว้ครบและมีเทสต์แล้ว · ถ้าทอยเองที่นี่จะ
/// กลายเป็นเส้นทางโจมตีที่สองที่วันหนึ่งจะเพี้ยนจากของเดิม
///
/// **การหักสิทธิ์ reaction ของผู้สวนยังทำไม่ได้** — turn state ที่มีเก็บทรัพยากรของเจ้าของเทิร์น
/// ปัจจุบันคนเดียว ไม่มีที่เก็บ "ก็อบลินใช้ reaction ไปแล้วในรอบนี้" · บันทึกไว้เป็นช่องว่างที่รู้ตัว
/// ไม่ใช่แกล้งทำเป็นหักแล้ว
/// </summary>
public static class MovementCommitEndpoints
{
    public static void MapMovementCommitEndpoints(this WebApplication app)
    {
        // ── เริ่มเดิน ──────────────────────────────────────────────────────────
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/movement/commit", async (
            Guid campaignId,
            Guid sessionId,
            MovementCommitRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            ISceneTokenStore tokenStore,
            IPendingReactionStore reactionStore,
            PendingReactionSweeper sweeper,
            ILoggerFactory loggerFactory) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (request.Path is null || request.Path.Count == 0)
            {
                return Results.BadRequest();
            }

            var context = await MovementSceneContext.LoadAsync(
                db, tokenStore, campaignId, sessionId, request.CharacterId, callerUserId.Value);
            if (context.Failure is not null)
            {
                return context.Failure;
            }

            var path = request.Path
                .Select(p => new GridPoint((int)Math.Round(p.X), (int)Math.Round(p.Y)))
                .ToList();

            PathCost cost;
            IReadOnlyList<OpportunityAttackTrigger> triggers;
            try
            {
                cost = MovementCostResolver.CostOfPath(
                    path, MovementMode.Walk, context.Terrain,
                    new MovementProfile(BudgetFeet: context.Actor!.SpeedFeet));

                triggers = OpportunityAttackResolver.Detect(path, context.Threats, request.Disengage);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }

            // จ่ายไม่ไหว = ไม่ขยับเลยแม้แต่ช่องเดียว (ไม่เดินไปครึ่งทางแล้วค้าง ซึ่งจะทำให้ผู้เล่น
            // เสียตำแหน่งไปโดยไม่ได้อะไรกลับมา)
            if (!cost.IsAffordable)
            {
                return Results.BadRequest();
            }

            var destination = triggers.Count == 0
                ? path[^1]
                : triggers[0].AttackFromSquare;

            await sweeper.MoveTokenAsync(
                campaignId, context.Scene!.Id, context.ActorToken!.Id, callerUserId.Value,
                destination.X, destination.Y, CancellationToken.None);

            if (triggers.Count == 0)
            {
                return Results.Ok(MovementCommitResponse.Completed(request.CharacterId, cost.TotalFeet, false));
            }

            var timeoutSeconds = context.Campaign!.ReactionTimeoutSeconds;
            var pending = new PendingReaction(
                Id: Guid.NewGuid(),
                CampaignId: campaignId,
                SessionId: sessionId,
                SceneId: context.Scene.Id,
                TokenId: context.ActorToken.Id,
                MoverCharacterId: request.CharacterId,
                ActingUserId: callerUserId.Value,
                Path: request.Path.Select(p => new PendingPathPoint(p.X, p.Y)).ToList(),
                CostFeet: cost.TotalFeet,
                Triggers: triggers.Select(t => new PendingReactionTrigger(
                    t.ThreatId,
                    t.ThreatName,
                    context.ThreatUserIds[t.ThreatId],
                    t.LeavesAtStepIndex,
                    t.AttackFromSquare.X,
                    t.AttackFromSquare.Y)).ToList(),
                CurrentTriggerIndex: 0,
                TimeoutSeconds: timeoutSeconds,
                DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds));

            // เก็บสถานะไม่ได้ = จะไม่มีใครมาปลุกให้เดินต่อ → เดินให้จบไปเลยดีกว่าค้างถาวร
            // (เสีย OA ไปหนึ่งครั้ง ดีกว่าเกมค้างจนต้องรีเฟรช)
            if (!await reactionStore.SaveAsync(pending))
            {
                loggerFactory.CreateLogger(typeof(MovementCommitEndpoints)).LogWarning(
                    "เก็บ pending reaction ไม่ได้ — เดินให้จบเส้นทางแทนการค้างรอ ({CharacterId})",
                    request.CharacterId);

                await sweeper.MoveTokenAsync(
                    campaignId, context.Scene.Id, context.ActorToken.Id, callerUserId.Value,
                    path[^1].X, path[^1].Y, CancellationToken.None);

                return Results.Ok(MovementCommitResponse.Completed(request.CharacterId, cost.TotalFeet, false));
            }

            await sweeper.NotifyThreatHolderAsync(pending, CancellationToken.None);

            var first = pending.CurrentTrigger;
            return Results.Ok(new MovementCommitResponse(
                request.CharacterId, "AwaitingReaction", cost.TotalFeet,
                pending.Id, first.ThreatCharacterId, first.ThreatName, timeoutSeconds, false));
        }).RequireAuthorization();

        // ── ผู้ถือสิทธิ์ตอบ ───────────────────────────────────────────────────
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/movement/reactions/{reactionId:guid}/respond", async (
            Guid campaignId,
            Guid sessionId,
            Guid reactionId,
            ReactionRespondRequest request,
            ClaimsPrincipal principal,
            IPendingReactionStore reactionStore,
            PendingReactionSweeper sweeper) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var pending = await reactionStore.GetAsync(reactionId);
            // ไม่มีแล้ว = ตอบไปแล้ว/หมดเวลาไปแล้ว/Redis ลืม — ทั้งหมดคือ "ไม่มีอะไรให้ตอบ" = 404
            if (pending is null || pending.CampaignId != campaignId || pending.SessionId != sessionId)
            {
                return Results.NotFound();
            }

            // เฉพาะเจ้าของสิทธิ์เท่านั้น — คนที่กำลังถูกสวนจะกดแทนไม่ได้
            if (pending.CurrentTrigger.ThreatUserId != callerUserId.Value)
            {
                return Results.Forbid();
            }

            var outcome = await sweeper.ResumeAsync(pending, request.UseReaction, CancellationToken.None);

            return Results.Ok(outcome.Next is null
                ? MovementCommitResponse.Completed(
                    pending.MoverCharacterId, pending.CostFeet, request.UseReaction)
                : new MovementCommitResponse(
                    pending.MoverCharacterId, "AwaitingReaction", pending.CostFeet,
                    outcome.Next.Id, outcome.Next.CurrentTrigger.ThreatCharacterId,
                    outcome.Next.CurrentTrigger.ThreatName, outcome.Next.TimeoutSeconds,
                    request.UseReaction));
        }).RequireAuthorization();
    }
}

/// <summary>
/// สิ่งที่ทั้ง preview และ commit ต้องโหลดเหมือนกันเป๊ะ — โหลดที่เดียว
///
/// **สำคัญที่สุดคือกฎ "ใครนับเป็นภัยคุกคาม"** ต้องเหมือนกันทั้งสอง endpoint ไม่งั้นผู้เล่นจะเห็น
/// คำเตือนอย่างหนึ่งตอน preview แล้วเจออีกอย่างตอนกดจริง ซึ่งทำลายความเชื่อถือของพาเนลทั้งหมด
/// </summary>
internal sealed class MovementSceneContext
{
    public IResult? Failure { get; private init; }
    public Campaign? Campaign { get; private init; }
    public Character? Actor { get; private init; }
    public Scene? Scene { get; private init; }
    public SceneTokenRecord? ActorToken { get; private init; }
    public List<ReachThreat> Threats { get; private init; } = [];
    public TerrainField Terrain { get; private init; } = TerrainField.Empty;

    /// <summary>ผู้ใช้ที่ถือสิทธิ์ reaction ของแต่ละภัยคุกคาม — เจ้าของตัวละคร หรือ DM ถ้าเป็น NPC</summary>
    public Dictionary<Guid, Guid> ThreatUserIds { get; private init; } = [];

    /// <summary>
    /// ระยะเอื้อมมาตรฐาน 5 ฟุต (SRD 5.2 "Reach": *"A creature has a 5-foot reach"*)
    ///
    /// มอนสเตอร์เอื้อมยาวเท่าไรอ่านจาก <c>monster_stat_blocks.reach_feet</c> ของตัวเอง (Sprint H Part 7)
    /// ค่านี้ (5 ฟุตตายตัว) ใช้เฉพาะตัวละคร (Character) ที่ยังไม่มีคอลัมน์ reach ในสคีมา
    /// </summary>
    private const int DefaultReachFeet = 5;

    public static async Task<MovementSceneContext> LoadAsync(
        DnDEzDbContext db, ISceneTokenStore tokenStore,
        Guid campaignId, Guid sessionId, Guid characterId, Guid callerUserId)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
        if (campaign is null)
        {
            return new MovementSceneContext { Failure = Results.NotFound() };
        }

        var session = await db.Sessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
        if (session is null)
        {
            return new MovementSceneContext { Failure = Results.NotFound() };
        }

        var (actor, authFailure) = await CampaignAuthorization.RequireCharacterActorAsync(
            db, characterId, campaignId, campaign, callerUserId);
        if (authFailure is not null)
        {
            return new MovementSceneContext { Failure = authFailure };
        }

        var scene = await db.Scenes.SingleOrDefaultAsync(s => s.SessionId == sessionId && s.IsActive);
        if (scene is null)
        {
            return new MovementSceneContext { Failure = Results.NotFound() };
        }

        var tokens = SceneTokenSerialization.Parse(scene.Tokens);
        var pending = await tokenStore.GetPendingPositionsAsync(scene.Id);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (pending.TryGetValue(tokens[i].Id, out var pos))
            {
                tokens[i] = tokens[i] with { X = pos.X, Y = pos.Y };
            }
        }

        // token ของตัวเองหาจากรายการที่ยังไม่กรองหมอก — คนเราย่อมรู้ว่าตัวเองอยู่ตรงไหนเสมอ
        var actorToken = tokens.FirstOrDefault(
            t => t.EntityType == "Character" && t.EntityId == characterId);
        if (actorToken is null)
        {
            return new MovementSceneContext { Failure = Results.NotFound() };
        }

        var isDm = campaign.OwnerUserId == callerUserId;
        var fogAreas = FogLayerSerialization.ParseRevealedAreas(scene.FogLayer);

        // เตือนเฉพาะศัตรูที่ผู้เดินมองเห็นแล้ว — ถ้าเตือนถึงตัวที่ยังอยู่ในหมอกก็เท่ากับบอกว่ามีมันอยู่
        // (ช่องรั่วแบบเดียวกับที่ §1.2 เตือนไว้) · ผลข้างเคียงที่ตั้งใจ: นักซุ่มโจมตีไม่โผล่ในคำเตือน
        // รับทั้ง Character และ Monster — มอนสเตอร์ก็เป็นภัยคุกคามที่ตั้งเอื้อมได้เหมือนกัน (Part 7)
        var visibleEnemyTokens = tokens
            .Where(t => (t.EntityType == "Character" || t.EntityType == "Monster") && t.EntityId != characterId)
            .Where(t => FogLayerSerialization.IsVisibleTo(t.X, t.Y, fogAreas, isDm))
            .ToList();

        var ids = visibleEnemyTokens.Where(t => t.EntityType == "Character")
            .Select(t => t.EntityId).Distinct().ToList();
        var characters = ids.Count == 0
            ? []
            : await db.Characters.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        var monsterIds = visibleEnemyTokens.Where(t => t.EntityType == "Monster")
            .Select(t => t.EntityId).Distinct().ToList();
        var monsters = monsterIds.Count == 0
            ? []
            : await db.MonsterStatBlocks.Where(m => monsterIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        var threats = new List<ReachThreat>();
        var threatUserIds = new Dictionary<Guid, Guid>();
        foreach (var token in visibleEnemyTokens)
        {
            if (token.EntityType == "Character")
            {
                if (!characters.TryGetValue(token.EntityId, out var character))
                {
                    continue;
                }

                // ฝ่าย: ใช้แกน "ใครคุม" เหมือน Part 9 (ยังไม่มีตาราง faction)
                if (character.ControlledByDm == actor!.ControlledByDm)
                {
                    continue;
                }

                threats.Add(new ReachThreat(
                    Id: character.Id,
                    Name: character.Name,
                    Position: new GridPoint((int)Math.Round(token.X), (int)Math.Round(token.Y)),
                    ReachFeet: DefaultReachFeet,
                    // หมอกของ Part 6 เป็นพื้นที่ที่เปิดร่วมกันทั้งโต๊ะ การมองเห็นจึงสมมาตร —
                    // ถ้าวันหนึ่งมี line-of-sight ต่อตัวจริง ๆ ต้องกลับมาแก้ตรงนี้
                    CanSeeMover: true,
                    // ยังไม่มีที่เก็บ reaction ของตัวที่ไม่ใช่เจ้าของเทิร์น → เตือนเกินดีกว่าเตือนขาด
                    ReactionAvailable: true));

                threatUserIds[character.Id] = character.OwnerUserId ?? campaign.OwnerUserId;
            }
            else if (token.EntityType == "Monster")
            {
                if (!monsters.TryGetValue(token.EntityId, out var monster))
                {
                    continue;
                }

                // ฝ่าย: มอนสเตอร์คุมโดย DM เสมอ — เป็นภัยคุกคามก็ต่อเมื่อผู้เดินไม่ใช่ฝ่าย DM
                if (actor!.ControlledByDm)
                {
                    continue;
                }

                threats.Add(new ReachThreat(
                    Id: monster.Id,
                    Name: monster.Name,
                    Position: new GridPoint((int)Math.Round(token.X), (int)Math.Round(token.Y)),
                    ReachFeet: monster.ReachFeet,
                    CanSeeMover: true,
                    ReactionAvailable: true));

                // มอนสเตอร์ไม่มีเจ้าของผู้เล่น — DM ถือสิทธิ์ reaction แทน
                threatUserIds[monster.Id] = campaign.OwnerUserId;
            }
        }

        return new MovementSceneContext
        {
            Campaign = campaign,
            Actor = actor,
            Scene = scene,
            ActorToken = actorToken,
            Threats = threats,
            ThreatUserIds = threatUserIds,
            Terrain = SceneTerrainSerialization.ToTerrainField(scene.Terrain),
        };
    }
}

internal record MovementCommitRequest(
    Guid CharacterId,
    List<MovementPreviewPoint>? Path,
    bool Disengage = false);

internal record ReactionRespondRequest(bool UseReaction);

internal record MovementCommitResponse(
    Guid CharacterId,
    string Status,
    int CostFeet,
    Guid? PendingReactionId,
    Guid? AwaitingThreatId,
    string? AwaitingThreatName,
    int? ReactionTimeoutSeconds,
    bool ReactionUsed)
{
    public static MovementCommitResponse Completed(Guid characterId, int costFeet, bool reactionUsed) =>
        new(characterId, "Completed", costFeet, null, null, null, null, reactionUsed);
}
