using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint G Part 10 — "ถ้าเดินเส้นทางนี้จะเกิดอะไรขึ้นบ้าง" คิดให้ครบ **ก่อน** ผู้เล่นกด
///
/// spatial-play-design §3.4 กำหนดไว้ตรง ๆ ว่าระบบต้องบอกก่อนกด:
/// *"ทางนี้ใช้ 25 จาก 30 ฟุต และจะโดนโจมตีสวนตอนออกจากระยะก็อบลิน"* แล้วสรุปว่า
/// **"นี่คือจุดที่ทำให้ OA ไม่ใช่กับดักที่มือใหม่เหยียบโดยไม่รู้ตัว แต่เป็นข้อมูลที่เห็นก่อนตัดสินใจ"**
///
/// **endpoint นี้ไม่เปลี่ยน state ใด ๆ เลย** — ไม่หัก movement ไม่ขยับ token ไม่กิน reaction
/// เรียกซ้ำกี่ครั้งก็ได้ผลเท่าเดิม (ผู้เล่นลากเส้นทางไปมาบนแผนที่ได้โดยไม่ต้องกลัวเผลอ commit)
/// การเดินจริงที่หักระยะและหยุดรอ reaction เป็นคนละ endpoint
///
/// เป็นที่แรกที่ geometry engine ของ Part 8 (<see cref="MovementCostResolver"/>) ถูกต่อเข้ากับ
/// endpoint จริง — ก่อนหน้านี้มันยังไม่มีใครเรียกเลย
/// </summary>
public static class MovementPreviewEndpoints
{
    /// <summary>
    /// ระยะเอื้อมมาตรฐาน 5 ฟุต (SRD 5.2 "Reach": *"A creature has a 5-foot reach"*)
    ///
    /// ⚠️ ยังไม่มีคอลัมน์ reach ต่อตัวละคร/มอนสเตอร์ในสคีมาเลย — มอนสเตอร์เอื้อมยาว (เช่นไฮดรา
    /// reach 10 ft) จึงยังถูกคิดเป็น 5 ฟุตอยู่ · engine รองรับค่าอื่นแล้ว (มีเทสต์คุมเคส 10 ฟุต)
    /// ขาดแค่ที่เก็บข้อมูล ไม่ใช่ขาดกฎ
    /// </summary>
    private const int DefaultReachFeet = 5;

