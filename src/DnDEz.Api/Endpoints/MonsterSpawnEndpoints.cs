using System.Security.Claims;
using System.Text;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Part E ของแผน "DM AI Assistant" (2026-09-22) — **มอนสเตอร์มี HP รายตัวได้แล้ว**
/// (เรื่องที่ผู้ใช้ค้างไว้ตั้งแต่ต้น: "Monster ไม่มี hp รายตัว")
///
/// ## วิธีที่เลือก: spawn แม่แบบเป็นแถว `characters` ไม่ใช่สร้างตารางใหม่
/// `EncounterParticipant` รับได้แค่ `CharacterId` — มอนสเตอร์จึงเข้าวง initiative ไม่ได้เลยก่อนหน้านี้
/// การ spawn เป็น `Character` (ControlledByDm=true, `SpawnedFromMonsterId` ชี้แม่แบบ) ทำให้ได้กลไกที่มี
/// อยู่แล้วมาใช้ **ฟรีทั้งหมด**: ลำดับเทิร์น, resolve การโจมตีใน `CombatEndpoints`, broadcast
/// `HpUpdated`, และการซ่อน HP จากผู้เล่นผ่าน `CharacterStatsVisibility` — ไม่ต้องเขียนระบบคู่ขนาน
/// ที่ต้องคอยซิงก์กันเอง
///
/// ## ชั่วคราวแค่ไหน
/// ผู้ใช้สั่งไว้ว่า HP รายตัวควรเป็นของชั่วคราว "แต่ pause กลางไฟต์แล้วกลับมาเล่นวันอื่นได้" →
/// เก็บใน **Postgres ไม่ใช่ Redis** (Redis ในโปรเจกต์นี้ยอมหายได้ ดู doc comment ของ `Encounter`) แต่ผูก
/// อายุไว้กับ encounter: พอ `POST .../encounters/{id}/end` ระบบ **สรุปผลไฟต์ลงบันทึกแคมเปญแล้วลบแถว
/// instance ทิ้ง** ตามที่ผู้ใช้เลือก ("เก็บไว้แค่พอ recap ไม่ต้องลงรายละเอียดระดับจำนวน hp")
///
/// ## ผู้เล่นเห็นอะไร
/// แถว instance มี `ControlledByDm=true` และดีฟอลต์ `NpcStatsVisibleToParty=false` → ผู้เล่นเห็นว่า
/// "มีก็อบลินอยู่ตรงนี้" (ชื่อ/เลเวล) แต่ **ไม่เห็น HP/AC** ผ่าน `CharacterStatsVisibility` ที่มีอยู่แล้ว
/// (Sprint F งานที่ 7) — ตรงกับหลักของโปรเจกต์ว่า AC/HP ของศัตรูเป็นของที่ผู้เล่นต้องค้นเอาในเกม ·
/// DM เปิดให้เห็นเป็นรายตัวได้ถ้าต้องการ (ส่ง `statsVisibleToParty: true` ตอน spawn)
/// </summary>
public static class MonsterSpawnEndpoints
{
    /// <summary>กัน DM เผลอพิมพ์ 9999 แล้วสร้างแถวท่วมแคมเปญ — ไฟต์จริงไม่เคยต้องการเกินนี้</summary>
    private const int MaxSpawnCount = 20;

    public static void MapMonsterSpawnEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/monsters/{monsterId:guid}/spawn", async (
            Guid campaignId,
            Guid monsterId,
            SpawnMonsterRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var count = request.Count ?? 1;
            if (count < 1 || count > MaxSpawnCount)
            {
                return Results.BadRequest(new SpawnErrorResponse("invalid_count"));
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // DM-only — การวางศัตรูลงสนามเป็นอำนาจของ DM ล้วน ๆ (และแม่แบบมอนสเตอร์ก็ DM-only อยู่แล้ว)
            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // ⚠️ IDOR: monsterId มาจาก client — ต้องยืนยันว่าแม่แบบอยู่ในแคมเปญนี้จริง ไม่ใช่ของโต๊ะอื่น
            var template = await db.MonsterStatBlocks
                .SingleOrDefaultAsync(m => m.Id == monsterId && m.CampaignId == campaignId);
            if (template is null)
            {
                return Results.NotFound();
            }

            // ดีฟอลต์ซ่อน HP/AC จากผู้เล่น — ศัตรูควรเป็นของที่ต้องค้นเอาในเกม ไม่ใช่เปิดตารางให้อ่าน
            var statsVisible = request.StatsVisibleToParty ?? false;

            // เลขต่อท้ายไล่จากจำนวนตัวที่ spawn จากแม่แบบเดียวกันและยังมีชีวิตอยู่ในแคมเปญ — กันชื่อซ้ำ
            // ตอน DM ทยอย spawn เพิ่มกลางไฟต์ (เช่นมี #1-#3 อยู่แล้ว spawn เพิ่ม 2 ตัวต้องได้ #4-#5)
            var existing = await db.Characters
                .CountAsync(c => c.CampaignId == campaignId && c.SpawnedFromMonsterId == monsterId);

            var spawned = new List<Character>(count);
            for (var i = 0; i < count; i++)
            {
                spawned.Add(new Character
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaignId,
                    OwnerUserId = null, // ไม่มีผู้เล่นเป็นเจ้าของ — DM คุมผ่าน ControlledByDm
                    Name = $"{template.Name} #{existing + i + 1}",
                    Sheet = "{}",
                    // HP/AC/speed/reach คัดลอกจากแม่แบบ ณ ตอน spawn (snapshot) — ถ้า DM แก้แม่แบบทีหลัง
                    // ตัวที่ยืนอยู่ในสนามแล้วต้องไม่เปลี่ยนตามกลางไฟต์ เหตุผลเดียวกับที่
                    // `EncounterParticipant` snapshot ค่า initiative modifier ไว้
                    CurrentHp = template.HitPoints,
                    MaxHp = template.HitPoints,
                    CurrentTempHp = 0,
                    ArmorClass = template.ArmorClass,
                    SpeedFeet = template.SpeedFeet,
                    Level = 1,
                    ControlledByDm = true,
                    NpcStatsVisibleToParty = statsVisible,
                    SpawnedFromMonsterId = monsterId,
                    HarvestConsent = false,
                    IsOrphan = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            db.Characters.AddRange(spawned);
            await db.SaveChangesAsync();

            return Results.Ok(spawned
                .Select(c => new SpawnedMonsterItem(c.Id, c.Name, c.CurrentHp, c.MaxHp, c.ArmorClass))
                .ToList());
        }).RequireAuthorization();
    }

