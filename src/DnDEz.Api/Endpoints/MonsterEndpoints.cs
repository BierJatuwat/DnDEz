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
    }
}

/// <summary>
/// ไม่ส่ง `actions`/`conditionRefs` (jsonb ก้อนใหญ่) ออกมาในลิสต์ — จอเลือกมอนสเตอร์มาวางต้องการแค่ชื่อกับ
/// ตัวเลขหลัก · รายละเอียดท่าเต็ม ๆ ดูได้จากคลัง SRD (`GET /srd/monsters/{key}`) ผ่าน `srdKey`
/// </summary>
internal record CampaignMonsterListItem(
    Guid Id, string Name, int ArmorClass, int HitPoints, int SpeedFeet, int ReachFeet,
    string? SrdKey, string ReviewStatus);
