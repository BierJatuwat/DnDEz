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

            var context = await MovementSceneContext.LoadAsync(
                db, tokenStore, campaignId, sessionId, request.CharacterId, callerUserId.Value);
            if (context.Failure is not null)
            {
                return context.Failure;
            }

            var path = request.Path.Select(p => new GridPoint((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToList();

            IReadOnlyList<OpportunityAttackTrigger> triggers;
            PathCost cost;
            try
            {
                cost = MovementCostResolver.CostOfPath(
                    path,
                    MovementMode.Walk,
                    // พื้นมาจาก scenes.terrain (ชั้นแผนที่เท่านั้น — ชั้น effect ยังไม่ทำ ดู SceneTerrainSerialization)
                    context.Terrain,
                    new MovementProfile(BudgetFeet: context.Actor!.SpeedFeet));

                // ── ภัยคุกคาม: เฉพาะศัตรูที่ "ผู้เดินมองเห็นแล้ว" เท่านั้น ──
                //
                // คำเตือนบอกชื่อศัตรู ถ้าเตือนถึงตัวที่ยังอยู่ในหมอกก็เท่ากับบอกว่ามีมันอยู่ ซึ่งเป็นช่องรั่ว
                // แบบเดียวกับที่ §1.2 เตือนไว้และ Part 9 แก้ไปแล้ว · ผลข้างเคียงที่ตั้งใจ: นักซุ่มโจมตี
                // ที่ยังไม่ถูกเปิดหมอกจะ **ไม่** โผล่ในคำเตือน ซึ่งถูกทั้งเรื่องความปลอดภัยและเรื่องเกม
                // (การซุ่มโจมตีควรเซอร์ไพรส์ — ไม่ใช่ประกาศล่วงหน้า) · ตอนนี้ threats มาจาก
                // MovementSceneContext.LoadAsync เดียวกับ commit/reachable รวมถึงมอนสเตอร์ที่มี
                // reach ต่อ stat block แล้ว ไม่ใช่ DefaultReachFeet ตายตัวอีกต่อไป
                triggers = OpportunityAttackResolver.Detect(path, context.Threats, request.Disengage);
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
                context.Terrain,
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