    public static void MapMovementPreviewEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/movement/preview", async (
            Guid campaignId,
            Guid sessionId,
            MovementPreviewRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            ISceneTokenStore tokenStore) =>
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

            var (actor, authFailure) = await CampaignAuthorization.RequireCharacterActorAsync(
                db, request.CharacterId, campaignId, campaign, callerUserId.Value);
            if (authFailure is not null)
            {
                return authFailure;
            }

            var isDm = campaign.OwnerUserId == callerUserId.Value;

            var scene = await db.Scenes.SingleOrDefaultAsync(s => s.SessionId == sessionId && s.IsActive);
            if (scene is null)
            {
                return Results.NotFound();
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

            var path = request.Path.Select(p => new GridPoint((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToList();

            // ── ภัยคุกคาม: เฉพาะศัตรูที่ "ผู้เดินมองเห็นแล้ว" เท่านั้น ──
            //
            // คำเตือนบอกชื่อศัตรู ถ้าเตือนถึงตัวที่ยังอยู่ในหมอกก็เท่ากับบอกว่ามีมันอยู่ ซึ่งเป็นช่องรั่ว
            // แบบเดียวกับที่ §1.2 เตือนไว้และ Part 9 แก้ไปแล้ว · ผลข้างเคียงที่ตั้งใจ: นักซุ่มโจมตี
            // ที่ยังไม่ถูกเปิดหมอกจะ **ไม่** โผล่ในคำเตือน ซึ่งถูกทั้งเรื่องความปลอดภัยและเรื่องเกม
            // (การซุ่มโจมตีควรเซอร์ไพรส์ — ไม่ใช่ประกาศล่วงหน้า)
            var fogAreas = FogLayerSerialization.ParseRevealedAreas(scene.FogLayer);
            var visibleTokens = tokens
                .Where(t => t.EntityType == "Character" && t.EntityId != request.CharacterId)
                .Where(t => FogLayerSerialization.IsVisibleTo(t.X, t.Y, fogAreas, isDm))
                .ToList();

            var threats = await BuildThreatsAsync(db, actor!, visibleTokens);

            IReadOnlyList<OpportunityAttackTrigger> triggers;
            PathCost cost;
            try
            {
                cost = MovementCostResolver.CostOfPath(
                    path,
                    MovementMode.Walk,
                    // ยังไม่มีที่เก็บพื้นเดินยาก/กำแพงในสคีมา — `scenes.grid` เก็บแค่ pixelsPerSquare
                    // กับ offset ไม่มีรายการช่อง · engine รองรับครบแล้ว (Part 8) ขาดแค่ข้อมูล
                    TerrainField.Empty,
                    new MovementProfile(BudgetFeet: actor!.SpeedFeet));

                triggers = OpportunityAttackResolver.Detect(path, threats, request.Disengage);
            }
            catch (ArgumentException)
            {
                // เส้นทางไม่ต่อเนื่อง/ย่ำช่องซ้ำ = input ผิดสัญญาของ caller → 400 ไม่ใช่ 500
                return Results.BadRequest();
            }

            return Results.Ok(new MovementPreviewResponse(
                request.CharacterId,
                cost.TotalFeet,
                cost.IsAffordable,
                cost.RequiresLandingCheck,
                triggers.Select(t => new MovementWarningResponse(
                    t.ThreatId,
                    t.ThreatName,
                    t.LeavesAtStepIndex,
                    t.AttackFromSquare.X,
                    t.AttackFromSquare.Y,
                    WarningMessage(t.ThreatName))).ToList()));
        }).RequireAuthorization();

        // ── "ลากไปได้ถึงไหน" — ขอบเขตที่กระจายตามต้นทุนจริง ไม่ใช่วงรัศมี (§3.1) ─────────────
        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/movement/reachable/{characterId:guid}", async (
            Guid campaignId,
            Guid sessionId,
            Guid characterId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            ISceneTokenStore tokenStore) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var context = await MovementSceneContext.LoadAsync(
                db, tokenStore, campaignId, sessionId, characterId, callerUserId.Value);
            if (context.Failure is not null)
            {
                return context.Failure;
            }

            var origin = new GridPoint(
                (int)Math.Round(context.ActorToken!.X), (int)Math.Round(context.ActorToken.Y));
            var budget = context.Actor!.SpeedFeet;

            var reachable = MovementCostResolver.Reachable(
                origin,
                MovementMode.Walk,
                TerrainField.Empty,
                new MovementProfile(BudgetFeet: budget));

            return Results.Ok(new ReachableResponse(
                characterId,
                budget,
                reachable.Values
                    .Select(r => new ReachableSquareResponse(
                        r.Square.X,
                        r.Square.Y,
                        r.CostFeet,
                        // เส้นทางที่ถูกที่สุดติดไปด้วยทุกช่อง — ตอนผู้เล่นปล่อย token ลงช่องไหน client
                        // ส่งเส้นทางอันนี้กลับมา commit ได้เลย **ไม่ต้องคิดเส้นทางเอง** ซึ่งกันไม่ให้
                        // client ส่งเส้นทางที่เลี่ยง OA หรือลัดผ่านสิ่งกีดขวางเข้ามา
                        r.Path.Select(p => new PathPointResponse(p.X, p.Y)).ToList()))
                    .ToList()));
        }).RequireAuthorization();
    }

    /// <summary>
    /// ข้อความเตือนภาษาคน — ต้องอ่านรู้เรื่องโดย **ไม่ต้องรู้จักคำว่า "opportunity attack"**
    /// (DoD ของ Part นี้) จึงอธิบายเป็นเหตุการณ์ว่าเกิดอะไรขึ้น ไม่ใช่ชื่อกฎ
    /// </summary>
    private static string WarningMessage(string threatName) =>
        $"เส้นทางนี้เดินออกจากระยะเอื้อมของ{threatName} — {threatName}จะได้โจมตีสวนคุณหนึ่งครั้งฟรี " +
        $"ถ้าไม่อยากโดน ลองเลือกถอย (Disengage) หรือเดินทางอื่นที่ไม่ออกจากระยะ";

    /// <summary>
    /// แปลง token ที่มองเห็นแล้วให้เป็นรายการภัยคุกคามที่ engine กินได้
    ///
    /// **ฝ่าย**: ใช้แกน "ใครคุม" เหมือน Part 9 (ยังไม่มีตาราง faction) — ตัวละครผู้เล่นมองว่า NPC
    /// ที่ DM คุมเป็นศัตรู และ NPC มองผู้เล่นเป็นศัตรูแบบสมมาตร
    ///
    /// **<c>CanSeeMover</c> = true เสมอสำหรับตัวที่ผ่านการกรองหมอกมาแล้ว** — โมเดลหมอกของ Part 6
    /// เป็น "พื้นที่ที่เปิดแล้ว" ร่วมกันทั้งโต๊ะ ไม่ได้แยกตามสายตาของแต่ละตัว การมองเห็นจึงสมมาตร
    /// ถ้าวันหนึ่งมีระบบ line-of-sight ต่อตัวจริง ๆ ต้องกลับมาแก้ตรงนี้
    ///
    /// **<c>ReactionAvailable</c> = true เสมอ** — turn state ที่มีอยู่เก็บทรัพยากรของ *เจ้าของเทิร์น
    /// ปัจจุบัน* คนเดียว ไม่ได้เก็บของทุกตัวในสนาม จึงยังไม่มีทางรู้ว่าศัตรูแต่ละตัวใช้ reaction ไปแล้ว
    /// หรือยัง · เลือก **เตือนเกินดีกว่าเตือนขาด** สำหรับพาเนลที่มีไว้กันมือใหม่เหยียบกับดัก
    /// (ทิศทางเดียวกับที่ Part 9 เลือกตอนเจอเงื่อนไข Incapacitated ที่ยังโมเดลไม่ได้)
    /// </summary>
    private static async Task<List<ReachThreat>> BuildThreatsAsync(
        DnDEzDbContext db, Character actor, List<SceneTokenRecord> visibleTokens)
    {
        var ids = visibleTokens.Select(t => t.EntityId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var characters = await db.Characters
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        var threats = new List<ReachThreat>();
        foreach (var token in visibleTokens)
        {
            if (!characters.TryGetValue(token.EntityId, out var character))
            {
                continue;
            }

            if (character.ControlledByDm == actor.ControlledByDm)
            {
                continue; // ฝ่ายเดียวกัน ไม่สวนกันเอง
            }

            threats.Add(new ReachThreat(
                Id: character.Id,
                Name: character.Name,
                Position: new GridPoint((int)Math.Round(token.X), (int)Math.Round(token.Y)),
                ReachFeet: DefaultReachFeet,
                CanSeeMover: true,
                ReactionAvailable: true));
        }

        return threats;
    }
}

internal record MovementPreviewPoint(double X, double Y);

internal record PathPointResponse(int X, int Y);

internal record ReachableSquareResponse(int X, int Y, int CostFeet, List<PathPointResponse> Path);

internal record ReachableResponse(Guid CharacterId, int BudgetFeet, List<ReachableSquareResponse> Squares);

internal record MovementPreviewRequest(
    Guid CharacterId,
    List<MovementPreviewPoint>? Path,
    bool Disengage = false);

internal record MovementWarningResponse(
    Guid ThreatId,
    string ThreatName,
    int LeavesAtStepIndex,
    int AttackFromX,
    int AttackFromY,
    string Message);

internal record MovementPreviewResponse(
    Guid CharacterId,
    int CostFeet,
    bool IsAffordable,
    bool RequiresLandingCheck,
    List<MovementWarningResponse> Warnings);
