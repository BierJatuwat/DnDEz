using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// รายชื่อแม่แบบมอนสเตอร์ของแคมเปญ (`monster_stat_blocks`) — **DM-only**
///
/// **ทำไมเพิ่งมามีตอนนี้**: ตารางนี้มีมาตั้งแต่ `InitialSchema` และถูกอ่านอยู่ 4 จุด (scene-prep, affordance,
/// การเดิน, ตอนวาง token) แต่ไม่เคยมีเส้นทางให้ "ดูว่าแคมเปญนี้มีมอนสเตอร์อะไรบ้าง" เลย — จอของ DM จึงเลือก
/// มอนสเตอร์มาวางบนแผนที่ไม่ได้ ทั้งที่ `POST .../tokens` รับ `entityType: "Monster"` มาตั้งแต่ Sprint G Part 5
///
/// **DM-only เพราะเป็นข้อมูลเตรียมฉาก** — AC/HP ของศัตรูคือสิ่งที่ผู้เล่นต้องค้นหาเอาเองในเกม (เหตุผลเดียวกับ
/// <see cref="ScenePrepEndpoints"/>) · สิ่งที่ผู้เล่นได้เห็นคือ **ชื่อของ token ที่โผล่พ้นหมอกแล้ว** เท่านั้น
///
/// **คืนทุก ReviewStatus** เช่นเดียวกับ scene-prep — จอนี้เป็นจอของ DM เอง ถ้ากรองเฉพาะ Approved ออกไป
/// DM จะมองไม่เห็นของที่รอรีวิวจนรีวิวไม่ได้ · กฎ "review_status != approved ห้ามเข้า orchestrator context"
/// คุมคนละ pipeline (PROJECT-CONTEXT §2)
///
/// การเพิ่มมอนสเตอร์เข้าแคมเปญอยู่ที่ `POST /campaigns/{id}/monsters/from-srd` ใน <see cref="SrdEndpoints"/>
/// (คัดลอกจากคลัง SRD 5.2.1) — ยังไม่มีเส้นทางสร้างมอนสเตอร์เองจากศูนย์ ดู doc comment ของ migration
/// `AddMonsterSpeedAndReach` สำหรับรายการที่ยังไม่ได้ทำของระบบมอนสเตอร์ทั้งหมด
///
/// ## PATCH — DM ปรับค่าพลังเองได้ (2026-09-22)
/// ⚠️ **แก้ที่ "แม่แบบ" ไม่ใช่ตัวใดตัวหนึ่งบนแผนที่** — ก็อบลิน 3 ตัวที่วางจากแม่แบบเดียวกันใช้แถวนี้ร่วมกัน
/// แก้ HP ที่นี่ = เปลี่ยนทั้งสามตัวพร้อมกัน · จะแยกรายตัวได้ต้องมี HP รายตัวก่อน ซึ่งยังไม่มี (ดู migration
/// `AddMonsterSpeedAndReach`) — หน้าจอต้องเขียนกำกับให้ DM รู้ ไม่ใช่ปล่อยให้เดาเอาเอง
/// </summary>
public static class MonsterEndpoints
{
    public static void MapMonsterEndpoints(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId:guid}/monsters", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // RLS กรอง campaign_id ให้อยู่แล้ว — WHERE ตรงนี้เพื่อให้อ่านโค้ดแล้วเห็นขอบเขตชัด ไม่ใช่ตัวกันสิทธิ์จริง
            var monsters = await db.MonsterStatBlocks
                .Where(m => m.CampaignId == campaignId)
                .OrderBy(m => m.Name)
                .Select(m => new CampaignMonsterListItem(
                    m.Id, m.Name, m.ArmorClass, m.HitPoints, m.SpeedFeet, m.ReachFeet,
                    m.SrdKey, m.ReviewStatus.ToString()))
                .ToListAsync();

