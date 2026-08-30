using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint A Part 3 — thin CRUD ของ `campaigns` (create + list + get · ยังไม่มี update/delete)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/CampaignCrudTests.cs</c> (เขียนก่อน implementation
/// ตาม TDD flow ของ CLAUDE.md) — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด:
///  • สร้างแคมเปญต้องผ่าน Postgres function `app_create_campaign()` เท่านั้น — INSERT ตรงบน
///    `campaigns` ถูก RLS บล็อกหลัง migration FixCampaignListRlsPolicy และ owner_user_id ต้องมาจาก
///    `app_current_user_id()` ฝั่ง DB ไม่ใช่ค่าจากภายนอก
///  • validate input ทุกอย่างก่อนแตะ DB — ทั้ง request อยู่ใน transaction เดียวของ middleware
///    ถ้าปล่อยให้ constraint ของ Postgres เป็นคนดัก ทรานแซกชันจะ abort แล้ว 400 กลายเป็น 500
/// </summary>
public static class CampaignEndpoints
{
    public static void MapCampaignEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns", async (
            CreateCampaignRequest request,
            DnDEzDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest();
            }

            var name = request.Name.Trim();
            if (name.Length > 200)
            {
                return Results.BadRequest();
            }

            var rulesSystem = request.RulesSystem ?? "srd2024";
            if (rulesSystem.Length > 32)
            {
                return Results.BadRequest();
            }

            if (!Enum.TryParse<CampaignMode>(request.Mode ?? nameof(CampaignMode.Guided), out var mode) ||
                !Enum.IsDefined(mode))
            {
                return Results.BadRequest();
            }

            if (!Enum.TryParse<DiceMode>(request.DiceMode ?? nameof(DiceMode.SystemRoll), out var diceMode) ||
                !Enum.IsDefined(diceMode))
            {
                return Results.BadRequest();
            }

            if (!Enum.TryParse<DeathSaveVisibility>(
                    request.DeathSaveVisibility ?? nameof(DeathSaveVisibility.PartyVisible),
                    out var deathSaveVisibility) ||
                !Enum.IsDefined(deathSaveVisibility))
            {
                return Results.BadRequest();
            }

            var campaignId = Guid.NewGuid();
            var membershipId = Guid.NewGuid();
            var modeText = mode.ToString();
            var diceModeText = diceMode.ToString();
            var deathSaveVisibilityText = deathSaveVisibility.ToString();

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT app_create_campaign({campaignId}, {name}, {modeText}, {rulesSystem}, {diceModeText}, {deathSaveVisibilityText}, {membershipId})");

            return Results.Created(
                $"/campaigns/{campaignId}",
                new CampaignCreatedResponse(campaignId, name, modeText));
        }).RequireAuthorization();

        app.MapGet("/campaigns", async (DnDEzDbContext db) =>
        {
            var campaigns = await db.Campaigns
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Select(c => new CampaignListItem(c.Id, c.Name, c.Mode.ToString(), c.CreatedAt, c.OwnerUserId))
                .ToListAsync();

            return Results.Ok(campaigns);
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}", async (
            Guid campaignId,
            DnDEzDbContext db) =>
        {
            var campaign = await db.Campaigns
                .SingleOrDefaultAsync(c => c.Id == campaignId);

            return campaign is null
                ? Results.NotFound()
                : Results.Ok(new CampaignDetailResponse(
                    campaign.Id,
                    campaign.Name,
                    campaign.Mode.ToString(),
                    campaign.RulesSystem,
                    campaign.DiceMode.ToString(),
                    campaign.DeathSaveVisibility.ToString(),
                    campaign.ReactionTimeoutSeconds,
                    campaign.CharacterReviewRequired,
                    campaign.StartingLevel,
                    campaign.CreatedAt,
                    campaign.OwnerUserId));
        }).RequireAuthorization();
    }
}

internal record CreateCampaignRequest(
    string? Name,
    string? Mode,
    string? RulesSystem,
    string? DiceMode,
    string? DeathSaveVisibility);

internal record CampaignCreatedResponse(Guid Id, string Name, string Mode);

// Sprint E Part 3 — เพิ่ม OwnerUserId เข้า list ด้วย (เดิมมีแค่ CampaignDetailResponse) — frontend
// ต้องใช้แยกว่า caller เป็น DM ของแคมเปญที่เลือกอยู่ไหม (โชว์ player action panel หรือ DM panel เดิม)
// ไม่ใช่ข้อมูลใหม่ที่ต้อง authorize เพิ่ม — สมาชิกทุกคนเห็น OwnerUserId ผ่าน GET รายตัวได้อยู่แล้ว
internal record CampaignListItem(Guid Id, string Name, string Mode, DateTimeOffset CreatedAt, Guid OwnerUserId);

internal record CampaignDetailResponse(
    Guid Id,
    string Name,
    string Mode,
    string RulesSystem,
    string DiceMode,
    string DeathSaveVisibility,
    int ReactionTimeoutSeconds,
    bool CharacterReviewRequired,
    int StartingLevel,
    DateTimeOffset CreatedAt,
    Guid OwnerUserId);
