using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint G Part 2 — API ของ `story_beats` + active-beat (design doc §11.1, §12)
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/StoryBeatEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// ทุก endpoint เป็น **DM เท่านั้น** (ไม่ใช่แค่สมาชิก) เพราะ `story_beats` มี `Secrets`/`DmNotes`
/// ที่ผู้เล่นไม่ควรเห็น — มุมมองที่กรองแล้วสำหรับผู้เล่น (ถ้าต้องการ) เป็นงานของ Part ถัดไป ไม่ใช่ Part นี้
/// </summary>
public static class StoryBeatEndpoints
{
    /// <summary>ความยาวสูงสุดของ Title — กันช่องโหว่ payload ใหญ่ผิดปกติ (pattern เดียวกับที่อื่นในโปรเจกต์)</summary>
    private const int MaxTitleLength = 200;

    public static void MapStoryBeatEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StoryBeatEndpoints));

        app.MapGet("/campaigns/{campaignId:guid}/beats", async (
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

            var beats = await db.StoryBeats
                .Where(b => b.CampaignId == campaignId)
                .OrderBy(b => b.Order)
                .Select(b => ToResponse(b))
                .ToListAsync();

            return Results.Ok(beats);
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/beats", async (
            Guid campaignId,
            CreateBeatRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
            {
                return Results.BadRequest();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var maxOrder = await db.StoryBeats
                .Where(b => b.CampaignId == campaignId)
                .Select(b => (int?)b.Order)
                .MaxAsync() ?? -1;

            var beat = new StoryBeat
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Order = maxOrder + 1,
                Title = request.Title,
                Summary = request.Summary,
                Objective = request.Objective,
                EntryTrigger = request.EntryTrigger,
                ExitConditions = request.ExitConditions ?? [],
                DmNotes = request.DmNotes,
                Secrets = request.Secrets ?? "[]",
                Progress = BeatProgress.Upcoming,
            };

            db.StoryBeats.Add(beat);
            await db.SaveChangesAsync();

            return Results.Created($"/campaigns/{campaignId}/beats/{beat.Id}", ToResponse(beat));
        }).RequireAuthorization();

        app.MapPatch("/campaigns/{campaignId:guid}/beats/{beatId:guid}", async (
            Guid campaignId,
            Guid beatId,
            UpdateBeatRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (request.Title is not null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength))
            {
                return Results.BadRequest();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var beat = await db.StoryBeats.SingleOrDefaultAsync(b => b.Id == beatId && b.CampaignId == campaignId);
            if (beat is null)
            {
                return Results.NotFound();
            }

            // null = ไม่แก้ฟิลด์นั้น (partial update) — Title กันว่างไว้แล้วข้างบน
            if (request.Title is not null) beat.Title = request.Title;
            if (request.Summary is not null) beat.Summary = request.Summary;
            if (request.Objective is not null) beat.Objective = request.Objective;
            if (request.EntryTrigger is not null) beat.EntryTrigger = request.EntryTrigger;
            if (request.ExitConditions is not null) beat.ExitConditions = request.ExitConditions;
            if (request.DmNotes is not null) beat.DmNotes = request.DmNotes;
            if (request.Secrets is not null) beat.Secrets = request.Secrets;

            await db.SaveChangesAsync();

            return Results.Ok(ToResponse(beat));
        }).RequireAuthorization();

        app.MapDelete("/campaigns/{campaignId:guid}/beats/{beatId:guid}", async (
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

            var beat = await db.StoryBeats.SingleOrDefaultAsync(b => b.Id == beatId && b.CampaignId == campaignId);
            if (beat is null)
            {
                return Results.NotFound();
            }

            db.StoryBeats.Remove(beat);
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPatch("/campaigns/{campaignId:guid}/beats/order", async (
            Guid campaignId,
            ReorderBeatsRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (request.BeatIds is null || request.BeatIds.Length == 0)
            {
                return Results.BadRequest();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var beats = await db.StoryBeats.Where(b => b.CampaignId == campaignId).ToListAsync();

            // ต้องตรงกันเป๊ะ — ไม่ขาด ไม่เกิน ไม่ซ้ำ (เทียบเป็น set ก่อน แล้วค่อยเช็คความยาวกันซ้ำ)
            var existingIds = beats.Select(b => b.Id).ToHashSet();
            var requestedIds = request.BeatIds.ToHashSet();
            if (requestedIds.Count != request.BeatIds.Length || !existingIds.SetEquals(requestedIds))
            {
                return Results.BadRequest();
            }

            var beatsById = beats.ToDictionary(b => b.Id);
            for (var i = 0; i < request.BeatIds.Length; i++)
            {
                beatsById[request.BeatIds[i]].Order = i;
            }

            await db.SaveChangesAsync();

            var ordered = request.BeatIds.Select(id => ToResponse(beatsById[id])).ToList();
            return Results.Ok(ordered);
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/active-beat", async (
            Guid campaignId,
            Guid sessionId,
            SetActiveBeatRequest request,
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

            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (session is null)
            {
                return Results.NotFound();
            }

            var targetBeat = await db.StoryBeats.SingleOrDefaultAsync(b => b.Id == request.BeatId && b.CampaignId == campaignId);
            if (targetBeat is null)
            {
                return Results.NotFound();
            }

            var previousActive = await db.StoryBeats
                .SingleOrDefaultAsync(b => b.CampaignId == campaignId && b.Progress == BeatProgress.Active);

            Guid? previousActiveBeatId = previousActive?.Id;

            if (targetBeat.Progress == BeatProgress.Active)
            {
                // beat เป้าหมาย active อยู่แล้ว — no-op ไม่เขียน ไม่ broadcast (§12.1 ไม่ได้พูดถึงเคสนี้
                // ตรง ๆ แต่ "ไม่มีอะไรเปลี่ยน" ไม่ควรขับ broadcast ที่บอกว่า "เหตุการณ์เกิดขึ้น")
                return Results.Ok(new ActiveBeatResponse(sessionId, campaignId, targetBeat.Id, targetBeat.Id));
            }

            // ⚠️ ต้อง demote ก่อน promote เป็นสอง SaveChangesAsync แยกกัน (ไม่ใช่ตั้งสองฟิลด์แล้ว
            // save รวดเดียว) — ix_story_beats_one_active_per_campaign เป็น unique index ตรวจทันที
            // (immediate ไม่ใช่ deferred) ถ้า EF บังเอิญเรียง UPDATE ให้ promote ก่อน demote (ลำดับ
            // ที่ SaveChanges ส่งจริงไม่ได้ผูกกับลำดับโค้ดเป๊ะ ๆ) จะมีสองแถว Active พร้อมกันชั่วขณะ
            // แล้วชน constraint จริง (พิสูจน์แล้ว — เจอ 500 จริงตอนรวมเป็น save เดียว ก่อนแก้เป็นแบบนี้)
            // ทั้งสอง SaveChangesAsync ยังอยู่ใน transaction เดียวของ request (ดู Program.cs
            // BeginTransactionAsync) จึง atomic เหมือนเดิม แค่บังคับลำดับ SQL ให้ถูกต้อง
            if (previousActive is not null && previousActive.Id != targetBeat.Id)
            {
                previousActive.Progress = BeatProgress.Passed;
                await db.SaveChangesAsync();
            }

            targetBeat.Progress = BeatProgress.Active;
            await db.SaveChangesAsync();

            var response = new ActiveBeatResponse(sessionId, campaignId, targetBeat.Id, previousActiveBeatId);

            try
            {
                await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync("ActiveBeatChanged", response);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast ActiveBeatChanged for campaign {CampaignId}", campaignId);
            }

            return Results.Ok(response);
        }).RequireAuthorization();
    }

    private static BeatResponse ToResponse(StoryBeat beat) => new(
        beat.Id, beat.Order, beat.Title, beat.Summary, beat.Objective, beat.EntryTrigger,
        beat.ExitConditions, beat.DmNotes, beat.Secrets, beat.Progress.ToString(), beat.ReviewStatus.ToString());
}

internal record CreateBeatRequest(
    string? Title, string? Summary, string? Objective, string? EntryTrigger,
    string[]? ExitConditions, string? DmNotes, string? Secrets);

internal record UpdateBeatRequest(
    string? Title, string? Summary, string? Objective, string? EntryTrigger,
    string[]? ExitConditions, string? DmNotes, string? Secrets);

internal record ReorderBeatsRequest(Guid[]? BeatIds);

internal record SetActiveBeatRequest(Guid BeatId);

internal record BeatResponse(
    Guid Id, int Order, string Title, string? Summary, string? Objective, string? EntryTrigger,
    string[] ExitConditions, string? DmNotes, string Secrets, string Progress, string ReviewStatus);

internal record ActiveBeatResponse(Guid SessionId, Guid CampaignId, Guid ActiveBeatId, Guid? PreviousActiveBeatId);
