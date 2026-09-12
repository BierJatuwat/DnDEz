using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint H Part 5 — DM ระบายพื้นเดินยาก/กำแพงลงฉาก (ชั้นแผนที่ของ spatial-play-design §3.3)
///
/// ⚠️ **ข้อมูลนี้กำหนดว่าใครเดินไปไหนได้** — ถ้าผู้เล่นเขียนได้เอง จะเปิดกำแพงให้ตัวเองเดินทะลุได้
/// ทั้งสอง endpoint จึงเป็น DM-only · และ RLS ของตาราง `scenes` (`campaign_isolation`) อนุญาตให้
/// **สมาชิกทุกคน** UPDATE ได้ที่ชั้น DB — การกันจึงอยู่ที่ชั้น endpoint นี้ที่เดียว (เหมือน grid/fog เดิม)
///
/// ## สเปก
/// ลำดับการเช็คห้ามสลับ:
///   1. ไม่มี user → 401
///   2. <c>CampaignAuthorization.RequireCampaignDmAsync</c> → ไม่ใช่สมาชิก 404 · ไม่ใช่ DM 403
///      **เช็คสิทธิ์ก่อน validate เนื้อหา** — ผู้เล่นที่ส่งเนื้อหาผิดต้องได้ 403 ไม่ใช่ 400
///      (ไม่ให้คนที่ไม่มีสิทธิ์ใช้ endpoint นี้ส่องกฎ validation)
///   3. scene ต้องอยู่ในแคมเปญนี้ผ่าน session (<c>s.Session.CampaignId == campaignId</c>) → ไม่งั้น 404 (กัน IDOR)
///   4. (PATCH) validate → 400: body null · paint ไม่ใช่ชื่อใน <see cref="TerrainPaint"/> เป๊ะ (case-sensitive
///      และต้องเป็นค่าที่ define จริง ไม่ใช่ตัวเลข) · squares null/ว่าง/เกิน
///      <see cref="SceneTerrainSerialization.MaxSquaresPerEdit"/> · มีช่องไหนนอกขอบ
///      <see cref="SceneTerrainSerialization.IsWithinBounds"/>
///   5. (PATCH) <see cref="SceneTerrainSerialization.Apply"/> → ไม่ Succeeded (เกินเพดานทั้งฉาก) → 400
///   6. บันทึก `scene.Terrain` แล้วคืน 200 + TerrainResponse (เรียงแบบเดียวกับ Serialize)
/// PATCH ต้องมี request body limit (<c>RequestSizeLimitAttribute</c> 64 KB) — 1,000 ช่องใช้ราว 20 KB
/// </summary>
public static class SceneTerrainEndpoints
{
    public static void MapSceneTerrainEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SceneTerrainEndpoints));

        app.MapGet("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/terrain", async (
            Guid campaignId,
            Guid sceneId,
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

            var scene = await db.Scenes
                .Include(s => s.Session)
                .SingleOrDefaultAsync(s => s.Id == sceneId && s.Session.CampaignId == campaignId);
            if (scene is null)
            {
                return Results.NotFound();
            }

            var layer = SceneTerrainSerialization.Parse(scene.Terrain);
            return Results.Ok(ToResponse(layer));
        }).RequireAuthorization();

        app.MapPatch("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/terrain", async (
            Guid campaignId,
            Guid sceneId,
            UpdateTerrainRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IHubContext<CampaignHub> hubContext) =>
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

            var scene = await db.Scenes
                .Include(s => s.Session)
                .SingleOrDefaultAsync(s => s.Id == sceneId && s.Session.CampaignId == campaignId);
            if (scene is null)
            {
                return Results.NotFound();
            }

            // Enum.TryParse ยอมรับตัวเลขสตริง ("1") ด้วย — ต้องเทียบกับชื่อจริงเท่านั้น (case-sensitive)
            if (request.Paint is null ||
                !Enum.GetNames<TerrainPaint>().Contains(request.Paint, StringComparer.Ordinal))
            {
                return Results.BadRequest();
            }

            var paint = Enum.Parse<TerrainPaint>(request.Paint);

            if (request.Squares is null || request.Squares.Count == 0 ||
                request.Squares.Count > SceneTerrainSerialization.MaxSquaresPerEdit)
            {
                return Results.BadRequest();
            }

            var squares = new List<GridPoint>();
            foreach (var square in request.Squares)
            {
                var point = new GridPoint(square.X, square.Y);
                if (!SceneTerrainSerialization.IsWithinBounds(point))
                {
                    return Results.BadRequest();
                }

                squares.Add(point);
            }

            var result = SceneTerrainSerialization.Apply(scene.Terrain, paint, squares);
            if (!result.Succeeded)
            {
                return Results.BadRequest();
            }

            scene.Terrain = result.Json!;
            await db.SaveChangesAsync();

            await BroadcastTerrainChangedAsync(hubContext, campaignId, scene.Id, logger);

            return Results.Ok(ToResponse(result.Layer));
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
    }

    /// <summary>
    /// Sprint H Part 6 — บอกทุก role ว่า "พื้นของฉากนี้เปลี่ยนแล้ว ดึงแผนที่ใหม่" (จอผู้เล่นเห็นหนองน้ำและ
    /// ขอบเขตเดินหดทันทีโดยไม่ต้อง refresh)
    ///
    /// ⚠️ **payload มีแค่ sceneId ไม่มีรายการช่องเลยโดยตั้งใจ** — ต่างจาก FogChanged ที่ส่งวงหมอกได้เพราะวงหมอก
    /// ไม่ใช่ความลับ แต่พื้นในหมอกเป็นความลับ · ผู้เล่นต้องได้เฉพาะส่วนที่ server กรองแล้วผ่าน GET scene
    /// · broadcast พลาดต้องไม่ทำให้ request พัง (พื้นบันทึกไปแล้ว) — แพทเทิร์นเดียวกับ FogChanged
    /// </summary>
    private static async Task BroadcastTerrainChangedAsync(
        IHubContext<CampaignHub> hubContext, Guid campaignId, Guid sceneId, ILogger logger)
    {
        try
        {
            var payload = new TerrainChangedBroadcast(sceneId);
            await hubContext.Clients.Group(CampaignHub.RoleGroupName(campaignId, isDm: true)).SendAsync("TerrainChanged", payload);
            await hubContext.Clients.Group(CampaignHub.RoleGroupName(campaignId, isDm: false)).SendAsync("TerrainChanged", payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast TerrainChanged for scene {SceneId}", sceneId);
        }
    }

    private static TerrainResponse ToResponse(TerrainLayer layer) => new(
        layer.Difficult.OrderBy(s => s.Y).ThenBy(s => s.X)
            .Select(s => new TerrainSquareResponse(s.X, s.Y)).ToList(),
        layer.Blocked.OrderBy(s => s.Y).ThenBy(s => s.X)
            .Select(s => new TerrainSquareResponse(s.X, s.Y)).ToList());
}

/// <summary>สัญญาณ "พื้นเปลี่ยน" — มีแค่ sceneId ห้ามเพิ่มรายการช่อง (ดู BroadcastTerrainChangedAsync)</summary>
internal record TerrainChangedBroadcast(Guid SceneId);

internal record TerrainSquareRequest(int X, int Y);

internal record UpdateTerrainRequest(string? Paint, List<TerrainSquareRequest>? Squares);

internal record TerrainSquareResponse(int X, int Y);

internal record TerrainResponse(List<TerrainSquareResponse> Difficult, List<TerrainSquareResponse> Blocked);
