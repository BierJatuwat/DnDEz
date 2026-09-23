using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// พื้นที่ที่เปิดแล้วหนึ่งวง (วงกลม) — โมเดลง่ายที่สุดที่ยังไม่พึ่ง grid/geometry engine ที่ยังไม่มี
/// (Sprint G Part 8 ถึงจะทำ) DM "เปิด" พื้นที่รอบจุดที่ปาร์ตี้สำรวจ ผู้เล่นเห็น token ที่อยู่ในวงไหน
/// วงหนึ่งเท่านั้น — <see cref="Id"/> ใช้ตอน hide (ถอนการเปิดวงนั้นทิ้ง ไม่ใช่ hide พื้นที่ตามพิกัด)
/// </summary>
internal record FogRevealedArea(Guid Id, double X, double Y, double Radius);

internal static class FogLayerSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<FogRevealedArea> ParseRevealedAreas(string fogLayerJson)
    {
        if (string.IsNullOrWhiteSpace(fogLayerJson) || fogLayerJson == "{}")
        {
            return [];
        }
        var doc = JsonSerializer.Deserialize<FogLayerDocument>(fogLayerJson, Options);
        return doc?.RevealedAreas ?? [];
    }

    public static string Serialize(List<FogRevealedArea> areas) =>
        JsonSerializer.Serialize(new FogLayerDocument(areas), Options);

    /// <summary>
    /// จุดนี้เปิดให้เห็นแล้วหรือยัง — วงกลมนับขอบเป็น "เห็น" ด้วย (<c>&lt;=</c>) ล็อกไว้ด้วยเทสต์
    /// เคสขอบทั้งฝั่ง backend และ frontend ห้ามเปลี่ยนเป็น <c>&lt;</c> โดยไม่แก้ทั้งสองที่พร้อมกัน
    ///
    /// **ต้องมีที่เดียวในทั้งระบบ** — เดิมเป็น private อยู่ใน <c>SceneEndpoints</c> ที่เดียว พอ
    /// Sprint G Part 9 ต้องใช้กฎเดียวกันเพื่อกรองเป้าก่อนคำนวณ affordance จึงย้ายมาไว้ตรงนี้แทนการ
    /// คัดลอกไปอีกไฟล์ · เหตุผลคือถ้ามีสองสำเนาแล้ววันหนึ่งมีคนแก้อันเดียว อีกอันจะยังรั่วเงียบ ๆ
    /// ต่อไปโดยไม่มีเทสต์ไหนจับได้ (design doc §1.2: การรั่วของ "มีอยู่ไหม" แรงกว่า "เลขเท่าไร")
    /// </summary>
    public static bool IsWithinArea(double x, double y, FogRevealedArea area)
    {
        var dx = x - area.X;
        var dy = y - area.Y;
        return (dx * dx) + (dy * dy) <= area.Radius * area.Radius;
    }

    /// <summary>ผู้ดูคนนี้มองเห็นจุดนี้ไหม — DM เห็นทุกอย่างเสมอไม่สนใจหมอก (§13.3) ส่วนคนอื่น
    /// เห็นเฉพาะจุดที่อยู่ในวงที่เปิดแล้วอย่างน้อยหนึ่งวง</summary>
    public static bool IsVisibleTo(double x, double y, IReadOnlyList<FogRevealedArea> revealedAreas, bool isDm) =>
        isDm || revealedAreas.Any(area => IsWithinArea(x, y, area));

    private record FogLayerDocument(List<FogRevealedArea> RevealedAreas);
}

