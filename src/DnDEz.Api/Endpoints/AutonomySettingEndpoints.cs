using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint F งานที่ 2a — เปิด dial `AutonomySetting` (Manual / Suggest / Autonomous) ต่อ subsystem
/// ให้ DM ปรับได้จริงผ่าน API เป็นครั้งแรก ตาราง `autonomy_settings` มีมาตั้งแต่ `InitialSchema` แล้ว
/// (unique index `(campaign_id, subsystem)` + check constraint ทั้งสองคอลัมน์) ตัวชั้น HTTP รอบแรก
/// จึงไม่ต้องมี migration ใหม่ · ภายหลัง security review ตามมาด้วย
/// `20260830122509_AddAutonomySettingDmOnlyRls` ที่เพิ่ม policy `member_read`/`dm_write` ให้ตารางนี้
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/AutonomySettingEndpointsTests.cs</c>
///
/// จุดที่ห้ามพลาด:
///  • **DM-only บังคับสองชั้นอิสระต่อกัน** — เช็ค `campaign.OwnerUserId != caller` ตรงนี้ในชั้น C#
///    และ policy `dm_write` ที่ชั้น DB (migration `20260830122509_AddAutonomySettingDmOnlyRls`)
///    ซึ่งบังคับว่ามีแต่ DM เท่านั้นที่ INSERT/UPDATE/DELETE ตารางนี้ได้ · สองชั้นนี้เป็น
///    defense in depth ให้กัน ไม่ใช่ตัวแทนกัน — ห้ามถอดเช็คใน C# ออกโดยคิดว่า "RLS กันให้แล้ว"
///    (และห้ามถอด policy ออกโดยคิดว่า "C# กันให้แล้ว" เช่นกัน)
///    (ลำดับ 404-ก่อน-403 ลอกจาก `PUT /campaigns/{id}/join-code` เป๊ะ:
///    แคมเปญที่มองไม่เห็นต้องคืน 404 เหมือนไม่มีอยู่จริง ไม่ใช่ 403 ที่ยืนยันว่ามีแคมเปญนี้อยู่)
///  • GET เปิดให้ **สมาชิกทุกคน** อ่านได้ (ไม่ใช่ DM-only) — ผู้เล่นควรรู้ว่าตอนนี้ AI มีอำนาจแค่ไหน
///    ในโต๊ะที่ตัวเองเล่นอยู่ ค่าพวกนี้ไม่ใช่ความลับของ DM แบบ proposal queue/story-beat secrets
///  • parse enum ผ่าน <see cref="TryParseEnumName"/> เท่านั้น ห้ามเรียก `Enum.TryParse` ตรง ๆ —
///    เหตุผลอยู่ที่ doc comment ของเมธอดนั้น (มันรับ "99" / "3" / "A,B" ผ่านหมด)
///  • อ่านค่า effective ใช้ `OrchestratorTurnRunner.GetEffectiveAutonomyLevelAsync` ตัวเดียวกับที่
///    pipeline จริงใช้ตัดสิน ห้าม query เอง — ถ้า default เปลี่ยนเมื่อไหร่ต้องเปลี่ยนพร้อมกันที่เดียว
///    ไม่งั้นหน้าจอ DM จะโชว์ค่าที่ไม่ตรงกับสิ่งที่ orchestrator ใช้จริง
/// </summary>
public static class AutonomySettingEndpoints
{
    public static void MapAutonomySettingEndpoints(this WebApplication app)
    {
        app.MapPut("/campaigns/{campaignId:guid}/autonomy-settings/{subsystem}", async (
            Guid campaignId,
            string subsystem,
            SetAutonomyLevelRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            // ลำดับ auth-ก่อน-validate ลอกจาก join-code PUT: คนที่ไม่มีสิทธิ์แตะแคมเปญนี้ต้องได้
            // คำตอบเดียวกันเสมอ (404/403) ไม่ว่าจะส่ง subsystem/level มาถูกหรือผิด — ไม่เปิดให้ใช้
            // ความต่างของ 400 vs 404 เป็นเครื่องมือ probe
            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            if (campaign.OwnerUserId != callerUserId.Value)
            {
                return Results.Forbid();
            }

            if (!TryParseEnumName<AutonomySubsystem>(subsystem, out var parsedSubsystem))
            {
                return Results.BadRequest();
            }

            if (!TryParseEnumName<AutonomyLevel>(request.Level, out var parsedLevel))
            {
                return Results.BadRequest();
            }

            var existing = await db.AutonomySettings
                .SingleOrDefaultAsync(a => a.CampaignId == campaignId && a.Subsystem == parsedSubsystem);

            if (existing is null)
            {
                db.AutonomySettings.Add(new AutonomySetting
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaignId,
                    Subsystem = parsedSubsystem,
                    Level = parsedLevel,
                });
            }
            else
            {
                existing.Level = parsedLevel;
            }

            // race: สอง PUT ของ (campaignId, subsystem) เดียวกันพร้อมกันอาจเห็น existing=null ทั้งคู่
            // แล้ว INSERT ชนกันที่ ix_autonomy_settings_campaign_id_subsystem — เส้นทางนี้หายาก
            // (ปกติ DM คนเดียวหมุน dial) แต่ถ้าไม่จับไว้ ตัวที่แพ้ race จะได้ 500 ดิบ ๆ
            //
            // ต้อง savepoint เพราะ INSERT นี้อยู่ใน transaction เดียวที่ middleware ใน Program.cs เปิด
            // คลุมทั้ง request — ถ้าปล่อยให้ error ลอย transaction จะ abort ทั้งก้อน ทำให้ commit
            // ตอนท้าย request พังตามไปด้วย (รูปแบบเดียวกับ ApiKeyEndpoints / /auth/register)
            var currentTransaction = db.Database.CurrentTransaction;
            const string savepoint = "set_autonomy_setting";
            if (currentTransaction is not null)
            {
                await currentTransaction.CreateSavepointAsync(savepoint);
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                if (currentTransaction is not null)
                {
                    await currentTransaction.RollbackToSavepointAsync(savepoint);
                }

                db.ChangeTracker.Clear();

                if (ex.InnerException is not PostgresException { SqlState: "23505" })
                {
                    throw;
                }

                // แพ้ race ให้ client ยิงซ้ำเอง (PUT นี้ idempotent — ยิงซ้ำ = ทับค่าเดิมตามปกติ)
                return Results.Conflict();
            }

            return Results.Ok(new AutonomySettingResponse(
                campaignId, parsedSubsystem.ToString(), parsedLevel.ToString()));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/autonomy-settings", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
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

            // คืนครบทุก subsystem เสมอ ไม่ใช่เฉพาะแถวที่มีใน DB — client จะได้ render dial ครบชุด
            // โดยไม่ต้องรู้ว่า "ไม่มีแถว = Suggest" เอง (กฎ default อยู่ในฝั่ง server ที่เดียว)
            // GetValues เรียงตามค่า enum = ลำดับที่ประกาศไว้ (Narration, NpcDialogue, RulesCombat,
            // MapScene) และ subsystem ที่เพิ่มทีหลังจะโผล่มาเองโดยไม่ต้องแก้ตรงนี้
            var settings = new List<AutonomySettingResponse>();
            foreach (var subsystem in Enum.GetValues<AutonomySubsystem>())
            {
                var level = await OrchestratorTurnRunner.GetEffectiveAutonomyLevelAsync(db, campaignId, subsystem);
                settings.Add(new AutonomySettingResponse(campaignId, subsystem.ToString(), level.ToString()));
            }

            return Results.Ok(settings);
        }).RequireAuthorization();
    }

    /// <summary>
    /// parse ชื่อ enum แบบเข้มกว่า <c>Enum.TryParse</c> ดิบ ๆ ซึ่งหลวมเกินกว่าจะเอามารับ input
    /// จากภายนอกตรง ๆ ได้ — มันคืน true ให้ทั้ง 3 กรณีนี้:
    ///   • <c>"99"</c> → ค่า 99 ที่ **ไม่มีนิยาม** ในตัว enum เลย (จะไปตายที่ check constraint
    ///     `ck_subsystem_autonomysetting` ตอน SaveChanges กลายเป็น 500 แทนที่จะเป็น 400)
    ///   • <c>"3"</c> → รูปตัวเลขของ MapScene ที่ไม่ใช่ contract ที่เราประกาศไว้
    ///   • <c>"Narration,MapScene"</c> → TryParse รับ comma-list แม้ enum จะไม่ใช่ [Flags]
    /// เช็คด้วยการ round-trip ชื่อกลับ: <c>Enum.GetName</c> คืน null ถ้าค่าไม่มีนิยาม และคืนชื่อ
    /// canonical ที่ต้องตรงกับสิ่งที่ผู้เรียกส่งมา (ไม่สนตัวพิมพ์เล็กใหญ่) — ดักครบทั้งสามกรณี
    /// </summary>
    private static bool TryParseEnumName<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!Enum.TryParse(trimmed, ignoreCase: true, out result))
        {
            return false;
        }

        var canonicalName = Enum.GetName(result);
        return canonicalName is not null
               && string.Equals(canonicalName, trimmed, StringComparison.OrdinalIgnoreCase);
    }
}

internal record SetAutonomyLevelRequest(string? Level);

internal record AutonomySettingResponse(Guid CampaignId, string Subsystem, string Level);
