using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// รูปทรง `character_presets.build` — เก็บเฉพาะ "โครง" ตามกฎ player-side-design §19 ข้อ 2
/// (race/class/ability/equipment เท่านั้น ไม่มีชื่อ/backstory/รูป) ดู doc comment เต็มใน migration
/// `SeedActionCatalogAdditionsAndCharacterPresets` สำหรับที่มาของตัวเลขแต่ละ preset
///
/// ⚠️ `ActionCatalogRefs` คืออาวุธ/เวทที่ preset นี้ให้มา อ้างอิงด้วย id เข้าคลังกลไก (Part 1) เท่านั้น
/// — ห้าม hardcode ตัวเลขระยะ/ราคาซ้ำที่นี่ (ซ้ำแหล่งความจริง ถ้าคลังแก้แล้วที่นี่ไม่รู้จะเพี้ยนทันที)
/// </summary>
internal record PresetAbilityScores(
    int Strength, int Dexterity, int Constitution, int Intelligence, int Wisdom, int Charisma);

internal record PresetBuild(
    string? Race,
    string? Class,
    PresetAbilityScores AbilityScores,
    int SpeedFeet,
    int ArmorClass,
    int HitPoints,
    string[] Equipment,
    Guid[] ActionCatalogRefs);

internal static class PresetBuildSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PresetBuild? TryParse(string buildJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PresetBuild>(buildJson, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Sprint G Part 7 — คลัง preset ตัวละคร (design doc §5, player-side-design §18-19)
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/CharacterPresetEndpointsTests.cs</c>
///
/// GET เท่านั้นในไฟล์นี้ — POST สร้างตัวละครจาก preset ยังอยู่ที่ endpoint เดิม
/// (`POST /campaigns/{cid}/characters` ใน CharacterEndpoints.cs รับ `PresetId` เพิ่ม) เพราะปลายทาง
/// จริงคือ "สร้างตัวละคร" ไม่ใช่ "จัดการ preset" — การมี endpoint สร้างตัวละครสองเส้นทาง (จาก preset
/// กับกรอกเอง) จะแยกกฎ validate/response ออกเป็นสองชุดที่ต้องดูแลให้ตรงกันตลอดไป
///
/// **ไม่ใช่ DM-only** — ผู้เล่นเลือก preset ของตัวเองได้เสมอ (player-side-design §18: "4 ทางเลือกใน
/// การมีตัวละคร" เปิดให้ทุกคน ไม่ใช่สิทธิ์ของ DM)
/// </summary>
public static class CharacterPresetEndpoints
{
    public static void MapCharacterPresetEndpoints(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId:guid}/character-presets", async (
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

            // RLS (`preset_visibility`) กรองให้อยู่แล้ว: Global เห็นได้ทุกคน + Campaign ของแคมเปญนี้
            // เท่านั้น — WHERE ตรงนี้แค่ตั้งใจให้อ่านง่าย ไม่ใช่ตัวกันสิทธิ์จริง (เหมือน endpoint อื่นทั้งหมด)
            var presets = await db.CharacterPresets
                .Where(p => p.Scope == PresetScope.Global || p.CampaignId == campaignId)
                .OrderByDescending(p => p.PopularityCount)
                .ThenBy(p => p.Name)
                .ToListAsync();

            var items = presets.Select(p =>
            {
                var build = PresetBuildSerialization.TryParse(p.Build);
                return new PresetListItem(
                    p.Id, p.Name, p.Scope.ToString(), p.Source.ToString(), p.Level,
                    p.PopularityCount, build?.Race, build?.Class, build?.ArmorClass, build?.HitPoints,
                    build?.AbilityScores);
            });

            return Results.Ok(items);
        }).RequireAuthorization();
    }
}

internal record PresetListItem(
    Guid Id, string Name, string Scope, string Source, int Level, int PopularityCount,
    string? Race, string? Class, int? ArmorClass, int? HitPoints, PresetAbilityScores? AbilityScores);
