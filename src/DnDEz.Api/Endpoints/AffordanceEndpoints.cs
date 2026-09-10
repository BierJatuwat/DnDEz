using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Encounters;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint G Part 9 — "ตาฉันแล้ว ทำอะไรได้บ้าง" คำนวณที่ server ต่อผู้ดูแต่ละคน
/// (spatial-play-design §1.2–§1.4)
///
/// **ไม่ใช่ฟีเจอร์ AI** — §1.3 แยกเรื่องนี้ออกจาก "AI แนะนำ action" ของ player-side-design §7
/// ชัดเจน: เดินได้ถึงไหน เอื้อมถึงใคร ยิงตอนประชิดแล้วเสียเปรียบไหม ล้วนมีคำตอบตายตัว คำนวณได้
/// เขียนเทสต์ได้ ต้นทุน token = ศูนย์ และใช้ได้กับทุกคนตลอดเวลาโดยไม่ต้องมี API key ของตัวเอง
/// ไฟล์นี้จึงไม่แตะ orchestrator/LLM เลยแม้แต่บรรทัดเดียว
///
/// ## ลำดับการทำงานที่ห้ามสลับ (§1.2)
/// **กรองหมอกก่อน แล้วค่อยคำนวณ** ไม่ใช่คำนวณจากความจริงทั้งใบแล้วค่อยกรองตอนส่ง —
/// *"รายการ 'สิ่งที่ทำได้' เป็นช่องรั่วของข้อมูลในตัวมันเอง ถ้าพาเนลบอกว่า 'โจมตีก็อบลินได้'
/// ผู้เล่นก็รู้แล้วว่ามีก็อบลินอยู่"* · ของที่รั่วได้คราวนี้คือ **"มีอยู่ไหม"** ซึ่งแรงกว่า
/// **"เลขเท่าไร"** ที่ Sprint F งานที่ 7 เคยพลาด
///
/// <see cref="AffordanceResolver.Resolve"/> รับได้แค่สิ่งที่เราส่งเข้าไป (ไม่มี DbContext ไม่มี
/// สถานะโลกทั้งใบ) จึง **ไม่มีทางพูดถึงสิ่งที่มองไม่เห็นได้เลยเชิงโครงสร้าง** — ความปลอดภัยตรงนี้
/// มาจากรูปร่างของ API ไม่ได้มาจากวินัยของคนเขียน
/// </summary>
public static class AffordanceEndpoints
{
    /// <summary>หนึ่งช่องบนกริด = 5 ฟุต ตาม SRD 5.2 — เลนส์เดียวกับ Part 8</summary>
    private const int FeetPerSquare = 5;

    public static void MapAffordanceEndpoints(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/affordances/{characterId:guid}", async (
            Guid campaignId,
            Guid sessionId,
            Guid characterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            ISceneTokenStore tokenStore,
            IEncounterTurnStateStore turnStateStore) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // ไม่ใช่สมาชิก → RLS กรองแคมเปญทิ้ง → 404 (ไม่ใช่ 403 เพราะ 403 เท่ากับยืนยันว่า
            // แคมเปญนี้มีอยู่จริง ซึ่งเป็นการรั่วอีกแบบ) แพทเทิร์นเดียวกับทั้งโปรเจกต์
            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var session = await db.Sessions
                .SingleOrDefaultAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (session is null)
            {
                return Results.NotFound();
            }

            // สิทธิ์: เจ้าของตัวละครเล่นเอง หรือ DM คุม NPC ที่ ControlledByDm — ใช้ helper เดิม
            // ไม่เขียนเช็คซ้ำ (งานที่ 3 ของ Sprint F ทำไว้แล้ว)
            var (actor, authFailure) = await CampaignAuthorization.RequireCharacterActorAsync(
                db, characterId, campaignId, campaign, callerUserId.Value);
            if (authFailure is not null)
            {
                return authFailure;
            }

            var isDm = campaign.OwnerUserId == callerUserId.Value;

