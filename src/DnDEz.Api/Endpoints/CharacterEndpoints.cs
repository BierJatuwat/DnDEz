using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint A Part 3 — thin CRUD ของ `characters` ภายใต้แคมเปญที่เลือกแล้ว (create + list + get)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/CharacterCrudTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด:
///  • `OwnerUserId` มาจาก claim ของ JWT เท่านั้น (ClaimsPrincipalExtensions.GetUserId) ห้ามรับจาก body
///    ไม่งั้นใครก็สร้างตัวละคร "ของคนอื่น" ได้
///  • ลิสต์ห้ามส่ง `Sheet` (jsonb ทั้งก้อน) ออกไป — เฉพาะ endpoint ตัวเดียวเท่านั้นที่ส่ง
///  • Level ต้องอยู่ในช่วง 1–20 (ck_characters_level) — ดักที่ handler ก่อนแตะ DB
/// </summary>
public static class CharacterEndpoints
{
    private const int MaxNameLength = 100;
    private const int MinLevel = 1;
    private const int MaxLevel = 20;

    public static void MapCharacterEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/characters", async (
            Guid campaignId,
            CreateCharacterRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var name = request.Name;
            var level = request.Level ?? 1;

            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            {
                return Results.BadRequest();
            }

            if (level < MinLevel || level > MaxLevel)
            {
                return Results.BadRequest();
            }

            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var ownerUserId = principal.GetUserId();

            var character = new Character
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                OwnerUserId = ownerUserId,
                Name = name,
                Level = level,
                Sheet = "{}",
                ControlledByDm = false,
                HarvestConsent = false,
                IsOrphan = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Characters.Add(character);
            await db.SaveChangesAsync();

            return Results.Created(
                $"/campaigns/{campaignId}/characters/{character.Id}",
                new CharacterCreatedResponse(
                    character.Id, character.CampaignId, character.Name, character.Level, character.OwnerUserId));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/characters", async (
            Guid campaignId,
            DnDEzDbContext db) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var characters = await db.Characters
                .Where(c => c.CampaignId == campaignId)
                .OrderBy(c => c.Id)
                .Select(c => new CharacterListItem(c.Id, c.Name, c.Level, c.OwnerUserId, c.ControlledByDm))
                .ToListAsync();

            return Results.Ok(characters);
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/characters/{characterId:guid}", async (
            Guid campaignId,
            Guid characterId,
            DnDEzDbContext db) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var character = await db.Characters
                .Where(c => c.Id == characterId && c.CampaignId == campaignId)
                .Select(c => new CharacterDetailResponse(
                    c.Id, c.CampaignId, c.Name, c.Level, c.Sheet, c.OwnerUserId,
                    c.ControlledByDm, c.HarvestConsent, c.IsOrphan, c.CreatedAt))
                .SingleOrDefaultAsync();

            return character is null ? Results.NotFound() : Results.Ok(character);
        }).RequireAuthorization();
    }
}

internal record CreateCharacterRequest(string? Name, int? Level);

internal record CharacterCreatedResponse(Guid Id, Guid CampaignId, string Name, int Level, Guid? OwnerUserId);

internal record CharacterListItem(Guid Id, string Name, int Level, Guid? OwnerUserId, bool ControlledByDm);

internal record CharacterDetailResponse(
    Guid Id,
    Guid CampaignId,
    string Name,
    int Level,
    string? Sheet,
    Guid? OwnerUserId,
    bool ControlledByDm,
    bool HarvestConsent,
    bool IsOrphan,
    DateTimeOffset CreatedAt);
