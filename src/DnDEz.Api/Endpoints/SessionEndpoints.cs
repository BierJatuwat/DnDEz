using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint A Part 3 — thin CRUD ของ `sessions` ภายใต้แคมเปญที่เลือกแล้ว (create + list + get)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/SessionCrudTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด:
///  • route parameter ต้องชื่อ <c>campaignId</c> เป๊ะ ๆ — middleware ใน Program.cs หาชื่อนี้เพื่อ
///    `SET LOCAL app.campaign_id` ให้ RLS ใช้ ถ้าตั้งชื่ออื่น policy จะกรองทุกแถวทิ้ง (fail closed)
///  • แคมเปญที่ผู้เรียกไม่ได้เป็นสมาชิก = มองไม่เห็น = 404 (ไม่ใช่ 403 และไม่ใช่ array ว่าง)
///  • validate input ก่อนแตะ DB เสมอ (ดูเหตุผลเรื่อง transaction เดียวต่อ request ใน CampaignEndpoints)
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions", async (
            Guid campaignId,
            CreateSessionRequest request,
            DnDEzDbContext db) =>
        {
            if (!Enum.TryParse<SessionMode>(request.Mode, out var mode) || !Enum.IsDefined(mode))
            {
                return Results.BadRequest();
            }

            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var session = new GameSession
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Mode = mode,
                Status = SessionStatus.Draft,
            };

            db.Sessions.Add(session);
            await db.SaveChangesAsync();

            return Results.Created(
                $"/campaigns/{campaignId}/sessions/{session.Id}",
                new SessionCreatedResponse(session.Id, session.CampaignId, session.Mode.ToString(), session.Status.ToString()));
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/sessions", async (
            Guid campaignId,
            DnDEzDbContext db) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var sessions = await db.Sessions
                .Where(s => s.CampaignId == campaignId)
                .OrderBy(s => s.Id)
                .Select(s => new SessionListItem(
                    s.Id, s.Mode.ToString(), s.Status.ToString(), s.JoinCode, s.StartedAt, s.EndedAt))
                .ToListAsync();

            return Results.Ok(sessions);
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}", async (
            Guid campaignId,
            Guid sessionId,
            DnDEzDbContext db) =>
        {
            if (!await CampaignVisibility.IsVisibleAsync(db, campaignId))
            {
                return Results.NotFound();
            }

            var session = await db.Sessions
                .Where(s => s.Id == sessionId && s.CampaignId == campaignId)
                .Select(s => new SessionListItem(
                    s.Id, s.Mode.ToString(), s.Status.ToString(), s.JoinCode, s.StartedAt, s.EndedAt))
                .SingleOrDefaultAsync();

            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();
    }
}

/// <summary>
/// เช็คว่าแคมเปญนี้มองเห็นได้ไหมก่อนแตะ DB อย่างอื่น (RLS policy `campaign_member_read` กรองให้แล้ว
/// ว่าเห็นเฉพาะแคมเปญที่ตัวเองเป็นสมาชิก) — ใช้ร่วมกันทั้ง SessionEndpoints และ CharacterEndpoints
/// เพื่อกันไม่ให้ WITH CHECK ของ RLS โยน error ตอน INSERT แล้วทำให้ transaction ทั้ง request abort
/// (500 แทนที่จะเป็น 404)
/// </summary>
internal static class CampaignVisibility
{
    public static Task<bool> IsVisibleAsync(DnDEzDbContext db, Guid campaignId) =>
        db.Campaigns.AnyAsync(c => c.Id == campaignId);
}

internal record CreateSessionRequest(string? Mode);

internal record SessionCreatedResponse(Guid Id, Guid CampaignId, string Mode, string Status);

internal record SessionListItem(
    Guid Id,
    string Mode,
    string Status,
    string? JoinCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);