            return Results.Ok(monsters);
        }).RequireAuthorization();

        // ── DM ปรับค่าพลังของแม่แบบก่อนเอาลงแผนที่ ──────────────────────────
        app.MapPatch("/campaigns/{campaignId:guid}/monsters/{monsterId:guid}", async (
            Guid campaignId,
            Guid monsterId,
            UpdateMonsterRequest? request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            if (request is null)
            {
                return Results.BadRequest(new MonsterErrorResponse("body_required"));
            }

            var monster = await db.MonsterStatBlocks
                .SingleOrDefaultAsync(m => m.Id == monsterId && m.CampaignId == campaignId);
            if (monster is null)
            {
                return Results.NotFound();
            }

            // validate ทุกค่าที่ส่งมา **ก่อนแตะแถวเลย** — ถ้าปล่อยให้ CHECK ของฐานเป็นด่านเดียว ผู้ใช้จะได้ 500
            // แทน 400 และไม่รู้ว่าค่าไหนผิด · ฟิลด์ที่ไม่ส่งมา = ไม่แก้ (PATCH ไม่ใช่ PUT)
            if (request.Name is not null)
            {
                var trimmed = request.Name.Trim();
                if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
                {
                    return Results.BadRequest(new MonsterErrorResponse("invalid_name"));
                }
            }

            if (request.ArmorClass is <= 0 or > MaxArmorClass)
            {
                return Results.BadRequest(new MonsterErrorResponse("invalid_armor_class"));
            }

            if (request.HitPoints is <= 0 or > MaxHitPoints)
            {
                return Results.BadRequest(new MonsterErrorResponse("invalid_hit_points"));
            }

            // speed 0 ถูกต้องได้ (สิ่งมีชีวิตที่ขยับเองไม่ได้) ต่างจาก reach ที่ต่ำกว่า 5 ไม่มีทางถูกตามกฎ
            if (request.SpeedFeet is < 0 or > MaxDistanceFeet)
            {
                return Results.BadRequest(new MonsterErrorResponse("invalid_speed"));
            }

            if (request.ReachFeet is < MinReachFeet or > MaxDistanceFeet)
            {
                return Results.BadRequest(new MonsterErrorResponse("invalid_reach"));
            }

            monster.Name = request.Name?.Trim() ?? monster.Name;
            monster.ArmorClass = request.ArmorClass ?? monster.ArmorClass;
            monster.HitPoints = request.HitPoints ?? monster.HitPoints;
            monster.SpeedFeet = request.SpeedFeet ?? monster.SpeedFeet;
            monster.ReachFeet = request.ReachFeet ?? monster.ReachFeet;

            await db.SaveChangesAsync();

            return Results.Ok(new CampaignMonsterListItem(
                monster.Id, monster.Name, monster.ArmorClass, monster.HitPoints, monster.SpeedFeet,
                monster.ReachFeet, monster.SrdKey, monster.ReviewStatus.ToString()));
        }).RequireAuthorization();
    }

    private const int MaxNameLength = 200;

    /// <summary>เพดานที่กันค่าพิมพ์พลาดอย่างเดียว ไม่ใช่กฎของเกม — AC สูงสุดใน SRD คือ 25 (Tarrasque)
    /// homebrew ที่สูงกว่านั้นยังใส่ได้ แต่ถ้าเผลอพิมพ์ 999 ควรถูกทักทันทีมากกว่าปล่อยให้ผู้เล่นตีไม่โดนทั้งเกม</summary>
    private const int MaxArmorClass = 99;

    /// <summary>HP สูงสุดใน SRD คือ 697 (Tarrasque) — เผื่อ homebrew ไว้กว้างมากแต่ยังกันเลขหลุดมือ</summary>
    private const int MaxHitPoints = 9999;

    /// <summary>ระยะสูงสุดที่รับ — reach ยาวสุดใน SRD คือ 60 ฟุต (Roper) ส่วน speed ก็ไม่เคยเกินหลักร้อยต้น ๆ</summary>
    private const int MaxDistanceFeet = 999;

    /// <summary>SRD 5.2 "Reach": *"A creature has a 5-foot reach"* — ต่ำกว่านี้ไม่มีทางถูก</summary>
    private const int MinReachFeet = 5;
}

/// <summary>
/// ฟิลด์ที่เป็น null = ไม่แก้ (PATCH ไม่ใช่ PUT) — ส่งมาเฉพาะช่องที่ DM แตะจริง
///
/// **แก้ได้เฉพาะตัวเลขที่ engine ใช้** — `actions`/`conditionRefs` ยังแก้ผ่านที่นี่ไม่ได้ เพราะยังไม่มี
/// UI ที่แก้ท่าได้อย่างปลอดภัย (ข้อความอิสระใน jsonb ที่ไม่มีใครตรวจ = แหล่งของข้อมูลที่เชื่อไม่ได้)
/// </summary>
internal record UpdateMonsterRequest(
    string? Name, int? ArmorClass, int? HitPoints, int? SpeedFeet, int? ReachFeet);

internal record MonsterErrorResponse(string Reason);

/// <summary>
/// ไม่ส่ง `actions`/`conditionRefs` (jsonb ก้อนใหญ่) ออกมาในลิสต์ — จอเลือกมอนสเตอร์มาวางต้องการแค่ชื่อกับ
/// ตัวเลขหลัก · รายละเอียดท่าเต็ม ๆ ดูได้จากคลัง SRD (`GET /srd/monsters/{key}`) ผ่าน `srdKey`
/// </summary>
internal record CampaignMonsterListItem(
    Guid Id, string Name, int ArmorClass, int HitPoints, int SpeedFeet, int ReachFeet,
    string? SrdKey, string ReviewStatus);
