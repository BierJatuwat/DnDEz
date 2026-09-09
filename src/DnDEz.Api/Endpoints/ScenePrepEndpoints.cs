using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint G Part 3 — GET scene-prep: monster_stat_blocks + npc_profiles ของ beat ที่ระบุ
/// (design doc §13.2 — DM peek beat อื่นได้โดยไม่เปลี่ยน active beat จริง จึงรับ beatId เป็น
/// route parameter แยกจาก active-beat state ทั้งหมด ไม่แตะ `story_beats.progress` เลย)
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/ScenePrepEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// ⚠️ **คืนทุก ReviewStatus โดยตั้งใจ (Pending/Approved/Rejected)** — endpoint นี้เป็นจอของ DM เอง
/// (DM-only ทั้งตัว) ไม่ใช่ context ที่ป้อนเข้า orchestrator ซึ่งเป็นอีก pipeline หนึ่งที่ยังต้องคุม
/// ด้วยกฎ "review_status != approved ห้ามส่งเข้า orchestrator context" (PROJECT-CONTEXT §2) ต่อไป
/// การกรองออกจากจอนี้จะทำให้ DM รีวิวเนื้อหาที่ยังไม่ approve ไม่ได้เลย ซึ่งขัดจุดประสงค์ของด่าน
/// review เอง — สอง pipeline นี้ต้องแยกกฎกันชัดเจน ไม่ใช่ใช้กฎเดียวกันทั้งระบบ
/// </summary>
public static class ScenePrepEndpoints
{
    public static void MapScenePrepEndpoints(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId:guid}/beats/{beatId:guid}/scene-prep", async (
            Guid campaignId,
            Guid beatId,
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

            var beat = await db.StoryBeats
                .Where(b => b.Id == beatId && b.CampaignId == campaignId)
                .Select(b => new { b.MonsterRefs, b.NpcRefs })
                .SingleOrDefaultAsync();
            if (beat is null)
            {
                return Results.NotFound();
            }

            // dangling ref (ตาราง monster_stat_blocks/npc_profiles ไม่มีแถวนั้นจริงแล้ว) ถูกข้ามเงียบ ๆ
            // ด้วย natural semantics ของ Contains + join — ไม่มีแถวตรง = ไม่ปรากฏในผลลัพธ์ ไม่ error
            var monsters = await db.MonsterStatBlocks
                .Where(m => beat.MonsterRefs.Contains(m.Id) && m.CampaignId == campaignId)
                .Select(m => ToResponse(m))
                .ToListAsync();

            var npcs = await db.NpcProfiles
                .Where(n => beat.NpcRefs.Contains(n.Id) && n.CampaignId == campaignId)
                .Select(n => ToResponse(n))
                .ToListAsync();

            return Results.Ok(new ScenePrepResponse(beatId, monsters, npcs));
        }).RequireAuthorization();
    }

    private static MonsterResponse ToResponse(MonsterStatBlock m) => new(
        m.Id, m.Name, m.ArmorClass, m.HitPoints, m.Actions, m.ConditionRefs, m.ReviewStatus.ToString());

    private static NpcResponse ToResponse(NpcProfile n) => new(
        n.Id, n.Name, n.Motive, n.VoiceHint, n.KnowsSecrets, n.ReviewStatus.ToString());
}

internal record MonsterResponse(
    Guid Id, string Name, int ArmorClass, int HitPoints, string Actions, string ConditionRefs, string ReviewStatus);

internal record NpcResponse(
    Guid Id, string Name, string? Motive, string? VoiceHint, string KnowsSecrets, string ReviewStatus);

internal record ScenePrepResponse(Guid BeatId, List<MonsterResponse> Monsters, List<NpcResponse> Npcs);