            var scene = await db.Scenes.SingleOrDefaultAsync(s => s.SessionId == sessionId && s.IsActive);
            if (scene is null)
            {
                // ยังไม่มีฉาก = ยังไม่มีอะไรเชิงพื้นที่ให้ทำ ซึ่งเป็นคำตอบที่ถูกต้อง ไม่ใช่ 404
                // (404 ตรงนี้จะแยกไม่ออกจาก "route ผิด")
                return Results.Ok(new AffordanceResponse(characterId, []));
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

            // ⚠️ token ของตัวเองหาจากรายการ **ที่ยังไม่กรองหมอก** โดยตั้งใจ — คนเราย่อมรู้ว่าตัวเอง
            // ยืนอยู่ตรงไหนเสมอ และการบอกตำแหน่งตัวเองให้เจ้าตัวฟังไม่ใช่การรั่วข้อมูลให้ใคร
            // ถ้ากรองด้วย ผู้เล่นที่ยืนอยู่ในพื้นที่ที่ยังไม่เปิดหมอกจะหา token ตัวเองไม่เจอ
            var actorToken = tokens.FirstOrDefault(
                t => t.EntityType == "Character" && t.EntityId == characterId);
            if (actorToken is null)
            {
                // ตัวละครยังไม่ถูกวางลงแผนที่ — ยังไม่มีระยะให้คิด จึงยังไม่มีตัวเลือกเชิงพื้นที่
                return Results.Ok(new AffordanceResponse(characterId, []));
            }

            var fogAreas = FogLayerSerialization.ParseRevealedAreas(scene.FogLayer);

            // ── กรองหมอกก่อน (ขั้นตอนที่ห้ามสลับ) ──
            var visibleTokens = tokens
                .Where(t => t.Id != actorToken.Id)
                .Where(t => FogLayerSerialization.IsVisibleTo(t.X, t.Y, fogAreas, isDm))
                .ToList();

            var visibleTargets = await BuildVisibleTargetsAsync(db, actor!, actorToken, visibleTokens);
            var options = await BuildOptionsAsync(db, actor!);
            var economy = await GetEconomyAsync(db, turnStateStore, sessionId, actor!);

            var affordances = AffordanceResolver.Resolve(economy, options, visibleTargets);

            // ราคาและระยะติดไปกับผลลัพธ์ด้วย — การ์ดในหน้าจอผู้เล่น (mockup uxpilot-player-view.png)
            // แสดง "1 Action"/"Bonus Action" มุมขวาบนของทุกใบ และระยะช่วยให้เห็นว่าตัวเลือกผูกกับ
            // ตำแหน่งบนแผนที่จริง · ทั้งสองค่ามาจากของที่คำนวณไปแล้วข้างบน ไม่ได้ query เพิ่ม และไม่ใช่
            // ข้อมูลลับ (ราคาเป็นของท่าตัวเอง ระยะเป็นของเป้าที่ผู้ดูมองเห็นอยู่แล้ว)
            var costByOptionId = options.ToDictionary(o => o.Id, o => o.Cost);
            var distanceByTargetId = visibleTargets.ToDictionary(t => t.Id, t => t.DistanceFeet);

            return Results.Ok(new AffordanceResponse(
                characterId,
                affordances.Select(a => new AffordanceItemResponse(
                    a.OptionId,
                    a.OptionName,
                    a.TargetId,
                    a.TargetName,
                    a.AdvantageMode.ToString(),
                    costByOptionId[a.OptionId].ToString(),
                    a.TargetId is not null && distanceByTargetId.TryGetValue(a.TargetId.Value, out var feet)
                        ? feet
                        : null,
                    a.Notes.Select(n => new AffordanceNoteResponse(n.Code.ToString(), n.Message)).ToList()))
                    .ToList()));
        }).RequireAuthorization();
    }

    /// <summary>
    /// แปลง token ที่ผู้ดูมองเห็นแล้วให้เป็นเป้าที่ engine กินได้
    ///
    /// token ที่ผูกกับ Monster ข้ามไปก่อนในรอบนี้ — <c>monster_stat_blocks</c> เป็นตารางแยกที่ยังไม่มี
    /// การ join เข้ามาที่ไหนเลย (§2.4 บอกว่าสุดท้ายต้องรวมเป็นคลังเดียวกัน แต่นั่นเป็นงานคนละรอบ)
    /// ไม่เดา join เองที่นี่
    /// </summary>
    private static async Task<List<AffordanceTarget>> BuildVisibleTargetsAsync(
        DnDEzDbContext db, Character actor, SceneTokenRecord actorToken, List<SceneTokenRecord> visibleTokens)
    {
        var characterIds = visibleTokens
            .Where(t => t.EntityType == "Character")
            .Select(t => t.EntityId)
            .Distinct()
            .ToList();

        if (characterIds.Count == 0)
        {
            return [];
        }

        var characters = await db.Characters
            .Where(c => characterIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        var targets = new List<AffordanceTarget>();
        foreach (var token in visibleTokens)
        {
            if (token.EntityType != "Character" || !characters.TryGetValue(token.EntityId, out var character))
            {
                continue;
            }

            targets.Add(new AffordanceTarget(
                Id: character.Id,
                Name: character.Name,
                // โมเดลฝ่ายอย่างง่ายในรอบนี้: ยังไม่มีตาราง faction/พันธมิตร จึงใช้แกน "ใครคุม" แทน —
                // ตัวละครผู้เล่นมองว่า NPC ที่ DM คุมเป็นศัตรู และ NPC ก็มองผู้เล่นเป็นศัตรูแบบสมมาตร
                IsEnemy: character.ControlledByDm != actor.ControlledByDm,
                DistanceFeet: DistanceInFeet(actorToken, token)));
        }

        return targets;
    }

    /// <summary>
    /// ระยะระหว่างสอง token เป็นฟุต — Chebyshev (นับช่อง ทแยงเท่าตรง) × 5
    /// SRD 5.2: *"To determine the range on a grid between two things ... count squares ...
    /// Count by the shortest route"* และหนึ่งช่อง = 5 ฟุต · เลนส์เดียวกับ Part 8
    ///
    /// ⚠️ พิกัด token เป็น <c>double</c> ที่ Part 5 **ไม่เคยนิยามหน่วยไว้** — ที่นี่ตีความว่าเป็น
    /// "ช่อง" ซึ่งสอดคล้องกับ fixture ที่มีอยู่ (พิกัดเลขน้อย ๆ, รัศมีหมอกหลักหน่วย) และกับกริดของ
    /// Part 8 · **จงใจรวมการแปลงหน่วยไว้ที่ฟังก์ชันเดียว** ถ้าวันหนึ่งตกลงกันว่าพิกัดเป็นพิกเซล
    /// ให้แก้ที่นี่ที่เดียว ไม่ต้องไล่แก้ทั้ง endpoint
    /// </summary>
    private static int DistanceInFeet(SceneTokenRecord from, SceneTokenRecord to)
    {
        var dx = Math.Abs((int)Math.Round(to.X) - (int)Math.Round(from.X));
        var dy = Math.Abs((int)Math.Round(to.Y) - (int)Math.Round(from.Y));
        return Math.Max(dx, dy) * FeetPerSquare;
    }

    /// <summary>
    /// ท่าที่ตัวละครนี้มีจริง อ่านจาก <c>sheet.actionCatalogRefs</c> (Part 7 เขียนไว้ตอนสร้างจาก preset)
    ///
    /// parse แบบไม่ไว้ใจข้อมูลเลย — <c>sheet</c> เป็น jsonb อิสระ ตัวละครเก่าที่สร้างก่อน Part 7
    /// ไม่มีคีย์นี้ และอาจเป็น null/ไม่ใช่ array/guid พัง ทุกกรณีต้องได้ลิสต์ว่าง ไม่ใช่ 500
    /// </summary>
    private static async Task<List<AffordanceOption>> BuildOptionsAsync(DnDEzDbContext db, Character actor)
    {
        var refs = ParseCatalogRefs(actor.Sheet);
        if (refs.Count == 0)
        {
            return [];
        }

        var entries = await db.ActionCatalogEntries
            .Where(e => refs.Contains(e.Id))
            .ToListAsync();

        return entries.Select(e => new AffordanceOption(
            Id: e.Id,
            Name: e.Name,
            Cost: MapCost(e.ActionCost),
            RequiresAttackRoll: e.RollKind == ActionRollKind.AttackRoll,
            TargetsEnemy: e.TargetKind == ActionTargetKind.Enemy,
            NormalRangeFeet: e.RangeNormalFeet,
            LongRangeFeet: e.RangeLongFeet)).ToList();
    }

    private static List<Guid> ParseCatalogRefs(string sheetJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(sheetJson) ? "{}" : sheetJson);

            // ต้องเช็คว่า root เป็น object ก่อนเสมอ — JsonElement.TryGetProperty **โยน**
            // InvalidOperationException ถ้า root เป็น array/สตริง/ตัวเลข ไม่ได้คืน false
            // ทุกวันนี้ยังไม่มี endpoint ไหนให้ผู้ใช้เขียน sheet ดิบ ๆ ได้ (server เขียนเองทุกจุด)
            // แต่คอลัมน์เป็น jsonb อิสระที่ไม่มีอะไรบังคับรูปร่าง — pipeline นำเข้าหรือ endpoint
            // ในอนาคตเขียน array ลงไปเมื่อไหร่ endpoint นี้จะ 500 ทันที จึงกันไว้ที่ขอบเขตความเชื่อถือ
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (!doc.RootElement.TryGetProperty("actionCatalogRefs", out var refs)
                || refs.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<Guid>();
            foreach (var item in refs.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id))
                {
                    result.Add(id);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// แปลงราคาจาก enum ของ Domain ไปเป็น enum ของ Rules — เขียนเป็น switch ชัด ๆ ไม่ cast ตัวเลข
    /// เพราะสอง enum นี้เป็นคนละ type โดยตั้งใจ (<c>DnDEz.Rules</c> เป็น zero-dependency ห้าม
    /// reference Domain) ถ้าวันหนึ่งมีใครเพิ่มค่าใหม่ข้างใดข้างหนึ่ง คอมไพเลอร์จะไม่เตือนถ้า cast
    /// ตัวเลข แต่ switch นี้จะพังให้เห็นทันที
    /// </summary>
    private static ActionResourceCost MapCost(ActionCostKind cost) => cost switch
    {
        ActionCostKind.Action => ActionResourceCost.Action,
        ActionCostKind.BonusAction => ActionResourceCost.BonusAction,
        ActionCostKind.Reaction => ActionResourceCost.Reaction,
        ActionCostKind.None => ActionResourceCost.None,
        _ => throw new ArgumentOutOfRangeException(nameof(cost), cost, "ราคา action ที่ไม่รู้จัก")
    };

    /// <summary>
    /// ทรัพยากรที่เหลือของเทิร์นนี้ — ถ้ามี encounter ที่ยัง active อยู่ใช้ค่าจริงจาก turn state store
    /// (Redis) ถ้าไม่มีการต่อสู้อยู่เลยถือว่าเต็มทุกอย่าง เพราะนอกการต่อสู้ยังไม่มีอะไรถูกใช้ไป
    ///
    /// งบเคลื่อนที่ fallback อ่านจาก speed จริงของตัวละคร (Part 8 เลิก hardcode 30 ไปแล้ว)
    /// </summary>
    private static async Task<ActionEconomyState> GetEconomyAsync(
        DnDEzDbContext db, IEncounterTurnStateStore turnStateStore, Guid sessionId, Character actor)
    {
        var encounter = await db.Encounters
            .SingleOrDefaultAsync(e => e.SessionId == sessionId && e.EndedAt == null);
        if (encounter is null)
        {
            return ActionEconomyResolver.StartTurn(actor.SpeedFeet);
        }

        var fallback = ActionEconomyResolver.StartTurn(actor.SpeedFeet);
        return (await turnStateStore.GetSnapshotAsync(encounter.Id, encounter.TurnNumber, fallback)).State;
    }
}

internal record AffordanceNoteResponse(string Code, string Message);

internal record AffordanceItemResponse(
    Guid OptionId,
    string OptionName,
    Guid? TargetId,
    string? TargetName,
    string AdvantageMode,
    string Cost,
    int? DistanceFeet,
    List<AffordanceNoteResponse> Notes);

internal record AffordanceResponse(Guid CharacterId, List<AffordanceItemResponse> Options);