/// <summary>
/// Sprint G Part 6 — เปิด/ปิดพื้นที่หมอก (design doc §13.3) DM-only ทั้งคู่ — ผู้เล่นไม่มีสิทธิ์
/// สั่งเปิดหมอกเอง (เป็นการตัดสินใจของ DM ล้วน ๆ ว่าปาร์ตี้ "เห็น" อะไรแล้ว)
///
/// broadcast ออกทั้งสอง role-group (DM/Player) ด้วย payload เดียวกัน — ตัวข้อมูล "พื้นที่ไหนเปิดแล้ว"
/// เองไม่ใช่ความลับ (ทุกคนเห็นพื้นที่ที่เปิดแล้วเหมือนกันหมด) ความลับอยู่ที่ "อะไรซ่อนอยู่หลังหมอก"
/// ต่างหาก ซึ่งกรองที่ระดับ token ใน GET scene ไม่ใช่ที่ fog event นี้
/// </summary>
public static class SceneFogEndpoints
{
    public static void MapSceneFogEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SceneFogEndpoints));

        app.MapPost("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/fog/reveal", async (
            Guid campaignId,
            Guid sceneId,
            RevealFogRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IHubContext<CampaignHub> hubContext) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (request.Radius is not (> 0) || !double.IsFinite(request.X) || !double.IsFinite(request.Y))
            {
                return Results.BadRequest();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var result = await RevealAsync(db, hubContext, logger, campaignId, sceneId, request.X, request.Y, request.Radius.Value);
            return result.IsOk ? Results.Ok(result.Value) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/fog/hide", async (
            Guid campaignId,
            Guid sceneId,
            HideFogRequest request,
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

            var result = await HideAsync(db, hubContext, logger, campaignId, sceneId, request.AreaId);
            return result.IsOk ? Results.Ok(result.Value) : Results.NotFound();
        }).RequireAuthorization();
    }

    // ── operations — ใช้ร่วมกันระหว่าง endpoint ด้านบนกับ tool `reveal_fog`/`hide_fog` ของผู้ช่วย DM (2026-09-23)
    // ผู้เรียกต้องเช็คสิทธิ์ DM มาก่อน · broadcast `FogChanged` ในตัว

    internal static async Task<OpResult<FogAreaResponse>> RevealAsync(
        DnDEzDbContext db, IHubContext<CampaignHub> hubContext, ILogger logger,
        Guid campaignId, Guid sceneId, double x, double y, double radius)
    {
        if (radius <= 0 || !double.IsFinite(radius) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return OpResult<FogAreaResponse>.Bad();
        }

        var scene = await db.Scenes
            .Include(s => s.Session)
            .SingleOrDefaultAsync(s => s.Id == sceneId && s.Session.CampaignId == campaignId);
        if (scene is null)
        {
            return OpResult<FogAreaResponse>.NotFound();
        }

        var areas = FogLayerSerialization.ParseRevealedAreas(scene.FogLayer);
        var newArea = new FogRevealedArea(Guid.NewGuid(), x, y, radius);
        areas.Add(newArea);
        scene.FogLayer = FogLayerSerialization.Serialize(areas);
        await db.SaveChangesAsync();

        await BroadcastFogChangedAsync(hubContext, campaignId, sceneId, areas, logger);

        return OpResult<FogAreaResponse>.Ok(ToResponse(newArea));
    }

    internal static async Task<OpResult<FogStateResponse>> HideAsync(
        DnDEzDbContext db, IHubContext<CampaignHub> hubContext, ILogger logger,
        Guid campaignId, Guid sceneId, Guid areaId)
    {
        var scene = await db.Scenes
            .Include(s => s.Session)
            .SingleOrDefaultAsync(s => s.Id == sceneId && s.Session.CampaignId == campaignId);
        if (scene is null)
        {
            return OpResult<FogStateResponse>.NotFound();
        }

        var areas = FogLayerSerialization.ParseRevealedAreas(scene.FogLayer);
        // ไม่มี id ตรง = no-op คืน 200 (ถอนพื้นที่ที่ไม่มีอยู่แล้ว = ผลลัพธ์เดียวกับที่ตั้งใจอยู่แล้ว
        // ไม่ใช่ error ของ caller)
        areas.RemoveAll(a => a.Id == areaId);
        scene.FogLayer = FogLayerSerialization.Serialize(areas);
        await db.SaveChangesAsync();

        await BroadcastFogChangedAsync(hubContext, campaignId, sceneId, areas, logger);

        return OpResult<FogStateResponse>.Ok(new FogStateResponse(sceneId, areas.Select(ToResponse).ToList()));
    }

    private static async Task BroadcastFogChangedAsync(
        IHubContext<CampaignHub> hubContext, Guid campaignId, Guid sceneId, List<FogRevealedArea> areas, ILogger logger)
    {
        try
        {
            var payload = new FogStateResponse(sceneId, areas.Select(ToResponse).ToList());
            await hubContext.Clients.Group(CampaignHub.RoleGroupName(campaignId, isDm: true)).SendAsync("FogChanged", payload);
            await hubContext.Clients.Group(CampaignHub.RoleGroupName(campaignId, isDm: false)).SendAsync("FogChanged", payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast FogChanged for scene {SceneId}", sceneId);
        }
    }

    private static FogAreaResponse ToResponse(FogRevealedArea area) => new(area.Id, area.X, area.Y, area.Radius);
}

internal record RevealFogRequest(double X, double Y, double? Radius);

internal record HideFogRequest(Guid AreaId);

internal record FogAreaResponse(Guid Id, double X, double Y, double Radius);

internal record FogStateResponse(Guid SceneId, List<FogAreaResponse> RevealedAreas);