    /// <summary>
    /// เรียกตอน `POST .../encounters/{id}/end` — สรุปผลไฟต์ลงบันทึกแคมเปญแล้วลบแถว instance ทิ้ง
    ///
    /// **ลบเฉพาะตัวที่ร่วมใน encounter นี้และเป็น instance จริง** (`SpawnedFromMonsterId != null`) —
    /// ไม่แตะตัวละครผู้เล่น และไม่แตะ NPC ถาวรที่ DM สร้างเอง (ซึ่งมี `SpawnedFromMonsterId = null`)
    ///
    /// สรุปที่เขียนเป็น **ภาพรวม ไม่ใช่ตัวเลข HP รายเทิร์น** ตามที่ผู้ใช้เลือกไว้ — บอกว่าตัวไหนล้ม
    /// (HP 0) ตัวไหนยังยืนอยู่ตอนจบ เพื่อให้ recap/AI ใช้อ้างอิงต่อได้โดยไม่ต้องเก็บ state เต็มไว้ตลอดกาล
    ///
    /// คืนจำนวนแถวที่ลบ (ผู้เรียกเอาไปใส่ response ได้) · ผู้เรียกเป็นคน `SaveChangesAsync` เอง
    /// </summary>
    internal static async Task<int> SummariseAndRemoveSpawnedMonstersAsync(
        DnDEzDbContext db, Guid campaignId, Guid encounterId, Guid sessionId)
    {
        var participantCharacterIds = await db.EncounterParticipants.AsNoTracking()
            .Where(p => p.EncounterId == encounterId)
            .Select(p => p.CharacterId)
            .ToListAsync();

        if (participantCharacterIds.Count == 0)
        {
            return 0;
        }

        var spawnedInstances = await db.Characters
            .Where(c => c.CampaignId == campaignId
                && c.SpawnedFromMonsterId != null
                && participantCharacterIds.Contains(c.Id))
            .ToListAsync();

        if (spawnedInstances.Count == 0)
        {
            return 0;
        }

        var downed = spawnedInstances.Where(c => c.CurrentHp <= 0).Select(c => c.Name).ToList();
        var standing = spawnedInstances.Where(c => c.CurrentHp > 0).Select(c => c.Name).ToList();

        var summary = new StringBuilder();
        summary.Append($"การต่อสู้จบลง มีศัตรูร่วมรบทั้งหมด {spawnedInstances.Count} ตัว");
        if (downed.Count > 0)
        {
            summary.Append($" · ล้มไป {downed.Count} ตัว ({string.Join(", ", downed)})");
        }

        if (standing.Count > 0)
        {
            summary.Append($" · ยังยืนอยู่ตอนจบ {standing.Count} ตัว ({string.Join(", ", standing)})");
        }

        db.CampaignJournalEntries.Add(new CampaignJournalEntry
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            SessionId = sessionId,
            Title = "สรุปผลการต่อสู้",
            Content = summary.ToString(),
            // ระบบสรุปเองจาก state จริง ไม่ใช่ DM พิมพ์และไม่ใช่ AI แต่ง (ดู enum JournalAuthor)
            AuthoredBy = JournalAuthor.System,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.Characters.RemoveRange(spawnedInstances);
        return spawnedInstances.Count;
    }
}

internal record SpawnMonsterRequest(int? Count, bool? StatsVisibleToParty);

internal record SpawnErrorResponse(string Error);

internal record SpawnedMonsterItem(Guid Id, string Name, int CurrentHp, int MaxHp, int ArmorClass);
