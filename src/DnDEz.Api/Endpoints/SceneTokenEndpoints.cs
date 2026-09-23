using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>token บนแผนที่ ผูกกับ entity จริงเสมอ (design doc §14) — EntityType เป็น "Character" หรือ
/// "Monster" เท่านั้น (ตรวจใน endpoint ไม่ใช่ DB CHECK เพราะเก็บใน jsonb ไม่ใช่คอลัมน์ typed)</summary>
internal record SceneTokenRecord(Guid Id, string EntityType, Guid EntityId, double X, double Y);

internal static class SceneTokenSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<SceneTokenRecord> Parse(string tokensJson) =>
        JsonSerializer.Deserialize<List<SceneTokenRecord>>(tokensJson, Options) ?? [];

    public static string Serialize(List<SceneTokenRecord> tokens) =>
        JsonSerializer.Serialize(tokens, Options);
}

/// <summary>
/// Sprint G Part 5 — token บนแผนที่: ephemeral (ลากผ่าน hub) + committed (PATCH ครั้งเดียวตอนวาง)
/// design doc §14 ทั้งหัวข้อ — contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/SceneTokenEndpointsTests.cs</c>
///
/// **ทุก token ผูกกับ entity จริง** (characters หรือ monster_stat_blocks) ไม่ใช่แค่รูปวงกลม —
/// Part 8/9 ต้องคำนวณระยะเอื้อม/ตำแหน่งจาก entity ที่ผูกไว้ ถ้า token ลอยเดี่ยว ๆ ไม่มีอะไรให้คำนวณ
///
/// **สิทธิ์ขยับ token**: DM ขยับได้ทุกตัวแบบไม่มีเงื่อนไข · ผู้เล่นขยับได้เฉพาะ token ที่ผูกกับ
/// ตัวละครที่ตัวเองเป็นเจ้าของ · token ที่ผูกกับ monster เป็น DM-only เสมอ (monster ไม่มีแนวคิด
/// "เจ้าของ" เลย) — ดูเหตุผลที่**ไม่ได้ใช้** <see cref="CampaignAuthorization.RequireCharacterActorAsync"/>
/// ตรง ๆ ตามที่สั่งไว้ตอนแรกใน doc comment ของ <see cref="CheckEntityAuthAsync"/>
/// </summary>
public static class SceneTokenEndpoints
{
    public static void MapSceneTokenEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SceneTokenEndpoints));

        app.MapPost("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/tokens", async (
            Guid campaignId,
            Guid sceneId,
            CreateTokenRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var result = await CreateTokenAsync(
                db, campaignId, sceneId, callerUserId.Value, request.EntityType, request.EntityId, request.X, request.Y);
            return result.Status switch
            {
                OpStatus.Ok => Results.Created(
                    $"/campaigns/{campaignId}/scenes/{sceneId}/tokens/{result.Value!.Id}", result.Value),
                OpStatus.BadRequest => Results.BadRequest(new TokenErrorResponse(result.Error!)),
                OpStatus.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization();

        app.MapPatch("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/tokens/{tokenId:guid}", async (
            Guid campaignId,
            Guid sceneId,
            Guid tokenId,
            MoveTokenRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            ISceneTokenStore tokenStore) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var result = await MoveTokenAsync(
                db, tokenStore, logger, campaignId, sceneId, tokenId, callerUserId.Value, request.X, request.Y);
            return result.Status switch
            {
                OpStatus.Ok => Results.Ok(result.Value),
                OpStatus.BadRequest => Results.BadRequest(),
                OpStatus.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization();

        app.MapDelete("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/tokens/{tokenId:guid}", async (
            Guid campaignId,
            Guid sceneId,
            Guid tokenId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            ISceneTokenStore tokenStore) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var result = await DeleteTokenAsync(db, tokenStore, campaignId, sceneId, tokenId, callerUserId.Value);
            return result.Status switch
            {
                OpStatus.Ok => Results.NoContent(),
                OpStatus.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization();
    }

    // ── operations — ใช้ร่วมกันระหว่าง endpoint ด้านบนกับ tool แผนที่ของผู้ช่วย DM (2026-09-23) ──
    // เช็คสิทธิ์ต่อ entity อยู่ในตัว operation เอง (ไม่ใช่ที่ผู้เรียก) เพราะ endpoint พวกนี้ผู้เล่นก็เรียกได้
    // กับ token ของตัวละครตัวเอง — ผู้ช่วย DM เรียกด้วย user id ของ DM จึงผ่านทุกตัวตามสเปก "DM ขยับได้ทุกตัว"

    internal static async Task<OpResult<TokenResponse>> CreateTokenAsync(
        DnDEzDbContext db, Guid campaignId, Guid sceneId, Guid callerUserId,
        string? entityType, Guid entityId, double x, double y)
    {
        if (entityType is not ("Character" or "Monster"))
        {
            return OpResult<TokenResponse>.Bad("invalid_entity_type");
        }

        var (campaign, scene) = await LoadSceneAsync(db, campaignId, sceneId);
        if (campaign is null || scene is null)
        {
            return OpResult<TokenResponse>.NotFound();
        }

        var access = await CheckEntityAuthAsync(db, entityType, entityId, campaignId, campaign, callerUserId);
        if (access != OpStatus.Ok)
        {
            return new OpResult<TokenResponse>(access, null, null);
        }

        var tokens = SceneTokenSerialization.Parse(scene.Tokens);
        var token = new SceneTokenRecord(Guid.NewGuid(), entityType, entityId, x, y);
        tokens.Add(token);
        scene.Tokens = SceneTokenSerialization.Serialize(tokens);
        await db.SaveChangesAsync();

        return OpResult<TokenResponse>.Ok(ToResponse(token));
    }

    internal static async Task<OpResult<TokenResponse>> MoveTokenAsync(
        DnDEzDbContext db, ISceneTokenStore tokenStore, ILogger logger,
        Guid campaignId, Guid sceneId, Guid tokenId, Guid callerUserId, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return OpResult<TokenResponse>.Bad();
        }

        var (campaign, scene) = await LoadSceneAsync(db, campaignId, sceneId);
        if (campaign is null || scene is null)
        {
            return OpResult<TokenResponse>.NotFound();
        }

        var tokens = SceneTokenSerialization.Parse(scene.Tokens);
        var index = tokens.FindIndex(t => t.Id == tokenId);
        if (index < 0)
        {
            return OpResult<TokenResponse>.NotFound();
        }

        var existing = tokens[index];
        var access = await CheckEntityAuthAsync(db, existing.EntityType, existing.EntityId, campaignId, campaign, callerUserId);
        if (access != OpStatus.Ok)
        {
            return new OpResult<TokenResponse>(access, null, null);
        }

        // ⚠️ พยายาม Redis ก่อนเสมอ (fast path, coalescing) — ถ้าเขียนไม่ได้ (Redis ล่ม) ต้อง
        // fallback ไปเขียน Postgres ตรง ๆ ทันที ห้ามยอมรับแล้วปล่อยข้อมูลหาย (§14.3)
        var wroteToRedis = await tokenStore.SetPendingPositionAsync(campaignId, sceneId, tokenId, x, y, callerUserId);

        if (!wroteToRedis)
        {
            logger.LogWarning(
                "Redis ใช้ไม่ได้ตอนย้าย token {TokenId} ของ scene {SceneId} — เขียน Postgres ตรง ๆ แทน",
                tokenId, sceneId);
            tokens[index] = existing with { X = x, Y = y };
            scene.Tokens = SceneTokenSerialization.Serialize(tokens);
            await db.SaveChangesAsync();
        }

        return OpResult<TokenResponse>.Ok(ToResponse(existing with { X = x, Y = y }));
    }

    internal static async Task<OpResult<bool>> DeleteTokenAsync(
        DnDEzDbContext db, ISceneTokenStore tokenStore, Guid campaignId, Guid sceneId, Guid tokenId, Guid callerUserId)
    {
        var (campaign, scene) = await LoadSceneAsync(db, campaignId, sceneId);
        if (campaign is null || scene is null)
        {
            return OpResult<bool>.NotFound();
        }

        var tokens = SceneTokenSerialization.Parse(scene.Tokens);
        var index = tokens.FindIndex(t => t.Id == tokenId);
        if (index < 0)
        {
            return OpResult<bool>.NotFound();
        }

        var existing = tokens[index];
        var access = await CheckEntityAuthAsync(db, existing.EntityType, existing.EntityId, campaignId, campaign, callerUserId);
        if (access != OpStatus.Ok)
        {
            return new OpResult<bool>(access, false, null);
        }

        tokens.RemoveAt(index);
        scene.Tokens = SceneTokenSerialization.Serialize(tokens);
        await db.SaveChangesAsync();

        // กัน pending เก่าฟื้นคืนชีพตอน flush รอบหน้า (ลบ token ไปแล้วแต่ Redis ยังจำตำแหน่งเก่าไว้)
        await tokenStore.ClearTokenAsync(sceneId, tokenId);

        return OpResult<bool>.Ok(true);
    }

    private static async Task<(Campaign? Campaign, Scene? Scene)> LoadSceneAsync(
        DnDEzDbContext db, Guid campaignId, Guid sceneId)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
        if (campaign is null)
        {
            return (null, null);
        }

        var scene = await db.Scenes
            .Include(s => s.Session)
            .SingleOrDefaultAsync(s => s.Id == sceneId && s.Session.CampaignId == campaignId);
        return (campaign, scene);
    }

    /// <summary>
    /// ⚠️ **เบี่ยงจากคำสั่งเดิมที่ให้ใช้ <see cref="CampaignAuthorization.RequireCharacterActorAsync"/>
    /// ตรง ๆ — ตรวจแล้วพบว่า semantics ไม่ตรงกับสเปกของ Part นี้จริง** ต้องบันทึกเหตุผลไว้ (ตรวจพบ
    /// จากเทสต์จริงที่ล้ม 403 ไม่ใช่แค่อ่านโค้ดเดา): helper ตัวนั้นให้ DM override เฉพาะเมื่อ
    /// <c>character.ControlledByDm == true</c> เท่านั้น (ออกแบบมาสำหรับ "DM คุมบทพูด/การตัดสินใจ
    /// ของ NPC" — player-side-design §6) แต่สเปกของ Part นี้ระบุตรง ๆ ว่า **"DM ขยับได้ทุกตัว"**
    /// แบบไม่มีเงื่อนไข (การจัดตำแหน่ง token บนแผนที่เป็นงาน logistics ของโต๊ะ ไม่ใช่การคุมเชิงเล่า
    /// เรื่อง — คนละแกนกับ ControlledByDm) ถ้าใช้ helper เดิมตรง ๆ ตัว DM เองจะขยับ token ของตัวละคร
    /// ผู้เล่นทั่วไปไม่ได้เลย (ControlledByDm=false เป็นค่าเริ่มต้นของตัวละครผู้เล่นทุกตัว) ขัดกับสเปก
    /// ตรง ๆ — จึงเขียนเช็คใหม่เฉพาะจุดนี้แทน (ยังคงหลักการเดียวกัน: entity ต้องมีอยู่จริงก่อนเช็ค
    /// สิทธิ์เสมอ, DM เช็คจาก <c>campaign.OwnerUserId</c> ชั้น C# ตาม convention เดิมในไฟล์นี้ทั้งหมด)
    /// </summary>
    private static async Task<OpStatus> CheckEntityAuthAsync(
        DnDEzDbContext db, string entityType, Guid entityId, Guid campaignId, Campaign campaign, Guid callerUserId)
    {
        if (entityType == "Character")
        {
            var character = await db.Characters.SingleOrDefaultAsync(c => c.Id == entityId && c.CampaignId == campaignId);
            if (character is null)
            {
                return OpStatus.NotFound;
            }

            if (campaign.OwnerUserId == callerUserId)
            {
                return OpStatus.Ok;
            }
            return character.OwnerUserId == callerUserId ? OpStatus.Ok : OpStatus.Forbidden;
        }

        // Monster — ไม่มีแนวคิด "เจ้าของ" เลย DM คุมได้เท่านั้น
        var monsterExists = await db.MonsterStatBlocks.AnyAsync(m => m.Id == entityId && m.CampaignId == campaignId);
        if (!monsterExists)
        {
            return OpStatus.NotFound;
        }
        return campaign.OwnerUserId == callerUserId ? OpStatus.Ok : OpStatus.Forbidden;
    }

    private static TokenResponse ToResponse(SceneTokenRecord token) =>
        new(token.Id, token.EntityType, token.EntityId, token.X, token.Y);
}

internal record CreateTokenRequest(string? EntityType, Guid EntityId, double X, double Y);

internal record MoveTokenRequest(double X, double Y);

/// <summary>
/// Sprint G Part 6 — <see cref="CurrentHp"/>/<see cref="MaxHp"/> เป็น optional (default null) และ
/// คำนวณเฉพาะใน GET scene (design doc §13.1 projection) endpoint create/move/delete ในไฟล์นี้ไม่ต้อง
/// รู้เรื่อง role ของผู้ดู เลยปล่อย null เสมอ — ไม่ใช่ omission แต่เป็นขอบเขตที่ตั้งใจ (คนละหน้าที่)
/// </summary>
// `Name` เพิ่มตอนทำคลังมอนสเตอร์ (2026-09-21): ก่อนหน้านี้หน้าจอหาชื่อจากลิสต์ตัวละครเอง ซึ่งใช้กับ token
// ของมอนสเตอร์ไม่ได้เลย (มอนสเตอร์ไม่ได้อยู่ในลิสต์นั้น) จึงขึ้นคำว่า "Monster" แทนชื่อจริงทุกตัว
//
// ⚠️ ไม่ใช่ข้อมูลใหม่ที่หลุดเพิ่ม: token ที่ยังอยู่ในหมอก **ถูกตัดทิ้งไปก่อนแล้ว** ใน
// `SceneEndpoints.BuildProjectedResponseAsync` (§13.3) · ชื่อของตัวละครที่มองเห็นได้ สมาชิกอ่านได้อยู่แล้วจาก
// `GET /campaigns/{id}/characters` และชื่อมอนสเตอร์ที่มองเห็นได้ก็ถูกส่งให้ผู้เล่นผ่าน affordance
// มาตั้งแต่ Sprint H Part 7 (`AffordanceTarget.Name`) — เทสต์ adversarial ที่ล็อกเรื่องนี้อยู่ใน
// `MonsterLibraryEndpointsTests.Player_response_raw_json_contains_no_trace_of_a_monster_hidden_by_fog`
internal record TokenResponse(
    Guid Id, string EntityType, Guid EntityId, double X, double Y, int? CurrentHp = null, int? MaxHp = null,
    string? Name = null);

internal record TokenErrorResponse(string Reason);
